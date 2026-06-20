// <copyright file="RayCaster.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoAim
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using ExileBridge;

    /// <summary>
    ///     Ray-casting utilities for line-of-sight and pathfinding, operating on the
    ///     SDK <see cref="ITerrain" /> walkable grid.
    /// </summary>
    public static class RayCaster
    {
        /// <summary>
        ///     Checks if there is a clear line of sight between two grid positions.
        /// </summary>
        /// <param name="terrain">current area terrain.</param>
        /// <param name="fromX">starting X.</param>
        /// <param name="fromY">starting Y.</param>
        /// <param name="toX">target X.</param>
        /// <param name="toY">target Y.</param>
        /// <returns>true if there is a clear line of sight; false if blocked.</returns>
        public static bool HasLineOfSight(ITerrain terrain, int fromX, int fromY, int toX, int toY)
        {
            var points = GetLinePoints(fromX, fromY, toX, toY);
            if (points.Length <= 3)
            {
                return true;
            }

            var solidBlockedCount = 0;
            foreach (var point in points)
            {
                var walkableValue = GetWalkableValue(terrain, point.X, point.Y);
                if (walkableValue <= 2 && walkableValue >= 0)
                {
                    solidBlockedCount++;
                }
            }

            // Very restrictive: any solid wall along the ray blocks targeting.
            return solidBlockedCount == 0;
        }

        /// <summary>
        ///     Checks if a monster is targetable (not behind walls).
        /// </summary>
        /// <param name="terrain">current area terrain.</param>
        /// <param name="playerPos">player grid position.</param>
        /// <param name="monsterPos">monster grid position.</param>
        /// <returns>true if the monster has line of sight; otherwise false.</returns>
        public static bool IsMonsterTargetable(ITerrain terrain, Vector2 playerPos, Vector2 monsterPos)
        {
            return HasLineOfSight(
                terrain,
                (int)playerPos.X,
                (int)playerPos.Y,
                (int)monsterPos.X,
                (int)monsterPos.Y);
        }

        /// <summary>
        ///     Bresenham's line algorithm: returns all integer points along a line.
        /// </summary>
        /// <param name="x0">start X.</param>
        /// <param name="y0">start Y.</param>
        /// <param name="x1">end X.</param>
        /// <param name="y1">end Y.</param>
        /// <returns>points along the line.</returns>
        public static (int X, int Y)[] GetLinePoints(int x0, int y0, int x1, int y1)
        {
            var points = new List<(int, int)>();

            var dx = Math.Abs(x1 - x0);
            var dy = Math.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;

            var x = x0;
            var y = y0;

            while (true)
            {
                points.Add((x, y));
                if (x == x1 && y == y1)
                {
                    break;
                }

                var e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }

            return points.ToArray();
        }

        /// <summary>
        ///     Gets the walkable nibble value at a grid position (0 = blocked).
        /// </summary>
        /// <param name="terrain">current area terrain.</param>
        /// <param name="x">grid X.</param>
        /// <param name="y">grid Y.</param>
        /// <returns>the 0-15 walkable value, or 0 when out of bounds.</returns>
        public static int GetWalkableValue(ITerrain terrain, int x, int y)
        {
            var mapWalkableData = terrain?.WalkableData;
            var bytesPerRow = terrain?.BytesPerRow ?? 0;

            if (mapWalkableData == null || mapWalkableData.Length == 0 || bytesPerRow <= 0)
            {
                return 0;
            }

            var totalRows = mapWalkableData.Length / bytesPerRow;
            var width = bytesPerRow * 2; // 2 nibbles per byte

            if (x < 0 || y < 0 || x >= width || y >= totalRows)
            {
                return 0;
            }

            var index = (y * bytesPerRow) + (x / 2);
            if (index >= mapWalkableData.Length)
            {
                return 0;
            }

            var data = mapWalkableData[index];
            var shiftAmount = (x % 2 == 0) ? 0 : 4;

            return (data >> shiftAmount) & 0xF;
        }
    }
}
