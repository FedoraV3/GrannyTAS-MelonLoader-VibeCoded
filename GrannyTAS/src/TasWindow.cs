using System.IO;
using System.Numerics;
using ImGuiNET;

namespace GrannyTAS
{
    /// <summary>
    /// The Dear ImGui control panel. The host feeds hardware mouse and keyboard
    /// input directly; global TAS hotkeys pause while a text field is active.
    /// </summary>
    public static class TasWindow
    {
        private static readonly Vector4 Dim = new Vector4(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Vector4 Good = new Vector4(0.40f, 0.90f, 0.45f, 1f);
        private static readonly Vector4 Warn = new Vector4(1.00f, 0.80f, 0.20f, 1f);
        private static readonly Vector4 Hot = new Vector4(1.00f, 0.40f, 0.40f, 1f);
        private static string _replayName = "";
        private static string _saveStatus = "";
        private static bool _wasPending;

        public static void Draw(GrannyTasMod mod)
        {
            var time = mod.Time;
            var macro = mod.Macro;

            ImGui.SetNextWindowSize(new Vector2(360f, 520f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(24f, 24f), ImGuiCond.FirstUseEver);

            if (!ImGui.Begin("GrannyTAS"))
            {
                ImGui.End();
                return;
            }

            // ImGui.End() must run even if a section below throws, or the
            // window stack Begin just pushed stays open — that is the real
            // balance guarantee; ImGuiHost.Discard is only the backstop for
            // whatever gets past this.
            try
            {
                DrawStatus(time);
                ImGui.Separator();
                DrawTransport(time);
                ImGui.Separator();
                DrawRates(time);
                ImGui.Separator();
                DrawMacro(macro);
                ImGui.Separator();
                DrawSavedMacros(macro);
                ImGui.Separator();
                DrawBuffer();
                ImGui.Separator();
                DrawGate();
                ImGui.Separator();
                DrawKeybinds();
                ImGui.Separator();
                DrawSettings(mod);
            }
            finally
            {
                ImGui.End();
            }
        }

        private static void DrawStatus(TimeController time)
        {
            if (!time.Enabled)
            {
                // Not an error state and not something to fix — the mod is
                // simply waiting for the player to be in control.
                ImGui.TextColored(Warn, "standing by");
                ImGui.TextColored(Dim, "  " + PlayerGate.Reason);
                return;
            }

            ImGui.Text($"frame  {time.FrameCount}");
            ImGui.SameLine();
            if (time.Paused) ImGui.TextColored(Hot, "  PAUSED");
            else ImGui.TextColored(Good, "  RUNNING");
            if (time.TimingMismatch)
                ImGui.TextColored(Hot, "clock mismatch detected; see the mod log");
            else if (!time.DeltaExact)
                ImGui.TextColored(Warn, "frame delta is close but not bit-exact; see the mod log");

            // Enemy ticks per player frame. The player advances in Update and the
            // AI in FixedUpdate, so this ratio is part of the simulation and must
            // match between recording and playback.
            var ratio = time.TicksPerFrame;
            if (time.RatioIsIntegral) ImGui.Text($"ratio  {ratio:0.###} AI ticks/frame");
            else ImGui.TextColored(Warn, $"ratio  {ratio:0.###} AI ticks/frame (non-integral)");
        }

        private static void DrawTransport(TimeController time)
        {
            if (!time.Enabled)
            {
                ImGui.TextColored(Dim, "transport unavailable until you have control");
                return;
            }

            if (ImGui.Button(
                    (time.Paused ? "Resume" : "Pause") + Keybinds.Suffix(TasAction.Pause),
                    new Vector2(160f, 0f)))
                GrannyTasMod.Instance.TogglePause();

            ImGui.BeginDisabled(!time.Paused);
            if (ImGui.Button("Step frame" + Keybinds.Suffix(TasAction.Step), new Vector2(160f, 0f)))
                time.StepFrame();
            ImGui.SameLine();
            if (ImGui.Button("Step x10" + Keybinds.Suffix(TasAction.Step10), new Vector2(160f, 0f)))
                GrannyTasMod.Instance.RequestSteps(10);
            ImGui.EndDisabled();
        }

        /// <summary>
        /// The rates stay editable while the mod is standing down — unlike the
        /// transport above, which has no simulation to act on.
        ///
        /// This is deliberate: the game's own pause menu shuts the player gate,
        /// and that is exactly when a TAS user wants to dial in a rate, with
        /// nothing moving and no run in progress. The controller holds whatever
        /// is set here and puts the whole set into force the moment control
        /// comes back, so the only thing the panel owes the user is to say so
        /// rather than to appear broken.
        /// </summary>
        private static void DrawRates(TimeController time)
        {
            if (!time.Enabled)
            {
                // Two different states, and conflating them would make the
                // stronger claim the weaker one: something edited here is
                // *waiting*, while an untouched panel is merely showing what
                // will be applied as it stands.
                ImGui.TextColored(time.HasPendingChanges ? Warn : Dim,
                    time.HasPendingChanges
                        ? "held — applies when you regain control"
                        : "applies when you regain control");
                ImGui.Spacing();
            }

            ImGui.TextColored(Dim, "simulation — changing these changes the run");
            ImGui.BeginDisabled(time.SimulationRatesLocked);

            var tick = time.TickRate;
            if (ImGui.SliderFloat("sim rate", ref tick, 10f, 240f, "%.0f /s")) time.SetTickRate(tick);

            var phys = time.PhysicsRate;
            if (ImGui.SliderFloat("physics", ref phys, 10f, 240f, "%.0f Hz")) time.SetPhysicsRate(phys);
            ImGui.EndDisabled();
            if (time.SimulationRatesLocked) ImGui.TextColored(Dim, "rates locked during a macro");

            ImGui.Spacing();
            ImGui.TextColored(Dim, "pacing — choose before recording");
            ImGui.BeginDisabled(time.SimulationRatesLocked);

            var speed = time.Speed;
            if (ImGui.SliderFloat("speed", ref speed, 0.01f, 8f, "%.2fx")) time.SetSpeed(speed);

            ImGui.Text($"frame cap  {(time.Uncapped ? "uncapped" : time.EffectiveFpsCap + " fps")}");
            ImGui.SameLine();
            var uncapped = time.Uncapped;
            if (ImGui.Checkbox("turbo" + Keybinds.Suffix(TasAction.Uncapped), ref uncapped))
                time.SetUncapped(uncapped);
            ImGui.EndDisabled();
            if (time.SimulationRatesLocked) ImGui.TextColored(Dim, "speed and turbo locked during a macro");
        }

        private static void DrawMacro(MacroEngine macro)
        {
            if (macro.HasUnsavedRecording && !_wasPending) _saveStatus = "";
            _wasPending = macro.HasUnsavedRecording;

            switch (macro.Mode)
            {
                case MacroMode.Recording:
                    ImGui.TextColored(Hot, $"RECORDING — {macro.FrameCount} frames");
                    break;
                case MacroMode.Playing:
                    ImGui.TextColored(Good, $"PLAYING — {macro.Playhead}/{macro.FrameCount}");
                    ImGui.ProgressBar(macro.FrameCount > 0 ? (float)macro.Playhead / macro.FrameCount : 0f,
                        new Vector2(-1f, 0f));
                    break;
                case MacroMode.Preparing:
                    ImGui.TextColored(Warn, "PREPARING PLAYBACK");
                    ImGui.TextWrapped(macro.PendingStatus);
                    break;
                case MacroMode.Aligning:
                    ImGui.TextColored(Warn, "ALIGNING — moving the physics step onto the recording's");
                    break;
                default:
                    ImGui.Text(macro.FrameCount > 0
                        ? $"macro  {macro.FrameCount} frames ({macro.Macro.DurationSeconds:0.##}s at 1x)"
                        : "macro  none");
                    break;
            }

            // What F12 will actually replay — not necessarily the last thing
            // recorded, once a saved macro has been loaded over it.
            ImGui.TextColored(Dim, "loaded  " +
                (string.IsNullOrEmpty(macro.LoadedPath) ? "none" : Path.GetFileName(macro.LoadedPath)));

            if (macro.HasUnsavedRecording)
            {
                ImGui.Spacing();
                ImGui.TextColored(Warn, "UNSAVED RECORDING");
                ImGui.SetNextItemWidth(-1f);
                var submit = ImGui.InputText("##replayName", ref _replayName, 128,
                    ImGuiInputTextFlags.EnterReturnsTrue);

                var validName = !string.IsNullOrWhiteSpace(_replayName);
                ImGui.BeginDisabled(!validName);
                if (ImGui.Button("Save replay", new Vector2(160f, 0f)) || (submit && validName))
                {
                    if (macro.TrySaveRecording(_replayName, out _saveStatus)) _replayName = "";
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Delete replay", new Vector2(160f, 0f)))
                {
                    macro.DiscardRecording();
                    _replayName = "";
                    _saveStatus = "Recording deleted.";
                }

                if (!string.IsNullOrEmpty(_saveStatus)) ImGui.TextWrapped(_saveStatus);
                else ImGui.TextColored(Dim, "enter a name, then save or delete this take");
                ImGui.Spacing();
            }

            // Both refuse while the player is not in control; disabling them
            // says so before the click rather than in the log after it.
            var ready = GrannyTasMod.Instance.Time.Enabled || macro.Mode == MacroMode.Preparing;
            ImGui.BeginDisabled(!ready && macro.Mode == MacroMode.Idle);

            ImGui.BeginDisabled(macro.Mode == MacroMode.Playing || macro.Mode == MacroMode.Preparing || macro.HasUnsavedRecording);
            if (ImGui.Button(
                    (macro.Mode == MacroMode.Recording ? "Stop" : "Record") + Keybinds.Suffix(TasAction.Record),
                    new Vector2(160f, 0f)))
                GrannyTasMod.Instance.ToggleRecord();
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(macro.Mode == MacroMode.Recording);
            if (ImGui.Button(
                    (macro.Mode == MacroMode.Playing || macro.Mode == MacroMode.Preparing ? "Cancel" : "Play") + Keybinds.Suffix(TasAction.Play),
                    new Vector2(160f, 0f)))
                GrannyTasMod.Instance.TogglePlay();
            ImGui.EndDisabled();

            ImGui.EndDisabled();

            ImGui.TextColored(Dim, "records at any speed; always plays back at 1x");
            ImGui.TextWrapped("Replay restores positions and level setup; doors, AI timers, and other world state may differ.");

            DrawSync(macro);
        }

        /// <summary>
        /// Whether the replay is (or was) the recorded run, bit for bit — and if
        /// not, the first frame and system where it stopped being. The full
        /// per-area table is in the report file named underneath.
        /// </summary>
        private static void DrawSync(MacroEngine macro)
        {
            var live = macro.LiveReport;
            var report = live ?? macro.LastReport;
            if (report == null) return;

            ImGui.Spacing();
            ImGui.TextColored(Dim, live != null ? "sync check (live)" : "sync check (last replay)");
            if (!report.TracesPresent)
            {
                ImGui.TextColored(Dim, "  macro predates sync traces; re-record for a full check");
                return;
            }

            if (report.Exact)
                ImGui.TextColored(Good, $"  bit-identical so far ({report.FramesReplayed} frames)");
            else if (report.FirstDivergence(out var frame, out var area))
            {
                ImGui.TextColored(Hot, $"  diverged at frame {frame}: {SyncReport.NameOf(area)}");
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(Dim, "  " + report[area].WorstNote);
                ImGui.PopTextWrapPos();
            }
            else ImGui.TextColored(Hot, $"  {report.MissingRigidbodies} recorded rigidbody frame(s) missing");

            var pins = report.RayPins + report.FlagPins;
            if (pins > 0 || report.PositionCorrections > 0 || report.RigidbodyCorrections > 0)
                ImGui.TextColored(Dim, $"  steered: {report.PositionCorrections} position, {report.RigidbodyCorrections} rigidbody, {pins} ray");

            if (live == null && !string.IsNullOrEmpty(macro.LastReportPath))
                ImGui.TextColored(Dim, "  report: " + Path.GetFileName(macro.LastReportPath));
        }

        /// <summary>
        /// Browse named recordings. Refresh also finds files copied in by hand.
        /// </summary>
        private static void DrawSavedMacros(MacroEngine macro)
        {
            if (!ImGui.CollapsingHeader("Saved macros")) return;

            if (ImGui.SmallButton("Refresh")) macro.RefreshSavedList();

            if (macro.SavedMacros.Count == 0)
            {
                ImGui.TextColored(Dim, "  none yet — saved recordings appear here");
                return;
            }

            // Loading replaces the in-memory macro outright, so it is guarded
            // exactly like the transport buttons above — disabled rather than
            // left to fail after the click while a recording or playback owns it.
            ImGui.BeginDisabled(macro.Mode != MacroMode.Idle || macro.HasUnsavedRecording);
            var index = 0;
            foreach (var info in macro.SavedMacros)
            {
                ImGui.Text($"{info.FileName}  ({info.FrameCount}f, {info.DurationSeconds:0.##}s)");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Load##saved{index}")) macro.Load(info.Path);
                index++;
            }
            ImGui.EndDisabled();
        }

        private static void DrawBuffer()
        {
            ImGui.Text("input buffer");
            ImGui.SameLine();
            if (ImGui.SmallButton("clear" + Keybinds.Suffix(TasAction.ClearBuffer))) VirtualInput.ClearBuffer();
            ImGui.TextColored(Dim, "tap keys to latch; look freely while paused");
            ImGui.TextColored(Dim, "camera pose is recorded on the next frame");

            var pending = VirtualInput.Pending;
            if (pending.Keys.Count == 0)
            {
                ImGui.TextColored(Dim, "  empty — latched keys appear here while paused");
                return;
            }

            var line = "";
            foreach (var k in InputFrame.Sampled)
                if (pending.Has(k)) line += "  " + k;

            ImGui.TextColored(Good, line);
        }

        /// <summary>
        /// Every condition PlayerGate tests, live.
        ///
        /// Worth the space because two of these flags could not be pinned down
        /// from the binary — nothing writes isAllowedToMove or CamK on the day-1
        /// path, so their values during the wake-up animation come from
        /// serialized scene data. Watching them here answers at runtime what
        /// static analysis could not, and turns "the buttons are greyed out"
        /// into a named cause.
        /// </summary>
        private static void DrawGate()
        {
            if (!ImGui.CollapsingHeader("Player gate")) return;

            var f = PlayerGate.Current;

            ImGui.TextColored(Dim, PlayerGate.Reason);

            Flag("player found", f.HasPlayer);
            if (!f.HasPlayer) return;

            Flag("object active", f.ObjectActive);
            Flag("controller enabled", f.ComponentEnabled);
            Flag("not paused", f.NotPaused);
            Flag("intro finished", f.IntroFinished);
            Flag("not caught", f.NotJumpscared);
            Flag("isAllowedToMove", f.IsAllowedToMove);
            Flag("AbleToMove", f.AbleToMove);
            Flag("CamK", f.CamK);
            Flag("camera live", f.CameraLive);

            ImGui.TextColored(Dim, $"settle  {f.Streak}/12 frames");
        }

        private static void Flag(string label, bool value)
        {
            ImGui.TextColored(value ? Good : Hot, value ? "  yes" : "  NO ");
            ImGui.SameLine();
            ImGui.Text(label);
        }

        private static void DrawKeybinds()
        {
            if (!ImGui.CollapsingHeader("Keybinds")) return;

            if (Keybinds.Rebinding.HasValue)
                ImGui.TextColored(Warn, "press a key — Esc cancels, Backspace unbinds");
            else
                ImGui.TextColored(Dim, "click a binding to change it");

            foreach (var b in Keybinds.All)
            {
                var capturing = Keybinds.Rebinding == b.Action;
                var label = capturing ? "..." : (b.Key == UnityEngine.KeyCode.None ? "unbound" : b.Key.ToString());

                // The ## suffix keeps each button's ImGui id unique without
                // putting the action name on the button face.
                if (ImGui.Button($"{label}##bind{b.Action}", new Vector2(110f, 0f)))
                {
                    if (capturing) Keybinds.CancelRebind();
                    else Keybinds.BeginRebind(b.Action);
                }

                ImGui.SameLine();
                if (b.Key == UnityEngine.KeyCode.None) ImGui.TextColored(Warn, b.Label);
                else ImGui.Text(b.Label);
            }

            ImGui.Spacing();
            ImGui.TextColored(Dim, "saved as you bind them; kept for next launch");
            if (ImGui.Button("Reset keybinds", new Vector2(160f, 0f))) Keybinds.ResetAll();
        }

        /// <summary>
        /// What the mod remembers between launches, and the two behaviours that
        /// are a matter of taste rather than correctness.
        /// </summary>
        private static void DrawSettings(GrannyTasMod mod)
        {
            if (!ImGui.CollapsingHeader("Settings")) return;

            var cfg = mod.Config;

            var overlay = cfg.ShowOverlay;
            if (ImGui.Checkbox("status overlay", ref overlay)) cfg.ShowOverlay = overlay;

            var atStart = cfg.PanelOpenAtStart;
            if (ImGui.Checkbox("open this panel at launch", ref atStart)) cfg.PanelOpenAtStart = atStart;

            var suppress = cfg.SuppressLookWithPanel;
            if (ImGui.Checkbox("panel swallows the mouse", ref suppress)) cfg.SuppressLookWithPanel = suppress;

            ImGui.TextColored(Dim, "  off: dragging this window also turns the camera");

            var pin = cfg.PinPlaybackPosition;
            if (ImGui.Checkbox("pin recorded position on playback", ref pin))
            {
                cfg.PinPlaybackPosition = pin;
                VirtualInput.PinPosition = pin;
            }
            ImGui.TextColored(Dim, "  off: replays drift, and interactions miss");

            var rays = cfg.PinInteractionRays;
            if (ImGui.Checkbox("pin pickup/door rays on playback", ref rays))
            {
                cfg.PinInteractionRays = rays;
                InteractionPin.Enabled = rays;
            }
            ImGui.TextColored(Dim, "  casts each ray from its recorded pose; the sync check counts every pin");

            var trace = cfg.PickupTrace;
            if (ImGui.Checkbox("log pickup gates", ref trace)) cfg.PickupTrace = trace;
            ImGui.TextColored(Dim, "  diagnostic: logs why an item pickup was refused");

            ImGui.Spacing();

            // The close decision is worth showing before it is made: it is the
            // one piece of behaviour here that depends on game state rather than
            // on a setting, so "why did my cursor stay free?" has an answer on
            // screen.
            ImGui.TextColored(Dim, CursorController.GameWantsFreeCursor
                ? "closing keeps the cursor free (menu or no gameplay)"
                : "closing re-locks the cursor to the game");

            ImGui.Spacing();
            ImGui.TextColored(Dim, "saved to");
            ImGui.TextColored(Dim, "  " + (cfg.FilePath ?? "MelonPreferences.cfg"));

            ImGui.Spacing();
            if (ImGui.Button("Reset everything", new Vector2(160f, 0f))) cfg.ResetAll(mod.Time);
            ImGui.SameLine();
            if (ImGui.Button("Close panel" + Keybinds.Suffix(TasAction.TogglePanel), new Vector2(160f, 0f)))
                mod.SetPanelVisible(false);
        }
    }
}
