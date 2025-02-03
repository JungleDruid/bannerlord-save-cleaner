using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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
using TaleWorlds.SaveSystem.Definition;
using Debug = TaleWorlds.Library.Debug;

namespace SaveCleaner;

public class Cleaner(CleanerMapView mapView, SaveCleanerOptions options)
{
    private readonly ILogger _logger = LogFactory.Get<Cleaner>();
    private Queue<object> _objectsToIterate;
    private object _rootObject;
    private readonly DefinitionContext _definitionContext = new();
    private readonly List<object> _childObjects = [];
    private readonly Dictionary<object, int> _idsOfChildObjects = new();
    private readonly List<object> _childContainers = [];
    private readonly Dictionary<object, int> _idsOfChildContainers = new();
    private readonly List<object> _temporaryCollectedObjects = [];
    private readonly Dictionary<object, HashSet<object>> _parentMap = new();
    private readonly HashSet<object> _removingObjects = [];
    private static string PlayerClanAndName => $"{Clan.PlayerClan.Name.ToString().ToLower()}_{Hero.MainHero.Name.ToString().ToLower()}";
    private static string BackupSaveName => $"before_cleaning_{PlayerClanAndName}_";
    private static string FinishSaveName => $"after_cleaning_{PlayerClanAndName}_";
    private Stopwatch _stopwatch;
    private string _backUpSave;
    private string _finishSave;
    private Dictionary<Type, int> _beforeCleanTypes;
    private Dictionary<Type, int> _afterCleanTypes;
    private CleanerState _state;
    private DetailState _detailState;
    private int _messageTick;
    private bool _cleaned;

    public bool Completed => _state == CleanerState.Complete && _detailState == DetailState.Ended;

    public Cleaner Start()
    {
        _stopwatch = new Stopwatch();
        _stopwatch.Start();
        CleanConditions.Prepare(options);
        ForwardState();
        mapView.SetActive(true);
        mapView.SetText(new TextObject("Clean Started"));
        InformationManager.DisplayMessage(new InformationMessage("======= Clean started =======", Colors.Yellow));

        return this;
    }

    private void ClearCollections()
    {
        _childObjects.Clear();
        _idsOfChildObjects.Clear();
        _childContainers.Clear();
        _idsOfChildContainers.Clear();
        _temporaryCollectedObjects.Clear();
        _parentMap.Clear();
        _removingObjects.Clear();
    }

    private void Collecting()
    {
        if (!StateGate("Collecting objects...")) return;

        Campaign.Current.TimeControlMode = CampaignTimeControlMode.Stop;
        Campaign.Current.SetTimeControlModeLock(true);
        _definitionContext.FillWithCurrentTypes();

        Campaign.Current.WaitAsyncTasks();
        _logger.LogDebug("Collecting objects...");
        CollectObjects();
        _logger.LogDebug($"Collected {_childObjects.Count} objects.");

        _beforeCleanTypes = GetTypeCollection(_childObjects);
        foreach (var kv in _beforeCleanTypes.OrderByQ(kv => -kv.Value))
        {
            _logger.LogTrace($"Collected [{kv.Key.Name}]: {kv.Value}");
        }

        _logger.LogDebug("Collecting references...");
        foreach (object obj in _childObjects.Where(RequireCleaning))
        {
            CollectReferences(obj);
        }

        _logger.LogDebug($"Collected {_removingObjects.Count} removable objects.");
        if (_removingObjects.Any())
        {
            var collected = GetTypeCollection(_removingObjects);
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
            OnComplete();
        }
    }

    private Dictionary<Type, int> GetTypeCollection(IEnumerable<object> enumerable)
    {
        Dictionary<Type, int> dict = new();
        foreach (object obj in enumerable)
        {
            Type type = obj.GetType();
            if (!dict.TryGetValue(type, out int value)) value = 0;
            dict[type] = value + 1;
        }

        return dict;
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

        ClearCollections();
        _definitionContext.FillWithCurrentTypes();
        Campaign.Current.WaitAsyncTasks();
        CollectObjects();
        _afterCleanTypes = GetTypeCollection(_childObjects);

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

        FinishState();
    }

    private void OnComplete()
    {
        if (_detailState == DetailState.Ended) return;
        Campaign.Current.SetTimeControlModeLock(false);
        if (!_cleaned && _backUpSave is not null)
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
        mapView.SetActive(false);
        FinishState();
    }

    private void OnError()
    {
        Campaign.Current.SetTimeControlModeLock(false);
        ChangeState(CleanerState.Complete);
        _stopwatch.Stop();
        InformationManager.DisplayMessage(new InformationMessage("Clean terminated. See logs for details.", Colors.Red));
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

        if (_parentMap.TryGetValue(obj, out var set))
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
        else
        {
            Debugger.Break();
        }
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
                if (parent is TroopRosterElement[] elements)
                {
                    if (_parentMap.TryGetValue(elements, out var set))
                    {
                        foreach (object rosterObject in set)
                        {
                            if (rosterObject is not TroopRoster roster) continue;
                            roster.RemoveTroop((CharacterObject)obj);
                            removed = true;
                        }
                    }
                    else
                    {
                        Debugger.Break();
                    }
                }
                else
                {
                    Debugger.Break();
                }

                break;
            case ContainerType.Queue:
                Debugger.Break();
                break;
            case ContainerType.None:
            default:
                _logger.LogError("Unable to remove from container type: " + containerType);
                break;
        }

        return removed;
    }

    private void CollectObjects()
    {
        _rootObject = Game.Current;
        _objectsToIterate = new Queue<object>(1024);
        _objectsToIterate.Enqueue(_rootObject);
        while (_objectsToIterate.Count > 0)
        {
            object parent = _objectsToIterate.Dequeue();
            if (parent.GetType().IsContainer(out ContainerType containerType))
                CollectContainerObjects(containerType, parent);
            else
                CollectObjects(parent);
        }
    }

    private bool RequireCleaning(object obj)
    {
        return CleanConditions.IsRemovable(obj);
    }

    private static readonly MethodInfo GetClassDefinitionMethod = typeof(DefinitionContext).Method("GetClassDefinition");
    private static readonly MethodInfo GetStructDefinitionMethod = typeof(DefinitionContext).Method("GetStructDefinition");
    private static readonly MethodInfo GetContainerDefinitionMethod = typeof(DefinitionContext).Method("GetContainerDefinition");

    private TypeDefinition GetClassDefinition(Type type)
    {
        return (TypeDefinition)GetClassDefinitionMethod.Invoke(_definitionContext, [type]);
    }

    private TypeDefinition GetStructDefinition(Type type)
    {
        return (TypeDefinition)GetStructDefinitionMethod.Invoke(_definitionContext, [type]);
    }

    private ContainerDefinition GetContainerDefinition(Type type)
    {
        return (ContainerDefinition)GetContainerDefinitionMethod.Invoke(_definitionContext, [type]);
    }

    private void AddParent(object child, object parent)
    {
        if (!_parentMap.TryGetValue(child, out var parents))
        {
            parents = new HashSet<object>();
            _parentMap.Add(child, parents);
        }

        parents.Add(parent);
    }

    private void CollectObjects(object parent)
    {
        if (_idsOfChildObjects.ContainsKey(parent))
            return;
        int count = _childObjects.Count;
        _childObjects.Add(parent);
        _idsOfChildObjects.Add(parent, count);
        Type type = parent.GetType();

        TypeDefinition classDefinition = GetClassDefinition(type);
        if (classDefinition is null)
        {
            _logger.LogWarning("Could not find type definition of type: " + type);
            return;
        }

        GetChildObjects(classDefinition, parent,
            _temporaryCollectedObjects);
        for (int index = 0; index < _temporaryCollectedObjects.Count; ++index)
        {
            object temporaryCollectedObject = _temporaryCollectedObjects[index];
            if (temporaryCollectedObject == null) continue;
            AddParent(temporaryCollectedObject, parent);
            _objectsToIterate.Enqueue(temporaryCollectedObject);
        }

        _temporaryCollectedObjects.Clear();
    }

    private void GetChildObjects(TypeDefinition typeDefinition, object target, List<object> collectedObjects)
    {
        if (typeDefinition.CollectObjectsMethod != null)
        {
            typeDefinition.CollectObjectsMethod(target, collectedObjects);
        }
        else
        {
            foreach (MemberDefinition memberDefinition in typeDefinition.MemberDefinitions)
            {
                GetChildObjectFrom(target, memberDefinition, collectedObjects);
            }
        }
    }

    private void GetChildObjectFrom(object target, MemberDefinition memberDefinition, List<object> collectedObjects)
    {
        Type memberType = memberDefinition.GetMemberType();
        if (memberType.IsClass || memberType.IsInterface)
        {
            if (!(memberType != typeof(string)))
                return;
            object obj = memberDefinition.GetValue(target);
            if (obj == null)
                return;
            collectedObjects.Add(obj);
        }
        else
        {
            TypeDefinition structDefinition = GetStructDefinition(memberType);
            if (structDefinition == null)
                return;
            object target1 = memberDefinition.GetValue(target);
            foreach (MemberDefinition memberDefinition1 in structDefinition.MemberDefinitions)
            {
                GetChildObjectFrom(target1, memberDefinition1, collectedObjects);
            }
        }
    }

    private void CollectContainerObjects(ContainerType containerType, object parent)
    {
        if (_idsOfChildContainers.ContainsKey(parent))
            return;
        int count = _childContainers.Count;
        _childContainers.Add(parent);
        _idsOfChildContainers.Add(parent, count);
        Type type = parent.GetType();
        ContainerDefinition containerDefinition = GetContainerDefinition(type);
        if (containerDefinition == null)
        {
            string message = "Cant find definition for " + type.FullName;
            Debug.Print(message, color: Debug.DebugColor.Red);
        }

        GetChildObjects(containerDefinition, containerType, parent, _temporaryCollectedObjects);
        for (int index = 0; index < _temporaryCollectedObjects.Count; ++index)
        {
            object temporaryCollectedObject = _temporaryCollectedObjects[index];
            if (temporaryCollectedObject == null) continue;
            AddParent(temporaryCollectedObject, parent);
            _objectsToIterate.Enqueue(temporaryCollectedObject);
        }

        _temporaryCollectedObjects.Clear();
    }

    private void GetChildObjects(ContainerDefinition containerDefinition, ContainerType containerType, object target, List<object> collectedObjects)
    {
        if (containerDefinition.CollectObjectsMethod != null)
        {
            if (containerDefinition.HasNoChildObject)
                return;
            containerDefinition.CollectObjectsMethod(target, collectedObjects);
        }
        else
        {
            switch (containerType)
            {
                case ContainerType.List:
                case ContainerType.CustomList:
                case ContainerType.CustomReadOnlyList:
                    var list = (IList)target;
                    foreach (object childElement in list)
                    {
                        if (childElement != null)
                            ProcessChildObjectElement(childElement, collectedObjects);
                    }

                    break;
                case ContainerType.Dictionary:
                    IDictionaryEnumerator enumerator1 = ((IDictionary)target).GetEnumerator();
                    try
                    {
                        while (enumerator1.MoveNext())
                        {
                            var current = (DictionaryEntry)enumerator1.Current!;
                            ProcessChildObjectElement(current.Key, collectedObjects);
                            object childElement = current.Value;
                            if (childElement != null)
                                ProcessChildObjectElement(childElement, collectedObjects);
                        }

                        break;
                    }
                    finally
                    {
                        if (enumerator1 is IDisposable disposable)
                            disposable.Dispose();
                    }
                case ContainerType.Array:
                    var array = (Array)target;
                    for (int index = 0; index < array.Length; ++index)
                    {
                        object childElement = array.GetValue(index);
                        if (childElement != null)
                            ProcessChildObjectElement(childElement, collectedObjects);
                    }

                    break;
                case ContainerType.Queue:
                    IEnumerator enumerator2 = ((IEnumerable)target).GetEnumerator();
                    try
                    {
                        while (enumerator2.MoveNext())
                        {
                            object current = enumerator2.Current;
                            if (current != null)
                                ProcessChildObjectElement(current, collectedObjects);
                        }

                        break;
                    }
                    finally
                    {
                        if (enumerator2 is IDisposable disposable)
                            disposable.Dispose();
                    }
                case ContainerType.None:
                default:
                    using (var enumerator3 = GetChildElements(containerType, target).GetEnumerator())
                    {
                        while (enumerator3.MoveNext())
                            ProcessChildObjectElement(enumerator3.Current, collectedObjects);
                        break;
                    }
            }
        }
    }

    public static IEnumerable<object> GetChildElements(ContainerType containerType, object target)
    {
        int i;
        if (containerType == ContainerType.List || containerType == ContainerType.CustomList || containerType == ContainerType.CustomReadOnlyList)
        {
            var list = (IList)target;
            for (i = 0; i < list.Count; ++i)
            {
                object childElement = list[i];
                if (childElement != null)
                    yield return childElement;
            }
        }
        else
        {
            switch (containerType)
            {
                case ContainerType.Dictionary:
                    foreach (DictionaryEntry dictionaryEntry in (IDictionary)target)
                    {
                        DictionaryEntry entry = dictionaryEntry;
                        yield return entry.Key;
                        object childElement = entry.Value;
                        if (childElement != null)
                            yield return childElement;
                    }

                    break;
                case ContainerType.Array:
                    var array = (Array)target;
                    for (i = 0; i < array.Length; ++i)
                    {
                        object childElement = array.GetValue(i);
                        if (childElement != null)
                            yield return childElement;
                    }

                    break;
                case ContainerType.Queue:
                    foreach (object childElement in (IEnumerable)target)
                    {
                        if (childElement != null)
                            yield return childElement;
                    }

                    break;
            }
        }
    }

    private void ProcessChildObjectElement(object childElement, List<object> collectedObjects)
    {
        Type type = childElement.GetType();
        bool isClass = type.IsClass;
        if (isClass && type != typeof(string))
        {
            collectedObjects.Add(childElement);
        }
        else
        {
            if (isClass)
                return;
            TypeDefinition structDefinition = GetStructDefinition(type);
            if (structDefinition == null)
                return;
            if (structDefinition.CollectObjectsMethod != null)
            {
                structDefinition.CollectObjectsMethod(childElement, collectedObjects);
            }
            else
            {
                foreach (MemberDefinition memberDefinition in structDefinition.MemberDefinitions)
                {
                    GetChildObjectFrom(childElement, memberDefinition, collectedObjects);
                }
            }
        }
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