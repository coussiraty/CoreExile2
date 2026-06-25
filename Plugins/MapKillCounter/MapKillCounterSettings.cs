// <copyright file="MapKillCounterSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace MapKillCounter
{
    using System.Numerics;
    using ExileBridge;

    /// <summary>Kill-list orientation.</summary>
    public enum KillListLayout
    {
        /// <summary>One rarity per line.</summary>
        Vertical,

        /// <summary>All rarities on a single line.</summary>
        Horizontal,
    }

    /// <summary>Map overlay verbosity.</summary>
    public enum MapOverlayMode
    {
        /// <summary>Kills + time.</summary>
        Full,

        /// <summary>A minimal clock only.</summary>
        TimerOnly,
    }

    /// <summary>Persisted MapKillCounter settings (public fields so ImGui can edit by ref).</summary>
    public sealed class MapKillCounterSettings : IPluginSettings
    {
        /// <summary>Show the per-map overlay window.</summary>
        public bool ShowOverlay = true;

        /// <summary>Map overlay mode (full or timer-only).</summary>
        public MapOverlayMode OverlayMode = MapOverlayMode.Full;

        /// <summary>Show the separate session-totals overlay window.</summary>
        public bool ShowSessionOverlay;

        /// <summary>Pause the map timer while in town/hideout.</summary>
        public bool PauseTimerInTownOrHideout = true;

        /// <summary>Pause the map timer while the game is not the foreground window.</summary>
        public bool PauseTimerWhenGameInBackground;

        /// <summary>Hide the overlay while PoE is not the foreground window.</summary>
        public bool HideOverlayWhenGameInBackground = true;

        /// <summary>Pause the map timer while the in-game escape menu is open (ESC).</summary>
        public bool PauseTimerWhenGamePaused = true;

        /// <summary>Count kills that happen in town/hideout.</summary>
        public bool CountKillsInTownOrHideout;

        /// <summary>When true, stats survive town/hideout visits and reset only on a new map instance.</summary>
        public bool ResetOnlyOnNewMap = true;

        /// <summary>Map overlay window position.</summary>
        public Vector2 OverlayPosition = new(40f, 120f);

        /// <summary>Session overlay window position.</summary>
        public Vector2 SessionOverlayPosition = new(40f, 300f);

        /// <summary>Fixed map overlay size in pixels. (0,0) picks a layout default.</summary>
        public Vector2 OverlaySize = Vector2.Zero;

        /// <summary>Fixed session overlay size in pixels. (0,0) picks a layout default.</summary>
        public Vector2 SessionOverlaySize = Vector2.Zero;

        /// <summary>Kill-list orientation.</summary>
        public KillListLayout Layout = KillListLayout.Vertical;

        /// <summary>Per-window font scale (so other plugins cannot resize this overlay).</summary>
        public float OverlayFontScale = 1f;

        /// <summary>Window background color.</summary>
        public Vector4 BackgroundColor = new(0f, 0f, 0f, 0.72f);

        /// <summary>Text color.</summary>
        public Vector4 TextColor = new(1f, 1f, 1f, 1f);

        /// <summary>Normal-rarity kill color.</summary>
        public Vector4 NormalColor = new(0.92f, 0.92f, 0.92f, 1f);

        /// <summary>Magic-rarity kill color.</summary>
        public Vector4 MagicColor = new(0.35f, 0.55f, 1f, 1f);

        /// <summary>Rare-rarity kill color.</summary>
        public Vector4 RareColor = new(1f, 1f, 0.2f, 1f);

        /// <summary>Unique-rarity kill color.</summary>
        public Vector4 UniqueColor = new(1f, 0.55f, 0.1f, 1f);
    }
}
