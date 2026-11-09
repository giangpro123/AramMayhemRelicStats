using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace HextechRelicStats;

/// <summary>Small patches for effects that do not go through the game's normal commands (Watcher Mantra, Regret revives).</summary>
internal static class OptionalPatches
{
    public static void InstallFor(Harmony harmony, string assemblyName)
    {
        try
        {
            if (assemblyName == "HextechRunes")
            {
                try
                {
                    harmony.Patch(Hex.Method("HextechRunes", "HextechRuneGrantHelper", "PickRewardRuneType"),
                        postfix: new HarmonyMethod(typeof(EggPatch), nameof(EggPatch.Post)));
                }
                catch (Exception e) { MainFile.Logger.Warn($"OptionalPatches egg: {e.Message}"); }
                try
                {
                    // Protein Shake converts enemy Block into healing inside its own helper, not in a normal hook.
                    harmony.Patch(Hex.Method("HextechRunes", "ProteinShakeEnemyHex", "HealThenBlock"),
                        prefix: new HarmonyMethod(typeof(ProteinShakePatch), nameof(ProteinShakePatch.Pre)),
                        finalizer: new HarmonyMethod(typeof(ProteinShakePatch), nameof(ProteinShakePatch.Fin)));
                }
                catch (Exception e) { MainFile.Logger.Warn($"OptionalPatches protein shake: {e.Message}"); }
                harmony.Patch(Hex.Method("HextechRunes", "HextechGoldRewardHelper", "AddFixedExtraGoldReward"),
                    prefix: new HarmonyMethod(typeof(GoldPatch), nameof(GoldPatch.Pre)));
            }
            else if (assemblyName == "HextechWatcherPack")
            {
                harmony.Patch(Hex.Method("HextechWatcherPack", "WatcherBridge", "GainMantra"),
                    prefix: new HarmonyMethod(typeof(MantraPatch), nameof(MantraPatch.Pre)),
                    postfix: new HarmonyMethod(typeof(MantraPatch), nameof(MantraPatch.Post)));
            }
            else if (assemblyName == "HextechRunesSponsorPack")
            {
                harmony.Patch(Hex.Method("HextechRunesSponsorPack", "RegretRune", "AfterDeath"),
                    prefix: new HarmonyMethod(typeof(RegretPatch), nameof(RegretPatch.Pre)),
                    postfix: new HarmonyMethod(typeof(RegretPatch), nameof(RegretPatch.Post)));
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"OptionalPatches {assemblyName}: {e.Message}"); }
    }

    internal static void Observe(Task t, Action done) =>
        t.ContinueWith(prev =>
        {
            if (!prev.IsCompletedSuccessfully) return;
            try { done(); } catch (Exception e) { MainFile.Logger.Warn($"OptionalPatches: {e.Message}"); }
        }, TaskContinuationOptions.ExecuteSynchronously);

    internal static class ProteinShakePatch
    {
        public static void Pre(out string? __state)
        {
            __state = default;
            try { __state = HexTracker.EnterHex("ProteinShake"); } catch (Exception e) { AutoTracker.Swallow(e); }
        }

        public static void Fin(string? __state)
        {
            try { HexTracker.ExitHex(__state); } catch (Exception e) { AutoTracker.Swallow(e); }
        }
    }

    internal static class EggPatch
    {
        // Prismatic Egg replaces treasure relics with random runes: count them by rarity.
        public static void Post(Player player, Type? __result, string?[] saltParts)
        {
            try
            {
                if (__result == null || saltParts == null || saltParts.Length == 0 || saltParts[0] != "prismatic-egg") return;
                var egg = player?.Relics.FirstOrDefault(r => r.Id.Entry == "PRISMATIC_EGG_RUNE");
                if (egg == null) return;
                var catalog = Hex.Find("HextechRunes")?.GetType("HextechRunes.HextechCatalog");
                var m = catalog?.GetMethod("TryGetPlayerRuneRarity", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (m == null) return;
                var relic = MegaCrit.Sts2.Core.Models.ModelDb.GetById<RelicModel>(MegaCrit.Sts2.Core.Models.ModelDb.GetId(__result));
                var args = new object?[] { relic, null };
                if (m.Invoke(null, args) is not true || args[1] == null) return;
                AutoTracker.Record(egg, args[1]!.ToString() + " augments", 1);
            }
            catch (Exception e) { AutoTracker.Swallow(e); }
        }
    }

    internal static class GoldPatch
    {
        // Extra gold added to the post-combat rewards by a rune/forger.
        public static void Pre(MegaCrit.Sts2.Core.Entities.Players.Player player, int amount)
        {
            try
            {
            var rune = AutoTracker.CurrentRune;
            if (rune != null && ReferenceEquals(rune.Owner, player)) AutoTracker.Record(rune, "Extra gold earned", amount);
            }
            catch (Exception e) { AutoTracker.Swallow(e); }
        }
    }

    internal static class MantraPatch
    {
        public static void Pre(out RelicModel? __state) => __state = AutoTracker.CurrentRune;

        public static void Post(Player owner, int amount, RelicModel? __state, ref Task __result)
        {
            try
            {
            if (__state == null) return;
            var rune = __state;
            OptionalPatches.Observe(__result, () => { if (ReferenceEquals(rune.Owner, owner)) AutoTracker.Record(rune, "Gained Mantra", amount); });
            }
            catch (Exception e) { AutoTracker.Swallow(e); }
        }

    }

    internal static class RegretPatch
    {
        private static int Used(object relic) => Traverse.Create(relic).Property("SavedRevivesUsed").GetValue<int>();

        public static void Pre(object __instance, out int __state) => __state = Used(__instance);

        public static void Post(object __instance, int __state, ref Task __result)
        {
            try
            {
            OptionalPatches.Observe(__result, () => AutoTracker.Record(__instance as RelicModel, "Revives used", Used(__instance) - __state));
            }
            catch (Exception e) { AutoTracker.Swallow(e); }
        }

    }
}
