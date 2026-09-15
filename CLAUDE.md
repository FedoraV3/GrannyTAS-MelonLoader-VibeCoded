# GrannyTAS — MelonLoader TAS mod for Granny Legacy

> 0.3.2 follow-up: [slow-motion navigation and speed lock](docs/speed-timing.md).
> timeScale now follows capped pacing with compensating captureDeltaTime;
> speed/turbo are locked during both recording and playback. older timing
> descriptions below are historical and superseded by this follow-up.

> Review update: see [2026-09-11 code review](docs/code-review.md) for current
> behavior, fixes, tests, and unresolved determinism limitations. That update
> supersedes older subsystem descriptions below.
>
> Working doc for any Claude picking this project up. Update it as facts are
> established. Mark anything unverified as **UNVERIFIED** — do not let guesses
> harden into assumptions.

## Goal

A TAS (tool-assisted speedrun) toolkit for **Granny Legacy**, as a MelonLoader mod:

1. **Frame stepper** — pause the game, advance exactly one frame at a time.
2. **Speed hacks** — arbitrary game-speed scaling (slow-mo for precision, fast-forward).
3. **Tick limit and FPS limit as *separate* controls** — physics/tick rate and
   render rate must be independently settable.
4. **Macro record + playback** — record inputs, replay them at *normal* speed
   even if they were recorded while the game was slowed down.

**Hard requirement: physics must match on playback.** A macro recorded at 0.1×
speed must produce a bit-identical run when replayed at 1×. This constraint
drives the entire architecture (see "Determinism" below).

## Target

| | |
|---|---|
| Game | Granny Legacy (Omega Mega Gigal Intel) |
| Install | `C:\Users\ir0n1c\Desktop\Granny_Legacy` |
| Executable | `Granny Legacy.exe` |
| Unity | **2022.3.62f2** |
| Scripting backend | **IL2CPP** (`GameAssembly.dll` 14.2 MB, `il2cpp_data\Metadata\global-metadata.dat`) |
| Build GUID | `313b7e613d344d508e0482d1248658f6` |
| Scenes | `level0`, `level1` (10 MB — likely the house), `level2` |
| Game version | **1.8.9** |
| Loader | **MelonLoader v0.7.3** Open-Beta, `net6` runtime, `version.dll` proxy |

### Key findings (full detail in [docs/game-analysis.md](docs/game-analysis.md))

`Assembly-CSharp.dll` is **not obfuscated** — 483 readable type names.

- **Player = `Il2Cpp.MobileFPS`**, a `CharacterController` FPS controller driven
  entirely from **`Update()`**. It has no `FixedUpdate`.
  Chain: `Update -> HandlePlayerMovement -> HandleVerticalMovement -> AdjustSpeed -> ApplyFinalMovements -> CharacterController.Move`.
- **Enemies run on `FixedUpdate`** — exactly six types:
  `AI_Granny`, `AI_Grandpa`, `Eyes_Granny`, `Eyes_Grandpa`, `Eyes_MomSpider`, `KickHatch`.
- **Input is the legacy `UnityEngine.Input` API** (`KeyCode` throughout,
  `KeyBindManage` rebinds Primary/Secondary/Crouch/Drop/Shoot). Not the new
  Input System. So macro injection has one clean choke point: patch `Input.*`.
- **RNG for item placement is `Il2Cpp.SeedManager`**, with explicit `Seed` and
  `RandomizeSeed` fields, resolved once at level load. Store the seed in the
  macro; no per-frame RNG interception needed for item layout.

**The central fact: the player advances on `Update`, the enemies on
`FixedUpdate`.** Their interleaving decides when Granny's vision samples the
player. A macro must therefore pin the Update:FixedUpdate ratio, not merely the
input stream — and this is exactly why tick limit and FPS limit are separate
controls rather than one.

### Prior tooling on this machine

- An **IDA Pro database** for `GameAssembly.dll` exists next to the game
  (`GameAssembly.dll.i64`, ~289 MB — already analyzed). An IDA Pro MCP is
  available for native-level questions. *Note: the `idalib` MCP server failed to
  connect in the first session; may need restarting.*
- **BepInEx** (with CinematicUnityExplorer) *was* installed here and was removed
  on 2026-09-11 to make room for MelonLoader. Its `BepInEx/interop/` folder held
  Il2CppInterop proxy assemblies — the single best source of managed type info.
  MelonLoader regenerates the equivalent, so this is not a loss, just a step.
- `GameAssembly2.dll` — user's own hard-patched build granting invincibility to
  Granny. Swap it in as `GameAssembly.dll` to use. Not part of the TAS mod; keep
  it out of the build, but it is useful for testing routes without dying.

## Toolchain

- .NET SDK **10.0.301** at `C:\Program Files\dotnet`
- `ilspycmd` 11.0.0.9375 installed globally (`~/.dotnet/tools`) — used to
  decompile IL2CPP proxy assemblies.
- Python 3.11.9

### Decompiling the game's managed code

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
ilspycmd -p -o <outdir> "<game>/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll"
```

The proxy assemblies contain full type and method **signatures** but method
**bodies are stubs** (they trampoline into `GameAssembly.dll`). For actual
logic, decompile the native function in IDA. Signatures alone are usually enough
to plan Harmony patches.

## Building

```bash
cd GrannyTAS && dotnet build
```

Targets `net6.0` (MelonLoader 0.7.3 runs IL2CPP mods on net6). The csproj
resolves game assemblies from `$(GameDir)`, defaulting to
`%USERPROFILE%\Desktop\Granny_Legacy`; override with
`dotnet build -p:GameDir="D:\path	o\Granny_Legacy"`. A successful build
auto-copies `GrannyTAS.dll` into the game's `Mods\` folder — suppress with
`-p:NoDeploy=true`.

Gotchas hit already, so future sessions don't rediscover them:
- There is **no `UnityEngine.CharacterControllerModule`** in this build;
  `CharacterController` lives in `UnityEngine.PhysicsModule`.
- **Il2CppInterop does not surface `GUIStyle`'s copy constructor.**
  `new GUIStyle(GUI.skin.label)` fails to compile (it binds the `IntPtr`
  overload). Build a `new GUIStyle()` and set fields instead — and don't mutate
  `GUI.skin.*`, that leaks into the game's own UI.
- `TextAnchor` needs `UnityEngine.TextRenderingModule`.

## Determinism — the core design problem

Recording at one speed and replaying at another only reproduces the same run if
every piece of state that affects simulation advances identically. Things that
break this, and how they must be handled:

| Hazard | Handling |
|---|---|
| `Time.deltaTime` varying per frame | Force a **fixed** delta during both record and playback. Never let real wall-clock frame time reach game logic. |
| Logic split across `Update` and `FixedUpdate` | Frame stepping must advance both in a fixed, reproducible ratio. If the player controller reads input in `Update` but moves in `FixedUpdate`, input must be latched per tick. |
| `UnityEngine.Random` | Seed must be captured at record start and restored at playback start. |
| Physics substep count drifting with frame rate | Pin `Time.fixedDeltaTime` and `Time.maximumDeltaTime`; consider driving `Physics.Simulate` manually. |
| Animation / NavMesh / coroutines running on unscaled time | Audit for `unscaledDeltaTime`, `Time.realtimeSinceStartup`, `WaitForSecondsRealtime`. |
| Frame-rate-dependent input sampling | Record input **per tick**, not per render frame. This is why tick limit and FPS limit must be separate knobs. |

**Design consequence — `Time.captureDeltaTime` is the load-bearing primitive.**
When it is non-zero, Unity stops deriving `Time.deltaTime` from the wall clock
and hands every frame exactly that value. Simulated time then advances a fixed
amount per rendered frame regardless of how long the frame really took, which
decouples real time from game time completely:

| Knob | Maps to | Affects simulation? |
|---|---|---|
| Tick rate | `Time.captureDeltaTime = 1/rate` | **Yes** — simulated seconds per frame |
| Physics rate | `Time.fixedDeltaTime = 1/rate` | **Yes** — enemy AI ticks |
| Speed | `Application.targetFrameRate = tickRate * speed` | **No** — pure wall-clock pacing |

**The trap this creates, hit once already:** because simulated time per frame is
pinned, wall-clock speed is *entirely* `fps / tickRate`. Leaving the frame rate
uncapped therefore leaves the *speed* uncapped — arming the mod on a machine
rendering 300 fps against a 60/s tick rate runs the game at 5x. `vSyncCount` is
forced to 0 (otherwise the monitor's refresh rate would set the game speed), so
nothing else is holding the frame rate back either.

Hence `TimeController` exposes **`Speed` as the primary control and derives the
frame cap from it** (`EffectiveFpsCap`), rather than exposing a raw FPS limit.
An explicit `Uncapped` turbo mode exists but is never a default.

So the macro is indexed by **frame number**, and replay speed is just how fast
frames are produced. A run recorded at 5 fps wall-clock and replayed at 240 fps
executes the identical simulation. `QualitySettings.vSyncCount` must be forced
to 0 or it clamps the frame rate behind `targetFrameRate`'s back.

The `captureDeltaTime / fixedDeltaTime` ratio must be held constant too — see
the Update/FixedUpdate split above. `TimeController.RatioIsIntegral` flags when
it is non-integral, which still replays deterministically but only from the same
starting phase.

### Answered by disassembly — [docs/ida-findings.md](docs/ida-findings.md)

Verified against `GameAssembly.dll`, evidence in
[docs/ida-raw-evidence.txt](docs/ida-raw-evidence.txt).

- **Movement scales linearly by `deltaTime`, exactly once.** Both
  `ApplyFinalMovements` (`0x18022ce70`) and `HandleVerticalMovement`
  (`0x18022d5f0`) do `Move(velocity * Time.deltaTime)` — not `dt^2`, not a bare
  constant. Distance per second is frame-rate independent.
- **Mouse look does NOT scale by `deltaTime`.** The real look code is
  `MobileFPS::GetTouchInput` (`0x18022d030`), *not* `DragHead` (which is a
  delayed camera-recenter coroutine). Raw per-frame `GetAxis` delta x
  `cameraRotationSpeed`, clamped to `[minXRotation, maxXRotation]`. **View
  direction is therefore a function of frame count**, so per-frame mouse deltas
  must be recorded and replayed per simulated frame.
- **AI RNG is rare but real.** `UnityEngine.Random.RandomRangeInt` appears twice
  in `AI_Granny`: in `ChooseWaypoint`, and inline in `FixedUpdate`
  (`0x1801c0080`, idle-voice clip pick). Both sit behind a walk-cycle timer
  state transition rather than firing every tick. None in `SensorPlayer`,
  `ChaseAction`, `LookAtThings`, `ResetAIDecision`, `StopChase`, `OnTriggerStay`
  or `Eyes_Granny::FixedUpdate`. **`Random.state` must still be captured in the
  macro header** — rare is not never.
- **Nothing in gameplay uses unscaled time.** Every caller of
  `unscaledDeltaTime` / `realtimeSinceStartup` / `WaitForSecondsRealtime` is UI
  or engine infrastructure (TMPro, UGUI, UIElements, PostProcessing). Speed
  control is safe.
- **`SeedManager.GeneratePlacement` does `rnd = new System.Random(Seed + attempt)`**,
  with `Seed` from PlayerPrefs, or from a one-time `UnityEngine.Random` call if
  the randomize toggle is set. Pin `Seed` + clear the toggle => deterministic
  item placement.

**Timing correction (2026-09-11):** `Time.deltaTime` inside `FixedUpdate`
returns `Time.fixedDeltaTime`. The prior claim that this is a game bug coupling
AI timers directly to render delta was incorrect. Update/FixedUpdate interleaving
and accumulator phase still matter; see [review](docs/code-review.md).

**Resolved (2026-09-14):** `FallSpeed` never accumulates — it's a fixed constant
(9.81) written once in `MobileFPS::.ctor`, multiplied by `Time.deltaTime`
exactly once wherever it's consumed. There is no `v += g*dt` ramp; fall speed
is identical on frame 1 of a fall and frame 1000. `FallingHolder` has its own,
unrelated `Speed`/`fallDuration` fields that accumulate a fall-damage *timer*,
not velocity. See the "FallSpeed accumulation (resolved)" section in
[docs/ida-findings.md](docs/ida-findings.md). Also, higher frame rates mean
more discrete `CharacterController.Move` calls, so collision resolution
granularity varies with tick rate even though the distance math is correct —
TAS runs must therefore pin tick rate, not merely speed.

## Subsystems

| File | Role |
|---|---|
| `src/PlayerGate.cs` | Decides when the player is actually in control; engages/disengages the mod |
| `src/TasConfig.cs` | The settings file — `UserData/GrannyTAS.cfg`; keybinds, rates, panel prefs |
| `src/Keybinds.cs` | Rebindable hotkeys, persisted via MelonPreferences |
| `src/CursorController.cs` | Owns the mouse cursor while the panel is open |
| `src/TimeController.cs` | `captureDeltaTime` pinning, tick/physics rate, speed, frame stepper |
| `src/VirtualInput.cs` | Harmony patches over `Input.*`; latched buffer; macro injection point |
| `src/InputFrame.cs` | One frame of input — the unit recorded and replayed |
| `src/MacroFile.cs` | Text macro format, load/save |
| `src/MacroEngine.cs` | Record/playback state machine |
| `src/WorldSnapshot.cs` | Captures/restores entity positions so a replay starts from the recorded world |
| `src/PickupDiagnostics.cs` | Traces the item-pickup gate chain and the pickup ray (off by default) |
| `src/ImGuiHost.cs` | Dear ImGui context + a Unity GL renderer backend |
| `src/TasWindow.cs` | The ImGui control panel |

### No arm switch — the player gate

There is no arm/disarm. `PlayerGate` decides every frame whether the player is
genuinely in control, and `GrannyTasMod.UpdateEngagement` brings the timing layer
and input interception up and down to match.

This exists because an explicit arm step could always be taken at the wrong
moment. Arming during the **wake-up animation** pinned timing over a sequence the
player does not drive, so recording there wrote frames nobody authored and the
macro desynced on the first replayed frame. Tying engagement to the same
condition that decides whether input means anything removes the mistake rather
than documenting it.

**The predicate is not a heuristic — it asks the same questions the game asks.**
`MobileFPS.Update` (`0x18022d9c0`) reads input behind exactly three gates:

```
if (!Paused.IsPaused)          // wraps the entire body
  if (!isAllowedToMove) ...    // returns before reading ANY input
  if (!AbleToMove) ...         // returns before reading WASD
  if (CamK) GetTouchInput()    // mouse look
```

So when the gate says "in control", the game is about to read input; when it
says otherwise, the game would ignore whatever the mod injects — precisely the
condition under which recording is meaningless.

| Checked | Type | Note |
|---|---|---|
| `Paused.IsPaused` | `public static bool` | The **only** static bool in the assembly |
| `MobileFPS.isAllowedToMove` | `bool` @ obj 0x85 | Cleared by jumpscares and all four `Escapes` |
| `MobileFPS.AbleToMove` | `bool` @ obj 0x86 | Cleared by `ScreenEffects.FrozePlayer`, `BearTrapLogic` |
| `MobileFPS.CamK` | `bool` @ obj 0x88 | Gates mouse look |
| `MobileFPS.playerCamera2` | `Camera` | The real camera; `playerCamera` is only the pitch pivot `Transform` |
| `MobileFPS.PS.IsJumpscared` / `.Killed` | `bool` | Belt-and-braces, but names the *reason* |
| `Days.PlayerBedAnim` holder active | — | The wake-up animation; see below |

Plus a **12-consecutive-frame settle**, because the intro coroutine flips its
flags in sequence rather than atomically.

**The intro is checked independently of the movement flags, deliberately.**
`Days.<StartDays>d__140` sets `isAllowedToMove`/`AbleToMove`/`CamK` to true only
on the `CurrentDay >= 2` branches — **nothing in the binary writes them on the
day-1 path**, so during the day-1 bed animation their values come from
serialized scene data and cannot be assumed false. What *is* certain is that the
coroutine ends the intro with `PlayerBedAnim.transform.parent.gameObject
.SetActive(false)`, so that object still being active means the sequence has not
finished. The panel's **Player gate** section shows every flag live, which is how
to settle the day-1 question at runtime rather than by static analysis.

**Do not use `Cursor.lockState` as the in-control signal.** `MobileFPS.Awake`
(`0x18022cf00`) sets it to `Locked` the instant the scene loads, long before the
intro ends. It only distinguishes menu from gameplay scene, which
`Paused.IsPaused` already covers. `Days.BeganDay` is likewise not a
"gameStarted" flag — it is true for ~2 s while the "Day N" text is on screen and
then set back to false.

**Standing down gates the transport, not the settings.** Rates, speed and turbo
stay editable while the gate is shut — from the panel or their hotkeys — because
the game's own pause menu *is* a shut gate, and that is exactly when a rate gets
dialled in. The setters always record the value and `TimeController.Apply()`
simply declines to touch engine timing while disengaged, flagging
`HasPendingChanges`; `Enable()` puts the whole set into force at once. Two
consequences worth keeping: `Enable()` adopts the game's `fixedDeltaTime` only
while `PhysicsRateOverridden` is false, or re-engaging after every cutscene would
silently undo a chosen physics rate; and `TasConfig.Sync` runs whether or not the
controller is engaged, because `ApplyTo` fires right after `Enable` and would
otherwise overwrite an unsaved edit at the moment it was meant to take effect.
Pause and frame-step stay gated — there is no simulation to act on.

`MacroEngine.StartRecording` / `StartPlayback` refuse while the gate is shut and
say why. Losing control mid-run stops a recording cleanly and aborts a playback,
rather than letting either trail off into a cutscene.

`TimeController.ResetFrameCount()` is called at both record and playback start:
the macro is indexed by frame number, so the on-screen frame counter is only
meaningful while it agrees with the index being written.

### Config — `UserData/GrannyTAS.cfg`

Its own MelonPreferences category with its own file path, not a corner of the
shared `MelonPreferences.cfg`. Two reasons: these settings are hand-edited (a
keybind list is exactly what gets tweaked in a text editor between sessions),
and a file holding only this mod's settings can be deleted to reset the mod
without disturbing every other mod's configuration.

Entries are created **before** `LoadFromFile` so the loaded values land on
entries that exist — a key read before its entry was defined is dropped again on
the next save.

Persisted: every keybind, `TickRate`, `Speed`, `PhysicsRate`, `ShowOverlay`,
`PanelOpenAtStart`, `SuppressLookWithPanel`.

**Writes are coalesced.** Dragging the speed slider changes the value every
frame, and committing each would put a synchronous disk write inside the render
loop. `Sync` marks dirty, `Tick` flushes at most every 2 s, and both
`OnDeinitializeMelon` and `OnApplicationQuit` flush so the last change is never
the one lost. Rebinds save immediately — they are rare and worth the write.

Two details that are easy to get backwards:

- **`PhysicsRate` saves 0 by default, meaning "adopt the game's own rate".**
  `TimeController.Enable` takes the game's `fixedDeltaTime` so that engaging the
  mod does not, by itself, change the AI. Writing the adopted value into the
  config would freeze today's build's rate into the settings and override the
  game's own after an update. Only `TimeController.PhysicsRateOverridden` — set
  by an explicit `SetPhysicsRate` — makes it persist. That flag also stops the
  adoption clobbering a chosen rate on every re-engage, which happens at every
  cutscene.
- **`Config.ApplyTo` runs *after* `Time.Enable`, not before**, or the adoption
  would overwrite the rates just restored. Which in turn means `Sync` must not
  be gated on `Enabled`: a rate dialled in while standing by has to reach the
  config before `ApplyTo` reads it back.

`Uncapped` is deliberately **not** persisted — turbo is a deliberate act, never
a state to wake up in.

### The cursor and the panel

The panel is mouse-driven, so opening it frees the cursor; the game locks and
hides it during play. Closing it has to put the cursor back — but back to *what
the game wants now*, not to a snapshot taken at open time. Those differ
routinely: open the panel, press Esc for the game's own pause menu, close the
panel, and restoring the saved "locked" leaves that menu with no usable cursor.
That is what the old save/restore in `GrannyTasMod` actually did.

`CursorController.Release` therefore re-derives, in order:

1. `Paused.IsPaused`, or no `MobileFPS` in the scene (main menu) → leave free.
2. The game asserted a lock while the panel held the cursor → lock.
3. The cursor was already free when the panel opened and the game has not
   re-locked since → leave free. Not every screen that frees the cursor goes
   through `Paused` (a keypad, an inventory view), so this infers the case the
   first test cannot name.
4. Otherwise gameplay is live → lock and hide.

While open, the free state is re-asserted **every frame** rather than set once,
because the game re-locks on its own schedule (`MobileFPS.Awake` on scene load,
and menu transitions after). Finding the cursor locked during that check *is*
the game asking for it back, so it is recorded for rule 2 rather than fought
over.

`VirtualInput.SuppressMouse` (on by default, `SuppressLookWithPanel`) stops
mouse motion and clicks reaching the game while the panel is up — otherwise
dragging the window spins the camera and clicking a button fires the gun. It is
applied in `PollHardware`, the one point hardware enters the mod, so the latched
buffer, the live look passthrough and the free-running advance all inherit it.
Both the game's frame and the recorder's frame are zeroed: a rotation the macro
records but the game never applied desyncs a replay exactly as badly as the
reverse. `ImGuiHost` reads the mouse under `VirtualInput.Bypass`, so the panel
itself is unaffected.

### Hotkeys

Every binding is **rebindable** — `Keybinds`, persisted through MelonPreferences
as `KeyCode` *names* (a config reading `F11` survives hand-editing in a way one
reading `301` does not). Rebind from the **Keybinds** section of the ImGui panel:
click a binding, press a key; Esc cancels, Backspace unbinds.

Defaults: **Insert** panel · F2 pause · F3 step · F4 step x10 · F5/F6 tick rate ·
F7/F8 speed · F9 uncapped · F10 clear buffer · F11 record · F12 play.

Two rules fall out of the capture being a small state machine inside the normal
per-frame update rather than a modal prompt:

- **While capturing, no hotkey fires.** Otherwise binding F3 would also step a
  frame.
- **A key drives one action.** Binding a key that is already taken unbinds the
  other, which is visible in the panel — silently sharing would make one of the
  two look broken.

F12 still collides with Steam's screenshot key if the overlay is enabled — but
that is now a rebind away rather than a code change.

### The input buffer

While paused, keys **latch** rather than being read live: tap W and it stays on
until tapped again, and the latched set is delivered to the next stepped frame.
Holding several physical keys steady while reaching for the step key is not
something a person can do reliably, and frame-stepping is worthless if composing
a frame's input is the hard part.

`Horizontal`/`Vertical` are **synthesized** from latched WASD. Unity builds those
axes from real hardware, so a latched W would otherwise leave them zero and the
player would not move.

### Looking around while paused

Look rotation is pure accumulation and is *not* `deltaTime`-scaled (see the RE
findings), so it still applies at `timeScale = 0` — which is what makes aiming
during a pause possible at all. But it mutates game state on a frame the macro
does not record.

Resolved using that same accumulation property: turning by a total of D spread
over many paused frames leaves the same orientation as turning by D in one
frame. So the **game** gets the live delta while paused, and the **recorder**
gets the accumulated total on the stepped frame while the game gets zero there
(it has already turned). Hence `VirtualInput.Current` and
`VirtualInput.ForRecord` are separate frames — do not collapse them.

The one known imprecision: pitch is clamped to `[minXRotation, maxXRotation]`,
so a paused sweep that pushes past the clamp and comes back could land
differently than one delivered in a single frame. Not expected to matter in
practice.

### Macro format and speed

Plain text, one line per **simulated frame**, header carrying `tickRate`,
`physicsRate`, `rngSeed`, `seedManagerSeed` and `scene`. Text because a TAS is
edited far more often than recorded.

**Recording works at any speed** — slow-motion, real time, or turbo — because
speed is wall-clock pacing only and never reaches the simulation. Two runs with
the same inputs produce identical macros regardless of the speed they were
picked out at. **Playback is always 1x**, deliberately: the slow part was
composing the inputs, and pinning it removes a knob that could only be set wrong.

`UnityEngine.Random.InitState(rngSeed)` is called at both record start and
playback start, which covers Granny's waypoint picks. `SeedManager.Seed` cannot
be restored mid-run (item placement is decided at level load), so it is recorded
to **verify** against and playback warns loudly on a mismatch.

All parsing/formatting uses `CultureInfo.InvariantCulture` — a comma decimal
separator would otherwise write `0,5` into a comma-separated field and silently
corrupt every macro saved.

Macros now use `.grannytas` extension (`UserData/GrannyTAS/macro.grannytas`).
Every `StopRecording` also archives a timestamped copy to `Saved/` so recordings
are never lost to the next take. The panel lists all saved macros with clickable
Load buttons (cached in `MacroEngine.SavedMacros`, headers peeked by
`MacroFile.PeekHeader` without parsing frames) rather than a text-entry field,
because the panel is mouse-only per the Dear ImGui constraints.

### Replay drift and why pickups were the thing that broke

A 3073-frame macro replayed with **every delivered input edge identical** — 0
differing edges across 1074 traced frames — and still lost one pickup: the
Hammer was taken at frame 2821 instead of 2916. What differed was 42 frames
where `PickRay`'s ring was on a different target, i.e. where the pickup ray
pointed.

`PickRay.Update` casts that ray from **its own transform**, and the whole pickup
state machine resolves inside one `Update` call — key edge, gates, raycast,
first-match-wins item dispatch, `Inventory.PickupItem` — with no cooldown, no
time dependency, and no duplicate guard ([full trace with addresses](docs/ida-pickup-findings.md)).
So nothing about the pickup path is timing-fragile. What is fragile is *where
the player is standing*: `CharacterController.Move` resolves collisions in float
arithmetic against an Update/FixedUpdate interleaving whose phase a recording's
pauses do not preserve, so a replay drifts. Sub-millimetre drift is invisible in
movement and decisive at a grazing ray angle — the ray hits a different collider
and the press is refused, or lands a frame early on a different item.

Hence `InputFrame.PlayerPosition`: the player's world position is recorded per
frame (a twelve-value pose field; nine-value macros still load) and
`VirtualInput.ApplyPose` puts the player back on it at each replayed frame's
issuance boundary, with the `CharacterController` disabled around the write.
`MaxPositionDrift` is measured whether or not the correction is enabled and
reported when playback stops, so a genuine desync stays visible instead of being
silently absorbed. `PinPlaybackPosition` (default on) turns the correction off
for anyone who wants pure input replay.

Two related notes: a non-integral tick:physics ratio (165/75 = 2.2) leaves the
fixed-step phase unpinned and makes drift worse — prefer an integral pair such
as 150/75. And `PickupTrace` in the config logs the whole gate chain plus what
the ray actually hits, which is how the above was established rather than
guessed; see `src/PickupDiagnostics.cs`.

### World snapshot

Replaying inputs only reproduces a run if the world starts in the same state —
Granny roams, so pressing play with her across the house replays the inputs
against a different world entirely.

`WorldSnapshot` captures transforms at record start (player + `AI_Granny`,
`AI_Grandpa`, `AI_MomSpider`, `AI_Slendrina`, `AtticSpider`, `RatEnemy`,
`SnowManAI`) keyed by full hierarchy path, stores them in the macro header, and
restores them on playback. Two mechanics matter:

- **`CharacterController` must be disabled around the player's teleport**, or it
  resolves against the position it still believes it has and partly undoes the write.
- **`NavMeshAgent.Warp` + `ResetPath`** for enemies — an agent owns its position
  and drags the transform back on the next tick otherwise.
- `AI_Granny.StopChase()` / `ResetAIDecision()` are called so the AI
  re-evaluates from the restored position.

**This is a positional restore, not a state restore.** Internal AI timers,
animation phase, and which doors were already opened are *not* captured — doing
so would mean serializing most of the game. Reload the level for a clean base if
a replay drifts.

### Dear ImGui

Real Dear ImGui via **ImGui.NET 1.91.6.1** plus native `cimgui.dll`. Deployment:
managed wrapper -> `UserLibs\` (MelonLoader resolves mod dependencies there;
`Mods\` would make it try to load it *as* a mod), native `cimgui.dll` -> game
root so the P/Invoke resolves.

Unity ships no ImGui backend, so `ImGuiHost` is one — it rasterises ImGui's draw
lists through Unity's immediate-mode `GL` API inside `OnGUI`'s repaint pass.
Constraints worth knowing:

- **Shaders cannot be compiled at runtime**, so the material must be found among
  shaders the game already ships. `UI/Default` is the target (texture + vertex
  colour = exactly ImGui's vertex format); `Sprites/Default`,
  `Hidden/Internal-GUITexture`, `Unlit/Transparent` are fallbacks.
- **The font atlas is row-flipped on upload**, because Unity's
  `LoadRawTextureData` is bottom-row-first and ImGui's atlas is top-row-first.
- `OnGUI` fires several times per frame; everything runs on `EventType.Repaint`
  only, since ImGui wants one NewFrame/Render pair and GL only draws there.
- `CopyLocalLockFileAssemblies` must stay `true` — library projects do not copy
  NuGet assemblies to the output directory, so `ImGui.NET.dll` would never reach
  the deploy step.

- **Clipping is done on the CPU.** Unity's GL wrapper exposes no scissor
  rectangle, and ImGui emits a clip rect per draw command that the backend is
  expected to honour. Ignoring them is visible immediately: the last widget in a
  size-constrained window draws straight through the bottom border. `DrawData`
  therefore clips each triangle against its command's rect
  (Sutherland-Hodgman, interpolating position, UV and colour) and fans the
  result. Triangles wholly inside the rect — almost all of them — take a fast
  path and are emitted untouched, so the cost is confined to window edges.

Known limit: **no keyboard input** — the panel is mouse-only, which is fine
because every control is also a hotkey.

Every entry point is guarded and sets `ImGuiHost.Failed`, which falls back to the
legacy IMGUI overlay. A missing `cimgui.dll` or a stripped shader should cost the
panel, not the whole interface.

## Status

- [x] Identify game, engine version, scripting backend
- [x] Install MelonLoader; generate `Il2CppAssemblies`
- [x] Decompile `Assembly-CSharp.dll`; map player controller, input path, RNG
- [x] IDA pass for the arithmetic-level unknowns (deltaTime usage, per-frame AI RNG)
- [x] Decide tick-pump architecture (`captureDeltaTime`, speed-primary)
- [x] Implement frame stepper (`TimeController`)
- [x] Implement speed / tick / physics-rate controls
- [x] Virtual input layer (`VirtualInput`) — pause freezes input, edges land on simulated frames
- [x] Input buffer for frame stepping; live look while paused
- [x] Capture RNG seed + `SeedManager.Seed` into a macro header
- [x] Implement macro record + playback
- [x] World snapshot so playback starts from the recorded positions
- [x] Dear ImGui panel (ImGui.NET + custom Unity GL backend)
- [x] Drop arm/disarm; auto-engage behind a player-control gate (`PlayerGate`)
- [x] Rebindable, persisted hotkeys (`Keybinds`)
- [x] Dedicated config file (`TasConfig`) — keybinds, rates, panel prefs survive a restart
- [x] Panel frees the cursor on open and re-derives the lock state on close (`CursorController`)
- [x] Diagnose replayed pickups (input replay is exact; player-position drift moved the ray)
- [x] Record player position per frame and correct replay drift (`PinPlaybackPosition`)
- [x] Verify a recorded run replays identically (the actual acceptance test) (1912 frames, 0.001 mm drift; pickup edge case pending)
- [x] ImGui clip rects honoured (CPU triangle clipping in `ImGuiHost.DrawData`)
- [ ] ImGui keyboard/text input if ever needed
- [x] Multiple macro slots / load by name (auto-archived timestamped `.grannytas` files, clickable load-list in panel)
- [x] Locate `FallSpeed` accumulation (likely `FallingHolder`) and confirm linear `dt`
