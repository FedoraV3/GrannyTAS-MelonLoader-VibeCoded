using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// The answer to "was the replay the same run?", area by area.
    ///
    /// Every comparison is made on state captured *before* the replay corrects
    /// anything, so a correction can never make a divergence disappear from
    /// this report — it only stops the divergence from compounding. "Same"
    /// means the same bits, not "close": a TAS either reproduces or it drifts,
    /// and the first frame it drifts on is the one worth knowing.
    ///
    /// The two exceptions are times measured as differences of absolute engine
    /// clocks (<see cref="Area.Clock"/>), which are compared to within
    /// <see cref="ClockTolerance"/>: a recording and its replay run at
    /// different absolute <c>Time.time</c>, so the same increment rounds
    /// differently in the last bits of a double.
    /// </summary>
    public sealed class SyncReport
    {
        public enum Area
        {
            Clock, FixedSteps, Position, Look, Camera, Controller, Fall, Enemies,
            Rigidbodies, RayPose, RayHit, EffectiveRayHit, Pickups, Random, ScriptOrder,
        }

        private static readonly string[] Names =
        {
            "clock", "fixed-step pattern", "player position", "look pose", "camera", "controller",
            "fall flags", "enemies", "rigidbodies", "ray pose", "ray hit (natural)", "ray hit (as cast)",
            "pickups", "random state", "script order",
        };

        public sealed class Stat
        {
            public int Compared;
            public int Differing;
            public int FirstFrame = -1;
            public double Worst;
            public string WorstNote = "";
        }

        /// <summary>Seconds. See the class remarks.</summary>
        public const double ClockTolerance = 1e-10;

        private const int MaxDetails = 200;

        private readonly Stat[] _stats;
        private readonly List<string> _details = new List<string>();
        private int _detailsDropped;

        public string MacroName = "";
        public int MacroFrames;
        public int FramesReplayed;

        /// <summary>Whether the macro carries per-frame traces (v5). Without them only drift is measured.</summary>
        public bool TracesPresent;

        /// <summary>Fixed-step phase error at frame 0, seconds; NaN when not aligned.</summary>
        public double StartPhaseError = double.NaN;

        public string RigNote = "";
        public string TeleportNote = "";
        public int PositionCorrections;
        public int RigidbodyCorrections;
        public int RayPins;
        public int ItemSteers;
        public int ClickPins;
        public int ForcedPickups;
        public int ForceFailures;
        public int FlagPins;
        public int MissingRigidbodies;
        public string FirstMissingRigidbody = "";
        public int UnmatchedRays;

        public SyncReport()
        {
            _stats = new Stat[Names.Length];
            for (var i = 0; i < _stats.Length; i++) _stats[i] = new Stat();
        }

        public Stat this[Area area] => _stats[(int)area];

        public static string NameOf(Area area) => Names[(int)area];

        public void Match(Area area) => _stats[(int)area].Compared++;

        public void Differ(Area area, int frame, double magnitude, string note)
        {
            var s = _stats[(int)area];
            s.Compared++;
            s.Differing++;
            if (s.FirstFrame < 0 || frame < s.FirstFrame) s.FirstFrame = frame;
            if (s.Differing == 1 || (double.IsFinite(magnitude) && magnitude > s.Worst))
            {
                s.Worst = double.IsFinite(magnitude) ? magnitude : 0;
                s.WorstNote = $"f{frame}: {note}";
            }
            if (_details.Count < MaxDetails) _details.Add($"f{frame} {NameOf(area)}: {note}");
            else _detailsDropped++;
        }

        public void CountRayPin() => RayPins++;
        public void CountFlagPin() => FlagPins++;

        // ---- comparisons ----------------------------------------------------

        /// <summary>
        /// Compare one frame boundary. <paramref name="expected"/> is the
        /// recorded frame; <paramref name="actualTrace"/> and
        /// <paramref name="actualPose"/> were captured the same way on replay,
        /// before any correction was applied.
        /// </summary>
        public void CompareBoundary(int frame, InputFrame expected, FrameTrace actualTrace, InputFrame actualPose)
        {
            var e = expected.Trace;

            if (expected.HasPosition && actualPose.HasPosition)
            {
                if (Bits.Same(expected.PlayerPosition, actualPose.PlayerPosition)) Match(Area.Position);
                else
                {
                    var d = Bits.Distance(expected.PlayerPosition, actualPose.PlayerPosition);
                    Differ(Area.Position, frame, d, $"off by {Mm(d)} ({V(expected.PlayerPosition)} vs {V(actualPose.PlayerPosition)})");
                }
            }

            if (expected.HasPose && actualPose.HasPose)
            {
                if (Bits.Same(expected.PlayerRotation, actualPose.PlayerRotation) &&
                    Bits.Same(expected.CameraLocalRotation, actualPose.CameraLocalRotation) &&
                    Bits.Same(expected.RotationX, actualPose.RotationX))
                    Match(Area.Look);
                else
                {
                    var a = Math.Max(Bits.Angle(expected.PlayerRotation, actualPose.PlayerRotation),
                        Bits.Angle(expected.CameraLocalRotation, actualPose.CameraLocalRotation));
                    Differ(Area.Look, frame, a, $"off by {Deg(a)} (pitch {F(expected.RotationX)} vs {F(actualPose.RotationX)})");
                }
            }

            if (e == null || actualTrace == null) return;

            if (e.HasClock && actualTrace.HasClock)
            {
                var clockOk = Bits.Same(e.Dt, actualTrace.Dt);
                var phaseError = Math.Abs(e.Phase - actualTrace.Phase);
                clockOk &= phaseError <= ClockTolerance;
                double elapsedError = 0;
                if (e.FixedSteps >= 0 && actualTrace.FixedSteps >= 0)
                {
                    elapsedError = Math.Abs(e.Elapsed - actualTrace.Elapsed);
                    clockOk &= elapsedError <= ClockTolerance;
                }
                if (clockOk) Match(Area.Clock);
                else
                    Differ(Area.Clock, frame, Math.Max(phaseError, elapsedError),
                        $"dt {F(e.Dt)} vs {F(actualTrace.Dt)}, phase off by {Ns(phaseError)}, elapsed off by {Ns(elapsedError)}");

                if (e.FixedSteps >= 0 && actualTrace.FixedSteps >= 0)
                {
                    if (e.FixedSteps == actualTrace.FixedSteps) Match(Area.FixedSteps);
                    else Differ(Area.FixedSteps, frame, Math.Abs(e.FixedSteps - actualTrace.FixedSteps),
                        $"recorded {e.FixedSteps} FixedUpdate step(s), replay ran {actualTrace.FixedSteps}");
                }
            }

            if (e.HasCamera && actualTrace.HasCamera)
            {
                if (Bits.Same(e.CameraPosition, actualTrace.CameraPosition) && Bits.Same(e.CameraRotation, actualTrace.CameraRotation))
                    Match(Area.Camera);
                else
                {
                    var d = Bits.Distance(e.CameraPosition, actualTrace.CameraPosition);
                    var a = Bits.Angle(e.CameraRotation, actualTrace.CameraRotation);
                    Differ(Area.Camera, frame, d, $"off by {Mm(d)} and {Deg(a)}");
                }
            }

            if (e.HasController && actualTrace.HasController)
            {
                if (e.Grounded == actualTrace.Grounded && Bits.Same(e.ControllerVelocity, actualTrace.ControllerVelocity) &&
                    Bits.Same(e.ControllerHeight, actualTrace.ControllerHeight))
                    Match(Area.Controller);
                else
                {
                    var dv = Bits.Distance(e.ControllerVelocity, actualTrace.ControllerVelocity);
                    Differ(Area.Controller, frame, dv,
                        $"grounded {B(e.Grounded)} vs {B(actualTrace.Grounded)}, velocity off by {F((float)dv)} m/s, " +
                        $"height {F(e.ControllerHeight)} vs {F(actualTrace.ControllerHeight)}");
                }
            }

            if (e.HasFall && actualTrace.HasFall)
            {
                if (e.Falling == actualTrace.Falling && e.Landing == actualTrace.Landing &&
                    Bits.Same(e.FallDuration, actualTrace.FallDuration))
                    Match(Area.Fall);
                else
                    Differ(Area.Fall, frame, Math.Abs((double)e.FallDuration - actualTrace.FallDuration),
                        $"falling {B(e.Falling)} vs {B(actualTrace.Falling)}, landing {B(e.Landing)} vs {B(actualTrace.Landing)}, " +
                        $"fall timer {F(e.FallDuration)} vs {F(actualTrace.FallDuration)}");
            }

            if (e.HasRng && actualTrace.HasRng)
            {
                if (e.Rng0 == actualTrace.Rng0 && e.Rng1 == actualTrace.Rng1 && e.Rng2 == actualTrace.Rng2 && e.Rng3 == actualTrace.Rng3)
                    Match(Area.Random);
                else
                    Differ(Area.Random, frame, 1,
                        "UnityEngine.Random state differs: something drew a different number of random values " +
                        "since the previous frame (Granny's waypoint or voice-line pick, most likely)");
            }

            if (e.Enemies.Count > 0) CompareEnemies(frame, e.Enemies, actualTrace.Enemies);
        }

        private void CompareEnemies(int frame, List<EnemySample> expected, List<EnemySample> actual)
        {
            var used = new bool[actual.Count];
            var differing = new List<string>();
            double worst = 0;

            foreach (var e in expected)
            {
                var index = -1;
                for (var i = 0; i < actual.Count; i++)
                    if (!used[i] && actual[i].Path == e.Path) { index = i; break; }
                if (index < 0)
                {
                    differing.Add($"{e.Path} missing");
                    worst = Math.Max(worst, double.PositiveInfinity);
                    continue;
                }
                used[index] = true;
                var a = actual[index];
                var same = Bits.Same(e.Position, a.Position) && Bits.Same(e.Rotation, a.Rotation) &&
                           (!e.HasAgent || !a.HasAgent || Bits.Same(e.AgentVelocity, a.AgentVelocity));
                if (same) continue;
                var d = Bits.Distance(e.Position, a.Position);
                worst = Math.Max(worst, d);
                differing.Add($"{e.Path} off by {Mm(d)} and {Deg(Bits.Angle(e.Rotation, a.Rotation))}");
            }

            if (differing.Count == 0) Match(Area.Enemies);
            else Differ(Area.Enemies, frame, double.IsFinite(worst) ? worst : 0, string.Join("; ", differing));
        }

        /// <summary>
        /// Compare one interaction ray at the moment it was cast.
        /// <paramref name="effectiveHit"/> is what the ray hit as the game
        /// actually cast it — after pinning, when a pin was applied.
        /// </summary>
        public void CompareRay(int frame, RaySample expected, RaySample actual, string effectiveHit)
        {
            if (expected == null)
            {
                UnmatchedRays++;
                return;
            }

            var kind = expected.Kind == RaySample.Door ? "DoorRay" : "PickRay";

            if (expected.Order == actual.Order) Match(Area.ScriptOrder);
            else Differ(Area.ScriptOrder, frame, 1,
                $"{kind} ran {(actual.Order == 0 ? "after" : "before")} the mod's frame update on replay, " +
                $"{(expected.Order == 0 ? "after" : "before")} it when recorded");

            var fallSame = !expected.HasFall || !actual.HasFall ||
                           (expected.Falling == actual.Falling && expected.Landing == actual.Landing);
            if (Bits.Same(expected.Position, actual.Position) && Bits.Same(expected.Rotation, actual.Rotation) && fallSame)
                Match(Area.RayPose);
            else
            {
                var d = Bits.Distance(expected.Position, actual.Position);
                var note = $"{kind} origin off by {Mm(d)}, aim off by {Deg(Bits.Angle(expected.Rotation, actual.Rotation))}";
                if (!fallSame)
                    note += $", falling/landing {B(expected.Falling)}/{B(expected.Landing)} vs {B(actual.Falling)}/{B(actual.Landing)}";
                Differ(Area.RayPose, frame, d, note);
            }

            if (expected.Hit == actual.Hit) Match(Area.RayHit);
            else Differ(Area.RayHit, frame, 1, $"{kind} hit '{Show(expected.Hit)}' when recorded, '{Show(actual.Hit)}' on replay");

            if (expected.Hit == effectiveHit) Match(Area.EffectiveRayHit);
            else Differ(Area.EffectiveRayHit, frame, 1,
                $"{kind} hit '{Show(expected.Hit)}' when recorded, '{Show(effectiveHit)}' as cast on replay " +
                "(the world under the ray differs, not the ray)");
        }

        /// <summary>
        /// Compare what happened between one frame's issue and the next's.
        /// <paramref name="forced"/> are pickups in <paramref name="actual"/>
        /// the mod made after the game's own pickup missed them: they are
        /// compared as the misses they were.
        /// </summary>
        public void CompareEvents(int frame, FrameTrace expected, FrameTrace actual, int[] raysConsumed, bool compareRays = true,
            IReadOnlyList<string> forced = null)
        {
            if (expected == null || actual == null) return;

            var natural = actual.Pickups;
            if (forced != null && forced.Count > 0)
            {
                natural = new List<string>(actual.Pickups);
                foreach (var item in forced) natural.Remove(item);
            }
            if (SequenceEqual(expected.Pickups, natural))
            {
                if (expected.Pickups.Count > 0) Match(Area.Pickups);
            }
            else
                Differ(Area.Pickups, frame, Math.Abs(expected.Pickups.Count - natural.Count),
                    $"recorded [{string.Join(", ", expected.Pickups)}], replay [{string.Join(", ", natural)}]" +
                    (forced != null && forced.Count > 0 ? $", forced [{string.Join(", ", forced)}]" : ""));

            if (!compareRays) return;
            for (var kind = 0; kind < 2; kind++)
            {
                var recorded = 0;
                foreach (var r in expected.Rays) if (r.Kind == kind) recorded++;
                var cast = raysConsumed != null && kind < raysConsumed.Length ? raysConsumed[kind] : 0;
                if (recorded > cast)
                    Differ(Area.RayPose, frame, recorded - cast,
                        $"{(kind == RaySample.Door ? "DoorRay" : "PickRay")} was cast {recorded} time(s) when recorded, {cast} on replay");
            }
        }

        /// <summary>A recorded item was put under the pinned pickup ray for one call.</summary>
        public void RecordItemSteer(int frame, string item, float distance)
        {
            ItemSteers++;
            Detail(distance > 0f
                ? string.Format(CultureInfo.InvariantCulture,
                    "f{0} pickup ray: '{1}' moved {2:0.0} cm under the recorded ray for one call", frame, item, distance * 100f)
                : $"f{frame} pickup ray: '{item}' reached by syncing its collider to its transform");
        }

        /// <summary>A recorded item the pickup ray missed was left where the replay has it.</summary>
        public void RecordItemLeft(int frame, string item, string reason) =>
            Detail($"f{frame} pickup ray: '{item}' not steered: {reason}");

        /// <summary>
        /// The game would have dropped the recorded click (no ring from the
        /// previous cast, or no interact edge), so the click was handed over.
        /// </summary>
        public void RecordClickPin(int frame, string item)
        {
            ClickPins++;
            Detail($"f{frame} pickup ray: recorded click on '{item}' handed to PickRay (the game would have dropped it)");
        }

        /// <summary>The game's own pickup missed a recorded item, so the mod picked it up after the call.</summary>
        public void RecordPickupForced(int frame, string item, string note)
        {
            ForcedPickups++;
            Detail($"f{frame} pickup forced: '{item}' put in hand after the game's pickup missed it" +
                   (string.IsNullOrEmpty(note) ? "" : $" ({note})"));
        }

        /// <summary>A recorded pickup that even forcing could not make.</summary>
        public void RecordPickupForceFailed(int frame, string item, string reason)
        {
            ForceFailures++;
            Detail($"f{frame} pickup NOT forced: '{item}': {reason}");
        }

        private void Detail(string text)
        {
            if (_details.Count < MaxDetails) _details.Add(text);
            else _detailsDropped++;
        }

        private static bool SequenceEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---- verdict ----------------------------------------------------------

        public bool Exact
        {
            get
            {
                foreach (var s in _stats) if (s.Differing > 0) return false;
                return MissingRigidbodies == 0;
            }
        }

        public bool FirstDivergence(out int frame, out Area area)
        {
            frame = int.MaxValue;
            area = Area.Clock;
            for (var i = 0; i < _stats.Length; i++)
            {
                var s = _stats[i];
                if (s.Differing > 0 && s.FirstFrame < frame) { frame = s.FirstFrame; area = (Area)i; }
            }
            return frame != int.MaxValue;
        }

        /// <summary>One line for the log and the panel.</summary>
        public string Summary()
        {
            if (!TracesPresent)
                return "sync: not checked — this macro predates sync traces; re-record it for a full check";
            if (FramesReplayed == 0) return "sync: no frames replayed";
            if (Exact)
                return $"sync: BIT-IDENTICAL over {FramesReplayed} frames" +
                       (RayPins + FlagPins + ItemSteers + ClickPins + PositionCorrections + RigidbodyCorrections > 0
                           ? " (after corrections)" : "");
            if (!FirstDivergence(out var frame, out var area))
                return $"sync: diverged — {MissingRigidbodies} recorded rigidbodies missing on replay";
            var areas = 0;
            foreach (var s in _stats) if (s.Differing > 0) areas++;
            return $"sync: diverged at frame {frame} ({NameOf(area)}: {this[area].WorstNote}); " +
                   $"{areas} area(s) differ";
        }

        public List<string> Lines()
        {
            var lines = new List<string>
            {
                "GrannyTAS replay sync report",
                $"macro      {(string.IsNullOrEmpty(MacroName) ? "(unsaved recording)" : MacroName)} ({MacroFrames} frames)",
                $"replayed   {FramesReplayed} frames at 1x",
                "verdict    " + Summary(),
                "start      " + (double.IsNaN(StartPhaseError)
                    ? "fixed-step phase not aligned (macro has no recorded phase)"
                    : $"fixed-step phase aligned to within {Ns(StartPhaseError)}"),
            };
            if (!string.IsNullOrEmpty(RigNote)) lines.Add("rig        " + RigNote);
            if (!string.IsNullOrEmpty(TeleportNote)) lines.Add("teleport   " + TeleportNote);
            lines.Add("");

            if (TracesPresent)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0,-22}{1,10}{2,11}{3,8}  {4}",
                    "area", "compared", "differing", "first", "worst"));
                for (var i = 0; i < _stats.Length; i++)
                {
                    var s = _stats[i];
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0,-22}{1,10}{2,11}{3,8}  {4}",
                        Names[i], s.Compared, s.Differing, s.FirstFrame < 0 ? "-" : s.FirstFrame.ToString(CultureInfo.InvariantCulture),
                        s.Differing > 0 ? s.WorstNote : ""));
                }
                lines.Add("");
            }
            else
            {
                lines.Add("This macro was recorded before per-frame sync traces existed, so only the");
                lines.Add("player-position and rigidbody corrections below were measured. Re-record it");
                lines.Add("with this build to get the full per-area comparison.");
                lines.Add("");
            }

            lines.Add("corrections applied on replay (each one is a divergence that was steered back):");
            lines.Add($"  player position   {PositionCorrections} frame(s)");
            lines.Add($"  rigidbodies       {RigidbodyCorrections} body-frame(s)");
            lines.Add($"  interaction rays  {RayPins} cast(s) pinned to the recorded pose");
            lines.Add($"  fall flags        {FlagPins} PickRay call(s) given the recorded flags");
            lines.Add($"  pickup items      {ItemSteers} cast(s) given the recorded item under the ray");
            lines.Add($"  pickup clicks     {ClickPins} cast(s) given the recorded click the game would have dropped");
            lines.Add($"  forced pickups    {ForcedPickups} item(s) picked up after the game's pickup missed them" +
                      (ForceFailures > 0 ? $", {ForceFailures} could not be" : ""));
            if (MissingRigidbodies > 0)
                lines.Add($"  missing bodies    {MissingRigidbodies} (first: {FirstMissingRigidbody})");
            if (UnmatchedRays > 0)
                lines.Add($"  unmatched rays    {UnmatchedRays} cast(s) with no recorded counterpart (not a divergence on its own)");
            lines.Add("");

            if (_details.Count > 0)
            {
                lines.Add($"first differences ({_details.Count}{(_detailsDropped > 0 ? $" shown, {_detailsDropped} more" : "")}):");
                foreach (var d in _details) lines.Add("  " + d);
            }
            return lines;
        }

        // ---- formatting -------------------------------------------------------

        private static string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);
        private static string B(bool v) => v ? "1" : "0";
        private static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";
        private static string Show(string hit) => string.IsNullOrEmpty(hit) ? "nothing" : hit;

        private static string Mm(double metres) => double.IsFinite(metres)
            ? (metres * 1000.0).ToString(metres < 1e-6 ? "0.#########" : "0.####", CultureInfo.InvariantCulture) + " mm"
            : "?";

        private static string Deg(double degrees) => double.IsFinite(degrees)
            ? degrees.ToString("0.######", CultureInfo.InvariantCulture) + "°"
            : "?";

        private static string Ns(double seconds) => double.IsFinite(seconds)
            ? (seconds * 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " ns"
            : "?";
    }
}
