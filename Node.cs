using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.LinQuick;

namespace SaveCleaner;

public class Node(object value)
{
    public object Value { get; } = value;
    private Node _parent;
    private Node _child;

    public Node Parent
    {
        get => _parent;
        set
        {
            if (_parent == value) return;
            _parent = value;
            value._child = this;
        }
    }

    public Node Child
    {
        get => _child;
        set
        {
            if (_child == value) return;
            _child = value;
            value._parent = this;
        }
    }

    public Node Top => Parent?.Top ?? this;
    public Node Bottom => Child?.Bottom ?? this;

    public string GetLinkString()
    {
        string result = "";
        Node current = Top;

        while (current is not null)
        {
            if (result != "") result += "-> ";
            object currentValue = current.Value;
            Type type = currentValue.GetType();
            result += $"[{type.Name}] ";
            object parentValue = current.Parent?.Value;
            if (parentValue != null)
            {
                Type parentType = parentValue.GetType();
                result += parentType
                    .GetRuntimeFields()
                    .WhereQ(f => f.FieldType == type)
                    .SelectQ(f => (f.GetValue(parentValue) == currentValue ? $"({f.Name})" : f.Name) + " ")
                    .Join(null, "& ");
            }

            current = current._child;
        }

        return result;
    }

    private string GetStringOfFields(object parent, object child, Type parentType = null)
    {
        parentType ??= parent.GetType();
        Type childType = child.GetType();
        string result = parentType
            .GetRuntimeFields()
            .WhereQ(f => f.FieldType == childType)
            .SelectQ(f => (f.GetValue(parent) == child ? $"({f.Name})" : f.Name) + " ")
            .Join(null, "& ");

        if (parentType.BaseType != null) result += GetStringOfFields(parent, child, parentType.BaseType);

        return result;
    }
}