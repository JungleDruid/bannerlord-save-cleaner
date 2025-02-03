using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace SaveCleaner;

public static class CleanConditions
{
    internal static readonly Dictionary<Type, List<Func<object, bool>>> Removable = new();
    internal static readonly Dictionary<Type, List<Func<object, bool>>> ForceKeep = new();

    public static void AddRemovable<T>(Func<object, bool> predicate) where T : MBSubModuleBase => AddCondition<T>(predicate, Removable);
    public static void AddEssential<T>(Func<object, bool> predicate) where T : MBSubModuleBase => AddCondition<T>(predicate, ForceKeep);
    public static bool RemoveRemovable<T>(Func<object, bool> predicate) where T : MBSubModuleBase => RemoveCondition<T>(predicate, Removable);
    public static bool RemoveEssential<T>(Func<object, bool> predicate) where T : MBSubModuleBase => RemoveCondition<T>(predicate, ForceKeep);
    public static bool RemoveAllRemovables<T>() where T : MBSubModuleBase => RemoveAllConditions<T>(Removable);
    public static bool RemoveAllEssentials<T>() where T : MBSubModuleBase => RemoveAllConditions<T>(ForceKeep);

    private static ulong s_cacheVersion = ulong.MaxValue;
    private static IEnumerable<Func<object, bool>> s_forceKeepCache;
    private static IEnumerable<Func<object, bool>> s_removableCache;

    public static bool IsRemovable(object obj)
    {
        return !s_forceKeepCache.Any(predicate => predicate.Invoke(obj)) &&
               s_removableCache
                   .Any(predicate => predicate.Invoke(obj));
    }

    internal static void Prepare(SaveCleanerOptions opt)
    {
        if (s_cacheVersion == opt.Version) return;
        s_cacheVersion = opt.Version;
        s_forceKeepCache = ForceKeep
            .Where(kv => kv.Key == typeof(SubModule) || opt.ModForceKeepEnabled && !opt.ForceKeepDisabled.Contains(GetModuleId(kv.Key)))
            .SelectMany(kv => kv.Value);
        s_removableCache = Removable
            .Where(kv => kv.Key == typeof(SubModule) || opt.ModRemovableEnabled && !opt.RemovableDisabled.Contains(GetModuleId(kv.Key)))
            .SelectMany(kv => kv.Value);
    }

    private static void AddCondition<T>(Func<object, bool> predicate, Dictionary<Type, List<Func<object, bool>>> collection) where T : MBSubModuleBase
    {
        if (!collection.TryGetValue(typeof(T), out var conditions))
        {
            conditions = [];
            collection[typeof(T)] = conditions;
        }

        if (conditions.Contains(predicate)) return;
        conditions.Add(predicate);
    }

    private static bool RemoveCondition<T>(Func<object, bool> predicate, Dictionary<Type, List<Func<object, bool>>> collection) where T : MBSubModuleBase
    {
        return collection.TryGetValue(typeof(T), out var conditions) && conditions.Remove(predicate);
    }

    private static bool RemoveAllConditions<T>(Dictionary<Type, List<Func<object, bool>>> collection) where T : MBSubModuleBase
    {
        return collection.Remove(typeof(T));
    }

    internal static string GetModuleName(Type type)
    {
        return type.Assembly.FullName.Split(',')[0];
    }

    internal static string GetModuleId(Type type)
    {
        return type.Assembly.Modules.First().FullyQualifiedName;
    }
    
    [Obsolete("Use AddEssential<T> instead")]
    public static void AddForceKeep<T>(Func<object, bool> predicate) where T : MBSubModuleBase => AddEssential<T>(predicate);
    [Obsolete("Use RemoveEssential<T> instead")]
    public static bool RemoveForceKeep<T>(Func<object, bool> predicate) where T : MBSubModuleBase => RemoveEssential<T>(predicate);
    [Obsolete("Use RemoveAllEssentials<T> instead")]
    public static bool RemoveAllForceKeeps<T>() where T : MBSubModuleBase => RemoveAllEssentials<T>();
}