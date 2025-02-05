using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Bannerlord.ButterLib.Logger.Extensions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.Core;
using TaleWorlds.LinQuick;

namespace SaveCleaner;

internal static class DefaultAddon
{
    private static readonly Dictionary<string, object> Vault = [];

    internal static void Register()
    {
        var addon = new SaveCleanerAddon(SubModule.Harmony.Id, "{=SVCLRSaveCleaner}Save Cleaner",
            new SaveCleanerAddon.BoolSetting(Settings.RemoveDisappearedHeroes, "{=SVCLRRemoveDisappearedHeroes}Remove Disappeared Heroes",
                "{=SVCLRRemoveDisappearedHeroesHint}These disappeared heroes are usually spawned by mods.", 0, true),
            new SaveCleanerAddon.BoolSetting(Settings.RemoveAbandonedCraftedItems, "{=SVCLRRemoveAbandonedCraftedItems}Remove Abandoned Crafted Items",
                "{=SVCLRRemoveAbandonedCraftedItemsHint}The game by default keeps all the crafted items even after they are disappeared.", 1, true));
        addon.OnPreClean += OnPreClean;
        addon.OnPostClean += OnPostClean;
        addon.Register<SubModule>();
    }

    private static bool OnPreClean(SaveCleanerAddon addon)
    {
        if (!FillVault(addon)) return false;

        addon.ClearRemovablePredicates();
        addon.ClearEssentialPredicates();

        if (addon.GetValue<bool>(Settings.RemoveDisappearedHeroes))
            addon.Removable += RemoveDisappearedHeroes;
        if (addon.GetValue<bool>(Settings.RemoveAbandonedCraftedItems) && Vault.ContainsKey("CraftingCampaignBehavior._craftedItemDictionary"))
            addon.Removable += RemoveAbandonedCraftedItems;

        return true;
    }

    private static bool FillVault(SaveCleanerAddon addon)
    {
        if (addon.GetValue<bool>(Settings.RemoveDisappearedHeroes))
        {
            object item = typeof(CraftingCampaignBehavior)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefaultQ(f => f.Name == "_craftedItemDictionary")
                ?.GetValue(CampaignBehaviorBase.GetCampaignBehavior<CraftingCampaignBehavior>());
            if (item != null)
            {
                Vault["CraftingCampaignBehavior._craftedItemDictionary"] = item;
            }
            else
            {
                SubModule.Instance.Logger.LogWarningAndDisplay($"SaveCleaner: Unable to find CraftingCampaignBehavior._craftedItemDictionary, disabling {Settings.RemoveAbandonedCraftedItems}");
            }
        }

        return true;
    }

    private static bool OnPostClean(SaveCleanerAddon addon)
    {
        Vault.Clear();

        return true;
    }

    private static bool RemoveDisappearedHeroes(SaveCleanerAddon addon, object o)
    {
        if (o is Hero hero)
        {
            return hero is { IsActive: false, PartyBelongedTo: null, CurrentSettlement: null } &&
                   !Hero.AllAliveHeroes.Contains(hero) &&
                   !Hero.DeadOrDisabledHeroes.Contains(hero);
        }

        return false;
    }

    private static bool RemoveAbandonedCraftedItems(SaveCleanerAddon addon, object obj)
    {
        if (obj is not ItemObject { IsCraftedByPlayer: true }) return false;

        if (addon.GetParents(obj).CountQ() == 1)
        {
            object parent = addon.GetParents(obj).First();
            if (parent == Vault["CraftingCampaignBehavior._craftedItemDictionary"])
                return true;
#if DEBUG
            Debugger.Break();
#endif
        }

        return false;
    }
}