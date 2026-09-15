# Input, camera, and paused free-look findings

## Purpose

These findings support accurate macro capture and a frame stepper where the
player can freely inspect the view while simulation is paused, then commit that
view on the next simulated frame.

## Verified facts

### Native camera path

`MobileFPS::GetTouchInput` is at `0x18022d030` in Granny Legacy 1.8.9. It:

1. reads two legacy `Input.GetAxis` values;
2. adds the horizontal value times `cameraRotationSpeed` to body yaw;
3. subtracts the vertical value times `cameraRotationSpeed` from `rotationX`;
4. clamps `rotationX` to `[minXRotation, maxXRotation]`;
5. writes `playerCamera.localRotation = Quaternion.Euler(rotationX, 0, 0)`;
6. post-multiplies `MobileFPS.transform.rotation` by the yaw quaternion.

It makes no `Time` call and has no movement, physics, or interaction side
effect. Evidence: `docs/ida-raw-evidence.txt`,
`[[IDA:MobileFPS_GetTouchInput_0x18022d030]]`.

`MobileFPS::Update` is at `0x18022d9c0`. Its ordinary movement path reads
Horizontal/Vertical, derives movement in the player's current transform space,
and calls `GetTouchInput` only when `CamK` is set and movement input is nonzero.
Therefore a pre-Update body orientation affects movement direction in that same
Update. Evidence: `docs/ida-raw-evidence.txt`,
`[[IDA:MobileFPS_Update_0x18022d9c0]]`.

### Existing virtual-input defect

`VirtualInput.Sample` samples every configured axis exclusively through
`Input.GetAxisRaw`. Both `Patch_GetAxis` and `Patch_GetAxisRaw` return that same
stored value. This changes a game caller of `Input.GetAxis` into a raw caller.
The native look method is a `GetAxis` caller, so the recorded stream does not
faithfully represent its live source. Evidence: `GrannyTAS/src/VirtualInput.cs`
and the native camera path above.

## Recommended design

### Input frame representation

Store two independent axis maps per `InputFrame`:

- a smoothed `GetAxis` map;
- a `GetAxisRaw` map.

Sample both while `VirtualInput.Bypass` is true and return the corresponding
map from each Harmony patch. Update the macro format version rather than
silently interpreting the old single-axis stream as both. The exact legacy
axis names consumed outside the examined player path remain an open caller
audit item.

### Paused free-look

On each zero-delta paused real frame, serve only the sampled smoothed mouse
axes under a narrow, scoped preview override and call
`MobileFPS.GetTouchInput()` directly. `GameplayFreeze` prevents the normal
`MobileFPS.Update` call; the native method itself has been verified to be
look-only. Do not add these preview mouse deltas to `Pending`: the preview has
already applied them.

At the next simulated frame, record a **pre-Update view pose** consisting of:

- `MobileFPS.transform.rotation` (body quaternion);
- `MobileFPS.playerCamera.localRotation` (pitch-pivot quaternion);
- `MobileFPS.rotationX` (the authoritative clamped scalar).

Apply this pose in a Harmony prefix for `MobileFPS.Update`, immediately before
the game reads movement and invokes native look. This prefix is the safe game
boundary; MelonLoader `OnUpdate` is not proven to precede every relevant game
Update. Let freshly sampled mouse input for the simulated frame pass through
natively once. It was not part of the prior paused preview, so suppressing it
would lose a real frame input. On playback, restore the recorded pre-Update
pose at the same prefix and replay that frame's ordinary input streams.

This eliminates the prior buffered-aim design's clamp error: summing deltas
before a single clamp is not equivalent to applying native look repeatedly
while paused.

## Confidence and caveats

- **VERIFIED:** native look arithmetic, clamp, target transforms, no delta-time
  scaling, and Update's movement/look gate.
- **HIGH CONFIDENCE:** the prefix placement applies a committed yaw before same
  frame movement because native movement uses the body transform space.
- **HIGH CONFIDENCE:** pending preview deltas would double-apply if also
  delivered as the next frame's input.
- **MEDIUM CONFIDENCE:** Harmony's managed `Input.GetAxis` patch covers the
  IL2CPP `InputUnsafeUtility.GetAxis` path in-game; the existing project design
  relies on this already and needs runtime confirmation after the split.
- **UNKNOWN:** other game callers of smoothed/raw axes, named button/string
  overloads, joystick input, and the exact IL2CPP string literals for the two
  camera axes.
