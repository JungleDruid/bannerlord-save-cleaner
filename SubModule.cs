using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
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

    private void OnServiceRegistration()
    {
        this.AddSerilogLoggerProvider($"{Name}.log", [$"{Name}.*"], o => o.MinimumLevel.Verbose());
    }

    protected override void OnSubModuleLoad()
    {
        Instance = this;
        OnServiceRegistration();
        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        CleanConditions.AddRemovable<SubModule>(Removable);
        CleanConditions.AddEssential<SubModule>(ForceKeep);
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

        if (_cleaner is null) return;
        if (_cleaner.Completed)
        {
            _cleaner = null;
        }
        else _cleaner.CleanerTick();
    }

    private bool TryStartCleanUp()
    {
        var mapScreen = MapScreen.Instance;
        if (mapScreen is null || mapScreen.IsActive != true || CleanerMapView is null || CleanerMapView.IsFinalized) return false;
        _cleaner ??= new Cleaner(CleanerMapView, Options).Start();
        _startPressed = false;
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
        ISettingsBuilder builder = SaveCleanerSettings.AddSettings(Options, campaign.UniqueGameId);
        _settings = builder.SetOnPropertyChanged(OnPropertyChanged).BuildAsPerCampaign();
        _settings?.Register();
    }

    private bool Removable(object obj)
    {
        if (obj is Hero hero)
        {
            return hero is { IsActive: false, PartyBelongedTo: null, CurrentSettlement: null } &&
                   !Hero.AllAliveHeroes.Contains(hero) &&
                   !Hero.DeadOrDisabledHeroes.Contains(hero);
        }

        return false;
    }

    private bool ForceKeep(object obj)
    {
        return false;
    }

    internal static void OnStartCleanPressed()
    {
        if (Instance is null) return;
        InformationManager.ShowInquiry(new InquiryData(
            "", "Close the menu to start the clean up process.", true, true,
            "OK", "Cancel", () => Instance._startPressed = true, () => Instance._startPressed = false));
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