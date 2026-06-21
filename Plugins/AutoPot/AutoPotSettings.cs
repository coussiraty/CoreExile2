// <copyright file="AutoPotSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoPot
{
    using System.Collections.Generic;
    using ExileBridge;

    /// <summary>Which vital pool a flask rule watches.</summary>
    public enum VitalKind
    {
        /// <summary>Life / health.</summary>
        Health,

        /// <summary>Energy shield.</summary>
        EnergyShield,

        /// <summary>Mana.</summary>
        Mana,
    }

    /// <summary>A single flask trigger rule.</summary>
    public sealed class FlaskRule
    {
        /// <summary>Display name.</summary>
        public string Name = "Flask";

        /// <summary>Whether this rule is active.</summary>
        public bool Enabled = true;

        /// <summary>Virtual-key code of the flask slot.</summary>
        public int Key = 49; // '1'

        /// <summary>Which vital this rule watches.</summary>
        public VitalKind Vital = VitalKind.Health;

        /// <summary>Trigger when the watched vital drops to or below this percentage.</summary>
        public int ThresholdPercent = 60;

        /// <summary>Minimum milliseconds between uses of this flask.</summary>
        public int CooldownMs = 1500;
    }

    /// <summary>Persisted AutoPot settings.</summary>
    public sealed class AutoPotSettings : IPluginSettings
    {
        /// <summary>Master enable.</summary>
        public bool Enabled = true;

        /// <summary>Do not use flasks while a large panel is open.</summary>
        public bool PauseWhenPanelOpen = true;

        /// <summary>Gets or sets the configured flask rules.</summary>
        public List<FlaskRule> Flasks { get; set; } = new();
    }
}
