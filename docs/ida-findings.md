# Granny Legacy 1.8.9 — Native RE Findings for TAS Determinism

IDB: `C:\Users\ir0n1c\Desktop\Granny_Legacy\GameAssembly.dll.i64` (imagebase `0x180000000`)
Session date: 2026-09-11. Full decompiled listings referenced below are archived in
`docs/ida-raw-evidence.txt` under the
`[[IDA:...]]` tags noted per section — search that file for the exact pseudocode/disasm quoted here.

All addresses were resolved from IL2CPP method tokens via the `Assembly-CSharp.dll`
`Il2CppCodeGenModule` struct (see "Token → address bridge" at the end); every resolved address was
cross-checked against the IL2CPP-derived symbol name already present in the IDB (e.g.
`MobileFPS$$ApplyFinalMovements`), so these are verified, not guessed.

---

## Q1 — Player movement time-scaling

**Functions:** `MobileFPS::ApplyFinalMovements` (`0x18022ce70`), `MobileFPS::HandleVerticalMovement`
(`0x18022d5f0`), plus the inlined duplicate movement/fall logic inside `MobileFPS::Update`
(`0x18022d9c0`). Tag: `[[IDA:MobileFPS_ApplyFinalMovements_0x18022ce70]]`,
`[[IDA:MobileFPS_HandleVerticalMovement_0x18022d5f0]]`.

**ApplyFinalMovements:**
```
deltaTime = UnityEngine.Time.get_deltaTime();
motion.x = moveDirection.x * deltaTime;
motion.y = moveDirection.y * deltaTime;
motion.z = moveDirection.z * deltaTime;
CharacterController.Move(motion);
```
`moveDirection` is a velocity-like vector (units/sec, built in `Update()` as
`moveSpeed * inputVector` then `TransformDirection`'d). It is multiplied by `Time.deltaTime`
**exactly once** before being handed to `CharacterController.Move`. This is the correct,
frame-rate-tolerant pattern (`distance = velocity * dt`), not `dt²` and not a bare per-frame
constant.

**HandleVerticalMovement:**
```
if (characterController.isGrounded) return;   // no gravity while grounded
motion = FallSpeed * Vector3.down * Time.deltaTime;   // single dt multiply
CharacterController.Move(motion);
```
`FallSpeed` is read as a plain scalar (m/s) here — its own accumulation (`FallSpeed += g*dt`) was
**not** found inside this function; it must happen elsewhere (most likely in the `FallingHolder`
component, not decompiled this session). Within `HandleVerticalMovement`/`Update`'s inlined
duplicate, the pattern is `move(FallSpeed_scalar * dt)`, i.e. a single linear `dt` term, not `dt²`.

**Implications for TAS determinism:** Horizontal and vertical `CharacterController.Move()` calls
both scale the per-frame delta by `Time.deltaTime` exactly once (not `dt²`, not omitted). As long
as `FallSpeed`'s own update-elsewhere also uses `Time.deltaTime` linearly (not verified this
session — flagged as an open item), total player-move distance per second should be frame-rate
independent. This is good news for TAS: distance traveled should not change with render FPS,
though `Update()` (not `FixedUpdate()`) drives movement, so higher FPS still means more Move() calls
per second — each individually scaled correctly, but discrete CharacterController collision
resolution is being computed more/less often depending on framerate, which *can* still cause
frame-rate-dependent physics edge cases (e.g. collision snagging, slope handling) even though the
raw distance math is frame-rate-scaled correctly.

---

## Q2 — Mouse look

**Function:** `MobileFPS::GetTouchInput` (`0x18022d030`) — this is the actual mouse-look code, not
`DragHead`/`DragHeadTimer` (those only start a coroutine that re-centers the camera pitch after a
0.45s `WaitForSeconds`, unrelated to per-frame look). Tag:
`[[IDA:MobileFPS_GetTouchInput_0x18022d030]]`.

```
yawDelta = InputUnsafeUtility.GetAxis(StringLiteral_953) * cameraRotationSpeed;
rotationX -= InputUnsafeUtility.GetAxis(StringLiteral_955) * cameraRotationSpeed;
rotationX = Clamp(rotationX, minXRotation, maxXRotation);
playerCamera.localRotation = Quaternion.Euler(rotationX, 0, 0);      // pitch
transform.rotation = transform.rotation * Quaternion.Euler(0, yawDelta, 0);  // yaw
```

- **No `Time.deltaTime` (or any Time function) is called anywhere in `GetTouchInput`.** Mouse input
  is applied as a raw per-frame `Input.GetAxis` delta multiplied only by the constant
  `cameraRotationSpeed`.
- **Input API confirmed:** `UnityEngine.Internal.InputUnsafeUtility$$GetAxis` — the native backing
  for the **legacy** `Input.GetAxis(string)` call, not the new Input System.
- The two string-literal arguments (`StringLiteral_953`, `StringLiteral_955`) could not be read
  directly from `GameAssembly.dll` — IL2CPP string literals are resolved from `global-metadata.dat`
  at runtime, and that file was not found on disk in this session (searched
  `C:\Users\ir0n1c\Desktop\Granny_Legacy` and `C:\Users\ir0n1c\Desktop`). Based on field naming
  (`cameraRotationSpeed`) and the classic FPS-look structure (pitch clamp + body yaw), these are
  almost certainly `"Mouse Y"` and `"Mouse X"` respectively — **flagged as inferred, not directly
  read**.
- Pitch (`rotationX`) is clamped every call via `Mathf.Clamp(rotationX, minXRotation, maxXRotation)`
  semantics (confirmed by the compare-and-select codegen).
- `GetTouchInput` is only invoked from `MobileFPS::Update` when `this->CamK == true` **and** a
  Horizontal/Vertical movement axis is non-zero (see `Update` disassembly) — i.e. mouse look in
  this build is gated behind movement input, not called unconditionally every frame.

**Implications for TAS determinism:** Since mouse look uses a raw, non-time-scaled
`Input.GetAxis` delta, look direction after N frames of a given (input, cameraRotationSpeed) is
**not** frame-rate independent — Unity's legacy `Input.GetAxis("Mouse X")` already returns a
smoothed value that is itself dependent on how often it's polled (once per rendered frame). For a
TAS this means recorded mouse deltas must be tied to the exact frame timing/count they were
captured at; simply replaying "N degrees per input event" will reproduce identically **only if the
frame count and per-frame Input.GetAxis sampling matches exactly** — a speed-hack that changes
`Time.timeScale` will not affect this path at all (good), but changing the render frame rate will.

---

## Q3 — Per-frame RNG in the AI

**Functions checked:** `AI_Granny::FixedUpdate` (`0x1801bdcc0`, 2917 instructions), `ChooseWaypoint`
(`0x1801bda70`), `SensorPlayer` (`0x1801c19d0`), `ChaseAction` (`0x1801bda40`), `LookAtThings`
(`0x1801bc6c0`), `ResetAIDecision` (`0x1801c1930`), `StopChase` (`0x1801c2230`), `OnTriggerStay`
(`0x1801c1680`), `Eyes_Granny::FixedUpdate` (`0x1801ddb80`). Tag:
`[[IDA:AI_Granny_FixedUpdate_0x1801bdcc0]]`, `[[IDA:Eyes_Granny_FixedUpdate_0x1801ddb80]]`.

**`UnityEngine.Random.RandomRangeInt` calls found (exactly 2 call sites total in the whole
component set):**

1. **`AI_Granny::ChooseWaypoint`** (`0x1801bda70`, called `0x1801bdac8`):
   ```
   do { WaypointNumb = Random.RandomRangeInt(0, Waypoints.Count); }
   while (WaypointNumb == CurrentWaypoint);
   ```
   Only runs when a new patrol waypoint is chosen (waypoint reached / idle timeout) — **not every
   tick**.

2. **Inline in `AI_Granny::FixedUpdate`** at `0x1801c0080`:
   ```
   clipIndex = Random.RandomRangeInt(0, voiceClipList.Count);   // pick idle/voice AudioClip
   ```
   Verified via disassembly that this call is gated behind a walk-cycle timer state transition:
   a field at `this+0x1A0` accumulates `+= Time.deltaTime` every `FixedUpdate` tick; once it
   reaches a threshold at `this+0x1A4`, the timer resets and (if the "target reached" flag at
   `this+0x18A` is set) `ChooseWaypoint` is called **and** this random clip-index pick happens in
   the same branch. So both `RandomRangeInt` calls fire together, once per state transition, not
   once per tick.

**No `UnityEngine.Random` or `System.Random` calls found** in `SensorPlayer`, `ChaseAction`
(essentially empty — 0x30 bytes, likely a thin dispatcher), `LookAtThings`, `ResetAIDecision`,
`StopChase`, `OnTriggerStay`, or `Eyes_Granny::FixedUpdate`.

**NavMesh usage confirmed** (all via callee-list, in `FixedUpdate`/`SensorPlayer`):
`NavMeshAgent.SetDestination`, `.get_destination`, `.set_speed`, `.get_isStopped`/`.set_isStopped`,
`.get_velocity`, `.set_baseOffset`. These run as part of the per-tick chase/patrol state machine;
`SetDestination` itself is only called once per state transition (single call site, same pattern as
`ChooseWaypoint`).

**Correction (2026-09-11 review):** The timers do call `Time.get_deltaTime()`,
but this is not evidence that they use the render delta. Unity returns
`fixedDeltaTime` when `deltaTime` is queried inside `FixedUpdate`.
[Unity 2022.3 API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Time-deltaTime.html).

RNG draw count still depends on AI state transitions, player behavior, and the
Update/FixedUpdate interleaving. Matching rates alone does not restore the
accumulator phase or internal AI state. No RNG reseeding was observed in this pass.

---

## Q4 — Unscaled time / `Time.timeScale`-immune paths

Enumerated **every caller** of the four "escape timeScale" APIs via `xrefs_to`:

| Function | Address | Xref count |
|---|---|---|
| `Time.get_unscaledDeltaTime` | `0x18072b360` | 5 |
| `Time.get_unscaledTime` | `0x18072b390` | 34 |
| `Time.get_realtimeSinceStartup` | `0x18072b270` | 18 |
| `WaitForSecondsRealtime..ctor` | `0x180731480` | 6 |

**Every single caller across all four is UI/engine infrastructure, not gameplay:**
`UnityEngine.UI.CoroutineTween.TweenRunner`, `UnityEngine.UI.Button` (submit animation),
`UnityEngine.UI.ScrollRect`, `TMPro.TMP_InputField` (+ its caret-blink coroutine and mouse-drag
coroutine), `UnityEngine.UI.InputField` (+ caret-blink), `TMPro.TMP_Dropdown` /
`UnityEngine.UI.Dropdown` (delayed-destroy coroutines), `UnityEngine.UIElements.ScrollView` /
`PanelEventHandler` / `DefaultEventSystem.Input`, `UnityEngine.EventSystems.StandaloneInputModule`
/ `TouchInputModule`, `TMPro.Examples.TMP_FrameRateCounter` (a debug FPS display), the third-party
`com.ootii.Utilities.Debug.Log` (bone-controller library's own log timestamps —
`com.ootii.Actors.BoneControllers` is referenced by `AI_Granny` for a `LookAtMotor`, but only its
*logging utility* touches unscaled time, not the motor itself), and
`UnityEngine.Rendering.PostProcessing.GrainRenderer` / `Dithering` (visual noise animation only).

**None of these call sites are in `MobileFPS`, `AI_Granny`, `Eyes_Granny`, or any gameplay
`*_Timer_d__NN` / `*Process_d__NN` coroutine state machine.** Every gameplay function decompiled
this session (`ApplyFinalMovements`, `HandleVerticalMovement`, `Update`, `FixedUpdate` ×2,
`GetTouchInput`) either uses `Time.get_deltaTime` (scaled) or no time function at all.

**Implications for TAS determinism:** No evidence found that player movement, enemy AI, or the
sampled timer coroutine (`MobileFPS.DragHeadTimer`, which uses `WaitForSeconds` — the **scaled**
variant, not `WaitForSecondsRealtime`) escape `Time.timeScale`. A `Time.timeScale` speed-hack should
work uniformly across these systems. **Caveat:** only `DragHeadTimer` was individually verified as
using the scaled `WaitForSeconds`; the other ~29 `*_d__NN` coroutine state machines in the codebase
were not individually decompiled this session (budget) — the xref sweep above proves none of them
call the unscaled-time APIs *directly*, which is strong (not just circumstantial) evidence they're
all timeScale-affected, but a follow-up pass could explicitly decompile each if 100% coverage is
required.

---

## Bonus — `SeedManager.GeneratePlacement` RNG/seed determinism

Function: `0x18024f770`. Tag: `[[IDA:SeedManager_GeneratePlacement_0x18024f770]]`.

Field layout observed on the `SeedManager` instance (byte offsets):
- `+0x40`: `int Seed`
- `+0x44`: `bool` — a randomize-seed toggle (exact C# name not confirmed from this binary; inferred
  from control flow)
- `+0x50`: `System.Random rnd`

Disassembled entry sequence:
```
if (this->field_0x44 == 0)
    this->Seed = PlayerPrefs.GetInt(<key string>);          // persisted-seed path
else
    this->Seed = UnityEngine.Random.RandomRangeInt(0, 999999999);  // auto-seed fallback path

// retry loop, up to 50 attempts (var_2A8 counter, capped at 0x32):
attempt++;
this->rnd = new System.Random(this->Seed + attempt);
RestoreOriginalStates(); ... placement attempts (PlaceItemAtTransform / PlaceItemInFreeArea /
PlaceItemInSpecificFreeArea, SeedManager.Shuffle<T> for list shuffling) ...
```

**Confirmed:** `this.rnd` **is** a `System.Random` constructed from `Seed + attemptNumber`. Forcing
`Seed` (`+0x40`) to a fixed value **does** fully determine item placement, *provided* the
`+0x44` toggle is false (so `Seed` isn't overwritten from `UnityEngine.Random` first) and the
number of retry attempts taken is itself deterministic — which it should be, since attempt count is
driven by placement success/failure against the same PRNG stream and fixed world state.

`UnityEngine.Random.RandomRangeInt` is called **exactly once** in this entire function, only as the
auto-seed fallback described above — it does not otherwise participate in placement.
`SeedManager.Shuffle<T>` (`0x1802d7590`, a fully-shared-generic instantiation) draws its shuffle
randomness from `this->rnd` (via inlined thunks `sub_1800E18A0`/`sub_18019D430`/`sub_18019CD90`),
**not** from `UnityEngine.Random` — consistent with full determinism once `Seed` is fixed and the
`+0x44` toggle path is avoided.

**TAS implication:** To force deterministic item placement, set `SeedManager+0x40` (`Seed`) to a
known value and ensure `SeedManager+0x44` is `0` (or explicitly zero it) before `GeneratePlacement`
runs.

---

## Token → address bridge (for future sessions)

**Route used: Route B (IL2CPP codegen module)** — the `MethodAddressToToken.db` file (Route A) was
reverse-engineered but not used for final resolution (format notes below in case it's useful later).

`Assembly-CSharp.dll`'s `Il2CppCodeGenModule` struct, located via the `"Assembly-CSharp.dll"` string
at `0x1809e4b78` (single xref to the struct at VA `0x1809e4af0`):

```
+0x00 moduleName          (char*)   = 0x1809e4b78
+0x08 methodPointerCount  (u64)     = 0x82d (2093)
+0x10 methodPointers      (ptr)     = 0x180c1cab0   <- Il2CppMethodPointer[2093], indexed by RID-1
+0x18 adjustorThunkCount            = 0
+0x20 adjustorThunks                = 0
+0x28 invokerIndices                = 0x1809e29d0
```

Resolution formula: `addr = *(uint64*)(0x180c1cab0 + ((token & 0xFFFFFF) - 1) * 8)`

Every resolved address below was cross-checked against the pre-existing IL2CPP symbol name in the
IDB (e.g. `MobileFPS$$ApplyFinalMovements`), confirming correctness.

| Method | Token | Address | IDB symbol |
|---|---|---|---|
| MobileFPS.Start | 0x600058E | 0x18022d9b0 | MobileFPS$$Start |
| MobileFPS.Awake | 0x600058F | 0x18022cf00 | MobileFPS$$Awake |
| MobileFPS.Update | 0x6000590 | 0x18022d9c0 | MobileFPS$$Update |
| MobileFPS.GetTouchInput | 0x6000591 | 0x18022d030 | MobileFPS$$GetTouchInput |
| MobileFPS.HandlePlayerMovement | 0x6000592 | 0x18022d470 | MobileFPS$$HandlePlayerMovement |
| MobileFPS.HandlePlayerMovementS | 0x6000593 | 0x18022d2f0 | MobileFPS$$HandlePlayerMovementS |
| MobileFPS.ApplyFinalMovements | 0x6000594 | 0x18022ce70 | MobileFPS$$ApplyFinalMovements |
| MobileFPS.HandleVerticalMovement | 0x6000595 | 0x18022d5f0 | MobileFPS$$HandleVerticalMovement |
| MobileFPS.AdjustSpeed | 0x6000596 | 0x18022ce60 | MobileFPS$$AdjustSpeed |
| MobileFPS.DragHead | 0x6000598 | 0x18022cfc0 | MobileFPS$$DragHead |
| MobileFPS.DragHeadTimer (ctor) | 0x6000599 | 0x18022cf60 | MobileFPS$$DragHeadTimer |
| MobileFPS._DragHeadTimer_d__47.MoveNext | 0x60005A0 | 0x180242a30 | MobileFPS._DragHeadTimer_d__47$$MoveNext |
| MobileFPS.OnControllerColliderHit | 0x600059C | 0x18022d6e0 | MobileFPS$$OnControllerColliderHit |
| AI_Granny.Start | 0x6000108 | 0x1801c1ec0 | AI_Granny$$Start |
| AI_Granny.FixedUpdate | 0x6000109 | 0x1801bdcc0 | AI_Granny$$FixedUpdate |
| AI_Granny.ResetAIDecision | 0x600010C | 0x1801c1930 | AI_Granny$$ResetAIDecision |
| AI_Granny.SensorPlayer | 0x6000114 | 0x1801c19d0 | AI_Granny$$SensorPlayer |
| AI_Granny.ChaseAction | 0x6000115 | 0x1801bda40 | AI_Granny$$ChaseAction |
| AI_Granny.StopChase | 0x6000116 | 0x1801c2230 | AI_Granny$$StopChase |
| AI_Granny.LookAtThings | 0x6000117 | 0x1801bc6c0 | AI_Granny$$LookAtThings |
| AI_Granny.ChooseWaypoint | 0x600011B | 0x1801bda70 | AI_Granny$$ChooseWaypoint |
| AI_Granny.OnTriggerStay | 0x600011D | 0x1801c1680 | AI_Granny$$OnTriggerStay |
| Eyes_Granny.FixedUpdate | 0x60001BA | 0x1801ddb80 | Eyes_Granny$$FixedUpdate |
| SeedManager.Awake | 0x6000635 | 0x18024e910 | SeedManager$$Awake |
| SeedManager.Start | 0x6000636 | 0x180253750 | SeedManager$$Start |
| SeedManager.GeneratePlacement | 0x6000637 | 0x18024f770 | SeedManager$$GeneratePlacement |
| SeedManager.Shuffle\<T\> | 0x6000643 | 0x1802d7590 | SeedManager$$Shuffle___Il2CppFullySharedGenericType_ (found via name-glob, not the token table — see note below) |

Note on `Shuffle`: the `methodPointers[RID-1]` slot for token `0x6000643` read back as `0`. Because
`Shuffle<T>` is generic, IL2CPP compiles it as a single "fully shared generic" instantiation rather
than a concrete per-call-site method with its own token-indexed slot; it was located instead via
`func_query` name-glob `*Shuffle*`.

Supporting addresses:
- `UnityEngine.Time.get_deltaTime` — `0x18072b210`
- `UnityEngine.Time.get_unscaledDeltaTime` — `0x18072b360`
- `UnityEngine.Time.get_unscaledTime` — `0x18072b390`
- `UnityEngine.Time.get_realtimeSinceStartup` — `0x18072b270`
- `UnityEngine.WaitForSecondsRealtime..ctor` — `0x180731480`
- `UnityEngine.Random.RandomRangeInt` — `0x180728f30`
- `System.Random..ctor(int seed)` — `0x1804680d0` (`System.Random$$.ctor_6447071440`)

### `MethodAddressToToken.db` format (reverse-engineered, NOT used for final resolution)

`C:\Users\ir0n1c\Desktop\Granny_Legacy\MelonLoader\Il2CppAssemblies\MethodAddressToToken.db`

```
offset 0:  "UMTM" (4 bytes magic)
offset 4:  u32 version = 1
offset 8:  u32 assembly_count = 0x51 (81)
offset 12: u32 method_count = 0x78ca (30922)
offset 16: u32 assembly_table_size_bytes = 0x1a2c (6700)
offset 20: assembly_table — 81x { u8 len (7-bit/LEB128-style, all <128 so single byte),
                                    ASCII bytes[len] } — full assembly display names
           (e.g. "Assembly-CSharp-firstpass, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null")
offset 20+6700=6720: method_table — 30922 x 16 bytes { u64 start_rva, u64 end_rva }
```

The method table is indexed by a **global method index** (cumulative across all 81 assemblies in
listed order), not directly by per-module token RID — mapping token RID → global index would
require a per-module method-count table that wasn't reconstructed this session. Abandoned in favor
of Route B once the codegen module was found (faster and directly verifiable).

---

## Player-control flags — `Il2Cpp.MobileFPS` (verified)

Field offsets read from the IDA struct `MobileFPS_Fields` (size 224;
`MobileFPS_o` = klass@0, monitor@8, fields@16), cross-checked against the
decompiled proxy. The *obj* column is what appears in disassembly.

| Field off | Obj off | Name | Type |
|---|---|---|---|
| 16 | 0x20 | `playerCamera` | `Transform` — the **pitch pivot**, not a camera |
| 32 | 0x30 | `playerCamera2` | `Camera` — the actual camera |
| 96 | 0x70 | `CameraAnim` | `Animation` |
| 116 | 0x84 | `IsCrouched` | `bool` |
| 117 | 0x85 | `isAllowedToMove` | `bool` |
| 118 | 0x86 | `AbleToMove` | `bool` |
| 119 | 0x87 | `CanFade` | `bool` |
| 120 | 0x88 | `CamK` | `bool` |
| 121 | 0x89 | `InWeb` | `bool` |
| 144 | 0x90 | `FallingHolder` | `Il2Cpp.FallingHolder` |
| 152 | 0x98 | `PS` | `Il2Cpp.PlayerStatus` |
| 160 | 0xA0 | `SystemDay` | `Il2Cpp.Days` |

There is **no** `canMove`, `canLook`, `isPaused` or `playerCanMove` field.

### The gates in `MobileFPS::Update` (`0x18022d9c0`)

In order, as decompiled:

1. `SpeedMove`/`SpeedMoveCrouch` overwritten each frame from `InWeb`.
2. `if (!Paused.IsPaused)` — wraps the **entire** body.
3. Ground `Physics.CheckSphere`, `CanFade` tilt lerp, `IsCrouched` speed pick.
4. `if (!isAllowedToMove)` → idle CrossFade, jump to the elevator/gravity tail.
   **No input read at all.**
5. `if (!AbleToMove)` → idle CrossFade, fall through to the look block.
   **No WASD read**, but look still runs if `CamK`.
6. Only with both set does it `GetAxis("Horizontal"/"Vertical")` and
   `CharacterController.Move`.
7. `if (CamK) GetTouchInput()`.

### Who writes the flags

**The only writes of `true`** to `isAllowedToMove` / `CamK` in the whole
assembly are in `Days::<StartDays>d__140::MoveNext` (`0x1801b4390`), six
identical groups — one per `Days.CurrentDay` branch:

```
IsCrouched = true; isAllowedToMove = false; InWeb = false;
MobileFPS.ResetRotation();
isAllowedToMove = true; IsCrouched = false; AbleToMove = true; CamK = true;
```

Clears: `PlayerStatus::PlayerGettingStopped` (`0x18024b820`, sets
`IsJumpscared` then clears both flags); all four `Escapes::Escape*`;
`ScreenEffects::FrozePlayer` (restored by `<TimerUnfroze>d__38`);
`BearTrapLogic::OnTriggerEnter` (restored by `RayRemoveTraps::Update`).

**Trap:** `Days::StartNew` (`0x1801b0070`) has a `[rax+85h], 0` store, but that
base is a `GetComponent<FallingHolder>()` result — **not** an
`isAllowedToMove` write.

### The wake-up sequence

`Days::<StartDays>d__140`, `CurrentDay == 1` timeline:

| State | What happens |
|---|---|
| 0 | `WaitForSeconds(2.0)` |
| 1 | `Cursor.visible = false`; day 0 → 1; `WaitForSeconds(3.0)` |
| 2 | day-text popup on, `BeganDay = true`, `WaitForSeconds(2.0)` |
| 3 | `BeganDay = false`, `BedNoise` on, `PlayerBedAnim.CrossFade`, `BlackScreen` fade, `WaitForSeconds(10.5)` |
| 4 | **`PlayerBedAnim.transform.parent.gameObject.SetActive(false)`**, `BedNoise` off, `UIElements`/`PauseManager` on, `DisableCaught()`, enemy spawn |

State 4 is the end-of-intro marker `PlayerGate` uses.

**`Days.BeganDay` is not a "gameStarted" flag** — true for ~2 s during the day
text, then false again.

**Unresolved:** nothing writes `isAllowedToMove`/`AbleToMove`/`CamK` on the
`CurrentDay == 1` path, so their values during the day-1 bed animation come from
serialized scene data and are not knowable from the binary. The panel's **Player
gate** readout exists to answer this at runtime.

### `Cursor.lockState` is not an in-control signal

All nine xrefs to `Cursor::set_lockState`: `MobileFPS::Awake` (`0x18022cf00`,
sets `Locked` **at scene load**), `MainMenu::Start`, `Paused::GoToMainMenu`,
`Paused::PauseGame` (x2), `Paused::Update` (x2), `Menu_Seed` (x2). It is already
`Locked` throughout the wake-up animation.

### Singletons

The only statics in `Assembly-CSharp` (excluding `<>c` caches,
`SeedManager.MaxAttempts`, `Outline.registeredMeshes`) are
**`Paused.IsPaused`** (`public static bool`) and
**`GameInputManager.Instance`** (mobile touch path; no `gameStarted`). There is
no `GameManager` and no `LevelManager` — `Il2Cpp.Days` is the closest thing, and
it is an instance reached via `MobileFPS.SystemDay`.

---

## FallSpeed accumulation (resolved)

Session date: 2026-09-14. Closes the open item: "`FallSpeed`'s own accumulation
was not located (likely in `FallingHolder`); confirm it integrates linearly in
`dt`." Tag: `[[IDA:MobileFPS_FallSpeed_accumulation_20260914]]`.

**Finding: there is no accumulation. `MobileFPS.FallSpeed` is a constant.**

`MobileFPS.FallSpeed` lives at field offset `0x3c` in `MobileFPS_Fields` (object
offset `0x4c` in `MobileFPS_o`). It is written **exactly once in the whole
binary**, in `MobileFPS::.ctor` (`0x18022e580`):

```
this->fields.FallSpeed = 9.8100004;   // 0x18022e597: mov dword ptr [rcx+4Ch], 411CF5C3h
```

A binary-wide search for any other store to that field offset (`movss`/`mov
dword ptr [reg+4Ch]`, all registers, whole `.text`/`il2cpp` segments) turns up
only this one instruction. `FallSpeed` is read — never written — by all three
places that consume it: `MobileFPS::ApplyFinalMovements` (`0x18022ce70`),
`MobileFPS::HandleVerticalMovement` (`0x18022d5f0`, confirmed via disasm at
`0x18022d649: movss xmm7, dword ptr [rbx+4Ch]`), and the inlined fall-motion
duplicate inside `MobileFPS::Update` (`0x18022d9c0`, at `0x18022e383`). All
three do the same thing: `motion = FallSpeed * Vector3.down * Time.deltaTime` —
a plain scalar times a single `Time.deltaTime` multiply, confirming the prior
finding that this is `distance = velocity * dt`, never `dt²`. There is no
`FallSpeed += gravity * dt` anywhere.

**`FallingHolder` does not touch `FallSpeed` at all — it has its own, unrelated
field also named `Speed`** at offset `0x4c` in `FallingHolder_Fields` (a
different class, different object layout; same numeric offset is coincidence).
`FallingHolder::Update` (`0x1802188d0`) accumulates a *fall-duration timer*,
not a velocity, once per frame while airborne:

```
this->fields.fallDuration = this->fields.fallDuration
                           + UnityEngine_Time__get_deltaTime() * this->fields.Speed;
// 0x1802189a1..0x1802189d5
this->fields.DurateCan = this->fields.DurateCan
                        + UnityEngine_Time__get_deltaTime() * 4.0;
// 0x1802189c9..0x1802189ee
```

`fallDuration` is compared against thresholds (`FallMega`, `FallDurationHold`,
`FallChecker`) to decide fall damage / landing sound, and is reset to 0 on
landing (`FallingHolder::HandleLanding`, `0x1802186d0`) — it is a **timer**, not
the vertical speed used for `CharacterController.Move`.

**Verdict: linear in `dt`, but not because of accumulation — `FallSpeed` never
accumulates.** It is a fixed constant (9.81, evidently meant to read as Earth
gravity but used as a flat fall *speed*, not an acceleration) multiplied by
`Time.deltaTime` exactly once per frame everywhere it's consumed. There is no
`v += g*dt` integration step anywhere in the binary, so the player's downward
speed is identical on frame 1 of a fall and frame 1000 of the same fall — it
does not ramp up. This is *more* deterministic than a real accumulator would
be (nothing to desync from a differing frame history), and the existing
finding that vertical motion is frame-rate-independent (`velocity * dt`, no
`dt²`) stands unchanged. The only residual frame-rate sensitivity is the
already-documented one: more frames per second means more discrete
`CharacterController.Move` calls while falling, so collision-resolution
granularity during a fall still varies with tick rate — pin tick rate as
already recommended, not because `FallSpeed` itself misbehaves.

Raw evidence archived under `[[IDA:MobileFPS_FallSpeed_accumulation_20260914]]`
in `docs/ida-raw-evidence.txt`.
