using HarmonyLib;
using SandBox.View.Map;

// ReSharper disable InconsistentNaming

namespace SaveCleaner;

public class Patches
{
    [HarmonyPatch(typeof(MapScreen), "OnInitialize")]
    public static class MapScreenOnInitializePatch
    {
        public static void Postfix(MapScreen __instance)
        {
            SubModule.Instance.OnMapScreenInit(__instance);
        }
    }
}