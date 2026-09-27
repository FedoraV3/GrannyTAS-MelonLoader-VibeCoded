using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// One interaction ray (<c>PickRay</c> or <c>DoorRay</c>) exactly as the
    /// game was about to cast it, plus the two fall flags <c>PickRay</c> gates
    /// every pickup on.
    ///
    /// Sampled at the ray's own <c>Update</c> prefix, not at the frame
    /// boundary: that is the only point where the pose the raycast will really
    /// use is known. Head bob, crouch and landing smoothing all move the camera
    /// between the frame boundary and that call.
    /// </summary>
    public sealed class RaySample
    {
        public const int Pick = 0;
        public const int Door = 1;

        public int Kind;

        /// <summary>
        /// 0 when cast in the same engine frame the macro frame was issued
        /// (the game's Update ran after the mod's), 1 when cast in a later one.
        /// A different value on replay means the script execution order changed.
        /// </summary>
        public int Order;

        public Vector3 Position;
        public Quaternion Rotation;

        public bool HasFall;
        public bool Falling;
        public bool Landing;

        /// <summary>Name of the collider the ray hits, empty for none.</summary>
        public string Hit = "";

        public RaySample Clone() => (RaySample)MemberwiseClone();
    }

    /// <summary>An enemy's pose at a frame boundary.</summary>
    public sealed class EnemySample
    {
        public string Path = "";
        public Vector3 Position;
        public Quaternion Rotation;
        public bool HasAgent;
        public Vector3 AgentVelocity;

        public EnemySample Clone() => (EnemySample)MemberwiseClone();
    }

    /// <summary>
    /// What the simulation looked like at one macro frame's boundary, and what
    /// happened between that frame and the next.
    ///
    /// Recording stores it; playback captures the same thing at the same point
    /// *before* correcting anything and compares the two. That comparison is
    /// what turns "the replay looked right" into "the replay was bit-identical
    /// on every frame", or names the first frame and the first system where it
    /// was not. See <see cref="SyncReport"/>.
    /// </summary>
    public sealed class FrameTrace
    {
        // ---- clock ----------------------------------------------------------

        public bool HasClock;

        /// <summary><c>Time.deltaTime</c> of the engine frame that issued this macro frame.</summary>
        public float Dt;

        /// <summary>Simulated seconds since the previous macro frame's boundary.</summary>
        public double Elapsed;

        /// <summary>
        /// <c>Time.timeAsDouble - Time.fixedTimeAsDouble</c>: how far into the
        /// current fixed step the frame boundary sits. Together with the deltas
        /// this decides which frames get a <c>FixedUpdate</c>.
        /// </summary>
        public double Phase;

        /// <summary>FixedUpdate steps since the previous macro frame; -1 on the first frame.</summary>
        public int FixedSteps = -1;

        // ---- player rig -----------------------------------------------------

        public bool HasCamera;
        public Vector3 CameraPosition;
        public Quaternion CameraRotation;

        public bool HasController;
        public bool Grounded;
        public Vector3 ControllerVelocity;
        public float ControllerHeight;

        public bool HasFall;
        public bool Falling;
        public bool Landing;
        public float FallDuration;

        public readonly List<EnemySample> Enemies = new List<EnemySample>();

        /// <summary>
        /// <c>UnityEngine.Random.state</c> (xorshift128 words). Equal states mean
        /// the same number of random draws happened since the seed — Granny's
        /// waypoint and voice-line picks included.
        /// </summary>
        public bool HasRng;
        public int Rng0, Rng1, Rng2, Rng3;

        // ---- events after this frame was issued -----------------------------

        public readonly List<RaySample> Rays = new List<RaySample>();
        public readonly List<string> Pickups = new List<string>();

        public FrameTrace Clone()
        {
            var fresh = new FrameTrace();
            fresh.CopyScalars(this);
            foreach (var e in Enemies) fresh.Enemies.Add(e.Clone());
            foreach (var r in Rays) fresh.Rays.Add(r.Clone());
            fresh.Pickups.AddRange(Pickups);
            return fresh;
        }

        private void CopyScalars(FrameTrace o)
        {
            HasClock = o.HasClock; Dt = o.Dt; Elapsed = o.Elapsed; Phase = o.Phase; FixedSteps = o.FixedSteps;
            HasCamera = o.HasCamera; CameraPosition = o.CameraPosition; CameraRotation = o.CameraRotation;
            HasController = o.HasController; Grounded = o.Grounded; ControllerVelocity = o.ControllerVelocity;
            ControllerHeight = o.ControllerHeight;
            HasFall = o.HasFall; Falling = o.Falling; Landing = o.Landing; FallDuration = o.FallDuration;
            HasRng = o.HasRng; Rng0 = o.Rng0; Rng1 = o.Rng1; Rng2 = o.Rng2; Rng3 = o.Rng3;
        }

        // ---- text encoding ------------------------------------------------------
        //
        // One '|'-free field of ';'-separated tagged groups, so new groups can be
        // added without breaking older readers (unknown tags are skipped).
        //
        //   t:dt,elapsed,phase,steps       c:px,py,pz,qx,qy,qz,qw
        //   k:grounded,vx,vy,vz,height     f:falling,landing,fallDuration
        //   e:path,px,py,pz,qx,qy,qz,qw[,vx,vy,vz]
        //   g:s0,s1,s2,s3                  (Random.state)
        //   r:kind,order,px,py,pz,qx,qy,qz,qw,fall,hit
        //   p:item
        //
        // Strings are base64 so names cannot collide with the separators.

        public string Encode()
        {
            var groups = new List<string>();
            if (HasClock)
                groups.Add("t:" + J(F(Dt), D(Elapsed), D(Phase), I(FixedSteps)));
            if (HasCamera)
                groups.Add("c:" + J(V(CameraPosition), Q(CameraRotation)));
            if (HasController)
                groups.Add("k:" + J(B(Grounded), V(ControllerVelocity), F(ControllerHeight)));
            if (HasFall)
                groups.Add("f:" + J(B(Falling), B(Landing), F(FallDuration)));
            if (HasRng)
                groups.Add("g:" + J(I(Rng0), I(Rng1), I(Rng2), I(Rng3)));
            foreach (var e in Enemies)
                groups.Add("e:" + (e.HasAgent
                    ? J(S(e.Path), V(e.Position), Q(e.Rotation), V(e.AgentVelocity))
                    : J(S(e.Path), V(e.Position), Q(e.Rotation))));
            foreach (var r in Rays)
            {
                var fall = r.HasFall ? (r.Falling ? 1 : 0) | (r.Landing ? 2 : 0) : -1;
                groups.Add("r:" + J(I(r.Kind), I(r.Order), V(r.Position), Q(r.Rotation), I(fall), S(r.Hit)));
            }
            foreach (var p in Pickups) groups.Add("p:" + S(p));
            return groups.Count == 0 ? "-" : string.Join(";", groups);
        }

        public static FrameTrace Decode(string text)
        {
            if (text == "-") return null;
            if (string.IsNullOrEmpty(text)) throw new FormatException("Trace field is empty.");

            var trace = new FrameTrace();
            foreach (var group in text.Split(';'))
            {
                if (group.Length < 2 || group[1] != ':') throw new FormatException("Malformed trace group.");
                var v = group.Substring(2).Split(',');
                switch (group[0])
                {
                    case 't':
                        Need(v, 4);
                        trace.HasClock = true;
                        trace.Dt = PF(v[0]); trace.Elapsed = PD(v[1]); trace.Phase = PD(v[2]); trace.FixedSteps = PI(v[3]);
                        break;
                    case 'c':
                        Need(v, 7);
                        trace.HasCamera = true;
                        trace.CameraPosition = PV(v, 0); trace.CameraRotation = PQ(v, 3);
                        break;
                    case 'k':
                        Need(v, 5);
                        trace.HasController = true;
                        trace.Grounded = PB(v[0]); trace.ControllerVelocity = PV(v, 1); trace.ControllerHeight = PF(v[4]);
                        break;
                    case 'f':
                        Need(v, 3);
                        trace.HasFall = true;
                        trace.Falling = PB(v[0]); trace.Landing = PB(v[1]); trace.FallDuration = PF(v[2]);
                        break;
                    case 'e':
                        if (v.Length != 8 && v.Length != 11) throw new FormatException("Expected eight or eleven enemy values.");
                        var e = new EnemySample { Path = PS(v[0]), Position = PV(v, 1), Rotation = PQ(v, 4) };
                        if (v.Length == 11) { e.HasAgent = true; e.AgentVelocity = PV(v, 8); }
                        trace.Enemies.Add(e);
                        break;
                    case 'r':
                        Need(v, 11);
                        var fall = PI(v[9]);
                        if (fall < -1 || fall > 3) throw new FormatException("Invalid ray fall flags.");
                        var kind = PI(v[0]);
                        var order = PI(v[1]);
                        if (kind < 0 || kind > 1 || order < 0 || order > 1) throw new FormatException("Invalid ray kind or order.");
                        trace.Rays.Add(new RaySample
                        {
                            Kind = kind, Order = order, Position = PV(v, 2), Rotation = PQ(v, 5),
                            HasFall = fall >= 0, Falling = fall > 0 && (fall & 1) != 0, Landing = fall > 0 && (fall & 2) != 0,
                            Hit = PS(v[10]),
                        });
                        break;
                    case 'p':
                        Need(v, 1);
                        trace.Pickups.Add(PS(v[0]));
                        break;
                    case 'g':
                        Need(v, 4);
                        trace.HasRng = true;
                        trace.Rng0 = PI(v[0]); trace.Rng1 = PI(v[1]); trace.Rng2 = PI(v[2]); trace.Rng3 = PI(v[3]);
                        break;
                    // Unknown tags are ignored so a newer build's traces still load.
                }
            }
            return trace;
        }

        private static void Need(string[] v, int n)
        {
            if (v.Length != n) throw new FormatException($"Expected {n} trace values, found {v.Length}.");
        }

        private static string J(params string[] parts) => string.Join(",", parts);
        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string B(bool v) => v ? "1" : "0";
        private static string V(Vector3 v) => J(F(v.x), F(v.y), F(v.z));
        private static string Q(Quaternion q) => J(F(q.x), F(q.y), F(q.z), F(q.w));
        private static string S(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));

        private static float PF(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                ? v : throw new FormatException($"Invalid finite number '{s}'.");

        private static double PD(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
                ? v : throw new FormatException($"Invalid finite number '{s}'.");

        private static int PI(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : throw new FormatException($"Invalid integer '{s}'.");

        private static bool PB(string s) => s == "1" ? true : s == "0" ? false
            : throw new FormatException($"Expected 0 or 1, found '{s}'.");

        private static Vector3 PV(string[] v, int o) => new Vector3(PF(v[o]), PF(v[o + 1]), PF(v[o + 2]));

        private static Quaternion PQ(string[] v, int o)
        {
            var q = new Quaternion(PF(v[o]), PF(v[o + 1]), PF(v[o + 2]), PF(v[o + 3]));
            var m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!float.IsFinite(m) || m < 1e-12f) throw new FormatException("Trace quaternion must have a finite, non-zero magnitude.");
            return q;
        }

        private static string PS(string s)
        {
            try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s)); }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            { throw new FormatException("Invalid trace string encoding.", e); }
        }
    }
}
