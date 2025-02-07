using System;
using System.Collections.Generic;
using MCM.Abstractions.Base;
using MCM.Abstractions.FluentBuilder;
using MCM.Common;
using TaleWorlds.Localization;

namespace SaveCleaner;

internal static class MCMSettings
{
    private static string SettingsId => $"{SubModule.Name}_v{SubModule.Version.ToString(1)}";
    private static string SettingsName => $"{new TextObject("{=SVCLRSaveCleaner}Save Cleaner")} {SubModule.Version.ToString(3)}";

    private class ModPresetBuilder
    {
        internal event Action<ISettingsPresetBuilder> OnBuildDefaults;

        internal void BuildDefaults(ISettingsPresetBuilder builder)
        {
            OnBuildDefaults?.Invoke(builder);
        }
    }

    public static ISettingsBuilder AddSettings(string id)
    {
        ModPresetBuilder modPresetBuilder = new();

        return BaseSettingsBuilder
            .Create(SettingsId, SettingsName)!
            .SetFormat("json2")
            .SetFolderName(SubModule.Name)
            .SetSubFolder(id)
            .CreateGroup("{=SVCLRGroupMain} Main/{=SVCLRCleanerMenu}Cleaner", BuildCleanerGroup)
            .BuildModGroups(modPresetBuilder)
            .CreatePreset(BaseSettings.DefaultPresetId, BaseSettings.DefaultPresetName, builder
                => modPresetBuilder.BuildDefaults(builder));

        void BuildCleanerGroup(ISettingsPropertyGroupBuilder builder)
            => builder
                .SetGroupOrder(0)
                .AddButton(
                    "start_cleaning",
                    "{=SVCLRStartCleaning}Start Cleaning",
                    new ProxyRef<Action>(() => SubModule.OnStartCleanPressed, null),
                    "{=SVCLRStartButton}Start",
                    propBuilder => propBuilder
                        .SetHintText("{=SVCLRStartCleaningHint}Will start cleaning after closing the menu."))
                .AddBool("compatibility_mode",
                    "{=SVCLRCompatibilityMode}Compatibility Mode",
                    new ProxyRef<bool>(() => GlobalOptions.CompatibilityMode, value => GlobalOptions.CompatibilityMode = value),
                    propBuilder => propBuilder.SetHintText(
                        "{=SVCLRCompatibilityModeHint}Prevents items from being removed from mods without addons."));
    }

    private static ISettingsBuilder BuildModGroups(this ISettingsBuilder builder, ModPresetBuilder modPresetBuilder)
    {
        foreach (SaveCleanerAddon addon in AddonManager.Addons)
        {
            builder.CreateGroup($"{new TextObject("{=SVCLRGroupAddons}Addons")}/{addon.Name}",
                groupBuilder =>
                {
                    groupBuilder.SetGroupOrder(addon.Id == SubModule.Harmony.Id ? 0 : 1);
                    if (addon.Owner != typeof(SubModule))
                    {
                        groupBuilder.AddToggle(
                            AddonToggleId(addon.Id),
                            addon.Name,
                            new ProxyRef<bool>(() => !addon.Disabled, value => addon.Disabled = !value),
                            propBuilder => propBuilder.SetHintText(addon.Id));
                    }

                    modPresetBuilder.OnBuildDefaults += presetBuilder => presetBuilder.SetPropertyValue($"enable_{addon.Id}", true);

                    if (addon.CanWipe)
                    {
                        SaveCleanerAddon cache = addon;
                        groupBuilder
                            .AddButton(
                                $"start_wiping_{addon.Id}",
                                new TextObject("{=SVCLRAddonWipe}Wipe out data from {ADDON_NAME}",
                                    new Dictionary<string, object> { ["ADDON_NAME"] = addon.Name }).ToString(),
                                new ProxyRef<Action>(() => () => SubModule.OnWipePressed(cache), null),
                                "{=SVCLRStartButton}Start",
                                propBuilder => propBuilder
                                    .SetHintText(new TextObject("{=SVCLRAddonWipeHint}Will start wiping data from {ADDON_NAME} after closing the menu.",
                                        new Dictionary<string, object> { ["ADDON_NAME"] = addon.Name }).ToString())
                                    .SetOrder(1000));
                    }

                    foreach (SaveCleanerAddon.ISetting setting in addon.Settings)
                    {
                        switch (setting)
                        {
                            case SaveCleanerAddon.BoolSetting s:
                                groupBuilder.AddBool(
                                    ModSettingId(addon.Id, s.Id),
                                    s.Name,
                                    new ProxyRef<bool>(() => s.Value, value => s.Value = value),
                                    propBuilder => propBuilder.SetOrder(s.Order).SetHintText(s.Hint)
                                );
                                modPresetBuilder.OnBuildDefaults += presetBuilder => presetBuilder.SetPropertyValue(ModSettingId(addon.Id, setting.Id), s.DefaultValue);
                                break;
                            case SaveCleanerAddon.IntSetting s:
                                groupBuilder.AddInteger(
                                    ModSettingId(addon.Id, s.Id),
                                    s.Name,
                                    s.Min,
                                    s.Max,
                                    new ProxyRef<int>(() => s.Value, value => s.Value = value),
                                    propBuilder => propBuilder.SetOrder(s.Order).SetHintText(s.Hint)
                                );
                                modPresetBuilder.OnBuildDefaults += presetBuilder => presetBuilder.SetPropertyValue(ModSettingId(addon.Id, setting.Id), s.DefaultValue);
                                break;
                            case SaveCleanerAddon.FloatSetting s:
                                groupBuilder.AddFloatingInteger(
                                    ModSettingId(addon.Id, s.Id),
                                    s.Name,
                                    s.Min,
                                    s.Max,
                                    new ProxyRef<float>(() => s.Value, value => s.Value = value),
                                    propBuilder => propBuilder.SetOrder(s.Order).SetHintText(s.Hint)
                                );
                                modPresetBuilder.OnBuildDefaults += presetBuilder => presetBuilder.SetPropertyValue(ModSettingId(addon.Id, setting.Id), s.DefaultValue);
                                break;
                            case SaveCleanerAddon.StringSetting s:
                                groupBuilder.AddText(
                                    ModSettingId(addon.Id, s.Id),
                                    s.Name,
                                    new ProxyRef<string>(() => s.Value, value => s.Value = value),
                                    propBuilder => propBuilder.SetOrder(s.Order).SetHintText(s.Hint)
                                );
                                modPresetBuilder.OnBuildDefaults += presetBuilder => presetBuilder.SetPropertyValue(ModSettingId(addon.Id, setting.Id), s.DefaultValue);
                                break;
                            default:
                                throw new NotImplementedException($"Unknown setting type: {setting.Name} ({setting.GetType().Name})");
                        }
                    }
                });
        }

        return builder;

        static string AddonToggleId(string id) => $"enable_{id}";
        static string ModSettingId(string modId, string settingId) => $"{modId}_{settingId}";
    }
}