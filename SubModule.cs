using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Bannerlord.ButterLib.Common.Extensions;
using HarmonyLib;
using JetBrains.Annotations;
using MCM.Abstractions.Base.PerCampaign;
using MCM.Abstractions.FluentBuilder;
using Microsoft.Extensions.Logging;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.LinQuick;
using TaleWorlds.MountAndBlade;

namespace SaveCleaner;

public class SubModule : MBSubModuleBase
{
    public static readonly string Name = typeof(SubModule).Namespace!;
    public static readonly Version Version = typeof(SubModule).Assembly.GetName().Version;
    public static readonly Harmony Harmony = new("Bannerlord.SaveCleaner.JungleDruid");
    private ILogger _logger;
    private ILogger Logger => _logger ??= LogFactory.Get<SubModule>();
    public static SubModule Instance { get; private set; }
    private Cleaner _cleaner;
    public SaveEventReceiver SaveEventReceiver { get; } = new();
    private CleanerMapView CleanerMapView { get; set; }
    [CanBeNull] private FluentPerCampaignSettings _settings;
    private bool _startPressed;
    internal SaveCleanerOptions Options { get; private set; }
    internal static Dictionary<Type, SaveCleanerAddon> Addons { get; } = [];
    private SaveCleanerAddon _wipeAddon;
    private bool CanCleanUp => MapScreen.Instance?.IsActive == true && CleanerMapView is not null && !CleanerMapView.IsFinalized;


    private void OnServiceRegistration()
    {
        this.AddSerilogLoggerProvider($"{Name}.log", [$"{Name}.*"], o => o.MinimumLevel.Verbose());
    }

    protected override void OnSubModuleLoad()
    {
        Instance = this;
        OnServiceRegistration();
        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        AddDefaultAddon();
    }

    private static void AddDefaultAddon()
    {
        var addon = new SaveCleanerAddon(Harmony.Id, Name,
            new SaveCleanerAddon.BoolSetting(Settings.RemoveDisappearedHeroes, "Remove Disappeared Heroes",
                "Remove disappeared heroes that are likely spawned by mods.", 0, true));
        addon.Removable += Removable;
        addon.Register<SubModule>();
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        Logger.LogInformation($"{Name} {Version} starting up...");
    }

    protected override void OnApplicationTick(float dt)
    {
#if DEBUG
        if ((Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl)) &&
            (Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift)) &&
            (Input.IsKeyDown(InputKey.LeftAlt) || Input.IsKeyDown(InputKey.RightAlt)) &&
            Input.IsKeyPressed(InputKey.B))
        {
            Debugger.Break();
        }

        if ((Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl)) &&
            (Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift)) &&
            (Input.IsKeyDown(InputKey.LeftAlt) || Input.IsKeyDown(InputKey.RightAlt)) &&
            Input.IsKeyPressed(InputKey.C))
        {
            TryStartCleanUp();
        }
#endif
        if (_startPressed)
        {
            if (!TryStartCleanUp()) return;
        }
        else if (_wipeAddon is not null)
        {
            if (!TryStartWipe()) return;
        }

        if (_cleaner is null) return;
        if (_cleaner.Completed)
        {
            _cleaner = null;
        }
        else _cleaner.CleanerTick();
    }

    private bool TryStartCleanUp()
    {
        if (!Addons.Values.AnyQ(a => !a.Disabled))
        {
            InformationManager.ShowInquiry(new InquiryData(
                "Error", "No available addon.", true, false,
                "OK", null, () => _startPressed = false, () => { }));
            return false;
        }

        if (!CanCleanUp) return false;

        _startPressed = false;

        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        InformationManager.ShowInquiry(new InquiryData(
            "Confirm Clean Up", "Start the clean up progress now?", true, true,
            "Yes", "No",
            () => _cleaner ??= new Cleaner(CleanerMapView, Addons.Values.ToListQ()).Start(),
            () => { }));

        return true;
    }

    private bool TryStartWipe()
    {
        if (_wipeAddon.Disabled)
        {
            InformationManager.ShowInquiry(new InquiryData(
                "Error", "Wipe target is disabled.", true, false,
                "OK", null, () => _startPressed = false, () => { }));
            return false;
        }

        if (!CanCleanUp) return false;

        SaveCleanerAddon addon = _wipeAddon;
        _wipeAddon = null;

        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        InformationManager.ShowInquiry(new InquiryData(
            $"Confirm Wipe {addon.Name}", "Start the wipe progress now?", true, true,
            "Yes", "No",
            () => _cleaner ??= new Cleaner(CleanerMapView, Addons.Values.ToListQ(), addon).Start(),
            () => { }));

        return true;
    }

    protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
    {
        Campaign.Current.AddCampaignEventReceiver(SaveEventReceiver);
        Options ??= new SaveCleanerOptions();
    }

    public override void OnAfterGameInitializationFinished(Game game, object starterObject)
    {
        if (game.GameType is not Campaign campaign) return;
        _settings?.Unregister();
        ISettingsBuilder builder = MCMSettings.AddSettings(Options, campaign.UniqueGameId);
        _settings = builder.SetOnPropertyChanged(OnPropertyChanged).BuildAsPerCampaign();
        _settings?.Register();
    }

    private static bool Removable(SaveCleanerAddon addon, object o)
    {
        if (o is Hero hero)
        {
            return addon.GetValue<bool>(Settings.RemoveDisappearedHeroes) &&
                   hero is { IsActive: false, PartyBelongedTo: null, CurrentSettlement: null } &&
                   !Hero.AllAliveHeroes.Contains(hero) &&
                   !Hero.DeadOrDisabledHeroes.Contains(hero);
        }

        return false;
    }

    internal static void OnStartCleanPressed()
    {
        if (Instance is null) return;
        InformationManager.ShowInquiry(new InquiryData(
            "Confirm Action", "Go back to the world map to start the clean up process.", true, true,
            "OK", "Cancel",
            () =>
            {
                Instance._startPressed = true;
                Instance._wipeAddon = null;
            },
            () => Instance._startPressed = false));
    }

    internal static void OnWipePressed(SaveCleanerAddon addon)
    {
        if (Instance is null) return;
        InformationManager.ShowInquiry(new InquiryData(
            $"Confirm Wipe {addon.Name}", "Go back to the world map to start wipe out the mod's data.", true, true,
            "OK", "Cancel", () =>
            {
                Instance._wipeAddon = addon;
                Instance._startPressed = false;
            },
            () => Instance._wipeAddon = null));
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        Options.Version += 1;
    }

    public void OnMapScreenInit(MapScreen mapScreen)
    {
        CleanerMapView = mapScreen.AddMapView<CleanerMapView>() as CleanerMapView;
    }
}