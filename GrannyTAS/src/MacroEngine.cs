using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyTAS
{
    /// <summary>
    /// <c>Aligning</c> is the short pre-roll between pressing play and the first
    /// replayed frame, in which the engine's fixed-step phase is moved onto the
    /// recording's (see <see cref="MacroEngine.UpdateAligning"/>).
    /// </summary>
    public enum MacroMode { Idle, Recording, Preparing, Aligning, Playing }

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
    /// different speeds, bit for bit.
    ///
    /// Every recorded frame also carries a <see cref="FrameTrace"/> of the world
    /// at its boundary. On replay the same trace is captured before anything is
    /// corrected and compared, so the end of a replay can say whether it was the
    /// same run — or which frame and which system it stopped being the same on.
    /// See <see cref="SyncReport"/> and docs/replay-sync.md.
    /// </summary>
    public sealed class MacroEngine
    {
        private readonly MelonLogger.Instance _log;
        private readonly TimeController _time;
        private readonly IMacroGameSetup _setup;
        private readonly IPickupRecovery _pickupRecovery;

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

        private bool _playbackPacingSaved;

        // Warn once per session rather than once per playback — the limitation
        // does not change between attempts, and re-saying it every replay
        // would just be noise.
        private bool _warnedSetupUnverified;

        // ---- fixed-step phase alignment (Aligning) ----
        // The phase each replayed frame starts at must equal the recording's,
        // or FixedUpdate — the AI, rigidbody physics, and with
        // autoSyncTransforms off, the moment moved colliders become visible to
        // raycasts — lands on different frames. See UpdateAligning.
        private const double AlignTolerance = 5e-11;
        private const double CoarseHopThreshold = 2e-4;
        private const double FineHopMargin = 1e-5;
        private const int MaxAlignAttempts = 12;
        private double _alignTarget;
        private double _alignFrame0Phase;
        private bool _alignReady;
        private int _alignReadyFrame;
        private int _alignAttempts;
        private double _alignLastTime;
        private double _startPhaseError = double.NaN;

        private SyncReport _report;
        private readonly InputFrame _poseScratch = new InputFrame();

        // ---- snapshots (rewind by re-simulation) ----
        // A snapshot is a bookmark into the recording, not a copy of the world:
        // loading one restarts the level and replays frames 0..N-1 at turbo,
        // which reproduces the world at frame N exactly as a replay of the
        // finished macro will — anything less would record a continuation
        // the final replay could never reach. See LoadSnapshot.
        private bool _rewinding;
        private bool _handoffPending;

        public MacroMode Mode { get; private set; } = MacroMode.Idle;
        public int Playhead => _playhead;
        public int FrameCount => _macro.Count;
        public MacroFile Macro => _macro;
        public string PendingStatus { get; private set; } = "";
        public float PeakRigidbodyPositionDrift => PhysicsFrameState.MaxPositionDrift;
        public int RigidbodyCorrections => PhysicsFrameState.CorrectionCount;

        /// <summary>The report being filled by the replay in progress, or null.</summary>
        public SyncReport LiveReport => Mode == MacroMode.Playing ? _report : null;

        /// <summary>The report of the most recent finished replay, or null.</summary>
        public SyncReport LastReport { get; private set; }

        /// <summary>Where <see cref="LastReport"/> was written, or null if it could not be.</summary>
        public string LastReportPath { get; private set; }

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

        /// <summary>Frame the snapshot returns to (frames kept: 0..N-1), or -1 for none.</summary>
        public int SnapshotFrame { get; private set; } = -1;

        public bool HasSnapshot => SnapshotFrame >= 0;

        /// <summary>
        /// Whether dying loads the snapshot by itself. One-shot: an automatic
        /// load disarms it, and only taking a new snapshot arms it again, so a
        /// spot that keeps killing you cannot loop forever.
        /// </summary>
        public bool SnapshotArmed { get; private set; }

        /// <summary>True from a snapshot load until recording resumes at the snapshot frame.</summary>
        public bool IsRewinding => _rewinding;

        /// <summary>Whether a snapshot load is possible right now.</summary>
        public bool CanLoadSnapshot =>
            HasSnapshot && SnapshotFrame <= _macro.Count &&
            (Mode == MacroMode.Recording || (Mode == MacroMode.Idle && HasUnsavedRecording));

        public MacroEngine(MelonLogger.Instance log, TimeController time, IMacroGameSetup setup = null,
            IPickupRecovery pickupRecovery = null)
        {
            _log = log;
            _time = time;
            _setup = setup ?? new MacroGameSetup();
            _pickupRecovery = pickupRecovery ?? new GamePickupRecovery();
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
                // And what state the player's rig is in — the head-bob phase
                // above all, which decides where the pickup ray starts.
                Rig = PlayerRigSnapshot.Capture(PlayerGate.Player),
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
            ClearSnapshot();

            // A fresh recording is not yet any file on disk — clear the name
            // rather than leave it pointing at whatever was loaded before.
            LoadedPath = null;

            // The macro is indexed by frame number, so the frame counter is
            // only meaningful while it agrees with the index being written.
            _time.ResetFrameCount();
            SyncProbe.Begin();
            SyncTracker.BeginRecording();
            Mode = MacroMode.Recording;
            _time.SimulationRatesLocked = true;

            _log.Msg($"Recording — {_macro.TickRate:0.#}/s sim, {_macro.PhysicsRate:0.#} Hz physics, rng {_macro.RngSeed}, " +
                     $"{_macro.Snapshot.Entities.Count} entities snapshotted, player rig " +
                     $"{_macro.Rig.Animations.Count} animation states / {_macro.Rig.Transforms.Count} transforms.");
        }

        public void StopRecording()
        {
            if (Mode != MacroMode.Recording) return;
            Mode = MacroMode.Idle;
            _time.SimulationRatesLocked = false;
            SyncTracker.Stop();
            SyncProbe.End();

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
                ClearSnapshot();
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
            ClearSnapshot();
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

            BeginPlayback();
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
            BeginPlayback();
        }

        /// <summary>
        /// Everything a replay changes before its first frame: timing into the
        /// macro's rates at 1x, unpaused, locked. Separated from
        /// <see cref="StartPlaybackNow"/> because the fixed step has to be at the
        /// macro's rate before the phase can be aligned to it.
        /// </summary>
        private void BeginPlayback()
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

            if (!_time.Enabled) _time.Enable();

            // Preparing locks every user-facing setter. Temporarily release the
            // lock so the macro's simulation rates can be restored internally.
            _time.SimulationRatesLocked = false;

            // Restore the parts of the header that are simulation, not pacing.
            _time.SetTickRate(_macro.TickRate);
            _time.SetPhysicsRate(_macro.PhysicsRate);

            // Save the pacing the player had chosen so StopPlayback can restore
            // it — forcing 1x here is a property of the replay, not something
            // that should overwrite (and, via TasConfig.Sync, persist) as their
            // new preferred speed.
            if (!_playbackPacingSaved)
            {
                _speedBeforePlayback = _time.Speed;
                _uncappedBeforePlayback = _time.Uncapped;
                _playbackPacingSaved = true;
            }
            _time.SetSpeed(1f);
            // A rewind is not for watching: fast-forward as fast as the machine
            // renders. Pacing never changes the simulation (the frame delta is
            // pinned), so this replays the same frames a 1x replay would.
            if (_rewinding) _time.SetUncapped(true);
            _time.SetPaused(false);
            _time.SimulationRatesLocked = true;

            // Rewinding to frame 0 has nothing to replay, so no pre-roll either:
            // recording resumes on a fresh clock exactly like StartRecording.
            if (_macro.Count == 0)
            {
                _startPhaseError = double.NaN;
                StartPlaybackNow();
                return;
            }

            var first = _macro.Frames[0].Trace;
            if (first != null && first.HasClock && _time.FixedStep > 0)
            {
                BeginAligning(first);
                return;
            }

            _startPhaseError = double.NaN;
            StartPlaybackNow();
        }

        private void BeginAligning(FrameTrace first)
        {
            var wasPreparing = Mode == MacroMode.Preparing;
            Mode = MacroMode.Aligning;
            if (wasPreparing) _sawSceneLoaded = false;

            // Frame 0 will run with the replay's delta; the frame before it must
            // leave the phase exactly one such delta short of the recording's.
            var replayDelta = 1f / _time.TickRate;
            if (!Bits.Same(first.Dt, replayDelta))
                _log.Warning($"Frame 0 was recorded with a {first.Dt:R}s delta but replays with {replayDelta:R}s " +
                             "(recorded by an older build with an inexact clock); the fixed-step pattern may drift.");

            _alignFrame0Phase = first.Phase;
            _alignTarget = Wrap(first.Phase - replayDelta, _time.FixedStep);
            _alignReady = false;
            _alignAttempts = 0;
            _alignLastTime = double.NaN;
            _alignReadyFrame = int.MinValue;
            _startPhaseError = double.NaN;
            _time.BeginAlignment();
            PendingStatus = "aligning the fixed-step phase";
        }

        /// <summary>
        /// Pre-roll, called once per engine frame while <see cref="MacroMode.Aligning"/>.
        ///
        /// Unity runs FixedUpdate whenever the fixed clock falls a whole step
        /// behind the frame clock, so with both deltas pinned, *which* frames get
        /// a FixedUpdate is decided entirely by how far into a fixed step the
        /// run starts — <c>time - fixedTime</c>. The recording started wherever
        /// the engine happened to be; a replay started wherever it happens to be
        /// now. At 60 frames against 50 Hz that is six different patterns, and
        /// the enemies' AI, every rigidbody step, and (with the game's
        /// autoSyncTransforms off) the moment a moved drawer or door becomes
        /// visible to the pickup ray all shift by a frame between them.
        ///
        /// So before frame 0 the engine is run for one or two frames of a
        /// computed, non-standard delta, with no input and no macro frame
        /// counted: first a coarse hop that lands just short of the recording's
        /// phase, then a tiny exact one onto it. The world snapshot is restored
        /// afterwards, so the pre-roll's few milliseconds of AI time leave no
        /// positional trace.
        /// </summary>
        public void UpdateAligning(bool simulating)
        {
            if (Mode != MacroMode.Aligning) return;
            var step = _time.FixedStep;

            if (!_alignReady)
            {
                var remaining = Wrap(_alignTarget - _time.FixedPhase, step);
                var error = Math.Min(remaining, step - remaining);

                // Only a frame that actually moved the clock uses up an attempt;
                // a pause during pre-roll just waits.
                var now = _time.EngineTime;
                if (!Bits.Same(now, _alignLastTime))
                {
                    _alignLastTime = now;
                    _alignAttempts++;
                }
                if (error <= AlignTolerance || _alignAttempts > MaxAlignAttempts)
                {
                    if (error > AlignTolerance)
                        _log.Warning($"Could not align the fixed-step phase (still {error * 1e9:0.###} ns out); replaying anyway.");
                    _alignReady = true;
                    _alignReadyFrame = _time.EngineFrame;
                    _time.EndAlignment();
                    return;
                }

                // Land a little short with the coarse hop so the last hop is
                // tiny: a float delta is only exact relative to its own size.
                var hop = remaining > CoarseHopThreshold ? remaining - FineHopMargin : remaining;
                _time.SetAlignmentDelta((float)hop);
                return;
            }

            // The first simulated frame after normal timing resumed is frame 0.
            if (!simulating || _time.EngineFrame <= _alignReadyFrame) return;

            var phaseError = Math.Abs(Wrap(_time.FixedPhase - _alignFrame0Phase + step / 2, step) - step / 2);
            if (phaseError > AlignTolerance && _alignAttempts < MaxAlignAttempts)
            {
                // Something advanced the clock in between (the game's pause menu
                // opening, most likely). Go round again rather than start wrong.
                _alignReady = false;
                _time.BeginAlignment();
                return;
            }

            _startPhaseError = phaseError;
            StartPlaybackNow();
        }

        private static double Wrap(double value, double step)
        {
            if (!(step > 0)) return 0;
            var r = value % step;
            return r < 0 ? r + step : r;
        }

        private void StartPlaybackNow()
        {
            // Put the world back before replaying anything. Without this the
            // inputs are replayed against wherever Granny happens to be now,
            // which is not the run that was recorded.
            if (!_macro.Snapshot.IsEmpty)
            {
                int restored;
                try { restored = _macro.Snapshot.Restore(); }
                catch (Exception e)
                {
                    AbortStart($"Playback aborted during positional restore: {e.Message}. Reload the level before retrying.");
                    return;
                }
                _log.Msg($"Restored {restored}/{_macro.Snapshot.Entities.Count} entities to their recorded positions.");

                if (restored < _macro.Snapshot.Entities.Count)
                {
                    AbortStart("Playback aborted: some entities could not be found. Reload the recorded level.");
                    return;
                }
            }
            else
            {
                if (_macro.HasSetupMetadata)
                {
                    AbortStart("Playback refused: macro has no world snapshot.");
                    return;
                }
                _log.Warning("Legacy macro has no world snapshot; starting position cannot be restored.");
            }

            var rigNote = "";
            if (!_macro.Rig.IsEmpty)
            {
                try { rigNote = _macro.Rig.Restore(PlayerGate.Player); }
                catch (Exception e) { rigNote = "player rig restore failed: " + e.Message; }
                _log.Msg("Restored " + rigNote + ".");
            }

            // Playback is always real time, whatever speed the run was picked at.
            // AI reset methods may consume random values; reset the stream last.
            UnityEngine.Random.InitState(_macro.RngSeed);

            VirtualInput.ResetForPlayback(_macro.InitialInput);
            PhysicsFrameState.ResetDrift();

            _report = new SyncReport
            {
                MacroName = string.IsNullOrEmpty(LoadedPath) ? "" : Path.GetFileName(LoadedPath),
                MacroFrames = _macro.Count,
                TracesPresent = _macro.HasTraces,
                StartPhaseError = _startPhaseError,
                RigNote = rigNote,
            };
            SyncProbe.Begin();
            SyncTracker.BeginReplay(_report, _pickupRecovery);

            _playhead = 0;
            _time.ResetFrameCount();
            Mode = MacroMode.Playing;
            _time.SimulationRatesLocked = true;
            PendingStatus = "";

            _log.Msg((_rewinding ? $"Rewinding — fast-forwarding {_macro.Count} frames" : $"Playing {_macro.Count} frames at 1x ({_macro.DurationSeconds:0.##}s)") +
                     (double.IsNaN(_startPhaseError) ? "." : $", fixed-step phase aligned to within {_startPhaseError * 1e9:0.###} ns."));

            if (_rewinding && _macro.Count == 0) _handoffPending = true;
        }

        /// <summary>Undo <see cref="BeginPlayback"/> when the replay cannot start after all.</summary>
        private void AbortStart(string reason)
        {
            if (Mode == MacroMode.Preparing)
            {
                CancelPreparing(reason);
                return;
            }

            _time.EndAlignment();
            Mode = MacroMode.Idle;
            _time.SimulationRatesLocked = false;
            RestorePacing();
            PendingStatus = reason;
            _log.Warning(reason);
            RewindFailed(reason);
        }

        private void RestorePacing()
        {
            if (!_playbackPacingSaved) return;
            _playbackPacingSaved = false;
            // SetSpeed always clears Uncapped, so restore it first and re-apply turbo after.
            _time.SetSpeed(_speedBeforePlayback);
            _time.SetUncapped(_uncappedBeforePlayback);
        }

        public void StopPlayback(string reason = null)
        {
            if (Mode == MacroMode.Preparing)
            {
                CancelPreparing(reason ?? "Playback setup cancelled.");
                return;
            }
            if (Mode == MacroMode.Aligning)
            {
                AbortStart(reason ?? "Playback cancelled before the first frame.");
                return;
            }
            if (Mode != MacroMode.Playing) return;
            Mode = MacroMode.Idle;
            _time.SimulationRatesLocked = false;

            // Put back whatever pacing the player had before playback forced
            // 1x/capped — see the comment in BeginPlayback.
            RestorePacing();

            var finalStatus = reason ?? $"Playback stopped at frame {_playhead}/{_macro.Count}.";
            PendingStatus = finalStatus;
            _log.Msg(finalStatus);

            // How far the replayed simulation wandered from the recorded run is
            // the number that says whether the replay can be trusted — a pickup
            // that lands on a different frame shows up here first, as a drift in
            // millimetres, long before it shows up as a missing item.
            if (_macro.Frames.Count > 0 && _macro.Frames[0].HasPosition)
                _log.Msg($"Position drift: {VirtualInput.MaxPositionDrift * 1000f:0.######} mm peak, " +
                         $"{VirtualInput.PositionCorrections} frame(s) corrected" +
                         (VirtualInput.PinPosition ? "." : " (correction off — drift was measured, not fixed)."));
            else
                _log.Msg("Macro carries no recorded positions, so replay drift could not be measured. " +
                         "Re-record to enable drift correction.");

            if (_macro.Frames.Count > 0 && _macro.Frames[0].HasPhysicsState)
                _log.Msg($"Rigidbody drift: {PhysicsFrameState.MaxPositionDrift * 1000f:0.######} mm peak, " +
                         $"{PhysicsFrameState.CorrectionCount} correction(s).");

            FinishReport();
            RewindFailed(finalStatus);
        }

        private void FinishReport(bool keepProbe = false)
        {
            SyncTracker.Stop();
            if (!keepProbe) SyncProbe.End();
            var report = _report;
            _report = null;
            if (report == null) return;

            report.PositionCorrections = VirtualInput.PositionCorrections;
            report.RigidbodyCorrections = PhysicsFrameState.CorrectionCount;
            if (VirtualInput.TeleportByToggle)
                report.TeleportNote = "corrections fell back to disabling the CharacterController (Physics.SyncTransforms did not move it)";

            LastReport = report;
            LastReportPath = WriteReport(report);
            var summary = report.Summary();
            if (report.TracesPresent && !report.Exact) _log.Warning(summary);
            else _log.Msg(summary);
            if (LastReportPath != null) _log.Msg($"Sync report: {LastReportPath}");
        }

        private string WriteReport(SyncReport report)
        {
            if (report.FramesReplayed == 0) return null;
            try
            {
                var dir = Path.Combine(MacroDirectory, "Reports");
                Directory.CreateDirectory(dir);
                var stem = string.IsNullOrEmpty(LoadedPath) ? "unsaved" : Path.GetFileNameWithoutExtension(LoadedPath);
                var path = Path.Combine(dir, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllLines(path, report.Lines());
                return path;
            }
            catch (Exception e)
            {
                _log.Warning("Could not write the sync report: " + e.Message);
                return null;
            }
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
            RestorePacing();
            _log.Warning(reason);
            RewindFailed(reason);
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
                SyncTracker.CompleteReplay();
                StopPlayback($"Playback finished — {_macro.Count} frames.");
                return false;
            }

            var frame = _macro.Frames[_playhead];

            // Measure first, correct second: everything the sync check compares
            // is read before a single corrective write, so a correction can
            // never hide the divergence it corrects.
            CompareBoundary(frame);
            SyncTracker.ReplayIssued(_playhead, frame.Trace);

            if (frame.HasPhysicsState)
            {
                try
                {
                    if (!PhysicsFrameState.TryApply(frame.PhysicsStates, out var error))
                    {
                        StopPlayback($"Playback aborted at frame {_playhead}: invalid rigidbody checkpoint — {error}.");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    StopPlayback($"Playback aborted at frame {_playhead}: physics checkpoint failed — {e.Message}");
                    return false;
                }
                ReportRigidbodies(frame);
            }

            VirtualInput.AdvanceFrom(frame);
            _playhead++;

            // The last bookmarked frame is out. The switch back to recording
            // waits for the start of the next engine frame (TakeRewindHandoff),
            // so this frame's Update — and every pickup and ray in it — still
            // belongs to the replay that the recording already holds.
            if (_rewinding && _playhead >= _macro.Count) _handoffPending = true;
            return true;
        }

        // ---- snapshots --------------------------------------------------------

        /// <summary>Bookmark the current frame of the recording and arm auto-load on death.</summary>
        public void SaveSnapshot()
        {
            if (Mode != MacroMode.Recording)
            {
                _log.Warning("Snapshots are taken while recording.");
                return;
            }
            SnapshotFrame = _macro.Count;
            SnapshotArmed = true;
            PendingStatus = $"Snapshot at frame {SnapshotFrame} — dying loads it once.";
            _log.Msg(PendingStatus);
        }

        /// <summary>
        /// Return to the snapshot: drop every frame recorded after it, restart
        /// the level, fast-forward the kept frames, and hand control back —
        /// paused, still recording — at the snapshot frame.
        ///
        /// Works while recording, and also right after a recording stopped
        /// (the usual case after a death with auto-load disarmed). If the
        /// rewind cannot finish, the shortened recording is kept unsaved.
        /// </summary>
        public void LoadSnapshot(string why = null)
        {
            if (!HasSnapshot)
            {
                _log.Warning("No snapshot to load — take one while recording first.");
                return;
            }
            if (!CanLoadSnapshot)
            {
                _log.Warning(SnapshotFrame > _macro.Count
                    ? "The snapshot is past the end of this recording."
                    : "Load a snapshot while recording, or right after the recording stopped.");
                return;
            }

            if (Mode == MacroMode.Recording)
            {
                Mode = MacroMode.Idle;
                SyncTracker.Stop();
                SyncProbe.End();
            }
            HasUnsavedRecording = false;

            var dropped = _macro.Count - SnapshotFrame;
            _macro.Frames.RemoveRange(SnapshotFrame, dropped);
            _rewinding = true;
            _handoffPending = false;

            _log.Msg($"Loading snapshot{(why != null ? " (" + why + ")" : "")}: dropped {dropped} frame(s), " +
                     $"replaying {SnapshotFrame} from a level restart.");
            BeginPreparing($"rewinding to frame {SnapshotFrame}");
        }

        /// <summary>
        /// Death while recording. Loads the snapshot if it is armed, and disarms
        /// it. Returns true when a rewind has started, so the caller must not
        /// stop the recording.
        /// </summary>
        public bool TryAutoLoadSnapshot()
        {
            if (Mode != MacroMode.Recording || !SnapshotArmed || !CanLoadSnapshot) return false;
            SnapshotArmed = false;
            LoadSnapshot("caught — auto-load");
            return _rewinding;
        }

        /// <summary>
        /// Call at the start of an engine frame, before the clock decides
        /// whether it simulates. When the rewind has replayed its last frame,
        /// switches back to recording and returns true: the caller pauses, so
        /// the snapshot frame is the next one recorded and nothing runs on
        /// until the player steps or resumes.
        /// </summary>
        public bool TakeRewindHandoff()
        {
            if (!_handoffPending || Mode != MacroMode.Playing) return false;
            _handoffPending = false;
            _rewinding = false;

            // Close and score the replayed stretch; keep the probe running so
            // the next recorded frame's clock trace continues from this one.
            SyncTracker.CompleteReplay();
            Mode = MacroMode.Idle;
            FinishReport(keepProbe: true);

            _time.SimulationRatesLocked = false;
            RestorePacing();
            RecordedAtSpeed = _time.Speed;
            SyncTracker.BeginRecording();
            Mode = MacroMode.Recording;
            _time.SimulationRatesLocked = true;
            _playhead = _macro.Count;
            // Playback resets the counter on the engine frame that issues frame
            // 0, so it trails the playhead by one; a recording needs it equal to
            // the index the next captured frame is written at.
            _time.ResetFrameCount(_macro.Count);

            var verdict = LastReport == null || !LastReport.TracesPresent || LastReport.Exact
                ? "frames reproduced exactly"
                : "WARNING: the fast-forward diverged from the recording — see the sync report";
            PendingStatus = $"Back at frame {_macro.Count}, paused and recording ({verdict}).";
            if (verdict.StartsWith("WARNING")) _log.Warning(PendingStatus);
            else _log.Msg(PendingStatus);
            return true;
        }

        private void RewindFailed(string reason)
        {
            if (!_rewinding) return;
            _rewinding = false;
            _handoffPending = false;
            HasUnsavedRecording = true;
            PendingStatus = $"Rewind did not finish ({reason}). The recording is kept up to frame {_macro.Count} — " +
                            "load the snapshot again, or save/discard it.";
            _log.Warning(PendingStatus);
        }

        private void ClearSnapshot()
        {
            SnapshotFrame = -1;
            SnapshotArmed = false;
        }

        private void CompareBoundary(InputFrame frame)
        {
            if (_report == null) return;
            var actual = new FrameTrace();
            try { SyncProbe.CaptureBoundary(actual); }
            catch { actual = null; }
            _poseScratch.Clear();
            VirtualInput.CapturePose(_poseScratch);
            _report.CompareBoundary(_playhead, frame, _report.TracesPresent ? actual : null, _poseScratch);
            _report.FramesReplayed = _playhead + 1;
        }

        private void ReportRigidbodies(InputFrame frame)
        {
            if (_report == null) return;
            if (PhysicsFrameState.LastMissing > 0)
            {
                if (_report.MissingRigidbodies == 0) _report.FirstMissingRigidbody = PhysicsFrameState.LastFirstMissing;
                _report.MissingRigidbodies += PhysicsFrameState.LastMissing;
            }

            if (PhysicsFrameState.LastDiffering > 0 || PhysicsFrameState.LastMissing > 0)
            {
                var note = PhysicsFrameState.LastDiffering > 0
                    ? $"{PhysicsFrameState.LastDiffering} body(ies) differ; worst {PhysicsFrameState.LastWorstNote}"
                    : "";
                if (PhysicsFrameState.LastMissing > 0)
                    note += (note.Length > 0 ? "; " : "") +
                            $"{PhysicsFrameState.LastMissing} recorded body(ies) missing, first '{PhysicsFrameState.LastFirstMissing}'";
                _report.Differ(SyncReport.Area.Rigidbodies, _playhead, PhysicsFrameState.LastWorstPosition, note);
            }
            else if (frame.PhysicsStates.Count > 0) _report.Match(SyncReport.Area.Rigidbodies);
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

            // A checkpoint failure runs every OnUpdate, so an uncaught throw
            // here would escape the mod's update loop. Mirror the playback-side
            // guard in TryDrivePlayback: fail the recording cleanly instead of
            // adding a half-captured frame.
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

            // The same boundary state a replay will be compared against. Read
            // only; a trace that cannot be taken costs the check, not the take.
            var trace = new FrameTrace();
            try { SyncProbe.CaptureBoundary(trace); }
            catch (Exception e) { _log.Warning($"Frame {_macro.Count}: sync trace incomplete — {e.Message}"); }
            copy.Trace = trace;

            _macro.Frames.Add(copy);
            SyncTracker.RecordIssued(_macro.Count - 1, trace);
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
                ClearSnapshot();
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
