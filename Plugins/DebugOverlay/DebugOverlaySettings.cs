// <copyright file="DebugOverlaySettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace DebugOverlay
{
    using ExileBridge;

    /// <summary>Persisted DebugOverlay settings.</summary>
    public sealed class DebugOverlaySettings : IPluginSettings
    {
        /// <summary>Whether the debug window is shown.</summary>
        public bool ShowWindow = true;

        /// <summary>Whether to list nearby monsters.</summary>
        public bool ListMonsters = true;

        /// <summary>Maximum nearby monsters to list.</summary>
        public int MaxMonsters = 15;
    }
}
