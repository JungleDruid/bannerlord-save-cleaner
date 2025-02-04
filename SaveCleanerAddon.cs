using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TaleWorlds.MountAndBlade;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable EventNeverSubscribedTo.Global

namespace SaveCleaner;

/// <summary>
/// Instantiate this class and call <see cref="Register{T}"/> to register the addon in SaveCleaner.
/// Wrap the call inside try-catch to have a soft dependency of SaveCleaner.
/// See <see cref="DefaultAddon.Register"/> for examples.
/// </summary>
/// <param name="id">A unique ID for this addon.</param>
/// <param name="name">The name to show in the MCM menu.</param>
/// <param name="settings">Additional settings for players to tweak in the MCM menu.</param>
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

    /// <summary>
    /// Invoked when checking if an object can be removed.
    /// </summary>
    public event ObjectPredicateDelegate Removable;

    /// <summary>
    /// Invoked when checking if an object must be kept.
    /// </summary>
    public event ObjectPredicateDelegate Essential;

    /// <summary>
    /// Invoked when the wipe button is clicked in the MCM menu.
    /// </summary>
    public event WipeDelegate OnWipe;

    /// <summary>
    /// Invoked before the clean process. Won't be invoked if <see cref="Disabled"/>
    /// </summary>
    public event PredicateDelegate OnPreClean;

    /// <summary>
    /// Invoked after the clean process. Won't be invoked if <see cref="Disabled"/>
    /// </summary>
    public event PredicateDelegate OnPostClean;

    internal bool CanWipe => OnWipe != null;

    public bool Disabled { get; internal set; }

    /// <summary>
    /// Get the current value from the <see cref="Settings"/>
    /// </summary>
    /// <param name="id">The setting's <see cref="ISetting.Id"/></param>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <returns></returns>
    public T GetValue<T>(string id) => ((AbstractSetting<T>)_settings[id]).Value;

    /// <summary>
    /// Call this to register the addon
    /// </summary>
    /// <typeparam name="T">The SubModule type</typeparam>
    public void Register<T>() where T : MBSubModuleBase
    {
        SubModule.Addons.Add(typeof(T), this);
    }

    /// <summary>
    /// Get the parents of <paramref name="obj"/> recursively.
    /// This should only be called inside <see cref="Removable"/> and <see cref="Essential"/>.
    /// </summary>
    /// <param name="obj">the object</param>
    /// <param name="depth">the recursive depth</param>
    /// <returns>the parent objects of <paramref name="obj"/></returns>
    /// <exception cref="NullReferenceException">Throws if the cleaner is not running.</exception>
    public IEnumerable<object> GetAllParents(object obj, int depth = -1)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        yield return _cleaner.GetAllParents(obj, depth, []);
    }


    /// <summary>
    /// Get the parents of <paramref name="obj"/> recursively, but only returns the right type.
    /// This should only be called inside <see cref="Removable"/> and <see cref="Essential"/>.
    /// </summary>
    /// <param name="obj">the object</param>
    /// <param name="depth">the recursive depth</param>
    /// <returns>the parent objects of <paramref name="obj"/> that match the type <typeparamref name="T"/></returns>
    /// <exception cref="NullReferenceException">Throws if the cleaner is not running.</exception>
    public IEnumerable<T> GetAllParents<T>(object obj, int depth = -1)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        return _cleaner.GetAllParents<T>(obj, depth, []);
    }

    /// <summary>
    /// Get the first parent of <paramref name="obj"/> that matches the <paramref name="predicate"/>.
    /// This should only be called inside <see cref="Removable"/> and <see cref="Essential"/>.
    /// </summary>
    /// <param name="obj">the object</param>
    /// <param name="predicate">the predicate for the parent to match</param>
    /// <param name="depth">the recursive depth</param>
    /// <returns>the first parent object of <paramref name="obj"/> that match <paramref name="predicate"/></returns>
    /// <exception cref="NullReferenceException">Throws if the cleaner is not running.</exception>
    public object GetFirstParent(object obj, Func<object, bool> predicate, int depth = -1)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        return _cleaner.GetFirstParent(obj, predicate, depth, []);
    }

    /// <summary>
    /// Returns the direct parents of <paramref name="obj"/>.
    /// It's more efficient than <see cref="GetAllParents"/> with depth of 1.
    /// </summary>
    /// <param name="obj">the object</param>
    /// <returns>the direct parents of <paramref name="obj"/></returns>
    /// <exception cref="NullReferenceException">Throws if the cleaner is not running.</exception>
    public IEnumerable<object> GetParents(object obj)
    {
        if (_cleaner is null) throw new NullReferenceException("No cleaner available.");
        return _cleaner.GetParents(obj);
    }

    /// <summary>
    /// Clear all events in <see cref="Removable"/>
    /// </summary>
    public void ClearRemovablePredicates()
    {
        Removable = null;
    }

    /// <summary>
    /// Clear all events in <see cref="Essential"/>
    /// </summary>
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

    /// <summary>
    /// The MCM setting interface
    /// </summary>
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

    /// <summary>
    /// A Text setting in MCM. See <see cref="MCM.Abstractions.Attributes.v2.SettingPropertyTextAttribute"/>.
    /// </summary>
    /// <param name="id">the setting's unique id</param>
    /// <param name="name">the setting's name</param>
    /// <param name="hint">the hint on mouseover</param>
    /// <param name="order">the order of the setting</param>
    /// <param name="defaultValue">the default value</param>
    public class StringSetting(string id, string name, string hint, int order, string defaultValue) : AbstractSetting<string>(id, name, hint, order, defaultValue);

    /// <summary>
    /// A Bool setting in MCM. See <see cref="MCM.Abstractions.Attributes.v2.SettingPropertyBoolAttribute"/>.
    /// </summary>
    /// <param name="id">the setting's unique id</param>
    /// <param name="name">the setting's name</param>
    /// <param name="hint">the hint on mouseover</param>
    /// <param name="order">the order of the setting</param>
    /// <param name="defaultValue">the default value</param>
    public class BoolSetting(string id, string name, string hint, int order, bool defaultValue) : AbstractSetting<bool>(id, name, hint, order, defaultValue);

    /// <summary>
    /// A Integer setting in MCM. See <see cref="MCM.Abstractions.Attributes.v2.SettingPropertyIntegerAttribute"/>.
    /// </summary>
    /// <param name="id">the setting's unique id</param>
    /// <param name="name">the setting's name</param>
    /// <param name="hint">the hint on mouseover</param>
    /// <param name="order">the order of the setting</param>
    /// <param name="defaultValue">the default value</param>
    /// <param name="min">the minimum value</param>
    /// <param name="max">the maximum value</param>
    public class IntSetting(string id, string name, string hint, int order, int defaultValue, int min, int max) : AbstractSetting<int>(id, name, hint, order, defaultValue)
    {
        public int Min { get; } = min;
        public int Max { get; } = max;
    }

    /// <summary>
    /// A FloatingInteger setting in MCM. See <see cref="MCM.Abstractions.Attributes.v2.SettingPropertyFloatingIntegerAttribute"/>.
    /// </summary>
    /// <param name="id">the setting's unique id</param>
    /// <param name="name">the setting's name</param>
    /// <param name="hint">the hint on mouseover</param>
    /// <param name="order">the order of the setting</param>
    /// <param name="defaultValue">the default value</param>
    /// <param name="min">the minimum value</param>
    /// <param name="max">the maximum value</param>
    public class FloatSetting(string id, string name, string hint, int order, float defaultValue, float min, float max) : AbstractSetting<float>(id, name, hint, order, defaultValue)
    {
        public float Min { get; } = min;
        public float Max { get; } = max;
    }
}