using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using RelicStats.Core;

namespace HextechRelicStats;

/// <summary>
/// Generic tracker for every Hextech rune. It remembers which rune's hook is currently running
/// (an AsyncLocal that survives awaits) and records the real result of the game's own commands
/// (heal, max HP, block, energy, gold, draw, damage, powers) against that rune.
/// </summary>
public static class AutoTracker
{
    // The rune whose hook is currently executing (flows through awaits).
    private static readonly AsyncLocal<RelicModel?> Current = new();
    // True while inside CreatureCmd.GainMaxHp (it heals internally; that heal is not "healing").
    private static readonly AsyncLocal<bool> InMaxHp = new();
    // True while inside PowerCmd.Apply (it may call ModifyAmount; don't count twice).
    private static readonly AsyncLocal<bool> InPowerApply = new();
    private static readonly AsyncLocal<RelicModel?> ApplyRune = new();

    private static readonly string[] HookPrefixes = { "Before", "After", "On" };

    public const string Heal = "HP healed", MaxHp = "Max HP gained", Block = "Block gained",
        Energy = "Energy gained", Gold = "Gold gained", Stars = "Stars gained",
        Cards = "Cards drawn", Damage = "Damage dealt", BonusDamage = "Bonus damage", HpLost = "HP lost",
        Triggers = "Times triggered", BonusPower = "Bonus power stacks", PowerReduced = "Power stacks reduced", Prevented = "Damage prevented",
        ExtraHpLost = "Extra HP lost", BonusBlock = "Bonus Block", ExtraPlays = "Extra cards played", EnergySaved = "Energy cost reduced",
        StarsSaved = "Star cost reduced";

    // "Real" game moments (not tooltips/previews) in which passive modifier hooks are counted.
    private static readonly AsyncLocal<bool> InRealDamage = new(), InRealBlock = new(), InHandDraw = new(),
        InEnergyReset = new(), InPlayCount = new(), InEnergyGain = new(), InCounted = new();
    private static readonly AsyncLocal<HashSet<string>?> SpendSeen = new();

    // passive modifier hook name -> postfix that measures what it changed
    private static readonly Dictionary<string, string> PassiveHooks = new()
    {
        ["ModifyDamageAdditiveCompat"] = nameof(DmgAddPost),
        ["ModifyDamageMultiplicativeCompat"] = nameof(DmgMulPost),
        ["ModifyBlockAdditive"] = nameof(BlockAddPost),
        ["TryModifyPowerAmountReceived"] = nameof(PowerRecvPost),
        ["ModifyPowerAmountGivenMultiplicative"] = nameof(PowerGivenMulPost),
        ["ModifyPowerAmountGivenAdditive"] = nameof(PowerGivenAddPost),
        ["ModifyHpLostAfterOsty"] = nameof(HpLostModPost),
        ["ModifyEnergyGain"] = nameof(EnergyGainModPost),
        ["ModifyBlockMultiplicative"] = nameof(BlockMulPost),
        ["ModifyHandDraw"] = nameof(HandDrawPost),
        ["ModifyMaxEnergy"] = nameof(MaxEnergyPost),
        ["ModifyCardPlayCount"] = nameof(PlayCountPost),
        ["TryModifyEnergyCostInCombat"] = nameof(EnergyCostPost),
        ["TryModifyEnergyCostInCombatLate"] = nameof(EnergyCostPost),
        ["TryModifyStarCost"] = nameof(StarCostPost),
    };

    /// <summary>Creates a stats object per rune and wraps every rune hook. Returns the stats to register.</summary>
    public static List<IRelicStats> Install(Harmony harmony, ISet<string> alreadyTracked, string[] assemblies, bool globalPatches)
    {
        var sw = Stopwatch.StartNew();
        var stats = new List<IRelicStats>();
        var methods = new HashSet<MethodBase>();
        var powerMethods = new HashSet<MethodBase>();
        var passives = new HashSet<MethodBase>();

        foreach (var asmName in assemblies)
        {
            foreach (var type in SafeTypes(Hex.Find(asmName)))
            {
                if (typeof(PowerModel).IsAssignableFrom(type) && !type.IsAbstract && !type.ContainsGenericParameters)
                    CollectHooks(type, typeof(PowerModel), powerMethods);
                if (!typeof(RelicModel).IsAssignableFrom(type)) continue;

                if (!type.IsAbstract && (type.Name.EndsWith("Rune") || type.Name.EndsWith("Hex") || type.Name == "FortuneForge"))
                {
                    var id = StringHelper.Slugify(type.Name);
                    if (!alreadyTracked.Contains(id))
                    {
                        var expected = ExpectedOverride.TryGetValue(type.Name, out var ov) ? ov
                            : type.Name.EndsWith("UpgradeRune") ? Array.Empty<string>() // card-tweak runes: nothing worth counting
                            : MetricTable.ByClass.GetValueOrDefault(type.Name);
                        stats.Add(new DynamicRuneStats(id, expected)
                        {
                            NonCombat = MetricTable.NonCombat.Contains(type.Name) || type.Name == "BloodIdolRune",
                            ShowOnly = expected != null && expected.Length > 0, // known effects only; unknown runes show what was recorded
                            OnceOnly = MetricTable.OncePerCombat.Contains(type.Name) || type.Name == "FortuneForge",
                            LivePower = LivePowers.GetValueOrDefault(type.Name).Item1,
                            LiveLabel = LivePowers.GetValueOrDefault(type.Name).Item2,
                            PctDamage = DeclaresOverride(type, "ModifyDamageMultiplicativeCompat"),
                            PctBlock = DeclaresOverride(type, "ModifyBlockMultiplicative"),
                            Hidden = HiddenRunes.Contains(type.Name),
                            HidePct = HidePctRunes.Contains(type.Name),
                        });
                    }
                }

                // Walk the base chain of each concrete rune so hooks declared on generic bases are
                // patched through their closed (real) types.
                if (type.IsAbstract || type.ContainsGenericParameters) continue;
                if (type.Name.EndsWith("UpgradeRune")) continue; // card-tweak runes: untouched
                CollectHooks(type, typeof(RelicModel), methods);
                CollectPassives(type, passives);
            }
        }

        int ok = 0, failed = 0;
        var prefix = new HarmonyMethod(typeof(AutoTracker), nameof(HookPrefix));
        var finalizer = new HarmonyMethod(typeof(AutoTracker), nameof(HookFinalizer));
        foreach (var m in methods)
        {
            try { harmony.Patch(m, prefix: prefix, finalizer: finalizer); ok++; }
            catch (Exception e) { failed++; MainFile.Logger.Warn($"AutoTracker: could not wrap {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }

        var pprefix = new HarmonyMethod(typeof(AutoTracker), nameof(PowerHookPrefix));
        var pfinalizer = new HarmonyMethod(typeof(AutoTracker), nameof(HookFinalizer));
        int pok = 0;
        foreach (var m in powerMethods)
        {
            try { harmony.Patch(m, prefix: pprefix, finalizer: pfinalizer); pok++; }
            catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: could not wrap power {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }

        int pasOk = 0;
        foreach (var m in passives)
        {
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(AutoTracker), PassiveHooks[m.Name]));
                pasOk++;
            }
            catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: could not wrap passive {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); }
        }

        foreach (var asmName in assemblies) OptionalPatches.InstallFor(harmony, asmName);

        int fx = globalPatches ? InstallEffects(harmony) : 0;

        MainFile.Logger.Info($"AutoTracker: {stats.Count} runes, wrapped {ok} rune hooks ({failed} failed) + {pok} power hooks + {pasOk} passive modifiers, {fx} effect patches, {sw.ElapsedMilliseconds} ms");
        return stats;
    }

    private static int InstallEffects(Harmony harmony)
    {
        int fx = 0;
        fx += TryPatch(harmony, AccessTools.Method(typeof(CreatureCmd), "Heal"), nameof(HealPre), nameof(HealPost));
        fx += TryPatch(harmony, AccessTools.Method(typeof(CreatureCmd), "SetCurrentHp"), nameof(SetHpPre), nameof(SetHpPost));
        fx += TryPatch(harmony, AccessTools.Method(typeof(CreatureCmd), "GainMaxHp"), nameof(MaxHpPre), nameof(MaxHpPost), nameof(MaxHpFinal));
        fx += TryPatch(harmony, FindOverload(typeof(CreatureCmd), "GainBlock", "amount"), nameof(BlockPre), nameof(BlockPost), nameof(BlockFinal));
        fx += TryPatch(harmony, AccessTools.Method(typeof(PlayerCmd), "GainEnergy"), nameof(EnergyPre), nameof(EnergyPost), nameof(EnergyFinal));
        fx += TryPatch(harmony, AccessTools.Method(typeof(PlayerCmd), "GainGold"), nameof(GoldPre), nameof(GoldPost));
        fx += TryPatch(harmony, AccessTools.Method(typeof(PlayerCmd), "GainStars"), nameof(StarsPre), nameof(StarsPost));
        fx += TryPatch(harmony, FindOverload(typeof(CardPileCmd), "Draw", "count"), nameof(DrawPre), nameof(DrawPost), nameof(DrawFinal));
        fx += TryPatch(harmony, FindOverload(typeof(CreatureCmd), "Damage", "dealer", "cardPlay", "targets"), nameof(DamagePre), nameof(DamagePost), nameof(DamageFinal));
        fx += TryPatch(harmony, FindOverload(typeof(PowerCmd), "Apply", "power", "target"), nameof(PowerApplyPre), nameof(PowerApplyPost), nameof(PowerApplyFinal));
        fx += TryPatch(harmony, AccessTools.Method(typeof(PowerCmd), "ModifyAmount"), nameof(PowerModPre), nameof(PowerModPost));

        try
        {
            var show = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet), "CreateAndShow",
                new[] { typeof(Godot.Control), typeof(IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>), typeof(MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment) });
            harmony.Patch(show, postfix: new HarmonyMethod(typeof(AutoTracker), nameof(TooltipOnTop)));
            fx++;
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: tooltip layering patch failed: {e.Message}"); }

        try
        {
            var hook = typeof(MegaCrit.Sts2.Core.Hooks.Hook);
            harmony.Patch(AccessTools.Method(hook, "ModifyDamage"), postfix: new HarmonyMethod(typeof(AutoTracker), nameof(HookDamageFinalPost)));
            harmony.Patch(AccessTools.Method(hook, "ModifyBlock"), postfix: new HarmonyMethod(typeof(AutoTracker), nameof(HookBlockFinalPost)));
            fx += 2;
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: final-amount patch failed: {e.Message}"); }

        try
        {
            var tipsGetter = AccessTools.PropertyGetter(typeof(RelicModel), "HoverTips");
            harmony.Patch(tipsGetter, prefix: new HarmonyMethod(typeof(AutoTracker), nameof(HoverTipsPrefix)));
            // After Relic Stats has added its box (it runs at normal priority), tidy it.
            harmony.Patch(tipsGetter, postfix: new HarmonyMethod(typeof(AutoTracker), nameof(StripFloor)) { priority = Priority.Last });
            fx++;
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: hover capture failed: {e.Message}"); }

        try
        {
            var stoke = Hex.Find("HextechRunes")?.GetType("HextechRunes.StokeRestSiteOption");
            var select = stoke == null ? null : AccessTools.Method(stoke, "OnSelect");
            if (select != null)
            {
                harmony.Patch(select, postfix: new HarmonyMethod(typeof(AutoTracker), nameof(StokeSelectPost)));
                fx++;
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: Stoke patch failed: {e.Message}"); }

        fx += PatchCounted(harmony);
        fx += TryFlag(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "ModifyHandDraw"), nameof(HandDrawFlagOn), nameof(HandDrawFlagOff));
        fx += TryFlag(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "ResetEnergy"), nameof(EnergyFlagOn), nameof(EnergyFlagOff));
        fx += TryFlag(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "AddMaxEnergyToCurrent"), nameof(EnergyFlagOn), nameof(EnergyFlagOff));
        fx += TryFlag(harmony, AccessTools.Method(typeof(CardModel), "GeneratePlayCount"), nameof(PlayCountFlagOn), nameof(PlayCountFlagOff));
        fx += TryFlag(harmony, AccessTools.Method(typeof(CardModel), "SpendResources"), nameof(SpendFlagOn), nameof(SpendFlagOff));

        return fx;
    }

    // ---------------------------------------------------------------- plumbing

    private static void CollectHooks(Type type, Type stopAt, ISet<MethodBase> into)
    {
        for (Type? t = type; t != null && t != stopAt && t != typeof(object); t = t.BaseType)
        {
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!m.IsVirtual || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                // Never patch methods on generic classes: .NET shares one compiled copy across type arguments and a
                // Harmony patch there loses the type argument (e.g. every "Upgrade: X" rune producing the same card).
                if (m.DeclaringType != null && (m.DeclaringType.IsGenericType || m.DeclaringType.ContainsGenericParameters)) continue;
                if (m.GetBaseDefinition().DeclaringType == t) continue; // only overrides of game hooks
                if (m.ReturnType != typeof(void) && m.ReturnType != typeof(Task)) continue;
                if (!HookPrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal)) && m.Name != "ApplySharedCombatVictory") continue;
                into.Add(m);
            }
        }
    }

    // Command -> (stat label, how to get the amount). "cards" = number of cards passed, "int" = first number argument, "one" = 1.
    private static readonly (string Type, string Method, string Key, string Mode)[] CountedCommands =
    {
        ("CardPileCmd", "Add", "Cards added", "cards"),
        ("CardPileCmd", "AddGeneratedCardToCombat", "Cards added", "cards"),
        ("CardPileCmd", "AddGeneratedCardsToCombat", "Cards added", "cards"),
        ("CardPileCmd", "RemoveFromDeck", "Cards removed from deck", "cards"),
        ("CardCmd", "Upgrade", "Cards upgraded", "cards"),
        ("CardCmd", "Transform", "Cards transformed", "cards"),
        ("CardCmd", "AutoPlay", "Cards auto-played", "cards"),
        ("CardCmd", "Exhaust", "Cards exhausted", "cards"),
        ("CardCmd", "Discard", "Cards discarded", "cards"),
        ("CardCmd", "Enchant", "Cards enchanted", "one"),
        ("OrbCmd", "Channel", "Orbs channeled", "one"),
        ("OrbCmd", "AddSlots", "Orb slots gained", "int"),
        ("PlayerCmd", "LoseGold", "Gold lost", "int"),
        ("CreatureCmd", "Stun", "Enemies stunned", "enemy"),
    };

    private static int PatchCounted(Harmony harmony)
    {
        int ok = 0;
        var asm = typeof(CreatureCmd).Assembly;
        foreach (var (typeName, method, _, _) in CountedCommands)
        {
            var type = asm.GetType("MegaCrit.Sts2.Core.Commands." + typeName);
            if (type == null) { MainFile.Logger.Warn($"AutoTracker: {typeName} not found"); continue; }
            foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != method || m.IsGenericMethodDefinition) continue;
                try
                {
                    harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(AutoTracker), nameof(CountPre)),
                        finalizer: new HarmonyMethod(typeof(AutoTracker), nameof(CountFinal)));
                    ok++;
                }
                catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: could not count {typeName}.{method}: {e.Message}"); }
            }
        }
        return ok;
    }

    public static void CountPre(MethodBase __originalMethod, object[] __args, out bool __state)
    {
        __state = default!;
        try
        {
        __state = false;
        // Card commands call each other (Transform -> Add ...): count only the outermost one. Other commands
        // (relic pickup, orbs, gold) never take part in that guard, or they would hide everything done inside them.
        bool guards = __originalMethod.DeclaringType?.Name is "CardPileCmd" or "CardCmd";
        if (guards)
        {
            if (InCounted.Value) return; // nested inside another counted card command
            InCounted.Value = true;
            __state = true;
        }
        var rune = Current.Value;
        if (rune == null) return;
        try
        {
            var name = __originalMethod.Name;
            var typeName = __originalMethod.DeclaringType?.Name;
            foreach (var c in CountedCommands)
            {
                if (c.Method != name || c.Type != typeName) continue;
                decimal amount = 1;
                if (c.Mode == "cards")
                {
                    foreach (var a in __args)
                    {
                        // Only ask collections for their size. Walking a lazily built list here could run its
                        // side effects a second time inside the game.
                        if (a is System.Collections.ICollection col) { amount = col.Count; break; }
                    }
                }
                else if (c.Mode == "int")
                {
                    foreach (var a in __args)
                    {
                        if (a is int i) { amount = i; break; }
                        if (a is decimal d) { amount = d; break; }
                    }
                }
                else if (c.Mode == "enemy")
                {
                    var target = __args.OfType<Creature>().FirstOrDefault();
                    if (target == null || !target.IsEnemy) return;
                }
                Record(rune, c.Key, amount);
                return;
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker count failed: {e.Message}"); }
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void CountFinal(bool __state)
    {
        try
        {
        if (__state) InCounted.Value = false;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // Draw hover tooltips above floating combat numbers and other overlays.
    public static void TooltipOnTop(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet? __result, IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>? __1)
    {
        try
        {
            if (__result == null || __1 == null) return;
            // Leave every tooltip that does not carry one of our Stats boxes completely alone.
            bool ours = false;
            foreach (var tip in __1)
            {
                if (tip is MegaCrit.Sts2.Core.HoverTips.HoverTip ht
                    && Traverse.Create(ht).Property("Title").GetValue()?.ToString() == "Stats") { ours = true; break; }
            }
            if (!ours) return;
            __result.ZAsRelative = false;
            __result.ZIndex = 4000;
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: tooltip layering failed: {e.Message}"); }
    }

    // Runes whose generated metric list is replaced by hand.
    private static readonly Dictionary<string, string[]> ExpectedOverride = new()
    {
        // Former hand-written counters, now shown as "this combat / total" like everything else.
        ["SlapRune"] = new[] { "Gained Strength" },
        ["BadTasteRune"] = new[] { Heal },
        ["BloodthirstRune"] = new[] { Heal },
        ["BloodIdolRune"] = new[] { Heal },
        ["GastritisRune"] = new[] { Heal, Cards },
        ["RegretRune"] = new[] { "Revives used" },
        ["WatcherHexDailyMantraRune"] = new[] { "Gained Mantra" },
        ["WatcherHexMantraFistRune"] = new[] { "Gained Mantra" },
        ["WatcherHexSecondStanceRune"] = new[] { "Gained Mantra" },
        ["WatcherHexCalmSurgeRune"] = new[] { Energy },
        ["WatcherHexWrathStrengthRune"] = new[] { "Gained Temporary Strength" },
        ["WatcherHexCalmDexterityRune"] = new[] { "Gained Temporary Dexterity" },
        ["WatcherHexMiracleUpgradeRune"] = new[] { Cards },
        ["WatcherHexIronStanceRune"] = new[] { Block },
        ["WatcherHexDivineEchoRune"] = new[] { Damage },
        ["FortuneForge"] = new[] { "Extra gold earned" },
        ["PrismaticEggRune"] = new[] { "Silver augments", "Gold augments", "Prismatic augments" },
        ["DeathWarrantRune"] = new[] { Damage },
        ["StokeRune"] = new[] { "Cards removed" },
    };

    // Runes that grant a temporary power: also show how much of it you have right now.
    private static readonly Dictionary<string, (string, string)> LivePowers = new()
    {
        ["WatcherHexCalmDexterityRune"] = ("WatcherHexCalmDexterityPower", "Current temporary Dexterity"),
        ["WatcherHexWrathStrengthRune"] = ("WatcherHexWrathStrengthPower", "Current temporary Strength"),
    };

    private static bool InheritsFrom(Type type, string baseName)
    {
        for (Type? t = type.BaseType; t != null; t = t.BaseType)
            if (t.Name == baseName) return true;
        return false;
    }

    private static bool DeclaresOverride(Type type, string method)
    {
        for (Type? t = type; t != null && t.Name != "HextechRelicBase" && t != typeof(RelicModel) && t != typeof(object); t = t.BaseType)
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (m.Name == method && m.IsVirtual && m.GetBaseDefinition().DeclaringType != t) return true;
        return false;
    }

    // The relic whose tooltip is being built right now (set just before Relic Stats asks for its description).
    internal static RelicModel? HoveredRelic;

    /// <summary>Removes the "Floor obtained: N" line Relic Stats adds, for ARAM Mayhem runes only.</summary>
    public static void StripFloor(RelicModel __instance, ref IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip> __result)
    {
        try
        {
            var asm = __instance.GetType().Assembly.GetName().Name;
            if (asm != "HextechRunes" && asm != "HextechRunesSponsorPack" && asm != "HextechWatcherPack") return;
            if (__instance.IsCanonical || __instance.Owner == null) return;
            var list = __result.ToList();
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not MegaCrit.Sts2.Core.HoverTips.HoverTip ht) continue;
                if (Convert.ToString(Traverse.Create(ht).Property("Title").GetValue()) != "Stats") continue;
                var desc = Convert.ToString(Traverse.Create(ht).Property("Description").GetValue()) ?? "";
                if (!desc.StartsWith("Floor obtained")) continue;
                int nl = desc.IndexOf('\n');
                var rest = nl >= 0 ? desc[(nl + 1)..] : "";
                changed = true;
                if (string.IsNullOrWhiteSpace(rest)) { list.RemoveAt(i); i--; continue; }
                object box = default(MegaCrit.Sts2.Core.HoverTips.HoverTip);
                AccessTools.Property(typeof(MegaCrit.Sts2.Core.HoverTips.HoverTip), "Title").SetValue(box, "Stats");
                AccessTools.Property(typeof(MegaCrit.Sts2.Core.HoverTips.HoverTip), "Description").SetValue(box, rest);
                list[i] = (MegaCrit.Sts2.Core.HoverTips.IHoverTip)(MegaCrit.Sts2.Core.HoverTips.HoverTip)box;
            }
            if (changed) __result = list;
        }
        catch (Exception e) { Swallow(e); }
    }

    public static void HoverTipsPrefix(RelicModel __instance) => HoveredRelic = __instance;

    /// <summary>Current damage/Block multiplier of a rune, asked from the rune itself. Null if it cannot be asked right now.</summary>
    internal static decimal? ProbeMultiplier(RelicModel relic, bool damage)
    {
        try
        {
            var owner = relic.Owner?.Creature;
            if (owner == null) return null;
            Creature? target = damage ? owner.CombatState?.HittableEnemies?.FirstOrDefault() : owner;
            if (target == null) return null;
            var m = relic.GetType().GetMethod(damage ? "ModifyDamageMultiplicativeCompat" : "ModifyBlockMultiplicative",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return null;
            var ps = m.GetParameters();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt == typeof(decimal)) args[i] = 100m;
                else if (pt.IsEnum) args[i] = Enum.ToObject(pt, 0);
                else if (pt == typeof(Creature)) args[i] = i == 0 ? target : owner;
                else args[i] = null;
            }
            return m.Invoke(relic, args) as decimal?;
        }
        catch { return null; }
    }

    // Rest-site "remove a card" option of the Stoke rune: count each card actually removed.
    public static void StokeSelectPost(object __instance, ref Task<bool> __result)
    {
        try
        {
        var option = __instance;
        __result = Then(__result, removed =>
        {
            if (!removed) return;
            var owner = Traverse.Create(option).Property("Owner").GetValue<Player>();
            var relic = owner?.Relics.FirstOrDefault(r => r.Id.Entry == "STOKE_RUNE");
            Record(relic, "Cards removed", 1);
        });
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    private static void CollectPassives(Type type, ISet<MethodBase> into)
    {
        for (Type? t = type; t != null && t != typeof(RelicModel) && t != typeof(object); t = t.BaseType)
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!m.IsVirtual || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                // Never patch methods on generic classes: .NET shares one compiled copy across type arguments and a
                // Harmony patch there loses the type argument (e.g. every "Upgrade: X" rune producing the same card).
                if (m.DeclaringType != null && (m.DeclaringType.IsGenericType || m.DeclaringType.ContainsGenericParameters)) continue;
                if (!PassiveHooks.ContainsKey(m.Name)) continue;
                into.Add(m);
            }
    }

    private static int TryFlag(Harmony harmony, MethodBase? target, string on, string off)
    {
        try
        {
            if (target == null) throw new InvalidOperationException("method not found");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(AutoTracker), on), finalizer: new HarmonyMethod(typeof(AutoTracker), off));
            return 1;
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: flag patch {on} failed: {e.Message}"); return 0; }
    }

    public static void HandDrawFlagOn() => InHandDraw.Value = true;
    public static void HandDrawFlagOff() => InHandDraw.Value = false;
    public static void EnergyFlagOn() => InEnergyReset.Value = true;
    public static void EnergyFlagOff() => InEnergyReset.Value = false;
    public static void PlayCountFlagOn() => InPlayCount.Value = true;
    public static void PlayCountFlagOff() => InPlayCount.Value = false;
    public static void SpendFlagOn() => SpendSeen.Value = new HashSet<string>();
    public static void SpendFlagOff() => SpendSeen.Value = null;

    // Postfixes use positional args (__1 ...) so they don't depend on the rune's parameter names.
    // A boost is queued with the amount it contributed and the running total right after it. When the whole
    // hit is resolved, each boost is scaled by what later modifiers did (Weak, Frail ...) and by how much of
    // the hit actually landed (Slippery, block, overkill), so only the effective bonus is counted.
    public sealed class BonusCtx
    {
        public readonly Dictionary<Creature, decimal> Final = new();
        public readonly List<(RelicModel Rune, Creature? Target, decimal Extra, decimal Out)> Entries = new();
    }

    public sealed class BonusState { public RelicModel? Rune; public BonusCtx? Prev; public BonusCtx Ctx = new(); }

    private static readonly AsyncLocal<BonusCtx?> DmgCtxLocal = new(), BlockCtxLocal = new();

    public static void DmgAddPost(object __instance, Creature? __0, decimal __1, decimal __result)
    {
        try
        {
        var ctx = DmgCtxLocal.Value;
        if (ctx != null && __result > 0 && __instance is RelicModel r) ctx.Entries.Add((r, __0, __result, __1 + __result));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void DmgMulPost(object __instance, Creature? __0, decimal __1, decimal __result)
    {
        try
        {
        var ctx = DmgCtxLocal.Value;
        if (ctx != null && __result > 1 && __instance is RelicModel r)
        {
            ctx.Entries.Add((r, __0, __1 * (__result - 1), __1 * __result));
            if (RelicStatsRegistry.Get(r.Id.Entry) is DynamicRuneStats ds) ds.LastDmgMult = __result;
        }
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void BlockAddPost(object __instance, Creature? __0, decimal __1, decimal __result)
    {
        try
        {
        var ctx = BlockCtxLocal.Value;
        if (ctx != null && __result > 0 && __instance is RelicModel r) ctx.Entries.Add((r, __0, __result, __1 + __result));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void BlockMulPost(object __instance, Creature? __0, decimal __1, decimal __result)
    {
        try
        {
        var ctx = BlockCtxLocal.Value;
        if (ctx != null && __result > 1 && __instance is RelicModel r)
        {
            ctx.Entries.Add((r, __0, __1 * (__result - 1), __1 * __result));
            if (RelicStatsRegistry.Get(r.Id.Entry) is DynamicRuneStats ds) ds.LastBlockMult = __result;
        }
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // The final amount the game settled on for each target (after every modifier).
    public static void HookDamageFinalPost(Creature? __2, decimal __result)
    {
        try
        {
        var ctx = DmgCtxLocal.Value;
        if (ctx != null && __2 != null) ctx.Final[__2] = __result;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void HookBlockFinalPost(Creature? __1, decimal __result)
    {
        try
        {
        var ctx = BlockCtxLocal.Value;
        if (ctx != null && __1 != null) ctx.Final[__1] = __result;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerRecvPost(object __instance, decimal __2, decimal __4, bool __result)
    {
        try
        {
        if (!InPowerApply.Value || !__result) return;
        if (__4 > __2) Record(__instance as RelicModel, BonusPower, __4 - __2);
        else if (__4 < __2) Record(__instance as RelicModel, PowerReduced, __2 - __4);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerGivenMulPost(object __instance, decimal __2, decimal __result)
    {
        try
        {
        if (InPowerApply.Value && __result > 1) Record(__instance as RelicModel, BonusPower, __2 * (__result - 1));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerGivenAddPost(object __instance, decimal __result)
    {
        try
        {
        if (InPowerApply.Value && __result > 0) Record(__instance as RelicModel, BonusPower, __result);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void HpLostModPost(object __instance, decimal __1, decimal __result)
    {
        try
        {
        if (!InRealDamage.Value) return;
        if (__result < __1) Record(__instance as RelicModel, Prevented, __1 - __result);
        else if (__result > __1) Record(__instance as RelicModel, ExtraHpLost, __result - __1);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void EnergyGainModPost(object __instance, decimal __1, decimal __result)
    {
        try
        {
        if (InEnergyGain.Value && __result > __1) Record(__instance as RelicModel, Energy, __result - __1);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void HandDrawPost(object __instance, decimal __1, decimal __result)
    {
        try
        {
        if (InHandDraw.Value && __result > __1) Record(__instance as RelicModel, Cards, __result - __1);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void MaxEnergyPost(object __instance, decimal __1, decimal __result)
    {
        try
        {
        if (InEnergyReset.Value && __result > __1) Record(__instance as RelicModel, Energy, __result - __1);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PlayCountPost(object __instance, int __2, int __result)
    {
        try
        {
        if (InPlayCount.Value && __result > __2) Record(__instance as RelicModel, ExtraPlays, __result - __2);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void EnergyCostPost(object __instance, decimal __1, decimal __2, bool __result)
    {
        try
        {
        var seen = SpendSeen.Value;
        if (seen == null || !__result || __2 >= __1 || __instance is not RelicModel r) return;
        if (seen.Add(r.Id.Entry + "/e")) Record(r, EnergySaved, __1 - __2);
        if (seen.Add(r.Id.Entry + "/t")) Record(r, Triggers, 1);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void StarCostPost(object __instance, decimal __1, decimal __2, bool __result)
    {
        try
        {
        var seen = SpendSeen.Value;
        if (seen == null || !__result || __2 >= __1 || __instance is not RelicModel r) return;
        if (seen.Add(r.Id.Entry + "/t")) Record(r, Triggers, 1);
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    private sealed class RuneHolder { public RelicModel? Rune; }
    // Which rune is responsible for a power instance (so later effects of that power count for the rune).
    private static readonly ConditionalWeakTable<PowerModel, RuneHolder> PowerSource = new();

    /// <summary>The rune behind a power: explicit source if known, else HextechFooPower -> owner's FooRune/FooHex.</summary>
    private static RelicModel? RuneForPower(PowerModel power)
    {
        if (PowerSource.TryGetValue(power, out var h) && h.Rune != null) return h.Rune;
        var relics = power.Owner?.Player?.Relics;
        if (relics == null) return null;
        var type = power.GetType();
        if (!PowerRuneIds.TryGetValue(type, out var ids))
        {
            var n = type.Name;
            if (n.StartsWith("Hextech")) n = n["Hextech".Length..];
            if (n.EndsWith("Power")) n = n[..^5];
            ids = (StringHelper.Slugify(n + "Rune"), StringHelper.Slugify(n + "Hex"));
            PowerRuneIds[type] = ids;
        }
        foreach (var r in relics)
        {
            var id = r.Id.Entry;
            if (id == ids.Item1 || id == ids.Item2) return r;
        }
        return null;
    }

    // Cache of power type -> candidate rune IDs (Slugify uses regexes; this runs in hot paths).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (string, string)> PowerRuneIds = new();

    private static void Tag(PowerModel power, RelicModel rune) => PowerSource.GetOrCreateValue(power).Rune = rune;

    public static void PowerHookPrefix(object __instance, out RelicModel? __state)
    {
        __state = default!;
        try
        {
        __state = Current.Value;
        if (__instance is PowerModel p && RuneForPower(p) is { } r && !r.IsCanonical) Current.Value = r;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    private static int _swallowed;

    /// <summary>A bug in this mod must never reach the game: log the first few and carry on.</summary>
    internal static void Swallow(Exception e)
    {
        if (System.Threading.Interlocked.Increment(ref _swallowed) <= 10)
            MainFile.Logger.Warn($"Relic Stats Hextech: ignored internal error: {e.GetType().Name}: {e.Message}");
    }

    internal static RelicModel? CurrentRune => Current.Value;

    /// <summary>Set the "rune currently acting" and return the previous one (used to stop other systems being credited).</summary>
    internal static RelicModel? SwapCurrent(RelicModel? rune)
    {
        var prev = Current.Value;
        Current.Value = rune;
        return prev;
    }

    internal static IEnumerable<Type> SafeTypesOf(Assembly? asm) => SafeTypes(asm);

    internal static MethodBase? FindOverloadOf(Type type, string name, params string[] mustHaveParams) =>
        FindOverload(type, name, mustHaveParams);

    private static IEnumerable<Type> SafeTypes(Assembly? asm)
    {
        if (asm == null) return Array.Empty<Type>();
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }

    private static MethodBase? FindOverload(Type type, string name, params string[] mustHaveParams) =>
        AccessTools.GetDeclaredMethods(type)
            .Where(m => m.Name == name && !m.IsGenericMethodDefinition)
            .Where(m => mustHaveParams.All(p => m.GetParameters().Any(x => x.Name == p)))
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault();

    private static int TryPatch(Harmony harmony, MethodBase? target, string pre, string post, string? fin = null)
    {
        try
        {
            if (target == null) throw new InvalidOperationException("method not found");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(AutoTracker), pre),
                postfix: new HarmonyMethod(typeof(AutoTracker), post),
                finalizer: fin == null ? null : new HarmonyMethod(typeof(AutoTracker), fin));
            return 1;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"AutoTracker: effect patch {post} failed: {e.Message}");
            return 0;
        }
    }

    // Temporary diagnostics: logs the first few events so we can see where tracking gets lost.
    private static int _diagBudget = DiagEnabled() ? 300 : 0;

    private static bool DiagEnabled()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return dir != null && System.IO.File.Exists(System.IO.Path.Combine(dir, "diag.txt"));
        }
        catch { return false; }
    }

    private static readonly HashSet<string> _diagSeen = new();
    internal static bool DiagOn => _diagBudget > 0;
    internal static void Diag(string msg)
    {
        if (_diagBudget <= 0) return;
        if (!_diagSeen.Add(msg)) return;
        _diagBudget--;
        MainFile.Logger.Info("AutoTracker diag: " + msg);
    }

    public static void HookPrefix(object __instance, out RelicModel? __state)
    {
        __state = default!;
        try
        {
        __state = Current.Value;
        if (__instance is RelicModel r && !r.IsCanonical)
        {
            Current.Value = r;
            if (DiagOn) Diag($"hook fired on {r.Id.Entry}");
        }
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void HookFinalizer(RelicModel? __state) => Current.Value = __state;

    internal static readonly HashSet<string> HiddenRunes = new() { "BigKnifeRune", "SolidTimeRune" };
    internal static readonly HashSet<string> HidePctRunes = new() { "DualWieldRune", "FirstAidKitRune" };

    // Blossom Blade heals a TEAMMATE, not its owner.
    private static bool TeammateHeal(RelicModel? rune, Creature? c) =>
        rune != null && c != null && !c.IsEnemy && rune.GetType().Name == "BlossomBladeRune";

    /// <summary>Records a gain for the rune whose hook is running, if it is ours and trackable.</summary>
    internal static void Record(RelicModel? rune, string key, decimal amount)
    {
        try
        {
            if (rune == null) return;
            if (DiagOn) Diag($"effect '{key}' {amount} for {rune.Id.Entry} (melted={rune.IsMelted}, mine={LocalContext.IsMine(rune)})");
            if (amount <= 0 || rune.IsMelted || !LocalContext.IsMine(rune)) return;
            var id = rune.Id.Entry;
            if (RelicStatsRegistry.Get(id) is not DynamicRuneStats s) return; // only things registered at startup are tracked
            s.Add(key, amount, rune.Owner?.Creature?.CombatState);
        }
        catch (Exception e) { MainFile.Logger.Warn($"AutoTracker record failed: {e.Message}"); }
    }

    private static bool Mine(RelicModel? rune, Creature? c) => rune?.Owner?.Creature != null && ReferenceEquals(rune.Owner.Creature, c);
    private static bool Mine(RelicModel? rune, Player? p) => rune?.Owner != null && ReferenceEquals(rune.Owner, p);

    // Observe-only: attach a continuation to the game's own task and hand the ORIGINAL task straight back,
    // so the game (and other mods) see exactly the same Task object and timing as without this mod.
    private static Task Then(Task t, Action done)
    {
        t.ContinueWith(prev =>
        {
            if (!prev.IsCompletedSuccessfully) return;
            try { done(); } catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: {e.Message}"); }
        }, TaskContinuationOptions.ExecuteSynchronously);
        return t;
    }

    private static Task<T> Then<T>(Task<T> t, Action<T> done)
    {
        t.ContinueWith(prev =>
        {
            if (!prev.IsCompletedSuccessfully) return;
            try { done(prev.Result); } catch (Exception e) { MainFile.Logger.Warn($"AutoTracker: {e.Message}"); }
        }, TaskContinuationOptions.ExecuteSynchronously);
        return t;
    }

    public sealed class Snap { public RelicModel? Rune; public int Before; }

    // ---------------------------------------------------------------- heal / max HP

    public static void HealPre(Creature creature, out Snap? __state)
    {
        __state = default!;
        try
        {
        var rune = InMaxHp.Value ? null : Current.Value;
        __state = Mine(rune, creature) || TeammateHeal(rune, creature) ? new Snap { Rune = rune, Before = (int)creature.CurrentHp } : null;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void HealPost(Creature creature, Snap? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var s = __state;
        __result = Then(__result, () => Record(s.Rune, Heal, (int)creature.CurrentHp - s.Before));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // Runes that set HP directly (e.g. Cruelty: "lose 2 HP") bypass the damage command.
    public static void SetHpPre(Creature creature, out Snap? __state)
    {
        __state = default!;
        try
        {
        var rune = InMaxHp.Value ? null : Current.Value;
        __state = Mine(rune, creature) ? new Snap { Rune = rune, Before = (int)creature.CurrentHp } : null;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void SetHpPost(Creature creature, Snap? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var s = __state;
        __result = Then(__result, () => Record(s.Rune, HpLost, s.Before - (int)creature.CurrentHp));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void MaxHpPre(Creature creature, out Snap? __state)
    {
        __state = default!;
        try
        {
        var rune = Current.Value;
        InMaxHp.Value = true;
        __state = Mine(rune, creature) ? new Snap { Rune = rune, Before = (int)creature.MaxHp } : null;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void MaxHpPost(Creature creature, Snap? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var s = __state;
        __result = Then(__result, () => Record(s.Rune, MaxHp, (int)creature.MaxHp - s.Before));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void MaxHpFinal() => InMaxHp.Value = false;

    // ---------------------------------------------------------------- block / energy / gold / stars / draw

    public static void BlockPre(Creature creature, out BonusState __state)
    {
        __state = default!;
        try
        {
        __state = new BonusState { Rune = Mine(Current.Value, creature) ? Current.Value : null, Prev = BlockCtxLocal.Value };
        BlockCtxLocal.Value = __state.Ctx;
        InRealBlock.Value = true;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void BlockFinal(BonusState __state)
    {
        try
        {
        BlockCtxLocal.Value = __state.Prev;
        InRealBlock.Value = __state.Prev != null;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void BlockPost(BonusState __state, ref Task<decimal> __result)
    {
        try
        {
        var st = __state;
        __result = Then(__result, gained =>
        {
            if (st.Rune != null) Record(st.Rune, Block, gained);
            foreach (var e in st.Ctx.Entries)
            {
                decimal f = e.Target != null && st.Ctx.Final.TryGetValue(e.Target, out var ff) ? ff : e.Out;
                decimal scale = e.Out > 0 ? f / e.Out : 1;
                Record(e.Rune, BonusBlock, e.Extra * scale);
            }
        });
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void EnergyPre(decimal amount, Player player, out RelicModel? __state)
    {
        __state = default!;
        try
        {
        __state = Mine(Current.Value, player) ? Current.Value : null;
        InEnergyGain.Value = true;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void EnergyFinal() => InEnergyGain.Value = false;

    public static void EnergyPost(decimal amount, RelicModel? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var rune = __state;
        __result = Then(__result, () => Record(rune, Energy, amount));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void GoldPre(decimal amount, Player player, out RelicModel? __state) =>
        __state = Mine(Current.Value, player) ? Current.Value : null;

    public static void GoldPost(decimal amount, RelicModel? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var rune = __state;
        __result = Then(__result, () => Record(rune, Gold, amount));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void StarsPre(decimal amount, Player player, out RelicModel? __state) =>
        __state = Mine(Current.Value, player) ? Current.Value : null;

    public static void StarsPost(decimal amount, RelicModel? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var rune = __state;
        __result = Then(__result, () => Record(rune, Stars, amount));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void DrawPre(Player player, out RelicModel? __state)
    {
        __state = default!;
        try
        {
        __state = Mine(Current.Value, player) ? Current.Value : null;
        InCounted.Value = true; // drawing moves cards via CardPileCmd.Add; do not count that as "cards added"
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void DrawFinal() => InCounted.Value = false;

    public static void DrawPost(RelicModel? __state, ref Task<IEnumerable<CardModel>> __result)
    {
        try
        {
        if (__state == null) return;
        var rune = __state;
        __result = Then(__result, cards => Record(rune, Cards, cards?.Count() ?? 0));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- damage

    public static void DamagePre(out BonusState __state)
    {
        __state = default!;
        try
        {
        __state = new BonusState { Rune = Current.Value, Prev = DmgCtxLocal.Value };
        DmgCtxLocal.Value = __state.Ctx;
        InRealDamage.Value = true;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void DamageFinal(BonusState __state)
    {
        try
        {
        DmgCtxLocal.Value = __state.Prev;
        InRealDamage.Value = __state.Prev != null;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void DamagePost(BonusState __state, ref Task<IEnumerable<DamageResult>> __result)
    {
        try
        {
        var st = __state;
        __result = Then(__result, results =>
        {
            if (results == null) return;
            var list = results.ToList();
            var rune = st.Rune;
            if (rune != null)
            {
                int total = 0, selfHp = 0;
                foreach (var r in list)
                {
                    if (r.Receiver == null) continue;
                    if (r.Receiver.IsEnemy) total += r.BlockedDamage + r.UnblockedDamage;
                    else if (Mine(rune, r.Receiver)) selfHp += r.UnblockedDamage;
                }
                Record(rune, Damage, total);
                Record(rune, HpLost, selfHp);
            }

            // Passive damage boosts: keep only what really reached the target.
            foreach (var e in st.Ctx.Entries)
            {
                decimal f = e.Target != null && st.Ctx.Final.TryGetValue(e.Target, out var ff) ? ff : e.Out;
                decimal scale = e.Out > 0 ? f / e.Out : 1;
                decimal actual = 0;
                bool found = false;
                foreach (var r in list)
                {
                    if (e.Target != null && !ReferenceEquals(r.Receiver, e.Target)) continue;
                    actual += r.BlockedDamage + r.UnblockedDamage + r.OverkillDamage;
                    found = true;
                }
                if (!found) foreach (var r in list) actual += r.BlockedDamage + r.UnblockedDamage + r.OverkillDamage;
                decimal ratio = f > 0 ? Math.Min(1m, actual / f) : 1m;
                Record(e.Rune, BonusDamage, e.Extra * scale * ratio);
            }
        });
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    // ---------------------------------------------------------------- powers

    private static readonly Dictionary<string, string> PowerAlias = new()
    {
        ["WatcherHexCalmDexterityPower"] = "Temporary Dexterity",
        ["WatcherHexWrathStrengthPower"] = "Temporary Strength",
        ["HextechTemporarySlowPower"] = "Slow",
        ["HextechPlayerSlowPower"] = "Slow",
    };

    internal static string PowerName(PowerModel p)
    {
        var n = p.GetType().Name;
        if (PowerAlias.TryGetValue(n, out var alias)) return alias;
        if (n.StartsWith("Hextech") && n.Length > 7) n = n[7..];
        if (n.EndsWith("Power") && n.Length > 5) n = n[..^5];
        return Regex.Replace(n, "([a-z])([A-Z])", "$1 $2");
    }

    private static void RecordPower(RelicModel rune, PowerModel power, Creature? target, decimal amount)
    {
        if (target == null || amount <= 0) return;
        if (power.GetType().Name == "HextechPlayerSlowPower") return; // mirror of Temporary Slow: show one Slow
        bool self = Mine(rune, target) || (rune.Owner?.Creature != null && target.Side == rune.Owner.Creature.Side);
        Record(rune, (self ? "Gained " : "Applied ") + PowerName(power), amount);
    }

    public static void PowerApplyPre(PowerModel power, Creature target, decimal amount, out RelicModel? __state)
    {
        __state = default!;
        try
        {
        var cur = Current.Value ?? RuneForPower(power);
        if (cur != null) Tag(power, cur);
        // A power applying another power for the SAME rune counts once (Frost Wraith's Slow); a different rune
        // acting during the apply (e.g. a conversion rune reacting to your Dexterity) is its own effect.
        bool nestedSameRune = InPowerApply.Value && ReferenceEquals(ApplyRune.Value, cur);
        __state = nestedSameRune ? null : cur;
        if (DiagOn) Diag($"PowerCmd.Apply {PowerName(power)} amount={amount} current={(cur?.Id.Entry ?? "none")} nested={nestedSameRune}");
        InPowerApply.Value = true;
        ApplyRune.Value = cur;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerApplyPost(PowerModel power, Creature target, decimal amount, RelicModel? __state, ref Task __result)
    {
        try
        {
        if (__state == null) return;
        var rune = __state;
        __result = Then(__result, () => RecordPower(rune, power, target, amount));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerApplyFinal()
    {
        try
        {
        InPowerApply.Value = false;
        ApplyRune.Value = null;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerModPre(PowerModel power, decimal offset, out RelicModel? __state)
    {
        __state = default!;
        try
        {
        var cur = Current.Value ?? RuneForPower(power);
        if (cur != null && offset > 0) Tag(power, cur);
        __state = InPowerApply.Value && ReferenceEquals(ApplyRune.Value, cur) ? null : cur;
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public static void PowerModPost(PowerModel power, decimal offset, RelicModel? __state, ref Task<int> __result)
    {
        try
        {
        if (__state == null) return;
        var rune = __state;
        __result = Then(__result, _ => RecordPower(rune, power, power.Owner, offset));
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }
}

/// <summary>Per-rune counters, each with a full-run total and a this-combat figure.</summary>
public sealed class DynamicRuneStats : IRelicStats
{
    private static readonly string[] Order =
    {
        AutoTracker.Triggers, AutoTracker.Damage, AutoTracker.BonusDamage, AutoTracker.HpLost, AutoTracker.Heal, AutoTracker.MaxHp, AutoTracker.Block, AutoTracker.BonusBlock, AutoTracker.Energy, AutoTracker.EnergySaved, AutoTracker.StarsSaved, AutoTracker.ExtraPlays, AutoTracker.BonusPower, AutoTracker.PowerReduced, AutoTracker.Prevented, AutoTracker.ExtraHpLost,
        AutoTracker.Cards, AutoTracker.Gold, AutoTracker.Stars,
    };

    private readonly Dictionary<string, decimal> _total = new();
    private readonly Dictionary<string, decimal> _combat = new();
    private object? _combatKey;

    private readonly string[] _expected;

    /// <summary>Rune only does things outside combat: show a single total instead of per-combat figures.</summary>
    public bool NonCombat { get; init; }
    /// <summary>Only acts once per combat (start/end of combat): a "this combat" line adds nothing, show the total only.</summary>
    public bool OnceOnly { get; init; }
    /// <summary>Show only the metrics listed for this rune (hides side effects such as conversions or revert steps).</summary>
    public bool ShowOnly { get; init; }
    /// <summary>Type name of a temporary power to display live (e.g. the Dexterity currently on you).</summary>
    public string? LivePower { get; init; }
    public string? LiveLabel { get; init; }
    /// <summary>Rune is a passive damage / Block multiplier: show its current percentage instead of running totals.</summary>
    public bool PctDamage { get; init; }
    public bool PctBlock { get; init; }
    /// <summary>Show no Stats box at all for this rune.</summary>
    public bool Hidden { get; init; }
    /// <summary>Keep the running Bonus damage / Bonus Block totals but drop the live "+N%" line (the description already says it).</summary>
    public bool HidePct { get; init; }
    /// <summary>Show nothing at all when no metric is known or recorded (used for enemy hexes).</summary>
    public bool Quiet { get; init; }
    public decimal LastDmgMult { get; set; } = 1m;
    public decimal LastBlockMult { get; set; } = 1m;

    private static readonly List<DynamicRuneStats> AllInstances = new();

    /// <summary>Zero every "this combat" figure (combat started or ended). Totals are untouched.</summary>
    public static void ResetCombatAll()
    {
        try
        {
        lock (AllInstances)
            foreach (var s in AllInstances) { s._combat.Clear(); s._combatKey = null; }
        }
        catch (Exception e) { AutoTracker.Swallow(e); }
    }

    public DynamicRuneStats(string relicId, string[]? expected = null)
    {
        lock (AllInstances) AllInstances.Add(this);
        RelicId = relicId;
        _expected = expected ?? Array.Empty<string>();
    }

    // 1 decimal for small numbers (bonus damage often comes in fractions), whole numbers otherwise.
    private static string Num(decimal d) =>
        d >= 100 ? ((long)decimal.Round(d)).ToString(CultureInfo.InvariantCulture) : decimal.Round(d, 1).ToString("0.#", CultureInfo.InvariantCulture);

    public string RelicId { get; }
    public StatCadence Cadence => StatCadence.Total;

    public void Add(string key, decimal amount, object? combatKey)
    {
        if (amount <= 0) return;
        if (AutoTracker.DiagOn) AutoTracker.Diag($"add {RelicId}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)} {key}");
        _total[key] = _total.GetValueOrDefault(key) + amount;
        if (combatKey == null) return;
        if (!ReferenceEquals(combatKey, _combatKey)) { _combat.Clear(); _combatKey = combatKey; }
        _combat[key] = _combat.GetValueOrDefault(key) + amount;
    }

    private static string BaseName(string key) =>
        key.StartsWith("Gained ") ? key[7..] : key.StartsWith("Applied ") ? key[8..] : key;

    private static string PctText(decimal mult)
    {
        decimal pct = decimal.Round((mult - 1m) * 100m, 1);
        return Fmt.Blue("+" + pct.ToString("0.#", CultureInfo.InvariantCulture) + "%");
    }

    private string? PercentLine(string label, bool damage)
    {
        decimal? m = null;
        var relic = AutoTracker.HoveredRelic;
        if (relic != null && relic.Id.Entry == RelicId)
        {
            if (damage && RelicId == "HEAVY_HITTER_RUNE" && relic.Owner?.Creature != null)
                m = 1m + Math.Floor((decimal)relic.Owner.Creature.MaxHp / 6m) / 100m; // 1% per 6 Max HP
            else
                m = AutoTracker.ProbeMultiplier(relic, damage);
        }
        m ??= damage ? LastDmgMult : LastBlockMult;
        return $"{label}: {PctText(m.Value)}";
    }

    public string GetDescription(int effectiveTurns, int effectiveCombats)
    {
        if (AutoTracker.DiagOn) AutoTracker.Diag($"tooltip {RelicId}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}: {_total.Count} metrics, total={string.Join(",", _total.Select(k => k.Key + "=" + k.Value))}");

        if (Hidden) return "";
        // Metrics to show: everything recorded, plus what this rune is known to affect (shown as 0 until it happens).
        var keys = new List<string>(_total.Keys);
        var recordedBases = new HashSet<string>(keys.Select(BaseName));
        foreach (var e in _expected)
        {
            if (keys.Contains(e)) continue;
            // "Gained Plating" vs "Applied Plating": do not show a 0 line if the other spelling has data.
            if ((e.StartsWith("Gained ") || e.StartsWith("Applied ")) && recordedBases.Contains(BaseName(e))) continue;
            keys.Add(e);
        }
        // Less noise: passive-power and trigger counters only appear when this rune is known to use them.
        keys.RemoveAll(k => (k == AutoTracker.BonusPower || k == AutoTracker.PowerReduced || k == AutoTracker.Triggers) && !_expected.Contains(k));
        if (ShowOnly) keys.RemoveAll(k => !_expected.Contains(k));
        // Percentage runes show their live percentage AND the accumulated bonus.
        if (PctDamage && !keys.Contains(AutoTracker.BonusDamage)) keys.Add(AutoTracker.BonusDamage);
        if (PctBlock && !keys.Contains(AutoTracker.BonusBlock)) keys.Add(AutoTracker.BonusBlock);
        if (keys.Count == 0 && !PctDamage && !PctBlock && LivePower == null) return ""; // nothing meaningful to show

        var ordered = keys.OrderBy(k => { int i = Array.IndexOf(Order, k); return i < 0 ? 99 : i; }).ThenBy(k => k);
        var lines = new List<string>();
        if (LivePower != null)
        {
            int live = 0;
            var rel = AutoTracker.HoveredRelic;
            if (rel != null && rel.Id.Entry == RelicId && rel.Owner?.Creature != null)
                live = rel.Owner.Creature.Powers.FirstOrDefault(p => p.GetType().Name == LivePower)?.Amount ?? 0;
            lines.Add($"{LiveLabel}: {Fmt.Blue(live)}");
        }
        if (PctDamage && !HidePct) lines.Add(PercentLine("Bonus damage", true)!);
        if (PctBlock && !HidePct) lines.Add(PercentLine("Bonus Block", false)!);
        foreach (var k in ordered)
        {
            bool green = k == AutoTracker.Heal || k == AutoTracker.MaxHp;
            decimal t = _total.GetValueOrDefault(k), c = _combat.GetValueOrDefault(k);
            string tt = green ? Fmt.Green(Num(t)) : Fmt.Blue(Num(t));
            string cc = green ? Fmt.Green(Num(c)) : Fmt.Blue(Num(c));
            if (NonCombat)
            {
                lines.Add($"{k}: {tt}");
            }
            else if (OnceOnly)
            {
                lines.Add($"{k}: {tt} (total)");
            }
            else
            {
                lines.Add($"{k}: {cc} (this combat)");
                lines.Add($"{k}: {tt} (total)");
            }
        }
        return string.Join("\n", lines);
    }

    public JsonObject Save()
    {
        if (_total.Count == 0) return new JsonObject(); // keep saves small
        var o = new JsonObject();
        foreach (var kv in _total) o[kv.Key] = kv.Value;
        return new JsonObject { ["totals"] = o };
    }

    public void Load(JsonObject data)
    {
        var incoming = new Dictionary<string, decimal>();
        if (data["totals"] is JsonObject o)
            foreach (var kv in o) if (kv.Value != null) incoming[kv.Key] = kv.Value.GetValue<decimal>();
        if (incoming.Remove("Extra card plays", out var oldPlays)) // renamed stat
            incoming["Extra cards played"] = Math.Max(oldPlays, incoming.GetValueOrDefault("Extra cards played"));

        // Relic Stats also uses Load() to peek at a past run for the run-history screen; that needs a real replace.
        bool historyPeek = _total.Count > 0 || incoming.Count > 0
            ? new StackTrace().ToString().Contains("LoadDescriptionFromFile")
            : false;

        if (historyPeek)
        {
            _total.Clear(); _combat.Clear(); _combatKey = null;
            foreach (var kv in incoming) _total[kv.Key] = kv.Value;
            return;
        }

        // Normal load (continuing a run): never let a stale or empty save wipe progress made since the last save.
        if (AutoTracker.DiagOn && _total.Count > 0)
            AutoTracker.Diag($"LOAD {RelicId}: kept in-memory {_total.Count} metrics, file had {incoming.Count}");
        foreach (var kv in incoming)
            if (!_total.TryGetValue(kv.Key, out var cur) || kv.Value > cur) _total[kv.Key] = kv.Value;
    }

    public void Reset() { if (_total.Count > 0 && AutoTracker.DiagOn) AutoTracker.Diag($"RESET {RelicId} (had {_total.Count} metrics)"); _total.Clear(); _combat.Clear(); _combatKey = null; }
}
