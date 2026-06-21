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

        /// <summary>Require line of sight to a monster before attacking.</summary>
        public bool UseLineOfSight = true;

        /// <summary>Minimum milliseconds between bot actions.</summary>
        public int ActionDelayMs = 150;
    }
}
