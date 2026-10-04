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
PlayerGate.Invalidate();
Check(!PlayerGate.IsReady && !PlayerGate.CanBridgeTransientControlLoss,
    "missing player remains a hard gate failure");
var gatePlayer = new Il2Cpp.MobileFPS();
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
Check(PhysicsFrameState.TryApply(new[] { capturedA }, out _) && PhysicsFrameState.CorrectionCount == 0 &&
      PhysicsFrameState.LastDiffering == 0,
    "a rigidbody already matching its checkpoint bit for bit is left untouched");
var recordedPosition = capturedA.Position;
rigidA.position = new Vector3(recordedPosition.x + .5e-6f, recordedPosition.y, recordedPosition.z);
Check(PhysicsFrameState.TryApply(new[] { capturedA }, out _) && PhysicsFrameState.CorrectionCount == 1 &&
      PhysicsFrameState.LastDiffering == 1 && Same(rigidA.position.x, recordedPosition.x),
    "sub-micrometre rigidbody drift is measured and corrected to the exact bits");
rigidA.position = new Vector3(recordedPosition.x + 2e-6f, recordedPosition.y, recordedPosition.z);
rigidA.velocity = Vector3.zero;
Check(PhysicsFrameState.TryApply(new[] { capturedA }, out _) && PhysicsFrameState.CorrectionCount == 2 &&
      Same(rigidA.position.x, recordedPosition.x) && Same(rigidA.velocity.x, capturedA.Velocity.x) &&
      PhysicsFrameState.MaxPositionDrift > 1e-6f,
    "dynamic rigidbody state is restored exactly");
rigidA.WakeUp();
Check(PhysicsFrameState.TryApply(new[] { capturedA }, out _) && rigidA.IsSleeping() && PhysicsFrameState.CorrectionCount == 3,
    "sleep state is restored after the corrective writes");
var kinematic = new PhysicsFrameState();
kinematic.CopyFrom(capturedA);
kinematic.IsKinematic = true;
rigidA.velocity = new Vector3(99, 98, 97);
Check(PhysicsFrameState.TryApply(new[] { kinematic }, out _) && Same(rigidA.velocity.x, 99),
    "kinematic checkpoint application avoids invalid velocity writes");
var sceneSeparator = capturedA.Identity.IndexOf(':');
Check(capturedA.Identity.StartsWith("test_scene:", StringComparison.Ordinal) && sceneSeparator > 0,
    "rigidbody identities are qualified by their scene, so two loaded scenes cannot collide");
var legacyState = new PhysicsFrameState();
legacyState.CopyFrom(capturedA);
legacyState.Identity = capturedA.Identity.Substring(sceneSeparator + 1);
rigidA.position = new Vector3(recordedPosition.x + 1f, recordedPosition.y, recordedPosition.z);
Check(PhysicsFrameState.TryApply(new[] { legacyState }, out _) && PhysicsFrameState.LastMissing == 0 &&
      Same(rigidA.position.x, recordedPosition.x) && !rigidA.isKinematic,
    "v4 scene-less rigidbody identities still resolve");
Check(PhysicsFrameState.TryApply(new[] { new PhysicsFrameState { Identity = "test_scene:missing#0", Rotation = Quaternion.identity }, capturedA },
          out var missingError) && missingError == null && PhysicsFrameState.LastMissing == 1 &&
      PhysicsFrameState.LastFirstMissing == "test_scene:missing#0",
    "a missing recorded rigidbody is counted without abandoning the rest of the checkpoint");
Check(!PhysicsFrameState.TryApply(new[] { new PhysicsFrameState { Identity = "" } }, out var invalidCheckpoint) &&
      invalidCheckpoint.Contains("invalid"),
    "a malformed rigidbody checkpoint is still refused");

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
var syncsBefore = Physics.SyncCalls;
VirtualInput.AdvanceFrom(drifted);
Check(Same(previewPlayer.transform.position.z, 3f) && previewPlayer.Controller.enabled &&
      Same(VirtualInput.MaxPositionDrift, 0.25f) && VirtualInput.PositionCorrections == 1,
    "playback puts the player back on the recorded position and reports the drift");
Check(Physics.SyncCalls == syncsBefore + 1 && !VirtualInput.TeleportByToggle,
    "position correction syncs the controller instead of rebuilding it (keeps its grounded state)");

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
VirtualInput.ResetDrift();
VirtualInput.ResetTeleportMode();
var fallbackEvents = 0;
VirtualInput.TeleportFallbackEngaged = () => fallbackEvents++;
for (var i = 0; i < 4; i++)
{
    previewPlayer.transform.position = new Vector3(1f, 2f, 9f);
    VirtualInput.AdvanceFrom(drifted);
}
Check(VirtualInput.TeleportByToggle && fallbackEvents == 1 && Same(previewPlayer.transform.position.z, 3f) &&
      previewPlayer.Controller.enabled && VirtualInput.PositionCorrections == 4,
    "corrections that do not stick fall back to the controller toggle, once");
VirtualInput.ResetTeleportMode();
VirtualInput.TeleportFallbackEngaged = null;
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
    frame.Trace = new FrameTrace { HasClock = true, Dt = 1f / 60f, Elapsed = 1.0 / 3.0, Phase = 0.0123456789012345, FixedSteps = 2 };
    frame.Trace.Pickups.Add("Hammer");
    macro.Frames.Add(frame);
    macro.Rig.Values.Add(new KeyValuePair<string, float[]>("fps.IsCrouched", new[] { 1f }));
    macro.Rig.Animations.Add(new PlayerRigSnapshot.AnimState { Owner = "fps.CameraAnim", Name = "walk|bob", Enabled = true, Weight = .5f, Time = .123456791f, Speed = 1f });
    macro.Rig.Transforms.Add(new PlayerRigSnapshot.LocalPose { Path = "0/2", Name = "Main Camera", Position = new Vector3(0, 1.6f, 0), Rotation = Quaternion.identity, Scale = new Vector3(1, 1, 1) });
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
        "rigidbody checkpoint round-trips bit for bit");
    var loadedTrace = loaded.Frames[0].Trace;
    Check(loaded.HasTraces && loadedTrace.HasClock && Bits.Same(loadedTrace.Phase, frame.Trace.Phase) &&
          Bits.Same(loadedTrace.Elapsed, frame.Trace.Elapsed) && loadedTrace.FixedSteps == 2 &&
          loadedTrace.Pickups.SequenceEqual(new[] { "Hammer" }),
        "v5 sync trace round-trips bit for bit");
    Check(loaded.Rig.Values.Count == 1 && loaded.Rig.Animations.Single().Name == "walk|bob" &&
          Same(loaded.Rig.Animations[0].Time, .123456791f) && loaded.Rig.Transforms.Single().Name == "Main Camera" &&
          Same(loaded.Rig.Transforms[0].Position.y, 1.6f),
        "player rig header round-trips, including names containing separators");
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
        (valid.Replace("macro v5", "macro v99"), "unknown version rejected"),
        (valid.Replace("|t:", "|t:x"), "malformed trace rejected"),
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
Check(desyncEngine.TryDrivePlayback() && desyncEngine.Mode == MacroMode.Playing && desyncEngine.Playhead == 1,
    "a missing playback rigidbody no longer aborts the replay");
desyncEngine.StopPlayback();
Check(desyncEngine.LastReport != null && desyncEngine.LastReport.MissingRigidbodies == 1 &&
      !desyncEngine.LastReport.Exact && !desyncTime.SimulationRatesLocked,
    "the missing rigidbody is carried by the sync report instead");
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
foreach (var speed in new[] { .01f, .1f, .15f, .23f, .25f, .3f, .37f, .45f, .48f, .56f, 1f, 1.5f, 4f })
{
    time.SetSpeed(speed);
    var capped = time.EffectiveFpsCap / time.TickRate;
    var powerOfTwo = Time.timeScale > 0 && (BitConverter.SingleToInt32Bits(Time.timeScale) & 0x7FFFFF) == 0;
    Check(powerOfTwo && Time.timeScale <= capped * 1.4143f && Time.timeScale >= capped / 1.4143f &&
          Same(Time.captureDeltaTime * Time.timeScale, 1f / 165f) &&
          (double)Time.captureDeltaTime * Time.timeScale == (double)(1f / 165f) &&
          Same(Time.fixedDeltaTime, physicsBeforeSpeed),
        $"{speed}x engine clock is a power of two and the frame delta is exact in float and double");
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
Check(!time.TimingMismatch && time.DeltaExact, "compensated clock passes the observed-delta check bit for bit");

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

// ---- sync traces ---------------------------------------------------------------
// The replay check is only as good as the round trip of what it compares.
var fullTrace = new FrameTrace
{
    HasClock = true, Dt = 1f / 60f, Elapsed = 0.016666667535901070, Phase = 0.012345678901234567, FixedSteps = 1,
    HasCamera = true, CameraPosition = new Vector3(1.1f, 2.2f, 3.3f), CameraRotation = new Quaternion(.1f, .2f, .3f, .9f),
    HasController = true, Grounded = true, ControllerVelocity = new Vector3(0, -9.81f, 0.0001f), ControllerHeight = 1.9f,
    HasFall = true, Falling = false, Landing = true, FallDuration = .5f,
    HasRng = true, Rng0 = -5, Rng1 = int.MaxValue, Rng2 = int.MinValue, Rng3 = 7,
};
fullTrace.Enemies.Add(new EnemySample
{
    Path = "/Granny|odd,name;x", Position = new Vector3(4, 5, 6), Rotation = new Quaternion(0, .7071068f, 0, .7071068f),
    HasAgent = true, AgentVelocity = new Vector3(.5f, 0, -.25f),
});
fullTrace.Enemies.Add(new EnemySample { Path = "/Grandpa", Position = new Vector3(7, 8, 9), Rotation = Quaternion.identity });
fullTrace.Rays.Add(new RaySample
{
    Kind = RaySample.Door, Order = 1, Position = new Vector3(.123456791f, 1.6f, -2), Rotation = new Quaternion(0, 0, .3826834f, .9238795f),
    HasFall = true, Falling = true, Landing = true, Hit = "Door;Front,1",
});
fullTrace.Rays.Add(new RaySample { Kind = RaySample.Pick, Position = Vector3.zero, Rotation = Quaternion.identity, Hit = "" });
fullTrace.Pickups.Add("Hammer");
fullTrace.Pickups.Add("Key, spare");
var decodedTrace = FrameTrace.Decode(fullTrace.Encode());
Check(decodedTrace.Encode() == fullTrace.Encode() && Bits.Same(decodedTrace.Phase, fullTrace.Phase) &&
      Bits.Same(decodedTrace.Elapsed, fullTrace.Elapsed) && decodedTrace.Rng2 == int.MinValue &&
      decodedTrace.Enemies[0].Path == "/Granny|odd,name;x" && decodedTrace.Enemies[0].HasAgent && !decodedTrace.Enemies[1].HasAgent &&
      decodedTrace.Rays[0].Hit == "Door;Front,1" && decodedTrace.Rays[0].Falling && decodedTrace.Rays[0].Landing &&
      !decodedTrace.Rays[1].HasFall && decodedTrace.Pickups[1] == "Key, spare",
    "frame trace round-trips bit for bit, including names containing separators");
var clonedTrace = fullTrace.Clone();
clonedTrace.Rays[0].Hit = "changed";
clonedTrace.Pickups.Clear();
Check(fullTrace.Rays[0].Hit == "Door;Front,1" && fullTrace.Pickups.Count == 2, "trace clones own their lists");
Check(FrameTrace.Decode("-") == null, "absent trace decodes to null");
Reject(() => FrameTrace.Decode("t:1,2,3"), "short trace group rejected");
Reject(() => FrameTrace.Decode("t:NaN,0,0,0"), "non-finite trace value rejected");
Reject(() => FrameTrace.Decode("r:5,0,0,0,0,0,0,0,1,-1,"), "invalid ray kind rejected");
Check(FrameTrace.Decode("z:from,a,newer,build;p:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Key"))).Pickups.Single() == "Key",
    "unknown trace groups are skipped so newer macros still load");

// ---- sync report ------------------------------------------------------------------
var verdict = new SyncReport { TracesPresent = true, FramesReplayed = 10 };
var boundary = new InputFrame { Trace = new FrameTrace { HasClock = true, Dt = 1f / 60f, Phase = .01, Elapsed = 1.0 / 60, FixedSteps = 1 } };
var sameClock = new FrameTrace { HasClock = true, Dt = 1f / 60f, Phase = .01 + 5e-14, Elapsed = 1.0 / 60 - 5e-14, FixedSteps = 1 };
verdict.CompareBoundary(3, boundary, sameClock, new InputFrame());
Check(verdict.Exact && verdict[SyncReport.Area.Clock].Compared == 1 && verdict.Summary().Contains("BIT-IDENTICAL"),
    "clock differences at double-rounding scale are not divergences");
var skippedStep = new FrameTrace { HasClock = true, Dt = 1f / 60f, Phase = .01, Elapsed = 1.0 / 60, FixedSteps = 0 };
verdict.CompareBoundary(4, boundary, skippedStep, new InputFrame());
Check(!verdict.Exact && verdict.FirstDivergence(out var firstFrame, out var firstArea) && firstFrame == 4 &&
      firstArea == SyncReport.Area.FixedSteps && verdict.Summary().Contains("frame 4"),
    "a FixedUpdate landing on another frame is reported with its frame and area");
Check(verdict.Lines().Any(l => l.StartsWith("fixed-step pattern", StringComparison.Ordinal) && l.Contains("  1  ")) &&
      verdict.Lines().Any(l => l.Contains("f4 fixed-step pattern")),
    "the report file lists the area table and the first differences");
var legacyVerdict = new SyncReport { TracesPresent = false, FramesReplayed = 5 };
Check(legacyVerdict.Summary().Contains("predates"), "a macro without traces says it could not be checked");

// ---- event attribution ------------------------------------------------------------------
var t0 = new FrameTrace();
var t1 = new FrameTrace();
SyncTracker.BeginRecording();
SyncTracker.OnPickup("before any frame");
SyncTracker.RecordIssued(0, t0);
SyncTracker.OnPickup("Hammer");
SyncTracker.RecordIssued(1, t1);
SyncTracker.Stop();
Check(t0.Pickups.SequenceEqual(new[] { "Hammer" }) && t1.Pickups.Count == 0,
    "pickups belong to the most recently issued macro frame");
var pickupReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(pickupReport);
SyncTracker.ReplayIssued(0, t0);
SyncTracker.ReplayIssued(1, t1);
SyncTracker.OnPickup("Hammer");
SyncTracker.Stop();
Check(pickupReport[SyncReport.Area.Pickups].Differing == 1 && pickupReport[SyncReport.Area.Pickups].FirstFrame == 0,
    "a missed pickup is reported on the frame it was recorded, and the final frame's window is not compared");

// ---- interaction ray pin ------------------------------------------------------------------
var rayPlayer = new Il2Cpp.MobileFPS();
var rayFall = rayPlayer.gameObject.AddComponent<Il2Cpp.FallingHolder>();
rayPlayer.FallingHolder = rayFall;
var pick = new Il2Cpp.PickRay { Player = rayPlayer.gameObject };
pick.transform.position = new Vector3(1f, 1.6f, 1f);
pick.transform.localPosition = new Vector3(0, .6f, 0);
Physics.HitAt = origin => Same(origin.x, 1f) ? "Apple" : "Drawer";
VirtualInput.Active = true;
Time.deltaTime = 1f / 60f;
var rayTrace = new FrameTrace();
SyncTracker.BeginRecording();
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick));
InteractionPin.After();
Check(rayTrace.Rays.Count == 0, "no ray is recorded before the first macro frame exists");
SyncTracker.RecordIssued(0, rayTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick));
InteractionPin.After();
SyncTracker.Stop();
Check(rayTrace.Rays.Count == 1 && rayTrace.Rays[0].Hit == "Apple" && Same(rayTrace.Rays[0].Position.x, 1f) &&
      rayTrace.Rays[0].HasFall && !rayTrace.Rays[0].Falling && rayTrace.Rays[0].Order == 0,
    "recording stores the ray as cast, what it hit, and the fall flags");

var rayReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(rayReport);
SyncTracker.ReplayIssued(0, rayTrace);
pick.transform.position = new Vector3(1.0005f, 1.6f, 1f);
var localBeforePin = pick.transform.localPosition;
rayFall.isFalling = true;
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick));
var castFrom = pick.transform.position;
var fallingDuringCast = rayFall.isFalling;
InteractionPin.After();
Check(Same(castFrom.x, 1f) && !fallingDuringCast && rayReport.RayPins == 1 && rayReport.FlagPins == 1,
    "replay casts the pickup ray from the recorded pose with the recorded fall flags");
Check(Bits.Same(pick.transform.localPosition, localBeforePin) && rayFall.isFalling,
    "the pin is undone after the call, so nothing but the raycast sees it");
Check(rayReport[SyncReport.Area.RayPose].Differing == 1 && rayReport[SyncReport.Area.RayHit].Differing == 1 &&
      rayReport[SyncReport.Area.EffectiveRayHit].Differing == 0 && rayReport[SyncReport.Area.ScriptOrder].Differing == 0,
    "the report shows the natural divergence and that the pinned cast hit the recorded target");
SyncTracker.ReplayIssued(1, rayTrace);
pick.transform.position = new Vector3(1f, 1.6f, 1f);
pick.transform.rotation = Quaternion.identity;
rayFall.isFalling = false;
var setPosesBefore = pick.transform.SetPoseCalls;
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick));
InteractionPin.After();
Check(pick.transform.SetPoseCalls == setPosesBefore && rayReport.RayPins == 1 && rayReport.FlagPins == 1,
    "a ray already on the recorded pose is not touched");
var door = new Il2Cpp.DoorRay();
InteractionPin.Before(RaySample.Door, door, null, InteractionPin.DoorDistance(door));
InteractionPin.After();
Check(rayReport.UnmatchedRays == 1, "a cast with no recorded counterpart is counted, not pinned");
SyncTracker.Stop();
Time.deltaTime = 0;
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick));
InteractionPin.After();
Check(pick.transform.SetPoseCalls == setPosesBefore, "frozen frames never pin");

// ---- recorded pickups: steering, click, forcing ----------------------------------------------
// Recorded: the pickup ray hit "Pliers" and the game picked it up. Replay: the
// ray is on the recorded pose, but the pliers sit 3 cm to the side, so it
// hits the drawer behind them.
Time.deltaTime = 1f / 60f;
var steerInventory = new GameObject().AddComponent<Il2Cpp.Inventory>();
var pliersHand = new GameObject();
pliersHand.SetActive(false);
steerInventory.ItemDefs.Add(new Il2Cpp.ItemDefs { itemName = "Pliers", handObject = pliersHand });
pick.Inventory = steerInventory;
var pliers = new GameObject { name = "Pliers" };
pliers.AddComponent<Il2Cpp.ItemSeedData>().itemName = "Pliers";
pliers.AddComponent<Collider>().Extents = new Vector3(.05f, .05f, .05f);
var pliersAside = new Vector3(1.03f, 1.6f, 1.8f);
var pliersLocal = new Vector3(.03f, 0, .3f);
void PlacePliers(Vector3 world)
{
    Time.frameCount++; // each scenario is its own engine frame
    pliers.SetActive(true);
    pliers.transform.position = world;
    pliers.transform.localPosition = pliersLocal;
}
// What the game's own PickRay.Update does on a cast that reaches the item with a click.
void GamePickup()
{
    if (!pick.buttonClicked || Physics.HitAt(pick.transform.position) != "Pliers") return;
    pick.buttonClicked = false;
    steerInventory.PickupItem("Pliers");
}
PlacePliers(pliersAside);
Physics.HitAt = origin => Same(pliers.transform.position.x, origin.x) ? "Pliers" : "Drawer";
Physics.HitDistance = 1f;
pick.transform.position = new Vector3(1f, 1.6f, 1f);
pick.transform.rotation = Quaternion.identity;
var pickupTrace = new FrameTrace();
pickupTrace.Rays.Add(new RaySample { Kind = RaySample.Pick, Position = pick.transform.position, Rotation = pick.transform.rotation, Hit = "Pliers" });
pickupTrace.Pickups.Add("Pliers");
var lookTrace = new FrameTrace();
lookTrace.Rays.Add(pickupTrace.Rays[0].Clone());

var steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
var pliersPoses = pliers.transform.SetPoseCalls;
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var hitDuringCall = Physics.HitAt(pick.transform.position);
var clickedDuringCall = pick.buttonClicked;
GamePickup();
InteractionPin.After(pick);
Check(hitDuringCall == "Pliers" && pliers.transform.SetPoseCalls == pliersPoses + 1 && steerReport.ItemSteers == 1,
    "a recorded item just off the pinned ray is put under it for the game's own pickup");
Check(clickedDuringCall && steerReport.ClickPins == 1 && steerReport.ForcedPickups == 0 && pliersHand.activeSelf,
    "with no ring left by the previous cast, the recorded click is handed to the game, which then picks the item up");
Check(Bits.Same(pliers.transform.localPosition, pliersLocal) && !pick.buttonClicked,
    "the steered item is put back and the click cleared when the call ends");
SyncTracker.ReplayIssued(1, new FrameTrace());
Check(steerReport[SyncReport.Area.Pickups].Differing == 0 && steerReport[SyncReport.Area.EffectiveRayHit].Differing == 1,
    "the pickup happens on the recorded frame, and the report still shows the ray missed before steering");
Check(steerReport.Lines().Any(l => l.Contains("pickup items") && l.Contains(" 1 ")) &&
      steerReport.Lines().Any(l => l.Contains("'Pliers' moved 3.0 cm")) &&
      steerReport.Lines().Any(l => l.Contains("pickup clicks") && l.Contains(" 1 ")),
    "the report counts the steer and the click, and says how far the item was moved");
SyncTracker.Stop();

PlacePliers(pliersAside);
pliersHand.SetActive(false);
var ring = new GameObject();
pick.Ring = ring;
VirtualInput.Previous.Clear();
VirtualInput.Current.Clear();
VirtualInput.Current.Keys.Add(KeyCode.E);
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var clickedWithRing = pick.buttonClicked;
pick.buttonClicked = true; // the game's own gate passes
GamePickup();
InteractionPin.After(pick);
Check(!clickedWithRing && steerReport.ClickPins == 0 && steerReport.ItemSteers == 1 && pliersHand.activeSelf,
    "a click the game accepts by itself (ring showing, interact edge) is left to the game");
SyncTracker.Stop();

// The replay is in sync: the cast hits the recorded item and the ring is up.
PlacePliers(new Vector3(1f, 1.6f, 1.8f));
pliersHand.SetActive(false);
pliersPoses = pliers.transform.SetPoseCalls;
var syncsInSync = Physics.SyncCalls;
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var clickedInSync = pick.buttonClicked;
pick.buttonClicked = true; // the game's own gate passes
GamePickup();
InteractionPin.After(pick);
Check(!clickedInSync && pliers.transform.SetPoseCalls == pliersPoses && Physics.SyncCalls == syncsInSync &&
      steerReport.ItemSteers + steerReport.ClickPins + steerReport.FlagPins + steerReport.ForcedPickups == 0 && pliersHand.activeSelf,
    "an in-sync pickup cast is left entirely to the game: nothing moved, synced, clicked or forced");
SyncTracker.Stop();
VirtualInput.Current.Clear();
pick.Ring = null;

PlacePliers(pliersAside);
pliersHand.SetActive(false);
pick.WJ.IsJumping = true;
pick.PlayerStatus.IsJumpscared = true;
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var gatesOpenDuringCall = !pick.WJ.IsJumping && !pick.PlayerStatus.IsJumpscared;
GamePickup();
InteractionPin.After(pick);
Check(gatesOpenDuringCall && pick.WJ.IsJumping && pick.PlayerStatus.IsJumpscared && steerReport.FlagPins == 1 &&
      steerReport.ForcedPickups == 0,
    "the jump and jumpscare gates are opened for the recorded pickup's cast only");
SyncTracker.Stop();
pick.WJ.IsJumping = false;
pick.PlayerStatus.IsJumpscared = false;

PlacePliers(pliersAside);
pliersPoses = pliers.transform.SetPoseCalls;
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, lookTrace);
var syncsBeforeLook = Physics.SyncCalls;
var picksBeforeLook = steerInventory.PickupCalls;
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var clickedOnLook = pick.buttonClicked;
InteractionPin.After(pick);
Check(pliers.transform.SetPoseCalls == pliersPoses && Physics.SyncCalls == syncsBeforeLook && steerReport.ItemSteers == 0 &&
      !clickedOnLook && steerInventory.PickupCalls == picksBeforeLook,
    "an item the recording only looked at is never moved, clicked or picked up");
SyncTracker.ReplayIssued(1, pickupTrace);
SyncTracker.OnPickup("Pliers");
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
Check(pliers.transform.SetPoseCalls == pliersPoses && steerReport.ItemSteers == 0 && steerInventory.PickupCalls == picksBeforeLook,
    "an item the replay already picked up in that frame is not steered or forced again");
SyncTracker.Stop();

PlacePliers(new Vector3(1.4f, 1.6f, 1.8f));
pliersHand.SetActive(false);
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var farHit = Physics.HitAt(pick.transform.position);
GamePickup();
InteractionPin.After(pick);
SyncTracker.ReplayIssued(1, new FrameTrace());
Check(farHit == "Pliers" && steerReport.ItemSteers == 1 && steerReport.ForcedPickups == 0 &&
      steerReport[SyncReport.Area.Pickups].Differing == 0 && steerReport.Lines().Any(l => l.Contains("'Pliers' moved 40.0 cm")),
    "an item far off the ray is still put under it for the game's pickup: there is no distance limit");
SyncTracker.Stop();

// The game's call takes nothing (its ray stops on something the mod cannot
// see, the frame is paused, ...): the pickup is made right after the call.
PlacePliers(pliersAside);
pliersHand.SetActive(false);
var picksBeforeForce = steerInventory.PickupCalls;
var dropsBeforeForce = pick.CheckDropCalls;
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
Check(steerInventory.PickupCalls == picksBeforeForce + 1 && pick.CheckDropCalls == dropsBeforeForce + 1 &&
      pliersHand.activeSelf && !pliers.activeInHierarchy && steerReport.ForcedPickups == 1 && !pick.buttonClicked,
    "a recorded pickup the game's call still missed is forced after it: held item dropped, item in hand, world copy destroyed");
SyncTracker.ReplayIssued(1, new FrameTrace());
Check(steerReport[SyncReport.Area.Pickups].Differing == 1 && steerReport[SyncReport.Area.Pickups].WorstNote.Contains("forced [Pliers]") &&
      steerReport.Lines().Any(l => l.Contains("forced pickups") && l.Contains(" 1 ")),
    "a forced pickup is still reported as the miss it was, and counted as a correction");
SyncTracker.Stop();

PlacePliers(pliersAside);
pliersHand.SetActive(false);
pliersPoses = pliers.transform.SetPoseCalls;
Physics.HitAt = _ => "Drawer";
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var poseAfterFailedSteer = pliers.transform.localPosition;
var clickedAfterFailedSteer = pick.buttonClicked;
InteractionPin.After(pick);
Check(pliers.transform.SetPoseCalls == pliersPoses + 1 && Bits.Same(poseAfterFailedSteer, pliersLocal) && !clickedAfterFailedSteer &&
      steerReport.ItemSteers == 0 && steerReport.Lines().Any(l => l.Contains("still missed it")),
    "a move that still does not put the item under the ray is undone before the game casts, and no click goes to what it hits");
Check(steerReport.ForcedPickups == 1 && pliersHand.activeSelf && !pliers.activeInHierarchy,
    "an item that could not be steered under the ray is still picked up on its recorded frame");
SyncTracker.Stop();

PlacePliers(pliersAside);
pliersHand.SetActive(false);
var twoCastTrace = new FrameTrace();
twoCastTrace.Rays.Add(pickupTrace.Rays[0].Clone());
twoCastTrace.Rays.Add(pickupTrace.Rays[0].Clone());
twoCastTrace.Pickups.Add("Pliers");
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, twoCastTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
var forcedAfterFirstCast = steerReport.ForcedPickups;
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
Check(forcedAfterFirstCast == 0 && steerReport.ForcedPickups == 1,
    "a pickup is forced only after the recording's last pickup cast of that frame");
SyncTracker.Stop();

pliers.SetActive(false);
pliersHand.SetActive(false);
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
Check(pliersHand.activeSelf && steerReport.ForcedPickups == 1 && steerReport.Lines().Any(l => l.Contains("no copy was left in the world")),
    "a recorded item with no world copy left on replay is still put in hand, and the report says so");
SyncTracker.Stop();
var picksWhileHeld = steerInventory.PickupCalls;
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
Check(steerInventory.PickupCalls == picksWhileHeld && steerReport.ForceFailures == 1 && steerReport.Lines().Any(l => l.Contains("already in hand")),
    "an item already in hand with no world copy left is not taken a second time");
SyncTracker.Stop();

steerInventory.ItemDefs.Add(new Il2Cpp.ItemDefs { itemName = "Shotgun", handObject = pick.H_SG });
pick.H_SG.SetActive(false);
var shotgun = new GameObject { name = "Shotgun" };
shotgun.AddComponent<Il2Cpp.ItemSeedData>().itemName = "Shotgun";
shotgun.AddComponent<Collider>().Extents = new Vector3(.3f, .1f, .1f);
shotgun.transform.position = new Vector3(5f, 1f, 5f);
var shotgunTrace = new FrameTrace();
shotgunTrace.Rays.Add(pickupTrace.Rays[0].Clone());
shotgunTrace.Pickups.Add("Shotgun");
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, shotgunTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
InteractionPin.After(pick);
Check(pick.ShotgunCalls == 1 && pick.H_SG.activeSelf && !shotgun.activeInHierarchy && steerReport.ForcedPickups == 1,
    "a forced shotgun pickup runs the game's shotgun follow-up");
SyncTracker.Stop();

var syncMark = Physics.SyncCalls;
Physics.HitAt = _ => Physics.SyncCalls > syncMark ? "Pliers" : "Drawer";
PlacePliers(pliersAside);
pliersHand.SetActive(false);
pliersPoses = pliers.transform.SetPoseCalls;
steerReport = new SyncReport { TracesPresent = true };
SyncTracker.BeginReplay(steerReport);
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
GamePickup();
InteractionPin.After(pick);
Check(pliers.transform.SetPoseCalls == pliersPoses && steerReport.ItemSteers == 1 && steerReport.ForcedPickups == 0 &&
      steerReport.Lines().Any(l => l.Contains("syncing its collider")),
    "an item whose collider only lagged its transform is reached by a sync, without moving it");
SyncTracker.Stop();

Physics.HitAt = origin => Same(pliers.transform.position.x, origin.x) ? "Pliers" : "Drawer";
PlacePliers(pliersAside);
pliersHand.SetActive(false);
pliersPoses = pliers.transform.SetPoseCalls;
var picksMeasureOnly = steerInventory.PickupCalls;
InteractionPin.Enabled = false;
SyncTracker.BeginReplay(new SyncReport { TracesPresent = true });
SyncTracker.ReplayIssued(0, pickupTrace);
InteractionPin.Before(RaySample.Pick, pick, pick.Player, InteractionPin.PickDistance(pick), InteractionPin.PickReach(pick));
var clickedMeasureOnly = pick.buttonClicked;
InteractionPin.After(pick);
SyncTracker.Stop();
InteractionPin.Enabled = true;
Check(pliers.transform.SetPoseCalls == pliersPoses && !clickedMeasureOnly && steerInventory.PickupCalls == picksMeasureOnly,
    "measure-only replays never move items, click, or force pickups");
pliers.SetActive(false);
Physics.HitDistance = 0f;
Time.deltaTime = 0;

Physics.HitAt = _ => null;
VirtualInput.Active = false;

// ---- player rig ----------------------------------------------------------------------------
var rigPlayer = new Il2Cpp.MobileFPS();
var rigFall = rigPlayer.gameObject.AddComponent<Il2Cpp.FallingHolder>();
rigPlayer.FallingHolder = rigFall;
var rigCrouch = rigPlayer.gameObject.AddComponent<Il2Cpp.CrouchHolder>();
var bob = new Animation();
bob.States.Add(new AnimationState { name = "walk", enabled = true, weight = .75f, time = 1.2345f, speed = 1 });
bob.States.Add(new AnimationState { name = "idle", enabled = false, weight = .25f, time = .5f, speed = 1 });
rigPlayer.CameraAnim = bob;
var rigChild = new GameObject();
rigChild.transform.name = "Cam";
rigChild.transform.parent = rigPlayer.transform;
rigChild.transform.localPosition = new Vector3(0, 1.6f, .1f);
rigPlayer.IsCrouched = true;
rigFall.fallDuration = .125f;
rigPlayer.Controller.height = 1.1f;
rigPlayer.Controller.center = new Vector3(0, .55f, 0);
rigCrouch.IsCrouched = true;
var rig = PlayerRigSnapshot.Capture(rigPlayer);
var rigCopy = new PlayerRigSnapshot();
foreach (var line in rig.Encode())
{
    var eq = line.IndexOf('=');
    rigCopy.AddDecoded(line.Substring(0, eq), line.Substring(eq + 1));
}
bob.States[0].time = 9f;
bob.States[0].weight = 0f;
bob.States[1].enabled = true;
rigPlayer.IsCrouched = false;
rigFall.fallDuration = 0f;
rigPlayer.Controller.height = 2f;
rigPlayer.Controller.center = Vector3.zero;
rigChild.transform.localPosition = Vector3.zero;
rigCrouch.IsCrouched = false;
var rigNote = rigCopy.Restore(rigPlayer);
Check(Same(bob.States[0].time, 1.2345f) && Same(bob.States[0].weight, .75f) && !bob.States[1].enabled && bob.SampleCalls == 1 &&
      rigPlayer.IsCrouched && Same(rigFall.fallDuration, .125f) && Same(rigPlayer.Controller.height, 1.1f) &&
      Same(rigPlayer.Controller.center.y, .55f) && Same(rigChild.transform.localPosition.y, 1.6f) && rigCrouch.IsCrouched &&
      rigNote.Contains("2 animation states"),
    "player rig restores head-bob phase, local transforms, controller geometry and movement flags");
rigChild.transform.name = "Renamed";
rigChild.transform.localPosition = Vector3.zero;
rigCopy.Restore(rigPlayer);
Check(Same(rigChild.transform.localPosition.y, 0f), "a rig transform that is no longer the same object is left alone");

// The game reports finished clips as enabled with no weight. Put back as
// "enabled at time 0" they play out and write their end pose mid-replay (the
// crouch pair left every replay at height 1.8 with the camera a metre down).
var crouchAnim = new Animation();
crouchAnim.States.Add(new AnimationState { name = "PlayerHukarSig", enabled = true, weight = 0f, time = 0f, speed = 1 });
crouchAnim.States.Add(new AnimationState { name = "PlayerReserSig", enabled = true, weight = 0f, time = 0f, speed = 1 });
rigCrouch.Anim = crouchAnim;
var idleRig = PlayerRigSnapshot.Capture(rigPlayer);
crouchAnim.States[0].enabled = false;
crouchAnim.States[0].time = .1f;
crouchAnim.States[1].weight = .5f;
crouchAnim.States[1].time = .2f;
var samplesBefore = crouchAnim.SampleCalls;
var idleNote = idleRig.Restore(rigPlayer);
Check(!crouchAnim.States[0].enabled && Same(crouchAnim.States[0].time, .1f),
    "an animation state the recording had no weight on is not re-armed on replay");
Check(!crouchAnim.States[1].enabled && Same(crouchAnim.States[1].weight, 0f) && crouchAnim.SampleCalls == samplesBefore + 1,
    "a state shaping the replay's pose that the recording had no weight on is silenced");
Check(idleNote.Contains("idle left alone"), "the rig note says how many idle states were left alone");
rigCrouch.Anim = null;

// ---- phase alignment ------------------------------------------------------------------------
// A minimal Unity clock: the frame delta is captureDeltaTime * timeScale, the
// double clock advances by the unrounded product (the stricter of the ways an
// engine could accumulate it), and FixedUpdate runs whenever the fixed clock
// is a whole step behind.
int EngineFrame()
{
    Time.frameCount++;
    var real = 1f / 60f;
    Time.deltaTime = Time.captureDeltaTime > 0f ? Time.captureDeltaTime * Time.timeScale : real * Time.timeScale;
    Time.timeAsDouble += Time.captureDeltaTime > 0f ? (double)Time.captureDeltaTime * Time.timeScale : (double)real * Time.timeScale;
    var steps = 0;
    while (Time.fixedTimeAsDouble + Time.fixedDeltaTime <= Time.timeAsDouble)
    {
        Time.fixedTimeAsDouble += Time.fixedDeltaTime;
        steps++;
    }
    return steps;
}

// The mod's per-frame order (GrannyTasMod.OnUpdate / OnLateUpdate), minus hardware.
bool ModFrame(MacroEngine e, TimeController t)
{
    EngineFrame();
    var simulating = t.OnUpdate();
    if (e.Mode == MacroMode.Aligning) e.UpdateAligning(simulating);
    if (e.Mode == MacroMode.Aligning)
    {
        VirtualInput.Freeze();
        t.OnLateUpdate();
        return false;
    }
    if (simulating)
    {
        if (!e.TryDrivePlayback()) VirtualInput.AdvanceFromHardware();
        e.CaptureFrame();
    }
    else VirtualInput.Freeze();
    t.OnLateUpdate();
    return simulating;
}

var clockTime = new TimeController();
Time.timeScale = 1;
Time.captureDeltaTime = 0;
Time.fixedDeltaTime = .02f;
Time.timeAsDouble = 812.3456789;
Time.fixedTimeAsDouble = 812.34;
clockTime.Enable();
clockTime.SetTickRate(60);
clockTime.SetSpeed(.48f);
Check(clockTime.PhysicsRate == 50 && Same(Time.timeScale, .5f), "0.48x records under an exact half-speed engine clock");

clockTime.BeginAlignment();
Time.deltaTime = 1f / 60f;
Check(!clockTime.OnUpdate() && clockTime.FrameCount == 0, "pre-roll frames never count as macro frames");
clockTime.SetAlignmentDelta(.0123f);
Check(Same(Time.captureDeltaTime, .0123f) && Time.timeScale == 1, "pre-roll runs unscaled with exactly the requested delta");
clockTime.EndAlignment();
Check(Same(Time.timeScale, .5f) && !clockTime.Aligning, "ending pre-roll restores the running clock");

var e2e = new MacroEngine(new MelonLogger.Instance(), clockTime);
for (var i = 0; i < 3; i++) ModFrame(e2e, clockTime);
e2e.StartRecording();
var recordedFrames = 0;
for (var i = 0; i < 240; i++)
{
    if (i == 90) clockTime.SetPaused(true);
    if (i == 110) clockTime.StepFrames(7);
    if (i == 140) clockTime.SetPaused(false);
    if (ModFrame(e2e, clockTime)) recordedFrames++;
}
e2e.StopRecording();
// The stubs cannot resolve the scene hierarchy faithfully; keep this timing
// test focused on clock alignment rather than snapshot lookup.
e2e.Macro.HasSetupMetadata = false;
e2e.Macro.Snapshot.Entities.Clear();
Check(e2e.FrameCount == recordedFrames && e2e.Macro.HasTraces && e2e.Macro.Frames[0].Trace.FixedSteps == -1 &&
      e2e.Macro.Frames.Skip(1).All(f => f.Trace.HasClock && f.Trace.FixedSteps >= 0),
    "recording captures a clock trace for every macro frame, across pause and frame steps");

// Replay from a different point in the fixed step, as a real replay would be.
Time.timeAsDouble += 7.777777;
while (Time.fixedTimeAsDouble + Time.fixedDeltaTime <= Time.timeAsDouble) Time.fixedTimeAsDouble += Time.fixedDeltaTime;
e2e.StartPlayback();
Check(e2e.Mode == MacroMode.Aligning && clockTime.Aligning, "a traced macro starts with fixed-step pre-roll");
var preRoll = 0;
while (e2e.Mode == MacroMode.Aligning && preRoll < 20)
{
    ModFrame(e2e, clockTime);
    preRoll++;
}
Check(e2e.Mode == MacroMode.Playing && e2e.Playhead == 1 && preRoll <= 4 && Same(Time.timeScale, 1f),
    $"pre-roll finishes in {preRoll} engine frames and frame 0 issues on the frame after (mode={e2e.Mode}, playhead={e2e.Playhead}, status={e2e.PendingStatus})");
for (var i = 0; i < 400 && e2e.Mode == MacroMode.Playing; i++) ModFrame(e2e, clockTime);
var e2eReport = e2e.LastReport;
Check(e2eReport != null && e2eReport.FramesReplayed == recordedFrames &&
      e2eReport[SyncReport.Area.FixedSteps].Compared == recordedFrames - 1 &&
      e2eReport[SyncReport.Area.FixedSteps].Differing == 0 && e2eReport[SyncReport.Area.Clock].Differing == 0 &&
      e2eReport.StartPhaseError < 1e-10 && e2eReport.Exact,
    "a 0.48x recording with pauses replays at 1x with the identical FixedUpdate pattern on every frame");

// Negative control: the same replay started out of phase must be caught.
// Put frame 1 half a fixed step away from where the recording had it.
e2e.Macro.Frames[0].Trace.HasClock = false;
var recordedPhase1 = e2e.Macro.Frames[1].Trace.Phase;
Time.fixedTimeAsDouble = Time.timeAsDouble - (((recordedPhase1 + .01 - 2.0 / 60.0) % .02 + .02) % .02);
e2e.StartPlayback();
Check(e2e.Mode == MacroMode.Playing, "a macro without a frame-0 clock skips pre-roll");
for (var i = 0; i < 400 && e2e.Mode == MacroMode.Playing; i++) ModFrame(e2e, clockTime);
Check(e2e.LastReport[SyncReport.Area.FixedSteps].Differing > 0 && !e2e.LastReport.Exact,
    "an unaligned replay is caught by the fixed-step comparison");
e2e.DiscardRecording();
clockTime.Disable();
if (Directory.Exists(MelonLoader.Utils.MelonEnvironment.UserDataDirectory))
    Directory.Delete(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, true);

Console.WriteLine($"{passed} regression checks passed");
