// <copyright file="MovementController.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    ///     Drives directional (WASD-style) character movement by holding the
    ///     screen-relative direction keys toward a target, so the mouse stays free.
    ///     Converts a player→target screen vector into up to two cardinal keys
    ///     (8-way) and only sends key changes — newly required keys get a key-down,
    ///     no-longer-required keys get a key-up — so nothing is ever left stuck and
    ///     keys are not re-pressed every frame. All key events go through the shared
    ///     <see cref="Input" /> worker.
    /// </summary>
    /// <remarks>
    ///     Assumes screen-relative movement (W = toward the top of the screen), which
    ///     is how Path of Exile 2 WASD movement works. The keys are configurable to
    ///     match the player's in-game bindings.
    /// </remarks>
    public sealed class MovementController
    {
        private static readonly HashSet<int> Empty = new();

        private readonly HashSet<int> held = new();
        private readonly List<int> scratch = new();

        private int up = 0x57;    // W
        private int down = 0x53;  // S
        private int left = 0x41;  // A
        private int right = 0x44; // D

        /// <summary>Gets a value indicating whether any movement key is currently held.</summary>
        public bool IsMoving => this.held.Count > 0;

        /// <summary>Sets the virtual-key codes for the four movement directions.</summary>
        /// <param name="upKey">screen-up key.</param>
        /// <param name="downKey">screen-down key.</param>
        /// <param name="leftKey">screen-left key.</param>
        /// <param name="rightKey">screen-right key.</param>
        public void Configure(int upKey, int downKey, int leftKey, int rightKey)
        {
            this.up = upKey;
            this.down = downKey;
            this.left = leftKey;
            this.right = rightKey;
        }

        /// <summary>
        ///     Holds the direction key(s) that move the character from
        ///     <paramref name="fromScreen" /> toward <paramref name="toScreen" />
        ///     (both in the same screen space). Releases everything when within
        ///     <paramref name="deadzone" /> pixels of the target.
        /// </summary>
        /// <param name="fromScreen">the player's screen position.</param>
        /// <param name="toScreen">the target's screen position.</param>
        /// <param name="deadzone">arrival radius in pixels.</param>
        public void MoveToward(Vector2 fromScreen, Vector2 toScreen, float deadzone = 16f)
        {
            var dx = toScreen.X - fromScreen.X;
            var dy = toScreen.Y - fromScreen.Y;
            var dist = MathF.Sqrt((dx * dx) + (dy * dy));
            if (dist < deadzone)
            {
                this.Stop();
                return;
            }

            // Decide which axes to press, with HYSTERESIS to stop key chatter near
            // sector boundaries: an axis needs a larger share of the vector to
            // ENGAGE (addT ~ 27 deg) than to stay engaged (keepT ~ 15 deg). The
            // dominant axis always exceeds addT (max component >= 0.707*dist), so
            // there is always at least one key.
            var addT = 0.46f * dist;
            var keepT = 0.26f * dist;
            var desired = new HashSet<int>();

            if (dx > 0 && dx > (this.held.Contains(this.right) ? keepT : addT))
            {
                desired.Add(this.right);
            }
            else if (dx < 0 && -dx > (this.held.Contains(this.left) ? keepT : addT))
            {
                desired.Add(this.left);
            }

            if (dy > 0 && dy > (this.held.Contains(this.down) ? keepT : addT))
            {
                desired.Add(this.down);
            }
            else if (dy < 0 && -dy > (this.held.Contains(this.up) ? keepT : addT))
            {
                desired.Add(this.up);
            }

            if (desired.Count == 0)
            {
                // Safety: between sectors, fall back to the dominant axis.
                if (MathF.Abs(dx) >= MathF.Abs(dy))
                {
                    desired.Add(dx >= 0 ? this.right : this.left);
                }
                else
                {
                    desired.Add(dy >= 0 ? this.down : this.up);
                }
            }

            this.Apply(desired);
        }

        /// <summary>Releases all currently held movement keys.</summary>
        public void Stop() => this.Apply(Empty);

        private void Apply(HashSet<int> desired)
        {
            this.scratch.Clear();
            foreach (var k in this.held)
            {
                if (!desired.Contains(k))
                {
                    this.scratch.Add(k);
                }
            }

            foreach (var k in this.scratch)
            {
                Input.KeyUp(k);
                this.held.Remove(k);
            }

            foreach (var k in desired)
            {
                if (this.held.Add(k))
                {
                    Input.KeyDown(k);
                }
            }
        }
    }
}
