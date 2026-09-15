using System;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Owns the mouse cursor while the TAS panel is open.
    ///
    /// The panel needs mouse interaction, so opening it has to free the cursor; the game
    /// locks and hides it during play. Closing it has to put the cursor back —
    /// but back to <i>what the game wants right now</i>, not to whatever was true
    /// when the panel opened. Those differ all the time: open the panel, hit Esc
    /// to bring up the game's pause menu, close the panel, and restoring the
    /// saved "locked" state would leave the pause menu with no usable cursor.
    ///
    /// So the close path re-derives the state instead of replaying a snapshot:
    ///
    /// <list type="bullet">
    /// <item>the game is paused (<c>Paused.IsPaused</c>) — a menu is up, it owns
    ///       the cursor, leave it free;</item>
    /// <item>there is no player controller in the scene — the main menu, same;</item>
    /// <item>otherwise gameplay is live, so lock and hide.</item>
    /// </list>
    ///
    /// While the panel is open the free state is re-asserted every frame rather
    /// than set once, because the game re-locks the cursor on its own schedule
    /// (<c>MobileFPS.Awake</c> does it on scene load, and menu transitions do it
    /// again) and a cursor that vanishes mid-click would look like the panel
    /// broke.
    /// </summary>
    public static class CursorController
    {
        /// <summary>True while the panel holds the cursor.</summary>
        public static bool Holding { get; private set; }

        /// <summary>The cursor was already free when the panel took it.</summary>
        private static bool _freeOnAcquire;

        /// <summary>The game asserted a lock at some point while the panel held the cursor.</summary>
        private static bool _gameRelocked;

        /// <summary>
        /// What the close path will do, live — worth showing in the panel,
        /// because it is the one behaviour here that depends on game state
        /// rather than on a setting.
        ///
        /// The first two tests name the state; the third infers it. Not every
        /// screen that frees the cursor goes through <c>Paused</c> — a keypad or
        /// an inventory view need not — so a cursor that was <i>already</i> free
        /// when the panel opened, and that the game has not re-locked since, is
        /// taken to belong to something that still wants it free.
        /// </summary>
        public static bool GameWantsFreeCursor
        {
            get
            {
                if (Paused() || !PlayerPresent()) return true;
                if (_gameRelocked) return false;
                return _freeOnAcquire;
            }
        }

        /// <summary>Take the cursor, noting the state it was taken from.</summary>
        public static void Acquire()
        {
            _freeOnAcquire = Cursor.lockState == CursorLockMode.None;
            _gameRelocked = false;
            Holding = true;
            Apply(free: true);
        }

        /// <summary>Hand the cursor back in the state the game wants *now*.</summary>
        public static void Release()
        {
            var free = GameWantsFreeCursor;
            Holding = false;
            Apply(free);
        }

        /// <summary>Call once per real frame; keeps the cursor free against the game re-locking it.</summary>
        public static void Tick()
        {
            if (!Holding) return;
            if (Cursor.lockState == CursorLockMode.None && Cursor.visible) return;

            // Nothing but the game writes the cursor while the panel holds it,
            // so finding it locked here *is* the game asking for it back. Note
            // that and hand it over on close rather than fighting over it now.
            if (Cursor.lockState == CursorLockMode.Locked) _gameRelocked = true;

            Apply(free: true);
        }

        private static void Apply(bool free)
        {
            try
            {
                Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = free;
            }
            catch
            {
                // Cursor is engine-side and should never throw, but a mod that
                // takes the game down over a cursor write would be worse than
                // one with an odd-looking cursor.
            }
        }

        /// <summary>
        /// <c>Paused.IsPaused</c> is the game's own menu flag. "Cannot tell"
        /// reads as paused: leaving the cursor free is recoverable (press Esc
        /// twice), locking it inside a menu is not.
        /// </summary>
        private static bool Paused()
        {
            try { return Paused_IsPaused(); }
            catch { return true; }
        }

        private static bool Paused_IsPaused() => Il2Cpp.Paused.IsPaused;

        /// <summary>
        /// No player controller means no gameplay to return the cursor to — the
        /// main menu, or a scene still loading. <see cref="PlayerGate"/> already
        /// caches the lookup, so this costs nothing per frame.
        /// </summary>
        private static bool PlayerPresent()
        {
            try
            {
                var p = PlayerGate.Player;
                if (p == null) p = UnityEngine.Object.FindObjectOfType<MobileFPS>();
                return p != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
