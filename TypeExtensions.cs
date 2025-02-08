using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;

namespace SaveCleaner;

public static class TypeExtensions
{
    public static bool IsContainer(this Type type) => type.IsContainer(out ContainerType _);

    public static bool IsContainer(this Type type, out ContainerType containerType)
    {
        containerType = ContainerType.None;
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            Type genericTypeDefinition = type.GetGenericTypeDefinition();
            if (genericTypeDefinition == typeof(Dictionary<,>))
            {
                containerType = ContainerType.Dictionary;
                return true;
            }

            if (genericTypeDefinition == typeof(List<>))
            {
                containerType = ContainerType.List;
                return true;
            }

            if (genericTypeDefinition == typeof(MBList<>))
            {
                containerType = ContainerType.CustomList;
                return true;
            }

            if (genericTypeDefinition == typeof(MBReadOnlyList<>))
            {
                containerType = ContainerType.CustomReadOnlyList;
                return true;
            }

            if (genericTypeDefinition == typeof(Queue<>))
            {
                containerType = ContainerType.Queue;
                return true;
            }
        }
        else if (type.IsArray)
        {
            containerType = ContainerType.Array;
            return true;
        }

        return false;
    }

    public static IEnumerable<FieldInfo> GetAllFields(this Type type, bool includeStatic = false)
    {
        BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        if (includeStatic) bindingFlags |= BindingFlags.Static;

        foreach (FieldInfo field in type.GetFields(bindingFlags))
        {
            yield return field;
        }

        if (type.BaseType == null) yield break;

        foreach (FieldInfo field in GetAllFields(type.BaseType)) yield return field;
    }
}