# PickRay.Update item-pickup path — IDA findings (2026-09-14)

Evidence gathered live from `GameAssembly.dll` via IDA MCP (imagebase `0x180000000`).
**IDB was not modified** (no renames/comments written), per task instruction.
Full raw decompile/disasm dumps referenced below are archived in
`for_claude_the_logic_pro.txt` under keyword `[[IDA:PickRay_Update_0x180237ce0]]`.

`PickRay.Update` (`0x180237ce0`, size `0xACF0`, 9444 instructions, 1821 basic
blocks) is a single monolithic `MonoBehaviour.Update` that handles **every**
hand-held/interactable prop in the game (guns, crossbow, chargebulb, freeze
trap, remote, and every generic collectible) as one long straight-line chain
of per-object blocks. There is exactly **one** call site to
`Inventory.PickupItem` (`0x1802459b0`) in the whole function: `0x18023f8e7`.

Struct layout used below comes from IDA's already-recovered
`PickRay_Fields` / `FallingHolder_Fields` / `WindowJumping_Fields` /
`PlayerStatus_Fields` local types (`type_inspect`). Object field offset =
Fields offset + `0x10` (klass + monitor header).

## 1. How the interact input is read

- **Key field**: `PickRay.MainInteract` (`int32` KeyCode, Fields `+0x528` →
  object `+0x538`). Read at `0x180239543` (`mov ecx,[rbx+538h]`), passed to
  `Input.GetKeyDown` at **`0x18023954b`**.
- **`buttonClicked`** is `PickRay.buttonClicked` (Fields `+0x4C0` → object
  `+0x4D0`), a `bool` field on the `PickRay` instance — **not local, but a
  genuinely persistent instance field**. However in practice it is set and
  consumed **within the same `Update()` invocation**: it is set once near the
  top of the function (`0x180239620`), then read/cleared by whichever
  down-stream item-dispatch block matches the raycast hit, further down in the
  *same* call. There is also an unconditional catch-all clear at
  `0x180242515` (see §2) reached whenever no per-item block's own gates
  passed. So functionally: **same-frame, not deferred to a later frame.**
  `Inventory.PickupItem` at `0x18023f8e7` fires on the same `Update()` call as
  the `GetKeyDown` edge that set `buttonClicked`, provided the raycast/gates
  below all pass on that frame.

- The **set** of `buttonClicked=true` at `0x180239620` is itself gated (all
  must hold, checked in this order after `GetKeyDown` returns true):
  1. `[rbx+28h]` = `PickRay.Ring` GameObject `.activeSelf` == true
     (`0x180239567`) — the crosshair "ring" prompt must be showing.
  2. `Paused.IsPaused` == false (`0x180239582`, static field via
     `Paused_TypeInfo+0xB8`).
  3. `PickRay.Player.GetComponent<FallingHolder>().isFalling` (object
     `+0x81`) == false (`0x1802395b5`).
  4. Same `FallingHolder.isLanding` (object `+0x82`) == false
     (`0x1802395e8`).
  5. `PickRay.WJ` (`WindowJumping_o*`, object `+0x98`) `.IsJumping`
     (object `+0x21`) == false (`0x180239601`).
  6. `PickRay.PlayerStatus` (object `+0x88`) `.IsJumpscared`
     (object `+0xA8`) == false (`0x180239617`).
  Only if all six hold does `0x180239620` set `buttonClicked=true`.

- A **second** key, `PickRay.DropKey` (Fields `+0x52C` → object `+0x53C`),
  is read at `0x180239627`/`0x18023962f` and sets **`buttonClicked2`**
  (Fields `+0x4C1` → object `+0x4D1`) at `0x1802396d3`, gated the same way
  plus one extra condition (`[rbx+48h] != 0`, a "looking at droppable" flag
  set by the very first raycast's name check). **This is the drop key, not a
  second pickup key** — `buttonClicked2` is consumed only by
  `PickRay.CheckItemDropping` (`0x180233070`), which clears it
  (`this->fields.buttonClicked2 = false`) after instantiating whatever the
  player is currently holding (crossbow bolt, shotgun-shell drop, freeze
  trap, etc.) — this is a *drop*, not an inventory pickup, and has no
  time/RNG dependency of its own.

- **No mouse button, no `Input.mousePosition`, no EventSystem/UI
  `Button.onClick`, no touch path** is used anywhere in the pickup dispatch
  chain. `Input.GetKey` (not `GetKeyDown`) is called twice
  (`0x18023fd1f`, `0x18023ff6a`), but both are **after** the
  `Inventory.PickupItem` call site, in code that reads `PickRay.ShootKey`
  for continuous weapon fire — unrelated to picking items up.

## 2. The rejection path — confirmed addresses and instances

The task's cited addresses are the **first** per-item block's gate (there is
exactly one such gate sequence guarding the entire generic item-name
dispatch chain that follows it, since later blocks reuse the *same*
`RaycastHit` struct without re-raycasting or re-checking these four flags —
see §4):

```
0x18023a094  call Physics.Raycast (distance = PickRay.field+0x508, "ParticleEffectElectricSpawn" slot reused as a float via layout aliasing — see note below)
0x18023a0a4  jz  loc_18024250B          ; raycast missed -> reject
0x18023a0aa..0x18023a0d4               ; GetComponent<FallingHolder>() on PickRay.Player
0x18023a0da  cmp [rax+81h], 0          ; FallingHolder.isFalling
0x18023a0e1  jnz loc_18024250B         ; TASK ADDR #1 — reject if isFalling
0x18023a0e7..0x18023a111               ; GetComponent<FallingHolder>() again
0x18023a117  cmp [rax+82h], 0          ; FallingHolder.isLanding
0x18023a11e  jnz loc_18024250B         ; TASK ADDR #2 — reject if isLanding
0x18023a124..0x18023a138               ; PickRay.WJ (WindowJumping_o*, +0x98)
0x18023a134  cmp [rax+21h], 0          ; WindowJumping.IsJumping
0x18023a138  jnz loc_18024250B         ; reject if IsJumping
0x18023a13e..0x18023a155               ; PickRay.PlayerStatus (+0x88)
0x18023a14e  cmp [rax+0A8h], 0         ; PlayerStatus.IsJumpscared
0x18023a155  jnz loc_18024250B         ; reject if IsJumpscared
```

`loc_18024250B` (the shared reject landing pad, referenced by all five jumps
above — confirmed via `xrefs_to`):

```
0x18024250b  call PickRay.TurnOffItemTexts     ; hides the item-name prompt UI
0x180242515  mov  [rbx+4D0h], 0                ; buttonClicked = false  (UNCONDITIONAL)
0x18024251c..0x180242590                       ; SetActive(false) on ring/crosshair UI, etc.
0x180242595  ...                               ; falls into unrelated per-frame housekeeping
                                                ; (watermelon-hint PlayerPrefs logic, other
                                                ;  UI active-checks — not part of pickup)
```

So: **`TurnOffItemTexts` + `buttonClicked = false` at `0x180242510`–
`0x18024252e` runs strictly AFTER the `GetKeyDown` block** in program order
(`0x18023954b` < `0x18024250b`) — both execute within the same `Update()`
call, confirming the whole state machine (key edge → gate → raycast → name
match → pickup) is resolved in a single frame, never spanning two frames.

Field/offset provenance (from IDA's recovered `*_Fields` UDTs, object offset
= Fields offset + `0x10`):

| Expression in disasm | Struct | Fields offset | Object offset |
|---|---|---|---|
| `[rbx+38h]` → `GetComponent<FallingHolder>()` | `PickRay.Player` (GameObject*) | `0x28` | `0x38` |
| `[rax+81h]` | `FallingHolder.isFalling` | `0x71` | `0x81` |
| `[rax+82h]` | `FallingHolder.isLanding` | `0x72` | `0x82` |
| `[rbx+98h]` | `PickRay.WJ` (`WindowJumping_o*`) | `0x88` | `0x98` |
| `[rax+21h]` | `WindowJumping.IsJumping` | `0x11` | `0x21` |
| `[rbx+88h]` | `PickRay.PlayerStatus` (`PlayerStatus_o*`) | `0x78` | `0x88` |
| `[rax+0A8h]` | `PlayerStatus.IsJumpscared` | `0x98` | `0xA8` |
| `[rbx+4D0h]` | `PickRay.buttonClicked` | `0x4C0` | `0x4D0` |
| `[rbx+4D1h]` | `PickRay.buttonClicked2` | `0x4C1` | `0x4D1` |
| `[rbx+4E8h]` | `PickRay.Inventory` (`Inventory_o*`) | `0x4D8` | `0x4E8` |
| `[rbx+538h]` | `PickRay.MainInteract` (KeyCode) | `0x528` | `0x538` |
| `[rbx+53Ch]` | `PickRay.DropKey` (KeyCode) | `0x52C` | `0x53C` |

All exactly match the task's cited semantics (`FallingHolder+129/+130` =
`0x81`/`0x82` decimal = isFalling/isLanding; `WJ.IsJumping`;
`PlayerStatus.IsJumpscared`).

**A much bigger, function-wide gate also exists**: at `0x1802396e8`, if
`Paused.IsPaused` is true, execution jumps to `0x18024294F`, which is the
function's **epilogue register-restore sequence** — i.e. when the game's own
pause flag is set, *the entire raycast + item-dispatch + pickup chain is
skipped for that frame*, function returns immediately. This is the game's
menu-pause flag (`Paused.IsPaused`), distinct from `Time.timeScale`; it
should not normally be set during GrannyTAS frame-stepping/capture-delta
operation, but if any code path sets it while a macro is being recorded or
replayed, pickups (and the entire PickRay logic) go dark for those frames.

## 3. The actual generic pickup block (`Inventory.PickupItem`)

Located at `0x18023f400`–`0x18023f950`. This block does **not** perform its
own `Physics.Raycast` — it reuses the `RaycastHit` struct (`var_9F0`)
populated by the single raycast at `0x18023a094` (see §4), which is already
gated by the five checks in §2. Sequence once that raycast has hit
something with an `ItemSeedData` component:

```
0x18023f436  RaycastHit.get_collider().GetComponent<ItemSeedData>()
0x18023f482  UnityEngine.Object op_Implicit (exists != null) -> if false, try next name (0x18023F637)
0x18023f4d8  itemSeedData.name == "<literal 2057>" -> if not, try next candidate (0x18023F637)
0x18023f4ea  PickRay.TurnOffItemTexts()
...          shows the item-name prompt text, SetActive on ring UI
0x18023f564  cmp [rbx+4D0h],0 ; jz loc_180242595   <- buttonClicked check for THIS pickup
0x18023f573  buttonClicked = false                 <- consumed here if clicked
0x18023f57d  call PickRay.CheckItemDropping()
0x18023f5be  Object.Destroy(hit collider's GameObject)   <- (this branch is a "poison trap" pickup — destroys world object)
...
```

A second, more general `ItemSeedData` name-driven path
(`0x18023f637`–`0x18023f950`) is the one that actually calls
`Inventory.PickupItem`:

```
0x18023f68a  Object.op_Implicit(itemSeedData) -> if null, fall to 0x18023F9B3 (next block)
0x18023f6d9  itemSeedData.name != "<literal 4978>" -> if equal, fall to 0x18023F9B3
0x18023f721  a SECOND ItemSeedData reference's .name != "<literal 5970>" -> else skip generic path (0x18023F9B3)
0x18023f785  ... != "<literal 2057>" -> else skip generic path
0x18023f797  PickRay.TurnOffItemTexts()
0x18023f7f1  Inventory.GetItemDefByName(inventory=[rbx+4E8h], name = hitItemSeedData.name)
0x18023f80e/0x18023f86f  SetActive(true) on prompt UI, TMP_Text set to item name
0x18023f874  cmp [rbx+4D0h],0 ; jz loc_180242595       <- buttonClicked check
0x18023f883  buttonClicked = false                     <- consumed
0x18023f88d  call PickRay.CheckItemDropping()
0x18023f8e7  call Inventory.PickupItem(inventory, itemName)   <-- THE call
0x18023f935  itemSeedData.name == "<literal 2330>" -> call PickRay.PickShotgun() (extra logic chained after generic pickup for the shotgun item specifically)
```

So the generic pickup fires only if:
1. The shared raycast at `0x18023a094` hit something (gated by §2's four
   flags), **and**
2. The hit collider carries an `ItemSeedData` component whose `name` does
   not match any of the earlier, more specific item blocks in the chain
   (poison trap, chargebulb "b"-tagged object, etc.) — first-match-wins,
   in fixed program order, so this is deterministic given the same raycast
   hit,
3. `buttonClicked` is still true when this block is reached.

String literals for the various `ItemSeedData.name` comparisons
(`StringLiteral_2057/4978/5970/2330/6408/…`) are IL2CPP lazily-resolved
metadata-usage tokens (`0xA000_00xx` pattern in the pointer slot) — their
text is **not statically resolvable from the raw binary image**; IDA's own
disasm comments show a resolved preview for some (`"Poison trap"`,
`"There's something inside this watermelon"`) but truncate others to a
single character (`"b"`, `"g"`, `"s"`). This is flagged as **unverified** —
the exact item names gated by each block could not be confirmed statically
and would need a runtime dump (e.g. via the mod itself, or a metadata
dumper) to pin down precisely.

## 4. Only two `Physics.Raycast` (overload `_6450236928`) calls in the whole function

- `0x180239d64` — the very first, "what am I looking at for special
  objects" raycast (distance field `PickRay+0x4F0`). Feeds the
  chargebulb/"b"-string special case and positions a hand transform.
- `0x18023a094` — **the raycast that feeds the entire generic item-name
  dispatch chain**, including the sole `Inventory.PickupItem` call.
  Distance = `PickRay` field object-offset `0x508` (struct name
  `ParticleEffectElectricSpawn` per the recovered layout — likely a
  decompiler mis-typed union/reused slot rather than the true "pickup
  raycast distance" field; flagged **uncertain**, the actual designer-facing
  field is probably `RaycastCheckItemDis` at Fields `+0x34`→object `+0x44`,
  used by a *different* raycast overload — not confirmed which raycast call
  consumes it).
  Origin: `this.transform.position` (`PickRay`'s own transform — the
  raycasting object, i.e. wherever the `PickRay` component's GameObject
  sits, typically the camera or a dedicated ray-origin socket parented to
  the camera — **not** independently confirmed which transform that is;
  needs a runtime/hierarchy check to be certain whether it is the camera
  pivot the mod restores via `WorldSnapshot`, or a separate hand/ray object).
  Direction: `this.transform.TransformDirection(Vector3.forward)` — i.e.
  the ray always points along the local +Z of that same transform, so if
  that transform lags or lerps toward the camera rotation independently of
  the camera pivot the mod restores, the ray can point somewhere different
  than expected for a frame or more. **This is the single most important
  unresolved item for the task's "what does the ray track" question** — it
  is not `MobileFPS.playerCamera`/`playerCamera2` directly by name in this
  function; `PickRay` has its own `Player` field (a `GameObject*`, object
  `+0x38`) used for the `FallingHolder` lookups, and the ray itself uses
  `PickRay`'s own `transform`, i.e. whatever GameObject the `PickRay`
  component itself is attached to. Whether that GameObject's rotation is
  driven every frame directly from the camera pivot (no lag) or through a
  smoothing/lerp step was **not verified** in this pass — recommend
  checking `PickRay`'s own `Awake`/`Start`/`LateUpdate` (if any) or whether
  it's simply parented under the camera transform in the scene hierarchy
  (a static/parenting fact, not code) — flagged **UNVERIFIED**.
- No dead-zone/layer-mask detail beyond: `deadZone` (`flt_...` global) is
  used for the second Raycast overload (`_6450235632`) at `0x180239e34`,
  which is part of the "b"-tagged chargebulb hand-positioning logic, not the
  generic-pickup raycast.

## 5. Time / cooldown / animation gates found

- **Audio-based gate on the FIRST ("b"-string / chargebulb-adjacent) item
  block only**: `0x18023a1ec` — `AudioSource.isPlaying` on `this`
  (`PickRay`'s own `AudioSource`) is checked; if true, that specific block's
  logic (and its consumption of `buttonClicked`) is skipped for the frame
  (jumps to `0x18023A38D`). This is **not** a generic gate on
  `Inventory.PickupItem` — the generic pickup block traced in §3 has no
  `AudioSource.isPlaying`/`Animation.isPlaying` check of its own.
- **`PlayerPrefs.GetFloat`/`SetFloat`/`Save`** appear at `0x1802425ca`–
  `0x1802426ae`, gated on `[rbx+4F4h]` and comparing against a stored float
  keyed by `StringLiteral_811` ("key" — resolved). This is the
  "watermelon hint text shown once" counter — increments
  (`addss xmm0, xmm6`) a persistent counter rather than checking a
  wall-clock cooldown, and it sits in the shared reject/cleanup region
  (`0x1802425a7` onward), reached regardless of which item was targeted.
  It is **unrelated to `Inventory.PickupItem`** and has no `Time.deltaTime`
  dependency — it is a monotonically-incremented save-file counter, so
  replaying it twice would double-count, but it doesn't gate or skip a
  pickup.
- **No `Time.deltaTime`, `Time.time`, `Time.timeScale`, coroutine, or
  `WaitForSeconds` reference was found anywhere in `PickRay.Update`,
  `Inventory.PickupItem`, `Inventory.GetItemDefByName`, or
  `Inventory.DropLogic`/`PickRay.CheckItemDropping`.** The only "timers" in
  the whole traced path are the `PlayerPrefs` counter above (not a cooldown)
  and the `FallingHolder.fallDuration`-style fields, which live in
  `FallingHolder`, not in the pickup path itself — `PickRay.Update` only
  *reads* `FallingHolder.isFalling`/`isLanding` booleans, it does not
  compute them.

## 6. `Inventory.PickupItem` / `Inventory.GetItemDefByName` — no time/RNG, no visible early-out for "already picked"

Full decompiles archived under `[[IDA:Inventory_PickupItem_0x1802459b0]]`
and `[[IDA:Inventory_GetItemDefByName_0x1802457e0]]` in
`for_claude_the_logic_pro.txt`.

- `GetItemDefByName` (`0x1802457e0`) is a plain linear scan over
  `Inventory.ItemDefs` (a `List<ItemDefs>`) comparing `itemName` by
  `String.op_Equality`; returns `null` if not found. No time/RNG.
- `PickupItem` (`0x1802459b0`):
  1. `List<ItemDefs>.Find(predicate matching itemName)` → if not found
     (`v17 == 0`), the function **silently returns** (`goto LABEL_22` /
     falls through to a null-deref-guard stub) — this is the one visible
     "early-out", triggered only if the name passed in doesn't exist in
     `ItemDefs` at all (a data/content bug, not a state check).
  2. `Inventory.DropLogic(this)` — called **unconditionally before** the
     found-item null check, i.e. every `PickupItem` call also re-runs the
     "drop any currently-visible dropped-item slot" sweep (decompiled at
     `0x1802454d0`; iterates a `HashSet` inside each `ItemDefs` entry,
     `Instantiate`s a drop prefab for any active slot). No time/RNG in
     `DropLogic` either.
  3. If found: `SetActive(true)` on the item's UI icon GameObject
     (`*(v13+24)`), then `AudioSource.PlayOneShot` on `this.Audio` with the
     item's clip (`*(v13+56)`).
  4. **No inventory-full check, no duplicate-item check, no "already
     picked" flag was found anywhere in this function.** Picking up the
     same item name twice would simply re-run steps 2–3 (re-enable icon,
     replay sound) with no state guard — i.e. nothing here would silently
     no-op a second, spurious pickup call; if TAS playback double-fires
     `PickupItem` for the same item, it would double-add rather than be
     rejected. (Whether double-adding shows up as a game-visible bug wasn't
     traced further — `ItemDefs` semantics for "already have it" weren't
     inspected beyond this function.)

## 7. Anything outside `PickRay.Update` needed first in the same frame?

Not found in this pass. The crosshair "ring" activation
(`PickRay.Ring.activeSelf`, gate #1 in §1) is read, not written, by
`PickRay.Update` — something else (not traced) must set `Ring.activeSelf`
true, but `PickRay.Update` itself reads that flag at the very top of its own
per-frame block (`0x180239558`–`0x180239574`), immediately after processing
several *other* item-specific "SetActive" calls earlier in the same
function body — i.e. as far as could be traced, this is all self-contained
within one `MonoBehaviour.Update()`; no evidence of a required
same-frame-earlier `Update()` from another component was found. This is
**not conclusively proven** absent tracing what actually sets
`Ring.activeSelf` (not attempted — would require finding all writers of
`PickRay.Ring`'s referenced GameObject's active state, which is scene/other
script logic outside the traced function).

## Ranked list — most to least likely to differ between record (paused /
frame-stepped, dt pinned) and playback (1x continuous)

1. **`Paused.IsPaused` state** (§2) — if GrannyTAS's frame-stepper or pause
   UI ever sets the game's own `Paused` flag (as opposed to `Time.timeScale`
   / `captureDeltaTime`), `PickRay.Update` skips entirely for that frame.
   Any asymmetry in when this flag is toggled between record and playback
   would desync pickups outright. **Highest-priority to verify** against
   `TimeController`/`CursorController`/panel code.
2. **Ray-origin transform lag** (§4) — if the `PickRay` GameObject's own
   transform (ray origin/direction) is driven by a smoothing/lerp step
   rather than snapping instantly to the camera pivot each frame, then
   frame-stepping at variable real-world cadence (even with pinned
   `deltaTime`) could still leave that transform mid-lerp differently than
   a continuous 1x playback would, making the raycast hit a different
   object between record and playback. **Unverified — needs a follow-up
   check** of what drives `PickRay`'s own transform.
3. **`FallingHolder.isFalling`/`isLanding`, `WindowJumping.IsJumping` timing**
   — these are booleans computed elsewhere (not in `PickRay.Update`) based
   on presumably continuous state (grounded checks, animation state) that
   could plausibly differ by a frame or two between a frame-stepped capture
   and continuous playback if their own update methods integrate real time
   or physics substeps differently — not directly traced in this pass, but
   they are real per-frame gates on every pickup attempt (§2), so any
   record/playback divergence in *their* timing directly blocks/unblocks
   pickups.
4. **First-match-wins ordering across the ~50-item dispatch chain** — fully
   deterministic given the same `RaycastHit` and the same `ItemSeedData`
   names, so low risk by itself, *provided* the raycast result is
   identical (see #2).
5. **`buttonClicked` persistence** — confirmed same-frame set/consume/clear
   (§1–§2), so this by itself is not a hazard; only matters if the
   `Input.GetKeyDown` edge itself is delivered on a different simulated
   frame between record and playback (a `VirtualInput`/macro-format
   concern, not something found to be wrong inside `PickRay.Update`).
6. **PlayerPrefs watermelon-hint counter** (§5) — low risk; it is a
   monotonic save-file counter unrelated to the generic pickup gate, would
   at most double-increment a hint counter, not block/duplicate an item
   pickup.
7. **`Inventory.PickupItem`'s lack of a duplicate-pickup guard** (§6) — if
   playback ever re-delivers the same `GetKeyDown` edge (e.g. due to an
   off-by-one frame duplication in macro playback), the game itself would
   not reject it — it would silently double up the item. This is a hazard
   *in the mod's macro-replay fidelity*, not in the native code, but worth
   flagging since native code offers no safety net here.

## Files

- Findings written to
  `C:\Users\ir0n1c\Documents\grannytasmelonlodaer\docs\ida-pickup-findings.md`
  (this file).
- Raw decompiles/disasm dumps appended to
  `C:\Users\ir0n1c\Documents\grannytasmelonlodaer\for_claude_the_logic_pro.txt`
  under keywords:
  - `[[IDA:PickRay_Update_0x180237ce0]]`
  - `[[IDA:Inventory_PickupItem_0x1802459b0]]`
  - `[[IDA:Inventory_GetItemDefByName_0x1802457e0]]`
  - `[[IDA:Inventory_DropLogic_0x1802454d0]]`
  - `[[IDA:PickRay_CheckItemDropping_0x180233070]]`
- **IDB was not modified** — no renames, comments, or type changes were
  written, per task instruction.
