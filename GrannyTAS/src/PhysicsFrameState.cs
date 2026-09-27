using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyTAS
{
    /// <summary>A rigidbody checkpoint sampled at a simulated-frame boundary.</summary>
    public sealed class PhysicsFrameState
    {
        public string Identity = "";
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public bool IsKinematic;
        public bool UseGravity;
        public bool DetectCollisions;
        public int Constraints;
        public bool Sleeping;

        public static float MaxPositionDrift { get; private set; }

        /// <summary>Body-frames that differed from the checkpoint and were corrected.</summary>
        public static int CorrectionCount { get; private set; }

        // Results of the most recent TryApply, for the sync report.
        public static int LastMissing { get; private set; }
        public static string LastFirstMissing { get; private set; } = "";
        public static int LastDiffering { get; private set; }
        public static float LastWorstPosition { get; private set; }
        public static string LastWorstNote { get; private set; } = "";

        /// <summary>Where one-off warnings go (duplicate identities); set by the mod.</summary>
        public static Action<string> Warn;
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        public void CopyFrom(PhysicsFrameState other)
        {
            Identity = other.Identity;
            Position = other.Position;
            Rotation = other.Rotation;
            Velocity = other.Velocity;
            AngularVelocity = other.AngularVelocity;
            IsKinematic = other.IsKinematic;
            UseGravity = other.UseGravity;
            DetectCollisions = other.DetectCollisions;
            Constraints = other.Constraints;
            Sleeping = other.Sleeping;
        }

        public static void ResetDrift()
        {
            MaxPositionDrift = 0f;
            CorrectionCount = 0;
            LastMissing = 0;
            LastFirstMissing = "";
            LastDiffering = 0;
            LastWorstPosition = 0f;
            LastWorstNote = "";
        }

        public static void CaptureInto(List<PhysicsFrameState> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();

            foreach (var pair in EnumerateActive())
            {
                var body = pair.Value;
                destination.Add(new PhysicsFrameState
                {
                    Identity = pair.Key,
                    Position = body.position,
                    Rotation = body.rotation,
                    Velocity = body.velocity,
                    AngularVelocity = body.angularVelocity,
                    IsKinematic = body.isKinematic,
                    UseGravity = body.useGravity,
                    DetectCollisions = body.detectCollisions,
                    Constraints = (int)body.constraints,
                    Sleeping = body.IsSleeping(),
                });
            }
        }

        /// <summary>
        /// Compare every checkpointed body with the live one and correct those
        /// that differ, bit for bit. A body that already matches is not touched
        /// at all — writing an identical velocity still wakes a sleeping body
        /// and resets its sleep timer, which is a divergence of its own.
        ///
        /// A recorded body that no longer exists (an item picked up on the
        /// replay but not in the recording) is counted in
        /// <see cref="LastMissing"/> and skipped; the rest are still corrected.
        /// It used to abort the replay, which turned one disagreement into zero
        /// accuracy for everything after it. The sync report carries it instead.
        ///
        /// Returns false only for a malformed checkpoint.
        /// </summary>
        public static bool TryApply(IReadOnlyList<PhysicsFrameState> expected, out string error)
        {
            error = null;
            LastMissing = 0;
            LastFirstMissing = "";
            LastDiffering = 0;
            LastWorstPosition = 0f;
            LastWorstNote = "";
            if (expected == null) return true;

            for (var i = 0; i < expected.Count; i++)
            {
                var state = expected[i];
                if (state == null || string.IsNullOrEmpty(state.Identity))
                {
                    error = $"invalid rigidbody checkpoint at index {i}";
                    return false;
                }
            }

            var active = EnumerateActive();
            Dictionary<string, Rigidbody> legacy = null;
            for (var i = 0; i < expected.Count; i++)
            {
                var state = expected[i];
                if (!active.TryGetValue(state.Identity, out var body) && IsLegacyIdentity(state.Identity))
                {
                    legacy ??= LegacyIndex(active);
                    legacy.TryGetValue(state.Identity, out body);
                }
                if (body == null)
                {
                    if (LastMissing++ == 0) LastFirstMissing = state.Identity;
                    continue;
                }
                Apply(body, state);
            }
            return true;
        }

        /// <summary>
        /// <c>scene:siblingIndices#componentOrdinal</c>. The scene prefix is
        /// what keeps two loaded scenes — the level and DontDestroyOnLoad, most
        /// often — from producing the same path: without it a recording in such
        /// a scene set failed on its first frame with a duplicate identity.
        /// </summary>
        public static string IdentityFor(Rigidbody body) => IdentityFor(body, null);

        private static string IdentityFor(Rigidbody body, Dictionary<int, string> sceneKeys)
        {
            if (body == null || body.transform == null || body.gameObject == null)
                throw new InvalidOperationException("Cannot identify a detached Rigidbody.");

            return SceneKey(body.gameObject, sceneKeys) + ":" + LocalIdentity(body);
        }

        /// <summary>The pre-scene identity format, still used to resolve v4 macros.</summary>
        private static string LocalIdentity(Rigidbody body)
        {
            var indices = new List<int>();
            for (var transform = body.transform; transform != null; transform = transform.parent)
                indices.Add(transform.GetSiblingIndex());
            indices.Reverse();

            var components = body.gameObject.GetComponents<Rigidbody>();
            var ordinal = -1;
            for (var i = 0; i < components.Length; i++)
                if (components[i] == body) { ordinal = i; break; }
            if (ordinal < 0) throw new InvalidOperationException("Rigidbody is absent from its GameObject component list.");

            return string.Join("/", indices) + "#" + ordinal;
        }

        private static bool IsLegacyIdentity(string identity) => identity.IndexOf(':') < 0;

        private static string SceneKey(GameObject go, Dictionary<int, string> cache)
        {
            Scene scene;
            try { scene = go.scene; }
            catch { return "?"; }

            var handle = 0;
            try { handle = scene.handle; } catch { }
            if (cache != null && cache.TryGetValue(handle, out var cached)) return cached;

            string name;
            try { name = scene.name ?? ""; }
            catch { name = ""; }

            // Two loaded scenes can share a name; number the later ones in load order.
            var ordinal = 0;
            try
            {
                var count = SceneManager.sceneCount;
                for (var i = 0; i < count; i++)
                {
                    var other = SceneManager.GetSceneAt(i);
                    if (other.handle == handle) break;
                    if ((other.name ?? "") == name) ordinal++;
                }
            }
            catch { }

            var key = ordinal == 0 ? name : name + "@" + ordinal;
            if (cache != null) cache[handle] = key;
            return key;
        }

        private static SortedDictionary<string, Rigidbody> EnumerateActive()
        {
            var result = new SortedDictionary<string, Rigidbody>(StringComparer.Ordinal);
            var sceneKeys = new Dictionary<int, string>();
            foreach (var body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (body == null || body.gameObject == null || !body.gameObject.activeInHierarchy) continue;
                var identity = IdentityFor(body, sceneKeys);
                if (result.ContainsKey(identity))
                {
                    // Cannot happen within one scene; keep the first rather than
                    // abandon the whole checkpoint, and say so once.
                    if (Warned.Add(identity))
                        Warn?.Invoke($"Two rigidbodies share the identity '{identity}'; only the first is checkpointed.");
                    continue;
                }
                result.Add(identity, body);
            }
            return result;
        }

        /// <summary>Legacy identity → body, with ambiguous identities mapped to null.</summary>
        private static Dictionary<string, Rigidbody> LegacyIndex(SortedDictionary<string, Rigidbody> active)
        {
            var index = new Dictionary<string, Rigidbody>(StringComparer.Ordinal);
            foreach (var body in active.Values)
            {
                string local;
                try { local = LocalIdentity(body); } catch { continue; }
                index[local] = index.ContainsKey(local) ? null : body;
            }
            return index;
        }

        private static void Apply(Rigidbody body, PhysicsFrameState state)
        {
            var position = body.position;
            var rotation = body.rotation;
            var velocity = body.velocity;
            var angular = body.angularVelocity;

            var drift = Bits.Distance(position, state.Position);
            if (float.IsFinite(drift) && drift > MaxPositionDrift) MaxPositionDrift = drift;

            var flagsSame = body.isKinematic == state.IsKinematic && body.useGravity == state.UseGravity &&
                            body.detectCollisions == state.DetectCollisions && (int)body.constraints == state.Constraints;
            var motionSame = state.IsKinematic ||
                             (Bits.Same(velocity, state.Velocity) && Bits.Same(angular, state.AngularVelocity));
            if (flagsSame && motionSame && Bits.Same(position, state.Position) && Bits.Same(rotation, state.Rotation) &&
                body.IsSleeping() == state.Sleeping)
                return;

            LastDiffering++;
            if (!float.IsFinite(drift) || drift >= LastWorstPosition)
            {
                LastWorstPosition = float.IsFinite(drift) ? drift : float.MaxValue;
                LastWorstNote = $"{state.Identity} off by {drift * 1000f:0.######} mm, " +
                                $"velocity off by {Bits.Distance(velocity, state.Velocity):0.######} m/s";
            }

            if (!flagsSame)
            {
                body.constraints = (RigidbodyConstraints)state.Constraints;
                body.detectCollisions = state.DetectCollisions;
                body.useGravity = state.UseGravity;
                body.isKinematic = state.IsKinematic;
            }
            if (!Bits.Same(position, state.Position)) body.position = state.Position;
            if (!Bits.Same(rotation, state.Rotation)) body.rotation = state.Rotation;

            // Unity rejects velocity writes on kinematic bodies. Their stored
            // values still round-trip, but pose and flags are the valid state
            // that can be restored through Rigidbody's public API.
            if (!state.IsKinematic)
            {
                if (!Bits.Same(velocity, state.Velocity)) body.velocity = state.Velocity;
                if (!Bits.Same(angular, state.AngularVelocity)) body.angularVelocity = state.AngularVelocity;
            }

            // Checked after the writes: a velocity write wakes the body.
            if (state.Sleeping && !body.IsSleeping()) body.Sleep();
            else if (!state.Sleeping && body.IsSleeping()) body.WakeUp();

            CorrectionCount++;
        }
    }
}
