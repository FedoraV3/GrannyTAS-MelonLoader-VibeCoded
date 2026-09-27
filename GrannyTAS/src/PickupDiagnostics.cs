using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Traces the item-pickup gate chain so a failed pickup names its own cause.
    ///
    /// Pickups replay unreliably while doors do not, which points at a gate
    /// `PickRay` checks and `DoorRay` does not rather than at the input layer.
    /// Guessing which one is cheap to get wrong: every candidate — the ring
    /// being active, `FallingHolder.isFalling` / `isLanding`, `WindowJumping
    /// .IsJumping`, `PlayerStatus.IsJumpscared`, `Paused.IsPaused`, and whether
    /// `buttonClicked` survives to the frame that consumes it — is observable at
    /// runtime, so this logs the whole tuple around the interact edge instead.
    ///
    /// Recording and playback of the same pickup produce two traces; the first
    /// line that differs is the answer. Off by default (`PickupTrace` in the
    /// config): it writes a line per frame in a window, which is noise during
    /// normal use.
    /// </summary>
    internal static class PickupDiagnostics
    {
        /// <summary>Frames still to be traced after an edge or a state change.</summary>
        private const int WindowFrames = 12;

        public static bool Enabled { get; set; }
        public static MelonLogger.Instance Log { get; set; }

        private static int _remaining;
        private static string _last = "";
        private static bool _sawPickup;

        /// <summary>What the mod is delivering, which is the trace's left-hand column.</summary>
        private static string Phase()
        {
            var macro = GrannyTasMod.Instance?.Macro;
            if (macro == null) return "idle";
            switch (macro.Mode)
            {
                case MacroMode.Recording: return "rec";
                case MacroMode.Playing: return "play";
                case MacroMode.Aligning: return "align";
                default: return GrannyTasMod.Instance.Time.Paused ? "paused" : "live";
            }
        }

        private static long Frame() => GrannyTasMod.Instance?.Time.FrameCount ?? -1;

        internal static void OnPickupItem(string item)
        {
            if (!Enabled) return;
            _sawPickup = true;
            _remaining = Math.Max(_remaining, 2);
            Log?.Msg($"[pickup] {Phase()} f{Frame()} PickupItem({item})");
        }

        /// <summary>
        /// Read the gates in the same order `PickRay.Update` tests them. Reads
        /// are individually guarded: a missing component must degrade the trace,
        /// never throw inside the game's Update.
        /// </summary>
        internal static void Trace(PickRay ray, bool after)
        {
            if (!Enabled || ray == null) return;

            try
            {
                var interact = ray.MainInteract;
                // The virtual state, not hardware: this is exactly what the
                // game was told this frame.
                var edge = VirtualInput.Active ? VirtualInput.Down(interact) : Input.GetKeyDown(interact);
                var held = VirtualInput.Active ? VirtualInput.Current.Has(interact) : Input.GetKey(interact);

                var ring = ray.Ring != null && ray.Ring.activeSelf;
                var clicked = ray.buttonClicked;
                var clicked2 = ray.buttonClicked2;

                var falling = false;
                var landing = false;
                var haveFall = false;
                if (ray.Player != null)
                {
                    var fh = ray.Player.GetComponent<FallingHolder>();
                    if (fh != null)
                    {
                        haveFall = true;
                        falling = fh.isFalling;
                        landing = fh.isLanding;
                    }
                }

                var jumping = ray.WJ != null && ray.WJ.IsJumping;
                var jumpscared = ray.PlayerStatus != null && ray.PlayerStatus.IsJumpscared;
                var gamePaused = Paused.IsPaused;

                // The pickup ray comes from PickRay's OWN transform, not from
                // the camera pivot the mod snapshots and restores (verified in
                // docs/ida-pickup-findings.md: origin this.transform.position,
                // direction this.transform.TransformDirection(forward)). If that
                // transform is smoothed or parented below the pivot, its pose is
                // unrestored state and the ray can point somewhere else on a
                // replay while the ring — a different, wider check — still shows.
                // So log where the ray actually goes and what it actually hits.
                var aim = RayTrace(ray);

                var state =
                    $"edge={(edge ? 1 : 0)} held={(held ? 1 : 0)} ring={(ring ? 1 : 0)} " +
                    $"clicked={(clicked ? 1 : 0)}/{(clicked2 ? 1 : 0)} " +
                    $"fall={(haveFall ? (falling ? "1" : "0") : "?")} land={(haveFall ? (landing ? "1" : "0") : "?")} " +
                    $"jump={(jumping ? 1 : 0)} scare={(jumpscared ? 1 : 0)} paused={(gamePaused ? 1 : 0)} " + aim;

                // An edge, or anything that changed, opens the window; inside it
                // every frame is logged so the frame after the edge — the one a
                // deferred `buttonClicked` would be consumed on — is never the
                // one missing from the trace.
                if (edge || state != _last) _remaining = WindowFrames;
                if (_remaining <= 0) { _last = state; return; }
                if (!after) _remaining--;

                Log?.Msg($"[pickup] {Phase()} f{Frame()} dt={Time.deltaTime:0.#####} " +
                         $"{(after ? "post" : "pre ")} {state}{(_sawPickup && after ? " PICKED" : "")}");
                _last = state;
                if (after) _sawPickup = false;
            }
            catch (Exception e)
            {
                Log?.Warning($"[pickup] trace failed: {e.Message}");
                Enabled = false;
            }
        }

        private static string _originPath;

        /// <summary>
        /// Reproduce the game's pickup ray and report what it hits.
        ///
        /// The distance field the native raycast uses could not be pinned
        /// statically, so both candidates are reported: a hit that appears only
        /// at the longer reach is itself the finding.
        /// </summary>
        private static string RayTrace(PickRay ray)
        {
            var origin = ray.transform;
            if (origin == null) return "ray=?";

            // The origin object's identity is the whole question, so name it once.
            if (_originPath == null)
            {
                _originPath = Path(origin);
                var player = PlayerGate.Player;
                var pivot = player != null && player.playerCamera != null ? Path(player.playerCamera) : "?";
                Log?.Msg($"[pickup] ray origin '{_originPath}' (camera pivot '{pivot}'), " +
                         $"RaycastDis={ray.RaycastDis:0.###} RaycastCheckItemDis={ray.RaycastCheckItemDis:0.###}");
            }

            var dir = origin.TransformDirection(Vector3.forward);
            var dis = Mathf.Max(ray.RaycastDis, ray.RaycastCheckItemDis);
            if (dis <= 0f) dis = 5f;

            var hit = Describe(origin.position, dir, dis, out var hitDis);
            // Divergence between the ray and the restored pivot is the signal.
            var pivotAngle = -1f;
            var p = PlayerGate.Player;
            if (p != null && p.playerCamera != null)
                pivotAngle = Vector3.Angle(dir, p.playerCamera.TransformDirection(Vector3.forward));

            var e = origin.eulerAngles;
            return $"aim=({e.x:0.###},{e.y:0.###},{e.z:0.###}) vspivot={pivotAngle:0.###} " +
                   $"hit={hit}@{hitDis:0.###}";
        }

        private static string Describe(Vector3 from, Vector3 dir, float dis, out float distance)
        {
            distance = -1f;
            RaycastHit info;
            if (!Physics.Raycast(from, dir, out info, dis)) return "-";
            distance = info.distance;

            var collider = info.collider;
            if (collider == null) return "?";
            var go = collider.gameObject;
            if (go == null) return "?";

            var seed = go.GetComponent<ItemSeedData>();
            return seed != null ? $"{go.name}[{seed.itemName}]" : go.name;
        }

        private static string Path(Transform t)
        {
            var name = t.name;
            for (var p = t.parent; p != null; p = p.parent) name = p.name + "/" + name;
            return name;
        }

        [HarmonyPatch(typeof(PickRay), nameof(PickRay.Update))]
        internal static class Patch_PickRay_Update
        {
            // Runs after GameplayFreeze's own prefix; a frozen frame skips the
            // original, and postfixes still run, so both sides are traced and a
            // frozen frame is visible as dt=0 in the trace rather than absent.
            private static void Prefix(PickRay __instance) => Trace(__instance, after: false);
            private static void Postfix(PickRay __instance) => Trace(__instance, after: true);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.PickupItem))]
        internal static class Patch_Inventory_PickupItem
        {
            private static void Prefix(string itemName)
            {
                // The sync check compares pickups frame by frame whether or not
                // the trace is on — a missed item is the symptom it exists for.
                SyncTracker.OnPickup(itemName ?? "?");
                OnPickupItem(itemName ?? "?");
            }
        }
    }
}
