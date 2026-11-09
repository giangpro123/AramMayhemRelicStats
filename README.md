# Relic Stats: ARAM Mayhem

Bridge mod that adds [Relic Stats](https://github.com/BSchneppe/relic-stats-sts2) tooltips for
ARAM: Mayhem (HextechRunes) runes, and for ARAM Mayhem: Watcher Pack runes when that pack is installed.

## Tracked
| Mod | Rune | Stat |
| --- | --- | --- |
| HextechRunes | Slap | Strength gained |
| HextechRunes | Bad Taste, Bloodthirst, Blood Idol | HP healed |
| Expansion Pack | Gastritis | HP healed |
| Expansion Pack | Regret | Revives used |
| Watcher Pack | Daily Mantra, Mantra Fist, Ascending Thought | Mantra granted (base, before Mantra Resonance) |
| Watcher Pack | Calm Surge | Energy gained |
| Watcher Pack | Fury Forged / Still Hands | Temporary Strength / Dexterity |
| Watcher Pack | Upgrade: Miracle | Cards drawn |
| Watcher Pack | Iron Stance | Block gained (measured) |
| Watcher Pack | Divine Echo | Damage dealt (measured) |

## Build
Needs the .NET 9 SDK. Paths default to `E:/SteamLibrary/steamapps/common/Slay the Spire 2`.

    dotnet build -c Release

Output: `bin/Release/HextechRelicStats.dll`, also copied to the game's `mods/HextechRelicStats/`.

## Publish to the Workshop
1. Download `ModUploader.exe` from https://github.com/megacrit/sts2-mod-uploader/releases and put it in this folder.
2. Steam must be running and logged in.
3. Run `publish.bat`. It builds, stages `workshop/content/`, and uploads.
4. First run creates `workshop/mod_id.txt`. Keep it: later runs update the same item.

## Adding a rune
- Fixed amount per trigger, and the rune calls `Flash()` when it fires: subclass `FlashCounterStats`
  and set `RelicId`, `PerProc`, `Format`. No patch needed.
- Variable amount: subclass `HexCounterStats`, add `[HarmonyPatch]` + `TargetMethod()`, measure in
  Prefix/Postfix. If the hook is `async`, wrap `__result` with `AfterTask(...)`.
- `RelicId` = class name in UPPER_SNAKE_CASE (`WatcherHexIronStanceRune` -> `WATCHER_HEX_IRON_STANCE_RUNE`).
