using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace GrannyTAS
{
    /// <summary>
    /// Everything the mod remembers between launches, in one file of its own:
    /// <c>UserData/GrannyTAS.cfg</c>.
    ///
    /// It gets its own file rather than sharing MelonLoader's global
    /// <c>MelonPreferences.cfg</c> because these settings are hand-edited — a
    /// keybind list and a set of rates are exactly what a TAS user tweaks in a
    /// text editor between sessions — and because a file holding only this
    /// mod's settings can be deleted to reset the mod without disturbing every
    /// other mod's configuration.
    ///
    /// Writes are <b>coalesced</b>. Dragging the speed slider changes the value
    /// every frame, and committing each of those would put a synchronous disk
    /// write inside the render loop. So <see cref="Sync"/> marks the config
    /// dirty and <see cref="Tick"/> commits at most once every
    /// <see cref="FlushInterval"/> seconds, plus once on shutdown — the last
    /// change must never be the one that gets lost.
    /// </summary>
    public sealed class TasConfig
    {
        /// <summary>Seconds between coalesced writes. Short enough that a crash costs a rebind, not a session.</summary>
        private const float FlushInterval = 2f;

        private MelonPreferences_Category _cat;

        private MelonPreferences_Entry<bool> _showOverlay;
        private MelonPreferences_Entry<bool> _panelOpenAtStart;
        private MelonPreferences_Entry<bool> _suppressLookWithPanel;
        private MelonPreferences_Entry<bool> _pickupTrace;
        private MelonPreferences_Entry<bool> _pinPlaybackPosition;
        private MelonPreferences_Entry<float> _tickRate;
        private MelonPreferences_Entry<float> _physicsRate;
        private MelonPreferences_Entry<float> _speed;

        private bool _dirty;
        private float _nextFlush;

        /// <summary>The category the keybinds live in too, so it is all one file.</summary>
        public MelonPreferences_Category Category => _cat;

        /// <summary>Where the settings actually landed, for the panel to show.</summary>
        public string FilePath { get; private set; }

        public bool ShowOverlay
        {
            get => _showOverlay.Value;
            set { if (_showOverlay.Value != value) { _showOverlay.Value = value; MarkDirty(); } }
        }

        public bool PanelOpenAtStart
        {
            get => _panelOpenAtStart.Value;
            set { if (_panelOpenAtStart.Value != value) { _panelOpenAtStart.Value = value; MarkDirty(); } }
        }

        /// <summary>
        /// Correct the player's position to the recorded one on every replayed
        /// frame. See <see cref="VirtualInput.PinPosition"/> — without it a
        /// replay drifts by fractions of a millimetre and interaction rays
        /// stop agreeing with the run that was recorded.
        /// </summary>
        public bool PinPlaybackPosition
        {
            get => _pinPlaybackPosition.Value;
            set { if (_pinPlaybackPosition.Value != value) { _pinPlaybackPosition.Value = value; MarkDirty(); } }
        }

        /// <summary>
        /// Log the item-pickup gate chain every frame around an interact press.
        ///
        /// Diagnostic only, and off by default: it is how a pickup that replays
        /// differently from the way it was recorded names the gate that rejected
        /// it, rather than the cause being guessed at. See
        /// <see cref="PickupDiagnostics"/>.
        /// </summary>
        public bool PickupTrace
        {
            get => _pickupTrace.Value;
            set { if (_pickupTrace.Value != value) { _pickupTrace.Value = value; MarkDirty(); } }
        }

        /// <summary>Whether an open panel swallows mouse look and clicks — see <see cref="CursorController"/>.</summary>
        public bool SuppressLookWithPanel
        {
            get => _suppressLookWithPanel.Value;
            set { if (_suppressLookWithPanel.Value != value) { _suppressLookWithPanel.Value = value; MarkDirty(); } }
        }

        public float TickRate => _tickRate.Value;

        public float Speed => _speed.Value;

        /// <summary>
        /// Saved physics rate, or 0 for "adopt whatever the game ships with".
        ///
        /// Zero is the default and is not the same as 50: <c>TimeController.Enable</c>
        /// deliberately takes the game's own <c>fixedDeltaTime</c> so that merely
        /// engaging the mod does not change how the AI behaves. Only a rate the
        /// user actually chose should override that.
        /// </summary>
        public float PhysicsRateOverride => _physicsRate.Value;

        public void Initialize(MelonLogger.Instance log)
        {
            _cat = MelonPreferences.CreateCategory("GrannyTAS", "GrannyTAS");

            try
            {
                FilePath = Path.Combine(MelonEnvironment.UserDataDirectory, "GrannyTAS.cfg");
                _cat.SetFilePath(FilePath, false);
            }
            catch (Exception e)
            {
                // A dedicated file is a nicety; losing it must not cost the
                // settings themselves, which then fall back to MelonPreferences.cfg.
                FilePath = null;
                log.Warning("Could not use a dedicated config file, falling back to MelonPreferences.cfg: " + e.Message);
            }

            _showOverlay = _cat.CreateEntry("ShowOverlay", true,
                description: "Draw the fallback TAS status overlay.");
            _panelOpenAtStart = _cat.CreateEntry("PanelOpenAtStart", false,
                description: "Open the TAS panel as soon as the game starts.");
            _suppressLookWithPanel = _cat.CreateEntry("SuppressLookWithPanel", true,
                description: "While the panel is open, stop mouse movement and clicks from reaching the game.");
            _pickupTrace = _cat.CreateEntry("PickupTrace", false,
                description: "Log the item-pickup gate chain around each interact press. Diagnostic; noisy.");
            _pinPlaybackPosition = _cat.CreateEntry("PinPlaybackPosition", true,
                description: "On playback, put the player back on the recorded position each frame so interaction rays stay on target.");

            _tickRate = _cat.CreateEntry("TickRate", 60f,
                description: "Simulated frames per second. Part of the run: changing it changes the simulation.");
            _physicsRate = _cat.CreateEntry("PhysicsRate", 0f,
                description: "Physics/AI rate in Hz. 0 means adopt the rate the game itself runs at.");
            _speed = _cat.CreateEntry("Speed", 1f,
                description: "Wall-clock pacing only; does not affect the simulation.");

            Keybinds.Initialize(_cat);

            // The file is read only after every entry exists, so loaded values
            // land on entries that can hold them. A key read before its entry
            // was defined would be dropped again on the next save.
            try { _cat.LoadFromFile(false); }
            catch (Exception e) { log.Warning("Could not read the config file, using defaults: " + e.Message); }
            Keybinds.Reload();
        }

        /// <summary>
        /// Push the live settings into the config, coalescing writes.
        ///
        /// This runs whether or not the controller is engaged, because settings
        /// can be changed while the mod is standing down and the controller
        /// holds them either way. It also has to: <see cref="ApplyTo"/> runs
        /// immediately after <c>Enable</c>, so a value edited while standing by
        /// but never written here would be overwritten by the stale saved one at
        /// the very moment it was supposed to take effect.
        /// </summary>
        public void Sync(TimeController time)
        {
            if (!Approximately(_tickRate.Value, time.TickRate)) { _tickRate.Value = time.TickRate; MarkDirty(); }
            if (!Approximately(_speed.Value, time.Speed)) { _speed.Value = time.Speed; MarkDirty(); }

            // Only a rate the user chose is worth storing; see PhysicsRateOverride.
            if (time.PhysicsRateOverridden && !Approximately(_physicsRate.Value, time.PhysicsRate))
            {
                _physicsRate.Value = time.PhysicsRate;
                MarkDirty();
            }
        }

        /// <summary>Apply the saved rates to a controller that has just engaged.</summary>
        public void ApplyTo(TimeController time)
        {
            time.SetTickRate(_tickRate.Value);
            if (_physicsRate.Value > 0f) time.SetPhysicsRate(_physicsRate.Value);
            time.SetSpeed(_speed.Value);
        }

        public void MarkDirty()
        {
            _dirty = true;
            if (_nextFlush <= 0f) _nextFlush = UnityEngine.Time.realtimeSinceStartup + FlushInterval;
        }

        /// <summary>Call once per real frame. Writes only when something changed and the interval has elapsed.</summary>
        public void Tick()
        {
            if (!_dirty) return;
            if (UnityEngine.Time.realtimeSinceStartup < _nextFlush) return;
            Flush();
        }

        /// <summary>Write now, whether or not the interval has elapsed.</summary>
        public void Flush()
        {
            try
            {
                MelonPreferences.Save();
                _dirty = false;
                _nextFlush = 0f;
            }
            catch
            {
                _dirty = true;
                _nextFlush = UnityEngine.Time.realtimeSinceStartup + FlushInterval;
            }
        }

        public void ResetAll(TimeController time)
        {
            if (time.SimulationRatesLocked) return;
            Keybinds.ResetAll();

            _showOverlay.ResetToDefault();
            _panelOpenAtStart.ResetToDefault();
            _suppressLookWithPanel.ResetToDefault();
            _pickupTrace.ResetToDefault();
            _pinPlaybackPosition.ResetToDefault();
            _tickRate.ResetToDefault();
            _physicsRate.ResetToDefault();
            _speed.ResetToDefault();

            time.ResetPhysicsRate();
            ApplyTo(time);
            Flush();
        }

        private static bool Approximately(float a, float b) => Math.Abs(a - b) < 1e-4f;
    }
}
