using System.Globalization;
using GrannyTAS;
using UnityEngine;
using MelonLoader;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("pass: " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (FormatException) { Check(true, name); return; }
    throw new Exception("accepted invalid data: " + name);
}
bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
bool SameQuaternion(Quaternion a, Quaternion b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z) && Same(a.w, b.w);
void SetGatePlayer(Il2Cpp.MobileFPS player)
{
    typeof(PlayerGate).GetField("_player", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        .SetValue(null, player);
}

// The full readiness gate starts and stops the TAS, while an already-running
// macro may bridge only the four control signals used by scripted transitions.
var gatePlayer = new Il2Cpp.MobileFPS();
PlayerGate.Invalidate();
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "missing player remains a hard gate failure");
SetGatePlayer(gatePlayer);
for (var i = 0; i < 12; i++) PlayerGate.IsReady.ToString();
Check(PlayerGate.IsReady, "player gate settles before initial engagement");
gatePlayer.isAllowedToMove = false;
Check(!PlayerGate.IsReady && PlayerGate.CanBridgeTransientControlLoss,
    "active macro may bridge isAllowedToMove hand-off");
gatePlayer.isAllowedToMove = true;
Check(!PlayerGate.IsReady && PlayerGate.CanBridgeTransientControlLoss,
    "active macro may bridge readiness settling after a hand-off");
gatePlayer.AbleToMove = false;
Check(!PlayerGate.IsReady && PlayerGate.CanBridgeTransientControlLoss,
    "active macro may bridge AbleToMove hand-off");
gatePlayer.AbleToMove = true;
gatePlayer.CamK = false;
Check(!PlayerGate.IsReady && PlayerGate.CanBridgeTransientControlLoss,
    "active macro may bridge CamK hand-off");
gatePlayer.CamK = true;
gatePlayer.playerCamera2.isActiveAndEnabled = false;
Check(!PlayerGate.IsReady && PlayerGate.CanBridgeTransientControlLoss,
    "active macro may bridge player-camera hand-off");
gatePlayer.playerCamera2.isActiveAndEnabled = true;
gatePlayer.PS.IsJumpscared = true;
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "jumpscare remains a hard gate failure");
gatePlayer.PS.IsJumpscared = false;
gatePlayer.gameObject.activeInHierarchy = false;
Check(!PlayerGate.IsReady && PlayerGate.CanBridgeTransientControlLoss,
    "active macro may bridge an inactive but still-existing cached player");
PlayerGate.Invalidate();
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "scene invalidation distinguishes a missing player from an inactive cached player");
SetGatePlayer(gatePlayer);
gatePlayer.gameObject.activeInHierarchy = true;
gatePlayer.enabled = false;
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "disabled player component remains a hard gate failure");
gatePlayer.enabled = true;
Il2Cpp.Paused.IsPaused = true;
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "game pause remains outside transient control bridging");
Il2Cpp.Paused.IsPaused = false;
gatePlayer.SystemDay = new Il2Cpp.Days { PlayerBedAnim = new Animator() };
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "day-start bed animation remains a hard gate failure");
gatePlayer.SystemDay = null;
PlayerGate.Invalidate();

var empty = new InputFrame();
var pressed = new InputFrame();
pressed.Keys.Add(KeyCode.E);
pressed.MouseButtons[0] = true;
VirtualInput.ResetForPlayback(empty);
VirtualInput.AdvanceFrom(pressed);
Check(VirtualInput.Down(KeyCode.E) && VirtualInput.MouseDown(0), "interaction edge on simulated frame");
VirtualInput.Freeze();
Check(!VirtualInput.Down(KeyCode.E) && !VirtualInput.MouseDown(0), "paused frames cannot repeat pickup/click edges");
Il2Cpp.Paused.IsPaused = true;
Check(VirtualInput.PassMouseToGame, "game pause menu receives live mouse input while TAS input is frozen");
VirtualInput.SuppressMouse = true;
Check(!VirtualInput.PassMouseToGame, "TAS panel mouse suppression retains ownership over the pause menu");
VirtualInput.SuppressMouse = false;
Il2Cpp.Paused.IsPaused = false;
Check(pressed.Has(KeyCode.Mouse0), "mouse keycode and mouse button views agree");
VirtualInput.AdvanceFrom(pressed);
Check(!VirtualInput.Down(KeyCode.E), "held interaction does not become a second press after pause");
VirtualInput.AdvanceFrom(empty);
Check(VirtualInput.Up(KeyCode.E), "release edge delivered on next simulated frame");
VirtualInput.Freeze();
Check(!VirtualInput.Up(KeyCode.E), "release edge suppressed while frozen");
VirtualInput.ResetForPlayback(pressed);
VirtualInput.AdvanceFrom(pressed);
Check(!VirtualInput.Down(KeyCode.E), "recorded initial held state prevents false first-frame pickup");

VirtualInput.Active = true;
Time.deltaTime = 0;
Check(!GameplayFreeze.Prefix(), "movement/fall/pickup updates skipped at zero delta");
Time.deltaTime = 1f / 60f;
Check(GameplayFreeze.Prefix(), "gameplay runs on stepped frame");
VirtualInput.Active = false;
Time.deltaTime = 0;
Check(GameplayFreeze.Prefix(), "freeze patches inert when mod disengaged");

Input.Axes["Mouse X"] = 2.5f;
Input.Axes["Mouse Y"] = -1.25f;
Input.Axes["Mouse ScrollWheel"] = .1f;
Input.RawAxes["Mouse X"] = 3.5f;
Input.RawAxes["Mouse Y"] = -2.25f;
Input.RawAxes["Mouse ScrollWheel"] = .2f;
VirtualInput.ClearBuffer();
VirtualInput.PollHardware();
VirtualInput.AccumulateBuffer();
VirtualInput.AdvanceFromBuffer();
Check(VirtualInput.Current.Axis("Mouse X") == 2.5f && VirtualInput.ForRecord.Axis("Mouse X") == 2.5f,
    "fresh stepped aim reaches game and recorder once");
Check(VirtualInput.Current.Axis("Mouse ScrollWheel") == .1f && VirtualInput.Current.AxisRaw("Mouse ScrollWheel") == .2f,
    "buffered scroll preserves separate smoothed and raw streams");
Input.Axes.Clear();
Input.RawAxes.Clear();
VirtualInput.PollHardware();
VirtualInput.AdvanceFromBuffer();
Check(VirtualInput.Current.Axis("Mouse X") == 0 && VirtualInput.Current.AxisRaw("Mouse X") == 0 &&
      VirtualInput.Current.Axis("Mouse ScrollWheel") == 0 && VirtualInput.Current.AxisRaw("Mouse ScrollWheel") == 0,
    "consecutive steps cannot repeat previous look or scroll");
Input.Axes["Mouse X"] = 3f;
Input.RawAxes["Mouse X"] = 4f;
VirtualInput.PollHardware();
VirtualInput.AdvanceFromHardware();
Check(VirtualInput.Current.Axis("Mouse X") == 3f && VirtualInput.Current.AxisRaw("Mouse X") == 4f,
    "resume uses only the new physical look delta");
Input.Keys.Add(KeyCode.E);
VirtualInput.PollHardware();
VirtualInput.SeedBufferFromHardware();
VirtualInput.AccumulateBuffer();
Check(VirtualInput.Pending.Has(KeyCode.E), "pause seed cannot toggle off a simultaneous interaction press");
Input.Keys.Clear();
Input.Axes.Clear();
Input.RawAxes.Clear();

var copied = new InputFrame();
copied.Keys.Add(KeyCode.W);
copied.MouseButtons[2] = true;
copied.Axes["Mouse X"] = .125f;
copied.RawAxes["Mouse X"] = .875f;
copied.HasPose = true;
copied.PlayerRotation = new Quaternion(.1f, .2f, .3f, .4f);
copied.CameraLocalRotation = new Quaternion(.5f, .6f, .7f, .8f);
copied.RotationX = -12.5f;
copied.HasPhysicsState = true;
copied.PhysicsStates.Add(new PhysicsFrameState { Identity = "0/1#0", Position = new Vector3(1, 2, 3) });
var copiedInto = new InputFrame();
copiedInto.CopyFrom(copied);
Check(copiedInto.Has(KeyCode.W) && copiedInto.Mouse(2) && Same(copiedInto.Axis("Mouse X"), .125f) &&
      Same(copiedInto.AxisRaw("Mouse X"), .875f) && SameQuaternion(copiedInto.PlayerRotation, copied.PlayerRotation) &&
      SameQuaternion(copiedInto.CameraLocalRotation, copied.CameraLocalRotation) && Same(copiedInto.RotationX, copied.RotationX),
    "input copy preserves raw axes and optional pre-frame pose");
Check(copiedInto.HasPhysicsState && copiedInto.PhysicsStates.Count == 1 &&
      !ReferenceEquals(copiedInto.PhysicsStates[0], copied.PhysicsStates[0]) &&
      Same(copiedInto.PhysicsStates[0].Position.y, 2f),
    "input copy owns an independent rigidbody checkpoint");
copiedInto.Clear();
Check(!copiedInto.HasPose && copiedInto.RawAxes.Count == 0 && Same(copiedInto.RotationX, 0) &&
      SameQuaternion(copiedInto.PlayerRotation, Quaternion.identity) && SameQuaternion(copiedInto.CameraLocalRotation, Quaternion.identity) &&
      !copiedInto.HasPhysicsState && copiedInto.PhysicsStates.Count == 0,
    "input clear removes pose and raw axes");

var rigidParent = new GameObject();
var rigidChildA = new GameObject();
var rigidChildB = new GameObject();
rigidChildA.transform.parent = rigidParent.transform;
rigidChildB.transform.parent = rigidParent.transform;
rigidChildA.transform.name = rigidChildB.transform.name = "duplicate";
var rigidA = rigidChildA.AddComponent<Rigidbody>();
var rigidB = rigidChildB.AddComponent<Rigidbody>();
Check(PhysicsFrameState.IdentityFor(rigidA) != PhysicsFrameState.IdentityFor(rigidB),
    "sibling-index rigidbody identities distinguish duplicate sibling names");

rigidA.position = new Vector3(.31415927f, -.00000012345679f, float.Epsilon);
rigidA.rotation = new Quaternion(.125f, -.25f, .375f, .875f);
rigidA.velocity = new Vector3(1.25f, -2.5f, 3.75f);
rigidA.angularVelocity = new Vector3(-4.25f, 5.5f, -6.75f);
rigidA.useGravity = false;
rigidA.detectCollisions = true;
rigidA.constraints = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezeRotationZ;
rigidA.Sleep();
var capturedPhysics = new List<PhysicsFrameState>();
PhysicsFrameState.CaptureInto(capturedPhysics);
var capturedA = capturedPhysics.Single(state => state.Identity == PhysicsFrameState.IdentityFor(rigidA));
Check(Same(capturedA.Position.x, rigidA.position.x) && SameQuaternion(capturedA.Rotation, rigidA.rotation) &&
      Same(capturedA.Velocity.z, rigidA.velocity.z) && Same(capturedA.AngularVelocity.x, rigidA.angularVelocity.x) &&
      !capturedA.UseGravity && capturedA.DetectCollisions && capturedA.Sleeping &&
      capturedA.Constraints == (int)rigidA.constraints,
    "rigidbody checkpoint captures pose, motion, flags, constraints, and sleep state");

PhysicsFrameState.ResetDrift();
var recordedPosition = capturedA.Position;
rigidA.position = new Vector3(recordedPosition.x + .5e-6f, recordedPosition.y, recordedPosition.z);
Check(PhysicsFrameState.TryApply(new[] { capturedA }, out _) && PhysicsFrameState.CorrectionCount == 0 &&
      !Same(rigidA.position.x, recordedPosition.x),
    "rigidbody drift at or below one micrometre remains inside the correction threshold");
rigidA.position = new Vector3(recordedPosition.x + 2e-6f, recordedPosition.y, recordedPosition.z);
rigidA.velocity = Vector3.zero;
Check(PhysicsFrameState.TryApply(new[] { capturedA }, out _) && PhysicsFrameState.CorrectionCount == 1 &&
      Same(rigidA.position.x, recordedPosition.x) && Same(rigidA.velocity.x, capturedA.Velocity.x) &&
      PhysicsFrameState.MaxPositionDrift > 1e-6f,
    "dynamic rigidbody state is restored exactly above the one-micrometre threshold");
var kinematic = new PhysicsFrameState();
kinematic.CopyFrom(capturedA);
kinematic.IsKinematic = true;
rigidA.velocity = new Vector3(99, 98, 97);
Check(PhysicsFrameState.TryApply(new[] { kinematic }, out _) && Same(rigidA.velocity.x, 99),
    "kinematic checkpoint application avoids invalid velocity writes");
Check(!PhysicsFrameState.TryApply(new[] { new PhysicsFrameState { Identity = "missing#0" } }, out var missingBody) &&
      missingBody.Contains("missing rigidbody"),
    "missing expected rigidbody is reported as a strict desync");

var previewPlayer = new Il2Cpp.MobileFPS();
SetGatePlayer(previewPlayer);
VirtualInput.Active = true;
Time.deltaTime = 0f;
previewPlayer.rotationX = 0;
previewPlayer.transform.rotation = Quaternion.identity;
Input.Axes["Mouse Y"] = 100f;
Input.RawAxes["Mouse Y"] = 100f;
VirtualInput.PollHardware();
VirtualInput.Freeze();
VirtualInput.PreviewPausedLook();
Check(previewPlayer.TouchInputCalls == 1 && previewPlayer.UpdateCalls == 0 && Same(previewPlayer.rotationX, -45f),
    "frozen zero-movement frame previews look without gameplay update");
Input.Axes["Mouse Y"] = -20f;
Input.RawAxes["Mouse Y"] = -20f;
VirtualInput.PollHardware();
VirtualInput.PreviewPausedLook();
Check(Same(previewPlayer.rotationX, -25f), "clamp then reverse records the actual previewed pitch");

Input.Axes["Mouse X"] = 7f;
Input.Axes["Mouse Y"] = 0f;
Input.RawAxes["Mouse X"] = 11f;
Input.RawAxes["Mouse Y"] = 0f;
VirtualInput.PollHardware();
VirtualInput.AdvanceFromBuffer();
var stepped = new InputFrame();
stepped.CopyFrom(VirtualInput.Current);
Check(stepped.HasPose && Same(stepped.RotationX, -25f) && Same(stepped.Axis("Mouse X"), 7f) &&
      Same(stepped.AxisRaw("Mouse X"), 11f), "step stores pre-gameplay pose and one fresh look delta");
previewPlayer.GetTouchInput();
var recordedBodyAfterOneDelta = previewPlayer.transform.rotation;
Input.Axes.Clear();
Input.RawAxes.Clear();
VirtualInput.PollHardware();
VirtualInput.AdvanceFromBuffer();
previewPlayer.GetTouchInput();
Check(Same(VirtualInput.Current.Axis("Mouse X"), 0) && SameQuaternion(previewPlayer.transform.rotation, recordedBodyAfterOneDelta),
    "next step cannot apply the prior fresh look delta again");
previewPlayer.transform.rotation = Quaternion.Euler(0, 123, 0);
previewPlayer.playerCamera.localRotation = Quaternion.Euler(12, 0, 0);
previewPlayer.rotationX = 12f;
Input.Axes["Mouse X"] = 999f;
Input.RawAxes["Mouse X"] = 999f;
VirtualInput.AdvanceFrom(stepped);
Check(Same(VirtualInput.Current.Axis("Mouse X"), 7f) && Same(VirtualInput.Current.AxisRaw("Mouse X"), 11f) &&
      Same(previewPlayer.rotationX, -25f) && SameQuaternion(previewPlayer.transform.rotation, stepped.PlayerRotation),
    "playback frame issuance ignores hardware and restores captured pre-frame pose");
previewPlayer.rotationX = 17f;
VirtualInput.Freeze();
VirtualInput.ApplyCurrentPose(previewPlayer);
Check(Same(previewPlayer.rotationX, 17f), "frozen player-update prefix cannot restore pose outside a simulated frame");

// Replaying identical input does not reproduce an identical trajectory, and a
// pickup ray cast from a player a millimetre off target hits something else.
// The correction is what keeps interactions landing on the recorded frame, and
// the measurement is what keeps a real desync visible.
var drifted = new InputFrame();
drifted.CopyFrom(stepped);
drifted.PlayerPosition = new Vector3(1f, 2f, 3f);
drifted.HasPosition = true;
VirtualInput.ResetDrift();
VirtualInput.PinPosition = true;
previewPlayer.transform.position = new Vector3(1f, 2f, 3.25f);
previewPlayer.Controller.enabled = true;
VirtualInput.AdvanceFrom(drifted);
Check(Same(previewPlayer.transform.position.z, 3f) && previewPlayer.Controller.enabled &&
      Same(VirtualInput.MaxPositionDrift, 0.25f) && VirtualInput.PositionCorrections == 1,
    "playback puts the player back on the recorded position and reports the drift");

previewPlayer.transform.position = new Vector3(1f, 2f, 3f);
VirtualInput.AdvanceFrom(drifted);
Check(VirtualInput.PositionCorrections == 1,
    "a player already on the recorded position is left alone");

VirtualInput.PinPosition = false;
VirtualInput.ResetDrift();
previewPlayer.transform.position = new Vector3(1f, 2f, 3.5f);
VirtualInput.AdvanceFrom(drifted);
Check(Same(previewPlayer.transform.position.z, 3.5f) && Same(VirtualInput.MaxPositionDrift, 0.5f) &&
      VirtualInput.PositionCorrections == 0,
    "correction off still measures drift without moving the player");

// A macro recorded before positions were stored: look pose, no position.
var poseWithoutPosition = new InputFrame();
poseWithoutPosition.CopyFrom(stepped);
poseWithoutPosition.HasPosition = false;
VirtualInput.PinPosition = true;
VirtualInput.ResetDrift();
previewPlayer.transform.position = new Vector3(9f, 9f, 9f);
VirtualInput.AdvanceFrom(poseWithoutPosition);
Check(Same(previewPlayer.transform.position.x, 9f) && Same(VirtualInput.MaxPositionDrift, 0f),
    "a macro without recorded positions replays untouched");
SetGatePlayer(null);
Input.Axes.Clear();
Input.RawAxes.Clear();
Input.Axes["Mouse X"] = 1f;
Input.Axes["Mouse Y"] = -2f;
Input.Axes["Mouse ScrollWheel"] = .25f;
Input.RawAxes["Mouse X"] = 3f;
Input.RawAxes["Mouse Y"] = -4f;
Input.RawAxes["Mouse ScrollWheel"] = .75f;
VirtualInput.ClearBuffer();
VirtualInput.SuppressMouse = true;
VirtualInput.PollHardware();
VirtualInput.AccumulateBuffer();
VirtualInput.AdvanceFromBuffer();
Check(Same(VirtualInput.Current.Axis("Mouse X"), 0) && Same(VirtualInput.Current.AxisRaw("Mouse X"), 0) &&
      Same(VirtualInput.Current.Axis("Mouse Y"), 0) && Same(VirtualInput.Current.AxisRaw("Mouse Y"), 0) &&
      Same(VirtualInput.Current.Axis("Mouse ScrollWheel"), 0) && Same(VirtualInput.Current.AxisRaw("Mouse ScrollWheel"), 0),
    "panel suppression clears both raw and smoothed mouse streams");
VirtualInput.SuppressMouse = false;
Input.Axes.Clear();
Input.RawAxes.Clear();

var time = new TimeController();
Time.timeScale = 1;
Time.captureDeltaTime = .04f;
time.Enable();
Time.timeScale = 0;
time.Disable(preserveGameTimeScale: true);
Check(Time.timeScale == 0 && Time.captureDeltaTime == .04f, "disengagement preserves game pause and original capture delta");
Time.timeScale = 1;
time.Enable();
time.SetPaused(true);
time.Disable();
Check(Time.timeScale == 1, "unload releases mod-owned pause");
time.SetTickRate(float.NaN);
time.SetPhysicsRate(float.PositiveInfinity);
time.SetSpeed(float.NegativeInfinity);
Check(time.TickRate == 60 && time.PhysicsRate == 50 && time.Speed == 1, "nonfinite settings rejected");
time.SimulationRatesLocked = true;
time.SetTickRate(120);
time.SetPhysicsRate(120);
Check(time.TickRate == 60 && time.PhysicsRate == 50, "simulation rates locked during macro");
time.SimulationRatesLocked = false;
time.Enable();
time.SetPaused(true);
Time.deltaTime = 0;
time.StepFrames(10);
Check(!time.OnUpdate(), "requesting a step does not count frozen request frame");
time.OnLateUpdate();
for (var i = 0; i < 10; i++)
{
    Time.deltaTime = 1f / 60f;
    if (!time.OnUpdate()) throw new Exception("step dropped");
    time.OnLateUpdate();
}
Check(time.FrameCount == 10 && time.StepsRemaining == 0 && Time.timeScale == 0, "ten-step queue runs exactly ten frames");
time.StepFrames(int.MaxValue);
time.StepFrames(1);
Check(time.StepsRemaining == int.MaxValue, "step count cannot overflow");
time.SetPaused(false);
Time.deltaTime = 0;
Check(!time.OnUpdate(), "resume request cannot count a zero-delta frame");
time.Disable();

MelonPreferences.Loaded["TickRate"] = 144f;
MelonPreferences.Loaded["Speed"] = .25f;
MelonPreferences.Loaded["PhysicsRate"] = 72f;
MelonPreferences.Loaded["Key_Step"] = "F10";
var config = new TasConfig();
config.Initialize(new MelonLogger.Instance());
time = new TimeController();
config.ApplyTo(time);
config.Sync(time);
Check(config.TickRate == 144 && config.Speed == .25f && time.PhysicsRate == 72, "settings hydrate before first standby sync");
Check(Keybinds.Key(TasAction.Step) == KeyCode.F10, "loaded keybind applied to live map");
Keybinds.BeginRebind(TasAction.Step);
Input.Pressed.Add(KeyCode.F3);
Keybinds.PollRebind();
Check(!Keybinds.Down(TasAction.Step), "rebind completion cannot trigger newly bound action");
Input.Pressed.Clear();
Keybinds.PollRebind();
Input.Keys.Add(KeyCode.F3);
VirtualInput.PollHardware();
VirtualInput.AdvanceFromHardware();
Check(!VirtualInput.Current.Has(KeyCode.F3), "transport hotkey excluded from recorded gameplay input");
Input.Keys.Clear();
config.ResetAll(time);
Check(time.TickRate == 60 && time.Speed == 1 && !time.PhysicsRateOverridden, "reset works while disengaged and restores physics adoption");
MelonPreferences.FailSave = true;
config.MarkDirty();
config.Flush();
var saves = MelonPreferences.Saves;
MelonPreferences.FailSave = false;
Time.realtimeSinceStartup += 3;
config.Tick();
Check(MelonPreferences.Saves == saves + 1, "failed config save retried");

var path = Path.Combine(Path.GetTempPath(), "grannytas-regression-" + Guid.NewGuid().ToString("N") + ".tas");
try
{
    CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
    var macro = new MacroFile();
    macro.HasSetupMetadata = true;
    macro.Difficulty = 4;
    macro.SelectableVersion = 7;
    macro.ItemPreset = 2;
    macro.GameBuild = "1.2.3";
    macro.InitialInput.CopyFrom(pressed);
    var frame = new InputFrame();
    frame.Keys.Add(KeyCode.F12);
    frame.Axes["Mouse X"] = 0.123456791f;
    frame.Axes["Mouse Y"] = -0.00000012345679f;
    frame.Axes["Horizontal"] = float.Epsilon;
    frame.RawAxes["Mouse X"] = .987654321f;
    frame.RawAxes["Mouse Y"] = -.000000987654321f;
    frame.RawAxes["Horizontal"] = -float.Epsilon;
    frame.HasPose = true;
    frame.PlayerRotation = new Quaternion(.123456791f, -.00000012345679f, float.Epsilon, 1f);
    frame.CameraLocalRotation = new Quaternion(-.333333343f, .777777791f, -.125f, .5f);
    frame.RotationX = -44.9999962f;
    frame.PlayerPosition = new Vector3(12.3456791f, -0.00000012345679f, float.Epsilon);
    frame.HasPosition = true;
    frame.HasPhysicsState = true;
    frame.PhysicsStates.Add(capturedA);
    macro.Frames.Add(frame);
    macro.Snapshot.Entities.Add(new EntitySnapshot {
        Path = "/player", IsPlayer = true, RotationX = .123456791f,
        Position = new Vector3(.123456791f, -0.00000012345679f, float.Epsilon),
        Rotation = new Quaternion(0, .123456791f, 0, 1),
        CameraLocalRotation = new Quaternion(.125f, -.25f, .375f, .875f),
        HasCameraRotation = true,
    });
    macro.Save(path);
    var loaded = MacroFile.Load(path);
    Check(Same(frame.Axis("Mouse X"), loaded.Frames[0].Axis("Mouse X")) &&
          Same(frame.Axis("Mouse Y"), loaded.Frames[0].Axis("Mouse Y")) &&
          Same(float.Epsilon, loaded.Frames[0].Axis("Horizontal")) &&
          Same(frame.AxisRaw("Mouse X"), loaded.Frames[0].AxisRaw("Mouse X")) &&
          Same(frame.AxisRaw("Mouse Y"), loaded.Frames[0].AxisRaw("Mouse Y")), "axis streams round-trip bit for bit under comma culture");
    Check(loaded.Frames[0].HasPose && SameQuaternion(frame.PlayerRotation, loaded.Frames[0].PlayerRotation) &&
          SameQuaternion(frame.CameraLocalRotation, loaded.Frames[0].CameraLocalRotation) && Same(frame.RotationX, loaded.Frames[0].RotationX),
        "pre-frame look pose round-trips bit for bit");
    Check(loaded.Frames[0].HasPosition &&
          Same(frame.PlayerPosition.x, loaded.Frames[0].PlayerPosition.x) &&
          Same(frame.PlayerPosition.y, loaded.Frames[0].PlayerPosition.y) &&
          Same(frame.PlayerPosition.z, loaded.Frames[0].PlayerPosition.z),
        "recorded player position round-trips bit for bit");
    var loadedBody = loaded.Frames[0].PhysicsStates.Single();
    Check(loaded.Frames[0].HasPhysicsState && loadedBody.Identity == capturedA.Identity &&
          Same(loadedBody.Position.x, capturedA.Position.x) && SameQuaternion(loadedBody.Rotation, capturedA.Rotation) &&
          Same(loadedBody.Velocity.y, capturedA.Velocity.y) && Same(loadedBody.AngularVelocity.z, capturedA.AngularVelocity.z) &&
          loadedBody.IsKinematic == capturedA.IsKinematic && loadedBody.UseGravity == capturedA.UseGravity &&
          loadedBody.DetectCollisions == capturedA.DetectCollisions && loadedBody.Constraints == capturedA.Constraints &&
          loadedBody.Sleeping == capturedA.Sleeping,
        "v4 rigidbody checkpoint round-trips bit for bit");
    Check(Same(macro.Snapshot.Entities[0].Position.y, loaded.Snapshot.Entities[0].Position.y) &&
          Same(macro.Snapshot.Entities[0].RotationX, loaded.Snapshot.Entities[0].RotationX) &&
          loaded.Snapshot.Entities[0].HasCameraRotation &&
          SameQuaternion(macro.Snapshot.Entities[0].CameraLocalRotation, loaded.Snapshot.Entities[0].CameraLocalRotation),
        "snapshot body, pitch, and camera pivot round-trip exactly");
    Check(loaded.Frames[0].Has(KeyCode.F12) && loaded.InitialInput.Has(KeyCode.E), "all stored keys and initial input survive save/load");
    Check(loaded.HasSetupMetadata && loaded.Difficulty == 4 && loaded.SelectableVersion == 7 &&
          loaded.ItemPreset == 2 && loaded.GameBuild == "1.2.3", "v3 level setup metadata round-trips");
    var valid = File.ReadAllText(path);
    macro.Save(path);
    Check(File.ReadAllText(path) == valid, "atomic replacement preserves stable encoding");
    var finitePosition = frame.PhysicsStates[0].Position;
    frame.PhysicsStates[0].Position = new Vector3(float.NaN, 0, 0);
    Reject(() => macro.Save(path), "v4 save rejects non-finite rigidbody state");
    frame.PhysicsStates[0].Position = finitePosition;
    foreach (var (bad, name) in new[] {
        (valid.Replace("macro v4", "macro v99"), "unknown version rejected"),
        (valid.Replace("tickRate=60", "tickRate=NaN"), "NaN rate rejected"),
        (valid.Replace("tickRate=60", "tickRate=0"), "zero rate rejected"),
        (valid.Replace("frames=1", "frames=2"), "truncated recording detected"),
        (valid.Replace("F12|0|", "invalid_key|0|"), "unknown frame key rejected"),
        (valid.Replace("F12|0|", "F12|8|"), "invalid mouse mask rejected"),
        (valid.Replace("0.9876543", "NaN"), "invalid raw axis rejected"),
        (valid.Replace("0.31415927", "NaN"), "non-finite rigidbody state rejected"),
        (valid + "|0|0,0\n", "incomplete frame rejected") })
    {
        File.WriteAllText(path, bad);
        Reject(() => MacroFile.Load(path), name);
    }
    var v2Header = "# GrannyTAS macro v2\nframes=1\n---\n|0|0,0,0,0,0|0,0,0,0,0|";
    File.WriteAllText(path, v2Header + "0,0,0,0,0,0,0,1,0\n");
    Reject(() => MacroFile.Load(path), "zero pose quaternion rejected");
    File.WriteAllText(path, v2Header + "3.4028235E+38,0,0,1,0,0,0,1,0\n");
    Reject(() => MacroFile.Load(path), "huge pose quaternion rejected");
    File.WriteAllText(path, v2Header + "0,0\n");
    Reject(() => MacroFile.Load(path), "malformed pose shape rejected");
    File.WriteAllText(path, v2Header + "0,0,0,1,0,0,0,1,-12.5\n");
    var poseOnly = MacroFile.Load(path);
    Check(poseOnly.Frames[0].HasPose && !poseOnly.Frames[0].HasPosition &&
          Same(poseOnly.Frames[0].RotationX, -12.5f),
        "nine-value pose still loads and claims no recorded position");
    File.WriteAllText(path, v2Header + "0,0,0,1,0,0,0,1,0,1,NaN,3\n");
    Reject(() => MacroFile.Load(path), "non-finite recorded position rejected");
    File.WriteAllText(path, v2Header + "0,0,0,1,0,0,0,1,0,1,2\n");
    Reject(() => MacroFile.Load(path), "eleven-value pose shape rejected");
    Reject(() => EntitySnapshot.Decode("player", "/player|NaN,0,0|0,0,0,1|0"), "invalid transform rejected");
    File.WriteAllText(path, "# GrannyTAS macro v1\nframes=1\ninitialInput=E|0|1,-2,0.25,0,1\n---\nW|0|3,-4,0.5,-1,1\n");
    var legacy = MacroFile.Load(path);
    Check(!legacy.Frames[0].HasPose && Same(legacy.Frames[0].Axis("Mouse X"), legacy.Frames[0].AxisRaw("Mouse X")) &&
          Same(legacy.InitialInput.Axis("Mouse ScrollWheel"), legacy.InitialInput.AxisRaw("Mouse ScrollWheel")),
        "v1 macros load with legacy axes copied to raw stream and no pose");
    Check(!legacy.HasSetupMetadata, "legacy macros do not invent level setup metadata");
    File.WriteAllText(path, "# GrannyTAS macro v3\nhasSetupMetadata=0\nframes=1\n---\nW|0|0,0,0,0,1|0,0,0,0,1|-\n");
    var legacyV3 = MacroFile.Load(path);
    Check(legacyV3.Count == 1 && !legacyV3.Frames[0].HasPhysicsState,
        "v3 macros retain their five-field best-effort playback behavior");
}
finally { File.Delete(path); }

Check(MacroEngine.DefaultPath.EndsWith(".grannytas", StringComparison.Ordinal),
    "default macro path uses the .grannytas extension");

time = new TimeController();
Time.timeScale = 1;
time.Enable();
var engine = new MacroEngine(new MelonLogger.Instance(), time);
engine.Macro.Frames.Add(pressed);
engine.Macro.Scene = "wrong_scene";
engine.StartPlayback();
Check(engine.Mode == MacroMode.Idle, "playback refused in wrong scene");
engine.Macro.Scene = "test_scene";
engine.Macro.SeedManagerSeed = 42;
engine.StartPlayback();
Check(engine.Mode == MacroMode.Idle, "playback refused with wrong item seed");
engine.Macro.SeedManagerSeed = 0;
PlayerPrefs.Ints["GameSeed"] = 0;
time.Enable();
engine.StartPlayback();
Check(engine.Mode == MacroMode.Playing && time.SimulationRatesLocked, "playback locks simulation rates");
time.SetSpeed(.1f);
time.SetUncapped(true);
time.ToggleUncapped();
Check(time.Speed == 1f && !time.Uncapped, "playback remains locked to 1x including turbo shortcuts");
engine.StartRecording();
engine.Load("does-not-exist.tas");
Check(engine.Mode == MacroMode.Playing && engine.FrameCount == 1, "record/load cannot replace active playback");
Check(engine.TryDrivePlayback() && !engine.TryDrivePlayback() && engine.Mode == MacroMode.Idle && !time.SimulationRatesLocked,
    "playback completion unlocks transport after final input");
var desyncTime = new TimeController();
Time.timeScale = 1;
desyncTime.Enable();
var desyncEngine = new MacroEngine(new MelonLogger.Instance(), desyncTime);
desyncEngine.Macro.Scene = "test_scene";
var desyncFrame = new InputFrame { HasPhysicsState = true };
desyncFrame.PhysicsStates.Add(new PhysicsFrameState { Identity = "absent/rigidbody#0", Rotation = Quaternion.identity });
desyncEngine.Macro.Frames.Add(desyncFrame);
desyncEngine.StartPlayback();
Check(!desyncEngine.TryDrivePlayback() && desyncEngine.Mode == MacroMode.Idle && desyncEngine.Playhead == 0 &&
      desyncEngine.PendingStatus.Contains("physics desync") && !desyncTime.SimulationRatesLocked,
    "missing playback rigidbody aborts explicitly before issuing frame input");
desyncTime.Disable();
time.SetSpeed(.25f);
engine.StartRecording();
time.SetSpeed(.5f);
time.SetUncapped(true);
time.ToggleUncapped();
Check(time.Speed == .25f && !time.Uncapped && engine.RecordedAtSpeed == .25f,
    "recording locks speed and both turbo setters");
time.SetPaused(true);
time.StepFrame();
Check(time.Paused && time.StepsRemaining == 1, "speed lock preserves pause and frame-step controls");
time.SetPaused(false);
engine.CaptureFrame();
Check(engine.Macro.Frames[0].HasPhysicsState && engine.Macro.Frames[0].PhysicsStates.Count >= 2,
    "recording captures active rigidbodies at the input frame boundary");
engine.StartPlayback();
Check(engine.Mode == MacroMode.Recording && engine.FrameCount == 1, "play cannot discard active recording");
engine.StopRecording();
Check(engine.Mode == MacroMode.Idle && !time.SimulationRatesLocked && engine.HasUnsavedRecording &&
      !File.Exists(MacroEngine.DefaultPath) && !Directory.Exists(MacroEngine.SavedMacroDirectory),
    "recording stop leaves the take pending without writing a file");
Check(engine.LoadedPath == null && engine.FrameCount == 1,
    "an unsaved recording remains in memory without claiming a disk path");

var blockedLoadPath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "load-target.grannytas");
var loadTarget = new MacroFile { TickRate = 60, PhysicsRate = 60, Scene = "test_scene" };
loadTarget.Frames.Add(pressed);
loadTarget.Frames.Add(pressed);
loadTarget.Save(blockedLoadPath);
engine.Load(blockedLoadPath);
engine.StartRecording();
Check(engine.Mode == MacroMode.Idle && engine.FrameCount == 1 && engine.HasUnsavedRecording,
    "load and a new recording are both blocked while a take is pending");

foreach (var invalidName in new[] { "", "   ", "../escape", "folder/replay", "folder\\replay", "bad:name", "CON" })
{
    Check(!engine.TrySaveRecording(invalidName, out var invalidStatus) &&
          !string.IsNullOrWhiteSpace(invalidStatus) && engine.HasUnsavedRecording,
        $"invalid replay name '{invalidName}' is rejected without losing the take");
}

Check(engine.TrySaveRecording("  named take.GRANNYTAS  ", out var savedStatus) &&
      savedStatus.Contains("named take.grannytas", StringComparison.OrdinalIgnoreCase),
    "a trimmed replay name with an optional extension saves successfully");
var namedPath = Path.Combine(MacroEngine.SavedMacroDirectory, "named take.grannytas");
Check(File.Exists(namedPath) && !engine.HasUnsavedRecording && engine.LoadedPath == namedPath,
    "named save clears pending state and points LoadedPath at the saved replay");
Check(engine.SavedMacros.Count == 1 && engine.SavedMacros[0].Path == namedPath,
    "named save refreshes the saved replay catalog");
var peekedHeader = MacroFile.PeekHeader(namedPath);
var loadedNamed = MacroFile.Load(namedPath);
Check(peekedHeader.Frames == loadedNamed.Count, "PeekHeader's frame count matches a full Load of the named file");

engine.StartRecording();
engine.CaptureFrame();
engine.StopRecording();
Check(!engine.TrySaveRecording("named take", out var duplicateStatus) &&
      duplicateStatus.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
      engine.HasUnsavedRecording && MacroFile.Load(namedPath).Count == loadedNamed.Count,
    "duplicate names never overwrite the existing replay or clear the pending take");
engine.DiscardRecording();
Check(!engine.HasUnsavedRecording && engine.FrameCount == 0 && engine.LoadedPath == null,
    "discard clears the pending recording from memory");
engine.StartPlayback();
Check(engine.Mode == MacroMode.Idle && engine.FrameCount == 0,
    "play cannot replay a discarded take");
engine.Load(namedPath);
Check(engine.LoadedPath == namedPath && engine.FrameCount == loadedNamed.Count,
    "Load points LoadedPath at whatever file was just loaded");
time.SetSpeed(.5f);
Check(time.Speed == .5f, "stopping recording unlocks speed");
Directory.Delete(MacroEngine.MacroDirectory, true);
File.Delete(blockedLoadPath);
Directory.Delete(MelonLoader.Utils.MelonEnvironment.UserDataDirectory);

foreach (var mismatch in new[] { "difficulty differs", "selectable version differs", "item preset differs", "item seed differs" })
{
    var pendingTime = new TimeController();
    Time.timeScale = 1;
    pendingTime.Enable();
    var setup = new FakeMacroGameSetup { RestartRequired = true, RestartReason = mismatch };
    var pending = new MacroEngine(new MelonLogger.Instance(), pendingTime, setup);
    pending.Macro.Scene = "test_scene";
    pending.Macro.Frames.Add(pressed);
    pending.StartPlayback();
    Check(pending.Mode == MacroMode.Preparing && setup.ApplyCalls == 1 && !pendingTime.Enabled,
        mismatch + " applies setup before restart and prevents old-scene TAS advancement");
    pending.UpdatePreparing(true);
    Check(pending.Mode == MacroMode.Preparing && pending.Playhead == 0,
        mismatch + " cannot start before a scene-load callback");
    pending.NotifySceneLoaded("another_scene");
    pending.UpdatePreparing(true);
    Check(pending.Mode == MacroMode.Preparing, mismatch + " ignores unrelated scene-load callbacks");
    pending.NotifySceneLoaded("test_scene");
    pending.UpdatePreparing(true);
    Check(pending.Mode == MacroMode.Playing && pending.Playhead == 0,
        mismatch + " starts at frame zero only after load and readiness");
    pending.StopPlayback();
    pendingTime.Disable();
}

var cancelTime = new TimeController();
Time.timeScale = 1;
cancelTime.Enable();
var cancelSetup = new FakeMacroGameSetup { RestartRequired = true };
var cancelled = new MacroEngine(new MelonLogger.Instance(), cancelTime, cancelSetup);
cancelled.Macro.Scene = "test_scene";
cancelled.Macro.Frames.Add(pressed);
cancelled.StartPlayback();
cancelled.StopPlayback();
cancelled.NotifySceneLoaded("test_scene");
cancelled.UpdatePreparing(true);
Check(cancelled.Mode == MacroMode.Idle && cancelled.Playhead == 0,
    "cancelling during reload cannot start playback from a later callback");

var failedTime = new TimeController();
Time.timeScale = 1;
failedTime.Enable();
var failedSetup = new FakeMacroGameSetup { RestartRequired = true, LoadedMatches = false };
var failed = new MacroEngine(new MelonLogger.Instance(), failedTime, failedSetup);
failed.Macro.Scene = "test_scene";
failed.Macro.Frames.Add(pressed);
failed.StartPlayback();
failed.NotifySceneLoaded("test_scene");
failed.UpdatePreparing(true);
Check(failed.Mode == MacroMode.Idle && failed.Playhead == 0 && !failedTime.SimulationRatesLocked,
    "loaded setup mismatch aborts and unlocks without issuing input");

var timeoutTime = new TimeController();
Time.timeScale = 1;
timeoutTime.Enable();
var timeoutSetup = new FakeMacroGameSetup { RestartRequired = true, Now = 10 };
var timedOut = new MacroEngine(new MelonLogger.Instance(), timeoutTime, timeoutSetup);
timedOut.Macro.Scene = "test_scene";
timedOut.Macro.Frames.Add(pressed);
timedOut.StartPlayback();
timeoutSetup.Now = 56;
timedOut.UpdatePreparing(false);
Check(timedOut.Mode == MacroMode.Idle && timedOut.Playhead == 0 && !timeoutTime.SimulationRatesLocked,
    "playback setup timeout cleans up without issuing input");

time.SetTickRate(165);
var physicsBeforeSpeed = Time.fixedDeltaTime;
foreach (var speed in new[] { .01f, .1f, .25f, .56f, 1f, 4f })
{
    time.SetSpeed(speed);
    var expectedScale = time.EffectiveFpsCap / time.TickRate;
    Check(Same(Time.timeScale, expectedScale) && MathF.Abs(Time.captureDeltaTime * Time.timeScale - 1f / 165f) < 1e-8f
        && Same(Time.fixedDeltaTime, physicsBeforeSpeed), $"{speed}x aligns engine clock while preserving frame/physics deltas");
}
time.SetSpeed(.1f);
time.SetPaused(true);
Check(Time.timeScale == 0 && Time.captureDeltaTime == 0, "slow-motion pause stops both clocks");
time.StepFrame();
time.OnLateUpdate();
Check(Time.timeScale == 1 && Application.targetFrameRate == 165 && Same(Time.captureDeltaTime, 1f / 165f),
    "slow-motion single step uses normal pacing and one simulation delta");
Time.deltaTime = Time.captureDeltaTime * Time.timeScale;
time.OnUpdate();
time.OnLateUpdate();
Check(Time.timeScale == 0 && Time.captureDeltaTime == 0, "single step returns to frozen clocks");
time.SetPaused(false);
Time.deltaTime = Time.captureDeltaTime * Time.timeScale;
time.OnUpdate(); time.OnUpdate(); time.OnUpdate();
Check(!time.TimingMismatch, "compensated clock passes observed-delta check");

var frameBeforeGamePause = time.FrameCount;
time.SetPaused(true);
time.StepFrames(2);
time.OnLateUpdate();
Check(Time.timeScale == 1 && time.StepsRemaining == 2,
    "queued TAS step is armed before an intervening game pause");
time.SetGamePaused(true);
Check(time.GamePaused && time.Paused && time.StepsRemaining == 2 &&
      Time.timeScale == 0 && Time.captureDeltaTime == 0,
    "game pause freezes clocks without changing TAS pause or queued steps");
Time.deltaTime = 1f / time.TickRate;
Check(!time.OnUpdate(), "game pause does not advance the TAS frame axis");
time.OnLateUpdate();
Check(time.StepsRemaining == 2 && Time.timeScale == 0,
    "game pause does not arm or consume a queued TAS step");
time.SetTickRate(120);
Check(Time.timeScale == 0 && Time.captureDeltaTime == 0,
    "timing setters keep the game pause frozen");
Time.timeScale = 1;
Time.captureDeltaTime = 1f;
time.Enforce();
Check(Time.timeScale == 0 && Time.captureDeltaTime == 0,
    "timing enforcement respects the game pause");
time.SetGamePaused(false);
Check(!time.GamePaused && time.StepsRemaining == 2 && Time.timeScale == 1,
    "unpausing the game restores the interrupted running step and queue");
Time.deltaTime = 0;
Check(!time.OnUpdate(), "zero-delta game-pause transition cannot advance the interrupted step");
time.OnLateUpdate();
Check(Time.timeScale == 1 && time.StepsRemaining == 2,
    "zero-delta unpause frame cannot consume the interrupted TAS step");
Time.deltaTime = 1f / time.TickRate;
Check(time.OnUpdate() && time.FrameCount == frameBeforeGamePause + 1,
    "first armed TAS step advances exactly one macro frame after unpause");
time.OnLateUpdate();
Check(time.StepsRemaining == 1 && Time.timeScale == 1,
    "remaining queued TAS step continues normally after game unpause");
time.OnUpdate();
time.OnLateUpdate();
Check(time.StepsRemaining == 0 && Time.timeScale == 0,
    "preserved queued TAS steps complete and return to TAS pause");
time.SetPaused(false);
Time.deltaTime = 1f / time.TickRate;
time.OnUpdate(); time.OnUpdate(); time.OnUpdate();
Time.deltaTime *= 2;
time.OnUpdate();
Check(time.TimingMismatch, "incorrect engine delta raises runtime timing diagnostic");
time.Disable();
Check(!time.TimingMismatch, "disengaging clears timing diagnostic");
Console.WriteLine($"{passed} regression checks passed");
