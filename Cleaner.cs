using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Bannerlord.ButterLib.Logger.Extensions;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using SaveCleaner.UI;
using SaveCleaner.Utils;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.LinQuick;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace SaveCleaner;

internal class Cleaner(CleanerMapView mapView, IReadOnlyList<SaveCleanerAddon> addons, SaveCleanerAddon wiping = null)
{
    private readonly ILogger _logger = LogFactory.Get<Cleaner>();
    private static string PlayerClanAndName => $"{Clan.PlayerClan.Name.ToString().ToLower()}_{Hero.MainHero.Name.ToString().ToLower()}";
    private string ActionName => wiping == null ? "cleaning" : $"wiping_{wiping.Name}";
    private string BackupSaveName => $"before_{ActionName}_{PlayerClanAndName}_";
    private string FinishSaveName => $"after_{ActionName}_{PlayerClanAndName}_";
    private readonly string _tempSaveName = $"cleaner_temp_{PlayerClanAndName}";
    private Stopwatch _stopwatch;
    private string _backUpSave;
    private string _finishSave;
    private Dictionary<Type, int> _beforeCleanTypes;
    private CleanerState _state;
    private DetailState _detailState;
    private int _messageTick;
    private bool _cleaned;
    private readonly Collector _collector = new();
    private readonly Collector _postSaveCollector = new();
    private readonly Dictionary<object, SaveCleanerAddon> _removableObjects = [];
    private readonly Dictionary<object, object> _blockedWithCompatibilityMode = [];
    private bool _isFastCollector;
    private readonly List<SaveCleanerAddon> _enabledAddons = addons.WhereQ(a => !a.Disabled).ToListQ();
    private readonly List<Tuple<Regex, SaveCleanerAddon>> _domainHandlers = [];
    private readonly Dictionary<Node, SaveCleanerAddon> _removalHandlers = [];
    private readonly HashSet<object> _failedRemovals = [];
    private bool _isCreatingTempSave;

    public bool Completed => _state == CleanerState.Complete && _detailState == DetailState.Ended;

    public Cleaner Start()
    {
        if (_state != CleanerState.None)
        {
            _logger.LogError("The cleaner is already running!");
            OnError();
            return this;
        }

        if (_enabledAddons.Count == 0)
        {
            _logger.LogError("No addon enabled!");
            OnError();
            return this;
        }

        _stopwatch = new Stopwatch();
        _stopwatch.Start();

        mapView.SetActive(true);
        mapView.SetText(new TextObject("{=SVCLRCleanStarted}Clean Started"));
        string line = new('=', 10);
        LogAndMessage($"{line} Start Cleaning {line}",
            $"{line} {new TextObject("{=SVCLRCleanStarted}Clean Started")} {line}",
            LogLevel.Information);

        if (SubModule.Instance.IsFastCollector)
        {
            _isFastCollector = true;
            LogAndMessage("Fast Collection Mode is ON",
                new TextObject("{=SVCLRFastCollectionMode}Fast Collection Mode is ON").ToString(),
                LogLevel.Information);
        }

        foreach (SaveCleanerAddon addon in _enabledAddons)
        {
            foreach (Regex regex in addon.SupportedNamespaceRegexes)
            {
                _domainHandlers.Add(new Tuple<Regex, SaveCleanerAddon>(regex, addon));
            }
        }

        ForwardState();
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

    internal void AddRelationToCollector(object child, object parent)
    {
        if (_isFastCollector != true) return;

        switch (_state)
        {
            case CleanerState.BackingUp:
                if (wiping is null || _isCreatingTempSave)
                    _collector.AddParent(child, parent);
                break;
            case CleanerState.Finalizing:
                _postSaveCollector.AddParent(child, parent);
                break;
        }
    }

    internal void SendChildObjectsToCollector(List<object> objects)
    {
        if (_isFastCollector != true) return;

        switch (_state)
        {
            case CleanerState.BackingUp:
                if (wiping is null || _isCreatingTempSave)
                    _collector.ChildObjects = objects;
                break;
            case CleanerState.Finalizing:
                _postSaveCollector.ChildObjects = objects;
                break;
        }
    }

    private bool QueryRemovable(object obj, Dictionary<Node, SaveCleanerAddon> newHandlers, Dictionary<object, SaveCleanerAddon> newRemovables, HashSet<object> visited)
    {
        if (!visited.Add(obj)) return true;
        if (_removalHandlers.AnyQ(tuple => tuple.Key.Value == obj)) return true;
        if (_failedRemovals.Contains(obj)) return false;

        bool failed = false;
        if (_collector.ParentMap.TryGetValue(obj, out var set))
        {
            foreach (object parent in set)
            {
                string ns = GetNamespace(parent);

                Node node = new(obj) { Parent = new Node(parent) };

                while (ns == "System" || ns.StartsWith("System."))
                {
                    if (_collector.ParentMap.TryGetValue(node.Top.Value, out var grandparents))
                    {
                        if (grandparents.Count > 1)
                        {
                            object issue = node.Top.Value;
                            _logger.LogWarning($"Multiple grandparents found with [{issue.GetType().Name}]{issue}. Parents: {grandparents.SelectQ(p => $"[{p.GetType().Name}]{p}").Join()}");
                            failed = true;
                            break;
                        }

                        object grandparent = grandparents.First();
                        node.Top.Parent = new Node(grandparent);
                        ns = GetNamespace(grandparent);
                    }
                    else
                    {
                        break;
                    }
                }

                if (failed) break;

                var matches = _domainHandlers.Where(tuple => tuple.Item1.IsMatch(ns)).ToListQ();
                if (matches.Any())
                {
                    foreach (var tuple in from tuple in matches let result = tuple.Item2.InvokeCanRemoveChild(node) where result select tuple)
                    {
                        newHandlers.Add(node, tuple.Item2);
                        break;
                    }
                }
                else if (!GlobalOptions.CompatibilityMode)
                {
                    if (addons[0].InvokeCanRemoveChild(node))
                    {
                        newHandlers.Add(node, addons[0]);
                    }
                    else
                    {
                        failed = true;
                        break;
                    }
                }
                else
                {
                    _blockedWithCompatibilityMode.Add(obj, node.Top.Value);
                    failed = true;
                    break;
                }
            }
        }
        else
        {
            SafeDebugger.Break();
            failed = true;
        }

        if (!failed)
        {
            foreach (SaveCleanerAddon addon in _enabledAddons)
            {
                foreach (object dependency in addon.GetDependencies(obj))
                {
                    if (QueryRemovable(dependency, newHandlers, newRemovables, visited))
                    {
                        if (!_removableObjects.ContainsKey(dependency) && !newRemovables.ContainsKey(dependency))
                            newRemovables.Add(dependency, addon);
                    }
                    else
                    {
                        failed = true;
                        break;
                    }
                }

                if (failed) break;
            }
        }

        if (failed)
        {
            _failedRemovals.Add(obj);
        }

        return !failed;
    }

    private readonly TextObject _collectingMessage = new("{=SVCLRStatusCollecting}Collecting objects...");

    private void Collecting()
    {
        if (!StateGate(_collectingMessage)) return;

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
            var childObjects = _isFastCollector ? _collector.ChildObjects : _collector.CollectObjects();
            _logger.LogDebug($"Collected {childObjects.Count} objects.");

            _beforeCleanTypes = Collector.GetTypeCollection(childObjects);
            foreach (var kv in _beforeCleanTypes.OrderByQ(kv => -kv.Value))
            {
                _logger.LogTrace($"Collected [{kv.Key.Name}]: {kv.Value}");
            }

            _logger.LogDebug("Collecting references...");
            Queue<object> removableQueries = [];
            foreach (object obj in childObjects)
            {
                SaveCleanerAddon addon = RemovableFromAddons(obj);
                if (addon is null) continue;
                _removableObjects.Add(obj, addon);
                removableQueries.Enqueue(obj);
            }

            Dictionary<Node, SaveCleanerAddon> newHandlers = [];
            Dictionary<object, SaveCleanerAddon> newRemovables = [];
            HashSet<object> visited = [];

            while (removableQueries.Count > 0)
            {
                object obj = removableQueries.Dequeue();
                if (QueryRemovable(obj, newHandlers, newRemovables, visited))
                {
                    newHandlers.Do(kv => _removalHandlers.Add(kv.Key, kv.Value));
                    newRemovables.Do(kv => _removableObjects.Add(kv.Key, kv.Value));
                }
                else
                {
                    RemoveDependenciesFromRemovables(obj);
                    _removableObjects.Remove(obj);
                    _failedRemovals.Add(obj);
                }

                newHandlers.Clear();
                newRemovables.Clear();
                visited.Clear();
            }

            bool failed;
            do
            {
                failed = false;
                foreach (object obj in _removableObjects.Keys.ToListQ().Where(obj => _enabledAddons.AnyQ(addon => !addon.GetDependencies(obj).AllQ(_removableObjects.ContainsKey))))
                {
                    failed = true;
                    _removableObjects.Remove(obj);
                }
            } while (failed);

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

                if (GlobalOptions.CompatibilityMode)
                {
                    LogAndMessage($"{_blockedWithCompatibilityMode.Count} of removable objects were blocked with compatibility mode.",
                        new TextObject("{=SVCLRCompatibilityBlockCount}{NUMBER} of removable objects were blocked with compability mode.",
                            new Dictionary<string, object> { ["NUMBER"] = _blockedWithCompatibilityMode.Count }).ToString(),
                        LogLevel.Information);
                    foreach (var groupByAssembly in _blockedWithCompatibilityMode.GroupBy(kv => kv.Value.GetType().Assembly, kv => kv))
                    {
                        string dll = groupByAssembly.Key.Modules.First().Name;
                        foreach (var groupByChildType in groupByAssembly.GroupBy(kv => kv.Key.GetType()))
                        {
                            string message = $"[{dll}] has prevented {groupByChildType.Count()} [{groupByChildType.Key.Name}] from removal with compatibility mode.";
                            LogAndMessage(message, message, LogLevel.Warning);
                            foreach (var groupByParentType in groupByChildType.GroupBy(kv => kv.Value.GetType()))
                            {
                                _logger.LogWarning($"[{groupByParentType.Key.Name}] is holding {groupByParentType.CountQ()} [{groupByChildType.Key.Name}]");
                            }
                        }
                    }
                }

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

    private void RemoveDependenciesFromRemovables(object obj)
    {
        if (!_removableObjects.ContainsKey(obj)) return;
        _removableObjects.Remove(obj);
        _enabledAddons.SelectMany(addon => addon.GetDependencies(obj)).Do(RemoveDependenciesFromRemovables);
    }

    private string GetNamespace(object parent)
    {
        Type type = parent.GetType();
        string ns = type.Namespace;
        if (!string.IsNullOrEmpty(ns)) return ns;

        ns = type.Assembly.Modules.First().Name;
        _logger.LogDebug($"[{type.Name}]{parent} doesn't have a namespace, using dll {ns} as fallback.");
        return ns;
    }

    private bool StateGate(TextObject startMessage)
    {
        switch (_detailState)
        {
            case DetailState.None:
                LogAndMessage(startMessage.Value.Substring(startMessage.Value.IndexOf('}') + 1), startMessage.ToString());
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

    private readonly TextObject _backupMessage = new("{=SVCLRStatusBackup}Creating backup save...");
    private readonly TextObject _tempSaveMessage = new("{=SVCLRStatusBackupTemp}Creating temporary save for data collection...");

    private void BackingUp()
    {
        if (Campaign.Current.SaveHandler.IsSaving) return;
        if (!StateGate(_isCreatingTempSave ? _tempSaveMessage : _backupMessage)) return;

        _backUpSave = GetAvailableSaveName(BackupSaveName);
        SubModule.Instance.SaveEventReceiver.SaveOver += OnSaveOver;
        Campaign.Current.SaveHandler.SaveAs(_backUpSave);
    }

    private readonly TextObject _finalizingMessage = new("{=SVCLRStatusFinalizing}Saving game...");

    private void Finalizing()
    {
        if (Campaign.Current.SaveHandler.IsSaving) return;
        if (!StateGate(_finalizingMessage)) return;

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

    private readonly TextObject _countingMessage = new("{=SVCLRStatusCounting}Counting results...");

    private void Counting()
    {
        if (!StateGate(_countingMessage)) return;

        Campaign.Current.WaitAsyncTasks();
        var childObjects = _isFastCollector ? _postSaveCollector.ChildObjects : new Collector().CollectObjects();
        var afterCleanTypes = Collector.GetTypeCollection(childObjects);

        Dictionary<Type, int> result = new();
        foreach (var kv in afterCleanTypes)
        {
            if (!_beforeCleanTypes.TryGetValue(kv.Key, out int before)) before = 0;
            if (before != kv.Value)
            {
                result.Add(kv.Key, before - kv.Value);
            }
        }

        if (result.Count > 0)
        {
            LogAndMessage("Clean results:",
                new TextObject("{=SVCLRCleanResults}Clean results:").ToString(),
                LogLevel.Information);
            foreach (var kv in result.OrderByQ(kv => -kv.Value))
            {
                string logMessage = $"[{kv.Key.Name}]: {kv.Value}";
                LogAndMessage(logMessage, logMessage, LogLevel.Information);
            }
        }

        if (_isFastCollector)
        {
            ChangeState(CleanerState.Complete);
        }
        else
        {
            FinishState();
        }
    }

    private readonly TextObject _removingMessage = new("{=SVCLRStatusRemoving}Removing objects...");

    private void Removing()
    {
        if (!StateGate(_removingMessage)) return;

        var failures = _removalHandlers.WhereQ(kv => !kv.Value.InvokeDoRemoveChild(kv.Key)).ToListQ();
        if (failures.Count > 0)
        {
            foreach (var kv in failures)
            {
                object obj = kv.Key.Value;
                object parent = kv.Key.Top.Value;
                _logger.LogError($"Handler {kv.Value} failed to remove object [{obj.GetType()}]{obj} from [{parent.GetType()}]{parent}");
            }

            OnError();
            return;
        }

        _cleaned = true;

        if (_isFastCollector)
        {
            ChangeState(CleanerState.Finalizing);
        }
        else
        {
            FinishState();
        }
    }

    private void FinishState()
    {
        _detailState = DetailState.Ended;
    }

    private void OnSaveOver(bool isSuccessful, string saveName)
    {
        if (saveName != _backUpSave && saveName != _finishSave && saveName != _tempSaveName) return;
        SubModule.Instance.SaveEventReceiver.SaveOver -= OnSaveOver;

        if (!isSuccessful)
        {
            string message = $"Failed to {(_state == CleanerState.BackingUp ? "backup before" : "save after ")} cleaning.";
            LogAndMessage(message, message, LogLevel.Error);
            OnError();
            return;
        }

        if (_state == CleanerState.BackingUp && !_isCreatingTempSave && wiping?.Wipe() == false)
        {
            LogAndMessage("Wipe failed!", new TextObject("{=SVCLRWipeFailed}Wipe failed!").ToString(), LogLevel.Error);
            OnError();
            return;
        }

        if (_isFastCollector)
        {
            switch (_state)
            {
                case CleanerState.BackingUp:
                    if (wiping is not null && !_isCreatingTempSave)
                    {
                        _isCreatingTempSave = true;
                        ChangeState(CleanerState.BackingUp);
                    }
                    else
                    {
                        if (_isCreatingTempSave)
                        {
                            MBSaveLoad.DeleteSaveGame(_tempSaveName);
                        }

                        ChangeState(CleanerState.Collecting);
                    }

                    break;
                case CleanerState.Finalizing:
                    ChangeState(_cleaned ? CleanerState.Counting : CleanerState.Complete);
                    break;
                default:
                    throw new InvalidOperationException($"Invalid state {_state}");
            }
        }
        else
        {
            FinishState();
        }
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

    private SaveCleanerAddon RemovableFromAddons(object obj)
    {
        return _enabledAddons.Any(addon => addon.IsEssential(obj)) ? null : _enabledAddons.FirstOrDefaultQ(addon => addon.IsRemovable(obj));
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

        _logger.Log(level, logMessage, exception);

        InformationManager.DisplayMessage(new InformationMessage(gameMessage, color));
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