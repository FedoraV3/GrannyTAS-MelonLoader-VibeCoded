using System.Collections.Generic;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Attributes things that happen *between* macro frames — interaction rays
    /// and item pickups — to the macro frame they belong to, on both the
    /// recording and the replay side.
    ///
    /// The rule is the same in both directions: an event belongs to the most
    /// recently issued macro frame. Whether the game's <c>Update</c> runs before
    /// or after the mod's in a given engine frame, and whether the AI's
    /// <c>FixedUpdate</c> ran at the start of it, is therefore identical between
    /// the two as long as the script order is — and <see cref="RaySample.Order"/>
    /// records that order so a change in it is reported rather than silently
    /// shifting every event by a frame.
    /// </summary>
    internal static class SyncTracker
    {
        public enum TrackMode { Off, Recording, Replaying }

        public static TrackMode Mode { get; private set; }

        /// <summary>The replay's report, while replaying.</summary>
        public static SyncReport Report { get; private set; }

        /// <summary>Index of the most recently issued macro frame; -1 before the first.</summary>
        public static int LastIssued { get; private set; } = -1;

        private static int _issuedEngineFrame = int.MinValue;

        // Recording: the trace of the last captured frame, collecting its events.
        private static FrameTrace _recording;

        // Replay: what the recording saw after LastIssued, and what the replay sees.
        private static FrameTrace _expected;
        private static FrameTrace _actual = new FrameTrace();
        private static readonly int[] RayCursor = new int[2];

        // Replay: pickups in _actual that the mod made rather than the game's
        // own pickup code (InteractionPin.ForcePending), this window.
        private static readonly List<string> Forced = new List<string>();
        private static bool _forcing;

        /// <summary>True once a macro frame exists for events to belong to.</summary>
        public static bool Active => Mode != TrackMode.Off && LastIssued >= 0;

        /// <summary>
        /// 0 when called during the engine frame that issued the current macro
        /// frame (after the mod's update), 1 when called in a later engine frame.
        /// </summary>
        public static int OrderTag => Time.frameCount == _issuedEngineFrame ? 0 : 1;

        public static void BeginRecording()
        {
            Reset();
            Mode = TrackMode.Recording;
        }

        public static void BeginReplay(SyncReport report)
        {
            Reset();
            Report = report;
            Mode = TrackMode.Replaying;
        }

        /// <summary>
        /// Stop attributing events. The last frame's event window is left
        /// uncompared: the recording's copy of it was cut off wherever recording
        /// stopped, so comparing it would report a difference that is not one.
        /// </summary>
        public static void Stop() => Reset();

        private static void Reset()
        {
            Mode = TrackMode.Off;
            LastIssued = -1;
            _issuedEngineFrame = int.MinValue;
            _recording = null;
            _expected = null;
            _actual = new FrameTrace();
            RayCursor[0] = RayCursor[1] = 0;
            Forced.Clear();
            _forcing = false;
        }

        /// <summary>Recording: frame <paramref name="index"/> was just captured into <paramref name="trace"/>.</summary>
        public static void RecordIssued(int index, FrameTrace trace)
        {
            if (Mode != TrackMode.Recording) return;
            LastIssued = index;
            _issuedEngineFrame = Time.frameCount;
            _recording = trace;
        }

        /// <summary>
        /// Replay: frame <paramref name="index"/> is being issued. Closes the
        /// previous frame's event window and compares it.
        /// </summary>
        public static void ReplayIssued(int index, FrameTrace expected)
        {
            if (Mode != TrackMode.Replaying) return;
            CloseReplayWindow(compareRays: true);

            LastIssued = index;
            _issuedEngineFrame = Time.frameCount;
            _expected = expected;
            _actual = new FrameTrace();
            RayCursor[0] = RayCursor[1] = 0;
            Forced.Clear();
        }

        /// <summary>
        /// Close the last naturally completed frame at the end of a replay.
        /// Pickup calls have had their full Update window by this point, while
        /// ray counts are intentionally omitted because recording may have
        /// stopped part-way through that final window.
        /// </summary>
        public static void CompleteReplay()
        {
            if (Mode != TrackMode.Replaying) return;
            CloseReplayWindow(compareRays: false);
            LastIssued = -1;
            _expected = null;
            _actual = new FrameTrace();
            RayCursor[0] = RayCursor[1] = 0;
            Forced.Clear();
        }

        private static void CloseReplayWindow(bool compareRays)
        {
            if (LastIssued < 0 || Report == null || !Report.TracesPresent) return;
            Report.CompareEvents(LastIssued, _expected, _actual, RayCursor, compareRays, Forced);
        }

        /// <summary>
        /// Replay: how many more times the recording picked up
        /// <paramref name="item"/> in the current frame's window than the
        /// replay has so far. 0 outside a replay window.
        /// </summary>
        public static int PendingPickups(string item)
        {
            if (Mode != TrackMode.Replaying || LastIssued < 0 || _expected == null) return 0;
            item ??= "";
            var pending = 0;
            foreach (var p in _expected.Pickups) if (p == item) pending++;
            foreach (var p in _actual.Pickups) if (p == item) pending--;
            return pending;
        }

        /// <summary>
        /// Replay: every pickup the recording made in the current frame's window
        /// that the replay has not made yet, in recorded order. Empty outside a
        /// replay window.
        /// </summary>
        public static List<string> PendingPickupList()
        {
            var pending = new List<string>();
            if (Mode != TrackMode.Replaying || LastIssued < 0 || _expected == null) return pending;
            pending.AddRange(_expected.Pickups);
            foreach (var p in _actual.Pickups) pending.Remove(p);
            return pending;
        }

        /// <summary>
        /// Replay: whether the recording cast a ray of this kind in the current
        /// window after the ones the replay has cast so far.
        /// </summary>
        public static bool MoreExpectedRays(int kind)
        {
            if (Mode != TrackMode.Replaying || _expected == null || kind < 0 || kind > 1) return false;
            var recorded = 0;
            foreach (var r in _expected.Rays) if (r.Kind == kind) recorded++;
            return RayCursor[kind] < recorded;
        }

        /// <summary>
        /// Replay: pickups until <see cref="EndForcedPickup"/> are the mod's,
        /// not the game's. They count toward the frame's pickups, so nothing
        /// forces them twice, and the report still lists them as missed.
        /// </summary>
        public static void BeginForcedPickup() => _forcing = Mode == TrackMode.Replaying;

        public static void EndForcedPickup() => _forcing = false;

        // ---- events -------------------------------------------------------------

        public static void OnPickup(string item)
        {
            if (!Active) return;
            if (Mode == TrackMode.Recording) _recording?.Pickups.Add(item ?? "");
            else
            {
                _actual.Pickups.Add(item ?? "");
                if (_forcing) Forced.Add(item ?? "");
            }
        }

        public static void RecordRay(RaySample sample)
        {
            if (Mode == TrackMode.Recording && LastIssued >= 0) _recording?.Rays.Add(sample);
        }

        /// <summary>The recorded ray of this kind that corresponds to the next replayed cast, or null.</summary>
        public static RaySample NextExpectedRay(int kind)
        {
            if (Mode != TrackMode.Replaying || LastIssued < 0 || kind < 0 || kind > 1) return null;
            var wanted = RayCursor[kind]++;
            if (_expected == null) return null;
            var seen = 0;
            foreach (var r in _expected.Rays)
                if (r.Kind == kind && seen++ == wanted) return r;
            return null;
        }

        public static void CompareRay(RaySample expected, RaySample actual, string effectiveHit)
        {
            if (Mode != TrackMode.Replaying || LastIssued < 0 || Report == null || !Report.TracesPresent) return;
            Report.CompareRay(LastIssued, expected, actual, effectiveHit);
        }
    }
}
