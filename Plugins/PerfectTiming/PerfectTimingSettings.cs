namespace PerfectTiming
{
    using ExileBridge;

    /// <summary>Settings for PerfectTiming — auto hold+release for the Warrior "Perfect Strike".</summary>
    public sealed class PerfectTimingSettings : IPluginSettings
    {
        // ── Auto strike ─────────────────────────────────────────────────────────────
        /// <summary>Master toggle for the auto hold+release.</summary>
        public bool AutoEnabled = false;

        /// <summary>Virtual-key the user presses/holds to fire an auto Perfect Strike.</summary>
        public int TriggerKey = 0;

        /// <summary>When true, the skill output is a mouse button; otherwise a keyboard key.</summary>
        public bool SkillIsMouse = true;

        /// <summary>Mouse button used for the skill (0 = Left, 1 = Right, 2 = Middle).</summary>
        public int SkillMouseButton = 1;

        /// <summary>Keyboard virtual-key used for the skill (when not a mouse button).</summary>
        public int SkillKey = 0;

        /// <summary>Delay (ms) from windup start (anim 461) to release. This is what "perfect" tunes.</summary>
        public int ReleaseDelayMs = 540;

        /// <summary>Repeat strikes while the trigger key stays held.</summary>
        public bool RepeatWhileHeld = true;

        /// <summary>Nudge the release delay toward the perfect outcome automatically.</summary>
        public bool AutoTune = false;

        // ── Animation ids (exposed in case a patch shifts them) ─────────────────────
        /// <summary>Animation id while charging/holding the windup.</summary>
        public int WindupAnim = 461;

        /// <summary>Animation id of the PERFECT release outcome (the target).</summary>
        public int PerfectAnim = 463;

        /// <summary>Animation id of a non-perfect (normal) release outcome.</summary>
        public int EndAnim = 462;

        // ── Probe / diagnostics ─────────────────────────────────────────────────────
        /// <summary>Show the live probe window (Actor animation + skill struct values).</summary>
        public bool ShowProbe = false;

        /// <summary>Case-insensitive substring used to pick the "target" skill to inspect/log.</summary>
        public string TargetSkillMatch = "Perfect";

        /// <summary>How many int32 slots of the skill-details struct to show live (from offset 0).</summary>
        public int SlotsToShow = 40;
    }
}
