# paused look and macro accuracy

version 0.3.3 replaces the buffered-aim behavior described in the earlier
[0.3.1 review](code-review.md).

while tas-paused, mouse movement turns the view immediately. movement keys and
actions still use the input buffer. looking around does not advance a simulated
frame; the next step or resume commits the view you selected. clearing the input
buffer does not undo that view. the panel's mouse suppression setting still
controls whether moving the pointer over the panel affects the camera.

the look preview calls the game's look routine without running its movement or
interaction update. native pitch clamping therefore happens on each mouse sample,
including when you reach a pitch limit and then reverse direction. replay stores
the resulting body rotation, camera pivot rotation and internal pitch at the
start of each simulated frame. that pose is restored before the frame's gameplay
input is applied; mouse movement already previewed while paused is not applied
again.

new recordings also retain separate smoothed and raw axis samples. previously,
both input getters received the raw sample, changing the game's smoothed movement
and mouse input. the new macro format preserves both streams with round-trip float
precision and continues to read legacy v1 recordings.

## runtime acceptance

1. pause with the panel closed. look up, down and sideways without stepping.
   verify the view responds while the player and frame counter stay still.
2. look past the pitch limit, then reverse slightly. step once and verify the
   view stays where you chose, with no extra rotation. repeat with several steps,
   a ten-frame step request, and resume.
3. latch movement or an interaction and aim before stepping. verify the action
   uses that view on the next frame. clear the buffer and verify aiming remains.
4. record those steps, save, load and replay. verify the same view directions,
   including the clamp-and-reverse case. mouse motion during playback must not
   replace the recorded aim.
5. enable panel mouse suppression and move/click in the panel. verify the camera
   remains still. repeat with suppression disabled.

unit regressions use engine boundary stubs. they cannot establish native harmony
hook ordering or unity physics behavior. full world restoration, fixed-update
phase, gameplay changes to camera settings and other native state remain limits
on deterministic replay.

validation commands:

```powershell
dotnet run --project tests/GrannyTAS.Regression --no-restore
dotnet build GrannyTAS/GrannyTAS.csproj -c Release -p:NoDeploy=true --no-restore
```

the release build produces `GrannyTAS/bin/Release/GrannyTAS.dll`. the commands
above do not install it into the game.
