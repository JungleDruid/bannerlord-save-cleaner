using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using TaleWorlds.Library;
using TaleWorlds.LinQuick;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

#if DEBUG
using System.Diagnostics;
using TaleWorlds.InputSystem;
#endif

namespace SaveCleaner;

public class SubModule : MBSubModuleBase
{
    public static readonly string Name = typeof(SubModule).Namespace!;
    public static readonly Version Version = typeof(SubModule).Assembly.GetName().Version;
    public static readonly Harmony Harmony = new("Bannerlord.SaveCleaner.JungleDruid");
    private ILogger _logger;
    internal ILogger Logger => _logger ??= LogFactory.Get<SubModule>();
    public static SubModule Instance { get; private set; }
    private Cleaner _cleaner;
    public SaveEventReceiver SaveEventReceiver { get; } = new();
    private CleanerMapView CleanerMapView { get; set; }
    [CanBeNull] private FluentPerCampaignSettings _settings;
    private bool _startPressed;
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
        DefaultAddon.Register();
    }

    protected override void OnBeforeInitialModuleScreenSetAsRoot()
    {
        Logger.LogInformation($"{Name} {Version} starting up...");
    }

    protected override void OnApplicationTick(float dt)
    {
#if DEBUG
        bool superKey = (Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl)) &&
                        (Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift)) &&
                        (Input.IsKeyDown(InputKey.LeftAlt) || Input.IsKeyDown(InputKey.RightAlt));

        if (superKey && Input.IsKeyPressed(InputKey.B))
        {
            Debugger.Break();
        }

        if (superKey && Input.IsKeyPressed(InputKey.C))
        {
            TryStartCleanUp();
        }

        if (superKey && Input.IsKeyPressed(InputKey.O) && _settings is not null)
        {
            // refresh MCM menu
            string id = _settings.SubFolder;
            _settings.Unregister();
            ISettingsBuilder builder = MCMSettings.AddSettings(id);
            _settings = builder.SetOnPropertyChanged(OnPropertyChanged).BuildAsPerCampaign();
            _settings.Register();
            Addons.Remove(typeof(SubModule));
            DefaultAddon.Register();
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
            _startPressed = false;
            InformationManager.ShowInquiry(new InquiryData(
                new TextObject("{=SVCLRError}Error").ToString(),
                new TextObject("{=SVCLRErrorNoAddonAvailable}No addon available.").ToString(),
                true, false,
                new TextObject("{=SVCLRButtonOK}OK").ToString(),
                null, () => { }, () => { }));
            return false;
        }

        if (!CanCleanUp) return false;

        _startPressed = false;

        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        InformationManager.ShowInquiry(new InquiryData(
            new TextObject("{=SVCLRDialogConfirmCleanTitle}Confirm Clean Up").ToString(),
            new TextObject("{=SVCLRDialogConfirmCleanContent}Start the clean up progress now?").ToString(),
            true, true,
            new TextObject("{=SVCLRButtonYes}Yes").ToString(),
            new TextObject("{=SVCLRButtonNo}No").ToString(),
            () => _cleaner ??= new Cleaner(CleanerMapView, Addons.Values.ToListQ()).Start(),
            () => { }));

        return true;
    }

    private bool TryStartWipe()
    {
        if (_wipeAddon.Disabled)
        {
            _wipeAddon = null;
            InformationManager.ShowInquiry(new InquiryData(
                new TextObject("{=SVCLRError}Error").ToString(),
                new TextObject("{=SVCLRErrorWipeTargetDisabled}Wipe target is disabled.").ToString(),
                true, false,
                new TextObject("{=SVCLRButtonOK}OK").ToString(),
                null, () => { }, () => { }));
            return false;
        }

        if (!CanCleanUp) return false;

        SaveCleanerAddon addon = _wipeAddon;
        _wipeAddon = null;

        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        InformationManager.ShowInquiry(new InquiryData(
            new TextObject("{=SVCLRDialogConfirmWipeTitle}Confirm Wipe {ADDON_NAME}",
                new Dictionary<string, object> { ["ADDON_NAME"] = addon.Name }).ToString(),
            new TextObject("{=SVCLRDialogConfirmWipeContent}Start the wipe progress now?").ToString(),
            true, true,
            new TextObject("{=SVCLRButtonYes}Yes").ToString(),
            new TextObject("{=SVCLRButtonNo}No").ToString(),
            () => _cleaner ??= new Cleaner(CleanerMapView, Addons.Values.ToListQ(), addon).Start(),
            () => { }));

        return true;
    }

    protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
    {
        Campaign.Current.AddCampaignEventReceiver(SaveEventReceiver);
    }

    public override void OnAfterGameInitializationFinished(Game game, object starterObject)
    {
        if (game.GameType is not Campaign campaign) return;
        _settings?.Unregister();
        ISettingsBuilder builder = MCMSettings.AddSettings(campaign.UniqueGameId);
        _settings = builder.SetOnPropertyChanged(OnPropertyChanged).BuildAsPerCampaign();
        _settings?.Register();
    }

    internal static void OnStartCleanPressed()
    {
        if (Instance is null) return;
        InformationManager.ShowInquiry(new InquiryData(
            new TextObject("{=SVCLRDialogCleanTitle}Start Cleaning").ToString(),
            new TextObject("{=SVCLRDialogCleanContent}Go back to the world map to start the clean up process.").ToString(),
            true, true,
            new TextObject("{=SVCLRButtonOK}OK").ToString(),
            new TextObject("{=SVCLRButtonCancel}Cancel").ToString(),
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
            new TextObject("{=SVCLRDialogWipeTitle}Start Wiping {ADDON_NAME}", new Dictionary<string, object> { ["ADDON_NAME"] = addon.Name }).ToString(),
            new TextObject("{=SVCLRDialogWipeContent}Go back to the world map to start wipe out the mod's data.").ToString(),
            true,
            true,
            new TextObject("{=SVCLRButtonOK}OK").ToString(),
            new TextObject("{=SVCLRButtonCancel}Cancel").ToString(),
            () =>
            {
                Instance._wipeAddon = addon;
                Instance._startPressed = false;
            },
            () => Instance._wipeAddon = null));
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
    }

    public void OnMapScreenInit(MapScreen mapScreen)
    {
        CleanerMapView = mapScreen.AddMapView<CleanerMapView>() as CleanerMapView;
    }
}