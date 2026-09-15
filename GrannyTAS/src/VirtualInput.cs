using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Sits between the game and <see cref="Input"/>.
    ///
    /// The game is served from a virtual input state that only advances on
    /// frames the simulation actually advances. Two things fall out of that:
    ///
    /// 1. **Pausing genuinely freezes the world.** Unity keeps calling
    ///    <c>Update</c> at <c>timeScale = 0</c> — only <c>deltaTime</c> goes to
    ///    zero — so without this the game still sees fresh
    ///    <c>Input.GetKeyDown</c> edges and fires interactions against a frozen
    ///    world (hiding in a bed while paused, for instance).
    ///
    /// 2. **Press/release edges land on simulated frames, not real ones.**
    ///    <c>GetKeyDown</c> is true for exactly one *simulated* frame.
    ///
    /// While paused the mod does not read your hands directly. Keys *latch* into
    /// <see cref="Pending"/> — tap W and it stays on until you tap it again — and
    /// the latched set is what the next stepped frame receives. Holding three
    /// physical keys steady while reaching for the step key is not something a
    /// person can do reliably, and frame-stepping is worthless if composing a
    /// frame's input is the hard part.
    ///
    /// The same layer is where recorded macros are injected on playback: swap
    /// the source of <see cref="Current"/> and nothing in the game can tell.
    ///
    /// Mouse look matters here more than it might look. <c>MobileFPS</c>'s look
    /// code applies the raw per-frame <c>GetAxis</c> delta with **no**
    /// <c>Time.deltaTime</c> scaling (confirmed by disassembly — see
    /// docs/ida-findings.md), so view direction is a function of the per-frame
    /// deltas themselves. Frozen frames apply look immediately and the next
    /// simulated frame records the resulting absolute camera/body pose.
    /// </summary>
    public static class VirtualInput
    {
        /// <summary>Patches are inert until this is on, so disarming restores stock behaviour exactly.</summary>
        public static bool Active { get; set; }

        /// <summary>
        /// Set while the mod reads hardware itself (sampling, and its own
        /// hotkeys). Without it those reads would be answered by the virtual
        /// state and the mod could never see a real key.
        /// </summary>
        public static bool Bypass { get; set; }

        /// <summary>
        /// Swallow mouse motion and clicks before they reach the game. Set while
        /// the TAS panel has the cursor: the panel is dragged and clicked with
        /// the same mouse the game reads for look and for shoot/interact, so
        /// without this, moving the window spins the camera and clicking a
        /// button fires the gun.
        ///
        /// Both the game's frame and the recorder's frame are zeroed, not just
        /// the game's — a rotation the macro records but the game never applied
        /// desyncs the replay exactly as badly as the reverse.
        /// </summary>
        public static bool SuppressMouse { get; set; }
        public static bool Frozen { get; private set; }

        /// <summary>
        /// Put the player back on the recorded position at the start of every
        /// replayed frame.
        ///
        /// Identical input does not by itself reproduce an identical trajectory.
        /// `CharacterController.Move` resolves collisions in float arithmetic
        /// against an Update/FixedUpdate interleaving whose phase a recording's
        /// pauses do not preserve, so a replay drifts by fractions of a
        /// millimetre. That is invisible in movement and decisive for
        /// interaction: `PickRay` casts its pickup ray from the player, so a
        /// grazing hit flips and the item is picked on a different frame — or
        /// on no frame at all, which is exactly the failure this addresses.
        ///
        /// Correcting toward the recorded position each frame makes the ray
        /// origin exact by construction instead of hoping the simulation stays
        /// convergent. <see cref="MaxPositionDrift"/> still reports how far it
        /// had to move, so a genuine desync is visible rather than papered over.
        /// </summary>
        public static bool PinPosition { get; set; } = true;

        /// <summary>Largest correction applied since the last reset, in metres.</summary>
        public static float MaxPositionDrift { get; private set; }

        /// <summary>Frame-relative count of corrections that exceeded the deadband.</summary>
        public static int PositionCorrections { get; private set; }

        /// <summary>
        /// Below this, leave the position alone. Rewriting a
        /// <c>CharacterController</c>'s position every frame for a difference
        /// smaller than the collision skin buys nothing and risks disturbing
        /// the very resolution it is meant to preserve.
        /// </summary>
        private const float PositionDeadband = 1e-6f;

        /// <summary>What the game currently sees.</summary>
        public static readonly InputFrame Current = new InputFrame();

        /// <summary>What it saw on the previous simulated frame; edges come from the pair.</summary>
        public static readonly InputFrame Previous = new InputFrame();

        /// <summary>
        /// What the recorder stores for this frame. Kept identical to the
        /// input delivered to the game, including its pre-frame look pose.
        /// </summary>
        public static readonly InputFrame ForRecord = new InputFrame();

        /// <summary>
        /// The latched buffer composed while paused, delivered on the next
        /// stepped frame. Wheel input accumulates; look is applied live instead.
        /// </summary>
        public static readonly InputFrame Pending = new InputFrame();

        // Hardware state, tracked every real frame so the buffer can latch on
        // real key presses independently of the virtual edges the game sees.
        private static readonly InputFrame RealNow = new InputFrame();
        private static readonly InputFrame RealPrev = new InputFrame();

        private static readonly InputFrame Scratch = new InputFrame();
        private static bool _previewingLook;
        private static bool _restoreCurrentPoseInPlayerUpdate;

        /// <summary>Call once per real frame, before anything else.</summary>
        public static void PollHardware()
        {
            RealPrev.CopyFrom(RealNow);
            Sample(RealNow);

            // Suppression is applied here, at the one point hardware enters the
            // mod, rather than at each of the places that consume it. Everything
            // downstream — the latched buffer, the live look passthrough, the
            // free-running advance — reads RealNow, so they all inherit it and
            // none of them can be the one that was forgotten.
            if (SuppressMouse)
            {
                RealNow.Axes["Mouse X"] = 0f;
                RealNow.Axes["Mouse Y"] = 0f;
                RealNow.Axes["Mouse ScrollWheel"] = 0f;
                RealNow.RawAxes["Mouse X"] = 0f;
                RealNow.RawAxes["Mouse Y"] = 0f;
                RealNow.RawAxes["Mouse ScrollWheel"] = 0f;
                for (var i = 0; i < 3; i++) RealNow.MouseButtons[i] = false;
            }
        }

        private static bool RealDown(KeyCode k) => RealNow.Has(k) && !RealPrev.Has(k);
        private static bool RealMouseDown(int b) => RealNow.Mouse(b) && !RealPrev.Mouse(b);

        /// <summary>
        /// Fold this real frame's presses into the latched buffer. Called on
        /// paused frames only — the frames where the player is composing input
        /// rather than spending it.
        /// </summary>
        public static void AccumulateBuffer()
        {
            foreach (var k in InputFrame.Sampled)
                if (RealDown(k))
                {
                    if (!Pending.Keys.Remove(k)) Pending.Keys.Add(k);   // latch toggle
                }

            for (var i = 0; i < 3; i++)
                if (RealMouseDown(i)) Pending.MouseButtons[i] = !Pending.MouseButtons[i];

            var scroll = Pending.Axis("Mouse ScrollWheel") + RealNow.Axis("Mouse ScrollWheel");
            var rawScroll = Pending.AxisRaw("Mouse ScrollWheel") + RealNow.AxisRaw("Mouse ScrollWheel");
            Pending.Axes["Mouse ScrollWheel"] = scroll;
            Pending.RawAxes["Mouse ScrollWheel"] = rawScroll;
        }

        public static void ClearBuffer()
        {
            Pending.Clear();
        }

        /// <summary>Seed the buffer from what is physically held, so pausing mid-stride keeps the stride.</summary>
        public static void SeedBufferFromHardware()
        {
            Pending.CopyFrom(RealNow);
            // The same physical press must not immediately toggle the seed off.
            RealPrev.CopyFrom(RealNow);
            Pending.Axes["Mouse X"] = 0f;
            Pending.Axes["Mouse Y"] = 0f;
            Pending.Axes["Mouse ScrollWheel"] = 0f;
            Pending.RawAxes["Mouse X"] = 0f;
            Pending.RawAxes["Mouse Y"] = 0f;
            Pending.RawAxes["Mouse ScrollWheel"] = 0f;
        }

        /// <summary>
        /// Keep all gameplay input inactive between simulated frames.
        /// The previous simulated frame remains available for edge detection.
        /// Look can still change through <see cref="PreviewPausedLook"/>, which
        /// invokes only the camera routine and leaves gameplay frozen.
        /// </summary>
        public static void Freeze()
        {
            Frozen = true;
        }

        /// <summary>Advance one simulated frame using live hardware (free-running play).</summary>
        public static void AdvanceFromHardware()
        {
            _restoreCurrentPoseInPlayerUpdate = false;
            Frozen = false;
            Previous.CopyFrom(Current);
            Current.CopyFrom(RealNow);
            // Buffered wheel input belongs to the first resumed frame too.
            foreach (var axis in new[] { "Mouse ScrollWheel" })
            {
                Current.Axes[axis] = Current.Axis(axis) + Pending.Axis(axis);
                Current.RawAxes[axis] = Current.AxisRaw(axis) + Pending.AxisRaw(axis);
                Pending.Axes[axis] = 0f;
                Pending.RawAxes[axis] = 0f;
            }
            CapturePose(Current);
            ForRecord.CopyFrom(Current);
        }

        /// <summary>
        /// Advance one simulated frame from the latched buffer.
        /// </summary>
        public static void AdvanceFromBuffer()
        {
            _restoreCurrentPoseInPlayerUpdate = false;
            Frozen = false;
            Previous.CopyFrom(Current);

            Current.CopyFrom(Pending);
            SynthesizeMovementAxes(Current);

            // Look is previewed immediately while paused, so old mouse motion
            // never enters Pending. A step still receives motion made on this
            // real frame, after its already-previewed starting pose is captured.
            Current.Axes["Mouse X"] = RealNow.Axis("Mouse X");
            Current.Axes["Mouse Y"] = RealNow.Axis("Mouse Y");
            Current.RawAxes["Mouse X"] = RealNow.AxisRaw("Mouse X");
            Current.RawAxes["Mouse Y"] = RealNow.AxisRaw("Mouse Y");
            CapturePose(Current);
            // The game and recorder receive the same frame and starting pose.
            ForRecord.CopyFrom(Current);

            // Deltas are consumed by the frame that receives them; leaving them
            // latched would re-apply the same rotation on every subsequent step.
            Pending.Axes["Mouse X"] = 0f;
            Pending.Axes["Mouse Y"] = 0f;
            Pending.Axes["Mouse ScrollWheel"] = 0f;
            Pending.RawAxes["Mouse ScrollWheel"] = 0f;
        }

        /// <summary>Advance one simulated frame from a recorded macro frame.</summary>
        public static void AdvanceFrom(InputFrame source)
        {
            Frozen = false;
            Previous.CopyFrom(Current);
            Current.CopyFrom(source);
            ForRecord.CopyFrom(source);
            _restoreCurrentPoseInPlayerUpdate = Current.HasPose;
            ApplyPose(Current);
        }

        /// <summary>
        /// Derive Horizontal/Vertical from latched movement keys. Unity's Input
        /// Manager builds those axes from real hardware, so a buffered W would
        /// otherwise leave them at zero and the player would not move.
        /// </summary>
        private static void SynthesizeMovementAxes(InputFrame f)
        {
            var right = f.Has(KeyCode.D) || f.Has(KeyCode.RightArrow);
            var left = f.Has(KeyCode.A) || f.Has(KeyCode.LeftArrow);
            var fwd = f.Has(KeyCode.W) || f.Has(KeyCode.UpArrow);
            var back = f.Has(KeyCode.S) || f.Has(KeyCode.DownArrow);

            f.Axes["Horizontal"] = (right ? 1f : 0f) - (left ? 1f : 0f);
            f.Axes["Vertical"] = (fwd ? 1f : 0f) - (back ? 1f : 0f);
            f.RawAxes["Horizontal"] = f.Axes["Horizontal"];
            f.RawAxes["Vertical"] = f.Axes["Vertical"];
        }

        /// <summary>Read real hardware into <paramref name="into"/>.</summary>
        public static void Sample(InputFrame into)
        {
            var wasBypassing = Bypass;
            Bypass = true;
            try
            {
                into.Clear();

                foreach (var k in InputFrame.Sampled)
                    if (!Keybinds.IsReserved(k) && Input.GetKey(k)) into.Keys.Add(k);

                for (var i = 0; i < 3; i++)
                    into.MouseButtons[i] = !Keybinds.IsReserved((KeyCode)((int)KeyCode.Mouse0 + i)) && Input.GetMouseButton(i);

                foreach (var axis in InputFrame.SampledAxes)
                {
                    into.Axes[axis] = Input.GetAxis(axis);
                    into.RawAxes[axis] = Input.GetAxisRaw(axis);
                }
            }
            finally
            {
                Bypass = wasBypassing;
            }
        }

        /// <summary>Seed both virtual frames from hardware, so arming creates no spurious edge.</summary>
        public static void SeedFromHardware()
        {
            Frozen = false;
            Sample(Scratch);
            Current.CopyFrom(Scratch);
            Previous.CopyFrom(Scratch);
            RealNow.CopyFrom(Scratch);
            RealPrev.CopyFrom(Scratch);
        }

        /// <summary>True when the patches should answer instead of the engine.</summary>
        internal static bool Intercept => Active && !Bypass;

        /// <summary>
        /// The game's pause menu must receive live mouse clicks so its resume
        /// button remains usable. Panel mouse suppression still takes priority
        /// when the TAS window owns the pointer.
        /// </summary>
        internal static bool PassMouseToGame => PlayerGate.GamePaused && !SuppressMouse;

        /// <summary>Apply hardware look on a frozen frame without running gameplay Update.</summary>
        public static void PreviewPausedLook()
        {
            if (!Active || !Frozen || SuppressMouse) return;
            var player = PlayerGate.Player;
            if (player == null || !player.enabled || !player.gameObject.activeInHierarchy ||
                !player.isAllowedToMove || !player.AbleToMove || !player.CamK ||
                player.playerCamera == null) return;
            try { if (Paused.IsPaused || Time.deltaTime != 0f) return; }
            catch { return; }

            _previewingLook = true;
            try { player.GetTouchInput(); }
            finally { _previewingLook = false; }
        }

        public static void CapturePose(InputFrame frame)
        {
            var player = PlayerGate.Player;
            if (frame == null || player == null || player.playerCamera == null) return;
            frame.HasPose = true;
            frame.PlayerRotation = player.transform.rotation;
            frame.CameraLocalRotation = player.playerCamera.localRotation;
            frame.RotationX = player.rotationX;
            frame.PlayerPosition = player.transform.position;
            frame.HasPosition = true;
        }

        /// <summary>Restore a recorded pose immediately at the frame issuance boundary.</summary>
        public static void ApplyPose(InputFrame frame)
        {
            var player = PlayerGate.Player;
            if (frame == null || !frame.HasPose || player == null || player.playerCamera == null) return;
            player.transform.rotation = frame.PlayerRotation;
            player.playerCamera.localRotation = frame.CameraLocalRotation;
            player.rotationX = frame.RotationX;
            ApplyPosition(player, frame);
        }

        /// <summary>
        /// Move the player back onto the recorded position, measuring the drift
        /// on the way. Measurement happens whether or not the correction is
        /// enabled: knowing how far a replay wandered is worth having even when
        /// the run is left to wander.
        /// </summary>
        private static void ApplyPosition(MobileFPS player, InputFrame frame)
        {
            if (!frame.HasPosition) return;

            var transform = player.transform;
            var drift = (transform.position - frame.PlayerPosition).magnitude;
            if (!float.IsFinite(drift)) return;
            if (drift > MaxPositionDrift) MaxPositionDrift = drift;
            if (!PinPosition || drift <= PositionDeadband) return;

            PositionCorrections++;

            // A CharacterController resolves collisions against the position it
            // already believes it has, so a write underneath it is partly
            // undone. Disabling it first makes the write stick — the same
            // mechanic WorldSnapshot.RestorePlayer relies on.
            CharacterController cc = null;
            try { cc = player.GetComponent<CharacterController>(); } catch { }

            var wasEnabled = false;
            if (cc != null) { wasEnabled = cc.enabled; cc.enabled = false; }
            try { transform.position = frame.PlayerPosition; }
            finally { if (cc != null) cc.enabled = wasEnabled; }
        }

        /// <summary>Start a replay's drift measurement from nothing.</summary>
        public static void ResetDrift()
        {
            MaxPositionDrift = 0f;
            PositionCorrections = 0;
        }

        internal static void ApplyCurrentPose(MobileFPS player)
        {
            if (!Active || Frozen || Time.deltaTime <= 0f || !_restoreCurrentPoseInPlayerUpdate ||
                player == null || player != PlayerGate.Player || !Current.HasPose || player.playerCamera == null) return;
            ApplyPose(Current);
            _restoreCurrentPoseInPlayerUpdate = false;
        }

        private static bool IsLookAxis(string name) => name == "Mouse X" || name == "Mouse Y";

        internal static float Axis(string name) =>
            _previewingLook && IsLookAxis(name) ? RealNow.Axis(name) : (Frozen ? 0f : Current.Axis(name));

        internal static float AxisRaw(string name) =>
            _previewingLook && IsLookAxis(name) ? RealNow.AxisRaw(name) : (Frozen ? 0f : Current.AxisRaw(name));

        public static void ResetForPlayback(InputFrame initial)
        {
            ResetDrift();
            _restoreCurrentPoseInPlayerUpdate = false;
            Current.CopyFrom(initial);
            Previous.CopyFrom(initial);
            ForRecord.CopyFrom(initial);
            ClearBuffer();
            Frozen = false;
        }

        internal static bool Down(KeyCode k) => !Frozen && Current.Has(k) && !Previous.Has(k);
        internal static bool Up(KeyCode k) => !Frozen && !Current.Has(k) && Previous.Has(k);
        internal static bool MouseDown(int b) => !Frozen && Current.Mouse(b) && !Previous.Mouse(b);
        internal static bool MouseUp(int b) => !Frozen && !Current.Mouse(b) && Previous.Mouse(b);
    }

    // ---- Harmony patches -------------------------------------------------
    // Each returns false to suppress the original and answer from the virtual
    // state. Returning true when not intercepting means an unarmed mod costs
    // nothing but the branch.

    [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(KeyCode))]
    internal static class Patch_GetKey
    {
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (key == KeyCode.Escape && VirtualInput.Frozen) return true;
            if (!VirtualInput.Intercept) return true;
            __result = !VirtualInput.Frozen && VirtualInput.Current.Has(key);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
    internal static class Patch_GetKeyDown
    {
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (key == KeyCode.Escape && VirtualInput.Frozen) return true;
            if (!VirtualInput.Intercept) return true;
            __result = VirtualInput.Down(key);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(KeyCode))]
    internal static class Patch_GetKeyUp
    {
        private static bool Prefix(KeyCode key, ref bool __result)
        {
            if (key == KeyCode.Escape && VirtualInput.Frozen) return true;
            if (!VirtualInput.Intercept) return true;
            __result = VirtualInput.Up(key);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton))]
    internal static class Patch_GetMouseButton
    {
        private static bool Prefix(int button, ref bool __result)
        {
            if (VirtualInput.PassMouseToGame) return true;
            if (!VirtualInput.Intercept) return true;
            __result = !VirtualInput.Frozen && VirtualInput.Current.Mouse(button);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown))]
    internal static class Patch_GetMouseButtonDown
    {
        private static bool Prefix(int button, ref bool __result)
        {
            if (VirtualInput.PassMouseToGame) return true;
            if (!VirtualInput.Intercept) return true;
            __result = VirtualInput.MouseDown(button);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp))]
    internal static class Patch_GetMouseButtonUp
    {
        private static bool Prefix(int button, ref bool __result)
        {
            if (VirtualInput.PassMouseToGame) return true;
            if (!VirtualInput.Intercept) return true;
            __result = VirtualInput.MouseUp(button);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
    internal static class Patch_GetAxis
    {
        private static bool Prefix(string axisName, ref float __result)
        {
            if (!VirtualInput.Intercept) return true;
            __result = VirtualInput.Axis(axisName);
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
    internal static class Patch_GetAxisRaw
    {
        private static bool Prefix(string axisName, ref float __result)
        {
            if (!VirtualInput.Intercept) return true;
            __result = VirtualInput.AxisRaw(axisName);
            return false;
        }
    }

    [HarmonyPatch(typeof(MobileFPS), nameof(MobileFPS.Update))]
    internal static class Patch_MobileFPS_Update_Pose
    {
        private static void Prefix(MobileFPS __instance) => VirtualInput.ApplyCurrentPose(__instance);
    }
}
