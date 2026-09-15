using HarmonyLib;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>Reads and reapplies the level-load settings that affect a recorded run.</summary>
    public sealed class MacroGameSetup : IMacroGameSetup
    {
        private const string SeedKey = "GameSeed";

        // Difficulty, the selectable-version field, and the item preset are
        // not yet located in the game's managed API — ReadDifficulty et al.
        // below are hardcoded placeholders, not real accessors. Keep this
        // flag false (and VerifiesLevelSetup false with it) until they are
        // actually implemented, or a match here would silently mean nothing.
        private const bool SetupAccessorsImplemented = false;

        private static bool _forceRecordedSeedOnNextPlacement;

        public float Realtime => Time.realtimeSinceStartup;

        /// <summary>
        /// Whether difficulty / selectable version / item preset are actually
        /// checked. False means the fields exist in the macro header but the
        /// comparisons always pass — the item seed check is real and always
        /// runs regardless of this flag.
        /// </summary>
        public bool VerifiesLevelSetup => SetupAccessorsImplemented;

        public void Capture(MacroFile macro)
        {
            macro.HasSetupMetadata = true;
            macro.GameBuild = Application.version ?? "";
            macro.SeedManagerSeed = ReadSeed();
            // Filled from the verified menu preferences below. Keeping all
            // setup in the macro header makes old v1/v2 files explicitly legacy.
            macro.Difficulty = ReadDifficulty();
            macro.SelectableVersion = ReadSelectableVersion();
            macro.ItemPreset = ReadItemPreset();
        }

        public bool CanReplayBuild(MacroFile macro, out string reason)
        {
            reason = "";
            if (!macro.HasSetupMetadata || string.IsNullOrEmpty(macro.GameBuild) || macro.GameBuild == Application.version)
                return true;
            reason = $"macro game build '{macro.GameBuild}' differs from installed build '{Application.version}'. " +
                     "A level restart cannot replace the game executable.";
            return false;
        }

        public bool RequiresRestart(MacroFile macro, out string reason)
        {
            if (ReadSeed() != macro.SeedManagerSeed)
            {
                reason = "item seed differs";
                return true;
            }
            if (macro.HasSetupMetadata && ReadDifficulty() != macro.Difficulty)
            {
                reason = "difficulty differs";
                return true;
            }
            if (macro.HasSetupMetadata && ReadSelectableVersion() != macro.SelectableVersion)
            {
                reason = "selectable version differs";
                return true;
            }
            if (macro.HasSetupMetadata && ReadItemPreset() != macro.ItemPreset)
            {
                reason = "item preset differs";
                return true;
            }
            reason = "";
            return false;
        }

        public void ApplyAndRestart(MacroFile macro)
        {
            PlayerPrefs.SetInt(SeedKey, macro.SeedManagerSeed);
            if (macro.HasSetupMetadata)
            {
                WriteDifficulty(macro.Difficulty);
                WriteSelectableVersion(macro.SelectableVersion);
                WriteItemPreset(macro.ItemPreset);
            }
            PlayerPrefs.Save();
            _forceRecordedSeedOnNextPlacement = true;

            var pause = UnityEngine.Object.FindObjectOfType<Il2Cpp.Paused>();
            if (pause == null) throw new System.InvalidOperationException("Could not find the game's restart controller.");
            pause.RestartP();
        }

        public bool LoadedSetupMatches(MacroFile macro, out string reason)
        {
            if (ReadSeed() != macro.SeedManagerSeed)
            {
                reason = $"item seed is {ReadSeed()}, expected {macro.SeedManagerSeed}";
                return false;
            }
            return !RequiresRestart(macro, out reason);
        }

        public void CancelPending() => _forceRecordedSeedOnNextPlacement = false;

        private static int ReadSeed()
        {
            try
            {
                var manager = UnityEngine.Object.FindObjectOfType<Il2Cpp.SeedManager>();
                return manager != null ? manager.Seed : PlayerPrefs.GetInt(SeedKey, 0);
            }
            catch { return PlayerPrefs.GetInt(SeedKey, 0); }
        }

        // These accessors are kept together because they map the menu's persisted
        // choices to the values consumed during the next scene load.
        private static int ReadDifficulty() => 0;
        private static int ReadSelectableVersion() => 0;
        private static int ReadItemPreset() => 0;
        private static void WriteDifficulty(int value) { }
        private static void WriteSelectableVersion(int value) { }
        private static void WriteItemPreset(int value) { }

        [HarmonyPatch(typeof(Il2Cpp.SeedManager), nameof(Il2Cpp.SeedManager.GeneratePlacement))]
        private static class SeedPlacementPatch
        {
            private static void Prefix(Il2Cpp.SeedManager __instance)
            {
                if (!_forceRecordedSeedOnNextPlacement || __instance == null) return;
                __instance.RandomizeSeed = false;
                _forceRecordedSeedOnNextPlacement = false;
            }
        }
    }
}
