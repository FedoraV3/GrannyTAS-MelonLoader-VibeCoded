using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyTAS
{
    public enum MacroMode { Idle, Recording, Preparing, Playing }

    public interface IMacroGameSetup
    {
        void Capture(MacroFile macro);
        bool RequiresRestart(MacroFile macro, out string reason);
        bool CanReplayBuild(MacroFile macro, out string reason);
        void ApplyAndRestart(MacroFile macro);
        bool LoadedSetupMatches(MacroFile macro, out string reason);
        void CancelPending();
        float Realtime { get; }

        /// <summary>
        /// Whether difficulty / selectable version / item preset are actually
        /// verified, as opposed to always comparing placeholder zeros. See
        /// <see cref="MacroGameSetup.VerifiesLevelSetup"/>.
        /// </summary>
        bool VerifiesLevelSetup { get; }
    }

    /// <summary>
    /// Records one input per simulated frame and replays at 1x. Simulation rates,
    /// speed, and turbo are locked for the duration of either operation. The
    /// timing controller compensates capture time to retain the frame delta at
    /// different speeds; native movement equivalence still needs verification.
    /// </summary>
    public sealed class MacroEngine
    {
        private readonly MelonLogger.Instance _log;
        private readonly TimeController _time;
        private readonly IMacroGameSetup _setup;

        private MacroFile _macro = new MacroFile();
        private int _playhead;
        private bool _sawSceneLoaded;
        private float _prepareStartedAt;
        private const float PrepareTimeoutSeconds = 45f;

        // What the player had dialled in before playback forced 1x/capped.
        // Playback pacing is always real time, but that is a property of the
        // replay, not a new user preference — StopPlayback puts these back.
        private float _speedBeforePlayback = 1f;
        private bool _uncappedBeforePlayback;

        // Warn once per session rather than once per playback — the limitation
        // does not change between attempts, and re-saying it every replay
        // would just be noise.
        private bool _warnedSetupUnverified;

        public MacroMode Mode { get; private set; } = MacroMode.Idle;
        public int Playhead => _playhead;
        public int FrameCount => _macro.Count;
        public MacroFile Macro => _macro;
        public string PendingStatus { get; private set; } = "";
        public float PeakRigidbodyPositionDrift => PhysicsFrameState.MaxPositionDrift;
        public int RigidbodyCorrections => PhysicsFrameState.CorrectionCount;

        /// <summary>
        /// True from the moment a recording stops until it is explicitly saved
        /// or discarded. Nothing writes to disk on its own any more — the run
        /// stays in memory (still playable) while the panel waits for a
        /// decision. <see cref="StartRecording"/> and <see cref="Load"/> both
        /// refuse while this is set, so a decision can't be skipped by accident.
        /// </summary>
        public bool HasUnsavedRecording { get; private set; }

        /// <summary>Speed selected before recording, held fixed until recording stops.</summary>
        public float RecordedAtSpeed { get; private set; } = 1f;

        /// <summary>
        /// Where <see cref="Macro"/> came from — the file a fresh <see cref="Load"/>
        /// named, or the named save path once a recording has been saved.
        /// Null for a freshly started recording that has not been saved
        /// anywhere yet. Exists so the panel can say what F12 will actually
        /// replay instead of leaving that to memory.
        /// </summary>
        public string LoadedPath { get; private set; }

        /// <summary>
        /// The saved-macro catalog, newest first. Rebuilt by
        /// <see cref="RefreshSavedList"/>; starts empty until the constructor's
        /// first scan or a caller asks again.
        /// </summary>
        public IReadOnlyList<SavedMacroInfo> SavedMacros { get; private set; } = Array.Empty<SavedMacroInfo>();

        public MacroEngine(MelonLogger.Instance log, TimeController time, IMacroGameSetup setup = null)
        {
            _log = log;
            _time = time;
            _setup = setup ?? new MacroGameSetup();
            RefreshSavedList();
        }

        public static string MacroDirectory =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "GrannyTAS");

        /// <summary>Where recordings explicitly named by the user are stored.</summary>
        public static string SavedMacroDirectory => Path.Combine(MacroDirectory, "Saved");

        public static string DefaultPath => Path.Combine(MacroDirectory, "macro.grannytas");

        // ---- recording ----------------------------------------------------

        public void StartRecording()
        {
            if (Mode != MacroMode.Idle) return;
            if (HasUnsavedRecording)
            {
                _log.Warning("Save or discard the previous recording before starting a new one.");
                return;
            }
            // The mod engages itself the moment the player has control, so a
            // refusal here means they do not — most often that the wake-up
            // animation is still playing. Recording across it produced macros
            // that began mid-cutscene and desynced on the first replayed frame.
            if (!_time.Enabled)
            {
                _log.Warning($"Not recording — {PlayerGate.Reason}. Wait until you have control of the player.");
                return;
            }

            _macro = new MacroFile
            {
                TickRate = _time.TickRate,
                PhysicsRate = _time.PhysicsRate,
                RngSeed = Environment.TickCount,
                Scene = SafeSceneName(),
                SeedManagerSeed = ReadSeedManagerSeed(),
                // Capture where everyone stands now, so playback can put the
                // world back before replaying a single input.
                Snapshot = WorldSnapshot.Capture(),
            };
            _setup.Capture(_macro);

            // Pin the RNG stream from here. Granny's waypoint choice pulls from
            // UnityEngine.Random, so replay needs the same stream from the same
            // starting point.
            UnityEngine.Random.InitState(_macro.RngSeed);
            _macro.InitialInput.CopyFrom(VirtualInput.Current);
            VirtualInput.ClearBuffer();

            RecordedAtSpeed = _time.Speed;
            _playhead = 0;

            // A fresh recording is not yet any file on disk — clear the name
            // rather than leave it pointing at whatever was loaded before.
            LoadedPath = null;

            // The macro is indexed by frame number, so the frame counter is
            // only meaningful while it agrees with the index being written.
            _time.ResetFrameCount();
            Mode = MacroMode.Recording;
            _time.SimulationRatesLocked = true;

            _log.Msg($"Recording — {_macro.TickRate:0.#}/s sim, {_macro.PhysicsRate:0.#} Hz physics, rng {_macro.RngSeed}, " +
                     $"{_macro.Snapshot.Entities.Count} entities snapshotted.");
        }

        public void StopRecording()
        {
            if (Mode != MacroMode.Recording) return;
            Mode = MacroMode.Idle;
            _time.SimulationRatesLocked = false;

            HasUnsavedRecording = true;
            PendingStatus = "Recording stopped. Name it to save, or discard it.";
            _log.Msg($"Recorded {_macro.Count} frames ({_macro.DurationSeconds:0.##}s at 1x); waiting to save or discard.");
        }

        public bool TrySaveRecording(string name, out string status)
        {
            if (!HasUnsavedRecording)
            {
                status = "There is no unsaved recording.";
                return false;
            }
            if (!TryBuildSavedPath(name, out var path, out status)) return false;
            if (File.Exists(path))
            {
                status = $"A replay named '{Path.GetFileName(path)}' already exists.";
                return false;
            }

            var temporaryPath = Path.Combine(SavedMacroDirectory, ".saving-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(SavedMacroDirectory);
                _macro.Save(temporaryPath);
                File.Move(temporaryPath, path);
                HasUnsavedRecording = false;
                LoadedPath = path;
                PendingStatus = $"Saved as {Path.GetFileName(path)}";
                RefreshSavedList();
                status = PendingStatus;
                _log.Msg($"Saved {_macro.Count} frames -> {path}");
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                status = $"A replay named '{Path.GetFileName(path)}' already exists.";
                _log.Warning(status);
                return false;
            }
            catch (Exception e)
            {
                status = "Could not save replay: " + e.Message;
                _log.Error(status);
                return false;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }

        public void DiscardRecording()
        {
            if (!HasUnsavedRecording) return;
            _macro = new MacroFile();
            _playhead = 0;
            LoadedPath = null;
            HasUnsavedRecording = false;
            PendingStatus = "Recording discarded.";
            _log.Msg(PendingStatus);
        }

        // ---- playback -----------------------------------------------------

        public void StartPlayback()
        {
            if (Mode != MacroMode.Idle) return;
            if (!_time.Enabled)
            {
                _log.Warning($"Not playing back — {PlayerGate.Reason}. Wait until you have control of the player.");
                return;
            }

            if (_macro.Count == 0)
            {
                try
                {
                    if (!File.Exists(DefaultPath))
                    {
                        _log.Warning($"No macro to play — nothing recorded and no file at {DefaultPath}.");
                        return;
                    }
                    _macro = MacroFile.Load(DefaultPath);
                    LoadedPath = DefaultPath;
                    _log.Msg($"Loaded {_macro.Count} frames from {DefaultPath}");
                }
                catch (Exception e)
                {
                    _log.Error($"Could not load macro: {e.Message}");
                    return;
                }
            }

            if (_macro.Count == 0)
            {
                _log.Warning("Macro is empty.");
                return;
            }

            if (!MatchesScene()) return;
            if (!_setup.CanReplayBuild(_macro, out var buildReason))
            {
                _log.Warning("Playback refused: " + buildReason);
                return;
            }

            if (_setup.RequiresRestart(_macro, out var restartReason))
            {
                BeginPreparing(restartReason);
                return;
            }

            StartPlaybackNow();
        }

        private void BeginPreparing(string reason)
        {
            Mode = MacroMode.Preparing;
            // Disable() clears SimulationRatesLocked as part of standing the
            // controller down, so the lock has to be taken after it, not before —
            // otherwise this line does nothing and the rates sit unlocked for the
            // whole reload.
            _time.Disable();
            _time.SimulationRatesLocked = true;
            _playhead = 0;
            _sawSceneLoaded = false;
            _prepareStartedAt = _setup.Realtime;
            PendingStatus = reason;
            try
            {
                _setup.ApplyAndRestart(_macro);
                _log.Msg($"Preparing playback — {reason}; reloading the level.");
            }
            catch (Exception e)
            {
                CancelPreparing("Playback setup failed: " + e.Message);
            }
        }

        /// <summary>Marks an actual scene-load callback; a matching scene name alone is insufficient.</summary>
        public void NotifySceneLoaded(string sceneName)
        {
            if (Mode != MacroMode.Preparing) return;
            if (!string.IsNullOrEmpty(_macro.Scene) && sceneName != _macro.Scene) return;
            _sawSceneLoaded = true;
            PendingStatus = "level loaded; waiting for player control";
        }

        /// <summary>Advances pending setup without ever issuing a macro frame in the old scene.</summary>
        public void UpdatePreparing(bool playerReady)
        {
            if (Mode != MacroMode.Preparing) return;
            if (_setup.Realtime - _prepareStartedAt > PrepareTimeoutSeconds)
            {
                CancelPreparing("Playback setup timed out while waiting for the reloaded level.");
                return;
            }
            if (!_sawSceneLoaded || !playerReady) return;
            if (!MatchesScene()) { CancelPreparing("Reloaded into the wrong scene."); return; }
            if (!_setup.LoadedSetupMatches(_macro, out var reason))
            {
                CancelPreparing("Reloaded setup does not match the macro: " + reason);
                return;
            }
            StartPlaybackNow();
        }

        private void StartPlaybackNow()
        {
            // The item seed is the only piece of level setup actually checked
            // right now — difficulty, selectable version and item preset are
            // placeholders (see MacroGameSetup.VerifiesLevelSetup). Say so once
            // rather than let a silent 0-vs-0 comparison imply they were.
            if (!_setup.VerifiesLevelSetup && !_warnedSetupUnverified)
            {
                _warnedSetupUnverified = true;
                _log.Warning("Difficulty, selectable version, and item preset are not verified before playback " +
                              "(accessors not yet implemented) — match them by hand. Only the item seed is checked.");
            }

            var wasPreparing = Mode == MacroMode.Preparing;
            if (!_time.Enabled) _time.Enable();

            // Preparing locks every user-facing setter. Temporarily release the
            // lock so the macro's simulation rates can be restored internally.
            _time.SimulationRatesLocked = false;

            // Restore the parts of the header that are simulation, not pacing.
            _time.SetTickRate(_macro.TickRate);
            _time.SetPhysicsRate(_macro.PhysicsRate);

            // Put the world back before replaying anything. Without this the
            // inputs are replayed against wherever Granny happens to be now,
            // which is not the run that was recorded.
            if (!_macro.Snapshot.IsEmpty)
            {
                int restored;
                try { restored = _macro.Snapshot.Restore(); }
                catch (Exception e)
                {
                    _log.Error($"Playback aborted during positional restore: {e.Message}. Reload the level before retrying.");
                    if (wasPreparing) CancelPreparing("Playback positional restore failed.");
                    return;
                }
                _log.Msg($"Restored {restored}/{_macro.Snapshot.Entities.Count} entities to their recorded positions.");

                if (restored < _macro.Snapshot.Entities.Count)
                {
                    _log.Warning("Playback aborted: some entities could not be found. Reload the recorded level.");
                    if (wasPreparing) CancelPreparing("Playback positional restore was incomplete.");
                    return;
                }
            }
            else
            {
                if (_macro.HasSetupMetadata)
                {
                    if (wasPreparing) CancelPreparing("Playback refused: macro has no world snapshot.");
                    else _log.Warning("Playback refused: macro has no world snapshot.");
                    return;
                }
                _log.Warning("Legacy macro has no world snapshot; starting position cannot be restored.");
            }

            // Playback is always real time, whatever speed the run was picked at.
            // AI reset methods may consume random values; reset the stream last.
            UnityEngine.Random.InitState(_macro.RngSeed);

            // Save the pacing the player had chosen so StopPlayback can restore
            // it — forcing 1x here is a property of the replay, not something
            // that should overwrite (and, via TasConfig.Sync, persist) as their
            // new preferred speed.
            _speedBeforePlayback = _time.Speed;
            _uncappedBeforePlayback = _time.Uncapped;
            _time.SetSpeed(1f);
            _time.SetPaused(false);

            VirtualInput.ResetForPlayback(_macro.InitialInput);
            PhysicsFrameState.ResetDrift();
            _playhead = 0;
            _time.ResetFrameCount();
            Mode = MacroMode.Playing;
            _time.SimulationRatesLocked = true;

            _log.Msg($"Playing {_macro.Count} frames at 1x ({_macro.DurationSeconds:0.##}s).");
        }

        public void StopPlayback(string reason = null)
        {
            if (Mode == MacroMode.Preparing)
            {
                CancelPreparing(reason ?? "Playback setup cancelled.");
                return;
            }
            if (Mode != MacroMode.Playing) return;
            Mode = MacroMode.Idle;
            _time.SimulationRatesLocked = false;

            // Put back whatever pacing the player had before playback forced
            // 1x/capped — see the comment in StartPlaybackNow. SetSpeed always
            // clears Uncapped, so restore it first and re-apply turbo after.
            _time.SetSpeed(_speedBeforePlayback);
            _time.SetUncapped(_uncappedBeforePlayback);

            var finalStatus = reason ?? $"Playback stopped at frame {_playhead}/{_macro.Count}.";
            PendingStatus = finalStatus;
            _log.Msg(finalStatus);

            // How far the replayed simulation wandered from the recorded run is
            // the number that says whether the replay can be trusted — a pickup
            // that lands on a different frame shows up here first, as a drift in
            // millimetres, long before it shows up as a missing item.
            if (_macro.Frames.Count > 0 && _macro.Frames[0].HasPosition)
                _log.Msg($"Position drift: {VirtualInput.MaxPositionDrift * 1000f:0.###} mm peak, " +
                         $"{VirtualInput.PositionCorrections} frame(s) corrected" +
                         (VirtualInput.PinPosition ? "." : " (correction off — drift was measured, not fixed)."));
            else
                _log.Msg("Macro carries no recorded positions, so replay drift could not be measured. " +
                         "Re-record to enable drift correction.");

            if (_macro.Frames.Count > 0 && _macro.Frames[0].HasPhysicsState)
                _log.Msg($"Rigidbody drift: {PhysicsFrameState.MaxPositionDrift * 1000f:0.######} mm peak, " +
                         $"{PhysicsFrameState.CorrectionCount} correction(s).");
        }

        private void CancelPreparing(string reason)
        {
            if (Mode != MacroMode.Preparing) return;
            Mode = MacroMode.Idle;
            PendingStatus = "";
            _sawSceneLoaded = false;
            _time.SimulationRatesLocked = false;
            _setup.CancelPending();
            if (_time.Enabled) _time.SetPaused(false);
            _log.Warning(reason);
        }

        /// <summary>
        /// Supply this simulated frame's input from the macro. Returns true if
        /// it did, meaning the caller must not advance input from any other
        /// source.
        /// </summary>
        public bool TryDrivePlayback()
        {
            if (Mode != MacroMode.Playing) return false;

            if (_playhead >= _macro.Count)
            {
                StopPlayback($"Playback finished — {_macro.Count} frames.");
                return false;
            }

            var frame = _macro.Frames[_playhead];
            if (frame.HasPhysicsState)
            {
                try
                {
                    if (!PhysicsFrameState.TryApply(frame.PhysicsStates, out var error))
                    {
                        StopPlayback($"Playback aborted at frame {_playhead}: physics desync — {error}.");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    StopPlayback($"Playback aborted at frame {_playhead}: physics checkpoint failed — {e.Message}");
                    return false;
                }
            }

            VirtualInput.AdvanceFrom(frame);
            _playhead++;
            return true;
        }

        /// <summary>
        /// Append this simulated frame to the recording. Call *after* input has
        /// been advanced, so there is something to capture.
        /// </summary>
        public void CaptureFrame()
        {
            if (Mode != MacroMode.Recording) return;

            // Includes the absolute camera/body pose captured before gameplay.
            var copy = new InputFrame();
            copy.CopyFrom(VirtualInput.ForRecord);

            // EnumerateActive throws InvalidOperationException on a duplicate
            // rigidbody identity (two loaded scenes can have root objects at the
            // same sibling index) — this runs every OnUpdate, so an uncaught
            // throw here would escape the mod's update loop. Mirror the
            // playback-side guard in TryDrivePlayback: fail the recording
            // cleanly instead of adding a half-captured frame.
            try
            {
                PhysicsFrameState.CaptureInto(copy.PhysicsStates);
            }
            catch (Exception e)
            {
                _log.Error($"Recording stopped at frame {_macro.Count}: rigidbody checkpoint failed — {e.Message}");
                StopRecording();
                return;
            }

            copy.HasPhysicsState = true;
            _macro.Frames.Add(copy);
            _playhead = _macro.Count;
        }

        public void Load(string path)
        {
            if (Mode != MacroMode.Idle)
            {
                _log.Warning("Stop the active recording or playback before loading a macro.");
                return;
            }
            if (HasUnsavedRecording)
            {
                _log.Warning("Save or discard the current recording before loading another replay.");
                return;
            }
            try
            {
                _macro = MacroFile.Load(path);
                _playhead = 0;
                LoadedPath = path;
                _log.Msg($"Loaded {_macro.Count} frames from {path}");
            }
            catch (Exception e)
            {
                _log.Error($"Could not load macro: {e.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the saved-macro catalog from disk. Called once at
        /// construction and after each successful named save; also exposed for
        /// the panel's Refresh button so hand-copied files appear immediately.
        /// </summary>
        public void RefreshSavedList()
        {
            var found = new List<SavedMacroInfo>();

            void Scan(string dir)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.GetFiles(dir, "*.grannytas"))
                {
                    try
                    {
                        var header = MacroFile.PeekHeader(file);
                        var duration = header.TickRate > 0f ? header.Frames / header.TickRate : 0f;
                        found.Add(new SavedMacroInfo(file, header.Frames, duration, header.Scene,
                            File.GetLastWriteTimeUtc(file)));
                    }
                    catch (Exception e)
                    {
                        _log.Warning($"Skipping unreadable saved macro '{file}': {e.Message}");
                    }
                }
            }

            // MacroDirectory itself only ever holds DefaultPath as a loose
            // *.grannytas file — GetFiles is non-recursive, so this does not
            // also pick up SavedMacroDirectory's contents a second time.
            Scan(MacroDirectory);
            Scan(SavedMacroDirectory);

            found.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            SavedMacros = found;
        }

        // ---- helpers ------------------------------------------------------

        private static bool TryBuildSavedPath(string name, out string path, out string error)
        {
            path = null;
            var trimmed = (name ?? "").Trim();
            const string extension = ".grannytas";
            if (trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - extension.Length).TrimEnd();

            if (trimmed.Length == 0)
            {
                error = "Enter a replay name.";
                return false;
            }
            if (trimmed == "." || trimmed == ".." || Path.GetFileName(trimmed) != trimmed ||
                trimmed.IndexOf('/') >= 0 || trimmed.IndexOf('\\') >= 0)
            {
                error = "The replay name must be a filename, not a path.";
                return false;
            }

            const string windowsInvalid = "<>:\"/\\|?*";
            foreach (var c in trimmed)
            {
                if (c < 32 || windowsInvalid.IndexOf(c) >= 0 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                {
                    error = $"The replay name contains an invalid character: '{c}'.";
                    return false;
                }
            }
            if (trimmed.EndsWith(".", StringComparison.Ordinal) || trimmed.EndsWith(" ", StringComparison.Ordinal))
            {
                error = "The replay name cannot end with a dot or space.";
                return false;
            }

            var reservedStem = trimmed;
            var firstDot = reservedStem.IndexOf('.');
            if (firstDot >= 0) reservedStem = reservedStem.Substring(0, firstDot);
            switch (reservedStem.ToUpperInvariant())
            {
                case "CON": case "PRN": case "AUX": case "NUL":
                case "COM1": case "COM2": case "COM3": case "COM4": case "COM5":
                case "COM6": case "COM7": case "COM8": case "COM9":
                case "LPT1": case "LPT2": case "LPT3": case "LPT4": case "LPT5":
                case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                    error = "That replay name is reserved by Windows.";
                    return false;
            }

            var root = Path.GetFullPath(SavedMacroDirectory) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(Path.Combine(SavedMacroDirectory, trimmed + extension));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = null;
                error = "The replay name must stay inside the saved replay folder.";
                return false;
            }

            error = "";
            return true;
        }

        private bool MatchesScene()
        {
            var scene = SafeSceneName();
            if (!string.IsNullOrEmpty(_macro.Scene) && scene != _macro.Scene)
            {
                _log.Warning($"Playback refused: macro scene '{_macro.Scene}', current scene '{scene}'.");
                return false;
            }

            return true;
        }

        private static string SafeSceneName()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Read <c>SeedManager.Seed</c> if the level has one. Absence is normal
        /// (menus, and levels without a randomizer), so this stays quiet.
        /// </summary>
        private static int ReadSeedManagerSeed()
        {
            try
            {
                var sm = UnityEngine.Object.FindObjectOfType<Il2Cpp.SeedManager>();
                return sm != null ? sm.Seed : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
