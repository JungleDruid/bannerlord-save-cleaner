using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TaleWorlds.MountAndBlade;

// ReSharper disable EventNeverSubscribedTo.Global

namespace SaveCleaner;

public sealed class SaveCleanerAddon(string id, string name, params SaveCleanerAddon.ISetting[] settings)
{
    public delegate bool ObjectPredicateDelegate(SaveCleanerAddon addon, object obj);

    public delegate bool WipeDelegate(SaveCleanerAddon addon);

    public delegate bool PredicateDelegate(SaveCleanerAddon addon);

    public string Id { get; } = id;
    public string Name { get; } = name;
    private readonly ImmutableDictionary<string, ISetting> _settings = settings.ToImmutableDictionary(s => s.Id, s => s);
    private Cleaner _cleaner;
    internal IEnumerable<ISetting> Settings => _settings.Values;

    public override string ToString() => $"{Name} ({Id})";

    public event ObjectPredicateDelegate Removable;
    public event ObjectPredicateDelegate Essential;
    public event WipeDelegate OnWipe;
    public event PredicateDelegate OnPreClean;
    public event PredicateDelegate OnPostClean;

    internal bool CanWipe => OnWipe != null;

    public bool Disabled { get; internal set; }

    public T GetValue<T>(string id) => ((AbstractSetting<T>)_settings[id]).Value;

    public void Register<T>() where T : MBSubModuleBase
    {
        SubModule.Addons.Add(typeof(T), this);
    }

    public IEnumerable<object> GetAllParents(object obj, int depth = -1)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        yield return _cleaner.GetAllParents(obj, depth, []);
    }

    public IEnumerable<T> GetAllParents<T>(object obj, int depth = -1)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        return _cleaner.GetAllParents<T>(obj, depth, []);
    }

    public object GetFirstParent(object obj, Func<object, bool> predicate, int depth = -1)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        return _cleaner.GetFirstParent(obj, predicate, depth, []);
    }

    public IEnumerable<object> GetParents(object obj)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        return _cleaner.GetParents(obj);
    }

    public void ClearRemovablePredicates()
    {
        Removable = null;
    }

    public void ClearEssentialPredicates()
    {
        Essential = null;
    }

    internal bool IsRemovable(object o)
    {
        return Removable?.GetInvocationList().Cast<ObjectPredicateDelegate>().Any(p => p.Invoke(this, o)) ?? false;
    }

    internal bool IsEssential(object o)
    {
        return Essential?.GetInvocationList().Cast<ObjectPredicateDelegate>().Any(p => p.Invoke(this, o)) ?? false;
    }

    internal bool Wipe()
    {
        if (Disabled) return false;
        return OnWipe?.GetInvocationList().Cast<WipeDelegate>().All(p => p.Invoke(this)) ?? false;
    }

    internal bool PreClean(Cleaner cleaner)
    {
        if (Disabled) return true;
        _cleaner = cleaner;
        return OnPreClean?.GetInvocationList().Cast<PredicateDelegate>().All(p => p.Invoke(this)) ?? true;
    }

    internal bool PostClean()
    {
        if (Disabled) return true;
        bool result = OnPostClean?.GetInvocationList().Cast<PredicateDelegate>().All(p => p.Invoke(this)) ?? true;
        _cleaner = null;
        return result;
    }

    public interface ISetting
    {
        public string Id { get; }
        public string Name { get; }
        public string Hint { get; }
        public int Order { get; }
    }

    public abstract class AbstractSetting<T>(string id, string name, string hint, int order, T defaultValue) : ISetting
    {
        public T Value { get; internal set; } = defaultValue;
        public T DefaultValue { get; } = defaultValue;
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Hint { get; } = hint;
        public int Order { get; } = order;
    }

    public class StringSetting(string id, string name, string hint, int order, string defaultValue) : AbstractSetting<string>(id, name, hint, order, defaultValue);

    public class BoolSetting(string id, string name, string hint, int order, bool defaultValue) : AbstractSetting<bool>(id, name, hint, order, defaultValue);

    public class IntSetting(string id, string name, string hint, int order, int defaultValue, int min, int max) : AbstractSetting<int>(id, name, hint, order, defaultValue)
    {
        public int Min { get; } = min;
        public int Max { get; } = max;
    }

    public class FloatSetting(string id, string name, string hint, int order, float defaultValue, float min, float max) : AbstractSetting<float>(id, name, hint, order, defaultValue)
    {
        public float Min { get; } = min;
        public float Max { get; } = max;
    }
}