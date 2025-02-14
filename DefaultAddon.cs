using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using SaveCleaner.Utils;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.LogEntries;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.LinQuick;
using TaleWorlds.SaveSystem;

namespace SaveCleaner;

internal static class DefaultAddon
{
    private static readonly Dictionary<string, object> Vault = [];

    internal static void Register()
    {
        var addon = new SaveCleanerAddon(SubModule.Harmony.Id, "{=SVCLRSaveCleaner}Save Cleaner",
            new SaveCleanerAddon.BoolSetting(Settings.RemoveDisappearedHeroes, "{=SVCLRRemoveDisappearedHeroes}Remove Disappeared Heroes",
                "{=SVCLRRemoveDisappearedHeroesHint}These disappeared heroes are usually spawned by mods.", 1, true),
            new SaveCleanerAddon.BoolSetting(Settings.RemoveAbandonedCraftedItems, "{=SVCLRRemoveAbandonedCraftedItems}Remove Abandoned Crafted Items",
                "{=SVCLRRemoveAbandonedCraftedItemsHint}The game by default keeps all the crafted items even after they are disappeared.", 2, true),
            new SaveCleanerAddon.BoolSetting(Settings.RemoveCorruptedLogs, "{=SVCLRRemoveCorruptedLogs}Remove Corrupted Logs",
                "{=SVCLRRemoveCorruptedLogsHint}Remove log entries that have important data missing, which may cause issues.", 2, true),
            new SaveCleanerAddon.BoolSetting(Settings.RemoveGlitchedParties, "{=SVCLRRemoveGlitchedParties}(Beta) Remove Glitched Parties",
                "{=SVCLRRemoveGlitchedPartiesHint}Remove parties that could crash the game likely due to mod uninstallations.", 2, false));
        addon.OnPreClean += OnPreClean;
        addon.OnPostClean += OnPostClean;
        addon.CanRemoveChild += CanRemoveChild;
        addon.DoRemoveChild += DoRemoveChild;
        addon.Dependencies += HeroDependencies;
        addon.Essential += Essential;
        addon.AddSupportedNamespace(new Regex(@"^(TaleWorlds|StoryMode|SandBox|System)\b"));
        addon.Register<SubModule>();
    }

    private static bool Essential(SaveCleanerAddon addon, object obj)
    {
        return obj switch
        {
            PlayerCharacterChangedLogEntry => true,
            PlayerRetiredLogEntry => true,
            Hero hero => hero.Clan == Clan.PlayerClan,
            _ => false
        };
    }

    private static IEnumerable<object> HeroDependencies(SaveCleanerAddon addon, object obj)
    {
        switch (obj)
        {
            case Hero hero:
                if (hero.CharacterObject != null)
                {
                    yield return hero.CharacterObject;
                }

                foreach (object logEntry in addon.GetParents(hero).WhereQ(o => o is LogEntry))
                {
                    yield return logEntry;
                }

                break;
            case CharacterObject { HeroObject: not null } characterObject:
                yield return characterObject.HeroObject;

                foreach (object logEntry in addon.GetParents(characterObject).WhereQ(o => o is LogEntry))
                {
                    yield return logEntry;
                }

                break;
        }
    }

    private static bool CanRemoveChild(SaveCleanerAddon addon, Node node)
    {
        object child = node.Value;
        object parent = node.Parent.Value;

        switch (child)
        {
            case CharacterObject { HeroObject: not null } characterObject when parent is Hero hero && hero.CharacterObject == characterObject:
            case Hero when parent is Hero or LogEntry:
                return true;
        }

        return RemoveFromParent(addon, node, true);
    }

    private static bool DoRemoveChild(SaveCleanerAddon addon, Node node)
    {
        return RemoveFromParent(addon, node, false);
    }

    private static bool RemoveFromParent(SaveCleanerAddon addon, Node node, bool dryRun)
    {
        bool removed = false;
        object child = node.Value;
        object parent = node.Parent.Value;

        if (child is MobileParty mobileParty)
        {
            return RemoveMobileParty(mobileParty, dryRun);
        }

        if (parent.GetType().IsContainer(out ContainerType containerType))
        {
            return RemoveFromContainer(addon, node, containerType, dryRun);
        }

        foreach (FieldInfo field in parent.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(parent) != child) continue;
            if (!dryRun) field.SetValue(parent, null);
            removed = true;
        }

        return removed;
    }

    private static bool RemoveMobileParty(MobileParty mobileParty, bool dryRun)
    {
        if (!mobileParty.IsActive && !mobileParty.IsVisible && mobileParty.Ai?.IsDisabled != true) return true;
        if (dryRun) return true;
        mobileParty.MapEvent?.FinalizeEvent();
        mobileParty.IsActive = false;
        mobileParty.Ai?.DisableAi();
        DestroyPartyAction.Apply(null, mobileParty);
        return true;
    }

    private static bool RemoveFromContainer(SaveCleanerAddon addon, Node node, ContainerType containerType, bool dryRun)
    {
        bool removed = false;
        object child = node.Value;
        object parent = node.Parent.Value;
        Type parentType = parent.GetType();
        switch (containerType)
        {
            case ContainerType.CustomList:
            case ContainerType.CustomReadOnlyList:
            case ContainerType.List:
                if (!dryRun) parentType.GetMethod("Remove")!.Invoke(parent, [child]);
                removed = true;
                break;
            case ContainerType.Dictionary:
                if (parentType.GenericTypeArguments.Length == 2 && parentType.GenericTypeArguments[0] == child.GetType())
                {
                    if (!dryRun) parentType.GetMethod("Remove")!.Invoke(parent, [child]);
                    removed = true;
                }

                break;
            case ContainerType.Array:
                if (parent is TroopRosterElement[] troopRosterElements)
                {
                    foreach (object rosterObject in addon.GetParents(troopRosterElements))
                    {
                        if (rosterObject is not TroopRoster roster) continue;
                        if (roster.Contains((CharacterObject)child))
                        {
                            if (!dryRun)
                            {
                                roster.RemoveTroop((CharacterObject)child);
                            }
                        }
                        else
                        {
                            SafeDebugger.Break();
                        }

                        removed = true;
                    }
                }
                else if (parent is ItemRosterElement[] itemRosterElements)
                {
                    foreach (object rosterObject in addon.GetParents(itemRosterElements))
                    {
                        if (rosterObject is not ItemRoster roster) continue;
                        if (!dryRun) roster.RemoveIf(e => e.EquipmentElement.Item == child ? e.Amount : 0);
                        removed = true;
                    }
                }

                break;
        }

        if (!removed)
            addon.Log($"Failed to remove [{child.GetType().Name}]{child} from [{parentType.Name}]{parent}. Link: {node.GetLinkString()}", LogLevel.Debug);
        return removed;
    }

    private static bool OnPreClean(SaveCleanerAddon addon)
    {
        if (!FillVault(addon)) return false;

        addon.ClearRemovablePredicates();

        if (addon.GetValue<bool>(Settings.RemoveDisappearedHeroes))
            addon.Removable += RemoveDisappearedHeroes;
        if (addon.GetValue<bool>(Settings.RemoveAbandonedCraftedItems) && Vault.ContainsKey("CraftingCampaignBehavior._craftedItemDictionary"))
            addon.Removable += RemoveAbandonedCraftedItems;
        if (addon.GetValue<bool>(Settings.RemoveCorruptedLogs))
            addon.Removable += RemoveCorruptedLogs;
        if (addon.GetValue<bool>(Settings.RemoveGlitchedParties))
            addon.Removable += RemoveGlitchedParties;

        return true;
    }

    private static bool RemoveGlitchedParties(SaveCleanerAddon addon, object obj)
    {
        if (obj is not MobileParty mobileParty) return false;
        if (mobileParty.IsActive && mobileParty.IsVisible && mobileParty.PartyComponent is null) return true;
        // fix crashes at TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors.AiPatrollingBehavior.AiHourlyTick
        if (!mobileParty.IsMilitia && !mobileParty.IsCaravan && !mobileParty.IsVillager && !mobileParty.IsBandit && !mobileParty.IsDisbanding && mobileParty.MapFaction?.Leader is null) return true;
        return false;
    }

    private static bool FillVault(SaveCleanerAddon addon)
    {
        Vault.Clear();
        if (addon.GetValue<bool>(Settings.RemoveAbandonedCraftedItems))
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
                addon.LogAndDisplay($"Unable to find CraftingCampaignBehavior._craftedItemDictionary, disabling {Settings.RemoveAbandonedCraftedItems}", LogLevel.Warning);
            }
        }

        return true;
    }

    private static bool OnPostClean(SaveCleanerAddon addon)
    {
        return true;
    }

    private static bool RemoveDisappearedHeroes(SaveCleanerAddon addon, object o)
    {
        switch (o)
        {
            case Hero hero:
                if (hero.CharacterObject is null) return true;
                if (hero is { PartyBelongedTo: null, CurrentSettlement: null } &&
                    (hero.IsDead || hero.IsDisabled) &&
                    !Hero.AllAliveHeroes.Contains(hero) &&
                    !Hero.DeadOrDisabledHeroes.Contains(hero)) return true;
                break;
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
            SafeDebugger.Break();
        }

        return false;
    }

    private static readonly AccessTools.FieldRef<OverruleInfluenceLogEntry, Hero> OverruleInfluenceLogEntryLiege =
        AccessTools.FieldRefAccess<OverruleInfluenceLogEntry, Hero>("_liege");

    private static bool RemoveCorruptedLogs(SaveCleanerAddon addon, object obj)
    {
        return obj switch
        {
            BesiegeSettlementLogEntry { BesiegerHero: null } => true,
            ChangeRomanticStateLogEntry e when e.Hero1 is null || e.Hero2 is null => true,
            CharacterBecameFugitiveLogEntry { Hero: null } => true,
            CharacterBornLogEntry { BornCharacter: null } => true,
            CharacterInsultedLogEntry e when e.Insultee is null || e.Insulter is null => true,
            CharacterKilledLogEntry { Victim: null } => true,
            CharacterMarriedLogEntry e when e.MarriedTo is null || e.MarriedHero is null => true,
            ChildbirthLogEntry { Mother: null } => true,
            ClanLeaderChangedLogEntry e when e.OldLeader is null || e.NewLeader is null => true,
            DefeatCharacterLogEntry e when e.WinnerHero is null || e.LoserHero is null => true,
            EndCaptivityLogEntry { Prisoner: null } => true,
            GatherArmyLogEntry { ArmyLeader: null } => true,
            IssueQuestLogEntry { IssueGiver: null } => true,
            IssueQuestStartLogEntry { IssueGiver: null } => true,
            OverruleInfluenceLogEntry e when OverruleInfluenceLogEntryLiege(e) is null => true,
            PlayerAttackAlleyLogEntry { CommonAreaOwner: null } => true,
            PlayerMeetLordLogEntry { Hero: null } => true,
            PregnancyLogEntry { Mother: null } => true,
            SettlementClaimedLogEntry { Claimant: null } => true,
            TakePrisonerLogEntry { Prisoner: null } => true,
            TournamentWonLogEntry { Winner: null } => true,
            VillageStateChangedLogEntry { RaidLeader: null } => true,
            _ => false
        };
    }
}