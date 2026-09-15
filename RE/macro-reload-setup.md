# Macro playback reload setup

## Purpose

This note records the configuration boundary required to restart a level before
macro playback. It is limited to Granny Legacy 1.8.9, Unity 2022.3.62f2,
IL2CPP build GUID `313b7e613d344d508e0482d1248658f6`.

## Verified facts

### Item seed

`SeedManager.GeneratePlacement` is native `0x18024f770`. Its managed proxy
exposes these public members:

```csharp
int Seed { get; set; }
bool RandomizeSeed { get; set; }
void GeneratePlacement()
```

At `0x18024fc70`, the native function tests `this + 0x44`, the generated
interop field `RandomizeSeed`:

```text
RandomizeSeed == true  -> UnityEngine.Random.RandomRangeInt(0, 999999999)
RandomizeSeed == false -> PlayerPrefs.GetInt("GameSeed", 0)
Seed = result
```

The `GameSeed` literal is verified by the existing native finding's
`StringLiteral_6078` reference and the local Il2CppDumper
`stringliteral.json`: literal 6078 (one-based; array index 6077) has value
`GameSeed`. The direct disassembly also loads the matching generated literal
slot at `0x18024fc87` before calling `PlayerPrefs.GetInt`.

After selecting the seed, placement retries up to 50 times and creates
`System.Random(Seed + attempt)`. See `docs/ida-raw-evidence.txt`, lines
168--199, and the direct instructions at `0x18024fc70`--`0x18024fd12`.

**Safe pre-load setup:**

```csharp
PlayerPrefs.SetInt("GameSeed", recordedSeed);
PlayerPrefs.Save();
```

Then ensure `SeedManager.RandomizeSeed` is false before `GeneratePlacement`
reads the preference. A Harmony prefix on `SeedManager.GeneratePlacement` is a
valid narrow hook for that boolean. Assigning `SeedManager.Seed` alone is
insufficient: the native false branch overwrites it from `GameSeed`.

Confidence: **VERIFIED**.

### Reload API and completion boundary

`Paused.RestartP()` is public in generated interop. Native analysis at
`0x180231cc0` verifies that it restores `Time.timeScale` to 1 and invokes the
scene reload path for the active level. It is the game-owned reload API.

The reload destroys all current scene objects. Pending playback must therefore
live in the Melon instance, not in any scene component, and must not restore a
world snapshot before the new scene has loaded and player control has settled.
The existing `PlayerGate.IsReady` predicates provide the relevant readiness
boundary: a live `MobileFPS`, active/enabled controller, not paused,
`isAllowedToMove`, `AbleToMove`, `CamK`, live player camera, no jumpscare, and
the wake-up hierarchy no longer active for 12 consecutive frames.

Confidence: **VERIFIED** for the API and `PlayerGate` implementation;
**HIGH CONFIDENCE** that this is the safe macro-resume boundary.

### Values available in a loaded gameplay scene

The generated proxy exposes the following actual scene data, suitable for
capture and post-load verification:

| Value | Runtime source | Type |
|---|---|---|
| displayed difficulty | `Paused.Data_Diff` | `TMP_Text` |
| displayed preset | `Paused.Data_Preset` | `TMP_Text` |
| displayed seed/status | `Paused.Data_S` | `TMP_Text` |
| displayed version/status | `Paused.Data_G` | `TMP_Text` |
| selected preset | `ObjectsManager.PresetChoosenCurrent` | `float` |

`MainMenu` has `Difficulty`, `VersionControl`, and `PresetS` slider fields,
but those are menu-scene controls, not a safe gameplay configuration API.
The local literal table contains five numbered item-preset labels plus
in-order/random modes, and nine selectable game-version labels 1.0 through
1.8. This establishes that preset and version are separate concepts.

Confidence: **VERIFIED** for exposed fields and selectable labels;
**UNKNOWN** for the exact PlayerPrefs persistence keys and write semantics of
difficulty, version, and preset.

## Required integration order

1. Capture scene name, `SeedManager.Seed`, `RandomizeSeed`, the four displayed
   `Paused` values, and `ObjectsManager.PresetChoosenCurrent` at record start.
2. On playback, compare the live captured values. If all match, restore the
   positional snapshot and begin playback without reload.
3. If the seed differs, persist `GameSeed`, arm the scoped
   `SeedManager.GeneratePlacement` randomize override, retain the macro as a
   pending playback request, and call `Paused.RestartP()`.
4. After the reload, wait for `PlayerGate.IsReady`; verify the loaded
   `SeedManager.Seed`, the UI values, and the preset float. Only then restore
   positions, reset Unity RNG, and arm macro input.
5. If any difficulty/version/preset value still mismatches, abort with a clear
   message. Do not write guessed PlayerPrefs keys.

## Open questions and limitation

The exact PlayerPrefs key/value mapping for difficulty, game version, and
preset is **UNKNOWN**. `MainMenu` holds UI sliders for each, but no generated
method exposes a configuration setter. `VersionControl` controls level
objects; it does not expose a game-version setting property. Names such as
`RandomSeed`, `PresetChoosin`, and `VersionPlayerPrefs*` are present in the
metadata/literal tables but are not sufficient evidence of a persistence key
or semantics.

The local `GameAssembly.dll.i64` exists, but IDA batch use is unavailable in
this session because it fails before database load with `Fatal registry error:
Access is denied`; the advertised IDA MCP is not callable. Direct native
disassembly via `objdump` remains available and was used for the seed branch.

Until those mappings are traced, automatic restart can guarantee the recorded
seed and can detect, but cannot safely force, difficulty/version/preset.
