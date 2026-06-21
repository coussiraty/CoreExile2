// <copyright file="Pathfinder.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace MapClearBot
{
    using System;
    using System.Collections.Generic;
    using ExileBridge;

    /// <summary>
    ///     A* pathfinding and frontier search over the area walkable grid
    ///     (<see cref="ITerrain.WalkableData" />). A cell is walkable when its 4-bit
    ///     value is non-zero (0 = wall). All searches are bounded by a node cap so a
    ///     single frame can never hang.
    /// </summary>
    internal sealed class Pathfinder
    {
        private static readonly (int Dx, int Dy, float Cost)[] Neighbors =
        {
            (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
            (1, 1, 1.41421356f), (1, -1, 1.41421356f), (-1, 1, 1.41421356f), (-1, -1, 1.41421356f),
        };

        private byte[] data = Array.Empty<byte>();
        private int bytesPerRow;
        private int width;
        private int rows;

        /// <summary>Gets the grid width in cells.</summary>
        public int Width => this.width;

        /// <summary>Gets the grid height in cells.</summary>
        public int Rows => this.rows;

        /// <summary>Gets a value indicating whether a usable grid is bound.</summary>
        public bool Ready => this.data.Length > 0 && this.bytesPerRow > 0;

        /// <summary>Re-reads grid dimensions from the terrain (cheap; call per frame).</summary>
        /// <param name="terrain">current area terrain.</param>
        public void Bind(ITerrain terrain)
        {
            var d = terrain?.WalkableData;
            var bpr = terrain?.BytesPerRow ?? 0;
            if (d == null || bpr <= 0)
            {
                this.data = Array.Empty<byte>();
                this.bytesPerRow = 0;
                this.width = 0;
                this.rows = 0;
                return;
            }

            this.data = d;
            this.bytesPerRow = bpr;
            this.width = bpr * 2;
            this.rows = d.Length / bpr;
        }

        /// <summary>Returns whether a grid cell is walkable (non-zero, in bounds).</summary>
        /// <param name="x">grid X.</param>
        /// <param name="y">grid Y.</param>
        /// <returns>true if walkable.</returns>
        public bool IsWalkable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= this.width || y >= this.rows)
            {
                return false;
            }

            var index = (y * this.bytesPerRow) + (x / 2);
            if ((uint)index >= (uint)this.data.Length)
            {
                return false;
            }

            return (((this.data[index] >> ((x % 2 == 0) ? 0 : 4)) & 0xF) > 0);
        }

        /// <summary>Straight-line line of sight on the grid (blocked by any wall cell).</summary>
        /// <param name="x0">from X.</param>
        /// <param name="y0">from Y.</param>
        /// <param name="x1">to X.</param>
        /// <param name="y1">to Y.</param>
        /// <returns>true if unobstructed.</returns>
        public bool HasLineOfSight(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            var err = dx - dy;
            var steps = 0;
            while (true)
            {
                if (++steps > 2 && !this.IsWalkable(x0, y0))
                {
                    return false;
                }

                if (x0 == x1 && y0 == y1)
                {
                    return true;
                }

                var e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        /// <summary>
        ///     A* from <paramref name="start" /> to <paramref name="goal" />. Returns
        ///     the cell path (start..goal) or null if unreachable within
        ///     <paramref name="maxNodes" /> expansions. If the goal cell is a wall, the
        ///     nearest walkable cell to it (within a small radius) is used instead.
        /// </summary>
        /// <param name="start">start cell.</param>
        /// <param name="goal">goal cell.</param>
        /// <param name="maxNodes">expansion cap.</param>
        /// <returns>path or null.</returns>
        public List<(int X, int Y)>? FindPath((int X, int Y) start, (int X, int Y) goal, int maxNodes)
        {
            if (!this.Ready || !this.IsWalkable(start.X, start.Y))
            {
                return null;
            }

            if (!this.IsWalkable(goal.X, goal.Y) && !this.TryNearestWalkable(goal.X, goal.Y, 6, out goal))
            {
                return null;
            }

            var startId = this.Id(start.X, start.Y);
            var goalId = this.Id(goal.X, goal.Y);

            var open = new PriorityQueue<int, float>();
            var gScore = new Dictionary<int, float> { [startId] = 0f };
            var cameFrom = new Dictionary<int, int>();
            var closed = new HashSet<int>();
            open.Enqueue(startId, this.Heuristic(start.X, start.Y, goal.X, goal.Y));

            var expanded = 0;
            while (open.Count > 0)
            {
                var current = open.Dequeue();
                if (current == goalId)
                {
                    return this.Reconstruct(cameFrom, current);
                }

                if (!closed.Add(current))
                {
                    continue;
                }

                if (++expanded > maxNodes)
                {
                    return null;
                }

                var cx = current % this.width;
                var cy = current / this.width;
                var baseG = gScore[current];

                foreach (var (dx, dy, cost) in Neighbors)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (!this.IsWalkable(nx, ny))
                    {
                        continue;
                    }

                    // No diagonal corner-cutting through walls.
                    if (dx != 0 && dy != 0 && (!this.IsWalkable(cx + dx, cy) || !this.IsWalkable(cx, cy + dy)))
                    {
                        continue;
                    }

                    var nid = this.Id(nx, ny);
                    if (closed.Contains(nid))
                    {
                        continue;
                    }

                    var tentative = baseG + cost;
                    if (gScore.TryGetValue(nid, out var known) && tentative >= known)
                    {
                        continue;
                    }

                    gScore[nid] = tentative;
                    cameFrom[nid] = current;
                    open.Enqueue(nid, tentative + this.Heuristic(nx, ny, goal.X, goal.Y));
                }
            }

            return null;
        }

        /// <summary>
        ///     BFS from <paramref name="start" /> for the nearest walkable cell whose
        ///     coarse cell (<paramref name="cellSize" />) is not in
        ///     <paramref name="explored" /> — i.e. the nearest unexplored frontier.
        /// </summary>
        /// <param name="start">start cell.</param>
        /// <param name="explored">set of visited coarse cells.</param>
        /// <param name="cellSize">coarse cell size.</param>
        /// <param name="maxNodes">expansion cap.</param>
        /// <param name="result">the frontier cell, when returning true.</param>
        /// <returns>true if a frontier was found.</returns>
        public bool TryNearestFrontier(
            (int X, int Y) start,
            HashSet<(int, int)> explored,
            int cellSize,
            int maxNodes,
            out (int X, int Y) result)
        {
            result = default;
            if (!this.Ready || !this.IsWalkable(start.X, start.Y) || cellSize <= 0)
            {
                return false;
            }

            var queue = new Queue<int>();
            var seen = new HashSet<int>();
            var startId = this.Id(start.X, start.Y);
            queue.Enqueue(startId);
            seen.Add(startId);
            var expanded = 0;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var cx = current % this.width;
                var cy = current / this.width;

                if (!explored.Contains((cx / cellSize, cy / cellSize)))
                {
                    result = (cx, cy);
                    return true;
                }

                if (++expanded > maxNodes)
                {
                    return false;
                }

                foreach (var (dx, dy, _) in Neighbors)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (!this.IsWalkable(nx, ny))
                    {
                        continue;
                    }

                    var nid = this.Id(nx, ny);
                    if (seen.Add(nid))
                    {
                        queue.Enqueue(nid);
                    }
                }
            }

            return false;
        }

        private bool TryNearestWalkable(int x, int y, int radius, out (int X, int Y) result)
        {
            for (var r = 0; r <= radius; r++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        {
                            continue;
                        }

                        if (this.IsWalkable(x + dx, y + dy))
                        {
                            result = (x + dx, y + dy);
                            return true;
                        }
                    }
                }
            }

            result = (x, y);
            return false;
        }

        private List<(int X, int Y)> Reconstruct(Dictionary<int, int> cameFrom, int current)
        {
            var path = new List<(int X, int Y)> { (current % this.width, current / this.width) };
            while (cameFrom.TryGetValue(current, out var prev))
            {
                current = prev;
                path.Add((current % this.width, current / this.width));
            }

            path.Reverse();
            return path;
        }

        private int Id(int x, int y) => (y * this.width) + x;

        private float Heuristic(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            // Octile distance.
            return (dx + dy) + ((1.41421356f - 2f) * Math.Min(dx, dy));
        }
    }
}
