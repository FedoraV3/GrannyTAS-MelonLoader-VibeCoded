using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyTAS
{
    /// <summary>One captured transform, plus the player's look pitch if it is the player.</summary>
    public sealed class EntitySnapshot
    {
        public string Path;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool IsPlayer;
        public float RotationX;
        public Quaternion CameraLocalRotation;
        public bool HasCameraRotation;

        public string Encode()
        {
            var sb = new StringBuilder();
            sb.Append(IsPlayer ? "player" : "entity").Append('=').Append(Path).Append('|');
            sb.Append(F(Position.x)).Append(',').Append(F(Position.y)).Append(',').Append(F(Position.z)).Append('|');
            sb.Append(F(Rotation.x)).Append(',').Append(F(Rotation.y)).Append(',')
              .Append(F(Rotation.z)).Append(',').Append(F(Rotation.w));
            if (IsPlayer)
            {
                sb.Append('|').Append(F(RotationX));
                if (HasCameraRotation)
                    sb.Append('|').Append(F(CameraLocalRotation.x)).Append(',').Append(F(CameraLocalRotation.y))
                      .Append(',').Append(F(CameraLocalRotation.z)).Append(',').Append(F(CameraLocalRotation.w));
            }
            return sb.ToString();
        }

        public static EntitySnapshot Decode(string key, string value)
        {
            var parts = value.Split('|');
            if ((key == "player" ? parts.Length != 4 && parts.Length != 5 : parts.Length != 3) ||
                string.IsNullOrWhiteSpace(parts[0]))
                throw new FormatException("Malformed entity snapshot.");

            var p = parts[1].Split(',');
            var r = parts[2].Split(',');
            if (p.Length != 3 || r.Length != 4) throw new FormatException("Malformed entity transform.");

            var snap = new EntitySnapshot
            {
                Path = parts[0],
                IsPlayer = key == "player",
                Position = new Vector3(P(p[0]), P(p[1]), P(p[2])),
                Rotation = new Quaternion(P(r[0]), P(r[1]), P(r[2]), P(r[3])),
            };

            if (snap.IsPlayer && parts.Length > 3) snap.RotationX = P(parts[3]);
            if (snap.IsPlayer && parts.Length == 5)
            {
                var c = parts[4].Split(',');
                if (c.Length != 4) throw new FormatException("Malformed player camera rotation.");
                snap.CameraLocalRotation = Q(c);
                snap.HasCameraRotation = true;
            }
            return snap;
        }

        private static Quaternion Q(string[] v)
        {
            var q = new Quaternion(P(v[0]), P(v[1]), P(v[2]), P(v[3]));
            var magnitudeSquared = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!float.IsFinite(magnitudeSquared) || magnitudeSquared < 1e-12f)
                throw new FormatException("Snapshot quaternion must have a finite, non-zero magnitude.");
            return q;
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static float P(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                ? v : throw new FormatException($"Invalid snapshot number '{s}'.");
    }

    /// <summary>
    /// Where everything that matters stood when recording began.
    ///
    /// Replaying inputs only reproduces a run if the run starts from the same
    /// place. Granny in particular roams, so pressing play with her halfway
    /// across the house replays your inputs against a completely different
    /// world and the run is meaningless.
    ///
    /// This is a *positional* restore, not a full state restore: transforms plus
    /// the player's look pitch, with AI decision state reset. It cannot recover
    /// internal AI timers, animation phase, or which doors you had already
    /// opened. That is a deliberate limit — capturing those would mean
    /// serializing most of the game. For the normal case (start recording, play,
    /// replay) it puts the world back where it was.
    /// </summary>
    public sealed class WorldSnapshot
    {
        public readonly List<EntitySnapshot> Entities = new List<EntitySnapshot>();

        public bool IsEmpty => Entities.Count == 0;

        /// <summary>
        /// Component types worth restoring. Everything that moves under its own
        /// power and can therefore invalidate a replay.
        /// </summary>
        internal static void ForEachTrackedEnemy(Action<Component> visit)
        {
            Visit<AI_Granny>(visit);
            Visit<AI_Grandpa>(visit);
            Visit<AI_MomSpider>(visit);
            Visit<AI_Slendrina>(visit);
            Visit<AtticSpider>(visit);
            Visit<RatEnemy>(visit);
            Visit<SnowManAI>(visit);
        }

        private static void Visit<T>(Action<Component> visit) where T : Component
        {
            try
            {
                var found = UnityEngine.Object.FindObjectsOfType<T>();
                if (found == null) return;
                foreach (var c in found)
                    if (c != null) visit(c);
            }
            catch
            {
                // A type absent from this scene is normal, not an error.
            }
        }

        public static WorldSnapshot Capture()
        {
            var snap = new WorldSnapshot();

            try
            {
                var player = UnityEngine.Object.FindObjectOfType<MobileFPS>();
                if (player != null)
                {
                    var t = player.transform;
                    snap.Entities.Add(new EntitySnapshot
                    {
                        Path = HierarchyPath(t),
                        Position = t.position,
                        Rotation = t.rotation,
                        IsPlayer = true,
                        RotationX = player.rotationX,
                        CameraLocalRotation = player.playerCamera != null
                            ? player.playerCamera.localRotation : Quaternion.identity,
                        HasCameraRotation = player.playerCamera != null,
                    });
                }
            }
            catch (Exception)
            {
                // Menus have no player; the snapshot is simply empty.
            }

            ForEachTrackedEnemy(c =>
            {
                var t = c.transform;
                snap.Entities.Add(new EntitySnapshot
                {
                    Path = HierarchyPath(t),
                    Position = t.position,
                    Rotation = t.rotation,
                });
            });

            return snap;
        }

        /// <summary>
        /// Put everything back. Returns how many entities were restored.
        ///
        /// Two passes: the first only resolves and checks, the second writes.
        /// Resolving every hierarchy path up front means a missing late entity
        /// can't leave the player and earlier enemies already teleported into a
        /// world that playback will refuse to use. The enemy pre-pass goes
        /// further and confirms each recorded position is actually reachable —
        /// agent non-null/enabled and <c>NavMesh.SamplePosition</c> succeeding —
        /// before any write happens, so a <c>NavMeshAgent.Warp</c> failure can't
        /// throw after earlier entities in the loop were already moved. The
        /// Warp call itself, and its throw, stay as a backstop for whatever the
        /// pre-pass cannot predict.
        /// </summary>
        public int Restore()
        {
            var restored = 0;
            var targets = new List<GameObject>(Entities.Count);

            // Resolve every hierarchy path first. A missing late entity must not
            // leave the player and earlier enemies already teleported into a
            // world that playback will refuse to use.
            foreach (var e in Entities)
            {
                var target = GameObject.Find(e.Path);
                if (target == null) return 0;
                targets.Add(target);
            }

            // Pre-pass: check every enemy's agent can actually reach its
            // recorded position before writing anything.
            for (var i = 0; i < Entities.Count; i++)
            {
                var e = Entities[i];
                if (e.IsPlayer) continue;
                var go = targets[i];

                NavMeshAgent agent = null;
                try { agent = go.GetComponent<NavMeshAgent>(); } catch { }
                if (agent == null || !agent.enabled) continue;

                if (!NavMesh.SamplePosition(e.Position, out _, 0.5f, NavMesh.AllAreas))
                    return 0;
            }

            for (var i = 0; i < Entities.Count; i++)
            {
                var e = Entities[i];
                var go = targets[i];

                if (e.IsPlayer) RestorePlayer(go, e);
                else RestoreEnemy(go, e);

                restored++;
            }

            return restored;
        }

        private static void RestorePlayer(GameObject go, EntitySnapshot e)
        {
            // A CharacterController resolves collisions against the position it
            // already believes it has, so writing transform.position underneath
            // it gets partly undone. MoveCharacter makes the write stick without
            // rebuilding the controller (which would throw away its grounded
            // state — see VirtualInput.TeleportByToggle).
            go.transform.rotation = e.Rotation;
            VirtualInput.MoveCharacter(go, e.Position);

            try
            {
                var fps = go.GetComponent<MobileFPS>();
                if (fps != null)
                {
                    fps.rotationX = e.RotationX;
                    if (e.HasCameraRotation && fps.playerCamera != null)
                        fps.playerCamera.localRotation = e.CameraLocalRotation;
                }
            }
            catch { }
        }

        private static void RestoreEnemy(GameObject go, EntitySnapshot e)
        {
            // A NavMeshAgent owns its position and will drag the transform back
            // to where it thinks it is on the next tick. Warp is the supported
            // way to move one.
            NavMeshAgent agent = null;
            try { agent = go.GetComponent<NavMeshAgent>(); } catch { }

            if (agent != null && agent.enabled)
            {
                if (!agent.Warp(e.Position))
                    throw new InvalidOperationException($"Cannot restore NavMesh agent '{e.Path}'.");

                try { agent.ResetPath(); } catch { }
            }
            else
            {
                go.transform.position = e.Position;
            }

            go.transform.rotation = e.Rotation;

            // Clear whatever the AI was in the middle of deciding, so it
            // re-evaluates from the restored position instead of continuing to
            // chase a destination from the old one.
            TryResetAi(go);
        }

        private static void TryResetAi(GameObject go)
        {
            try
            {
                var granny = go.GetComponent<AI_Granny>();
                if (granny != null) { granny.StopChase(); granny.ResetAIDecision(); return; }
            }
            catch { }

            try
            {
                var grandpa = go.GetComponent<AI_Grandpa>();
                if (grandpa != null) { grandpa.StopChase(); grandpa.ResetAIDecision(); }
            }
            catch { }
        }

        /// <summary>
        /// Full hierarchy path, since scenes routinely contain several objects
        /// sharing a name and a bare name would restore the wrong one.
        /// </summary>
        internal static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return "/" + sb;
        }

        public IEnumerable<string> Encode()
        {
            foreach (var e in Entities) yield return e.Encode();
        }

        public void AddDecoded(string key, string value)
        {
            var e = EntitySnapshot.Decode(key, value);
            if (e != null) Entities.Add(e);
        }
    }
}
