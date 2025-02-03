using System;
using System.Collections.Generic;
using System.Linq;

// ReSharper disable NonReadonlyMemberInGetHashCode

namespace SaveCleaner;

public sealed record SaveCleanerOptions
{
    public ulong Version { get; set; }
    public bool CleanHeroes { get; set; } = true;
    public bool CleanDisappearedHeroes { get; set; } = true;
    public bool ModRemovableEnabled { get; set; } = true;
    public bool ModForceKeepEnabled { get; set; } = true;
    public HashSet<string> RemovableDisabled { get; private set; } = [];
    public HashSet<string> ForceKeepDisabled { get; private set; } = [];

    public SaveCleanerOptions DeepClone()
    {
        SaveCleanerOptions clone = this with
        {
            RemovableDisabled = [..RemovableDisabled],
            ForceKeepDisabled = [..ForceKeepDisabled]
        };

        return clone;
    }

    public bool Equals(SaveCleanerOptions other)
    {
        return GetHashCode() == other?.GetHashCode();
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            CleanHeroes,
            CleanDisappearedHeroes,
            RemovableDisabled.GetConsistentHashCode(),
            ForceKeepDisabled.GetConsistentHashCode());
    }
}

public static class HashSetExtensions
{
    public static int GetConsistentHashCode(this HashSet<string> hashSet)
    {
        return hashSet.Select(item => item?.GetHashCode() ?? 0).Aggregate(0, (acc, hash) => acc ^ hash);
    }
}