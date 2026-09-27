using System.Collections.Generic;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// The complete input state for one simulated frame — the unit the macro
    /// recorder stores and the playback engine replays.
    /// </summary>
    public sealed class InputFrame
    {
        public readonly HashSet<KeyCode> Keys = new HashSet<KeyCode>();
        public readonly bool[] MouseButtons = new bool[3];
        public readonly Dictionary<string, float> Axes = new Dictionary<string, float>();
        public readonly Dictionary<string, float> RawAxes = new Dictionary<string, float>();
        public readonly List<PhysicsFrameState> PhysicsStates = new List<PhysicsFrameState>();
        public bool HasPhysicsState;

        // Optional absolute look state at the start of this simulated frame.
        public bool HasPose;
        public Quaternion PlayerRotation;
        public Quaternion CameraLocalRotation;
        public float RotationX;

        /// <summary>
        /// The player's world position at the start of this simulated frame.
        ///
        /// Recorded because replaying identical input does not reproduce an
        /// identical trajectory: `CharacterController.Move` resolves collisions
        /// in float arithmetic against an Update/FixedUpdate interleaving whose
        /// phase a recording's pauses do not preserve. Millimetres of drift are
        /// harmless for movement and decisive for interaction — `PickRay` casts
        /// its pickup ray from the player, so a grazing hit flips and the item
        /// is picked on a different frame or not at all.
        ///
        /// Kept separate from <see cref="HasPose"/> so a macro recorded before
        /// positions were stored still loads and still replays its look pose.
        /// </summary>
        public bool HasPosition;
        public Vector3 PlayerPosition;

        /// <summary>
        /// World state at this frame's boundary and the events after it, for the
        /// replay sync check (v5 macros). Null when not recorded.
        /// </summary>
        public FrameTrace Trace;

        /// <summary>
        /// Keyboard codes sampled every frame, excluding duplicate enum aliases
        /// and joystick codes. Game action rebinds must not disappear because
        /// their key was missing from a handwritten sampling list.
        /// </summary>
        public static readonly KeyCode[] Sampled = KeyboardKeys();

        private static KeyCode[] KeyboardKeys()
        {
            var keys = new HashSet<KeyCode>();
            foreach (KeyCode key in System.Enum.GetValues(typeof(KeyCode)))
                if (key > KeyCode.None && key < KeyCode.Mouse0) keys.Add(key);
            var sorted = new List<KeyCode>(keys);
            sorted.Sort();
            return sorted.ToArray();
        }

        /// <summary>
        /// Axes sampled every frame. "Mouse X"/"Mouse Y" are per-frame deltas
        /// rather than absolute positions, so they must be captured per
        /// simulated frame or look direction will not reproduce.
        /// </summary>
        public static readonly string[] SampledAxes =
        {
            "Mouse X", "Mouse Y", "Mouse ScrollWheel", "Horizontal", "Vertical",
        };

        public bool Has(KeyCode k) => k >= KeyCode.Mouse0 && k <= KeyCode.Mouse2
            ? Mouse((int)k - (int)KeyCode.Mouse0) : Keys.Contains(k);

        public float Axis(string name) => Axes.TryGetValue(name, out var v) ? v : 0f;
        public float AxisRaw(string name) => RawAxes.TryGetValue(name, out var v) ? v : Axis(name);

        public bool Mouse(int button) =>
            button >= 0 && button < MouseButtons.Length && MouseButtons[button];

        public void Clear()
        {
            Keys.Clear();
            Axes.Clear();
            RawAxes.Clear();
            PhysicsStates.Clear();
            HasPhysicsState = false;
            HasPose = false;
            PlayerRotation = Quaternion.identity;
            CameraLocalRotation = Quaternion.identity;
            RotationX = 0f;
            HasPosition = false;
            PlayerPosition = Vector3.zero;
            Trace = null;
            for (var i = 0; i < MouseButtons.Length; i++) MouseButtons[i] = false;
        }

        public void CopyFrom(InputFrame other)
        {
            Clear();
            foreach (var k in other.Keys) Keys.Add(k);
            foreach (var kv in other.Axes) Axes[kv.Key] = kv.Value;
            foreach (var kv in other.RawAxes) RawAxes[kv.Key] = kv.Value;
            for (var i = 0; i < MouseButtons.Length; i++) MouseButtons[i] = other.MouseButtons[i];
            HasPose = other.HasPose;
            PlayerRotation = other.PlayerRotation;
            CameraLocalRotation = other.CameraLocalRotation;
            RotationX = other.RotationX;
            HasPosition = other.HasPosition;
            PlayerPosition = other.PlayerPosition;
            HasPhysicsState = other.HasPhysicsState;
            foreach (var state in other.PhysicsStates)
            {
                var copy = new PhysicsFrameState();
                copy.CopyFrom(state);
                PhysicsStates.Add(copy);
            }
            Trace = other.Trace?.Clone();
        }
    }
}
