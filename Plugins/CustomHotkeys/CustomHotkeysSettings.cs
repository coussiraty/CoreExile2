// <copyright file="CustomHotkeysSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace CustomHotkeys
{
    using System.Collections.Generic;
    using ExileBridge;

    /// <summary>Kind of step inside a macro.</summary>
    public enum MacroStepType
    {
        /// <summary>Press and release a key.</summary>
        KeyPress = 0,

        /// <summary>Left mouse click.</summary>
        LeftClick = 1,

        /// <summary>Right mouse click.</summary>
        RightClick = 2,

        /// <summary>Middle mouse click.</summary>
        MiddleClick = 3,

        /// <summary>Wait for a fixed delay.</summary>
        Delay = 4,
    }

    /// <summary>A single action inside a macro.</summary>
    public sealed class MacroStep
    {
        /// <summary>Gets or sets the step type.</summary>
        public MacroStepType Type { get; set; } = MacroStepType.KeyPress;

        /// <summary>Gets or sets the virtual-key code (for key steps).</summary>
        public int Key { get; set; }

        /// <summary>Gets or sets the delay in milliseconds (for delay steps / key hold).</summary>
        public int DelayMs { get; set; } = 50;
    }

    /// <summary>A hotkey-triggered macro.</summary>
    public sealed class Macro
    {
        /// <summary>Gets or sets the macro display name.</summary>
        public string Name { get; set; } = "New Macro";

        /// <summary>Gets or sets the trigger virtual-key code.</summary>
        public int TriggerKey { get; set; }

        /// <summary>Gets or sets a value indicating whether the macro is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the ordered steps.</summary>
        public List<MacroStep> Steps { get; set; } = new();
    }

    /// <summary>Persisted CustomHotkeys settings.</summary>
    public sealed class CustomHotkeysSettings : IPluginSettings
    {
        /// <summary>Whether the macro engine is active.</summary>
        public bool Enabled = true;

        /// <summary>Whether macros only fire while the game window is focused.</summary>
        public bool OnlyWhenForeground = true;

        /// <summary>Gets or sets the configured macros.</summary>
        public List<Macro> Macros { get; set; } = new();
    }
}
