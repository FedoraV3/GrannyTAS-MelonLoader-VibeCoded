# GrannyTAS — MelonLoader TAS mod for Granny Legacy

> **Full history preserved:** the previous, much more detailed version of this
> file (subsystem rationale, design-decision narratives, RE findings, gotchas)
> is archived at [docs/archive/CLAUDE.full.2026-09-15.md](docs/archive/CLAUDE.full.2026-09-15.md).
> This file is a compact index — restore from the archive if deeper context is needed.
>
> See also: [docs/speed-timing.md](docs/speed-timing.md) (slow-motion nav / speed
> lock, supersedes older timing notes), [docs/code-review.md](docs/code-review.md)
> (2026-09-11 review — current behavior, fixes, tests, unresolved determinism
> limitations), [docs/game-analysis.md](docs/game-analysis.md) (game RE detail),
> [docs/ida-findings.md](docs/ida-findings.md) (disassembly-verified facts).

## Goal

TAS toolkit for **Granny Legacy** as a MelonLoader mod: frame stepper, speed
hacks, independent tick/FPS limits, and macro record/playback that is
**bit-identical on replay regardless of recording speed**. See the archive for
the full rationale — this constraint drives the whole architecture.

## Target

| | |
|---|---|
| Game | Granny Legacy (Omega Mega Gigal Intel), v1.8.9 |
| Install | `C:\Users\ir0n1c\Desktop\Granny_Legacy` |
| Unity | 2022.3.62f2, IL2CPP |
| Loader | MelonLoader v0.7.3 Open-Beta, net6 |

Key game facts (full detail in [docs/game-analysis.md](docs/game-analysis.md)):
player = `Il2Cpp.MobileFPS` (Update-driven, `CharacterController`); enemies run
on `FixedUpdate` (`AI_Granny`, `AI_Grandpa`, `Eyes_Granny`, `Eyes_Grandpa`,
`Eyes_MomSpider`, `KickHatch`); input is legacy `UnityEngine.Input`; RNG for
item placement is `Il2Cpp.SeedManager`.

## Building

```bash
cd GrannyTAS && dotnet build
```

Targets `net6.0`; resolves game assemblies from `$(GameDir)` (default
`%USERPROFILE%\Desktop\Granny_Legacy`); auto-deploys `GrannyTAS.dll` to the
game's `Mods\` folder (`-p:NoDeploy=true` to suppress). See the archive for
build gotchas (missing `CharacterControllerModule`, `GUIStyle` copy-ctor,
`TextAnchor` namespace).

## Determinism — the core design problem

`Time.captureDeltaTime` is the load-bearing primitive: pin it and simulated
time per frame is decoupled from wall clock. `TimeController` exposes **Speed**
as the primary control and derives the frame cap from it, rather than exposing
a raw FPS limit — an uncapped frame rate silently uncaps speed otherwise.
Full hazard table (deltaTime, Update/FixedUpdate split, RNG, physics substeps,
unscaled time, per-tick input) and disassembly-verified findings are in the
archive and [docs/ida-findings.md](docs/ida-findings.md).

## Subsystems

| File | Role |
|---|---|
| `src/PlayerGate.cs` | Decides when the player is actually in control; engages/disengages the mod |
| `src/TasConfig.cs` | Settings file — `UserData/GrannyTAS.cfg` |
| `src/Keybinds.cs` | Rebindable hotkeys, persisted via MelonPreferences |
| `src/CursorController.cs` | Owns the mouse cursor while the panel is open |
| `src/TimeController.cs` | `captureDeltaTime` pinning, tick/physics rate, speed, frame stepper |
| `src/VirtualInput.cs` | Harmony patches over `Input.*`; latched buffer; macro injection point |
| `src/InputFrame.cs` | One frame of input — the unit recorded and replayed |
| `src/MacroFile.cs` | Text macro format, load/save |
| `src/MacroEngine.cs` | Record/playback state machine |
| `src/WorldSnapshot.cs` | Captures/restores entity positions so a replay starts from the recorded world |
| `src/PickupDiagnostics.cs` | Traces the item-pickup gate chain and ray (off by default) |
| `src/ImGuiHost.cs` | Dear ImGui context + Unity GL renderer backend |
| `src/TasWindow.cs` | The ImGui control panel |

No arm/disarm switch — `PlayerGate` checks the same three flags the game itself
gates input on (`Paused.IsPaused`, `MobileFPS.isAllowedToMove`, `.AbleToMove`),
plus a 12-frame settle and a day-1 bed-animation check. Rationale and full flag
table in the archive.

Default hotkeys: **Insert** panel · F2 pause · F3 step · F4 step x10 ·
F5/F6 tick rate · F7/F8 speed · F9 uncapped · F10 clear buffer · F11 record ·
F12 play. All rebindable in the panel's Keybinds section.

Macros: plain text, one line per simulated frame, `.grannytas` extension,
header carries `tickRate`/`physicsRate`/`rngSeed`/`seedManagerSeed`/`scene`.
Recording works at any speed; playback is always 1x. Player position is
recorded per frame and corrected on replay (`PinPlaybackPosition`) — see the
archive's "Replay drift" section for why this was needed (a 3073-frame replay
lost one pickup to sub-mm position drift at a grazing ray angle, despite
input replay being exact).

## Status

Core pipeline (frame stepper, speed/tick/physics controls, virtual input,
macro record/playback, world snapshot, ImGui panel, player gate, rebindable
hotkeys, config persistence, replay drift correction) is implemented and was
verified on a recorded run (1912 frames, 0.001mm drift). Remaining: ImGui
keyboard/text input (not currently needed). See the archive's Status checklist
for full history.
