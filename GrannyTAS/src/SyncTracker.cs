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
        private static IPickupRecovery _pickupRecovery;
        private static bool _suppressPickup;
        private const int MaxPickupRecoveriesPerFrame = 16;

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

        public static void BeginReplay(SyncReport report, IPickupRecovery pickupRecovery = null)
        {
            Reset();
            Report = report;
            _pickupRecovery = pickupRecovery;
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
            _pickupRecovery = null;
            _suppressPickup = false;
            RayCursor[0] = RayCursor[1] = 0;
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
        }

        private static void CloseReplayWindow(bool compareRays)
        {
            if (LastIssued < 0 || Report == null || !Report.TracesPresent) return;

            // The report must see the unassisted replay before recovery changes
            // inventory or removes a world source.
            Report.CompareEvents(LastIssued, _expected, _actual, RayCursor, compareRays);
            RecoverMissingPickupSuffix();
        }

        private static void RecoverMissingPickupSuffix()
        {
            if (_pickupRecovery == null || _expected == null || _actual == null || _expected.Pickups.Count == 0)
                return;

            var expectedCount = _expected.Pickups.Count;
            var actualCount = _actual.Pickups.Count;
            if (expectedCount == actualCount)
            {
                var identical = true;
                for (var i = 0; i < expectedCount; i++)
                    if (_expected.Pickups[i] != _actual.Pickups[i]) { identical = false; break; }
                if (identical) return;
            }

            // Greedily consume recorded occurrences in order. The latest
            // natural match anchors recovery: older misses are reported but
            // left alone because forcing one would overwrite a later pickup.
            var occurrences = new Dictionary<string, Queue<int>>(System.StringComparer.Ordinal);
            for (var i = 0; i < expectedCount; i++)
            {
                var name = _expected.Pickups[i] ?? "";
                if (!occurrences.TryGetValue(name, out var queue)) occurrences[name] = queue = new Queue<int>();
                queue.Enqueue(i);
            }
            var cursor = 0;
            var lastNaturalExpected = -1;
            var naturalMatches = 0;
            for (var i = 0; i < actualCount; i++)
            {
                var name = _actual.Pickups[i] ?? "";
                if (!occurrences.TryGetValue(name, out var queue)) continue;
                while (queue.Count > 0 && queue.Peek() < cursor) queue.Dequeue();
                if (queue.Count == 0) continue;
                lastNaturalExpected = queue.Dequeue();
                cursor = lastNaturalExpected + 1;
                naturalMatches++;
            }

            var prefixMisses = lastNaturalExpected < 0 ? 0 : lastNaturalExpected + 1 - naturalMatches;
            if (prefixMisses > 0)
                Report.RecordPickupRecoverySkipped(LastIssued, prefixMisses,
                    "earlier recorded pickup(s) precede an item already picked naturally");

            var attempts = 0;
            var forcedAny = false;
            var failed = false;
            var lastForced = "";
            for (var i = lastNaturalExpected + 1; i < expectedCount; i++)
            {
                if (attempts >= MaxPickupRecoveriesPerFrame)
                {
                    Report.RecordPickupRecoverySkipped(LastIssued, expectedCount - i,
                        $"per-frame recovery limit ({MaxPickupRecoveriesPerFrame}) reached");
                    break;
                }

                attempts++;
                var item = _expected.Pickups[i] ?? "";
                var result = TryRecover(item);

                Report.RecordPickupRecovery(LastIssued, item, result);
                if (result.Outcome == PickupRecoveryOutcome.Failed)
                {
                    failed = true;
                    var remaining = _expected.Pickups.Count - i - 1;
                    if (remaining > 0)
                        Report.RecordPickupRecoverySkipped(LastIssued, remaining,
                            "recovery transaction stopped after a failure");
                    break;
                }
                forcedAny = true;
                lastForced = item;
            }

            // Forcing an earlier missed item can replace a later item that was
            // picked naturally. Re-equip the recording's terminal item so the
            // player's hand ends in the recorded state. A trailing unrelated
            // replay pickup needs the same repair even when no expected call
            // was missing.
            var terminal = _expected.Pickups[expectedCount - 1] ?? "";
            var actualTerminal = actualCount == 0 ? "" : _actual.Pickups[actualCount - 1] ?? "";
            if (!failed && ((forcedAny && lastForced != terminal) || (!forcedAny && actualTerminal != terminal)))
            {
                if (attempts >= MaxPickupRecoveriesPerFrame)
                    Report.RecordPickupRecoverySkipped(LastIssued, 1,
                        $"per-frame recovery limit ({MaxPickupRecoveriesPerFrame}) reached before terminal item restore");
                else
                {
                    var result = TryRecover(terminal);
                    Report.RecordPickupRecovery(LastIssued, terminal, result);
                }
            }
        }

        private static PickupRecoveryResult TryRecover(string item)
        {
            _suppressPickup = true;
            try { return _pickupRecovery.TryRecover(item); }
            catch (System.Exception e) { return PickupRecoveryResult.Failed(e.Message); }
            finally { _suppressPickup = false; }
        }

        // ---- events -------------------------------------------------------------

        public static void OnPickup(string item)
        {
            if (!Active || _suppressPickup) return;
            if (Mode == TrackMode.Recording) _recording?.Pickups.Add(item ?? "");
            else _actual.Pickups.Add(item ?? "");
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
