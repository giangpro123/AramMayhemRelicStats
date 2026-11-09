using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using RelicStats.Core;

namespace HextechRelicStats;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "HextechRelicStats";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    // One instance per stats class, kept for the whole session so re-registration reuses them.
    private static readonly List<IRelicStats> OurStats = new();

    private static readonly string[] OptionalMods = { "HextechRunesSponsorPack", "HextechWatcherPack" };
    private static readonly HashSet<string> _installed = new();
    private static readonly List<Type> _pendingPatches = new();
    private static HashSet<string> _manual = new();
    private static Harmony? _harmony;

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            var name = args.LoadedAssembly.GetName().Name;
            if (name == null || !OptionalMods.Contains(name) || !_installed.Add(name) || _harmony == null) return;

            lock (OurStats)
            {
                OurStats.AddRange(AutoTracker.Install(_harmony, _manual, new[] { name }, false));
                RegisterOurStats();
                foreach (var type in _pendingPatches.ToList())
                {
                    try { _harmony.CreateClassProcessor(type).Patch(); _pendingPatches.Remove(type); }
                    catch { /* belongs to the other optional mod, which is not loaded yet */ }
                }
            }
            Logger.Info($"Optional mod {name} loaded after us: tracking added");
        }
        catch (Exception e) { Logger.Warn($"Late install failed: {e.Message}"); }
    }

    public static void Initialize()
    {
        if (Hex.Assembly == null)
        {
            Logger.Warn("HextechRunes assembly not loaded; nothing to track.");
            return;
        }

        foreach (var type in Assembly.GetExecutingAssembly().GetTypes()
                     .Where(t => typeof(IRelicStats).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(DynamicRuneStats)))
        {
            OurStats.Add((IRelicStats)Activator.CreateInstance(type)!);
        }

        var harmony = new Harmony(ModId);

        // RelicStats 0.7.0 only auto-discovers stats classes inside its own DLL, and its
        // DiscoverAndRegister() clears the table. So: register now, and again whenever it re-runs.
        harmony.Patch(
            AccessTools.Method(typeof(RelicStatsRegistry), nameof(RelicStatsRegistry.DiscoverAndRegister)),
            postfix: new HarmonyMethod(typeof(MainFile), nameof(RegisterOurStats)));
        RegisterOurStats();

        // Generic tracker for every other rune (heal, max HP, block, energy, damage, powers ...).
        try
        {
            _manual = new HashSet<string>(OurStats.Select(x => x.RelicId));
            _harmony = harmony;
            var present = new List<string> { "HextechRunes" };
            foreach (var name in OptionalMods)
                if (Hex.Find(name) != null) { present.Add(name); _installed.Add(name); }
            OurStats.AddRange(AutoTracker.Install(harmony, _manual, present.ToArray(), true));
            OurStats.AddRange(HexTracker.Install(harmony));
            CombatReset.Install(harmony);
            RegisterOurStats();

            // The Expansion Pack and Watcher Pack are optional and may load after us (or never): pick them up
            // whenever their assembly appears.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
        catch (Exception e) { Logger.Error($"AutoTracker failed to install: {e}"); }

        int ok = 0, failed = 0, skipped = 0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes()
                     .Where(t => t.GetCustomAttributes<HarmonyPatch>().Any()))
        {
            try { harmony.CreateClassProcessor(type).Patch(); ok++; }
            catch (Exception e)
            {
                // The Watcher Pack and Expansion Pack are optional: a missing one is normal, not an error.
                bool optionalMissing = e.ToString().Contains("not found (mod not installed?)");
                if (optionalMissing) { skipped++; _pendingPatches.Add(type); Logger.Info($"Skipped {type.Name}: optional mod not installed (yet)"); }
                else { failed++; Logger.Warn($"Failed to patch {type.Name}: {e.Message}"); }
            }
        }
        Logger.Info($"Registered {OurStats.Count} Hextech stats, patched {ok} ({failed} failed, {skipped} skipped: optional mods not installed)");
    }

    /// <summary>Registers a stats object created after startup (runes first seen at runtime).</summary>
    public static void AddStats(IRelicStats stats)
    {
        OurStats.Add(stats);
        RegisterOurStats();
    }

    public static void RegisterOurStats()
    {
        var field = typeof(RelicStatsRegistry).GetField("_stats", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not Dictionary<string, IRelicStats> table)
        {
            Logger.Error("Could not reach RelicStatsRegistry._stats (RelicStats internals changed?)");
            return;
        }
        foreach (var stats in OurStats) table[stats.RelicId] = stats;
    }
}
