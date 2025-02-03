using System;
using MCM.Abstractions.Base;
using MCM.Abstractions.FluentBuilder;
using MCM.Common;

namespace SaveCleaner;

internal static class MCMSettings
{
    private static string SettingsId => $"{SubModule.Name}_v{SubModule.Version.ToString(1)}";
    private static string SettingsName => $"{SubModule.Name} {SubModule.Version.ToString(3)}";

    private class ModPresetBuilder
    {
        internal event Action<ISettingsPresetBuilder> OnBuildDefaults;

        internal void BuildDefaults(ISettingsPresetBuilder builder)
        {
            OnBuildDefaults?.Invoke(builder);
        }
    }

    public static ISettingsBuilder AddSettings(SaveCleanerOptions opt, string id)
    {
        ModPresetBuilder modPresetBuilder = new();

        return BaseSettingsBuilder
            .Create(SettingsId, SettingsName)!
            .SetFormat("json2")
            .SetFolderName(SubModule.Name)
            .SetSubFolder(id)
            .CreateGroup("Main/Actions", BuildButtonGroup)
            .BuildModGroups(modPresetBuilder)
            .CreatePreset(BaseSettings.DefaultPresetId, BaseSettings.DefaultPresetName, builder
                => modPresetBuilder.BuildDefaults(builder));

        void BuildButtonGroup(ISettingsPropertyGroupBuilder builder)
            => builder
                .SetGroupOrder(0)
                .AddButton(
                    "start_cleaning",
                    "Start Cleaning",
                    new ProxyRef<Action>(() => SubModule.OnStartCleanPressed, null),
                    "Start",
                    propBuilder => propBuilder
                        .SetHintText("Start cleaning after closing the menu."));
    }

    private static ISettingsBuilder BuildModGroups(this ISettingsBuilder builder, ModPresetBuilder modPresetBuilder)
    {
        foreach (SaveCleanerAddon addon in SubModule.Addons.Values)
        {
            builder.CreateGroup($"Mod/{addon.Name}",
                groupBuilder =>
                {
                    groupBuilder
                        .SetGroupOrder(addon.Id == SubModule.Harmony.Id ? 0 : 1)
                        .AddToggle(
                            AddonToggleId(addon.Id),
                            addon.Name,
                            new ProxyRef<bool>(() => !addon.Disabled, value => addon.Disabled = !value),
                            propBuilder => propBuilder.SetHintText(addon.Id));

                    modPresetBuilder.OnBuildDefaults += presetBuilder => presetBuilder.SetPropertyValue($"enable_{addon.Id}", true);

                    if (addon.CanWipe)
                    {
                        SaveCleanerAddon cache = addon;
                        groupBuilder
                            .AddButton(
                                $"start_wiping_{addon.Id}",
                                $"Wipe out data from {addon.Name}",
                                new ProxyRef<Action>(() => () => SubModule.OnWipePressed(cache), null),
                                "Wipe",
                                propBuilder => propBuilder
                                    .SetHintText($"Start wiping data from {addon.Id} after closing the menu.")
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