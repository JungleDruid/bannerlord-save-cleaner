using System.Collections.Generic;
using System.Collections.Immutable;
using TaleWorlds.MountAndBlade;

// ReSharper disable EventNeverSubscribedTo.Global

namespace SaveCleaner;

public sealed class SaveCleanerAddon(string id, string name, params SaveCleanerAddon.ISetting[] settings)
{
    public delegate bool ObjectPredicateDelegate(SaveCleanerAddon addon, object obj);

    public delegate bool WipeDelegate(SaveCleanerAddon addon);

    public string Id { get; } = id;
    public string Name { get; } = name;
    private readonly ImmutableDictionary<string, ISetting> _settings = settings.ToImmutableDictionary(s => s.Id, s => s);
    internal IEnumerable<ISetting> Settings => _settings.Values;

    public event ObjectPredicateDelegate Removable;
    public event ObjectPredicateDelegate Essential;
    public event WipeDelegate OnWipe;

    internal bool CanWipe => OnWipe != null;

    public bool Disabled { get; internal set; }

    public T GetValue<T>(string id) => ((AbstractSetting<T>)_settings[id]).Value;

    public void Register<T>() where T : MBSubModuleBase
    {
        SubModule.Addons.Add(typeof(T), this);
    }

    internal bool IsRemovable(object o)
    {
        return Removable?.Invoke(this, o) ?? false;
    }

    internal bool IsEssential(object o)
    {
        return Essential?.Invoke(this, o) ?? false;
    }

    internal bool Wipe()
    {
        return OnWipe?.Invoke(this) ?? false;
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