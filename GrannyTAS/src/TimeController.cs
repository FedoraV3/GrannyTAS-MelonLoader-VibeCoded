using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Owns simulation timing and wall-clock pacing. The render cap chooses
    /// frames per real second; timeScale communicates that pacing to the engine.
    /// captureDeltaTime compensates for timeScale so the scaled Update delta
    /// remains 1/TickRate, while fixedDeltaTime stays 1/PhysicsRate.
    /// Matching these deltas is necessary but does not prove deterministic
    /// replay: native navigation and fixed-update phase need runtime checks.
    /// </summary>
    public sealed class TimeController
    {
        /// <summary>
        /// Simulated frames per simulated second — how much game time each
        /// rendered frame represents. Part of the simulation: changing it
        /// changes the run.
        /// </summary>
        public float TickRate { get; private set; } = 60f;

        /// <summary>
        /// FixedUpdate rate in ticks per simulated second, driving enemy AI.
        /// Part of the simulation. Defaults to whatever the game itself used.
        /// </summary>
        public float PhysicsRate { get; private set; } = 50f;

        /// <summary>
        /// Wall-clock playback speed. 1.0 is real time, 0.1 is ten times slower,
        /// 4.0 is four times faster. Purely a pacing control — it does not touch
        /// the simulation, which is what lets a macro recorded at 0.1 replay
        /// identically at 1.0.
        /// </summary>
        public float Speed { get; private set; } = 1f;

        /// <summary>
        /// Render as fast as the machine allows. Wall-clock pacing is unknown
        /// in this mode, so native navigation equivalence is not guaranteed.
        /// </summary>
        public bool Uncapped { get; private set; }

        public bool Enabled { get; private set; }
        public bool Paused { get; private set; }
        /// <summary>
        /// True while the game's own pause menu is open. This freezes the TAS
        /// clock without changing its transport state or queued frame steps.
        /// </summary>
        public bool GamePaused { get; private set; }
        public bool SimulationRatesLocked { get; set; }
        public bool TimingMismatch { get; private set; }

        /// <summary>Whether the last checked Update delta was exactly <c>1 / TickRate</c>, bit for bit.</summary>
        public bool DeltaExact { get; private set; } = true;
        public float ObservedFrameDelta { get; private set; }

        /// <summary>
        /// Whether <see cref="PhysicsRate"/> is a value the user chose rather
        /// than the one adopted from the game.
        ///
        /// <see cref="Enable"/> adopts the game's own <c>fixedDeltaTime</c> so
        /// that engaging the mod does not, by itself, change the AI — but that
        /// adoption must not overwrite a rate the user set deliberately while
        /// standing by. So it happens only while this is false.
        /// </summary>
        public bool PhysicsRateOverridden { get; private set; }

        /// <summary>
        /// True when a setting was changed while standing down, so it is holding
        /// rather than in force. The panel says so; <see cref="Enable"/> clears
        /// it by applying everything at once.
        /// </summary>
        public bool HasPendingChanges { get; private set; }

        /// <summary>Frames advanced since <see cref="Enable"/>. This is the macro's time axis.</summary>
        public long FrameCount { get; private set; }

        /// <summary>The frame cap actually handed to Unity; -1 when uncapped.</summary>
        public int EffectiveFpsCap => Uncapped ? -1 : Mathf.Max(1, Mathf.RoundToInt(TickRate * Speed));

        /// <summary>
        /// The cap while paused. <see cref="EffectiveFpsCap"/> only means
        /// "wall-clock speed" on frames that simulate, and no frame simulates
        /// while paused - so applying it there buys nothing and costs the
        /// interface: at 0.01x of 60/s it is a 1 fps cap, which means a step
        /// hotkey can take a second to register and the input latch samples the
        /// keyboard once per second, dropping quick taps outright.
        /// </summary>
        public int PausedFpsCap => Uncapped ? -1 : Mathf.Max(60, EffectiveFpsCap);

        /// <summary>
        /// The engine timeScale while running: the power of two nearest the
        /// capped speed (<c>EffectiveFpsCap / TickRate</c>).
        ///
        /// Engine-owned systems still see slow motion as slow motion, but the
        /// scale is quantised so that <c>captureDeltaTime * timeScale</c> is
        /// exactly <c>1 / TickRate</c> — in float *and* in double. With the
        /// unquantised scale (29/60 at 0.48x) the float product happened to
        /// round back to 1/60 but the double product did not, so the engine's
        /// double-precision clock advanced by a different amount per frame
        /// while recording at 0.48x than while replaying at 1x. That moves the
        /// fixed-step phase a few hundred picoseconds per frame, and over a
        /// long run is enough to move a FixedUpdate onto a neighbouring frame.
        /// Dividing by a power of two only changes the exponent, so the
        /// compensated capture delta is exact by construction. Pacing is still
        /// set precisely by the frame cap.
        /// </summary>
        public float RunningTimeScale => Uncapped ? 1f : ExactScale(EffectiveFpsCap / TickRate);

        internal static float ExactScale(float approximate)
        {
            if (!float.IsFinite(approximate) || approximate <= 0f) return 1f;
            var exponent = (int)System.Math.Round(System.Math.Log(approximate, 2.0));
            exponent = System.Math.Clamp(exponent, -10, 7);
            return (float)System.Math.Pow(2.0, exponent);
        }

        /// <summary>
        /// How far into the current fixed step the engine clock is:
        /// <c>Time.timeAsDouble - Time.fixedTimeAsDouble</c>. With the Update
        /// and fixed deltas pinned, this at the first macro frame decides which
        /// later frames get a FixedUpdate, so a replay has to start from the
        /// recording's value — see <see cref="SetAlignmentDelta"/>.
        /// </summary>
        public double FixedPhase => Time.timeAsDouble - Time.fixedTimeAsDouble;

        /// <summary>The fixed step as the engine accumulates it.</summary>
        public double FixedStep => Time.fixedDeltaTime;

        public int EngineFrame => Time.frameCount;

        /// <summary>The engine's double-precision frame clock.</summary>
        public double EngineTime => Time.timeAsDouble;

        /// <summary>
        /// True while the macro engine is moving the fixed-step phase into place
        /// before a replay. Frames in this state are pre-roll: they advance the
        /// engine clock by whatever <see cref="SetAlignmentDelta"/> asked for,
        /// and never count as a macro frame.
        /// </summary>
        public bool Aligning { get; private set; }
        private float _alignDelta;

        /// <summary>
        /// Begin pre-roll. Until a delta is set, frames keep the normal timing
        /// but are not counted as macro frames.
        /// </summary>
        public void BeginAlignment()
        {
            if (!Enabled) return;
            Aligning = true;
            _alignDelta = 0f;
            Apply();
        }

        /// <summary>
        /// Run the following frames with exactly <paramref name="delta"/>
        /// simulated seconds each (timeScale 1). Takes effect from the next
        /// engine frame, because Unity chooses a frame's delta before Update.
        /// </summary>
        public void SetAlignmentDelta(float delta)
        {
            if (!Enabled || !Aligning) return;
            _alignDelta = float.IsFinite(delta) && delta > 0f ? delta : 0f;
            Apply();
        }

        /// <summary>Return to normal timing; the next simulated frame is a macro frame again.</summary>
        public void EndAlignment()
        {
            if (!Aligning) return;
            Aligning = false;
            _alignDelta = 0f;
            Apply();
        }

        /// <summary>
        /// A step cannot be serviced within the frame that requests it. By the
        /// time a hotkey is read in <c>Update</c>, that frame's
        /// <c>FixedUpdate</c> has already run and the game's own <c>Update</c>
        /// may have too — so unpausing mid-frame would simulate a torn, partial
        /// frame. The request is therefore held, unpaused at the end of the
        /// requesting frame, and re-paused at the end of the following one,
        /// which runs whole.
        /// </summary>
        private enum StepState { None, Requested, Running }

        private StepState _step = StepState.None;
        private int _stepsRemaining;
        private float _savedFixedDelta;
        private int _savedVSync;
        private int _savedTargetFps;
        private float _savedTimeScale;
        private float _savedMaxDelta;
        private float _savedCaptureDelta;

        // What Apply() last put into force. Enforce() compares the engine
        // against these every frame, because a single write at pause time is
        // only as good as the engine's willingness to leave it alone - and a
        // pause that quietly stops holding is worse than no pause at all.
        private float _wantTimeScale = 1f;
        private float _wantCapture;
        private float _wantFixedDelta;
        private bool _reportedDrift;
        private bool _reportedPause;
        private int _framesSinceApply;
        private bool _simulatedThisUpdate;

        /// <summary>Where diagnostics go; set by the mod at startup.</summary>
        public System.Action<string> Log;

        public void Enable()
        {
            if (Enabled) return;

            _savedFixedDelta = Time.fixedDeltaTime;
            _savedVSync = QualitySettings.vSyncCount;
            _savedTargetFps = Application.targetFrameRate;
            _savedMaxDelta = Time.maximumDeltaTime;
            _savedCaptureDelta = Time.captureDeltaTime;

            // Saved rather than assumed to be 1. The gate disengages on
            // Paused.IsPaused, and the game's own pause menu pauses by setting
            // timeScale to 0 (Paused::PauseGame) - so restoring a hardcoded 1 on
            // the way out would un-pause the pause menu the player just opened.
            _savedTimeScale = Time.timeScale;

            // Adopt the game's own physics rate so arming the mod does not, by
            // itself, alter how the AI behaves relative to vanilla.
            // A rate the user chose survives re-engaging, though: control is lost
            // and regained at every cutscene, and having the panel silently snap
            // back to the stock rate each time would be indistinguishable from
            // the setting not working.
            if (!PhysicsRateOverridden && _savedFixedDelta > 0f) PhysicsRate = 1f / _savedFixedDelta;

            // vSync would clamp the frame rate independently of targetFrameRate,
            // reintroducing a dependency on the monitor's refresh rate — and
            // since frame rate is now literally the speed control, that would
            // mean the monitor sets the game speed. It has to go.
            QualitySettings.vSyncCount = 0;

            Enabled = true;
            FrameCount = 0;
            HasPendingChanges = false;   // Apply puts everything into force at once
            Apply();
        }

        public void Disable(bool preserveGameTimeScale = false)
        {
            if (!Enabled) return;

            Time.captureDeltaTime = _savedCaptureDelta;
            // A menu/cutscene may have taken ownership since Enable().
            if (!preserveGameTimeScale && Time.timeScale == _wantTimeScale)
                Time.timeScale = _savedTimeScale;
            Time.fixedDeltaTime = _savedFixedDelta;
            Time.maximumDeltaTime = _savedMaxDelta;
            QualitySettings.vSyncCount = _savedVSync;
            Application.targetFrameRate = _savedTargetFps;

            Enabled = false;
            Paused = false;
            GamePaused = false;
            Aligning = false;
            _alignDelta = 0f;
            SimulationRatesLocked = false;
            _step = StepState.None;
            _stepsRemaining = 0;
            _reportedDrift = false;
            _reportedPause = false;
            TimingMismatch = false;
            DeltaExact = true;
        }

        /// <summary>
        /// Restart the frame axis at zero. Called when recording begins: the
        /// macro is indexed by frame number, so the counter on screen is only
        /// meaningful if it agrees with the index being written.
        /// </summary>
        public void ResetFrameCount(int to = 0) => FrameCount = to;

        public void SetTickRate(float ticksPerSecond)
        {
            if (SimulationRatesLocked || !float.IsFinite(ticksPerSecond)) return;
            TickRate = Mathf.Clamp(ticksPerSecond, 1f, 1000f);
            Apply();
        }

        public void SetPhysicsRate(float ticksPerSecond)
        {
            if (SimulationRatesLocked || !float.IsFinite(ticksPerSecond)) return;
            PhysicsRate = Mathf.Clamp(ticksPerSecond, 1f, 1000f);
            PhysicsRateOverridden = true;
            Apply();
        }

        public void SetSpeed(float speed)
        {
            if (SimulationRatesLocked || !float.IsFinite(speed)) return;
            Speed = Mathf.Clamp(speed, 0.01f, 100f);
            Uncapped = false;
            Apply();
        }

        public void SetUncapped(bool uncapped)
        {
            if (SimulationRatesLocked) return;
            Uncapped = uncapped;
            Apply();
        }

        public void ToggleUncapped() => SetUncapped(!Uncapped);

        public void SetPaused(bool paused)
        {
            if (!Enabled) return;
            Paused = paused;
            _step = StepState.None;
            _stepsRemaining = 0;
            _reportedPause = false;
            Apply();
        }

        public void TogglePause() => SetPaused(!Paused);

        /// <summary>
        /// Suspend or resume around the game's pause menu. Unlike
        /// <see cref="SetPaused"/>, this preserves the TAS pause and step state.
        /// </summary>
        public void SetGamePaused(bool paused)
        {
            if (!Enabled || GamePaused == paused) return;
            GamePaused = paused;
            _reportedPause = false;
            Apply();
        }

        /// <summary>
        /// Queue exactly one whole frame while paused. It runs on the frame
        /// *after* this call — see <see cref="StepState"/>.
        /// </summary>
        public void StepFrame() => StepFrames(1);

        /// <summary>Queue <paramref name="count"/> whole frames while paused.</summary>
        public void StepFrames(int count)
        {
            if (!Enabled || !Paused || count <= 0) return;

            _stepsRemaining = (int)System.Math.Min(int.MaxValue, (long)_stepsRemaining + count);
            if (_step == StepState.None) _step = StepState.Requested;
        }

        /// <summary>Frames still queued to step, for display.</summary>
        public int StepsRemaining => _stepsRemaining;

        public void ResetPhysicsRate()
        {
            if (SimulationRatesLocked) return;
            PhysicsRateOverridden = false;
            var delta = Enabled ? _savedFixedDelta : Time.fixedDeltaTime;
            if (float.IsFinite(delta) && delta > 0f) PhysicsRate = 1f / delta;
            Apply();
        }

        /// <summary>
        /// Call at the start of the frame, before game logic. Returns whether
        /// the simulation advances this frame — the caller uses it to decide
        /// whether to advance virtual input.
        /// </summary>
        public bool OnUpdate()
        {
            if (!Enabled) return false;

            _simulatedThisUpdate = false;
            Enforce();
            ObservedFrameDelta = Time.deltaTime;

            // Unity selected deltaTime before this Update. On the frame where
            // the menu opens it can therefore still contain the prior running
            // delta even though the game has already set timeScale to zero.
            // The live pause flag is authoritative for TAS advancement.
            if (GamePaused) return false;

            // Pre-roll frames move the engine clock but are never macro frames,
            // whatever delta they ran with.
            if (Aligning) return false;

            // One-shot readout on the first frame of a pause. A pause that does
            // not hold looks exactly like frozen input from the outside, so the
            // engine's actual numbers are worth a line in the log.
            if (Paused && _step == StepState.None && !_reportedPause)
            {
                _reportedPause = true;
                Log?.Invoke(
                    $"paused - timeScale={Time.timeScale:0.###} deltaTime={Time.deltaTime:0.#####} " +
                    $"captureDeltaTime={Time.captureDeltaTime:0.#####} fixedDeltaTime={Time.fixedDeltaTime:0.#####} " +
                    $"time={Time.time:0.###}");
            }

            // Unity has already selected this frame's delta before Update.
            // A pause/resume hotkey changes the next frame, not this one.
            var simulating = Time.deltaTime > 0f;
            _simulatedThisUpdate = simulating;
            // Ignore the transition frame: Unity selected its delta before
            // a hotkey or panel applied the new timing settings.
            if (++_framesSinceApply >= 3 && simulating)
            {
                var expected = 1f / TickRate;
                var mismatch = Mathf.Abs(ObservedFrameDelta - expected) > Mathf.Max(1e-6f, expected * 0.001f);
                if (mismatch && !TimingMismatch)
                    Log?.Invoke($"TAS clock mismatch: Update delta={ObservedFrameDelta:R}, expected={expected:R}, " +
                                $"timeScale={Time.timeScale:R}, captureDelta={Time.captureDeltaTime:R}. Replay timing is not verified.");
                TimingMismatch = mismatch;

                // Close is not the same: a delta one bit off at one speed and
                // not at another is a replay that drifts by construction.
                var exact = Bits.Same(ObservedFrameDelta, expected);
                if (!exact && DeltaExact && !mismatch)
                    Log?.Invoke($"TAS clock is close but not bit-exact: Update delta={ObservedFrameDelta:R}, " +
                                $"expected={expected:R}, timeScale={Time.timeScale:R}, captureDelta={Time.captureDeltaTime:R}.");
                DeltaExact = exact;
            }
            if (simulating) FrameCount++;
            return simulating;
        }

        /// <summary>Call after game logic, to drive the step state machine.</summary>
        public void OnLateUpdate()
        {
            if (!Enabled) return;

            // A queued TAS step belongs to TAS time. Opening the game's menu
            // must not arm or consume it while the game owns the clock.
            if (GamePaused) return;

            switch (_step)
            {
                case StepState.Requested:
                    // Let the *next* frame run whole. Apply() rather than a bare
                    // timeScale write: a stepped frame needs captureDeltaTime
                    // restored as well, and a second copy of that policy here is
                    // how the two drift apart.
                    _step = StepState.Running;
                    Apply();
                    break;

                case StepState.Running:
                    // A game-pause resume can restore the running clock after
                    // Unity already selected a zero delta for this frame. Do
                    // not spend the queued step until a whole frame ran.
                    if (!_simulatedThisUpdate) break;

                    // That frame is now complete. Keep going if more were queued,
                    // since each queued frame still runs whole.
                    if (_stepsRemaining > 0) _stepsRemaining--;
                    if (_stepsRemaining > 0) break;

                    _step = StepState.None;
                    Apply();
                    break;
            }
        }

        private void Apply()
        {
            // A setting changed while standing down is remembered and takes
            // effect on the next Enable — the engine's own timing must be left
            // alone while the mod is not engaged.
            if (!Enabled)
            {
                HasPendingChanges = true;
                return;
            }

            var frozen = GamePaused || (Paused && _step != StepState.Running);
            var aligning = !frozen && Aligning && _alignDelta > 0f;
            _framesSinceApply = 0;
            TimingMismatch = false;

            // Capping rendering alone leaves timeScale at 1, which does not
            // communicate slow motion to native engine systems such as NavMesh.
            // Scale the engine clock too, compensating captureDeltaTime so the
            // scaled Update delta stays 1/TickRate rather than slowing twice.
            // Unity 2022.3 documents captureDeltaTime as scaled by timeScale.
            // A requested step runs at normal pacing; idle pause frames keep
            // both clocks at zero (including the capture override). Alignment
            // pre-roll runs unscaled with exactly the delta it asked for.
            _wantTimeScale = frozen ? 0f : (aligning || _step == StepState.Running ? 1f : RunningTimeScale);
            _wantCapture = frozen ? 0f : aligning ? _alignDelta : (1f / TickRate) / _wantTimeScale;
            Time.timeScale = _wantTimeScale;
            Time.captureDeltaTime = _wantCapture;

            // Enemy AI tick. Held in a fixed ratio against captureDeltaTime.
            _wantFixedDelta = 1f / PhysicsRate;
            Time.fixedDeltaTime = _wantFixedDelta;

            // Guard against the spiral-of-death clamp silently dropping fixed
            // ticks on a slow frame, which would break the ratio.
            Time.maximumDeltaTime = Mathf.Max(Time.fixedDeltaTime * 16f, 4f / TickRate);

            Application.targetFrameRate = frozen ? PausedFpsCap :
                (aligning || _step == StepState.Running ? Mathf.Max(1, Mathf.RoundToInt(TickRate)) : EffectiveFpsCap);
        }

        /// <summary>
        /// Re-assert the time state if anything moved it, once per frame.
        ///
        /// <see cref="Apply"/> is setter-driven, so without this the entire
        /// timing layer rests on a single write that happened at pause time.
        /// Disassembly says the game's own writers are all event-gated
        /// (<c>Paused::PauseGame</c>, the Esc edge in <c>Paused::Update</c>, and
        /// a <c>timeScale = 15</c> debug hotkey behind <c>GetKeyDown('b')</c>
        /// during the bed intro), so nothing should be fighting the mod - but
        /// checking costs three float compares and being wrong costs a pause
        /// that does not pause.
        /// </summary>
        public void Enforce()
        {
            if (!Enabled) return;

            if (Time.timeScale == _wantTimeScale &&
                Time.captureDeltaTime == _wantCapture &&
                Time.fixedDeltaTime == _wantFixedDelta) return;

            if (!_reportedDrift)
            {
                _reportedDrift = true;
                Log?.Invoke(
                    "time state was overwritten from outside the mod - re-asserting. " +
                    $"timeScale {Time.timeScale:0.###}->{_wantTimeScale:0.###}, " +
                    $"capture {Time.captureDeltaTime:0.#####}->{_wantCapture:0.#####}, " +
                    $"fixed {Time.fixedDeltaTime:0.#####}->{_wantFixedDelta:0.#####}");
            }

            Apply();
        }

        /// <summary>
        /// Enemy ticks per player frame. Must stay constant between recording
        /// and playback; a non-integer value means the interleaving pattern
        /// repeats on a cycle rather than every frame, which is still
        /// deterministic but only if the run starts from the same phase.
        /// </summary>
        public float TicksPerFrame => PhysicsRate / TickRate;

        public bool RatioIsIntegral => Mathf.Abs(TicksPerFrame - Mathf.Round(TicksPerFrame)) < 1e-4f;
    }
}
