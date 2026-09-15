using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// A recorded run: a header of everything the simulation depends on besides
    /// input, followed by one line per simulated frame.
    ///
    /// The format is plain text on purpose. A TAS is edited far more often than
    /// it is recorded, and a text file can be diffed, hand-patched a frame at a
    /// time, and inspected when a replay desyncs.
    ///
    /// Frames are indexed by **frame number, not time**. That is what lets a run
    /// recorded at 0.1x replay at 1x: playback speed is only how fast frames are
    /// produced, and the simulation never learns the difference.
    /// </summary>
    public sealed class MacroFile
    {
        public const string Magic = "# GrannyTAS macro v4";
        private const string V3Magic = "# GrannyTAS macro v3";
        private const string V2Magic = "# GrannyTAS macro v2";
        private const string LegacyMagic = "# GrannyTAS macro v1";

        /// <summary>Simulated seconds per frame, as <c>1/TickRate</c>. Part of the simulation.</summary>
        public float TickRate = 60f;

        /// <summary>FixedUpdate rate driving enemy AI. Part of the simulation.</summary>
        public float PhysicsRate = 50f;

        /// <summary>
        /// Seed pushed into <see cref="UnityEngine.Random"/> at record start and
        /// again at playback start. <c>AI_Granny</c> pulls from it in
        /// <c>ChooseWaypoint</c> and for idle voice lines, so without this
        /// Granny picks different waypoints on replay.
        /// </summary>
        public int RngSeed;

        /// <summary>
        /// <c>SeedManager.Seed</c> as observed when recording began. Item
        /// placement is decided once at level load from this, so it cannot be
        /// restored mid-run — it is recorded to *verify* against, and playback
        /// warns on a mismatch rather than silently desyncing.
        /// </summary>
        public int SeedManagerSeed;

        /// <summary>True when difficulty/version/preset were captured by v3 or later.</summary>
        public bool HasSetupMetadata;
        public int Difficulty;
        public int SelectableVersion;
        public int ItemPreset;
        public string GameBuild = "";

        public string Scene = "";
        public InputFrame InitialInput = new InputFrame();

        /// <summary>
        /// Where the player and every roaming enemy stood when recording began.
        /// Replaying inputs is meaningless if the world does not start in the
        /// same place — Granny in particular roams.
        /// </summary>
        public WorldSnapshot Snapshot = new WorldSnapshot();

        public readonly List<InputFrame> Frames = new List<InputFrame>();

        public int Count => Frames.Count;

        /// <summary>Wall-clock length at 1x, which is not how long it took to record.</summary>
        public float DurationSeconds => TickRate > 0f ? Frames.Count / TickRate : 0f;

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine(Magic);
            sb.AppendLine(Inv($"tickRate={TickRate}"));
            sb.AppendLine(Inv($"physicsRate={PhysicsRate}"));
            sb.AppendLine(Inv($"rngSeed={RngSeed}"));
            sb.AppendLine(Inv($"seedManagerSeed={SeedManagerSeed}"));
            sb.AppendLine(Inv($"hasSetupMetadata={(HasSetupMetadata ? 1 : 0)}"));
            sb.AppendLine(Inv($"difficulty={Difficulty}"));
            sb.AppendLine(Inv($"selectableVersion={SelectableVersion}"));
            sb.AppendLine(Inv($"itemPreset={ItemPreset}"));
            sb.AppendLine($"gameBuild={GameBuild}");
            sb.AppendLine($"scene={Scene}");
            sb.AppendLine(Inv($"frames={Frames.Count}"));
            sb.AppendLine("initialInput=" + Encode(InitialInput));
            foreach (var line in Snapshot.Encode()) sb.AppendLine(line);
            sb.AppendLine("---");

            foreach (var f in Frames) sb.AppendLine(Encode(f));

            // Write beside the destination so a failed write leaves the last
            // successful recording intact. Rename only after flushing the data.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static MacroFile Load(string path)
        {
            var macro = new MacroFile();
            var body = false;
            var magic = false;
            var version = 0;
            int? expectedFrames = null;
            bool? hasSetupMetadata = null;
            var setupFields = 0;
            var lineNumber = 0;

            foreach (var raw in File.ReadLines(path))
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (!magic)
                {
                    if (line == Magic) version = 4;
                    else if (line == V3Magic) version = 3;
                    else if (line == V2Magic) version = 2;
                    else if (line == LegacyMagic) version = 1;
                    else throw new FormatException("Missing or unsupported GrannyTAS macro version.");
                    magic = true;
                    continue;
                }

                try
                {
                    if (!body)
                    {
                        if (line == "---") { body = true; continue; }
                        if (line.StartsWith("#")) continue;

                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;

                        var key = line.Substring(0, eq);
                        var val = line.Substring(eq + 1);

                        switch (key)
                        {
                            case "tickRate": macro.TickRate = ParseRate(val); break;
                            case "physicsRate": macro.PhysicsRate = ParseRate(val); break;
                            case "rngSeed": macro.RngSeed = ParseInt(val); break;
                            case "seedManagerSeed": macro.SeedManagerSeed = ParseInt(val); break;
                            case "hasSetupMetadata":
                                var metadata = ParseInt(val);
                                if (metadata != 0 && metadata != 1) throw new FormatException("hasSetupMetadata must be 0 or 1.");
                                hasSetupMetadata = metadata == 1;
                                break;
                            case "difficulty": macro.Difficulty = ParseInt(val); setupFields |= 1; break;
                            case "selectableVersion": macro.SelectableVersion = ParseInt(val); setupFields |= 2; break;
                            case "itemPreset": macro.ItemPreset = ParseInt(val); setupFields |= 4; break;
                            case "gameBuild": macro.GameBuild = val; setupFields |= 8; break;
                            case "scene": macro.Scene = val; break;
                            case "frames": expectedFrames = ParseInt(val); break;
                            case "initialInput": macro.InitialInput = Decode(val, version); break;
                            case "player":
                            case "entity": macro.Snapshot.AddDecoded(key, val); break;
                        }
                        continue;
                    }

                    macro.Frames.Add(Decode(line, version));
                }
                catch (FormatException e)
                {
                    throw new FormatException($"Macro line {lineNumber}: {e.Message}", e);
                }
            }

            if (!magic || !body) throw new FormatException("Macro is missing its header or frame separator.");
            if (version >= 3 && !hasSetupMetadata.HasValue)
                throw new FormatException("Macro is missing hasSetupMetadata.");
            macro.HasSetupMetadata = version >= 3 && hasSetupMetadata == true;
            if (macro.HasSetupMetadata && setupFields != 15)
                throw new FormatException("Macro is missing required level setup metadata.");
            if (expectedFrames.HasValue && expectedFrames.Value != macro.Count)
                throw new FormatException($"Header declares {expectedFrames} frames; found {macro.Count}.");
            return macro;
        }

        /// <summary>
        /// The handful of header fields a directory listing needs, without the
        /// frame count validation and setup-metadata checks that make a full
        /// <see cref="Load"/> the right call for actually playing a macro back.
        /// </summary>
        public readonly struct MacroHeaderInfo
        {
            public int Frames { get; }
            public float TickRate { get; }
            public string Scene { get; }

            public MacroHeaderInfo(int frames, float tickRate, string scene)
            {
                Frames = frames;
                TickRate = tickRate;
                Scene = scene;
            }
        }

        /// <summary>
        /// Reads only the lines up to the "---" separator — never the frames
        /// that follow. The saved-macro panel calls this once per file in a
        /// directory scan, so it has to stay cheap regardless of how long the
        /// recording is; a full <see cref="Load"/> would parse and validate
        /// every frame just to show a duration in a list.
        /// </summary>
        public static MacroHeaderInfo PeekHeader(string path)
        {
            var magic = false;
            var tickRate = 60f;
            var scene = "";
            var frames = 0;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (!magic)
                {
                    if (line != Magic && line != V3Magic && line != V2Magic && line != LegacyMagic)
                        throw new FormatException("Missing or unsupported GrannyTAS macro version.");
                    magic = true;
                    continue;
                }

                if (line == "---") break;
                if (line.StartsWith("#")) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq);
                var val = line.Substring(eq + 1);

                switch (key)
                {
                    case "tickRate": tickRate = ParseRate(val); break;
                    case "scene": scene = val; break;
                    case "frames": frames = ParseInt(val); break;
                }
            }

            if (!magic) throw new FormatException("Macro is missing its header or frame separator.");
            return new MacroHeaderInfo(frames, tickRate, scene);
        }

        // v4 adds a sixth field containing the pre-frame rigidbody checkpoint.
        private static string Encode(InputFrame f)
        {
            var keys = new List<string>();
            var sorted = new List<KeyCode>(f.Keys);
            sorted.Sort();
            foreach (var k in sorted) keys.Add(k.ToString());

            var bits = 0;
            for (var i = 0; i < 3; i++) if (f.Mouse(i)) bits |= 1 << i;

            var axes = string.Join(",", new[]
            {
                Fmt(f.Axis("Mouse X")), Fmt(f.Axis("Mouse Y")), Fmt(f.Axis("Mouse ScrollWheel")),
                Fmt(f.Axis("Horizontal")), Fmt(f.Axis("Vertical")),
            });

            var rawAxes = string.Join(",", new[]
            {
                Fmt(f.AxisRaw("Mouse X")), Fmt(f.AxisRaw("Mouse Y")), Fmt(f.AxisRaw("Mouse ScrollWheel")),
                Fmt(f.AxisRaw("Horizontal")), Fmt(f.AxisRaw("Vertical")),
            });

            // Nine values is the look pose alone; twelve appends the player's
            // world position. Both are read back (see Decode), so a macro saved
            // by an older build still loads and a macro saved by this one still
            // carries the position that keeps interaction rays on target.
            var pose = "-";
            if (f.HasPose)
            {
                var values = new List<string>
                {
                    Fmt(f.PlayerRotation.x), Fmt(f.PlayerRotation.y), Fmt(f.PlayerRotation.z), Fmt(f.PlayerRotation.w),
                    Fmt(f.CameraLocalRotation.x), Fmt(f.CameraLocalRotation.y),
                    Fmt(f.CameraLocalRotation.z), Fmt(f.CameraLocalRotation.w), Fmt(f.RotationX),
                };
                if (f.HasPosition)
                {
                    values.Add(Fmt(f.PlayerPosition.x));
                    values.Add(Fmt(f.PlayerPosition.y));
                    values.Add(Fmt(f.PlayerPosition.z));
                }
                pose = string.Join(",", values);
            }

            return string.Join("|", new[]
            {
                string.Join(",", keys), bits.ToString(CultureInfo.InvariantCulture), axes, rawAxes, pose,
                EncodePhysics(f.PhysicsStates)
            });
        }

        private static InputFrame Decode(string line, int version)
        {
            var f = new InputFrame();
            var parts = line.Split('|');
            var expectedParts = version >= 4 ? 6 : version >= 2 ? 5 : 3;
            if (parts.Length != expectedParts)
                throw new FormatException(version >= 2
                    ? version >= 4
                        ? "Expected keys|mouse buttons|five axes|five raw axes|pose|physics state."
                        : "Expected keys|mouse buttons|five axes|five raw axes|pose."
                    : "Expected keys|mouse buttons|five axes.");

            if (parts.Length > 0 && parts[0].Length > 0)
                foreach (var name in parts[0].Split(','))
                {
                    if (!Enum.TryParse<KeyCode>(name, out var k) || !Enum.IsDefined(typeof(KeyCode), k))
                        throw new FormatException($"Unknown key '{name}'.");
                    f.Keys.Add(k);
                }

            if (parts.Length > 1)
            {
                var bits = ParseInt(parts[1]);
                if (bits < 0 || bits > 7) throw new FormatException("Invalid mouse button mask.");
                for (var i = 0; i < 3; i++) f.MouseButtons[i] = (bits & (1 << i)) != 0;
            }

            if (parts.Length > 2)
            {
                var a = parts[2].Split(',');
                if (a.Length != 5) throw new FormatException("Expected five axis values.");
                var names = new[] { "Mouse X", "Mouse Y", "Mouse ScrollWheel", "Horizontal", "Vertical" };
                for (var i = 0; i < names.Length && i < a.Length; i++)
                    f.Axes[names[i]] = ParseFloat(a[i]);
            }

            var axisNames = new[] { "Mouse X", "Mouse Y", "Mouse ScrollWheel", "Horizontal", "Vertical" };
            if (version >= 2)
            {
                var raw = parts[3].Split(',');
                if (raw.Length != axisNames.Length) throw new FormatException("Expected five raw axis values.");
                for (var i = 0; i < raw.Length; i++) f.RawAxes[axisNames[i]] = ParseFloat(raw[i]);

                if (parts[4] != "-")
                {
                    var pose = parts[4].Split(',');
                    if (pose.Length != 9 && pose.Length != 12)
                        throw new FormatException("Expected nine pose values, or twelve with a player position.");
                    f.PlayerRotation = ParseQuaternion(pose, 0);
                    f.CameraLocalRotation = ParseQuaternion(pose, 4);
                    f.RotationX = ParseFloat(pose[8]);
                    f.HasPose = true;

                    if (pose.Length == 12)
                    {
                        f.PlayerPosition = new Vector3(
                            ParseFloat(pose[9]), ParseFloat(pose[10]), ParseFloat(pose[11]));
                        f.HasPosition = true;
                    }
                }
            }
            else
            {
                // v1 supplied one raw sample to both Unity APIs. Preserve that
                // exact legacy behavior rather than inventing smoothing.
                foreach (var name in axisNames) f.RawAxes[name] = f.Axis(name);
            }

            if (version >= 4)
            {
                DecodePhysics(parts[5], f.PhysicsStates);
                f.HasPhysicsState = true;
            }

            return f;
        }

        private static string EncodePhysics(IReadOnlyList<PhysicsFrameState> states)
        {
            if (states == null || states.Count == 0) return "-";
            var entries = new string[states.Count];
            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (s == null || string.IsNullOrEmpty(s.Identity))
                    throw new FormatException($"Invalid rigidbody checkpoint at index {i}.");
                ValidatePhysics(s);
                entries[i] = string.Join(",", new[]
                {
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Identity)),
                    Fmt(s.Position.x), Fmt(s.Position.y), Fmt(s.Position.z),
                    Fmt(s.Rotation.x), Fmt(s.Rotation.y), Fmt(s.Rotation.z), Fmt(s.Rotation.w),
                    Fmt(s.Velocity.x), Fmt(s.Velocity.y), Fmt(s.Velocity.z),
                    Fmt(s.AngularVelocity.x), Fmt(s.AngularVelocity.y), Fmt(s.AngularVelocity.z),
                    Bool(s.IsKinematic), Bool(s.UseGravity), Bool(s.DetectCollisions),
                    s.Constraints.ToString(CultureInfo.InvariantCulture), Bool(s.Sleeping),
                });
            }
            return string.Join(";", entries);
        }

        private static void DecodePhysics(string encoded, List<PhysicsFrameState> destination)
        {
            destination.Clear();
            if (encoded == "-") return;
            if (string.IsNullOrEmpty(encoded)) throw new FormatException("Physics state field is empty.");

            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in encoded.Split(';'))
            {
                var values = entry.Split(',');
                if (values.Length != 19) throw new FormatException("Expected nineteen rigidbody state values.");

                string identity;
                try { identity = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(values[0])); }
                catch (Exception e) when (e is FormatException || e is ArgumentException)
                { throw new FormatException("Invalid rigidbody identity encoding.", e); }
                if (string.IsNullOrEmpty(identity)) throw new FormatException("Rigidbody identity cannot be empty.");
                if (!identities.Add(identity)) throw new FormatException($"Duplicate rigidbody identity '{identity}'.");

                destination.Add(new PhysicsFrameState
                {
                    Identity = identity,
                    Position = new Vector3(ParseFloat(values[1]), ParseFloat(values[2]), ParseFloat(values[3])),
                    Rotation = ParseQuaternion(values, 4),
                    Velocity = new Vector3(ParseFloat(values[8]), ParseFloat(values[9]), ParseFloat(values[10])),
                    AngularVelocity = new Vector3(ParseFloat(values[11]), ParseFloat(values[12]), ParseFloat(values[13])),
                    IsKinematic = ParseBool(values[14]),
                    UseGravity = ParseBool(values[15]),
                    DetectCollisions = ParseBool(values[16]),
                    Constraints = ParseInt(values[17]),
                    Sleeping = ParseBool(values[18]),
                });
            }
        }

        private static string Bool(bool value) => value ? "1" : "0";

        private static void ValidatePhysics(PhysicsFrameState state)
        {
            var finite = float.IsFinite(state.Position.x) && float.IsFinite(state.Position.y) && float.IsFinite(state.Position.z) &&
                         float.IsFinite(state.Rotation.x) && float.IsFinite(state.Rotation.y) &&
                         float.IsFinite(state.Rotation.z) && float.IsFinite(state.Rotation.w) &&
                         float.IsFinite(state.Velocity.x) && float.IsFinite(state.Velocity.y) && float.IsFinite(state.Velocity.z) &&
                         float.IsFinite(state.AngularVelocity.x) && float.IsFinite(state.AngularVelocity.y) &&
                         float.IsFinite(state.AngularVelocity.z);
            var magnitudeSquared = state.Rotation.x * state.Rotation.x + state.Rotation.y * state.Rotation.y +
                                   state.Rotation.z * state.Rotation.z + state.Rotation.w * state.Rotation.w;
            if (!finite || !float.IsFinite(magnitudeSquared) || magnitudeSquared < 1e-12f)
                throw new FormatException($"Rigidbody '{state.Identity}' contains invalid finite state.");
        }

        private static bool ParseBool(string value)
        {
            var parsed = ParseInt(value);
            if (parsed != 0 && parsed != 1) throw new FormatException($"Expected 0 or 1, found '{value}'.");
            return parsed == 1;
        }

        private static Quaternion ParseQuaternion(string[] values, int offset)
        {
            var q = new Quaternion(ParseFloat(values[offset]), ParseFloat(values[offset + 1]),
                ParseFloat(values[offset + 2]), ParseFloat(values[offset + 3]));
            var magnitudeSquared = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!float.IsFinite(magnitudeSquared) || magnitudeSquared < 1e-12f)
                throw new FormatException("Pose quaternion must have a finite, non-zero magnitude.");
            return q;
        }

        // Everything goes through invariant culture. A machine with a comma
        // decimal separator would otherwise write "0,5" into a comma-separated
        // field and silently corrupt every macro it saved.
        private static string Fmt(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Inv(FormattableString s) => FormattableString.Invariant(s);

        private static float ParseFloat(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                ? v : throw new FormatException($"Invalid finite number '{s}'.");

        private static float ParseRate(string s)
        {
            var value = ParseFloat(s);
            if (value < 1f || value > 1000f) throw new FormatException("Rate must be between 1 and 1000.");
            return value;
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : throw new FormatException($"Invalid integer '{s}'.");
    }
}
