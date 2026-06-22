// <copyright file="FollowBotSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace FollowBot
{
    using ExileBridge;

    /// <summary>How the bot moves the character.</summary>
    public enum MovementMode
    {
        /// <summary>Move by pointing the cursor and clicking (hijacks the mouse).</summary>
        MouseClick,

        /// <summary>Move with screen-relative WASD keys (mouse stays free). Requires
        /// WASD movement to be enabled and bound in Path of Exile 2.</summary>
        Wasd,
    }

    /// <summary>Persisted FollowBot settings.</summary>
    public sealed class FollowBotSettings : IPluginSettings
    {
        /// <summary>Master enable.</summary>
        public bool Enabled;

        /// <summary>Character name of the player to follow.</summary>
        public string LeaderName = string.Empty;

        /// <summary>Start moving when the leader is farther than this (grid units).</summary>
        public float FollowDistance = 25f;

        /// <summary>How the character is moved.</summary>
        public MovementMode Movement = MovementMode.MouseClick;

        /// <summary>Use a left click to move instead of a dedicated move key (mouse mode).</summary>
        public bool UseLeftClick = true;

        /// <summary>Virtual-key code of the "move only" key (mouse mode, when not clicking).</summary>
        public int MoveKey = 84; // 'T'

        /// <summary>WASD: screen-up key.</summary>
        public int MoveUpKey = 87; // W

        /// <summary>WASD: screen-down key.</summary>
        public int MoveDownKey = 83; // S

        /// <summary>WASD: screen-left key.</summary>
        public int MoveLeftKey = 65; // A

        /// <summary>WASD: screen-right key.</summary>
        public int MoveRightKey = 68; // D

        /// <summary>WASD arrival radius in pixels.</summary>
        public int MoveDeadzonePx = 22;

        /// <summary>Minimum milliseconds between move commands (mouse mode).</summary>
        public int RepathMs = 250;
    }
}
