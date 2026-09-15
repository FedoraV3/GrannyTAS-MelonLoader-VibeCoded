using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>A rigidbody checkpoint sampled at a simulated-frame boundary.</summary>
    public sealed class PhysicsFrameState
    {
        public const float PositionDeadband = 1e-6f;

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
        public static int CorrectionCount { get; private set; }

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
        /// Applies a complete checkpoint. All expected bodies are resolved
        /// before any are changed, so a missing object cannot leave a partially
        /// corrected world behind.
        /// </summary>
        public static bool TryApply(IReadOnlyList<PhysicsFrameState> expected, out string error)
        {
            error = null;
            if (expected == null) return true;

            var active = EnumerateActive();
            var resolved = new Rigidbody[expected.Count];
            for (var i = 0; i < expected.Count; i++)
            {
                var state = expected[i];
                if (state == null || string.IsNullOrEmpty(state.Identity))
                {
                    error = $"invalid rigidbody checkpoint at index {i}";
                    return false;
                }
                if (!active.TryGetValue(state.Identity, out resolved[i]))
                {
                    error = $"missing rigidbody '{state.Identity}'";
                    return false;
                }
            }

            for (var i = 0; i < expected.Count; i++) Apply(resolved[i], expected[i]);
            return true;
        }

        public static string IdentityFor(Rigidbody body)
        {
            if (body == null || body.transform == null || body.gameObject == null)
                throw new InvalidOperationException("Cannot identify a detached Rigidbody.");

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

        private static SortedDictionary<string, Rigidbody> EnumerateActive()
        {
            var result = new SortedDictionary<string, Rigidbody>(StringComparer.Ordinal);
            foreach (var body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (body == null || body.gameObject == null || !body.gameObject.activeInHierarchy) continue;
                var identity = IdentityFor(body);
                if (result.ContainsKey(identity))
                    throw new InvalidOperationException($"Duplicate rigidbody identity '{identity}'.");
                result.Add(identity, body);
            }
            return result;
        }

        private static void Apply(Rigidbody body, PhysicsFrameState state)
        {
            var drift = (body.position - state.Position).magnitude;
            if (float.IsFinite(drift) && drift > MaxPositionDrift) MaxPositionDrift = drift;

            body.constraints = (RigidbodyConstraints)state.Constraints;
            body.detectCollisions = state.DetectCollisions;
            body.useGravity = state.UseGravity;
            body.isKinematic = state.IsKinematic;

            if (!float.IsFinite(drift) || drift > PositionDeadband)
            {
                body.position = state.Position;
                CorrectionCount++;
            }
            body.rotation = state.Rotation;

            // Unity rejects velocity writes on kinematic bodies. Their stored
            // values still round-trip, but pose and flags are the valid state
            // that can be restored through Rigidbody's public API.
            if (!state.IsKinematic)
            {
                body.velocity = state.Velocity;
                body.angularVelocity = state.AngularVelocity;
            }

            if (state.Sleeping) body.Sleep();
            else body.WakeUp();
        }
    }
}
