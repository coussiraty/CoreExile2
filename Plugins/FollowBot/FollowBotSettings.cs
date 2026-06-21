// <copyright file="FollowBotSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace FollowBot
{
    using ExileBridge;

    /// <summary>Persisted FollowBot settings.</summary>
    public sealed class FollowBotSettings : IPluginSettings
    {
        /// <summary>Master enable.</summary>
        public bool Enabled;

        /// <summary>Character name of the player to follow.</summary>
        public string LeaderName = string.Empty;

        /// <summary>Start moving when the leader is farther than this (grid units).</summary>
        public float FollowDistance = 25f;

        /// <summary>Use a left click to move instead of a dedicated move key.</summary>
        public bool UseLeftClick = true;

        /// <summary>Virtual-key code of the "move only" key (used when not clicking).</summary>
        public int MoveKey = 84; // 'T'

        /// <summary>Minimum milliseconds between move commands.</summary>
        public int RepathMs = 250;
    }
}
