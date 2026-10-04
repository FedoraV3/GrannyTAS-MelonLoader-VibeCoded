using System;
using System.Collections.Generic;
using ImGuiNET;
using Il2Cpp;
using UnityEngine;
using UnityEngine.AI;
using Vec2 = System.Numerics.Vector2;
using Vec4 = System.Numerics.Vector4;

namespace GrannyTAS
{
    /// <summary>
    /// Shows where every enemy is, what it is doing and where it is heading, so
    /// a route can be timed against Granny's real position instead of guessed.
    ///
    /// Three views of the same data:
    ///
    /// * **In the world** — a box and label over each enemy that is on screen,
    ///   an arrow at the screen edge for each one that is not, and the enemy's
    ///   remaining nav path drawn on the floor.
    /// * **Radar** — top-down, player-relative, forward is up. Shows enemies
    ///   through walls, their facing, their vision cone and their path.
    /// * **Panel list** — distance, floor difference, state and an ETA in
    ///   simulated frames, for reading exact numbers while paused.
    ///
    /// **Read-only by construction**, like <see cref="SyncProbe"/>: everything
    /// here runs in OnGUI's repaint pass and only reads transforms, agent
    /// properties and AI fields. Nothing is written back, no game method is
    /// called and no random number is drawn, so a run recorded with the ESP on
    /// replays identically with it off, and the other way round.
    /// </summary>
    internal static class EnemyEsp
    {
        public enum Alert { Calm, Busy, Search, Sound, Chase, Caught }

        private sealed class Target
        {
            public Component Ai;
            public Transform Transform;
            public NavMeshAgent Agent;
            public string Name;
        }

        /// <summary>One enemy as it stands at this repaint.</summary>
        public sealed class Seen
        {
            public string Name;
            public Vector3 Position;
            public Vector3 Forward;
            public float Height;

            public Alert Alert;
            public string State = "";

            /// <summary>The game's own "can see the player" flag, from the enemy's Eyes component.</summary>
            public bool HasEyes;
            public bool SeesPlayer;
            public Vector3 EyePosition;
            public Vector3 EyeForward;
            public float EyeHalfFovRad;
            public float ViewRange;

            /// <summary>Agent position followed by the remaining path corners.</summary>
            public readonly List<Vector3> Path = new List<Vector3>();
            public float PathLength;
            public float AgentSpeed;

            /// <summary>Horizontal distance to the player and height relative to the player's feet.</summary>
            public float Distance;
            public float Rise;

            /// <summary>A door the enemy's door ray is on, and how long until it gives — see <see cref="DoorTimer"/>.</summary>
            public bool HasDoor;
            public DoorTimer.Rule DoorRule;
            public string DoorName = "";
            public Vector3 DoorPosition;
            public float DoorElapsed;
            public int DoorRattles;
            /// <summary>FixedUpdate steps until the door acts, counting that step; -1 unknown.</summary>
            public int DoorTicks = -1;
        }

        /// <summary>Above this height difference an enemy counts as on another floor.</summary>
        public const float FloorGap = 1.6f;

        private const float RescanInterval = 1f;
        private const float DefaultHeight = 1.8f;

        public static Action<string> Warn;

        private static List<Target> _targets = new List<Target>();
        private static readonly List<Seen> _seen = new List<Seen>();
        private static float _nextScan;
        private static bool _warned;

        private static bool _canCast;
        private static float _fixedStep;

        private static bool _havePlayer;
        private static Vector3 _playerFeet;
        private static Vector3 _viewForward;
        private static Vector3 _viewRight;

        /// <summary>What the last <see cref="Gather"/> found, nearest first.</summary>
        public static IReadOnlyList<Seen> Current => _seen;

        public static bool HasPlayer => _havePlayer;

        /// <summary>Forget resolved enemies; the next gather searches again. Call on scene load.</summary>
        public static void Invalidate()
        {
            _targets = new List<Target>();
            _seen.Clear();
            _nextScan = 0f;
        }

        // ---- gathering --------------------------------------------------------

        /// <summary>Read the current state of every enemy. Once per repaint.</summary>
        public static void Gather()
        {
            _seen.Clear();

            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                _nextScan = now + RescanInterval;
                Rescan();
            }

            _havePlayer = ReadPlayer(ViewCamera());

            // A raycast is a pure query only while transforms are not auto-synced
            // (the game's setting). With auto-sync on it would push moved
            // colliders into physics early and change what the game's own rays
            // see, so the door lookup is skipped rather than risk the run.
            try { _canCast = !Physics.autoSyncTransforms; } catch { _canCast = false; }
            try { _fixedStep = UnityEngine.Time.fixedDeltaTime; } catch { _fixedStep = 0f; }

            foreach (var t in _targets)
            {
                try
                {
                    if (t.Transform == null) continue;
                    var s = new Seen { Name = t.Name };
                    Read(t, s);
                    _seen.Add(s);
                }
                catch
                {
                    // A destroyed or half-torn-down enemy; the next rescan drops it.
                }
            }

            _seen.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        private static void Rescan()
        {
            var fresh = new List<Target>();
            WorldSnapshot.ForEachTrackedEnemy(c =>
            {
                try
                {
                    var t = new Target { Ai = c, Transform = c.transform, Name = NameOf(c) };
                    try { t.Agent = c.gameObject.GetComponent<NavMeshAgent>(); } catch { }
                    fresh.Add(t);
                }
                catch { }
            });
            _targets = fresh;
        }

        private static string NameOf(Component c) => c switch
        {
            AI_Granny => "Granny",
            AI_Grandpa => "Grandpa",
            AI_MomSpider => "Spider",
            AI_Slendrina => "Slendrina",
            AtticSpider => "Attic spider",
            RatEnemy => "Rat",
            SnowManAI => "Snowman",
            _ => c.gameObject.name,
        };

        private static bool ReadPlayer(Camera cam)
        {
            Transform view = null;
            try { view = cam != null ? cam.transform : null; } catch { }

            var player = PlayerGate.Player;
            try
            {
                if (player == null) return false;

                _playerFeet = player.transform.position;
                try
                {
                    var cc = player.characterController;
                    if (cc != null) _playerFeet.y = cc.bounds.min.y;
                }
                catch { }

                if (view == null) view = player.transform;
                _viewForward = Flat(view.forward);
                if (_viewForward.sqrMagnitude < 1e-6f) _viewForward = Flat(player.transform.forward);
                if (_viewForward.sqrMagnitude < 1e-6f) _viewForward = Vector3.forward;
                _viewForward.Normalize();
                _viewRight = new Vector3(_viewForward.z, 0f, -_viewForward.x);
                return true;
            }
            catch { return false; }
        }

        private static void Read(Target t, Seen s)
        {
            var tr = t.Transform;
            s.Position = tr.position;
            s.Forward = tr.forward;
            s.Height = DefaultHeight;

            var agent = t.Agent;
            var agentLive = false;
            try { agentLive = agent != null && agent.enabled && agent.isOnNavMesh; } catch { }

            if (agent != null)
            {
                try
                {
                    var h = agent.height * Mathf.Abs(tr.lossyScale.y);
                    if (h > 0.4f && h < 4f) s.Height = h;
                }
                catch { }
            }

            if (agentLive)
            {
                try
                {
                    s.AgentSpeed = agent.speed;
                    if (agent.hasPath)
                    {
                        s.Path.Add(s.Position);
                        var corners = agent.path.corners;
                        // Corner 0 is where the path was computed from, which is
                        // behind her once she has started walking it.
                        for (var i = 1; i < corners.Length; i++) s.Path.Add(corners[i]);
                        for (var i = 1; i < s.Path.Count; i++) s.PathLength += Vector3.Distance(s.Path[i - 1], s.Path[i]);
                    }
                }
                catch { s.Path.Clear(); s.PathLength = 0f; }
            }

            ReadState(t.Ai, s, agentLive ? agent : null);

            if (_havePlayer)
            {
                var d = s.Position - _playerFeet;
                s.Rise = d.y;
                d.y = 0f;
                s.Distance = d.magnitude;
            }
        }

        private static void ReadState(Component ai, Seen s, NavMeshAgent agent)
        {
            try
            {
                switch (ai)
                {
                    case AI_Granny g:
                        if (g.CaughtPlayer) Set(s, Alert.Caught, "CAUGHT YOU");
                        else if (g.IsDying) Set(s, Alert.Busy, "down");
                        else if (g.IsChasing) Set(s, Alert.Chase, "CHASING");
                        else if (g.IsFollowingSound) Set(s, Alert.Sound, "heard a noise");
                        else if (g.IsSearching) Set(s, Alert.Search, "searching");
                        else if (g.IsBlind) Set(s, Alert.Busy, "blinded");
                        else if (g.IsOpeningDoor) Set(s, Alert.Busy, "opening a door");
                        else if (g.PlacingTrap) Set(s, Alert.Busy, "placing a trap");
                        else if (g.AtDishes) Set(s, Alert.Busy, "washing dishes");
                        else Walking(s, g.IsWalking, g.IsIdle);
                        Door(s, g.OpenDoorRay, g.DoorDistance, g.LayerEx, g.TimerDoorOpening,
                            g.LockedDoorAttempts, g.CanOpenClosets);
                        var ge = g.EnemyVision;
                        if (ge != null) Eyes(s, ge.CameraView, ge.ViewRange, ge.Seeing || ge.PlayerSpotted);
                        break;

                    case AI_Grandpa p:
                        if (p.IsDying) Set(s, Alert.Busy, "down");
                        else if (p.IsShooting) Set(s, Alert.Chase, "SHOOTING");
                        else if (p.IsChasing) Set(s, Alert.Chase, "CHASING");
                        else if (p.IsFollowingSound) Set(s, Alert.Sound, "heard a noise");
                        else if (p.IsSearching) Set(s, Alert.Search, "searching");
                        else if (p.IsBlind) Set(s, Alert.Busy, "blinded");
                        else if (p.IsInSauna) Set(s, Alert.Busy, "in the sauna");
                        else if (p.IsOpeningDoor) Set(s, Alert.Busy, "opening a door");
                        else Walking(s, p.IsWalking, p.IsIdle);
                        Door(s, p.OpenDoorRay, p.DoorDistance, p.LayerEx, p.TimerDoorOpening,
                            p.LockedDoorAttempts, p.CanOpenClosets);
                        var pe = p.EnemyVision;
                        if (pe != null) Eyes(s, pe.CameraView, pe.ViewRange, pe.Seeing || pe.PlayerSpotted);
                        break;

                    case AI_MomSpider m:
                        if (m.IsChasing) Set(s, Alert.Chase, "CHASING");
                        else if (m.IsAngry) Set(s, Alert.Search, "angry");
                        else if (m.IsHit) Set(s, Alert.Busy, "hit");
                        else if (m.IsEating) Set(s, Alert.Busy, "eating");
                        else Walking(s, m.IsWalking, m.IsIdle);
                        var me = m.Eyes;
                        if (me != null) Eyes(s, me.CameraView, me.ViewRange, me.Seeing);
                        break;

                    default:
                        var moving = false;
                        try { moving = agent != null && agent.velocity.sqrMagnitude > 0.01f; } catch { }
                        Set(s, Alert.Calm, moving ? "moving" : "");
                        break;
                }
            }
            catch
            {
                // Fields missing in this game build: position still shows.
            }
        }

        private static void Set(Seen s, Alert alert, string state)
        {
            s.Alert = alert;
            s.State = state;
        }

        private static void Walking(Seen s, bool walking, bool idle) =>
            Set(s, Alert.Calm, walking ? "patrolling" : idle ? "idle" : "");

        private static void Eyes(Seen s, Camera eye, float range, bool sees)
        {
            s.SeesPlayer = sees;
            if (eye == null) return;
            try
            {
                var t = eye.transform;
                s.EyePosition = t.position;
                s.EyeForward = t.forward;
                var hfov = Camera.VerticalToHorizontalFieldOfView(eye.fieldOfView, eye.aspect > 0f ? eye.aspect : 1f);
                s.EyeHalfFovRad = Mathf.Clamp(hfov, 1f, 179f) * 0.5f * Mathf.Deg2Rad;
                s.ViewRange = range;
                s.HasEyes = true;
            }
            catch { s.HasEyes = false; }
        }

        /// <summary>
        /// Repeat the enemy's own door cast to find the door it is working on.
        /// The cast runs at render time with the enemy's current pose, so it can
        /// disagree with the last FixedUpdate's for a frame while she turns; the
        /// timer itself is the game's.
        /// </summary>
        private static void Door(Seen s, Transform ray, float distance, LayerMask mask, float elapsed,
            float attempts, bool canOpenClosets)
        {
            if (!_canCast || ray == null || !(distance > 0f)) return;
            try
            {
                if (!Physics.Raycast(ray.position, ray.forward, out var hit, distance, mask.value)) return;
                var collider = hit.collider;
                if (collider == null) return;

                var name = collider.gameObject.name;
                var rule = DoorTimer.ForName(name, canOpenClosets);
                if (rule == null) return;

                s.HasDoor = true;
                s.DoorRule = rule;
                s.DoorName = name;
                s.DoorPosition = collider.bounds.center;
                s.DoorElapsed = elapsed;
                s.DoorRattles = (int)attempts;
                s.DoorTicks = DoorTimer.TicksRemaining(rule, elapsed, _fixedStep);
            }
            catch { s.HasDoor = false; }
        }

        /// <summary>
        /// Simulated seconds until the enemy reaches the end of its path at its
        /// set speed, or a negative number when it has no path to walk.
        /// </summary>
        public static float SecondsToPathEnd(Seen s) =>
            s.PathLength > 0.05f && s.AgentSpeed > 0.01f ? s.PathLength / s.AgentSpeed : -1f;

        // ---- drawing (Dear ImGui) -----------------------------------------------

        public static uint ColorOf(Alert alert, float alpha = 1f) => Col(AlertColor(alert), alpha);

        public static Vec4 AlertColor(Alert alert) => alert switch
        {
            Alert.Caught => new Vec4(1.00f, 0.20f, 0.85f, 1f),
            Alert.Chase => new Vec4(1.00f, 0.25f, 0.25f, 1f),
            Alert.Sound => new Vec4(1.00f, 0.55f, 0.15f, 1f),
            Alert.Search => new Vec4(1.00f, 0.85f, 0.20f, 1f),
            Alert.Busy => new Vec4(0.55f, 0.75f, 1.00f, 1f),
            _ => new Vec4(0.40f, 0.95f, 0.45f, 1f),
        };

        private static uint Col(Vec4 c, float alpha) =>
            ImGui.ColorConvertFloat4ToU32(new Vec4(c.X, c.Y, c.Z, c.W * alpha));

        private static uint Col(float r, float g, float b, float a) =>
            ImGui.ColorConvertFloat4ToU32(new Vec4(r, g, b, a));

        /// <summary>
        /// Draw onto ImGui's background list, between <c>NewFrame</c> and
        /// <c>Render</c>. Never opens a window, so a throw here cannot unbalance
        /// the window stack; it is still contained, so the panel survives.
        /// </summary>
        public static void Draw(TasConfig cfg)
        {
            try
            {
                var dl = ImGui.GetBackgroundDrawList();
                var cam = ViewCamera();

                if (cam != null)
                    foreach (var s in _seen) DrawInWorld(dl, cam, s, cfg);

                if (cfg.EspRadar && _havePlayer) DrawRadar(dl, cam, cfg);
            }
            catch (Exception e)
            {
                if (_warned) return;
                _warned = true;
                Warn?.Invoke("ESP draw failed (" + e.Message + "); it will keep trying each frame.");
            }
        }

        private static void DrawInWorld(ImDrawListPtr dl, Camera cam, Seen s, TasConfig cfg)
        {
            var col = ColorOf(s.Alert);
            var shadow = Col(0f, 0f, 0f, 0.85f);

            if (cfg.EspPaths && s.Path.Count > 1)
            {
                var pathCol = ColorOf(s.Alert, 0.55f);
                for (var i = 1; i < s.Path.Count; i++)
                    WorldLine(dl, cam, s.Path[i - 1] + Vector3.up * 0.05f, s.Path[i] + Vector3.up * 0.05f, pathCol, 2f);

                if (Project(cam, s.Path[s.Path.Count - 1], out var end))
                    dl.AddCircle(end, 6f, pathCol, 12, 2f);
            }

            if (s.HasDoor) DrawDoor(dl, cam, s);

            var w = Screen.width;
            var h = Screen.height;
            var onFeet = Project(cam, s.Position, out var feet);
            var onHead = Project(cam, s.Position + Vector3.up * s.Height, out var head);

            if (onFeet && onHead && (OnScreen(feet, w, h) || OnScreen(head, w, h)))
            {
                var boxH = Mathf.Max(feet.Y - head.Y, 8f);
                var boxW = boxH * 0.45f;
                var cx = (feet.X + head.X) * 0.5f;
                var min = new Vec2(cx - boxW * 0.5f, head.Y);
                var max = new Vec2(cx + boxW * 0.5f, head.Y + boxH);

                dl.AddRect(min, max, shadow, 0f, ImDrawFlags.None, 3.5f);
                dl.AddRect(min, max, col, 0f, ImDrawFlags.None, 1.5f);

                var y = min.Y - 30f;
                Label(dl, new Vec2(cx, y), $"{s.Name}  {DistanceText(s)}", col);
                Label(dl, new Vec2(cx, y + 14f), StateLine(s), col);
                return;
            }

            // Off screen or behind: an arrow at the screen edge pointing the way
            // to turn, so she is never out of mind just because she is out of view.
            var local = cam.transform.InverseTransformPoint(s.Position + Vector3.up * (s.Height * 0.5f));
            var dir = new Vec2(local.x, -local.y);
            if (local.z < 0f && dir.LengthSquared() < 1e-4f) dir = new Vec2(0f, 1f);
            if (dir.LengthSquared() < 1e-8f) dir = new Vec2(0f, 1f);
            dir = Vec2.Normalize(dir);

            var center = new Vec2(w * 0.5f, h * 0.5f);
            var rx = w * 0.5f - 60f;
            var ry = h * 0.5f - 60f;
            var scale = 1f / MathF.Sqrt(dir.X * dir.X / (rx * rx) + dir.Y * dir.Y / (ry * ry));
            var tip = center + dir * scale;
            var side = new Vec2(-dir.Y, dir.X);

            var a = tip + dir * 12f;
            var b = tip - dir * 6f + side * 9f;
            var c = tip - dir * 6f - side * 9f;
            dl.AddTriangleFilled(a, b, c, col);
            dl.AddTriangle(a, b, c, shadow, 1.5f);

            var textAt = tip - dir * 26f;
            Label(dl, new Vec2(textAt.X, textAt.Y - 12f), $"{s.Name} {DistanceText(s)}", col);
            if (s.Alert >= Alert.Search || s.SeesPlayer || s.HasDoor) Label(dl, new Vec2(textAt.X, textAt.Y + 2f), StateLine(s), col);
        }

        public static Vec4 DoorColor(Seen s)
        {
            var secs = DoorTimer.SecondsRemaining(s.DoorRule, s.DoorElapsed);
            return secs <= 0.5f ? new Vec4(1f, 0.25f, 0.25f, 1f) : new Vec4(1f, 0.6f, 0.15f, 1f);
        }

        /// <summary>
        /// A countdown ring on the door itself. Drawn over everything, so it
        /// reads through walls like the rest of the overlay.
        /// </summary>
        private static void DrawDoor(ImDrawListPtr dl, Camera cam, Seen s)
        {
            if (!Project(cam, s.DoorPosition, out var c)) return;
            if (!OnScreen(c, Screen.width, Screen.height)) return;

            var col = Col(DoorColor(s), 1f);
            const float r = 17f;
            var progress = DoorTimer.Progress(s.DoorRule, s.DoorElapsed);

            dl.AddCircleFilled(c, r + 3f, Col(0f, 0f, 0f, 0.6f), 32);
            dl.AddCircle(c, r, Col(1f, 1f, 1f, 0.2f), 32, 4f);
            if (progress > 0f)
            {
                var start = -MathF.PI / 2f;
                dl.PathArcTo(c, r, start, start + MathF.PI * 2f * progress, 32);
                dl.PathStroke(col, ImDrawFlags.None, 4f);
            }

            var secs = DoorTimer.SecondsRemaining(s.DoorRule, s.DoorElapsed);
            var mid = $"{secs:0.0}";
            var size = ImGui.CalcTextSize(mid);
            dl.AddText(c - size * 0.5f, Col(1f, 1f, 1f, 1f), mid);

            var y = c.Y + r + 6f;
            Label(dl, new Vec2(c.X, y), $"{s.Name} at {s.DoorName}", col);
            Label(dl, new Vec2(c.X, y + 14f), DoorText(s), col);
            if (s.DoorRule.Rattles.Length > 0)
                Label(dl, new Vec2(c.X, y + 28f),
                    $"rattle {Mathf.Clamp(s.DoorRattles, 0, s.DoorRule.Rattles.Length)}/{s.DoorRule.Rattles.Length}", col);
        }

        private static void DrawRadar(ImDrawListPtr dl, Camera cam, TasConfig cfg)
        {
            const float radius = 100f;
            const float margin = 16f;
            var center = new Vec2(Screen.width - margin - radius, margin + radius);
            var range = Mathf.Max(cfg.EspRadarRange, 1f);
            var scale = radius / range;

            dl.AddCircleFilled(center, radius, Col(0f, 0f, 0f, 0.55f), 64);
            dl.AddCircle(center, radius, Col(1f, 1f, 1f, 0.35f), 64, 1.5f);
            dl.AddCircle(center, radius * 0.5f, Col(1f, 1f, 1f, 0.12f), 48, 1f);
            dl.AddLine(center - new Vec2(radius, 0f), center + new Vec2(radius, 0f), Col(1f, 1f, 1f, 0.08f), 1f);
            dl.AddLine(center - new Vec2(0f, radius), center + new Vec2(0f, radius), Col(1f, 1f, 1f, 0.08f), 1f);

            var bmin = center - new Vec2(radius, radius);
            var bmax = center + new Vec2(radius, radius);
            dl.PushClipRect(bmin, bmax, true);
            try
            {
                // The player's own view cone, so "behind me" reads at a glance.
                var half = 30f * Mathf.Deg2Rad;
                try
                {
                    if (cam != null)
                        half = Camera.VerticalToHorizontalFieldOfView(cam.fieldOfView, cam.aspect) * 0.5f * Mathf.Deg2Rad;
                }
                catch { }
                Wedge(dl, center, -MathF.PI / 2f, half, radius, Col(1f, 1f, 1f, 0.07f));

                // Furthest first, so the nearest enemy is drawn on top.
                for (var i = _seen.Count - 1; i >= 0; i--)
                {
                    var s = _seen[i];
                    var otherFloor = Mathf.Abs(s.Rise) > FloorGap;
                    var alpha = otherFloor ? 0.45f : 1f;

                    if (cfg.EspVision && s.HasEyes && !otherFloor)
                    {
                        var eye = ToRadar(center, scale, s.EyePosition);
                        var look = RadarDir(s.EyeForward);
                        if (look.LengthSquared() > 1e-6f)
                        {
                            var reach = Mathf.Min(s.ViewRange > 0f ? s.ViewRange : range, range * 2f) * scale;
                            var tint = s.SeesPlayer ? Col(1f, 0.2f, 0.2f, 0.30f) : Col(1f, 1f, 0.6f, 0.12f);
                            Wedge(dl, eye, MathF.Atan2(look.Y, look.X), s.EyeHalfFovRad, reach, tint);
                        }
                    }

                    if (cfg.EspPaths && s.Path.Count > 1)
                    {
                        var pathCol = ColorOf(s.Alert, 0.5f * alpha);
                        for (var p = 1; p < s.Path.Count; p++)
                            dl.AddLine(ToRadar(center, scale, s.Path[p - 1]), ToRadar(center, scale, s.Path[p]), pathCol, 1.5f);
                    }
                }
            }
            finally { dl.PopClipRect(); }

            for (var i = _seen.Count - 1; i >= 0; i--)
            {
                var s = _seen[i];
                var otherFloor = Mathf.Abs(s.Rise) > FloorGap;
                var col = ColorOf(s.Alert, otherFloor ? 0.5f : 1f);

                var p = ToRadar(center, scale, s.Position);
                var off = p - center;
                var outside = off.Length() > radius - 5f;
                if (outside) p = center + Vec2.Normalize(off) * (radius - 5f);

                if (outside || otherFloor) dl.AddCircle(p, 5f, col, 12, 2f);
                else dl.AddCircleFilled(p, 5f, col, 12);

                var face = RadarDir(s.Forward);
                if (!outside && face.LengthSquared() > 1e-6f)
                    dl.AddLine(p, p + Vec2.Normalize(face) * 11f, col, 2f);

                if (s.SeesPlayer) dl.AddCircle(p, 9f, Col(1f, 0.2f, 0.2f, 1f), 16, 2f);

                if (s.HasDoor)
                {
                    var d = ToRadar(center, scale, s.DoorPosition);
                    if ((d - center).Length() <= radius - 4f)
                    {
                        var dc = Col(DoorColor(s), 1f);
                        dl.AddRectFilled(d - new Vec2(4f, 4f), d + new Vec2(4f, 4f), dc);
                        dl.AddLine(p, d, dc, 1f);
                    }
                }
            }

            // The player: a small arrow, always pointing up.
            dl.AddTriangleFilled(center + new Vec2(0f, -7f), center + new Vec2(-5f, 5f), center + new Vec2(5f, 5f),
                Col(1f, 1f, 1f, 0.95f));

            Label(dl, new Vec2(center.X, center.Y + radius + 3f), $"radar {range:0} m", Col(1f, 1f, 1f, 0.6f));
        }

        private static void Wedge(ImDrawListPtr dl, Vec2 apex, float angle, float half, float length, uint col)
        {
            if (length <= 0f || half <= 0f) return;
            dl.PathLineTo(apex);
            dl.PathArcTo(apex, length, angle - half, angle + half, 20);
            dl.PathFillConvex(col);
        }

        /// <summary>Player-relative, forward up, in radar pixels.</summary>
        private static Vec2 ToRadar(Vec2 center, float scale, Vector3 world)
        {
            var d = world - _playerFeet;
            return center + new Vec2(Vector3.Dot(d, _viewRight), -Vector3.Dot(d, _viewForward)) * scale;
        }

        private static Vec2 RadarDir(Vector3 dir) =>
            new Vec2(Vector3.Dot(dir, _viewRight), -Vector3.Dot(dir, _viewForward));

        private static void Label(ImDrawListPtr dl, Vec2 centerTop, string text, uint col)
        {
            if (string.IsNullOrEmpty(text)) return;
            var size = ImGui.CalcTextSize(text);
            var at = new Vec2(centerTop.X - size.X * 0.5f, centerTop.Y);
            dl.AddText(at + new Vec2(1f, 1f), Col(0f, 0f, 0f, 0.9f), text);
            dl.AddText(at, col, text);
        }

        public static string DistanceText(Seen s)
        {
            if (!_havePlayer) return "";
            var text = $"{s.Distance:0.0}m";
            if (s.Rise > FloorGap) text += $" ^{s.Rise:0.0}";
            else if (s.Rise < -FloorGap) text += $" v{-s.Rise:0.0}";
            return text;
        }

        public static string StateLine(Seen s)
        {
            var line = s.State;
            if (s.SeesPlayer) line = (line.Length > 0 ? line + " | " : "") + "SEES YOU";
            if (s.HasDoor) line = (line.Length > 0 ? line + " | " : "") + DoorText(s);
            var eta = EtaText(s);
            if (eta.Length > 0) line = (line.Length > 0 ? line + " | " : "") + eta;
            return line;
        }

        /// <summary>"door opens 1.24s (62t ~62f)": game seconds, physics ticks and TAS frames.</summary>
        public static string DoorText(Seen s)
        {
            if (!s.HasDoor) return "";
            var secs = DoorTimer.SecondsRemaining(s.DoorRule, s.DoorElapsed);
            var text = $"door {s.DoorRule.Action} {secs:0.00}s";
            if (s.DoorTicks >= 0)
            {
                text += $" ({s.DoorTicks}t";
                var mod = GrannyTasMod.Instance;
                var perFrame = mod != null && mod.Time.Enabled ? mod.Time.TicksPerFrame : 0f;
                if (perFrame > 0f) text += $" ~{Mathf.CeilToInt(s.DoorTicks / perFrame)}f";
                text += ")";
            }
            return text;
        }

        /// <summary>Time to the end of the current path, in simulated seconds and TAS frames.</summary>
        public static string EtaText(Seen s)
        {
            var secs = SecondsToPathEnd(s);
            if (secs < 0f) return "";
            var mod = GrannyTasMod.Instance;
            var rate = mod != null && mod.Time.Enabled ? mod.Time.TickRate : 0f;
            return rate > 0f ? $"path {secs:0.0}s ({Mathf.CeilToInt(secs * rate)}f)" : $"path {secs:0.0}s";
        }

        // ---- projection ---------------------------------------------------------

        /// <summary>The camera the player is looking through.</summary>
        private static Camera ViewCamera()
        {
            try
            {
                var p = PlayerGate.Player;
                if (p != null)
                {
                    var c = p.playerCamera2;
                    if (c != null && c.isActiveAndEnabled) return c;
                }
            }
            catch { }

            try
            {
                var main = Camera.main;
                if (main != null && main.isActiveAndEnabled) return main;
            }
            catch { }

            return null;
        }

        /// <summary>World to ImGui pixels (top-left origin). False behind the camera.</summary>
        private static bool Project(Camera cam, Vector3 world, out Vec2 screen)
        {
            var p = cam.WorldToScreenPoint(world);
            if (p.z <= 0.01f)
            {
                screen = default;
                return false;
            }
            screen = new Vec2(p.x, Screen.height - p.y);
            return true;
        }

        private static bool OnScreen(Vec2 p, float w, float h) => p.X >= 0f && p.X <= w && p.Y >= 0f && p.Y <= h;

        /// <summary>A world-space segment, clipped at the near plane so a path running behind the camera draws correctly.</summary>
        private static void WorldLine(ImDrawListPtr dl, Camera cam, Vector3 a, Vector3 b, uint col, float thickness)
        {
            var t = cam.transform;
            var origin = t.position;
            var fwd = t.forward;
            var near = cam.nearClipPlane + 0.02f;

            var da = Vector3.Dot(a - origin, fwd);
            var db = Vector3.Dot(b - origin, fwd);
            if (da < near && db < near) return;
            if (da < near) a = Vector3.Lerp(a, b, (near - da) / (db - da));
            else if (db < near) b = Vector3.Lerp(b, a, (near - db) / (da - db));

            if (Project(cam, a, out var sa) && Project(cam, b, out var sb)) dl.AddLine(sa, sb, col, thickness);
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // ---- fallback (legacy IMGUI) ------------------------------------------

        /// <summary>
        /// Labels only, for when Dear ImGui could not start: the information
        /// matters more than the boxes.
        /// </summary>
        public static void DrawLegacy(GUIStyle style)
        {
            var cam = ViewCamera();
            if (cam == null) return;

            foreach (var s in _seen)
            {
                try
                {
                    if (!Project(cam, s.Position + Vector3.up * s.Height, out var head)) continue;
                    if (!OnScreen(head, Screen.width, Screen.height)) continue;
                    var hex = ColorUtility.ToHtmlStringRGB(new Color(AlertColor(s.Alert).X, AlertColor(s.Alert).Y, AlertColor(s.Alert).Z));
                    GUI.Label(new Rect(head.X - 90f, head.Y - 36f, 180f, 36f),
                        $"<color=#{hex}>{s.Name} {DistanceText(s)}\n{StateLine(s)}</color>", style);
                }
                catch { }
            }
        }
    }
}
