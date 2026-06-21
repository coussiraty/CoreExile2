// <copyright file="MapClearBotSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace MapClearBot
{
    using ExileBridge;

    /// <summary>Persisted MapClearBot settings.</summary>
    public sealed class MapClearBotSettings : IPluginSettings
    {
        /// <summary>Master enable (also toggled with <see cref="ToggleKey" />).</summary>
        public bool Enabled;

        /// <summary>Virtual-key code that toggles the bot on/off.</summary>
        public int ToggleKey = 115; // F4

        /// <summary>Attack with a left click instead of a key.</summary>
        public bool AttackWithLeftClick;

        /// <summary>Virtual-key code of the attack/skill.</summary>
        public int AttackKey = 81; // Q

        /// <summary>Move with a left click instead of a key.</summary>
        public bool MoveWithLeftClick = true;

        /// <summary>Virtual-key code of the "move only" key (used when not clicking).</summary>
        public int MoveKey = 84; // T

        /// <summary>Engage monsters within this range (grid units).</summary>
        public float CombatRange = 70f;

        /// <summary>Pick up items within this range (grid units).</summary>
        public float LootRange = 50f;

        /// <summary>Whether to pick up ground items.</summary>
        public bool PickItems = true;

        /// <summary>Only loot items whose metadata path contains this text (empty = all).</summary>
        public string LootFilter = string.Empty;

        /// <summary>Require line of sight to a monster before attacking.</summary>
        public bool UseLineOfSight = true;

        /// <summary>Flee when life drops to or below this percent (0 disables).</summary>
        public int FleeLifePercent = 35;

        /// <summary>Minimum milliseconds between bot actions.</summary>
        public int ActionDelayMs = 150;

        /// <summary>Coarse cell size (grid units) used to track explored areas.</summary>
        public int ExploreCellSize = 12;

        /// <summary>How many tiles ahead on the path to aim the move click.</summary>
        public int LookaheadTiles = 14;

        /// <summary>Recompute the path at most this often (ms).</summary>
        public int PathRecomputeMs = 400;

        /// <summary>Abandon an explore target after this many seconds without progress.</summary>
        public float StuckSeconds = 2.0f;

        /// <summary>A* expansion cap (bounds per-frame cost).</summary>
        public int MaxPathNodes = 20000;

        /// <summary>When the map is fully explored, head to the nearest area transition tile.</summary>
        public bool GoToTransitionWhenCleared;

        /// <summary>Draw the current path and target for debugging.</summary>
        public bool ShowPath = true;
    }
}
