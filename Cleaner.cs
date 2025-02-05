using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Bannerlord.ButterLib.Logger.Extensions;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Internal;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.LogEntries;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.LinQuick;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace SaveCleaner;

internal class Cleaner(CleanerMapView mapView, List<SaveCleanerAddon> addons, SaveCleanerAddon wiping = null)
{
    private readonly ILogger _logger = LogFactory.Get<Cleaner>();
    private static string PlayerClanAndName => $"{Clan.PlayerClan.Name.ToString().ToLower()}_{Hero.MainHero.Name.ToString().ToLower()}";
    private string ActionName => wiping == null ? "cleaning" : $"wiping_{wiping.Name}";
    private string BackupSaveName => $"before_{ActionName}_{PlayerClanAndName}_";
    private string FinishSaveName => $"after_{ActionName}_{PlayerClanAndName}_";
    private Stopwatch _stopwatch;
    private string _backUpSave;
    private string _finishSave;
    private Dictionary<Type, int> _beforeCleanTypes;
    private Dictionary<Type, int> _afterCleanTypes;
    private CleanerState _state;
    private DetailState _detailState;
    private int _messageTick;
    private bool _cleaned;
    private readonly Collector _collector = new();
    private readonly Dictionary<object, SaveCleanerAddon> _removableObjects = [];

    public bool Completed => _state == CleanerState.Complete && _detailState == DetailState.Ended;

    public Cleaner Start()
    {
        if (_state != CleanerState.None)
        {
            _logger.LogError("The cleaner is already running!");
            OnError();
            return this;
        }

        _stopwatch = new Stopwatch();
        _stopwatch.Start();

        ForwardState();
        mapView.SetActive(true);
        mapView.SetText(new TextObject("{=SVCLRCleanStarted}Clean Started"));
        string line = new('=', 10);
        LogAndMessage($"{line} Start Cleaning {line}",
            $"{line} {new TextObject("{=SVCLRCleanStarted}Clean Started")} {line}",
            LogLevel.Information);

        return this;
    }

    public IEnumerable<object> GetAllParents(object obj, int depth, HashSet<object> visited)
    {
        if (!visited.Add(obj) || depth == 0) yield break;
        if (!_collector.ParentMap.TryGetValue(obj, out var parents)) yield break;

        foreach (object parent in parents.Where(parent => !visited.Contains(parent)))
        {
            yield return parent;
        }

        if (depth == 1) yield break;
        foreach (object parent in parents.Where(parent => !visited.Contains(parent)))
        {
            foreach (object p in GetAllParents(parent, depth - 1, visited)) yield return p;
        }
    }

    public IEnumerable<T> GetAllParents<T>(object obj, int depth, HashSet<object> visited)
    {
        foreach (object parent in GetAllParents(obj, depth, visited))
        {
            if (parent is T t) yield return t;
        }
    }

    public IEnumerable<object> GetParents(object obj)
    {
        return _collector.ParentMap.TryGetValue(obj, out var parents) ? parents : [];
    }

    public object GetFirstParent(object obj, Func<object, bool> predicate, int depth, HashSet<object> visited)
    {
        if (!visited.Add(obj) || depth == 0) return null;
        if (!_collector.ParentMap.TryGetValue(obj, out var parents)) return null;
        object match = parents.FirstOrDefaultQ(predicate);
        if (match != null) return match;
        if (depth == 1) return null;
        return parents
            .Where(parent => !visited.Contains(parent))
            .Select(parent => GetFirstParent(parent, predicate, depth - 1, visited))
            .FirstOrDefault(o => o != null);
    }

    private void Collecting()
    {
        if (!StateGate(new TextObject("{=SVCLRStatusCollecting}Collecting objects..."))) return;

        var failedAddons = addons.WhereQ(a => !a.Disabled && !a.PreClean(this)).ToListQ();
        if (failedAddons.Count > 0)
        {
            _logger.LogErrorAndDisplay($"PreClean failed by addons: [{failedAddons.Join()}]");
            OnError();
            return;
        }

        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        Campaign.Current.SetTimeControlModeLock(true);

        try
        {
            Campaign.Current.WaitAsyncTasks();
            _logger.LogDebug("Collecting objects...");
            var childObjects = _collector.CollectObjects();
            _logger.LogDebug($"Collected {childObjects.Count} objects.");

            _beforeCleanTypes = Collector.GetTypeCollection(childObjects);
            foreach (var kv in _beforeCleanTypes.OrderByQ(kv => -kv.Value))
            {
                _logger.LogTrace($"Collected [{kv.Key.Name}]: {kv.Value}");
            }

            _logger.LogDebug("Collecting references...");
            foreach (object obj in childObjects)
            {
                SaveCleanerAddon addon = RemovableFromAddons(obj);
                if (addon is null) continue;
                CollectReferences(obj, addon);
            }

            FilterOutNonRemovableObjects();

            _logger.LogDebug($"Collected {_removableObjects.Count} removable objects.");
            if (_removableObjects.Any())
            {
                var collected = Collector.GetTypeCollection(_removableObjects.Keys);
                foreach (var kv in collected.OrderByQ(kv => -kv.Value))
                {
                    _logger.LogTrace($"Collected Removable [{kv.Key.Name}]: {kv.Value}");
                }

                foreach (var grouping in _removableObjects.GroupBy(kv => kv.Value))
                {
                    int count = grouping.Count();
                    LogAndMessage($"{grouping.Key} has collected {count} removable objects.",
                        new TextObject("{=SVCLRAddonCollectedCount}{ADDON} has collected {NUMBER} removable objects.",
                            new Dictionary<string, object>
                            {
                                ["ADDON"] = grouping.Key.Name,
                                ["NUMBER"] = count
                            }).ToString());
                }

                LogAndMessage($"Collected {_removableObjects.Count} removable objects in total.",
                    new TextObject("{=SVCLRTotalCollectedCount}Collected {NUMBER} removable objects in total.",
                        new Dictionary<string, object> { ["NUMBER"] = _removableObjects.Count, }).ToString());
                FinishState();
            }
            else
            {
                LogAndMessage("Nothing to clean.", new TextObject("{=SVCLRNothingToClean}Nothing to clean.").ToString(), LogLevel.Information);
                if (wiping is null) OnComplete();
                else ChangeState(CleanerState.Finalizing);
            }
        }
        catch (Exception ex)
        {
            LogAndMessage("Error while collecting objects.",
                new TextObject("{=SVCLRErrorCollectingObjects}Error while collecting objects.").ToString(),
                LogLevel.Error, ex);
            OnError();
        }
    }

    private bool StateGate(TextObject startMessage)
    {
        switch (_detailState)
        {
            case DetailState.None:
                LogAndMessage(startMessage.Value.Substring(startMessage.Value.IndexOf('}')), startMessage.ToString());
                mapView.SetText(startMessage);
                _detailState = DetailState.Starting;
                return false;
            case DetailState.Starting:
                if (--_messageTick > 0) return false;
                _detailState = DetailState.Started;
                return true;
            case DetailState.Started:
                return false;
            case DetailState.Ended:
                ForwardState();
                return false;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void BackingUp()
    {
        if (Campaign.Current.SaveHandler.IsSaving) return;
        if (!StateGate(new TextObject("{=SVCLRStatusBackup}Creating backup save..."))) return;

        _backUpSave = GetAvailableSaveName(BackupSaveName);
        SubModule.Instance.SaveEventReceiver.SaveOver += OnSaveOver;
        Campaign.Current.SaveHandler.SaveAs(_backUpSave);
    }

    private void Finalizing()
    {
        if (Campaign.Current.SaveHandler.IsSaving) return;
        if (!StateGate(new TextObject("{=SVCLRStatusFinalizing}Saving game..."))) return;

        var failedAddons = addons.WhereQ(a => !a.Disabled && !a.PostClean()).ToListQ();
        if (failedAddons.Count > 0)
        {
            _logger.LogErrorAndDisplay($"PostClean failed by addons: [{failedAddons.Join()}]");
            OnError();
            return;
        }

        Campaign.Current.SetTimeControlModeLock(false);
        _finishSave = GetAvailableSaveName(FinishSaveName);
        SubModule.Instance.SaveEventReceiver.SaveOver += OnSaveOver;
        Campaign.Current.SaveHandler.SaveAs(_finishSave);
    }

    private static string GetAvailableSaveName(string prefix)
    {
        int index = 0;
        while (true)
        {
            string saveName = prefix + index;
            if (MBSaveLoad.GetSaveFileWithName(saveName) is null) return saveName;
            index += 1;
        }
    }

    public void CleanerTick()
    {
        switch (_state)
        {
            case CleanerState.None:
                break;
            case CleanerState.BackingUp:
                BackingUp();
                break;
            case CleanerState.Collecting:
                Collecting();
                break;
            case CleanerState.Removing:
                Removing();
                break;
            case CleanerState.Counting:
                Counting();
                break;
            case CleanerState.Finalizing:
                Finalizing();
                break;
            case CleanerState.Complete:
                OnComplete();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void Counting()
    {
        if (!StateGate(new TextObject("{=SVCLRStatusCounting}Counting results..."))) return;

        Campaign.Current.WaitAsyncTasks();
        var childObjects = new Collector().CollectObjects();
        _afterCleanTypes = Collector.GetTypeCollection(childObjects);

        Dictionary<Type, int> result = new();
        foreach (var kv in _afterCleanTypes)
        {
            if (!_beforeCleanTypes.TryGetValue(kv.Key, out int before)) before = 0;
            if (before != kv.Value)
            {
                result.Add(kv.Key, before - kv.Value);
            }
        }

        LogAndMessage("Clean results:",
            new TextObject("{=SVCLRCleanResults}Clean results:").ToString(),
            LogLevel.Information);
        foreach (var kv in result.OrderByQ(kv => -kv.Value))
        {
            string logMessage = $"[{kv.Key.Name}]: {kv.Value}";
            LogAndMessage(logMessage, logMessage, LogLevel.Information);
        }

        FinishState();
    }

    private void FilterOutNonRemovableObjects()
    {
        while (_removableObjects.Count > 0)
        {
            var failedObjects = _removableObjects.WhereQ(kv => !RemoveReferences(kv.Key, kv.Value, true)).ToListQ();
            if (!failedObjects.AnyQ()) return;
            failedObjects.Select(kv => kv.Key).Do(FlushFromRemovables);
        }
    }

    private void FlushFromRemovables(object obj)
    {
        _removableObjects.Remove(obj);
        if (!_collector.ChildMap.TryGetValue(obj, out var set)) return;
        set.WhereQ(_removableObjects.ContainsKey).Do(FlushFromRemovables);
    }

    private void Removing()
    {
        if (!StateGate(new TextObject("{=SVCLRStatusRemoving}Removing objects..."))) return;

        if (_removableObjects.WhereQ(kv => !RemoveReferences(kv.Key, kv.Value, false)).AnyQ())
        {
            throw new InvalidOperationException("Removing objects failed, but should never happen.");
        }

        LogAndMessage($"Cleaned {_removableObjects.Count} objects.",
            new TextObject("{=SVCLRCleanedObjectsCount}Cleaned {NUMBER} objects.",
                new Dictionary<string, object> { ["NUMBER"] = _removableObjects.Count }).ToString(),
            LogLevel.Information);
        _cleaned = true;
        FinishState();
    }

    private void FinishState()
    {
        _detailState = DetailState.Ended;
    }

    private bool SafeToRemove(object obj)
    {
        if (_removableObjects.ContainsKey(obj)) return true;
        if (obj.GetType().IsContainer()) return false;
        switch (obj)
        {
            case LogEntry:
            case CharacterObject { HeroObject: not null } characterObject when _removableObjects.ContainsKey(characterObject.HeroObject):
                return true;
            default:
                return false;
        }
    }

    private void OnSaveOver(bool isSuccessful, string saveName)
    {
        if (saveName != _backUpSave && saveName != _finishSave) return;
        SubModule.Instance.SaveEventReceiver.SaveOver -= OnSaveOver;

        if (!isSuccessful)
        {
            string message = $"Failed to {(_state == CleanerState.BackingUp ? "backup before" : "save after ")} cleaning.";
            LogAndMessage(message, message, LogLevel.Error);
            OnError();
            return;
        }

        if (_state == CleanerState.BackingUp && wiping?.Wipe() == false)
        {
            LogAndMessage("Wipe failed!", new TextObject("{=SVCLRWipeFailed}Wipe failed!").ToString(), LogLevel.Error);
            OnError();
            return;
        }

        FinishState();
    }

    private void OnComplete()
    {
        if (_detailState == DetailState.Ended) return;
        Campaign.Current.SetTimeControlModeLock(false);
        if (!_cleaned && _backUpSave is not null && wiping is null)
        {
            SaveGameFileInfo save = MBSaveLoad.GetSaveFileWithName(_backUpSave);
            if (save is not null)
            {
                LogAndMessage("Removing backup save...", new TextObject("{=SVCLRRemovingBackup}Removing backup save...").ToString());
                MBSaveLoad.DeleteSaveGame(_backUpSave);
            }
        }

        ChangeState(CleanerState.Complete);
        _stopwatch.Stop();
        string seconds = (_stopwatch.ElapsedMilliseconds / 1000f).ToString("F2");
        LogAndMessage($"Clean complete. Took {seconds} seconds to finish.",
            new TextObject("{=SVCLRCleanComplete}Clean complete. Took {NUMBER} seconds to finish.",
                new Dictionary<string, object> { ["NUMBER"] = seconds }).ToString(),
            LogLevel.Information);
        if (_finishSave is not null)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                new TextObject("{=SVCLRSuccessLoadReminder}Please load the save before continue playing: ") + _finishSave, Colors.Yellow));
        }

        mapView.SetActive(false);
        FinishState();
    }

    private void OnError()
    {
        Campaign.Current.SetTimeControlModeLock(false);
        ChangeState(CleanerState.Complete);
        _stopwatch.Stop();
        LogAndMessage("Clean terminated. See logs for details.",
            new TextObject("{=SVCLRCleanTerminated}Clean terminated. See logs for details.").ToString(),
            LogLevel.Error);

        if (_backUpSave is not null)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                new TextObject("{=SVCLRFailureLoadReminder}Please load the backup save before continue playing: ") + _backUpSave, Colors.Red));
        }

        mapView.SetActive(false);
        FinishState();
    }

    private void CollectReferences(object obj, SaveCleanerAddon sourceAddon)
    {
        if (obj.GetType().IsContainer()) return;
        if (_removableObjects.ContainsKey(obj)) return;
        _removableObjects.Add(obj, sourceAddon);
        _logger.LogTrace($"Collecting references of [{obj.GetType()}]{obj}...");

        if (_collector.ParentMap.TryGetValue(obj, out var set))
        {
            foreach (object parent in set.Where(SafeToRemove))
            {
                CollectReferences(parent, sourceAddon);
            }
        }
        else
        {
            _logger.LogWarning($"Could find parents of [{obj.GetType()}]{obj}");
        }
    }

    private bool RemoveReferences(object obj, SaveCleanerAddon source, bool dryRun)
    {
        if (!dryRun) _logger.LogTrace($"[{source}] Removing references of [{obj.GetType()}]{obj}...");
        if (!dryRun && obj is MBObjectBase mbObject)
        {
            MBObjectManager.Instance.UnregisterObject(mbObject);
        }

        if (_collector.ParentMap.TryGetValue(obj, out var set))
        {
            foreach (object parent in set.Where(parent => !_removableObjects.ContainsKey(parent) && !RemoveFromParent(obj, parent, dryRun)))
            {
                _logger.LogWarning($"Failed to remove [{obj.GetType()}]{obj} from [{parent.GetType()}]{parent}");
                return false;
            }
        }
        else
        {
            _logger.LogWarning($"Could find parents of [{obj.GetType()}]{obj}");
            return false;
        }

        return true;
    }

    private bool RemoveFromParent(object obj, object parent, bool dryRun)
    {
        _logger.LogTrace($"Removing [{obj.GetType()}]{obj} from [{parent.GetType()}]{parent}");
        bool removed = false;
        if (parent.GetType().IsContainer(out ContainerType containerType))
        {
            return RemoveFromContainer(obj, parent, containerType, dryRun);
        }

        foreach (var field in parent.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(parent) != obj) continue;
            if (!dryRun) field.SetValue(parent, null);
            removed = true;
        }

        return removed;
    }

    private bool RemoveFromContainer(object obj, object parent, ContainerType containerType, bool dryRun)
    {
        bool removed = false;
        Type parentType = parent.GetType();
        switch (containerType)
        {
            case ContainerType.CustomList:
            case ContainerType.CustomReadOnlyList:
            case ContainerType.List:
                if (!dryRun) parentType.GetMethod("Remove")!.Invoke(parent, [obj]);
                removed = true;
                break;
            case ContainerType.Dictionary:
                if (parentType.GenericTypeArguments.Length == 2 && parentType.GenericTypeArguments[0] == obj.GetType())
                {
                    if (!dryRun) parentType.GetMethod("Remove")!.Invoke(parent, [obj]);
                    removed = true;
                }

                break;
            case ContainerType.Array:
                if (parent is TroopRosterElement[] troopRosterElements)
                {
                    if (_collector.ParentMap.TryGetValue(troopRosterElements, out var set))
                    {
                        foreach (object rosterObject in set)
                        {
                            if (rosterObject is not TroopRoster roster) continue;
                            if (!dryRun) roster.RemoveTroop((CharacterObject)obj);
                            removed = true;
                        }
                    }
                }
                else if (parent is ItemRosterElement[] itemRosterElements)
                {
                    if (_collector.ParentMap.TryGetValue(itemRosterElements, out var set))
                    {
                        foreach (object rosterObject in set)
                        {
                            if (rosterObject is not ItemRoster roster) continue;
                            if (!dryRun) roster.RemoveIf(e => e.EquipmentElement.Item == obj ? e.Amount : 0);
                            removed = true;
                        }
                    }
                }

                break;
        }

        return removed;
    }

    private SaveCleanerAddon RemovableFromAddons(object obj)
    {
        var enabledAddons = addons.WhereQ(a => !a.Disabled).ToListQ();
        return enabledAddons.Any(addon => addon.IsEssential(obj)) ? null : enabledAddons.FirstOrDefaultQ(addon => addon.IsRemovable(obj));
    }

    private void ChangeState(CleanerState state)
    {
        _state = state;
        _detailState = DetailState.None;
        _messageTick = 3;
    }

    private void ForwardState()
    {
        CleanerState state = _state;
        if (state != CleanerState.Complete)
        {
            ChangeState(state + 1);
        }
    }

    private void LogAndMessage(string logMessage, string gameMessage, LogLevel level = LogLevel.Debug, Exception exception = null)
    {
        Color color = level switch
        {
            LogLevel.None => Colors.Black,
            LogLevel.Trace => Colors.Gray,
            LogLevel.Debug => Colors.White,
            LogLevel.Information => Colors.Cyan,
            LogLevel.Warning => Colors.Yellow,
            LogLevel.Error => Colors.Red,
            LogLevel.Critical => Colors.Magenta,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };

        _logger.Log(level, 0, new FormattedLogValues(logMessage), exception, LogFormatter);
        InformationManager.DisplayMessage(new InformationMessage(gameMessage, color));
    }

    private static string LogFormatter(FormattedLogValues state, Exception exception)
    {
        return state.ToString();
    }

    public enum CleanerState
    {
        None,
        BackingUp,
        Collecting,
        Removing,
        Counting,
        Finalizing,
        Complete
    }

    private enum DetailState
    {
        None,
        Starting,
        Started,
        Ended
    }
}