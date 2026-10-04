using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>Everything the user can bind a key to.</summary>
    public enum TasAction
    {
        TogglePanel,
        ToggleEsp,
        Pause,
        Step,
        Step10,
        TickRateDown,
        TickRateUp,
        SpeedDown,
        SpeedUp,
        Uncapped,
        ClearBuffer,
        Record,
        Play,
        SaveSnapshot,
        LoadSnapshot,
    }

    /// <summary>
    /// User-rebindable hotkeys, persisted through MelonPreferences.
    ///
    /// Bindings are stored as <see cref="KeyCode"/> *names* rather than the
    /// underlying integers: the numeric values are an engine implementation
    /// detail, and a config file that reads "F11" survives being hand-edited in
    /// a way that one reading "301" does not.
    ///
    /// Rebinding is a small state machine rather than a blocking prompt —
    /// <see cref="BeginRebind"/> arms the capture, and the next real key press
    /// seen by <see cref="PollRebind"/> becomes the binding. That keeps the
    /// whole thing inside the normal per-frame update, with no modal loop that
    /// would have to pump the game itself.
    /// </summary>
    public static class Keybinds
    {
        public sealed class Binding
        {
            public TasAction Action;
            public string Label;
            public KeyCode Default;
            public KeyCode Key;
            internal MelonPreferences_Entry<string> Entry;
        }

        private static readonly Dictionary<TasAction, Binding> Map = new Dictionary<TasAction, Binding>();
        private static readonly List<Binding> Ordered = new List<Binding>();

        /// <summary>Every binding, in display order.</summary>
        public static IReadOnlyList<Binding> All => Ordered;

        /// <summary>The action currently waiting for a key, or null.</summary>
        public static TasAction? Rebinding { get; private set; }

        /// <summary>
        /// Candidate keys for capture. Joystick codes are excluded: there are
        /// ~200 of them, they are indistinguishable from one another in a
        /// keyboard-driven tool, and scanning them all is pure cost.
        /// </summary>
        private static KeyCode[] _capturable;
        private static bool _consumedRebind;

        public static void Initialize(MelonPreferences_Category cfg)
        {
            Map.Clear();
            Ordered.Clear();
            Define(cfg, TasAction.TogglePanel, "Toggle panel", KeyCode.Insert);
            Define(cfg, TasAction.ToggleEsp, "Toggle enemy ESP", KeyCode.F1);
            Define(cfg, TasAction.Pause, "Pause / resume", KeyCode.F2);
            Define(cfg, TasAction.Step, "Step 1 frame", KeyCode.F3);
            Define(cfg, TasAction.Step10, "Step 10 frames", KeyCode.F4);
            Define(cfg, TasAction.TickRateDown, "Sim rate -10", KeyCode.F5);
            Define(cfg, TasAction.TickRateUp, "Sim rate +10", KeyCode.F6);
            Define(cfg, TasAction.SpeedDown, "Speed down", KeyCode.F7);
            Define(cfg, TasAction.SpeedUp, "Speed up", KeyCode.F8);
            Define(cfg, TasAction.Uncapped, "Turbo (uncapped)", KeyCode.F9);
            Define(cfg, TasAction.ClearBuffer, "Clear input buffer", KeyCode.F10);
            Define(cfg, TasAction.Record, "Record", KeyCode.F11);
            Define(cfg, TasAction.Play, "Play", KeyCode.F12);
            Define(cfg, TasAction.SaveSnapshot, "Save snapshot (arms auto-load)", KeyCode.Home);
            Define(cfg, TasAction.LoadSnapshot, "Load snapshot", KeyCode.End);
        }

        private static void Define(MelonPreferences_Category cfg, TasAction action, string label, KeyCode def)
        {
            var entry = cfg.CreateEntry("Key_" + action, def.ToString(), description: label);

            var b = new Binding
            {
                Action = action,
                Label = label,
                Default = def,
                Key = Parse(entry.Value, def),
                Entry = entry,
            };

            Map[action] = b;
            Ordered.Add(b);
        }

        private static KeyCode Parse(string name, KeyCode fallback) =>
            Enum.TryParse<KeyCode>(name, out var k) && Enum.IsDefined(typeof(KeyCode), k) ? k : fallback;

        public static void Reload()
        {
            var used = new HashSet<KeyCode>();
            foreach (var b in Ordered)
            {
                b.Key = Parse(b.Entry.Value, b.Default);
                if (b.Key != KeyCode.None && !used.Add(b.Key)) b.Key = KeyCode.None;
                b.Entry.Value = b.Key.ToString();
            }
        }

        public static Binding Get(TasAction action) => Map[action];

        public static KeyCode Key(TasAction action) => Map[action].Key;

        public static bool IsReserved(KeyCode key)
        {
            if (Rebinding.HasValue || _consumedRebind) return true;
            foreach (var binding in Ordered)
                if (binding.Key != KeyCode.None && binding.Key == key) return true;
            return false;
        }

        /// <summary>
        /// True on the real frame the bound key goes down. Callers must already
        /// be running with <see cref="VirtualInput.Bypass"/> set — hotkeys read
        /// hardware, never the virtual state they control.
        /// </summary>
        public static bool Down(TasAction action)
        {
            // While capturing, every key belongs to the capture. Otherwise
            // pressing F3 to bind it would also step a frame.
            if (Rebinding.HasValue || _consumedRebind) return false;

            var k = Map[action].Key;
            return k != KeyCode.None && Input.GetKeyDown(k);
        }

        public static void Set(TasAction action, KeyCode key)
        {
            var b = Map[action];

            // A key can only drive one action; silently letting two share it
            // would make one of them look broken.
            foreach (var other in Ordered)
                if (other != b && other.Key == key)
                {
                    other.Key = KeyCode.None;
                    other.Entry.Value = KeyCode.None.ToString();
                }

            b.Key = key;
            b.Entry.Value = key.ToString();
            MelonPreferences.Save();
        }

        public static void BeginRebind(TasAction action) => Rebinding = action;

        public static void CancelRebind() => Rebinding = null;

        /// <summary>
        /// Cancel a pending capture without going through <see cref="PollRebind"/>.
        ///
        /// <see cref="_consumedRebind"/> is only recomputed inside PollRebind, and
        /// the caller skips PollRebind while a text field has focus — so arming a
        /// rebind and then clicking, say, the replay-name field left
        /// <c>_consumedRebind</c> latched true forever: <see cref="IsReserved"/>
        /// would then report every key reserved and <see cref="Down"/> would fire
        /// for nothing, including the panel toggle. A text field taking focus is
        /// also the right UX to cancel a capture anyway — it cannot see keys while
        /// ImGui owns them.
        /// </summary>
        public static void SuspendRebind()
        {
            Rebinding = null;
            _consumedRebind = false;
        }

        public static void ResetAll()
        {
            foreach (var b in Ordered)
            {
                b.Key = b.Default;
                b.Entry.Value = b.Default.ToString();
            }
            MelonPreferences.Save();
        }

        /// <summary>
        /// Consume the next real key press as the pending binding. Call once per
        /// real frame while bypassing the input patches.
        /// </summary>
        public static void PollRebind()
        {
            _consumedRebind = Rebinding.HasValue;
            if (!Rebinding.HasValue) return;

            // Escape aborts, Backspace unbinds. Neither is bindable as a result,
            // which is an acceptable trade for always having a way out.
            if (Input.GetKeyDown(KeyCode.Escape)) { Rebinding = null; return; }

            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                Set(Rebinding.Value, KeyCode.None);
                Rebinding = null;
                return;
            }

            foreach (var k in Capturable())
            {
                if (!Input.GetKeyDown(k)) continue;
                Set(Rebinding.Value, k);
                Rebinding = null;
                return;
            }
        }

        private static KeyCode[] Capturable()
        {
            if (_capturable != null) return _capturable;

            var list = new List<KeyCode>();
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None) continue;
                if (k >= KeyCode.JoystickButton0) continue;
                list.Add(k);
            }

            return _capturable = list.ToArray();
        }

        /// <summary>Human-readable binding, for labels like "Pause (F2)".</summary>
        public static string Name(TasAction action)
        {
            var k = Map[action].Key;
            return k == KeyCode.None ? "unbound" : k.ToString();
        }

        public static string Suffix(TasAction action)
        {
            var k = Map[action].Key;
            return k == KeyCode.None ? "" : " (" + k + ")";
        }
    }
}
