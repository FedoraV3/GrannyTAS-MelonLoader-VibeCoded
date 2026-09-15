using System;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Decides whether the player is actually in control of the character.
    ///
    /// This is what replaced the arm/disarm switch. The mod pins engine timing
    /// and takes over input, and doing that while the game is running its own
    /// scripted sequence — the wake-up animation at the start of a day above
    /// all — produced macros whose first frames were never driven by the player.
    /// Replaying one desynced immediately, because the recorded frames and the
    /// replayed frames were not even the same kind of frame.
    ///
    /// The predicate is not a heuristic. <c>MobileFPS.Update</c> reads input
    /// behind exactly three gates, verified by disassembly
    /// (see docs/ida-findings.md):
    ///
    /// <code>
    ///   if (!Paused.IsPaused)            // whole body
    ///     if (!isAllowedToMove) ...      // returns before reading any input
    ///     if (!AbleToMove) ...           // returns before reading WASD
    ///     if (CamK) GetTouchInput()      // mouse look
    /// </code>
    ///
    /// So this class asks the same questions the game asks. When it says the
    /// player is in control, the game is about to read input; when it says they
    /// are not, the game will ignore whatever the mod injects — which is
    /// precisely the condition under which recording is meaningless.
    /// </summary>
    public static class PlayerGate
    {
        /// <summary>
        /// Consecutive ready frames required before engaging. At 60 fps this is
        /// a fifth of a second — unnoticeable, and long enough to outlast the
        /// frame or two of flicker as the intro coroutine flips its flags in
        /// sequence rather than atomically.
        /// </summary>
        private const int SettleFrames = 12;

        private static int _streak;
        private static MobileFPS _player;

        /// <summary>Why the gate is where it is, for the log and the panel.</summary>
        public static string Reason { get; private set; } = "waiting for the player";

        /// <summary>The player controller, when one exists. Null otherwise.</summary>
        public static MobileFPS Player => _player;

        /// <summary>
        /// Whether the most recent failed readiness probe is safe for an active
        /// macro to bridge. Scripted movement transitions temporarily take one
        /// or more movement/look gates, the player camera, and sometimes the
        /// cached player's active flag away without destroying the player or
        /// ending the playable scene. Those are the only failures ignored here;
        /// pause, startup, death, teardown, and probe failures remain hard stops.
        /// </summary>
        public static bool CanBridgeTransientControlLoss { get; private set; }

        /// <summary>
        /// Whether the game's own pause menu currently owns the simulation
        /// clock. Exposed separately from <see cref="IsReady"/> so an engaged
        /// transport can suspend without treating a menu as lost control.
        /// </summary>
        public static bool GamePaused => ReadIsPaused();

        /// <summary>
        /// Live values of everything the gate tests, for the panel's diagnostics
        /// readout. Two of these flags could not be pinned down statically —
        /// nothing in the binary writes <c>isAllowedToMove</c>/<c>CamK</c> on the
        /// day-1 path, so their intro values come from serialized scene data —
        /// which is exactly why they are worth showing live rather than trusting.
        /// </summary>
        public struct Flags
        {
            public bool HasPlayer;
            public bool ObjectActive;
            public bool ComponentEnabled;
            public bool NotPaused;
            public bool IsAllowedToMove;
            public bool AbleToMove;
            public bool CamK;
            public bool CameraLive;
            public bool NotJumpscared;
            public bool IntroFinished;
            public int Streak;
        }

        public static Flags Current { get; private set; }

        /// <summary>
        /// True once the player has been demonstrably in control for
        /// <see cref="SettleFrames"/> frames. Poll once per real frame.
        /// </summary>
        public static bool IsReady
        {
            get
            {
                if (!Probe())
                {
                    _streak = 0;
                    return false;
                }

                if (_streak < SettleFrames)
                {
                    _streak++;
                    if (_streak < SettleFrames)
                    {
                        Reason = "player control settling";
                        return false;
                    }
                }

                Reason = "in control";
                return true;
            }
        }

        private static bool Probe()
        {
            var f = new Flags();
            var ok = false;
            CanBridgeTransientControlLoss = false;

            try
            {
                // The cached reference dies with the scene; Unity's overloaded
                // null covers the destroyed case as well as the never-found one.
                if (_player == null) _player = UnityEngine.Object.FindObjectOfType<MobileFPS>();

                f.HasPlayer = _player != null;
                if (!f.HasPlayer)
                {
                    Reason = "no player in the scene";
                }
                else
                {
                    f.ObjectActive = _player.gameObject.activeInHierarchy;
                    f.ComponentEnabled = _player.enabled;
                    f.NotPaused = !ReadIsPaused();
                    f.IsAllowedToMove = _player.isAllowedToMove;
                    f.AbleToMove = _player.AbleToMove;
                    f.CamK = _player.CamK;
                    f.CameraLive = CameraLive(_player);
                    f.NotJumpscared = NotJumpscared(_player);
                    f.IntroFinished = IntroFinished(_player);

                    ok = f.ObjectActive && f.ComponentEnabled && f.NotPaused &&
                         f.IsAllowedToMove && f.AbleToMove && f.CamK &&
                         f.CameraLive && f.NotJumpscared && f.IntroFinished;

                    // Once a macro is already active, these hard conditions
                    // distinguish a short scripted hand-off (window jump, bed
                    // hide, and similar animations) from a scene/player that is
                    // genuinely no longer safe to drive. The omitted readiness
                    // conditions are precisely the transition-owned gates. An
                    // inactive cached player is included because the window
                    // jump disables that object before restoring it; a missing
                    // or destroyed player already failed HasPlayer above.
                    CanBridgeTransientControlLoss =
                        f.HasPlayer && f.ComponentEnabled && f.NotPaused &&
                        f.NotJumpscared && f.IntroFinished;

                    if (!ok) Reason = FirstFailure(f);
                }
            }
            catch (Exception e)
            {
                // Objects get torn down underneath this during scene loads, and
                // an Il2Cpp member that does not marshal the way it was expected
                // to throws here too. Naming the exception matters: the previous
                // version reported a bare "scene in flux" for both, which said
                // nothing about which call had actually failed.
                Reason = "probe error: " + e.Message;
                ok = false;
            }

            f.Streak = _streak;
            Current = f;
            return ok;
        }

        /// <summary>
        /// Report the *first* unmet condition rather than all of them. They fall
        /// in rough causal order, so the first is the one worth acting on.
        /// </summary>
        private static string FirstFailure(Flags f)
        {
            if (!f.ObjectActive) return "player object inactive";
            if (!f.ComponentEnabled) return "player controller disabled";
            if (!f.NotPaused) return "game is paused";
            if (!f.IntroFinished) return "wake-up animation still playing";
            if (!f.NotJumpscared) return "player caught";
            if (!f.IsAllowedToMove) return "isAllowedToMove is false (cutscene or escape)";
            if (!f.AbleToMove) return "AbleToMove is false (frozen, trapped or webbed)";
            if (!f.CamK) return "CamK is false (look control not handed over yet)";
            if (!f.CameraLive) return "player camera not rendering";
            return "not in control";
        }

        /// <summary>
        /// <c>Paused.IsPaused</c> is the only static bool in the assembly and
        /// wraps the whole of <c>MobileFPS.Update</c>. Guarded on its own: a
        /// static field touch can throw if the class has not initialised yet,
        /// and "cannot tell" should read as paused rather than as in-control.
        /// </summary>
        private static bool ReadIsPaused()
        {
            try { return Paused.IsPaused; }
            catch { return true; }
        }

        /// <summary>
        /// <c>playerCamera2</c> is the actual <c>Camera</c> (<c>playerCamera</c>
        /// is only the pitch pivot Transform). A cutscene camera swap is one of
        /// the ways control is taken away, so the player's own camera has to be
        /// the one rendering.
        /// </summary>
        private static bool CameraLive(MobileFPS fps)
        {
            try
            {
                var cam = fps.playerCamera2;
                return cam != null && cam.isActiveAndEnabled;
            }
            catch
            {
                // Absent on some scenes; do not let it veto the gate on its own.
                return true;
            }
        }

        /// <summary>
        /// A jumpscare clears <c>isAllowedToMove</c> and <c>AbleToMove</c>
        /// anyway, so this is belt and braces — but it makes the *reason* read
        /// "player caught" instead of a bare flag name.
        /// </summary>
        private static bool NotJumpscared(MobileFPS fps)
        {
            try
            {
                var ps = fps.PS;
                if (ps == null) return true;
                return !ps.IsJumpscared && !ps.Killed;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// The wake-up sequence, which is the whole reason this class exists.
        ///
        /// <c>Days.StartDays</c> ends the intro by deactivating the bed
        /// animation's parent object, so that object being active means the
        /// sequence has not finished. This is checked *independently* of the
        /// movement flags on purpose: the flags are set explicitly only on the
        /// day-2-and-later branches, so on day 1 they come from serialized scene
        /// data and cannot be assumed false during the animation.
        /// </summary>
        private static bool IntroFinished(MobileFPS fps)
        {
            try
            {
                var days = fps.SystemDay;
                if (days == null) return true;

                var bed = days.PlayerBedAnim;
                if (bed == null) return true;

                var holder = bed.transform.parent;
                var obj = holder != null ? holder.gameObject : bed.gameObject;

                return !obj.activeInHierarchy;
            }
            catch
            {
                // Unreachable intro machinery must not wedge the gate shut.
                return true;
            }
        }

        /// <summary>Drop cached state, e.g. across a scene change.</summary>
        public static void Invalidate()
        {
            _player = null;
            _streak = 0;
            CanBridgeTransientControlLoss = false;
        }
    }
}
