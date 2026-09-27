# Replay sync — bit-exact replay and the sync check (0.4.0)

Supersedes the "replay drift" notes in the archive for anything that
disagrees. Covers why replays still missed item pickups after position
pinning, what changed, and the per-frame check that now says whether a replay
was the recorded run.

## Evidence the old replay was not the recorded run

From `MelonLoader/Logs` (2026-09-14 / 09-19 sessions, 60 tick / 50 Hz):

| recording | replay result |
|---|---|
| 2856 frames at 0.23x, Granny chasing | 46.043 mm peak drift, 12 frames corrected |
| 973 frames at 0.15x, with pauses | 0.137 mm peak, 1 frame corrected — **identical on two replays** |
| 1041 frames at 0.37x, with pauses | 0.002 mm peak, 0 corrected |
| recording at 0.48x (09-19) | aborted on frame 0: `Duplicate rigidbody identity '0/6#0'` (418 errors) |

Two replays drifting by exactly the same amount means the difference was
systematic — something the recording and the replay did differently every
time — not noise.

## Causes and fixes

| # | cause | effect on pickups | fix |
|---|---|---|---|
| 1 | **Fixed-step phase never restored.** Which frames get a `FixedUpdate` depends on `time − fixedTime` at the first frame; a replay started wherever the engine happened to be (six different patterns at 60/50). | The game's `m_AutoSyncTransforms` is **False** (read from `globalgamemanagers`), so a collider moved by a transform — a drawer, a door, Granny — only becomes visible to raycasts at the next physics step. A pickup ray therefore saw the drawer front on one run and the item on the other. AI and rigidbodies also stepped on different frames. | **Pre-roll alignment** (`MacroMode.Aligning`, `MacroEngine.UpdateAligning`): before frame 0 the engine runs one coarse and one tiny exact frame of computed delta, no input, until the phase equals the recording's to within 50 ps. Then the world snapshot is restored and frame 0 issues. |
| 2 | **Clock not exact in double at non-1x speed.** `timeScale = cap/tick` (29/60 at 0.48x): the float product `capture × timeScale` rounded back to 1/60, the double product did not. | The engine's double clock drifted a few hundred ps per recorded frame relative to replay — enough over a long run to move a `FixedUpdate` onto a neighbouring frame. | `TimeController.RunningTimeScale` is the **power of two** nearest the capped speed; dividing by it is exact, so the delta is `1/TickRate` bit for bit in float and double. Pacing still comes from the frame cap. |
| 3 | **Camera rig not restored.** The pickup ray starts at the camera, which `MobileFPS.CameraAnim` head-bobs, `CrouchHolder` animates, `FallingHolder` smooths. | A replay started with the bob animation at a different point in its cycle, so every pickup ray for the whole run pointed a few mm elsewhere. | `PlayerRigSnapshot`: animation states (weight/time/speed), every local transform under the player, controller geometry, and movement/fall/crouch scalars are captured at record start and restored (then `Animation.Sample()`d) at replay start. |
| 4 | **Ray pose at cast time can still differ** even with the player's position pinned at the frame boundary: the player moves inside the frame, and the camera sits on top of that. | A grazing ray on a small item misses. | `InteractionPin`: `PickRay`/`DoorRay` record their world pose (and `isFalling`/`isLanding` for `PickRay`) at their `Update` prefix; on replay the same prefix puts them back **for the duration of that call only**, then restores what was there. Written only when the bits differ. |
| 5 | **Position correction rebuilt the CharacterController** (disable → move → enable). | Rebuilding drops the controller's grounded state; `FallingHolder` turns that into `isFalling`, which is one of `PickRay`'s rejection gates. The correction meant to save a pickup could cost one. | `VirtualInput.MoveCharacter`: write + `Physics.SyncTransforms()`. Falls back to the toggle for the session (and says so) if three consecutive corrections do not stick. |
| 6 | **Corrections wrote identical values every frame** — quaternions (which can renormalise in the last bit), rigidbody velocity (wakes a sleeping body and resets its sleep timer). | A replay that was in sync was nudged out of it by the corrections themselves. | Pose, rigidbody and rig writes now happen only when the live value differs **bitwise**; the old 1 µm deadband is gone, so any difference is also corrected exactly. |
| 7 | **Rigidbody identity ignored the scene**, so the level and `DontDestroyOnLoad` could produce the same path; one missing body aborted the whole replay. | Recording failed outright in some scene sets; an item picked up on replay but not in the recording ended the replay. | Identity is `scene:path#ordinal` (v4 identities still resolve); a missing body is counted in the report and the rest are still corrected. |

## The sync check

Every recorded frame (macro v5) carries a `FrameTrace` of the world at its
boundary. On replay the same trace is captured **before any correction**, and
`SyncReport` compares them. Corrections keep a divergence from compounding;
they can never hide it from the report.

| area | compared | exact means |
|---|---|---|
| clock | `deltaTime` bits; phase and elapsed time to 0.1 ns (absolute clocks differ between runs, so the same increment rounds differently in the last double bits) | same simulated time |
| fixed-step pattern | FixedUpdate steps since the previous frame | AI/physics ran on the same frames |
| player position / look pose | bitwise | — |
| camera | world pose of `playerCamera2` | head bob, crouch, landing smoothing agree |
| controller | grounded, velocity, height | — |
| fall flags | `isFalling`, `isLanding`, fall timer | pickup gates agree |
| random state | `UnityEngine.Random.state`, read via its engine icall (the managed accessor is stripped) | same number of random draws — Granny's waypoint and voice picks |
| enemies | pose and nav-agent velocity of every tracked AI | — |
| rigidbodies | full checkpoint, bitwise | — |
| ray pose / ray hit / ray hit as cast | pickup and door rays at cast time | "as cast" is after pinning: a difference there means the *world* under the ray differs, not the ray |
| pickups | `Inventory.PickupItem` calls per frame | the same items on the same frames |
| script order | whether the ray ran before or after the mod's update | engine-frame attribution is valid |

## Missed item pickup recovery

When a v5 macro records a pickup call but replay misses it, the replay waits
until the game's interaction window closes, reports the natural mismatch, then
equips the recorded item before issuing the next macro input. The final frame's
pickup window is handled when playback finishes. A successful natural pickup
is never repeated. The report lists forced, already held, failed, and skipped
recoveries separately; a forced pickup does not erase the original divergence.

For a generic item, recovery validates its inventory definition, runs the
game's drop logic through `PickRay.CheckItemDropping`, calls
`Inventory.PickupItem`, and checks that the hand object became active. It
removes a matching world item only when exactly one active source can be
identified. The hand is still equipped when no world source remains. If
several sources share the name, recovery stops rather than destroy an
unidentified object. The special shotgun item named `b` is skipped because it
requires an additional game method. Older macros without pickup traces cannot
recover missed items; record a new macro to enable this feature.

At the end of every replay: one summary line in the log and on the panel
(`sync: BIT-IDENTICAL over N frames`, or `sync: diverged at frame K (area: …)`),
plus a full report in `UserData/GrannyTAS/Reports/<macro>-<timestamp>.txt` —
the per-area table, the corrections that were applied, and the first 200
differences. The panel shows it live during the replay.

A macro recorded before this build has no traces: it still replays (with
position/rigidbody measurement as before) and the report says it could not be
fully checked. **Re-record** to get alignment, rig restore, ray pinning and the
full check.

## Checking it in the game

1. Load a level, wait for control, record a short run that picks up two or
   three items (at least one inside a drawer), at any speed, with a pause and
   a few frame steps in it. Save it.
2. Replay it. The log should show `fixed-step phase aligned to within … ns`,
   `Restored player rig: …`, and at the end `sync: BIT-IDENTICAL over N frames`.
3. If it diverges, open the report: the first area and frame is where to look.
   `ray hit (as cast)` differing means the ray was right but the world was not;
   `fixed-step pattern` or `random state` differing points at AI timing.
4. Turn off "pin pickup/door rays" in Settings and replay again: the report
   then measures the unassisted replay, and every divergence it lists is one
   the pin would have steered back.

## Format v5

Frame line: `keys|mouse|axes|rawAxes|pose|physics|trace`. The trace field is
`;`-separated tagged groups (`t:` clock, `g:` random state, `c:` camera,
`k:` controller, `f:` fall, `e:` enemy, `r:` ray, `p:` pickup), strings base64;
unknown tags are skipped. Header adds `rigValue=` / `rigAnim=` / `rigPose=`
lines. v1–v4 still load.

## What is still not restored

- **AI internal state.** Enemies are put back by position and their decision
  is reset; timers, current path and animation phase are not. With the phase
  aligned and the RNG seeded they usually agree, and the `enemies` / `random
  state` areas say when they do not. Reloading the level before recording and
  replaying gives both runs the same AI start.
- **Zero-delta frames.** Frames spent paused while recording still run every
  Update the mod does not freeze, and coroutines that `yield return null`
  advance on them; the replay has none. No divergence from this has been
  observed, but it is a difference.
- **Absolute `Time.time`.** The replay runs at a different absolute time, so a
  script comparing `Time.time` against a float threshold can round differently.
- **Script execution order** must be the same in both runs; the `script order`
  area reports it if not.
- **Coroutines in flight** (a crouch transition mid-way at record start) and
  PhysX internal caches (enhanced determinism is off in this build) are not
  restorable.
- **The game's own pause menu during recording** advances the clock on its
  transition frame without a macro frame; the `clock` area shows it.
