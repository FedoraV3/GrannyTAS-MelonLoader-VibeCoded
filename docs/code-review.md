# grannytas review — 2026-09-11

version 0.3.1 fixes pause/interaction state handling, macro serialization,
settings persistence, and interface cleanup. deterministic replay remains
unverified; this is a positional replay tool, not a complete savestate system.

## findings and changes

| priority | finding | change |
| --- | --- | --- |
| high | movement and fall bookkeeping keep running at zero delta. pickup rejects a falling/landing player and hides the prompt. | freeze `MobileFPS.Update`, `FallingHolder.Update`, `PickRay.Update`, and `DoorRay.Update` while the engaged simulation has zero delta. the actual pickup outcome still needs an in-game check. |
| high | key/mouse press and release edges remain true across paused render frames. | freeze input reads without destroying the previous simulated input; consume transitions only when simulation advances. escape can still open the game menu while tas-paused. |
| high | live paused aiming changes the camera outside recorded frames; pitch clamping makes summed deltas inequivalent. buffered scroll was recorded but never delivered. | buffer aim and scroll, then deliver exactly the same values to the game and recorder on step/resume. paused aiming now moves the camera on the next step/resume. |
| high | startup sync copies controller defaults over loaded rates; loaded keybind entries never update the live binding map. | hydrate the controller once before sync, reload live bindings after preferences load, and retain settings across engagement. |
| high | stopping tas ownership can replace a newly opened game's pause with the old timescale. | preserve the game's pause value and restore the preexisting capture delta. |
| high | saved axes/transforms round to six decimal places, changing playback inputs and start poses. | use round-trip float formatting, retain every stored key, and store initial held input to reproduce the first input edge. |
| high | malformed/truncated files silently substitute zero/default values. direct writes can destroy the previous recording. | validate version, finite values, frame shape/count, transforms and rate bounds; flush a sibling temporary file before replacement. |
| high | rates can change during a macro even though the header stores only one pair. transport actions can replace an active recording without saving it. | lock simulation rates during capture/playback; refuse conflicting record/play/load actions; reject incompatible scene/item seed. |
| medium | recording hotkey runs after capture, leaving an unrecorded update after its snapshot. | handle transport before the current update's input/capture decision. engine-selected delta determines whether that frame counts. this does not resolve fixed-update ordering. |
| medium | a failed navmesh warp is counted as restored; teleport exceptions can leave the character controller disabled. | abort playback on warp failure and restore controller enablement with `finally`. reset rng after ai restore calls. |
| medium | completing a rebind can immediately fire the newly bound action. arbitrary game action rebinds can fall outside the sampled key list. | consume the entire rebind frame, sample defined keyboard keys, and exclude tas transport keys from gameplay recording. mouse keycodes 0–2 share the mouse-button state. |
| medium | reset does not affect a disengaged controller or clear a physics override; failed saves drop the dirty flag. | apply reset to both controller states, restore automatic physics adoption, and retry failed saves. |
| medium | an imgui failure can leave the cursor captured; drawing errors can leave gl state unbalanced; shutdown only flushes preferences. | release failed panels, guard frame/render calls, balance gl cleanup, save active recording on shutdown, and release timing/input/cursor/resources on unload. disable imgui ini persistence as originally intended. |

## pickup investigation

the local ida mcp was reachable on `127.0.0.1:13337` through a read-only helper
even though its tools were absent from the session tool catalog.
[native excerpts](ida-review-evidence.txt) retain call-site addresses.

- `PickRay.Update`, `0x180237ce0`: reads `MainInteract` through `GetKeyDown` at
  `0x18023954b`. it accepts that edge only when its ring is already active and
  the player passes pause, falling, landing, jumping and jumpscare checks.
- the ray-hit path independently checks falling/landing at `0x18023a0e1` and
  `0x18023a11e`; rejection clears `buttonClicked` and hides the ring at
  `0x180242515` / `0x18024252e`.
- `FallingHolder.Update`, `0x1802188d0`: reads controller grounding at
  `0x18021896d`, then sets `isFalling=true` at `0x1802189b0` before accumulating
  the delta-scaled timers. zero delta therefore does not prevent this mutation.

inference: a zero-distance controller update losing its grounded result can
activate precisely the pickup rejection path the user reported. the database
already contained an analyst comment describing this scenario; the branches
above were checked independently. the runtime grounding transition has not
been reproduced in this review. the patch preserves genuine falling state
rather than forcing the player grounded or bypassing pickup requirements.

## remaining limits

- **no complete world restore.** positions and look pitch omit doors, inventory,
  ai timers, paths, animation/coroutine state, falling state and other gameplay
  state. replaying in a changed level is not guaranteed to reproduce a run.
- **player-loop ordering and fixed-step phase remain unverified.** sampling in
  melonloader `OnUpdate` does not establish delivery before every game's
  `Update`, and occurs after the frame's fixed updates. matching rates does not
  restore the accumulator phase. a dedicated player-loop boundary and recorded
  state/tick comparisons are needed before claiming bit-identical simulation.
- **input coverage is keyboard plus three mouse buttons and five axes.** string
  key overloads, named buttons, joystick input, and separate smoothed/raw axis
  streams are not recorded. both axis getter patches currently return the raw
  sampled value. these need a native caller audit before broader claims.
- **freeze coverage is targeted.** other update-driven gameplay and coroutine
  state can still change at zero delta; the new patches address the verified
  player/fall/interaction path, not a complete player-loop pause.
- **snapshot identities use hierarchy names.** identically named siblings and
  inactive objects are not robustly resolved. legacy v1 files without initial
  input use an empty initial state.
- **requested speed is approximate.** integer frame caps round the requested
  rate, and performance can fall below that cap. requested 0.01x at 60 ticks/s
  becomes a 1 fps cap, approximately 0.0167x.

the previous notes incorrectly called `deltaTime` inside `FixedUpdate` a
render-delta bug. unity explicitly returns `fixedDeltaTime` in that context.
[unity 2022.3 reference](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Time-deltaTime.html).
the update/fixed-update ordering concern still applies.
[execution order](https://docs.unity3d.com/2022.3/Documentation/Manual/ExecutionOrder.html).

## validation

```powershell
dotnet run --project tests/GrannyTAS.Regression --no-restore
dotnet build GrannyTAS/GrannyTAS.csproj -c Release -p:NoDeploy=true --no-restore
```

48 regression checks exercise linked production code against small engine and
loader boundary stubs. they cover serialization precision/corruption, initial
input, frozen edges and gameplay guards, buffering, timing transitions, settings,
rebinds and macro transport. they do not execute unity, native harmony detours,
physics, or the imgui renderer. first test restore needs the .net 8 reference
pack; it was fetched and the checks passed in this environment.

in-game acceptance still required:

1. from a fresh level, look at a nearby item while grounded. pause with f2 and
   verify its pickup prompt remains. tap the interaction key, then f3: exactly
   one pickup should happen. retry with a door and crouching.
2. step ten frames with f4. check that no input edges fire during the paused
   intervals and that the counter increases by ten. test real falling too.
3. buffer aim while paused and step/resume. verify it applies once. the panel
   displays accumulated aim; closing it restores mouse control.
4. open the game's escape menu from both running and tas-paused states. verify
   the game stays paused and the menu retains its cursor.
5. verified: 1912-frame gameplay sequence recorded at 165 tick/s, 75 Hz
   physics replayed identically three times in the same session; max position
   drift 0.001 mm, zero frames corrected. caveat: sequence contained no pickup
   interaction; a follow-up test exercising a pickup/door press is needed to
   validate that specific edge case against the known Hammer-frame drift.
