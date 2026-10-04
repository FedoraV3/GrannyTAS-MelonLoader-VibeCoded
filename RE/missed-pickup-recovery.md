# Missed generic pickup recovery

> **Superseded 2026-09-30, reinstated in part 2026-10-04.** The adapter
> described in the "recovery contract" below (`GamePickupRecovery`) put the
> item in the hand a frame late and was removed. Replay now first makes the
> game's own `PickRay.Update` take the item (steer it under the ray, open the
> gates, hand over the click). If that call still misses it, the same native
> sequence is run **in that call's postfix, on the recorded frame**
> (`InteractionPin.ForcePending`). The world copy is chosen nearest the
> recorded ray instead of requiring a unique name. See docs/replay-sync.md,
> "Missed item pickups". The native-path findings in this file are still
> accurate. `Inventory.PickupItem` and `PickRay.PickShotgun` each have exactly
> one caller (interop `CallerCount = 1`, `PickRay.Update`).

## purpose

Document the native generic-item pickup path in Granny Legacy 1.8.9 for macro
replay recovery. The desired result is to put a recorded-but-missed item into
the player's hand and remove the corresponding world object, without depending
on the original raycast, prompt, or input edge.

The live IDA database is `GameAssembly.dll.i64` (MD5
`fd24a4d70f11a67b7a89c2bed4840155`). Addresses below use its current imagebase
of zero.

## relevant symbols

| Symbol | Address | Finding |
|---|---:|---|
| `PickRay$$Update` | `0x239570` | Normal generic-pickup dispatcher. |
| `Inventory$$PickupItem` | `0x247240` | Activates the selected generic hand object. |
| `Inventory$$DropLogic` | `0x246D60` | Hides active generic hand objects and spawns their world representations. |
| `Inventory$$GetItem` | `0x247160` | Exact-name `ItemDefs` lookup. |
| `Inventory$$GetItemDefByName` | `0x247070` | Equivalent exact-name linear lookup. |
| `PickRay$$CheckItemDropping` | `0x234900` | Handles special held objects before generic pickup. |

## verified normal generic lifecycle

1. `PickRay$$Update` gets `ItemSeedData` from the raycast collider.
2. At `0x24111D`, it calls `PickRay$$CheckItemDropping(PickRay)`. This has
   no explicit managed arguments beyond `this` and runs before every generic
   pickup.
3. At `0x241177`, it calls `Inventory$$PickupItem(PickRay.Inventory,
   itemSeedData.itemName)`.
4. `Inventory$$PickupItem` finds the exact name in `Inventory.ItemDefs`, calls
   `Inventory$$DropLogic`, sets `ItemDefs.handObject` active, then plays the
   item's pick audio.
5. The update path obtains the hit collider's GameObject and calls
   `UnityEngine.Object.Destroy(GameObject)` at `0x241214`.

The source destruction is outside `Inventory$$PickupItem`. A recovery that
calls only `PickupItem` leaves the missed world item behind and permits a later
duplicate pickup.

Destroying the source GameObject also runs `ItemSeedData$$OnDestroy`
(`0x248A30`). That method finds `WeightController` and calls
`WeightController.RemoveItem(this ItemSeedData)`, so direct destruction retains
the game's weight-controller cleanup.

Confidence: **VERIFIED** by decompilation and surrounding disassembly.

## recovered structures

### `ItemDefs_o`

| Object offset | Field | Type | Confidence |
|---:|---|---|---|
| `0x10` | `itemName` | `System.String*` | **VERIFIED** |
| `0x18` | `handObject` | `UnityEngine.GameObject*` | **VERIFIED** |
| `0x20` | `dropPoint` | `UnityEngine.Transform*` | **VERIFIED** |
| `0x28` | `textHighlighted` | `System.String*` | **VERIFIED** |
| `0x30` | `itemInt` | `int32` | **VERIFIED** |
| `0x38` | `pickAudioClip` | `UnityEngine.AudioClip*` | **VERIFIED** |

`PickupItem` calls `SetActive(true)` on `handObject`, so it really does put a
generic inventory item in the player's hand rather than merely enabling a UI
indicator.

### `ItemSeedData_o`

| Object offset | Field | Type | Confidence |
|---:|---|---|---|
| `0x20` | `itemName` | `System.String*` | **VERIFIED** |
| `0x28` | `category` | `int32` | **VERIFIED** |
| `0x30` | `containedItems` | `List<string>*` | **VERIFIED** |

## recovery contract

For a recorded **generic** item, the ideal source identity is the specific
world object hit while recording. Current v5 traces retain only the item name,
so the implementation accepts a source only when exactly one active
`ItemSeedData` object has that name. On a replay mismatch:

1. Locate a unique active source with `ItemSeedData` and the expected exact
   name, if one remains. Abort source cleanup when multiple sources match.
2. Look up the matching `ItemDefs`; abort recovery if it does not exist.
3. If its `handObject` is already active, consider the item recovered and do
   not call `PickupItem` again.
4. Resolve an initialized `PickRay` and invoke `CheckItemDropping`.
5. Invoke `Inventory.PickupItem` with the canonical `ItemDefs.itemName`.
6. Destroy the unique source GameObject after that call succeeds, if found.

The adapter still equips the hand when no active source remains. This is a
recovery choice based on the user's recorded pickup, not proof that the world
item was present on replay. Unity defers `Destroy` to the end of the frame, so
the adapter remembers consumed sources during that frame to avoid selecting one
twice.

This bypasses all normal `PickRay.Update` input/raycast gates, while matching
the two game-owned state changes needed for a generic pickup.

`PickupItem` always calls `DropLogic` after the list search and before checking
whether the result is null. Therefore the adapter must validate the item name
first. It also performs the expected replacement behavior: active generic
`handObject` entries are disabled and their associated item prefabs are spawned
at the corresponding `dropPoint`.

`CheckItemDropping` is required before the generic call because it owns the
replacement behavior for special held objects. It checks `H_CR`, `H_DSH`,
`H_FT`, and `H_PTRAP`; when one is active it disables that hand object, spawns
the corresponding world object at its drop transform, and updates the related
special state. With no special hand active it clears the drop indicator and
calls `Inventory.DropLogic`. If a live initialized `PickRay` is unavailable,
the recovery must abort rather than call `PickupItem` directly and leave a
special held object active alongside the recovered generic item.

Confidence: **VERIFIED** for ordering and side effects.

## special-item boundary

`PickRay$$CheckItemDropping` explicitly handles special held objects such as
the shotgun, crossbow, freeze trap, and poison trap. The generic path also has
a post-pickup `"b"` **`ItemSeedData.itemName`** branch to
`PickRay$$PickShotgun`. `PickShotgun` enables `MoreAmmo1`, plays a shotgun
load/prepare audio path selected by `LoadedGun`, and plays an animation beneath
`H_SG`. A complete recovery for this exact item name must call it after
`PickupItem`; otherwise treat the item as unsupported rather than leaving the
shotgun in a partial state.

Before invoking `CheckItemDropping`, require non-null `H_CR`, `H_DSH`, `H_FT`,
`H_PTRAP`, `Drop1`, and `Inventory`: the method reaches all of those fields
even if no special object is active. An active special branch additionally
requires its matching drop fields: `H_CR` uses `DropP` and `ItemDrop`; `H_DSH`
also uses `ShotgunHandMain` and `ShotgunHandPipes`; `H_FT` and `H_PTRAP` use
`DropPFreezeTrap` and their matching world prefab. Missing any required object
is a reason to skip recovery.

Confidence: **VERIFIED** for the `"b"` comparison, method ordering, and
referenced fields; **UNKNOWN** whether every recorded item name can be
classified from current macro data without retaining the source component/type.
