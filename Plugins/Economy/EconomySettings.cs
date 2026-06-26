// <copyright file="StashValueSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace Economy
{
    using System.Numerics;
    using ExileBridge;

    /// <summary>Persisted Economy (pricing) settings (public fields so ImGui can edit by ref).</summary>
    public sealed class EconomySettings : IPluginSettings
    {
        /// <summary>Draw value labels over stash items (left panel).</summary>
        public bool ShowOverlay = true;

        /// <summary>Draw value labels over inventory items (right panel).</summary>
        public bool ShowInventoryOverlay;

        /// <summary>Draw value labels on Ritual Tribute Shop reward tiles.</summary>
        public bool ShowRitualPrices = true;

        /// <summary>Draw value labels on Runecraft / Expedition reward rows.</summary>
        public bool ShowRunecraftPrices = true;

        /// <summary>Log ritual rewards that couldn't be priced (to build art→name fallbacks).</summary>
        public bool RitualDebugLog;

        /// <summary>Minimum value (in the display currency) for an item to get a label.</summary>
        public float MinValueEx;

        /// <summary>Hide pricing overlays while hovering over an item slot (so tooltips are readable).</summary>
        public bool HidePriceOnHover = true;

        /// <summary>Price source: 0 = poe.ninja, 1 = poe2scout.</summary>
        public int PriceSource = 1;

        /// <summary>PoE2 league name for price lookups.</summary>
        public string League = "Runes of Aldur";

        /// <summary>Automatic price refresh interval in minutes.</summary>
        public int RefreshIntervalMin = 5;

        /// <summary>Display currency: 0 = Divine, 1 = Exalted, 2 = Chaos.</summary>
        public int DisplayCurrency = 1;

        /// <summary>Font size scale for price labels.</summary>
        public float PriceFontScale = 1.0f;

        /// <summary>Horizontal pixel offset of the label inside the item slot.</summary>
        public float PriceOffsetX = 5f;

        /// <summary>Vertical pixel offset of the label inside the item slot.</summary>
        public float PriceOffsetY = -5f;

        /// <summary>Label text color (RGBA 0-1).</summary>
        public Vector4 TextColor = new(1f, 235f / 255f, 140f / 255f, 1f);
    }
}
