using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

namespace HextechRelicStats;

/// <summary>Clears every "this combat" counter when a combat starts and again when it ends.</summary>
internal static class CombatReset
{
    public static int Install(Harmony harmony)
    {
        int ok = 0;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(CombatManager), "SetUpCombat"),
                prefix: new HarmonyMethod(typeof(CombatReset), nameof(ResetNow)));
            ok++;
        }
        catch (Exception e) { MainFile.Logger.Warn($"CombatReset: start patch failed: {e.Message}"); }

        try
        {
            var end = AutoTracker.FindOverloadOf(typeof(CombatManager), "EndCombatInternal");
            if (end == null) throw new InvalidOperationException("EndCombatInternal not found");
            harmony.Patch(end, postfix: new HarmonyMethod(typeof(CombatReset), nameof(AfterEnd)));
            ok++;
        }
        catch (Exception e) { MainFile.Logger.Warn($"CombatReset: end patch failed: {e.Message}"); }
        return ok;
    }

    public static void ResetNow() => DynamicRuneStats.ResetCombatAll();

    public static void AfterEnd(ref Task __result)
    {
        // Observe only; the game keeps its own Task.
        __result.ContinueWith(prev =>
        {
            // The game can finish an old (stale) combat while a new one is running: only reset when no combat is in progress.
            if (prev.IsCompletedSuccessfully && !(CombatManager.Instance?.IsInProgress ?? false)) DynamicRuneStats.ResetCombatAll();
        }, TaskContinuationOptions.ExecuteSynchronously);
    }
}
