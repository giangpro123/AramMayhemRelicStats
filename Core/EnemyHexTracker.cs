using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using RelicStats.Core;

namespace HextechRelicStats;

/// <summary>
/// Tracks what ENEMY hexes do (enemy healing, Block, powers, damage to you). Enemy hexes are not relics, so the
/// stats live in the Relic Stats registry under "ENEMYHEX_&lt;kind&gt;" (this gives saving and per-run reset for free)
/// and are shown by adding a Stats box to the enemy hex tooltip.
/// </summary>
internal static class HexTracker
{
    public const string IdPrefix = "ENEMYHEX_";

    // Hexes whose stats are not found by the static scan.
    private static readonly Dictionary<string, string[]> EnemyOverride = new()
    {
        ["Goldrend"] = new[] { "Gold lost" },
        ["Mystery"] = new[] { "Cards transformed" },
        ["Upgrade"] = new[] { "Cards downgraded" },
        ["JeweledGauntlet"] = new[] { "Moves repeated" },
    };

    // Any non-null key works for "this combat": the figures are cleared when a combat starts and ends.
    private static readonly object CombatKey = new();

    // The enemy hex whose effect is currently running (flows through awaits).
    private static readonly AsyncLocal<string?> CurrentHex = new();
    private static readonly AsyncLocal<bool> InApply = new();
    private static readonly AsyncLocal<string?> ApplyHex = new();

    public static List<IRelicStats> Install(Harmony harmony)
    {
        var stats = new List<IRelicStats>();
        var asm = Hex.Find("HextechRunes");
        if (asm == null) return stats;

        var kindType = asm.GetType("HextechRunes.MonsterHexKind");
        var effectType = asm.GetType("HextechRunes.HextechEnemyHexEffect");
        if (kindType == null || effectType == null)
        {
            MainFile.Logger.Warn("HexTracker: enemy hex types not found (HextechRunes changed?)");
            return stats;
        }

        foreach (var name in Enum.GetNames(kindType))
            stats.Add(new DynamicRuneStats(IdPrefix + name, EnemyOverride.TryGetValue(name, out var ov) ? ov : EnemyMetricTable.ByKind.GetValueOrDefault(name)) { Quiet = true, OnceOnly = EnemyMetricTable.OncePerCombat.Contains(name) || name == "Mystery" });

        // Wrap every enemy hex effect hook so we know which hex is acting.
        var methods = new HashSet<MethodBase>();
        foreach (var type in AutoTracker.SafeTypesOf(asm))
        {
            if (type.IsAbstract || !effectType.IsAssignableFrom(type)) continue;
            for (Type? t = type; t != null && t != effectType && t != typeof(object); t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!m.IsVirtual || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                    if (m.DeclaringType != null && (m.DeclaringType.IsGenericType || m.DeclaringType.ContainsGenericParameters)) continue;
                    if (m.GetBaseDefinition().DeclaringType == t) continue;
                    if (m.ReturnType != typeof(void) && m.ReturnType != typeof(Task)) continue;
                    if (!(m.Name.StartsWith("Before") || m.Name.StartsWith("After") || m.Name.StartsWith("Apply"))) continue;
                    methods.Add(m);
                }
        }

        int ok = 0;
        var pre = new HarmonyMethod(typeof(HexTracker), nameof(HookPrefix));
        var fin = new HarmonyMethod(typeof(HexTracker), nameof(HookFinalizer));
        foreach (var m in methods)
        {
            try { harmony.Patch(m, prefix: pre, finalizer: fin); ok++; }
            catch (Exception e) { MainFile.Logger.Warn($"HexTracker: could not wrap {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }

        int fx = 0;
        fx += Patch(harmony, AccessTools.Method(typeof(CreatureCmd), "Heal"), nameof(HealPre), nameof(HealPost));
        fx += Patch(harmony, AccessTools.Method(typeof(CreatureCmd), "GainMaxHp"), nameof(MaxHpPre), nameof(MaxHpPost));
        fx += Patch(harmony, AutoTracker.FindOverloadOf(typeof(CreatureCmd), "GainBlock", "amount"), nameof(BlockPre), nameof(BlockPost));
        fx += Patch(harmony, AutoTracker.FindOverloadOf(typeof(CreatureCmd), "Damage", "dealer", "cardPlay", "targets"), nameof(DamagePre), nameof(DamagePost));
        fx += Patch(harmony, AutoTracker.FindOverloadOf(typeof(PowerCmd), "Apply", "power", "target"), nameof(PowerPre), nameof(PowerPost), nameof(PowerFinal));
        fx += Patch(harmony, AccessTools.Method(typeof(PowerCmd), "ModifyAmount"), nameof(PowerModPre), nameof(PowerModPost));

        foreach (var name in new[] { "Add", "AddGeneratedCardToCombat", "AddGeneratedCardsToCombat" })
            foreach (var m in typeof(CardPileCmd).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != name || m.IsGenericMethodDefinition) continue;
                try
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(HexTracker), nameof(CardAddPre)),
                        finalizer: new HarmonyMethod(typeof(HexTracker), nameof(CardAddFinal)));
                    fx++;
                }
                catch (Exception e) { MainFile.Logger.Warn($"HexTracker: card add patch failed: {e.Message}"); }
            }

        fx += Patch(harmony, AutoTracker.FindOverloadOf(typeof(PlayerCmd), "LoseGold", "amount", "player"), nameof(GoldLossPre), nameof(GoldLossPost));
        foreach (var m in typeof(CardCmd).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.IsGenericMethodDefinition) continue;
            try
            {
                if (m.Name == "Transform")
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(HexTracker), nameof(TransformPre)), finalizer: new HarmonyMethod(typeof(HexTracker), nameof(TransformFinal)));
                    fx++;
                }
                else if (m.Name == "Downgrade")
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(HexTracker), nameof(DowngradePre)), finalizer: new HarmonyMethod(typeof(HexTracker), nameof(DowngradeFinal)));
                    fx++;
                }
            }
            catch (Exception e) { MainFile.Logger.Warn($"HexTracker: card command patch failed: {e.Message}"); }
        }
        try
        {
            // Jeweled Gauntlet logs a line each time it repeats a monster's move; that is the one reliable marker.
            var log = asm.GetType("HextechRunes.HextechLog");
            var info = log == null ? null : AccessTools.Method(log, "Info", new[] { typeof(string), typeof(string) });
            if (info != null) { harmony.Patch(info, prefix: new HarmonyMethod(typeof(HexTracker), nameof(JeweledLogPre))); fx++; }
        }
        catch (Exception e) { MainFile.Logger.Warn($"HexTracker: jeweled gauntlet patch failed: {e.Message}"); }

        try
        {
            var catalog = asm.GetType("HextechRunes.MonsterHexCatalog");
            var tips = catalog == null ? null : AccessTools.Method(catalog, "GetEnemyHexHoverTips");
            if (tips == null) throw new InvalidOperationException("GetEnemyHexHoverTips not found");
            harmony.Patch(tips, postfix: new HarmonyMethod(typeof(HexTracker), nameof(TipsPost)));
            fx++;
        }
        catch (Exception e) { MainFile.Logger.Warn($"HexTracker: tooltip patch failed: {e.Message}"); }

        MainFile.Logger.Info($"HexTracker: {stats.Count} enemy hexes, wrapped {ok} hooks, {fx} effect patches");
        return stats;
    }

    private static int Patch(Harmony harmony, MethodBase? target, string pre, string post, string? fin = null)
    {
        try
        {
            if (target == null) throw new InvalidOperationException("method not found");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(HexTracker), pre),
                postfix: new HarmonyMethod(typeof(HexTracker), post),
                finalizer: fin == null ? null : new HarmonyMethod(typeof(HexTracker), fin));
            return 1;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"HexTracker: patch {post} failed: {e.Message}");
            return 0;
        }
    }

    private static void Rec(string? kind, string key, decimal amount, object? combatKey)
    {
        try
        {
            if (kind == null || amount <= 0) return;
            if (RelicStatsRegistry.Get(IdPrefix + kind) is DynamicRuneStats s) s.Add(key, amount, combatKey);
        }
        catch (Exception e) { MainFile.Logger.Warn($"HexTracker record failed: {e.Message}"); }
    }

    // Observe-only: attach a continuation to the game's own task and hand the ORIGINAL task straight back,
    // so the game (and other mods) see exactly the same Task object and timing as without this mod.
    private static Task Then(Task t, Action done)
    {
        t.ContinueWith(prev =>
        {
            if (!prev.IsCompletedSuccessfully) return;
            try { done(); } catch (Exception e) { MainFile.Logger.Warn($"HexTracker: {e.Message}"); }
        }, TaskContinuationOptions.ExecuteSynchronously);
        return t;
    }

    private static Task<T> Then<T>(Task<T> t, Action<T> done)
    {
        t.ContinueWith(prev =>
        {
            if (!prev.IsCompletedSuccessfully) return;
            try { done(prev.Result); } catch (Exception e) { MainFile.Logger.Warn($"HexTracker: {e.Message}"); }
        }, TaskContinuationOptions.ExecuteSynchronously);
        return t;
    }

    // ---------------------------------------------------------------- which hex is acting

    /// <summary>Marks an enemy hex as "acting" for code that is not a normal hook (used by OptionalPatches).</summary>
    internal static string? EnterHex(string kind)
    {
        var prev = CurrentHex.Value;
        CurrentHex.Value = kind;
        return prev;
    }

    internal static void ExitHex(string? previous) => CurrentHex.Value = previous;

    public sealed class HexState { public string? Hex; public MegaCrit.Sts2.Core.Models.RelicModel? Rune; }

    public static void HookPrefix(object __instance, out HexState __state)
    {
        __state = default!;
        try
        {
        // While an enemy hex acts, no player rune is "acting": otherwise whatever the hex does (buffs, damage)
        // would be credited to the rune whose hook happened to be running.
        __state = new HexState { Hex = CurrentHex.Value, Rune = AutoTracker.SwapCurrent(null) };
        var kind = Traverse.Create(__instance).Property("Kind").GetValue();
        if (kind != null) CurrentHex.Value = kind.ToString();
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void HookFinalizer(HexState __state)
    {
        try
        {
        CurrentHex.Value = __state.Hex;
        AutoTracker.SwapCurrent(__state.Rune);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- effects on enemies

    public sealed class Snap { public string? Kind; public int Before; }

    public static void HealPre(Creature creature, out Snap? __state) =>
        __state = CurrentHex.Value != null && creature.IsEnemy ? new Snap { Kind = CurrentHex.Value, Before = (int)creature.CurrentHp } : null;

    public static void HealPost(Creature creature, Snap? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var s = __state;
        __result = Then(__result, () => Rec(s.Kind, AutoTracker.Heal, (int)creature.CurrentHp - s.Before, creature.CombatState));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void MaxHpPre(Creature creature, out Snap? __state) =>
        __state = CurrentHex.Value != null && creature.IsEnemy ? new Snap { Kind = CurrentHex.Value, Before = (int)creature.MaxHp } : null;

    public static void MaxHpPost(Creature creature, Snap? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var s = __state;
        __result = Then(__result, () => Rec(s.Kind, AutoTracker.MaxHp, (int)creature.MaxHp - s.Before, creature.CombatState));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void BlockPre(Creature creature, out string? __state) =>
        __state = CurrentHex.Value != null && creature.IsEnemy ? CurrentHex.Value : null;

    public static void BlockPost(Creature creature, string? __state, ref Task<decimal> __result)
    {
        try
        {
        if (__state == null) return;
        var kind = __state;
        __result = Then(__result, gained => Rec(kind, AutoTracker.Block, gained, creature.CombatState));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- damage to the player

    public static void DamagePre(out string? __state) => __state = CurrentHex.Value;

    public static void DamagePost(string? __state, ref Task<IEnumerable<DamageResult>> __result)
    {
        try
        {
        if (__state == null) return;
        var kind = __state;
        __result = Then(__result, results =>
        {
            if (results == null) return;
            foreach (var r in results)
                if (r.Receiver != null && !r.Receiver.IsEnemy)
                    Rec(kind, "Damage to you", r.BlockedDamage + r.UnblockedDamage, r.Receiver.CombatState);
        });
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- powers

    private static string PowerName(PowerModel p)
    {
        var n = p.GetType().Name;
        if (n.StartsWith("Hextech") && n.Length > 7) n = n[7..];
        if (n.EndsWith("Power") && n.Length > 5) n = n[..^5];
        return System.Text.RegularExpressions.Regex.Replace(n, "([a-z])([A-Z])", "$1 $2");
    }

    private static void RecPower(string kind, PowerModel power, Creature? target, decimal amount)
    {
        if (target == null || amount <= 0) return;
        if (power.GetType().Name == "HextechPlayerSlowPower") return; // mirror of Temporary Slow: show one Slow
        Rec(kind, (target.IsEnemy ? "Gained " : "Applied ") + AutoTracker.PowerName(power), amount, target.CombatState);
    }

    public static void PowerPre(out string? __state)
    {
        __state = default!;
        try
        {
        var cur = CurrentHex.Value;
        __state = InApply.Value && ApplyHex.Value == cur ? null : cur; // a hex's own nested applies count once
        InApply.Value = true;
        ApplyHex.Value = cur;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerPost(PowerModel power, Creature target, decimal amount, string? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var kind = __state;
        __result = Then(__result, () => RecPower(kind, power, target, amount));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerFinal()
    {
        InApply.Value = false;
        ApplyHex.Value = null;
    }

    public static void PowerModPre(out string? __state) => __state = InApply.Value && ApplyHex.Value == CurrentHex.Value ? null : CurrentHex.Value;

    public static void PowerModPost(PowerModel power, decimal offset, string? __state, ref Task<int> __result)
    {
        try
        {
        if (__state == null) return;
        var kind = __state;
        __result = Then(__result, _ => RecPower(kind, power, power.Owner, offset));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- status cards added to your deck

    private static readonly AsyncLocal<bool> InCardAdd = new();

    private static string CardLabel(CardModel card)
    {
        var n = card.GetType().Name;
        if (n.StartsWith("Hextech") && n.Length > 7) n = n[7..];
        return System.Text.RegularExpressions.Regex.Replace(n, "([a-z])([A-Z])", "$1 $2") + " added";
    }

    public static void CardAddPre(object[] __args, out string? __state)
    {
        __state = default!;
        try
        {
        __state = null;
        if (InCardAdd.Value) return; // CardPileCmd calling itself: count once
        InCardAdd.Value = true;
        __state = "x";
        var kind = CurrentHex.Value;
        if (kind == null) return;
        try
        {
            foreach (var a in __args)
            {
                if (a is CardModel one) Rec(kind, CardLabel(one), 1, one.Owner?.Creature?.CombatState);
                else if (a is System.Collections.ICollection many) // never walk lazy lists owned by the game
                    foreach (var o in many)
                        if (o is CardModel c) Rec(kind, CardLabel(c), 1, c.Owner?.Creature?.CombatState);
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"HexTracker card count failed: {e.Message}"); }
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void CardAddFinal(string? __state)
    {
        try
        {
        if (__state != null) InCardAdd.Value = false;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- gold, transforms, downgrades, repeated moves

    public sealed class GoldSnap { public string? Kind; public int Before; }

    public static void GoldLossPre(Player player, out GoldSnap? __state)
    {
        __state = null;
        try { if (CurrentHex.Value != null && player != null) __state = new GoldSnap { Kind = CurrentHex.Value, Before = (int)player.Gold }; }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void GoldLossPost(Player player, GoldSnap? __state, ref Task __result)
    {
        try
        {
            if (__state == null) return;
            var s = __state;
            __result = Then(__result, () => Rec(s.Kind, "Gold lost", s.Before - (int)player.Gold, CombatKey));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    private static readonly AsyncLocal<bool> InTransform = new();

    public static void TransformPre(object[] __args, out string? __state)
    {
        __state = null;
        try
        {
            if (InTransform.Value) return; // Transform calls itself: count once
            InTransform.Value = true;
            __state = "x";
            var kind = CurrentHex.Value;
            if (kind == null) return;
            int n = 0;
            foreach (var a in __args)
            {
                if (a is CardModel) { n = 1; break; }
                if (a is System.Collections.ICollection many) { n = many.Count; break; } // never walk lazy lists owned by the game
            }
            Rec(kind, "Cards transformed", n, CombatKey);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void TransformFinal(string? __state)
    {
        try { if (__state != null) InTransform.Value = false; }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    private static readonly AsyncLocal<bool> InDowngrade = new();

    public static void DowngradePre(out string? __state)
    {
        __state = null;
        try
        {
            if (InDowngrade.Value) return;
            InDowngrade.Value = true;
            __state = "x";
            Rec(CurrentHex.Value, "Cards downgraded", 1, CombatKey);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void DowngradeFinal(string? __state)
    {
        try { if (__state != null) InDowngrade.Value = false; }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void JeweledLogPre(string tag, string message)
    {
        try
        {
            if (tag == "JeweledGauntlet" && message != null && message.Contains("repeating move"))
                Rec("JeweledGauntlet", "Moves repeated", 1, CombatKey);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- tooltip

    public static void TipsPost(object __0, ref IEnumerable<IHoverTip> __result)
    {
        try
        {
            var kind = __0?.ToString();
            if (kind == null || RelicStatsRegistry.Get(IdPrefix + kind) is not DynamicRuneStats s) return;
            var text = s.GetDescription(0, 0);
            if (string.IsNullOrEmpty(text)) return;
            object box = default(HoverTip);
            AccessTools.Property(typeof(HoverTip), "Title").SetValue(box, "Stats");
            AccessTools.Property(typeof(HoverTip), "Description").SetValue(box, text);
            var list = __result.ToList();
            list.Add((IHoverTip)(HoverTip)box);
            __result = list;
        }
        catch (Exception e) { MainFile.Logger.Warn($"HexTracker tooltip failed: {e.Message}"); }
    }
}
