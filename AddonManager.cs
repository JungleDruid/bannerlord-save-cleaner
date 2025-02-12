using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using TaleWorlds.LinQuick;

namespace SaveCleaner;

internal class AddonManager
{
    // ReSharper disable once InconsistentNaming
    private static readonly List<SaveCleanerAddon> _addons = [];
    private static ILogger s_logger;
    private static ILogger Logger => s_logger ??= LogFactory.Get<AddonManager>();

    internal static IReadOnlyList<SaveCleanerAddon> Addons => _addons;

    internal static void Register<T>(SaveCleanerAddon addon)
    {
        SaveCleanerAddon existed = _addons.FirstOrDefaultQ(a => a.Owner == typeof(T));
        if (existed is not null)
        {
            Logger.LogWarning($"Multiple register of addon by {typeof(T).Name} detected. Removing the existed addon.");
            _addons.Remove(existed);
        }

        addon.Owner = typeof(T);
        if (typeof(T) == typeof(SubModule))
        {
            _addons.Insert(0, addon);
        }
        else
        {
            _addons.Add(addon);
        }

        Logger.LogInformation($"Addon ({typeof(T).Name}) registered. {typeof(T).Assembly.FullName}");
    }

    internal static void Unregister<T>()
    {
        SaveCleanerAddon existed = _addons.FirstOrDefaultQ(addon => addon.Owner == typeof(T));
        if (existed is not null)
        {
            _addons.Remove(existed);
        }
        else
        {
            Logger.LogWarning($"{typeof(T).Name} does not have any addon to unregister.");
        }
    }
}