using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Bannerlord.ButterLib.Logger.Extensions;
using HarmonyLib;
using Microsoft.Extensions.Logging;
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
    private readonly HashSet<object> _removingObjects = [];

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
        mapView.SetText(new TextObject("Clean Started"));
        InformationManager.DisplayMessage(new InformationMessage("======= Clean started =======", Colors.Yellow));

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
        if (!StateGate("Collecting objects...")) return;

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
            foreach (object obj in childObjects.Where(RequireCleaning))
            {
                CollectReferences(obj);
            }

            _logger.LogDebug($"Collected {_removingObjects.Count} removable objects.");
            if (_removingObjects.Any())
            {
                var collected = Collector.GetTypeCollection(_removingObjects);
                foreach (var kv in collected.OrderByQ(kv => -kv.Value))
                {
                    _logger.LogTrace($"Collected Removable [{kv.Key.Name}]: {kv.Value}");
                }

                InformationManager.DisplayMessage(new InformationMessage($"Collected {_removingObjects.Count} removable objects, cleaning up...", Colors.Cyan));
                FinishState();
            }
            else
            {
                InformationManager.DisplayMessage(new InformationMessage("Nothing to clean.", Colors.Cyan));
                if (wiping is null) OnComplete();
                else ChangeState(CleanerState.Finalizing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while collecting objects.");
            OnError();
        }
    }

    private bool StateGate(string startMessage)
    {
        switch (_detailState)
        {
            case DetailState.None:
                InformationManager.DisplayMessage(new InformationMessage(startMessage, Colors.Cyan));
                mapView.SetText(new TextObject(startMessage));
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
        if (!StateGate("Creating backup save...")) return;

        _backUpSave = GetAvailableSaveName(BackupSaveName);
        SubModule.Instance.SaveEventReceiver.SaveOver += OnSaveOver;
        Campaign.Current.SaveHandler.SaveAs(_backUpSave);
    }

    private void Finalizing()
    {
        if (Campaign.Current.SaveHandler.IsSaving) return;
        if (!StateGate("Saving game...")) return;

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
        if (!StateGate("Counting results...")) return;

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

        string message = "Clean results:";
        _logger.LogInformation(message);
        InformationManager.DisplayMessage(new InformationMessage(message, Colors.Cyan));
        foreach (var kv in result.OrderByQ(kv => -kv.Value))
        {
            message = $"[{kv.Key.Name}]: {kv.Value}";
            _logger.LogInformation(message);
            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Cyan));
        }

        FinishState();
    }

    private void Removing()
    {
        if (!StateGate("Removing objects...")) return;

        foreach (object obj in _removingObjects)
        {
            _logger.LogTrace($"Removing {obj.GetType()}: {obj}");
            CollectReferences(obj, true);
        }

        InformationManager.DisplayMessage(new InformationMessage($"Cleaned {_removingObjects.Count} objects", Colors.Cyan));
        _cleaned = true;
        FinishState();
    }

    private void FinishState()
    {
        _detailState = DetailState.Ended;
    }

    private bool SafeToRemove(object obj)
    {
        if (_removingObjects.Contains(obj)) return true;
        if (obj.GetType().IsContainer()) return false;
        switch (obj)
        {
            case LogEntry:
            case CharacterObject { HeroObject: not null } characterObject when _removingObjects.Contains(characterObject.HeroObject):
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
            InformationManager.DisplayMessage(new InformationMessage(message, Colors.Red));
            _logger.LogError(message);
            OnError();
            return;
        }

        if (_state == CleanerState.BackingUp && wiping?.Wipe() == false)
        {
            InformationManager.DisplayMessage(new InformationMessage("Wipe failed!", Colors.Red));
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
                InformationManager.DisplayMessage(new InformationMessage("Removing backup save...", Colors.Cyan));
                MBSaveLoad.DeleteSaveGame(_backUpSave);
            }
        }

        ChangeState(CleanerState.Complete);
        _stopwatch.Stop();
        InformationManager.DisplayMessage(new InformationMessage($"Clean ended. Took {_stopwatch.ElapsedMilliseconds}ms to finish.", Colors.Yellow));
        if (_finishSave is not null)
        {
            InformationManager.DisplayMessage(new InformationMessage($"Please load the save before continue playing: {_finishSave}", Colors.Yellow));
        }

        mapView.SetActive(false);
        FinishState();
    }

    private void OnError()
    {
        Campaign.Current.SetTimeControlModeLock(false);
        ChangeState(CleanerState.Complete);
        _stopwatch.Stop();
        _logger.LogError("Clean terminated dur to errors.");
        InformationManager.DisplayMessage(new InformationMessage("Clean terminated. See logs for details.", Colors.Red));

        if (_backUpSave is not null)
        {
            InformationManager.DisplayMessage(new InformationMessage("Reloading the backup save is recommended: " + _backUpSave, Colors.Red));
        }

        mapView.SetActive(false);
        FinishState();
    }

    private void CollectReferences(object obj, bool remove = false)
    {
        if (!remove)
        {
            if (!_removingObjects.Add(obj)) return;
            if (obj.GetType().IsContainer()) return;
            _logger.LogTrace($"Collecting references of [{obj.GetType()}]{obj}...");
        }
        else
        {
            _logger.LogTrace($"Removing references of [{obj.GetType()}]{obj}...");
            if (obj is MBObjectBase mbObject)
            {
                MBObjectManager.Instance.UnregisterObject(mbObject);
            }
        }

        if (_collector.ParentMap.TryGetValue(obj, out var set))
        {
            foreach (object parent in set)
            {
                if (remove)
                {
                    if (_removingObjects.Contains(parent)) continue;
                    if (!RemoveFromParent(obj, parent))
                    {
                        _logger.LogWarning($"Failed to remove [{obj.GetType()}]{obj} from [{parent.GetType()}]{parent}");
                    }
                }
                else
                {
                    if (SafeToRemove(parent)) CollectReferences(parent);
                }
            }
        }
#if DEBUG
        else
        {
            Debugger.Break();
        }
#endif
    }

    private bool RemoveFromParent(object obj, object parent)
    {
        _logger.LogTrace($"Removing [{obj.GetType()}]{obj} from [{parent.GetType()}]{parent}");
        bool removed = false;
        if (parent.GetType().IsContainer(out ContainerType containerType))
        {
            return RemoveFromContainer(obj, parent, containerType);
        }

        foreach (var field in parent.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(parent) != obj) continue;
            field.SetValue(parent, null);
            removed = true;
        }

        return removed;
    }

    private bool RemoveFromContainer(object obj, object parent, ContainerType containerType)
    {
        bool removed = false;
        switch (containerType)
        {
            case ContainerType.CustomList:
            case ContainerType.CustomReadOnlyList:
            case ContainerType.List:
            case ContainerType.Dictionary:
                parent.GetType().GetMethod("Remove")!.Invoke(parent, [obj]);
                removed = true;
                break;
            case ContainerType.Array:
                if (parent is TroopRosterElement[] troopRosterElements)
                {
                    if (_collector.ParentMap.TryGetValue(troopRosterElements, out var set))
                    {
                        foreach (object rosterObject in set)
                        {
                            if (rosterObject is not TroopRoster roster) continue;
                            roster.RemoveTroop((CharacterObject)obj);
                            removed = true;
                        }
                    }
#if DEBUG
                    else
                    {
                        Debugger.Break();
                    }
#endif
                }
                else if (parent is ItemRosterElement[] itemRosterElements)
                {
                    if (_collector.ParentMap.TryGetValue(itemRosterElements, out var set))
                    {
                        foreach (object rosterObject in set)
                        {
                            if (rosterObject is not ItemRoster roster) continue;
                            roster.RemoveIf(e => e.EquipmentElement.Item == obj ? e.Amount : 0);
                            removed = true;
                        }
                    }
                }
                else if (parent is EquipmentElement[])
                {
                }
#if DEBUG
                else
                {
                    Debugger.Break();
                }
#endif

                break;
            case ContainerType.Queue:
#if DEBUG
                Debugger.Break();
#endif
                break;
            case ContainerType.None:
            default:
                _logger.LogError("Unable to remove from container type: " + containerType);
                break;
        }

        return removed;
    }

    private bool RequireCleaning(object obj)
    {
        return !addons.WhereQ(a => !a.Disabled).Any(addon => addon.IsEssential(obj)) && addons.Any(addon => addon.IsRemovable(obj));
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