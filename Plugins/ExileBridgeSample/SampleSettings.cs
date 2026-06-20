// <copyright file="SampleSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridgeSample
{
    using ExileBridge;

    /// <summary>Settings for the <see cref="SamplePlugin" />.</summary>
    public sealed class SampleSettings : IPluginSettings
    {
        /// <summary>Gets or sets a value indicating whether the demo window is shown.</summary>
        public bool ShowWindow = true;

        /// <summary>Gets or sets a value indicating whether to list nearby monsters.</summary>
        public bool ShowNearbyMonsters = true;
    }
}
