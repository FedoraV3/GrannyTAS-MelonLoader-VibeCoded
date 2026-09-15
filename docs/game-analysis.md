# Granny Legacy 1.8.9 — game analysis

Source: `MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll`, decompiled with
`ilspycmd`. **Not obfuscated** — 483 types with clean, readable names.

> Proxy assemblies carry full type/method/field **signatures**, but method
> **bodies are stubs** that trampoline into `GameAssembly.dll`. Everything below
> is derived from signatures and member names. Where exact arithmetic matters
> (e.g. whether a term is multiplied by `deltaTime`), it must be confirmed in
> IDA against `GameAssembly.dll` — flagged **UNVERIFIED** where that applies.

Full type list: [game-types.txt](game-types.txt)

## Player controller — `Il2Cpp.MobileFPS`

The PC build still uses the mobile-derived controller class. **`CharacterController`-based, driven entirely from `Update()`. It has no `FixedUpdate`.**

Call chain:

```
Update()
  -> HandlePlayerMovement() / HandlePlayerMovementS()
  -> HandleVerticalMovement()
  -> AdjustSpeed()
  -> ApplyFinalMovements()   -> CharacterController.Move()
```

Other methods: `Awake`, `Start`, `DragHead`, `DragHeadTimer` (coroutine),
`GetTouchInput`, `OnControllerColliderHit`, `ResetRotation`,
`ResetRotationSlendrina`, `ReserRotation` (sic).

### Fields that define the movement model

| Group | Fields |
|---|---|
| Motor | `characterController`, `moveDirection`, `moveSpeed`, `SpeedMove`, `SpeedMoveCrouch`, `SpeedFade` |
| Vertical | `FallSpeed`, `FallingHolder`, `groundCheck`, `groundCheckRadius`, `groundLayerMask`, `WasOnPlatform` |
| Look | `rotationX`, `cameraRotationSpeed`, `maxXRotation`, `minXRotation`, `playerCamera`, `playerCamera2`, `CamK`, `CamPivot` |
| State gates | `AbleToMove`, `isAllowedToMove`, `isMoving`, `IsCrouched`, `InWeb`, `InElev` |
| Elevator | `ElevatorObj`, `ElevatorYAxis`, `LastElevPos` |

This is a textbook `deltaTime`-scaled `CharacterController` FPS controller.

## Input — legacy `UnityEngine.Input`

`KeyCode` appears in `KeyBindManage`, `DoorRay`, `PickRay`, `CrouchHolder`,
`CarManager`, `RayRemoveTraps` — so the game uses the **legacy Input Manager**,
not the new Input System package.

`KeyBindManage` exposes rebindable actions with `KeyCode` fields and an
`allowedKeys` list: **PrimaryInteraction, SecondaryInteraction, Crouch, Drop,
Shoot** (`DetectKeyPress`, `StartRebind`, `ConfirmNewKey`).

`GameInputManager` has only `Instance` and `TouchedScreen` — it is the mobile
touch path and is irrelevant on PC.

**Consequence:** macro playback injects by patching `UnityEngine.Input`
(`GetKey`, `GetKeyDown`, `GetKeyUp`, `GetAxis`, `GetAxisRaw`, `GetMouseButton*`)
— a single well-defined choke point. Movement axes and mouse look must be
captured too, not just the rebindable action keys.

## Enemy AI — runs on `FixedUpdate`

Exactly six types implement `FixedUpdate`:

`AI_Granny`, `AI_Grandpa`, `Eyes_Granny`, `Eyes_Grandpa`, `Eyes_MomSpider`, `KickHatch`

`LateUpdate` users: `CameraController`, `AttachToConstrainedParent`,
`FollowHand` (+ TMP internals).

### This is the central design fact

> **The player advances on `Update`. The enemies advance on `FixedUpdate`.**

The two run at independent rates, and the *interleaving* of them decides when
Granny's vision cone (`Eyes_Granny`) samples the player's position. Any drift in
that interleaving desynchronizes a replay even if the player's own inputs are
reproduced perfectly.

Two things follow:

1. It vindicates the requirement that **tick limit and FPS limit be separate
   knobs** — they genuinely drive different halves of the simulation.
2. **A macro must pin the Update:FixedUpdate ratio**, not just the input stream.
   Record the frame count and the fixed-tick count together, and reproduce both.

## RNG — `Il2Cpp.SeedManager`

Item/puzzle randomizer, run at level load, not per frame. Relevant fields:
**`Seed`**, **`RandomizeSeed`**, `rnd` (a `System.Random`), `MaxAttempts`,
`EscapeItemPuzzleChance`, `allItems`, `spawnAreas`, `puzzleDefs`,
`originalStates`, `usedItemNames`, `usedPuzzleSpawns`.

Methods: `GeneratePlacement`, `Shuffle`, `PlaceItemInFreeArea`,
`PlaceItemAtTransform`, `IsSafeContainerPlacement`, `ValidatePuzzleDependencies`,
`DetectCircularDependencies`, `SaveOriginalStates`, `RestoreOriginalStates`.

**Handling:** this is the easy case. Item placement is decided once from an
explicit integer seed. A macro stores the `Seed` it was recorded under; playback
sets `RandomizeSeed = false` and writes `Seed` back before level load. No
per-frame RNG interception needed for item layout.

**UNVERIFIED:** whether enemy AI additionally uses `UnityEngine.Random` per
frame (roam target selection, etc.). If it does, `Random.state` must also be
captured and restored. Check `AI_Granny` / `AI_Grandpa` in IDA.

## Determinism hazards, restated concretely

| Hazard | Status |
|---|---|
| Player movement scaled by `Time.deltaTime` in `ApplyFinalMovements` | Very likely. **UNVERIFIED** — confirm in IDA. Forces a fixed delta during record and playback. |
| Mouse look **not** scaled by `deltaTime` (raw per-frame `GetAxis` delta) | Typical for this controller shape. **UNVERIFIED.** If true, total rotation depends on frame *count*, so replay must reproduce frames 1:1 — which the tick-indexed design already does. |
| Update/FixedUpdate interleaving | **Confirmed hazard.** See above. |
| Item placement RNG | Solved via `SeedManager.Seed`. |
| Per-frame AI RNG | **UNVERIFIED.** |
| Coroutine timers (`WaitForSeconds` in ~30 `_Timer*_d__NN` state machines) | Scale with `Time.timeScale`, so they follow speed changes correctly — *unless* any use `WaitForSecondsRealtime`. **UNVERIFIED.** |

## Open questions for IDA

The `.i64` database (`GameAssembly.dll.i64`) is already analyzed and sits next to
the game. Questions that need it:

1. `MobileFPS.ApplyFinalMovements` / `HandleVerticalMovement` — exact `deltaTime`
   usage, and whether `FallSpeed` integrates with `deltaTime` or `deltaTime²`.
2. `MobileFPS.DragHead` — is mouse look `deltaTime`-scaled?
3. `AI_Granny.FixedUpdate` — any `UnityEngine.Random` calls?
4. Any `Time.unscaledDeltaTime` / `realtimeSinceStartup` /
   `WaitForSecondsRealtime` in gameplay paths (these would ignore `timeScale`
   and break speed hacks).
