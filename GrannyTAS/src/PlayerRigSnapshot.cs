using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// The player's internal state at record start — the part a positional
    /// <see cref="WorldSnapshot"/> cannot see.
    ///
    /// The pickup ray is cast from the camera, and the camera is not where the
    /// player transform says: <c>MobileFPS.CameraAnim</c> head-bobs it,
    /// <c>CrouchHolder</c> animates it down and resizes the controller, and
    /// <c>FallingHolder</c> smooths it on landing. None of that was restored
    /// before, so a replay started with the bob animation at a different point
    /// in its cycle and every pickup ray for the rest of the run pointed a few
    /// millimetres somewhere else — invisible in movement, and exactly the
    /// size of error that turns a grazing hit on an item into a miss.
    ///
    /// Captured: every animation state on the player's animation components,
    /// the local pose of every transform under the player, the controller's
    /// geometry, and the movement/fall/crouch scalars. Running coroutines (a
    /// crouch half-way through its transition) are not restorable; the log
    /// says so when that is the case.
    /// </summary>
    public sealed class PlayerRigSnapshot
    {
        public sealed class AnimState
        {
            public string Owner = "";
            public string Name = "";
            public bool Enabled;
            public float Weight;
            public float Time;
            public float Speed;
        }

        public sealed class LocalPose
        {
            public string Path = "";
            public string Name = "";
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        /// <summary>A rig deeper than this is not a player rig; stop rather than serialize a scene.</summary>
        private const int MaxTransforms = 2048;

        public readonly List<AnimState> Animations = new List<AnimState>();
        public readonly List<LocalPose> Transforms = new List<LocalPose>();
        public readonly List<KeyValuePair<string, float[]>> Values = new List<KeyValuePair<string, float[]>>();

        public bool IsEmpty => Animations.Count == 0 && Transforms.Count == 0 && Values.Count == 0;

        // ---- what is captured ---------------------------------------------------

        private sealed class Rig
        {
            public MobileFPS Fps;
            public FallingHolder Fall;
            public CrouchHolder Crouch;
            public CharacterController Controller;
        }

        private sealed class Field
        {
            public string Name;
            public Func<Rig, float[]> Get;
            public Action<Rig, float[]> Set;
        }

        private static Field Bool<T>(string name, Func<Rig, T> owner, Func<T, bool> get, Action<T, bool> set) where T : class =>
            new Field
            {
                Name = name,
                Get = r => owner(r) is T o ? new[] { get(o) ? 1f : 0f } : null,
                Set = (r, v) => { if (owner(r) is T o && v.Length == 1) set(o, v[0] != 0f); },
            };

        private static Field Float<T>(string name, Func<Rig, T> owner, Func<T, float> get, Action<T, float> set) where T : class =>
            new Field
            {
                Name = name,
                Get = r => owner(r) is T o ? new[] { get(o) } : null,
                Set = (r, v) => { if (owner(r) is T o && v.Length == 1) set(o, v[0]); },
            };

        private static Field Vec<T>(string name, Func<Rig, T> owner, Func<T, Vector3> get, Action<T, Vector3> set) where T : class =>
            new Field
            {
                Name = name,
                Get = r =>
                {
                    if (!(owner(r) is T o)) return null;
                    var v = get(o);
                    return new[] { v.x, v.y, v.z };
                },
                Set = (r, v) => { if (owner(r) is T o && v.Length == 3) set(o, new Vector3(v[0], v[1], v[2])); },
            };

        // Movement gates (isAllowedToMove, AbleToMove, CamK) are deliberately
        // absent: PlayerGate already requires them to be set at both ends, and
        // writing them would hand control to a player the game has frozen.
        private static readonly Field[] Fields =
        {
            Bool("fps.IsCrouched", r => r.Fps, o => o.IsCrouched, (o, v) => o.IsCrouched = v),
            Bool("fps.isMoving", r => r.Fps, o => o.isMoving, (o, v) => o.isMoving = v),
            Vec("fps.moveDirection", r => r.Fps, o => o.moveDirection, (o, v) => o.moveDirection = v),
            Float("fps.moveSpeed", r => r.Fps, o => o.moveSpeed, (o, v) => o.moveSpeed = v),
            Bool("fps.CanFade", r => r.Fps, o => o.CanFade, (o, v) => o.CanFade = v),
            Bool("fps.InWeb", r => r.Fps, o => o.InWeb, (o, v) => o.InWeb = v),
            Bool("fps.WasOnPlatform", r => r.Fps, o => o.WasOnPlatform, (o, v) => o.WasOnPlatform = v),
            Bool("fps.RotateXReser", r => r.Fps, o => o.RotateXReser, (o, v) => o.RotateXReser = v),

            Bool("fall.isFalling", r => r.Fall, o => o.isFalling, (o, v) => o.isFalling = v),
            Bool("fall.isLanding", r => r.Fall, o => o.isLanding, (o, v) => o.isLanding = v),
            Bool("fall.Fell", r => r.Fall, o => o.Fell, (o, v) => o.Fell = v),
            Bool("fall.Damaged", r => r.Fall, o => o.Damaged, (o, v) => o.Damaged = v),
            Bool("fall.DeathFall", r => r.Fall, o => o.DeathFall, (o, v) => o.DeathFall = v),
            Bool("fall.CanFallSound", r => r.Fall, o => o.CanFallSound, (o, v) => o.CanFallSound = v),
            Bool("fall.CanCamSmooth", r => r.Fall, o => o.CanCamSmooth, (o, v) => o.CanCamSmooth = v),
            Float("fall.fallDuration", r => r.Fall, o => o.fallDuration, (o, v) => o.fallDuration = v),
            Float("fall.DurateCan", r => r.Fall, o => o.DurateCan, (o, v) => o.DurateCan = v),

            Bool("crouch.On1", r => r.Crouch, o => o.On1, (o, v) => o.On1 = v),
            Bool("crouch.On2", r => r.Crouch, o => o.On2, (o, v) => o.On2 = v),
            Bool("crouch.isCrouching", r => r.Crouch, o => o.isCrouching, (o, v) => o.isCrouching = v),
            Bool("crouch.IsCrouched", r => r.Crouch, o => o.IsCrouched, (o, v) => o.IsCrouched = v),
            Bool("crouch.Starter", r => r.Crouch, o => o.Starter, (o, v) => o.Starter = v),
            Bool("crouch.Disabled", r => r.Crouch, o => o.Disabled, (o, v) => o.Disabled = v),
            Bool("crouch.IsBelow", r => r.Crouch, o => o.IsBelow, (o, v) => o.IsBelow = v),

            // Crouching resizes the capsule; a standing replay of a crouched
            // recording would collide with everything differently.
            Float("cc.height", r => r.Controller, o => o.height, (o, v) => o.height = v),
            Float("cc.radius", r => r.Controller, o => o.radius, (o, v) => o.radius = v),
            Vec("cc.center", r => r.Controller, o => o.center, (o, v) => o.center = v),
        };

        private static readonly (string Key, Func<Rig, Animation> Get)[] AnimationOwners =
        {
            ("fps.CameraAnim", r => r.Fps?.CameraAnim),
            ("fall.playerAnimation", r => r.Fall?.playerAnimation),
            ("crouch.Cam", r => r.Crouch?.Cam),
            ("crouch.Anim", r => r.Crouch?.Anim),
        };

        private static Rig Resolve(MobileFPS player)
        {
            var rig = new Rig { Fps = player };
            try { rig.Fall = player.FallingHolder; } catch { }
            try { rig.Controller = player.characterController; } catch { }
            try { if (rig.Controller == null) rig.Controller = player.GetComponent<CharacterController>(); } catch { }
            try { rig.Crouch = player.gameObject.GetComponent<CrouchHolder>(); } catch { }
            try { if (rig.Crouch == null) rig.Crouch = player.gameObject.GetComponentInChildren<CrouchHolder>(); } catch { }
            return rig;
        }

        // ---- capture / restore --------------------------------------------------

        public static PlayerRigSnapshot Capture(MobileFPS player)
        {
            var snap = new PlayerRigSnapshot();
            if (player == null) return snap;
            var rig = Resolve(player);

            foreach (var field in Fields)
            {
                try
                {
                    var v = field.Get(rig);
                    if (v != null && AllFinite(v)) snap.Values.Add(new KeyValuePair<string, float[]>(field.Name, v));
                }
                catch { }
            }

            var seen = new List<Animation>();
            foreach (var (key, get) in AnimationOwners)
            {
                try
                {
                    var anim = get(rig);
                    if (anim == null || seen.Exists(a => a == anim)) continue;
                    seen.Add(anim);
                    var count = anim.GetClipCount();
                    for (var i = 0; i < count; i++)
                    {
                        var s = anim.GetStateAtIndex(i);
                        if (s == null) continue;
                        if (!float.IsFinite(s.weight) || !float.IsFinite(s.time) || !float.IsFinite(s.speed)) continue;
                        snap.Animations.Add(new AnimState
                        {
                            Owner = key, Name = s.name ?? "", Enabled = s.enabled,
                            Weight = s.weight, Time = s.time, Speed = s.speed,
                        });
                    }
                }
                catch { }
            }

            try { Walk(player.transform, "", snap.Transforms); }
            catch { }

            return snap;
        }

        private static void Walk(Transform t, string path, List<LocalPose> into)
        {
            var count = t.childCount;
            for (var i = 0; i < count && into.Count < MaxTransforms; i++)
            {
                var c = t.GetChild(i);
                if (c == null) continue;
                var p = path.Length == 0 ? i.ToString(CultureInfo.InvariantCulture) : path + "/" + i.ToString(CultureInfo.InvariantCulture);
                into.Add(new LocalPose
                {
                    Path = p, Name = c.name ?? "",
                    Position = c.localPosition, Rotation = c.localRotation, Scale = c.localScale,
                });
                Walk(c, p, into);
            }
        }

        /// <summary>
        /// Put the rig back. Every item is independent and guarded: a missing
        /// animation state or a renamed child costs that item, not the replay.
        /// Returns a one-line summary for the log.
        /// </summary>
        public string Restore(MobileFPS player)
        {
            if (player == null || IsEmpty) return "no player rig to restore";
            var rig = Resolve(player);
            int values = 0, poses = 0, states = 0, missing = 0;

            foreach (var (name, v) in Values)
            {
                var field = Array.Find(Fields, f => f.Name == name);
                if (field == null) { missing++; continue; }
                try
                {
                    // Only on a difference: resizing the capsule to the size it
                    // already has can still rebuild its contact state.
                    var current = field.Get(rig);
                    if (current == null) { missing++; continue; }
                    if (!SameBits(current, v)) field.Set(rig, v);
                    values++;
                }
                catch { missing++; }
            }

            foreach (var pose in Transforms)
            {
                try
                {
                    var t = Find(player.transform, pose.Path);
                    if (t == null || (t.name ?? "") != pose.Name) { missing++; continue; }
                    // Written only when different, so an already-matching rig is
                    // not marked dirty for the next physics sync.
                    if (!Bits.Same(t.localPosition, pose.Position)) t.localPosition = pose.Position;
                    if (!Bits.Same(t.localRotation, pose.Rotation)) t.localRotation = pose.Rotation;
                    if (!Bits.Same(t.localScale, pose.Scale)) t.localScale = pose.Scale;
                    poses++;
                }
                catch { missing++; }
            }

            foreach (var (key, get) in AnimationOwners)
            {
                Animation anim;
                try { anim = get(rig); } catch { continue; }
                if (anim == null) continue;

                var touched = false;
                foreach (var state in Animations)
                {
                    if (state.Owner != key) continue;
                    try
                    {
                        var s = anim[state.Name];
                        if (s == null) { missing++; continue; }
                        s.enabled = state.Enabled;
                        s.weight = state.Weight;
                        s.time = state.Time;
                        s.speed = state.Speed;
                        states++;
                        touched = true;
                    }
                    catch { missing++; }
                }

                // Apply the restored states to the bones now rather than at the
                // end of the frame, so frame 0 already sees the recorded camera.
                if (touched)
                {
                    try { anim.Sample(); } catch { }
                }
            }

            var crouchNote = "";
            if (WasTrue("crouch.isCrouching")) crouchNote = " (a crouch transition was in progress when recording began; its coroutine cannot be restored)";
            return $"player rig: {values} values, {poses} transforms, {states} animation states restored" +
                   (missing > 0 ? $", {missing} not found" : "") + crouchNote;
        }

        private bool WasTrue(string name)
        {
            foreach (var (n, v) in Values)
                if (n == name) return v.Length == 1 && v[0] != 0f;
            return false;
        }

        private static Transform Find(Transform root, string path)
        {
            var t = root;
            foreach (var part in path.Split('/'))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return null;
                if (index < 0 || index >= t.childCount) return null;
                t = t.GetChild(index);
                if (t == null) return null;
            }
            return t;
        }

        private static bool SameBits(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (!Bits.Same(a[i], b[i])) return false;
            return true;
        }

        private static bool AllFinite(float[] v)
        {
            foreach (var x in v) if (!float.IsFinite(x)) return false;
            return true;
        }

        // ---- text encoding ------------------------------------------------------

        public IEnumerable<string> Encode()
        {
            foreach (var (name, v) in Values)
                yield return "rigValue=" + name + "|" + string.Join(",", Array.ConvertAll(v, F));
            foreach (var a in Animations)
                yield return "rigAnim=" + a.Owner + "|" + S(a.Name) + "|" +
                             string.Join(",", a.Enabled ? "1" : "0", F(a.Weight), F(a.Time), F(a.Speed));
            foreach (var p in Transforms)
                yield return "rigPose=" + p.Path + "|" + S(p.Name) + "|" + string.Join(",",
                    F(p.Position.x), F(p.Position.y), F(p.Position.z),
                    F(p.Rotation.x), F(p.Rotation.y), F(p.Rotation.z), F(p.Rotation.w),
                    F(p.Scale.x), F(p.Scale.y), F(p.Scale.z));
        }

        /// <summary>Accepts one <c>rigValue</c>/<c>rigAnim</c>/<c>rigPose</c> header line.</summary>
        public void AddDecoded(string key, string value)
        {
            var parts = value.Split('|');
            switch (key)
            {
                case "rigValue":
                {
                    if (parts.Length != 2 || parts[0].Length == 0) throw new FormatException("Malformed rig value.");
                    var v = Array.ConvertAll(parts[1].Split(','), P);
                    if (v.Length != 1 && v.Length != 3) throw new FormatException("Rig values have one or three components.");
                    Values.Add(new KeyValuePair<string, float[]>(parts[0], v));
                    break;
                }
                case "rigAnim":
                {
                    if (parts.Length != 3 || parts[0].Length == 0) throw new FormatException("Malformed rig animation state.");
                    var v = parts[2].Split(',');
                    if (v.Length != 4 || (v[0] != "0" && v[0] != "1")) throw new FormatException("Malformed rig animation values.");
                    Animations.Add(new AnimState
                    {
                        Owner = parts[0], Name = PS(parts[1]), Enabled = v[0] == "1",
                        Weight = P(v[1]), Time = P(v[2]), Speed = P(v[3]),
                    });
                    break;
                }
                case "rigPose":
                {
                    if (parts.Length != 3 || parts[0].Length == 0) throw new FormatException("Malformed rig transform.");
                    var v = Array.ConvertAll(parts[2].Split(','), P);
                    if (v.Length != 10) throw new FormatException("Expected ten rig transform values.");
                    var q = new Quaternion(v[3], v[4], v[5], v[6]);
                    var m = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                    if (!float.IsFinite(m) || m < 1e-12f) throw new FormatException("Rig quaternion must have a finite, non-zero magnitude.");
                    Transforms.Add(new LocalPose
                    {
                        Path = parts[0], Name = PS(parts[1]),
                        Position = new Vector3(v[0], v[1], v[2]), Rotation = q, Scale = new Vector3(v[7], v[8], v[9]),
                    });
                    break;
                }
            }
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string S(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));

        private static float P(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                ? v : throw new FormatException($"Invalid rig number '{s}'.");

        private static string PS(string s)
        {
            try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s)); }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            { throw new FormatException("Invalid rig string encoding.", e); }
        }
    }
}
