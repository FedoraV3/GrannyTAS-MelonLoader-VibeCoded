using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(GrannyTAS.GrannyTasMod), "GrannyTAS", "0.4.0", "ir0n1c")]
// Values as MelonLoader itself resolves them at startup (see MelonLoader/Latest.log).
// Note app.info spells the product "Granny Legacy" without the colon; the log is
// what the compatibility check actually compares against.
[assembly: MelonGame("Omega Mega Gigal Intel", "Granny: Legacy")]

namespace GrannyTAS
{
    /// <summary>
    /// The mod has no arm/disarm switch. It engages itself the moment the player
    /// is actually in control of the character, and stands down when they are
    /// not — see <see cref="PlayerGate"/>.
    ///
    /// The reason is that an explicit arm step could always be taken at the
    /// wrong moment. Arming during the wake-up animation pinned timing over a
    /// sequence the player does not drive, so a macro recorded there began
    /// mid-cutscene and desynced immediately on replay. Tying engagement to the
    /// same condition that decides whether input means anything removes that
    /// class of mistake instead of documenting it.
    /// </summary>
    public sealed class GrannyTasMod : MelonMod
    {
        public static GrannyTasMod Instance { get; private set; }

        public TimeController Time { get; } = new TimeController();
        public MacroEngine Macro { get; private set; }

        /// <summary>Settings that survive a restart — keybinds, rates, panel preferences.</summary>
        public TasConfig Config { get; } = new TasConfig();

        private GUIStyle _style;

        private ImGuiHost _imgui;

        public override void OnInitializeMelon()
        {
            Instance = this;
            Macro = new MacroEngine(LoggerInstance, Time);
            Time.Log = msg => LoggerInstance.Msg(msg);

            Config.Initialize(LoggerInstance);
            Config.ApplyTo(Time);

            PickupDiagnostics.Log = LoggerInstance;
            EnemyEsp.Warn = msg => LoggerInstance.Warning(msg);
            VirtualInput.PinPosition = Config.PinPlaybackPosition;
            InteractionPin.Enabled = Config.PinInteractionRays;
            PickupDiagnostics.Enabled = Config.PickupTrace;
            SyncProbe.ReadRandomState = RandomProbe.TryRead;
            PhysicsFrameState.Warn = msg => LoggerInstance.Warning(msg);
            VirtualInput.TeleportFallbackEngaged = () => LoggerInstance.Warning(
                "Position corrections are not sticking via Physics.SyncTransforms; falling back to " +
                "disabling the CharacterController around each correction for the rest of this session.");
            if (PickupDiagnostics.Enabled)
                LoggerInstance.Msg("Pickup trace on — every interact press logs its gate chain.");

            _imgui = new ImGuiHost(LoggerInstance);
            if (Config.PanelOpenAtStart) SetPanelVisible(true);

            LoggerInstance.Msg("GrannyTAS loaded — engages automatically once you have control of the player.");
            LoggerInstance.Msg($"{Keybinds.Name(TasAction.TogglePanel)} opens the panel; every key is rebindable there.");
            LoggerInstance.Msg($"Config:  {Config.FilePath ?? "MelonPreferences.cfg"}");
            LoggerInstance.Msg($"Macros: {MacroEngine.MacroDirectory}");
        }

        /// <summary>Last chance to commit coalesced setting changes.</summary>
        public override void OnDeinitializeMelon()
        {
            OnApplicationQuit();
            VirtualInput.Active = false;
            SetPanelVisible(false);
            Time.Disable(preserveGameTimeScale: Il2Cpp.Paused.IsPaused);
            _imgui?.Shutdown();
            PlayerGate.Invalidate();
            Instance = null;
        }

        public override void OnApplicationQuit()
        {
            if (Macro?.Mode == MacroMode.Recording) Macro.StopRecording();
            if (Macro?.Mode == MacroMode.Playing || Macro?.Mode == MacroMode.Preparing || Macro?.Mode == MacroMode.Aligning)
                Macro.StopPlayback();
            Config.Sync(Time);
            Config.Flush();
        }

        public override void OnUpdate()
        {
            // Rebinding and the readiness probe read hardware, not the virtual
            // state they control — otherwise the pause key could never unpause
            // anything, and a rebind could never see a real press.
            VirtualInput.Bypass = true;
            try
            {
                // A text field taking focus cancels a pending capture rather than
                // leaving it armed while unpolled — the capture can't see keys
                // while ImGui owns them anyway, and leaving it armed would latch
                // Keybinds' internal "reserved" state for every key. See
                // Keybinds.SuspendRebind.
                if (!TextInputActive) Keybinds.PollRebind();
                else Keybinds.SuspendRebind();
                if (Macro.Mode == MacroMode.Preparing)
                {
                    Macro.UpdatePreparing(PlayerGate.IsReady);
                    if (Macro.Mode == MacroMode.Playing || Macro.Mode == MacroMode.Aligning)
                    {
                        VirtualInput.Active = true;
                    }
                }
                else UpdateEngagement();
            }
            finally { VirtualInput.Bypass = false; }

            // A snapshot load or a restarting replay keeps input frozen through
            // the reload, which also swallowed the game's own B-to-speed-up the
            // wake-up. That key alone goes through while the bed animation
            // plays; it runs before any frame is replayed, so it cannot touch
            // the run.
            VirtualInput.PassWakeUpKey = Macro.Mode == MacroMode.Preparing && PlayerGate.InWakeUp;

            // The panel owns the cursor and the mouse for as long as it is open,
            // whether or not the timing layer is engaged — it is reachable from
            // the main menu too.
            CursorController.Tick();
            VirtualInput.SuppressMouse = PanelVisible && Config.SuppressLookWithPanel;

            Config.Sync(Time);
            Config.Tick();
            PickupDiagnostics.Enabled = Config.PickupTrace;
            VirtualInput.PinPosition = Config.PinPlaybackPosition;
            InteractionPin.Enabled = Config.PinInteractionRays;

            // A restart request can make control disappear and disable timing.
            // Keep the request alive, but never advance input or the TAS frame
            // axis until a real load callback and the settled player gate agree.
            if (Macro.Mode == MacroMode.Preparing)
            {
                VirtualInput.Bypass = true;
                try
                {
                    var textInputActive = TextInputActive;
                    if (Keybinds.Down(TasAction.TogglePanel)) TogglePanel();
                    if (!textInputActive && Keybinds.Down(TasAction.Play)) TogglePlay();
                }
                finally { VirtualInput.Bypass = false; }
                VirtualInput.Freeze();
                return;
            }

            if (!Time.Enabled)
            {
                VirtualInput.Bypass = true;
                try { HandleIdleKeys(); }
                finally { VirtualInput.Bypass = false; }
                return;
            }

            // Hardware is polled every real frame, whether or not the simulation
            // advances — that is what lets the input buffer latch while paused.
            VirtualInput.PollHardware();

            // Transport actions must run before the input/capture decision.
            // Starting recording afterward left one unrecorded gameplay frame
            // between the snapshot and the first recorded input.
            VirtualInput.Bypass = true;
            try { HandleHotkeys(); }
            finally { VirtualInput.Bypass = false; }

            if (Macro.Mode == MacroMode.Preparing)
            {
                VirtualInput.Freeze();
                return;
            }

            // A snapshot load has replayed its last kept frame: recording
            // resumes here, paused, so the snapshot frame is the next one taken.
            if (Macro.TakeRewindHandoff())
            {
                Time.SetPaused(true);
                VirtualInput.ClearBuffer();
            }

            var simulating = Time.OnUpdate();

            // Pre-roll before a replay's first frame: the engine clock moves so
            // the fixed-step phase can be put where the recording had it, but no
            // input is delivered and nothing counts as a macro frame. On the
            // frame alignment completes this switches to Playing, and that same
            // frame issues macro frame 0 below.
            if (Macro.Mode == MacroMode.Aligning) Macro.UpdateAligning(simulating);
            if (Macro.Mode == MacroMode.Aligning)
            {
                VirtualInput.Freeze();
                return;
            }

            if (simulating)
            {
                // Playback supplies its own frame; otherwise input comes from the
                // latched buffer on a step, or live hardware when free-running.
                if (!Macro.TryDrivePlayback())
                {
                    if (Time.Paused)
                    {
                        VirtualInput.AccumulateBuffer();
                        VirtualInput.AdvanceFromBuffer();
                    }
                    else VirtualInput.AdvanceFromHardware();
                }

                // Capture after the advance, so there is a frame to capture.
                // Recording during playback is a no-op; the modes are exclusive.
                Macro.CaptureFrame();
            }
            else
            {
                if (Time.Paused && Macro.Mode != MacroMode.Playing) VirtualInput.AccumulateBuffer();

                VirtualInput.Freeze();
                if (Time.Paused && Macro.Mode != MacroMode.Playing) VirtualInput.PreviewPausedLook();
            }

        }

        public override void OnLateUpdate()
        {
            // The game's Paused.Update can open its menu after this mod's
            // OnUpdate has already sampled the flag. Catch that transition
            // before the TAS step state machine can re-apply a running clock.
            // Resume remains an OnUpdate operation so readiness is checked
            // before any macro frame can advance.
            if (Time.Enabled && PlayerGate.GamePaused) Time.SetGamePaused(true);
            Time.OnLateUpdate();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            PlayerGate.Invalidate();
            EnemyEsp.Invalidate();
            Macro?.NotifySceneLoaded(sceneName);
        }

        // ---- automatic engagement -----------------------------------------

        /// <summary>
        /// Bring the timing layer up when the player gains control and take it
        /// down when they lose it. The gate is polled every frame but only acts
        /// on a change.
        /// </summary>
        private void UpdateEngagement()
        {
            var gamePaused = PlayerGate.GamePaused;

            if (Time.Enabled)
            {
                Time.SetGamePaused(gamePaused);

                // The menu temporarily makes MobileFPS ignore all input, but it
                // is not a transport stop. Avoid probing while it is open so the
                // settled readiness streak and every macro/input state survive.
                // The first unpaused frame probes normally and still stops on a
                // real scene change or loss of player control before advancing.
                if (gamePaused) return;
            }

            var ready = PlayerGate.IsReady;

            if (ready && !Time.Enabled)
            {
                Time.Enable();

                // Config is hydrated once at initialization; Enable preserves
                // explicit physics overrides and adopts only the default rate.
                VirtualInput.SeedFromHardware();
                VirtualInput.ClearBuffer();
                VirtualInput.Active = true;

                LoggerInstance.Msg(
                    $"Engaged — {Time.TickRate} fps sim, {Time.PhysicsRate} Hz physics, " +
                    $"{Time.Speed:0.##}x speed ({Time.EffectiveFpsCap} fps cap).");
                LogPhysicsSettingsOnce();
            }
            else if (!ready && Time.Enabled)
            {
                // Window-jump, bed-hide, and similar scripted transitions take
                // the movement/look gates away briefly while gameplay continues.
                // An active macro must keep its clock and frame stream running
                // through that hand-off so recording and playback stay aligned.
                // PlayerGate permits only those transient gate failures; hard
                // loss (death, startup, teardown, probe errors) still stops.
                var macroActive = Macro.Mode == MacroMode.Recording || Macro.Mode == MacroMode.Playing;
                if (macroActive && PlayerGate.CanBridgeTransientControlLoss) return;

                // Caught with an armed snapshot: rewind instead of ending the take.
                // The rewind restarts the level itself, so nothing below applies.
                if (Macro.Mode == MacroMode.Recording && PlayerGate.PlayerCaught && Macro.TryAutoLoadSnapshot())
                {
                    VirtualInput.Active = false;
                    return;
                }

                if (Macro.Mode == MacroMode.Recording) Macro.StopRecording();
                if (Macro.Mode == MacroMode.Playing || Macro.Mode == MacroMode.Aligning)
                    Macro.StopPlayback("Playback stopped — lost player control.");

                Time.Disable(preserveGameTimeScale: Il2Cpp.Paused.IsPaused);
                VirtualInput.Active = false;
                LoggerInstance.Msg($"Disengaged ({PlayerGate.Reason}) — engine timing and input restored.");
            }
        }

        // ---- actions the ImGui panel shares with the hotkeys ---------------

        public void TogglePause()
        {
            Time.TogglePause();
            // Pausing mid-stride should keep the stride, rather than making you
            // re-latch everything you were already holding.
            if (Time.Paused) VirtualInput.SeedBufferFromHardware();
        }

        public void RequestSteps(int count) => Time.StepFrames(count);

        public void ToggleRecord()
        {
            if (Macro.Mode == MacroMode.Recording) Macro.StopRecording();
            else Macro.StartRecording();
        }

        public void TogglePlay()
        {
            if (Macro.Mode == MacroMode.Playing || Macro.Mode == MacroMode.Preparing || Macro.Mode == MacroMode.Aligning)
                Macro.StopPlayback();
            else Macro.StartPlayback();
        }

        private bool _loggedPhysicsSettings;

        /// <summary>
        /// The two engine physics settings replay determinism leans on, once per
        /// session. With autoSyncTransforms off (the game's setting), a collider
        /// moved by a transform — a drawer, a door, Granny — only becomes visible
        /// to raycasts at the next physics step, which is why the fixed-step
        /// phase decides what the pickup ray can hit.
        /// </summary>
        private void LogPhysicsSettingsOnce()
        {
            if (_loggedPhysicsSettings) return;
            _loggedPhysicsSettings = true;
            try
            {
                LoggerInstance.Msg($"Physics: autoSyncTransforms={Physics.autoSyncTransforms}, " +
                                   $"fixed step {UnityEngine.Time.fixedDeltaTime:R}s.");
            }
            catch { }
        }

        /// <summary>Whether the ImGui panel is currently up.</summary>
        public bool PanelVisible => _imgui != null && _imgui.Visible;

        private bool TextInputActive => _imgui != null && _imgui.WantsTextInput;

        /// <summary>
        /// Open or close the panel, handing the cursor over to
        /// <see cref="CursorController"/> in both directions.
        ///
        /// Note the close path does not restore a snapshot taken at open time:
        /// the game's cursor state can change while the panel is up (opening its
        /// own pause menu, most obviously), and putting back a stale "locked"
        /// would leave that menu unusable. See CursorController for the rule.
        /// </summary>
        public void SetPanelVisible(bool visible)
        {
            if (_imgui == null || visible == _imgui.Visible) return;

            if (visible)
            {
                CursorController.Acquire();
            }
            else
            {
                // A capture left armed would eat the first key pressed after the
                // panel closes, and there would be nothing on screen saying why.
                Keybinds.CancelRebind();
                CursorController.Release();
                VirtualInput.SuppressMouse = false;
            }

            _imgui.Visible = visible;
        }

        public void TogglePanel() => SetPanelVisible(!PanelVisible);

        /// <summary>
        /// The panel and the settings stay reachable while the TAS is standing
        /// down. Only the transport is missing here, because pausing or stepping
        /// a simulation the mod is not driving means nothing — whereas a rate is
        /// just a number, and the natural moment to set one is while the game's
        /// own pause menu is up and nothing is moving.
        /// </summary>
        private void HandleIdleKeys()
        {
            var textInputActive = TextInputActive;
            if (Keybinds.Down(TasAction.TogglePanel)) TogglePanel();

            if (textInputActive) return;

            HandleSettingsKeys();

            // After a death without an armed snapshot the recording has stopped
            // and control is gone — exactly when loading the snapshot is wanted.
            if (Keybinds.Down(TasAction.LoadSnapshot)) Macro.LoadSnapshot();
        }

        private void HandleHotkeys()
        {
            var textInputActive = TextInputActive;
            if (Keybinds.Down(TasAction.TogglePanel)) TogglePanel();

            if (textInputActive) return;

            if (Keybinds.Down(TasAction.Pause)) TogglePause();
            if (Keybinds.Down(TasAction.Step)) Time.StepFrame();
            if (Keybinds.Down(TasAction.Step10)) Time.StepFrames(10);

            HandleSettingsKeys();

            if (Keybinds.Down(TasAction.ClearBuffer)) VirtualInput.ClearBuffer();

            if (Keybinds.Down(TasAction.Record)) ToggleRecord();
            if (Keybinds.Down(TasAction.Play)) TogglePlay();

            if (Keybinds.Down(TasAction.SaveSnapshot)) Macro.SaveSnapshot();
            if (Keybinds.Down(TasAction.LoadSnapshot)) Macro.LoadSnapshot();
        }

        /// <summary>
        /// The rate and pacing keys, which work whether or not the mod is
        /// engaged. While it is standing down the setters only record the value;
        /// <see cref="TimeController.Enable"/> puts the whole set into force at
        /// once when control comes back.
        /// </summary>
        private void HandleSettingsKeys()
        {
            // Display only, so it belongs with the keys that work in any state.
            if (Keybinds.Down(TasAction.ToggleEsp)) Config.ShowEsp = !Config.ShowEsp;

            if (Keybinds.Down(TasAction.TickRateDown)) Time.SetTickRate(Time.TickRate - 10f);
            if (Keybinds.Down(TasAction.TickRateUp)) Time.SetTickRate(Time.TickRate + 10f);

            if (Keybinds.Down(TasAction.SpeedDown)) Time.SetSpeed(StepSpeed(Time.Speed, -1));
            if (Keybinds.Down(TasAction.SpeedUp)) Time.SetSpeed(StepSpeed(Time.Speed, +1));
            if (Keybinds.Down(TasAction.Uncapped)) Time.ToggleUncapped();
        }

        // Speed is perceptual, so the ladder is multiplicative rather than a
        // fixed increment — a flat +0.1 is a huge jump at 0.05 and invisible at 8.
        private static readonly float[] SpeedLadder =
            { 0.01f, 0.02f, 0.05f, 0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f, 8f, 16f };

        private static float StepSpeed(float current, int direction)
        {
            var i = 0;
            for (var n = 0; n < SpeedLadder.Length; n++)
                if (Mathf.Abs(SpeedLadder[n] - current) < Mathf.Abs(SpeedLadder[i] - current)) i = n;

            return SpeedLadder[Mathf.Clamp(i + direction, 0, SpeedLadder.Length - 1)];
        }

        private static GUIStyle MakeStyle()
        {
            var s = new GUIStyle
            {
                fontSize = 13,
                richText = true,
                alignment = TextAnchor.MiddleLeft,
            };
            s.normal.textColor = Color.white;
            return s;
        }

        public override void OnGUI()
        {
            // OnGUI fires several times per frame (Layout, then Repaint). ImGui
            // wants exactly one NewFrame/Render pair, and GL only draws on
            // Repaint, so everything happens there.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            // The ESP draws with the panel closed too, so an ImGui frame runs
            // whenever either wants one. The panel list reads the same data,
            // which is why gathering also happens with only the panel up.
            var panel = _imgui != null && _imgui.Visible;
            var esp = Config.ShowEsp;
            if (esp || panel) EnemyEsp.Gather();

            if (_imgui != null && (panel || esp) && !_imgui.Failed)
            {
                if (_imgui.Initialize())
                {
                    _imgui.NewFrame(UnityEngine.Time.unscaledDeltaTime);
                    if (_imgui.Ready)
                    {
                        // Background draw list only, no window: cannot unbalance the stack.
                        if (esp) EnemyEsp.Draw(Config);
                        if (panel)
                        {
                            try { TasWindow.Draw(this); }
                            catch (System.Exception e) { _imgui.Discard(); _imgui.ReportFailure(e.Message); }
                        }
                    }
                    _imgui.Render();
                    if (panel && !_imgui.Failed) return;
                }

                // Initialisation failed; fall through to the legacy overlay so
                // the user is not left without an interface.
                if (panel) SetPanelVisible(false);
            }

            if (_imgui?.Failed == true && PanelVisible) SetPanelVisible(false);

            if (esp && (_imgui == null || _imgui.Failed)) EnemyEsp.DrawLegacy(_style ??= MakeStyle());
            DrawLegacyOverlay();
        }

        /// <summary>
        /// The original IMGUI status readout. Kept as the fallback for when Dear
        /// ImGui cannot start — a missing cimgui.dll or a stripped shader should
        /// cost the panel, not the whole interface.
        /// </summary>
        private void DrawLegacyOverlay()
        {
            if (!Config.ShowOverlay) return;

            // Il2CppInterop does not surface GUIStyle's copy constructor, and
            // mutating GUI.skin.label would leak into the game's own UI — so
            // build a standalone style instead.
            var style = _style ??= MakeStyle();

            if (!Time.Enabled)
            {
                GUI.Box(new Rect(8f, 8f, 300f, 44f), "GrannyTAS");
                GUI.Label(new Rect(16f, 28f, 290f, 18f),
                    $"<color=#888888>standing by — {PlayerGate.Reason}</color>", style);
                return;
            }

            GUI.Box(new Rect(8f, 8f, 300f, 148f), "GrannyTAS");

            var speed = Time.Uncapped
                ? "<color=#ffcc00>UNCAPPED</color>"
                : $"{Time.Speed:0.##}x  ({Time.EffectiveFpsCap} fps)";
            var ratio = Time.TicksPerFrame.ToString("0.###") +
                        (Time.RatioIsIntegral ? "" : "  <color=#ffcc00>(non-integral)</color>");

            GUI.Label(new Rect(16f, 28f, 290f, 18f), $"frame   {Time.FrameCount}", style);
            GUI.Label(new Rect(16f, 44f, 290f, 18f), $"sim     {Time.TickRate:0.#} /s", style);
            GUI.Label(new Rect(16f, 60f, 290f, 18f), $"physics {Time.PhysicsRate:0.#} Hz", style);
            GUI.Label(new Rect(16f, 76f, 290f, 18f), $"speed   {speed}", style);
            GUI.Label(new Rect(16f, 92f, 290f, 18f), $"ratio   {ratio}", style);
            GUI.Label(new Rect(16f, 108f, 290f, 18f), $"macro   {MacroStatus()}", style);
            GUI.Label(new Rect(16f, 124f, 290f, 18f), $"buffer  {BufferSummary()}", style);

            if (Time.Paused)
                GUI.Label(new Rect(215f, 28f, 90f, 18f), "<color=#ff6666>PAUSED</color>", style);
        }

        private string MacroStatus()
        {
            switch (Macro.Mode)
            {
                case MacroMode.Recording:
                    return $"<color=#ff6666>REC</color> {Macro.FrameCount}";
                case MacroMode.Playing:
                    return Macro.IsRewinding
                        ? $"<color=#ffcc00>REWIND</color> {Macro.Playhead}/{Macro.FrameCount}"
                        : $"<color=#66ff66>PLAY</color> {Macro.Playhead}/{Macro.FrameCount}";
                case MacroMode.Preparing:
                    return "<color=#ffcc00>PREPARING</color>";
                case MacroMode.Aligning:
                    return "<color=#ffcc00>ALIGNING</color>";
                default:
                    return Macro.FrameCount > 0 ? $"idle ({Macro.FrameCount} frames)" : "idle";
            }
        }

        private static string BufferSummary()
        {
            var pending = VirtualInput.Pending;
            if (pending.Keys.Count == 0) return "<color=#888888>empty</color>";

            var s = "";
            foreach (var k in InputFrame.Sampled)
                if (pending.Has(k)) s += (s.Length > 0 ? " " : "") + k;

            return s;
        }
    }
}
