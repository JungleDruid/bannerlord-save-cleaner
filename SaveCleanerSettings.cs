using System;
using MCM.Abstractions.Base;
using MCM.Abstractions.FluentBuilder;
using MCM.Abstractions.FluentBuilder.Models;
using MCM.Common;

namespace SaveCleaner;

public static class SaveCleanerSettings
{
    private static string SettingsId => $"{SubModule.Name}_v{SubModule.Version.ToString(1)}";
    private static string SettingsName => $"{SubModule.Name} {SubModule.Version.ToString(3)}";

    public static ISettingsBuilder AddSettings(SaveCleanerOptions opt, string id)
    {
        return BaseSettingsBuilder.Create(SettingsId, SettingsName)!
            .SetFormat("json2")
            .SetFolderName(SubModule.Name)
            .SetSubFolder(id)
            .CreateGroup("Actions", BuildButtonGroup)
            .CreateGroup("Clean Heroes", BuildCleanHeroesGroup)
            .CreateGroup("Mod Removables", BuildModRemovablesGroup)
            .CreateGroup("Mod Force-Keeps", BuildModForceKeepsGroup)
            .CreatePreset(BaseSettings.DefaultPresetId, BaseSettings.DefaultPresetName, builder => BuildDefaultPreset(builder, new SaveCleanerOptions()));

        static void BuildDefaultPreset(ISettingsPresetBuilder builder, SaveCleanerOptions opt)
            => builder
                .SetPropertyValue("clean_heroes", opt.CleanHeroes)
                .SetPropertyValue("clean_disappeared_heroes", opt.CleanDisappearedHeroes);

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

        void BuildCleanHeroesGroup(ISettingsPropertyGroupBuilder builder)
            => builder
                .SetGroupOrder(1)
                .AddToggle(
                    "clean_heroes",
                    "Clean Heroes",
                    new ProxyRef<bool>(() => opt.CleanHeroes, value => opt.CleanHeroes = value),
                    propBuilder => propBuilder
                        .SetHintText("Use built-in methods to clean up heroes."))
                .AddBool(
                    "clean_disappeared_heroes",
                    "Clean Disappeared Heroes",
                    new ProxyRef<bool>(() => opt.CleanDisappearedHeroes, value => opt.CleanDisappearedHeroes = value),
                    propBuilder => propBuilder
                        .SetHintText("Clean up disappeared heroes that are likely spawned by mods."));

        void BuildModRemovablesGroup(ISettingsPropertyGroupBuilder builder)
        {
            builder
                .SetGroupOrder(2)
                .AddToggle(
                    "mod_removable",
                    "Mod Removables",
                    new ProxyRef<bool>(() => opt.ModRemovableEnabled, value => opt.ModRemovableEnabled = value),
                    propBuilder => propBuilder
                        .SetHintText("Allow other mods to define removable objects."));
            foreach (Type key in CleanConditions.Removable.Keys)
            {
                if (key == typeof(SubModule)) continue;
                string modId = CleanConditions.GetModuleId(key);
                string name = CleanConditions.GetModuleName(key);
                builder.AddBool(
                    "enable_removable_" + modId,
                    name,
                    new ProxyRef<bool>(() => !opt.RemovableDisabled.Contains(modId),
                        value =>
                        {
                            if (value) opt.RemovableDisabled.Remove(modId);
                            else opt.RemovableDisabled.Add(modId);
                        }),
                    propBuilder => propBuilder.SetHintText(modId));
            }
        }

        void BuildModForceKeepsGroup(ISettingsPropertyGroupBuilder builder)
        {
            builder
                .SetGroupOrder(3)
                .AddToggle(
                    "mod_force_keep",
                    "Mod Force Keep",
                    new ProxyRef<bool>(() => opt.ModForceKeepEnabled, value => opt.ModForceKeepEnabled = value),
                    propBuilder => propBuilder
                        .SetHintText("Allow other mods to keep objects from being removed."));
            foreach (Type key in CleanConditions.ForceKeep.Keys)
            {
                if (key == typeof(SubModule)) continue;
                string modId = CleanConditions.GetModuleId(key);
                string name = CleanConditions.GetModuleName(key);
                builder.AddBool(
                    "enable_force_keep_" + modId,
                    name,
                    new ProxyRef<bool>(() => !opt.ForceKeepDisabled.Contains(modId),
                        value =>
                        {
                            if (value) opt.ForceKeepDisabled.Remove(modId);
                            else opt.ForceKeepDisabled.Add(modId);
                        }),
                    propBuilder => propBuilder.SetHintText(modId));
            }
        }
    }
}