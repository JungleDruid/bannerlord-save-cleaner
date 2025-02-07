using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bannerlord.ButterLib.Logger.Extensions;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;
using TaleWorlds.SaveSystem.Definition;

namespace SaveCleaner;

/// <summary>
/// ASSUMING DIRECT CONTROL
/// </summary>
internal class Collector
{
    private readonly ILogger _logger = LogFactory.Get<Collector>();

    private Queue<object> _objectsToIterate;
    private object _rootObject;
    private readonly DefinitionContext _definitionContext = new();
    private readonly Dictionary<object, int> _idsOfChildObjects = new();
    private readonly List<object> _childContainers = [];
    private readonly Dictionary<object, int> _idsOfChildContainers = new();
    private readonly List<object> _temporaryCollectedObjects = [];
    private readonly Dictionary<object, HashSet<object>> _parentMap = new();
    private readonly Dictionary<object, HashSet<object>> _childMap = new();

    private bool _collected;

    internal List<object> ChildObjects { get; set; } = [];
    public IReadOnlyDictionary<object, HashSet<object>> ParentMap => _parentMap;
    public IReadOnlyDictionary<object, HashSet<object>> ChildMap => _childMap;

    private static readonly MethodInfo GetClassDefinitionMethod = AccessTools.Method(typeof(DefinitionContext), "GetClassDefinition");
    private static readonly MethodInfo GetStructDefinitionMethod = AccessTools.Method(typeof(DefinitionContext), "GetStructDefinition");
    private static readonly MethodInfo GetContainerDefinitionMethod = AccessTools.Method(typeof(DefinitionContext), "GetContainerDefinition");

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

    internal void AddParent(object child, object parent)
    {
        if (_parentMap.TryGetValue(child, out var parents))
            parents.Add(parent);
        else
            _parentMap.Add(child, [parent]);

        if (_childMap.TryGetValue(parent, out var children))
            children.Add(child);
        else
            _childMap.Add(parent, [child]);
    }


    public IReadOnlyList<object> CollectObjects()
    {
        if (_collected)
        {
            _logger.LogErrorAndDisplay("CollectObjects called more than once");
            return ChildObjects;
        }

        _definitionContext.FillWithCurrentTypes();

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

        _collected = true;
        return ChildObjects;
    }

    private void CollectObjects(object parent)
    {
        if (_idsOfChildObjects.ContainsKey(parent))
            return;
        int count = ChildObjects.Count;
        ChildObjects.Add(parent);
        _idsOfChildObjects.Add(parent, count);
        Type type = parent.GetType();

        TypeDefinition classDefinition = GetClassDefinition(type);
        if (classDefinition is null)
        {
            _logger.LogWarning("Could not find type definition of type: " + type);
            return;
        }

        GetChildObjects(classDefinition, parent, _temporaryCollectedObjects);
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

    private static IEnumerable<object> GetChildElements(ContainerType containerType, object target)
    {
        int i;
        if (containerType is ContainerType.List or ContainerType.CustomList or ContainerType.CustomReadOnlyList)
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

    public static Dictionary<Type, int> GetTypeCollection(IEnumerable<object> enumerable)
    {
        return enumerable.GroupBy(obj => obj.GetType()).ToDictionary(group => group.Key, group => group.Count());
    }
}