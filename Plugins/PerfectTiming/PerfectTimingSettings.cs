namespace PerfectTiming
{
    using System.Collections.Generic;
    using ExileBridge;
    using Newtonsoft.Json;

    /// <summary>
    ///     A single "hold + release for perfect timing" skill (Perfect Strike, Snipe, etc.). Each has
    ///     its own trigger key, skill output, animation ids, and tuned release delay, so several can be
    ///     active at once.
    /// </summary>
    public sealed class SkillProfile
    {
        public string Name = "Perfect Strike";
        public bool Enabled = true;

        /// <summary>Virtual-key the user holds to fire this skill's perfect cycle.</summary>
        public int TriggerKey = 0;

        /// <summary>Skill output is a mouse button (vs a keyboard key).</summary>
        public bool SkillIsMouse = true;

        /// <summary>Mouse button used (0 = Left, 1 = Right, 2 = Middle).</summary>
        public int SkillMouseButton = 1;

        /// <summary>Keyboard virtual-key used (when not a mouse button).</summary>
        public int SkillKey = 0;

        /// <summary>AnimationId while charging/holding the windup (find via the probe).</summary>
        public int WindupAnim = 461;

        /// <summary>AnimationId of the PERFECT release outcome (the target).</summary>
        public int PerfectAnim = 463;

        /// <summary>AnimationId of a normal (non-perfect) release outcome.</summary>
        public int EndAnim = 462;

        /// <summary>
        ///     Release on the live charge value (Actor+ChargeOffset, a 0..~0.5 animation-phase float)
        ///     instead of a timer — precise and attack-speed independent. This is the good path.
        /// </summary>
        public bool UseChargeValue = true;

        /// <summary>Charge/phase value to release at (the "perfect" point; ~0.43 for Perfect Strike).</summary>
        public float PerfectPhase = 0.43f;

        /// <summary>Actor offset of the animation-phase float (found via the probe; 0x340 for this build).</summary>
        public int ChargeOffset = 0x340;

        /// <summary>Delay (ms) from windup start to release — used only when UseChargeValue is off.</summary>
        public int ReleaseDelayMs = 540;

        /// <summary>
        ///     Auto-scale the release to the live windup length (robust to attack-speed changes): the
        ///     plugin periodically lets a strike complete naturally to measure the windup, then releases
        ///     at <see cref="ReleaseFraction" /> of it.
        /// </summary>
        public bool ScaleToWindup = true;

        /// <summary>Windup length (ms) captured at calibration; the delay scales by WindupEst/WindupRef.</summary>
        public double WindupRef;

        /// <summary>Only fire when a live monster is under/near the cursor (skip loot/ground clicks).</summary>
        public bool OnlyOnMonster = true;

        /// <summary>Screen-pixel radius around the cursor to look for a live monster.</summary>
        public float CursorRadius = 70f;

        /// <summary>Repeat while the trigger key stays held.</summary>
        public bool RepeatWhileHeld = true;

        /// <summary>Nudge the release timing toward the perfect outcome automatically.</summary>
        public bool AutoTune = false;

        // ── runtime state (not persisted) ───────────────────────────────────────────
        [JsonIgnore] public int Phase; // 0 idle, 1 wait-windup, 2 charging, 3 wait-outcome
        [JsonIgnore] public long PressTicks;
        [JsonIgnore] public long OutcomeStart;
        [JsonIgnore] public volatile bool ReleaseScheduled;
        [JsonIgnore] public volatile bool SkillHeld;
        [JsonIgnore] public long HoldToken;
        [JsonIgnore] public double WindupEst;
        [JsonIgnore] public long WindupStartTicks;
        [JsonIgnore] public int StrikeCount;
        [JsonIgnore] public int StrikeDelayMs;
        [JsonIgnore] public bool ProbeStrike;
        [JsonIgnore] public int TuneDir;
        [JsonIgnore] public int WindowHits;
        [JsonIgnore] public int WindowCount;
        [JsonIgnore] public double LastRate = -1;
        [JsonIgnore] public bool TriggerWasDown;
        [JsonIgnore] public int ConsecMiss;
        [JsonIgnore] public int Hits;
        [JsonIgnore] public int Misses;
        [JsonIgnore] public bool LastPerfect;
        [JsonIgnore] public Queue<bool> Recent = new();
    }

    /// <summary>Settings for PerfectTiming — auto hold+release for charge/perfect skills.</summary>
    public sealed class PerfectTimingSettings : IPluginSettings
    {
        /// <summary>Master toggle for all profiles.</summary>
        public bool AutoEnabled = false;

        /// <summary>Configured skills. Seeded from the legacy flat fields on first load.</summary>
        public List<SkillProfile> Profiles = new();

        // ── Legacy v1 flat fields — used only to seed the first profile on migration ──
        public int TriggerKey = 0;
        public bool SkillIsMouse = true;
        public int SkillMouseButton = 1;
        public int SkillKey = 0;
        public int ReleaseDelayMs = 540;
        public bool RepeatWhileHeld = true;
        public bool AutoTune = false;
        public int WindupAnim = 461;
        public int PerfectAnim = 463;
        public int EndAnim = 462;

        // ── Probe / diagnostics ─────────────────────────────────────────────────────
        public bool ShowProbe = false;
        public string TargetSkillMatch = "Perfect";
        public int SlotsToShow = 40;
    }
}
