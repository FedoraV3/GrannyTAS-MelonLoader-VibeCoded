namespace GrannyTAS
{
    /// <summary>
    /// How long an enemy has left at a door, transcribed from
    /// <c>AI_Granny::FixedUpdate</c> (0x1801bdcc0) and <c>AI_Grandpa::FixedUpdate</c>,
    /// which share the logic and every constant.
    ///
    /// Each FixedUpdate the enemy casts <c>OpenDoorRay.forward</c> for
    /// <c>DoorDistance</c> against <c>LayerEx</c>. A miss zeroes
    /// <c>TimerDoorOpening</c>. A hit is classified by the collider's
    /// <c>gameObject.name</c> and adds <c>Time.deltaTime</c> — the fixed step —
    /// to the timer until that door's threshold, when the door's open action
    /// runs. Opening renames the object, so the next cast no longer matches.
    ///
    /// Pure, with no engine types, so the countdown can be regression-tested:
    /// it replays the game's own single-precision accumulation, which makes the
    /// tick count exact rather than <c>(threshold - timer) / step</c> rounded.
    /// </summary>
    public static class DoorTimer
    {
        public enum Kind
        {
            /// <summary>Plain door: opens once the timer reaches 2 s (<c>AnimatedStuff.OpenAction</c>).</summary>
            Door,
            /// <summary>Wardrobe or locker: 1 s, and only when <c>CanOpenClosets</c>.</summary>
            Closet,
            /// <summary>Car hood and closet halves the enemy shuts instead: 0.1 s (<c>CloseAction</c>).</summary>
            Shut,
            /// <summary>Sauna door: 2 s while the sauna is in its normal state.</summary>
            Sauna,
            /// <summary>Backdoor and attic door: rattles at 0.8/1.6/2.4 s, opens after 4.3 s.</summary>
            Locked,
            /// <summary>Prison door: rattles at 0.4/0.8/1.2 s, smacked open after 2 s.</summary>
            Prison,
        }

        public sealed class Rule
        {
            public Kind Kind;
            public float Threshold;
            /// <summary>True when the game tests <c>timer &gt; threshold</c> rather than <c>timer &gt;= threshold</c>.</summary>
            public bool Strict;
            public float[] Rattles = new float[0];
            public string Action = "opens";
        }

        private static readonly Rule DoorRule = new Rule { Kind = Kind.Door, Threshold = 2f };
        private static readonly Rule ClosetRule = new Rule { Kind = Kind.Closet, Threshold = 1f };
        private static readonly Rule ShutRule = new Rule { Kind = Kind.Shut, Threshold = 0.1f, Action = "shuts" };
        private static readonly Rule SaunaRule = new Rule { Kind = Kind.Sauna, Threshold = 2f };
        private static readonly Rule LockedRule = new Rule
        {
            Kind = Kind.Locked, Threshold = 4.3f, Strict = true, Rattles = new[] { 0.8f, 1.6f, 2.4f },
        };
        private static readonly Rule PrisonRule = new Rule
        {
            Kind = Kind.Prison, Threshold = 2f, Strict = true, Rattles = new[] { 0.4f, 0.8f, 1.2f }, Action = "breaks",
        };

        /// <summary>The rule for a collider name, or null when the enemy ignores it (and resets).</summary>
        public static Rule ForName(string name, bool canOpenClosets)
        {
            switch (name)
            {
                case "Innerdoor":
                case "SmallDoor":
                case "MeatDoor":
                case "OldHouseWoodenDoor":
                case "OuthouseDoor":
                case "StealDoor":
                    return DoorRule;
                case "GarderobDoor":
                case "MetalLockerDoor":
                    return canOpenClosets ? ClosetRule : null;
                case "Motorhuv2":
                case "ClosetDoorR2":
                case "ClosetDoorL2":
                    return ShutRule;
                case "BastuDoor":
                    return SaunaRule;
                case "Backdoor":
                case "InnerdoorVind":
                    return LockedRule;
                case "prisonDoor":
                    return PrisonRule;
                default:
                    return null;
            }
        }

        public static bool Done(Rule rule, float timer) =>
            rule.Strict ? timer > rule.Threshold : timer >= rule.Threshold;

        /// <summary>
        /// FixedUpdate steps still to run before the door acts, counting the
        /// step that acts. Accumulates exactly as the game does
        /// (<c>timer = deltaTime + timer</c> in single precision). -1 when the
        /// step is not positive.
        /// </summary>
        public static int TicksRemaining(Rule rule, float timer, float step)
        {
            if (!(step > 0f)) return -1;
            var n = 0;
            while (!Done(rule, timer))
            {
                timer = step + timer;
                if (++n > 1_000_000) return -1;
            }
            return n;
        }

        /// <summary>Seconds still to accumulate, for display.</summary>
        public static float SecondsRemaining(Rule rule, float timer) =>
            rule.Threshold - timer > 0f ? rule.Threshold - timer : 0f;

        public static float Progress(Rule rule, float timer) =>
            rule.Threshold > 0f ? (timer / rule.Threshold < 1f ? (timer > 0f ? timer / rule.Threshold : 0f) : 1f) : 1f;
    }
}
