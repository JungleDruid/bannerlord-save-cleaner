using System;
using TaleWorlds.MountAndBlade;

// ReSharper disable All

namespace SaveCleaner;

[Obsolete("Use SaveCleanerAddon instead.", true)]
public static class CleanConditions
{
    public static void AddRemovable<T>(Func<object, bool> predicate) where T : MBSubModuleBase
    {
    }

    public static void AddEssential<T>(Func<object, bool> predicate) where T : MBSubModuleBase
    {
    }

    public static bool RemoveRemovable<T>(Func<object, bool> predicate) where T : MBSubModuleBase => false;

    public static bool RemoveEssential<T>(Func<object, bool> predicate) where T : MBSubModuleBase => false;

    public static bool RemoveAllRemovables<T>() where T : MBSubModuleBase => false;
    public static bool RemoveAllEssentials<T>() where T : MBSubModuleBase => false;

    public static bool IsRemovable(object obj) => false;

    public static void AddForceKeep<T>(Func<object, bool> predicate) where T : MBSubModuleBase => AddEssential<T>(predicate);

    public static bool RemoveForceKeep<T>(Func<object, bool> predicate) where T : MBSubModuleBase => RemoveEssential<T>(predicate);

    public static bool RemoveAllForceKeeps<T>() where T : MBSubModuleBase => RemoveAllEssentials<T>();
}