using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Makes the pickup and door rays of a replay the rays of the recording,
    /// and makes every item the recording picked up end up picked up on the
    /// same frame of the replay.
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
    ///
    /// On a cast in whose frame the recording picked an item up, more is
    /// needed, because the game refuses the click for reasons the ray pin does
    /// not touch: it only accepts the interact edge when the pickup ring was
    /// already showing from the previous frame's cast. So for that one call
    /// (<see cref="AssistPickup"/>) the item is put under the ray, and the
    /// click and the jump/scare/fall gates are given the values the recording
    /// must have had — the game's own pickup code then takes the item. If it
    /// still does not, the item is picked up right after the call through the
    /// same game methods the pickup code calls (<see cref="ForcePending"/>), so
    /// a recorded pickup is never lost.
    /// </summary>
    internal static class InteractionPin
    {
        /// <summary>Pin replayed rays to the recorded pose and enforce recorded pickups. Off: measure only.</summary>
        public static bool Enabled { get; set; } = true;

        // Gap kept between the steered item and the ray origin or whatever the
        // ray would otherwise hit.
        private const float Clearance = .01f;

        private static Transform _moved;
        private static Vector3 _savedLocalPosition;
        private static Quaternion _savedLocalRotation;
        private static Vector3 _pinnedLocalPosition;
        private static Quaternion _pinnedLocalRotation;

        private static FallingHolder _pinnedFall;
        private static bool _savedFalling, _savedLanding, _pinnedFalling, _pinnedLanding;

        private static Transform _steered;
        private static Vector3 _steerSavedLocalPosition, _steerPinnedLocalPosition;
        private static Quaternion _steerSavedLocalRotation, _steerPinnedLocalRotation;

        private static PickRay _clicked;
        private static WindowJumping _unjumped;
        private static PlayerStatus _unscared;

        // The pickup cast whose recorded pickups After() makes sure of.
        private static PickRay _forceRay;
        private static Transform _forcePlayer;
        private static GameObject _forceTarget;
        private static Vector3 _forceOrigin, _forceDirection;

        // Unity defers Destroy to the end of the frame: an item forced this
        // frame is still active and must not be taken twice.
        private static readonly List<GameObject> Consumed = new List<GameObject>();
        private static int _consumedFrame = -1;

        /// <param name="itemReach">
        /// Pickup ray only: how far the game's item cast reaches. 0 leaves items
        /// where they are.
        /// </param>
        internal static void Before(int kind, Component ray, GameObject player, float distance, float itemReach = 0f)
        {
            // A postfix that never ran must not leave a pin behind for the next call.
            Release();
            _forceRay = null;

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
                        PinFall(fall, expected.Falling, expected.Landing);
                        SyncTracker.Report?.CountFlagPin();
                    }
                    catch { }
                }
            }

            // Measured before the item or any gate is touched, like everything else.
            SyncTracker.CompareRay(expected, actual, effectiveHit ?? actual.Hit);

            if (expected != null && Enabled && kind == RaySample.Pick && ray is PickRay pickRay &&
                SyncTracker.PendingPickupList().Count > 0)
            {
                _forceRay = pickRay;
                _forceTarget = null;
                _forcePlayer = PlayerTransform(player);
                try
                {
                    _forceOrigin = t.position;
                    _forceDirection = t.forward;
                }
                catch { _forceRay = null; return; }
                var sameAsRecorded = expected.Hit == (effectiveHit ?? actual.Hit) ? expected.Hit : null;
                AssistPickup(pickRay, t, fall, itemReach > 0f ? itemReach : distance, sameAsRecorded);
            }
        }

        /// <summary>
        /// The recording picked an item up during this frame and the replay has
        /// not yet. Set this call up so the game's own <c>PickRay.Update</c>
        /// takes it — on the recorded frame, with its prompt, sound, drop and
        /// special-item logic, exactly as a player's pickup:
        /// the item is put under the (already pinned) ray, and the gates that
        /// would refuse the click are given the values the recording had. All
        /// of it is undone when the call ends; an item that was picked up is
        /// destroyed before the frame renders, so the move is never on screen.
        /// </summary>
        /// <param name="sameAsRecorded">
        /// The recorded hit, when the cast already hits the same thing: the
        /// world under the ray is the recording's, and nothing is moved or synced.
        /// </param>
        private static void AssistPickup(PickRay ray, Transform origin, FallingHolder fall, float reach, string sameAsRecorded)
        {
            var frame = SyncTracker.LastIssued;
            var report = SyncTracker.Report;
            var candidates = Candidates(_forcePlayer);
            if (candidates.Count == 0) return; // nothing to steer; After() still forces it

            ItemSeedData target = null;
            if (!string.IsNullOrEmpty(sameAsRecorded))
            {
                try
                {
                    foreach (var item in candidates)
                        if (item.gameObject.name == sameAsRecorded) { target = item; break; }
                }
                catch { }
            }
            target ??= SteerItem(origin, candidates, reach, frame, report);
            if (target == null) return; // a click would go to whatever else is under the ray
            _forceTarget = target.gameObject;
            var name = target.itemName ?? "";

            // The recording's pickup passed every gate below on this call; the
            // replay's may not. docs/ida-pickup-findings.md §1–§2.
            var pinned = false;
            try
            {
                if (fall != null && (fall.isFalling || fall.isLanding))
                {
                    PinFall(fall, false, false);
                    pinned = true;
                }
            }
            catch { }
            try
            {
                var jump = ray.WJ;
                if (jump != null && jump.IsJumping)
                {
                    jump.IsJumping = false;
                    _unjumped = jump;
                    pinned = true;
                }
            }
            catch { }
            try
            {
                var status = ray.PlayerStatus;
                if (status != null && status.IsJumpscared)
                {
                    status.IsJumpscared = false;
                    _unscared = status;
                    pinned = true;
                }
            }
            catch { }
            if (pinned) report?.CountFlagPin();

            // The game turns the interact edge into buttonClicked only while
            // the ring from the previous frame's cast is still showing. A replay
            // whose previous cast missed the item has no ring, and the click is
            // dropped however well this cast is aimed. Hand the click over
            // directly; the item block consumes it like any other.
            try
            {
                if (!ray.buttonClicked && !(InteractEdge(ray) && RingShowing(ray)))
                {
                    ray.buttonClicked = true;
                    _clicked = ray;
                    report?.RecordClickPin(frame, name);
                }
            }
            catch { }
        }

        /// <summary>
        /// Active world items, not held by the player, of a name the recording
        /// picked up in this frame and the replay has not yet.
        /// </summary>
        private static List<ItemSeedData> Candidates(Transform player)
        {
            var candidates = new List<ItemSeedData>();
            if (_consumedFrame != Time.frameCount)
            {
                Consumed.Clear();
                _consumedFrame = Time.frameCount;
            }
            try
            {
                foreach (var item in UnityEngine.Object.FindObjectsOfType<ItemSeedData>())
                {
                    if (item == null) continue;
                    var go = item.gameObject;
                    if (go == null || !go.activeInHierarchy || Consumed.Contains(go)) continue;
                    if (IsUnder(item.transform, player)) continue; // held, not lying in the world
                    if (SyncTracker.PendingPickups(item.itemName) > 0) candidates.Add(item);
                }
            }
            catch { }
            return candidates;
        }

        /// <summary>
        /// Put the nearest candidate where the pickup ray meets it first, for
        /// this one call. Returns the item the ray now hits, or null when no
        /// candidate could be made the first thing on the ray.
        /// </summary>
        private static ItemSeedData SteerItem(Transform ray, List<ItemSeedData> candidates, float reach, int frame, SyncReport report)
        {
            if (!(reach > 0f) || !float.IsFinite(reach)) reach = 5f;
            try
            {
                var origin = ray.position;
                var direction = ray.forward;

                var hit = FirstHit(origin, direction, reach, _forcePlayer, out var clear);
                var already = Owner(hit, candidates);
                if (already != null) return already;

                // autoSyncTransforms is off: a collider only follows its
                // transform after a sync. A stale collider is the likeliest
                // reason the item is off the ray, so sync and look again first.
                Physics.SyncTransforms();
                hit = FirstHit(origin, direction, reach, _forcePlayer, out clear);
                already = Owner(hit, candidates);
                if (already != null)
                {
                    report?.RecordItemSteer(frame, already.itemName, 0f);
                    return already;
                }

                ItemSeedData chosen = null;
                var shift = Vector3.zero;
                var best = float.MaxValue;
                foreach (var item in candidates)
                {
                    var collider = item.gameObject.GetComponent<Collider>();
                    if (collider == null || !collider.enabled) continue;
                    var bounds = collider.bounds;
                    // Far enough out that the ray does not start inside it, and
                    // in front of whatever the ray hits instead — as close to
                    // that as the item's size allows.
                    var near = bounds.extents.magnitude + Clearance;
                    var far = Math.Max(near, clear - Clearance);
                    var along = Mathf.Clamp(Vector3.Dot(bounds.center - origin, direction), near, far);
                    var move = origin + direction * along - bounds.center;
                    var length = move.magnitude;
                    if (length >= best) continue;
                    best = length;
                    shift = move;
                    chosen = item;
                }

                if (chosen == null)
                {
                    report?.RecordItemLeft(frame, candidates[0].itemName, "it has no enabled collider to put under the ray");
                    return null;
                }

                var moved = chosen.transform;
                _steerSavedLocalPosition = moved.localPosition;
                _steerSavedLocalRotation = moved.localRotation;
                moved.SetPositionAndRotation(moved.position + shift, moved.rotation);
                _steerPinnedLocalPosition = moved.localPosition;
                _steerPinnedLocalRotation = moved.localRotation;
                _steered = moved;
                Physics.SyncTransforms();

                if (Owner(FirstHit(origin, direction, reach, _forcePlayer, out _), candidates) != chosen)
                {
                    ReleaseSteer();
                    report?.RecordItemLeft(frame, chosen.itemName, "the ray still missed it after the move");
                    return null;
                }
                report?.RecordItemSteer(frame, chosen.itemName, best);
                return chosen;
            }
            catch
            {
                ReleaseSteer();
                return null;
            }
        }

        /// <summary>
        /// After the game's own call: every pickup the recording made in this
        /// frame that the game still did not make is made now, through the
        /// methods its pickup code calls, before anything else runs. Only after
        /// the recording's last pickup cast of the frame — an earlier cast may
        /// still be the one that takes it.
        /// </summary>
        private static void ForcePending(PickRay ray)
        {
            if (SyncTracker.MoreExpectedRays(RaySample.Pick)) return;
            var pending = SyncTracker.PendingPickupList();
            if (pending.Count == 0) return;

            var frame = SyncTracker.LastIssued;
            var report = SyncTracker.Report;
            foreach (var item in pending)
            {
                var failure = ForceOne(ray, item, out var note);
                if (failure == null) report?.RecordPickupForced(frame, item, note);
                else report?.RecordPickupForceFailed(frame, item, failure);
            }
        }

        /// <returns>Null when the item was picked up, otherwise why it was not.</returns>
        private static string ForceOne(PickRay ray, string itemName, out string note)
        {
            note = "";
            if (string.IsNullOrEmpty(itemName)) return "the recorded item has no name";

            Inventory inventory;
            ItemDefs definition;
            try
            {
                inventory = ray.Inventory;
                if (inventory == null) return "PickRay has no Inventory";
                definition = inventory.GetItemDefByName(itemName);
            }
            catch (Exception e) { return "item lookup failed: " + e.Message; }
            if (definition == null) return "the inventory has no item of that name";

            GameObject hand;
            try
            {
                hand = definition.handObject;
                if (hand == null) return "the item has no hand object";
                // CheckItemDropping reaches all of these, and the drop fields of
                // whichever special item is in hand (RE/missed-pickup-recovery.md).
                if (ray.H_CR == null || ray.H_DSH == null || ray.H_FT == null || ray.H_PTRAP == null || ray.Drop1 == null)
                    return "PickRay's special-hand references are not set up";
                if (ray.H_CR.activeSelf && (ray.DropP == null || ray.ItemDrop == null))
                    return "the crossbow in hand has no drop references";
                if (ray.H_DSH.activeSelf && (ray.DropP == null || ray.ItemDrop == null ||
                                             ray.ShotgunHandMain == null || ray.ShotgunHandPipes == null))
                    return "the shotgun in hand has no drop references";
                if (ray.H_FT.activeSelf && (ray.DropPFreezeTrap == null || ray.O_FT == null))
                    return "the freeze trap in hand has no drop references";
                if (ray.H_PTRAP.activeSelf && (ray.DropPFreezeTrap == null || ray.O_PTRAP == null))
                    return "the poison trap in hand has no drop references";
            }
            catch (Exception e) { return "could not inspect the hand: " + e.Message; }

            var source = Source(itemName, hand);
            try
            {
                // Taking another copy of the item in hand would only drop and
                // re-take it, leaving one more in the world than the recording.
                if (source == null && hand.activeSelf) return "it is already in hand and no copy is left in the world";
            }
            catch { }

            SyncTracker.BeginForcedPickup();
            try
            {
                // The generic pickup block of PickRay.Update, in its order:
                // consume the click, drop what is held, take the item, finish
                // the shotgun, destroy the world copy.
                try { ray.buttonClicked = false; } catch { }
                ray.CheckItemDropping();
                inventory.PickupItem(definition.itemName);
                if (IsShotgun(ray, itemName, hand))
                {
                    try { ray.PickShotgun(); }
                    catch (Exception e) { note = "shotgun follow-up failed: " + e.Message; }
                }
                if (source != null)
                {
                    UnityEngine.Object.Destroy(source);
                    Consumed.Add(source);
                }
                else note = Join(note, "no copy was left in the world");
            }
            catch (Exception e) { return "the game's pickup call failed: " + e.Message; }
            finally { SyncTracker.EndForcedPickup(); }

            try
            {
                if (!hand.activeSelf) return "PickupItem ran but the item is not in hand";
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The world copy to remove: the one the assist aimed at, else the one
        /// nearest the recorded ray.
        /// </summary>
        private static GameObject Source(string itemName, GameObject hand)
        {
            GameObject best = null;
            var bestDistance = float.MaxValue;
            foreach (var item in Candidates(_forcePlayer))
            {
                try
                {
                    var go = item.gameObject;
                    if (item.itemName != itemName || go == hand) continue;
                    if (_forceTarget != null && go == _forceTarget) return go;
                    var d = DistanceToRay(item.transform.position);
                    if (d >= bestDistance) continue;
                    bestDistance = d;
                    best = go;
                }
                catch { }
            }
            return best;
        }

        private static float DistanceToRay(Vector3 point)
        {
            var along = Math.Max(0f, Vector3.Dot(point - _forceOrigin, _forceDirection));
            return (_forceOrigin + _forceDirection * along - point).magnitude;
        }

        /// <summary>
        /// The generic block follows the shotgun's pickup with PickShotgun
        /// (RE/missed-pickup-recovery.md, "special-item boundary"). The name
        /// it tests is the "b" literal; the shotgun's hand object is the
        /// object PickShotgun animates.
        /// </summary>
        private static bool IsShotgun(PickRay ray, string itemName, GameObject hand)
        {
            if (itemName == "b") return true;
            try { return ray.H_SG != null && hand == ray.H_SG; }
            catch { return false; }
        }

        private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : a + "; " + b;

        private static bool InteractEdge(PickRay ray)
        {
            var key = ray.MainInteract;
            return VirtualInput.Active ? VirtualInput.Down(key) : Input.GetKeyDown(key);
        }

        private static bool RingShowing(PickRay ray)
        {
            var ring = ray.Ring;
            return ring != null && ring.activeSelf;
        }

        private static void PinFall(FallingHolder fall, bool falling, bool landing)
        {
            if (_pinnedFall == null)
            {
                _savedFalling = fall.isFalling;
                _savedLanding = fall.isLanding;
                _pinnedFall = fall;
            }
            fall.isFalling = falling;
            fall.isLanding = landing;
            _pinnedFalling = falling;
            _pinnedLanding = landing;
        }

        /// <summary>
        /// The nearest collider along the ray that is not part of the player.
        /// The player's own colliders sit wherever the replay has the player,
        /// which a pinned ray can pass through; the game's cast does not stop
        /// on them when the player is where it was recorded.
        /// </summary>
        private static Collider FirstHit(Vector3 origin, Vector3 direction, float reach, Transform player, out float distance)
        {
            distance = reach;
            Collider first = null;
            try
            {
                var hits = Physics.RaycastAll(origin, direction, reach);
                if (hits == null) return null;
                for (var i = 0; i < hits.Length; i++)
                {
                    var hit = hits[i];
                    var collider = hit.collider;
                    if (collider == null || !(hit.distance < distance)) continue;
                    if (IsUnder(collider.transform, player)) continue;
                    first = collider;
                    distance = hit.distance;
                }
            }
            catch { }
            return first;
        }

        private static ItemSeedData Owner(Collider collider, List<ItemSeedData> candidates)
        {
            if (collider == null) return null;
            try
            {
                var go = collider.gameObject;
                if (go == null) return null;
                foreach (var item in candidates)
                    if (item.gameObject == go) return item;
            }
            catch { }
            return null;
        }

        private static bool IsUnder(Transform t, Transform ancestor) =>
            t != null && ancestor != null && t.IsChildOf(ancestor);

        private static Transform PlayerTransform(GameObject player)
        {
            try
            {
                if (player != null) return player.transform;
            }
            catch { }
            try { return PlayerGate.Player?.transform; }
            catch { return null; }
        }

        private static void ReleaseSteer()
        {
            if (_steered == null) return;
            try
            {
                if (Bits.Same(_steered.localPosition, _steerPinnedLocalPosition) &&
                    Bits.Same(_steered.localRotation, _steerPinnedLocalRotation))
                {
                    _steered.localPosition = _steerSavedLocalPosition;
                    _steered.localRotation = _steerSavedLocalRotation;
                }
                Physics.SyncTransforms();
            }
            catch { }
            _steered = null;
        }

        /// <param name="ray">The pickup ray whose call just ended; null for a door ray.</param>
        internal static void After(PickRay ray = null)
        {
            Release();

            var force = _forceRay;
            _forceRay = null;
            if (force == null || ray == null || !Enabled || SyncTracker.Mode != SyncTracker.TrackMode.Replaying) return;
            try
            {
                if (ray != force) return;
            }
            catch { return; }
            ForcePending(force);
            _forceTarget = null;
        }

        /// <summary>
        /// Undo the pin. A value the game itself changed during the call wins:
        /// only what still holds the pinned value is put back.
        /// </summary>
        private static void Release()
        {
            ReleaseSteer();

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

            // A click the game consumed or rejected is already false; one it
            // never reached (a paused frame returns early) must not carry over.
            if (_clicked != null)
            {
                try { _clicked.buttonClicked = false; }
                catch { }
                _clicked = null;
            }
            if (_unjumped != null)
            {
                try
                {
                    if (!_unjumped.IsJumping) _unjumped.IsJumping = true;
                }
                catch { }
                _unjumped = null;
            }
            if (_unscared != null)
            {
                try
                {
                    if (!_unscared.IsJumpscared) _unscared.IsJumpscared = true;
                }
                catch { }
                _unscared = null;
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

        /// <summary>
        /// How far the game's item cast certainly reaches: the shorter of the
        /// two distance fields, since which one it uses is unverified
        /// (docs/ida-pickup-findings.md §4). 0 when neither is usable.
        /// </summary>
        internal static float PickReach(PickRay ray)
        {
            try
            {
                float a = ray.RaycastDis, b = ray.RaycastCheckItemDis;
                var okA = a > 0f && float.IsFinite(a);
                var okB = b > 0f && float.IsFinite(b);
                return okA && okB ? Math.Min(a, b) : okA ? a : okB ? b : 0f;
            }
            catch { return 0f; }
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
            // nothing else; first among postfixes so it is gone — and any
            // missed pickup made — before anyone else looks.
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(PickRay __instance)
            {
                // Idle is the common case; do not touch game fields for it.
                if (!SyncTracker.Active) return;
                Before(RaySample.Pick, __instance, PickPlayer(__instance), PickDistance(__instance), PickReach(__instance));
            }

            [HarmonyPriority(Priority.First)]
            private static void Postfix(PickRay __instance) => After(__instance);
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
