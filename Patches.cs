using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using SandBox.View.Map;
using TaleWorlds.SaveSystem.Save;

// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
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

    // [HarmonyPatch(typeof(SaveContext), "CollectObjects", typeof(object))]
    public static class SaveContextCollectObjectsPatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new(instructions);

            // this._objectsToIterate.Enqueue(obj);
            CodeMatcher target = matcher.MatchEndForward(
                CodeMatch.IsLdarg(0),
                CodeMatch.LoadsField(AccessTools.Field(typeof(SaveContext), "_objectsToIterate")),
                CodeMatch.LoadsLocal(),
                CodeMatch.Calls(() => default(Queue<object>).Enqueue(null)));

            var objLocal = (LocalBuilder)target.InstructionAt(-1).operand;

            CodeInstruction[] insertionCode =
            [
                CodeInstruction.LoadLocal(objLocal.LocalIndex),
                CodeInstruction.LoadArgument(1),
                CodeInstruction.Call(typeof(Patches), nameof(AddRelationToCollector))
            ];

            target.Advance(1).Insert(insertionCode);

            return matcher.Instructions();
        }
    }

    // [HarmonyPatch(typeof(SaveContext), "CollectContainerObjects", typeof(ContainerType), typeof(object))]
    public static class SaveContextCollectContainerObjectsPatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher matcher = new(instructions);

            // this._objectsToIterate.Enqueue(obj);
            CodeMatcher target = matcher.MatchEndForward(
                CodeMatch.IsLdarg(0),
                CodeMatch.LoadsField(AccessTools.Field(typeof(SaveContext), "_objectsToIterate")),
                CodeMatch.LoadsLocal(),
                CodeMatch.Calls(() => default(Queue<object>).Enqueue(null)));

            var objLocal = (LocalBuilder)target.InstructionAt(-1).operand;

            CodeInstruction[] insertionCode =
            [
                CodeInstruction.LoadLocal(objLocal.LocalIndex),
                CodeInstruction.LoadArgument(2),
                CodeInstruction.Call(typeof(Patches), nameof(AddRelationToCollector))
            ];

            target.Advance(1).Insert(insertionCode);

            return matcher.Instructions();
        }
    }

    // [HarmonyPatch(typeof(SaveContext), nameof(SaveContext.Save))]
    public static class SaveContextSavePatch
    {
        public static void Postfix(List<object> ____childObjects)
        {
            SubModule.Instance?.CurrentCleaner?.SendChildObjectsToCollector(____childObjects);
        }
    }

    private static void AddRelationToCollector(object child, object parent)
    {
        SubModule.Instance?.CurrentCleaner?.AddRelationToCollector(child, parent);
    }
}