using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Makes the pickup and door rays of a replay the rays of the recording.
    ///
    /// A pickup succeeds on the frame <c>PickRay.Update</c> casts a ray from
    /// its own transform and hits the item, with the interact edge and the fall
    /// flags agreeing (see docs/ida-pickup-findings.md). The input edge already
    /// replays exactly. The ray does not have to: it starts at the camera, and
    /// the camera sits on top of the player's position, the head-bob animation,
    /// crouch height and landing smoothing — any of which can be a fraction of
    /// a millimetre off on replay, which is all a grazing ray needs to miss.
    ///
    /// So the ray's world pose and the two fall flags are recorded at the
    /// moment <c>PickRay.Update</c> starts, and on replay the same moment puts
    /// them back for the duration of that one call — then restores what was
    /// there, so nothing but the raycast ever sees the pin. Nothing is written
    /// when the replay already matches bit for bit, which is the normal case;
    /// every pin that did happen is counted in the <see cref="SyncReport"/>, so
    /// a replay that needed steering says so.
    /// </summary>
    internal static class InteractionPin
    {
        /// <summary>Pin replayed rays to the recorded pose. Off: measure only.</summary>
        public static bool Enabled { get; set; } = true;

        private static Transform _moved;
        private static Vector3 _savedLocalPosition;
        private static Quaternion _savedLocalRotation;
        private static Vector3 _pinnedLocalPosition;
        private static Quaternion _pinnedLocalRotation;

        private static FallingHolder _pinnedFall;
        private static bool _savedFalling, _savedLanding, _pinnedFalling, _pinnedLanding;

        internal static void Before(int kind, Component ray, GameObject player, float distance)
        {
            // A postfix that never ran must not leave a pin behind for the next call.
            Release();

            // Same condition as GameplayFreeze: the original only runs on a
            // frame the simulation advances.
            if (!VirtualInput.Active || Time.deltaTime <= 0f || !SyncTracker.Active || ray == null) return;

            Transform t;
            var actual = new RaySample { Kind = kind, Order = SyncTracker.OrderTag };
            try
            {
                t = ray.transform;
                if (t == null) return;
                actual.Position = t.position;
                actual.Rotation = t.rotation;
            }
            catch { return; }

            var fall = kind == RaySample.Pick ? FindFall(player) : null;
            if (fall != null)
            {
                try
                {
                    actual.Falling = fall.isFalling;
                    actual.Landing = fall.isLanding;
                    actual.HasFall = true;
                }
                catch { fall = null; }
            }
            actual.Hit = HitName(t, distance);

            if (SyncTracker.Mode == SyncTracker.TrackMode.Recording)
            {
                SyncTracker.RecordRay(actual);
                return;
            }

            var expected = SyncTracker.NextExpectedRay(kind);
            string effectiveHit = null;
            if (expected != null && Enabled)
            {
                if (!Bits.Same(actual.Position, expected.Position) || !Bits.Same(actual.Rotation, expected.Rotation))
                {
                    try
                    {
                        _savedLocalPosition = t.localPosition;
                        _savedLocalRotation = t.localRotation;
                        t.SetPositionAndRotation(expected.Position, expected.Rotation);
                        _pinnedLocalPosition = t.localPosition;
                        _pinnedLocalRotation = t.localRotation;
                        _moved = t;
                        SyncTracker.Report?.CountRayPin();
                        effectiveHit = HitName(t, distance);
                    }
                    catch { Release(); }
                }

                if (fall != null && expected.HasFall &&
                    (actual.Falling != expected.Falling || actual.Landing != expected.Landing))
                {
                    try
                    {
                        _savedFalling = actual.Falling;
                        _savedLanding = actual.Landing;
                        fall.isFalling = expected.Falling;
                        fall.isLanding = expected.Landing;
                        _pinnedFalling = expected.Falling;
                        _pinnedLanding = expected.Landing;
                        _pinnedFall = fall;
                        SyncTracker.Report?.CountFlagPin();
                    }
                    catch { }
                }
            }

            SyncTracker.CompareRay(expected, actual, effectiveHit ?? actual.Hit);
        }

        internal static void After() => Release();

        /// <summary>
        /// Undo the pin. A value the game itself changed during the call wins:
        /// only what still holds the pinned value is put back.
        /// </summary>
        private static void Release()
        {
            if (_moved != null)
            {
                try
                {
                    if (Bits.Same(_moved.localPosition, _pinnedLocalPosition) &&
                        Bits.Same(_moved.localRotation, _pinnedLocalRotation))
                    {
                        _moved.localPosition = _savedLocalPosition;
                        _moved.localRotation = _savedLocalRotation;
                    }
                }
                catch { }
                _moved = null;
            }

            if (_pinnedFall != null)
            {
                try
                {
                    if (_pinnedFall.isFalling == _pinnedFalling) _pinnedFall.isFalling = _savedFalling;
                    if (_pinnedFall.isLanding == _pinnedLanding) _pinnedFall.isLanding = _savedLanding;
                }
                catch { }
                _pinnedFall = null;
            }
        }

        private static FallingHolder FindFall(GameObject player)
        {
            try
            {
                if (player != null)
                {
                    var fall = player.GetComponent<FallingHolder>();
                    if (fall != null) return fall;
                }
            }
            catch { }
            try { return PlayerGate.Player?.FallingHolder; }
            catch { return null; }
        }

        /// <summary>
        /// What a ray from this transform hits, by collider name. A proxy for
        /// the game's own cast (whose layer mask is not known statically), good
        /// enough to tell "same target" from "different target".
        /// </summary>
        private static string HitName(Transform origin, float distance)
        {
            try
            {
                if (!(distance > 0f) || !float.IsFinite(distance)) distance = 5f;
                if (!Physics.Raycast(origin.position, origin.forward, out var hit, distance)) return "";
                var collider = hit.collider;
                if (collider == null) return "?";
                var go = collider.gameObject;
                return go != null ? go.name ?? "?" : "?";
            }
            catch { return "?"; }
        }

        internal static float PickDistance(PickRay ray)
        {
            try { return Math.Max(ray.RaycastDis, ray.RaycastCheckItemDis); }
            catch { return 5f; }
        }

        internal static float DoorDistance(DoorRay ray)
        {
            try { return ray.RaycastDis; }
            catch { return 5f; }
        }

        internal static GameObject PickPlayer(PickRay ray)
        {
            try { return ray.Player; }
            catch { return null; }
        }

        [HarmonyPatch(typeof(PickRay), nameof(PickRay.Update))]
        internal static class Patch_PickRay
        {
            // Last among prefixes so the pin is in place for the original and
            // nothing else; first among postfixes so it is gone before anyone
            // else looks.
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(PickRay __instance)
            {
                // Idle is the common case; do not touch game fields for it.
                if (!SyncTracker.Active) return;
                Before(RaySample.Pick, __instance, PickPlayer(__instance), PickDistance(__instance));
            }

            [HarmonyPriority(Priority.First)]
            private static void Postfix() => After();
        }

        [HarmonyPatch(typeof(DoorRay), nameof(DoorRay.Update))]
        internal static class Patch_DoorRay
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(DoorRay __instance)
            {
                if (!SyncTracker.Active) return;
                Before(RaySample.Door, __instance, null, DoorDistance(__instance));
            }

            [HarmonyPriority(Priority.First)]
            private static void Postfix() => After();
        }
    }
}
