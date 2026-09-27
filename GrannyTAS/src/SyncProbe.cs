using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyTAS
{
    /// <summary>
    /// Reads the simulation state a <see cref="FrameTrace"/> holds, at a macro
    /// frame boundary. Read-only by construction: nothing here may change the
    /// state it is measuring, or the recording would differ from a run made
    /// without it.
    /// </summary>
    internal static class SyncProbe
    {
        private sealed class Tracked
        {
            public string Path;
            public Transform Transform;
            public NavMeshAgent Agent;
        }

        /// <summary>
        /// Reads <c>UnityEngine.Random.state</c> into four ints; set by the mod
        /// (see RandomProbe). Null or returning false: no RNG check.
        /// </summary>
        public static Func<int[], bool> ReadRandomState;

        private static List<Tracked> _enemies = new List<Tracked>();
        private static readonly int[] RandomScratch = new int[4];
        private static double _lastTime;
        private static double _lastFixedTime;
        private static bool _haveLast;

        /// <summary>
        /// Start a run. Enemies are resolved once here rather than searched for
        /// every frame; one destroyed mid-run simply stops being sampled, which
        /// the comparison reports as missing.
        /// </summary>
        public static void Begin()
        {
            _haveLast = false;
            _enemies = new List<Tracked>();
            WorldSnapshot.ForEachTrackedEnemy(c =>
            {
                var tracked = new Tracked { Transform = c.transform };
                try { tracked.Path = WorldSnapshot.HierarchyPath(c.transform); } catch { return; }
                try { tracked.Agent = c.gameObject.GetComponent<NavMeshAgent>(); } catch { }
                _enemies.Add(tracked);
            });
        }

        public static void End()
        {
            _haveLast = false;
            _enemies = new List<Tracked>();
        }

        public static void CaptureBoundary(FrameTrace trace)
        {
            CaptureClock(trace);
            CaptureRandom(trace);
            CaptureRig(trace);
            CaptureEnemies(trace);
        }

        private static void CaptureRandom(FrameTrace trace)
        {
            var read = ReadRandomState;
            if (read == null) return;
            try
            {
                if (!read(RandomScratch)) return;
                trace.Rng0 = RandomScratch[0];
                trace.Rng1 = RandomScratch[1];
                trace.Rng2 = RandomScratch[2];
                trace.Rng3 = RandomScratch[3];
                trace.HasRng = true;
            }
            catch { trace.HasRng = false; }
        }

        private static void CaptureClock(FrameTrace trace)
        {
            double now, fixedNow, fixedStep;
            try
            {
                now = Time.timeAsDouble;
                fixedNow = Time.fixedTimeAsDouble;
                fixedStep = Time.fixedDeltaTime;
            }
            catch { return; }

            trace.HasClock = true;
            trace.Dt = Time.deltaTime;
            trace.Phase = now - fixedNow;
            if (_haveLast && fixedStep > 0)
            {
                trace.Elapsed = now - _lastTime;
                trace.FixedSteps = (int)Math.Round((fixedNow - _lastFixedTime) / fixedStep);
            }
            else
            {
                trace.Elapsed = 0;
                trace.FixedSteps = -1;
            }
            _lastTime = now;
            _lastFixedTime = fixedNow;
            _haveLast = true;
        }

        private static void CaptureRig(FrameTrace trace)
        {
            var player = PlayerGate.Player;
            if (player == null) return;

            try
            {
                var camera = player.playerCamera2;
                if (camera != null)
                {
                    var t = camera.transform;
                    trace.CameraPosition = t.position;
                    trace.CameraRotation = t.rotation;
                    trace.HasCamera = true;
                }
            }
            catch { trace.HasCamera = false; }

            try
            {
                var cc = player.characterController;
                if (cc == null) cc = player.GetComponent<CharacterController>();
                if (cc != null)
                {
                    trace.Grounded = cc.isGrounded;
                    trace.ControllerVelocity = cc.velocity;
                    trace.ControllerHeight = cc.height;
                    trace.HasController = true;
                }
            }
            catch { trace.HasController = false; }

            try
            {
                var fall = player.FallingHolder;
                if (fall != null)
                {
                    trace.Falling = fall.isFalling;
                    trace.Landing = fall.isLanding;
                    trace.FallDuration = fall.fallDuration;
                    trace.HasFall = true;
                }
            }
            catch { trace.HasFall = false; }
        }

        private static void CaptureEnemies(FrameTrace trace)
        {
            foreach (var e in _enemies)
            {
                try
                {
                    var t = e.Transform;
                    if (t == null) continue;
                    var sample = new EnemySample { Path = e.Path, Position = t.position, Rotation = t.rotation };
                    var agent = e.Agent;
                    if (agent != null && agent.enabled && agent.isOnNavMesh)
                    {
                        sample.AgentVelocity = agent.velocity;
                        sample.HasAgent = true;
                    }
                    trace.Enemies.Add(sample);
                }
                catch { }
            }
        }
    }
}
