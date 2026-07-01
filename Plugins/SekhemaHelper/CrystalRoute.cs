namespace SekhemaHelper
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using ExileBridge;
    using ImGuiNET;

    /// <summary>
    /// "Death Crystal Run": paints a walkable pickup route through the lethal Hourglass crystals in
    /// your current Sekhema escape room, drawn on the large area map. Ported from yokkenUA's
    /// SekhemaHelper v0.1.0 onto the ExileBridge SDK.
    ///
    /// The A* route is computed on a background thread (never the render thread) over a snapshot of
    /// pure arrays, and re-projected to the map every frame so it tracks pan/zoom without stutter.
    /// </summary>
    internal sealed class CrystalRoute
    {
        // The lethal "Hourglass" crystal entities all share this metadata-path suffix.
        private const string CrystalPathSuffix = "Hazards/HourglassLethal";

        // Above this crystal count, order legs by straight-line distance (the O(n^2) walkable distance
        // matrix gets too expensive); legs are still DRAWN along terrain.
        private const int MaxWalkableOrderCount = 24;

        // Bound A* expansion per leg — rooms are small; this just caps a pathological unreachable leg.
        private const int LegMaxIterations = 60_000;

        // Re-scan entities / re-trigger the route at most ~5x/sec (the heavy part is off-thread anyway).
        private const long ScanIntervalMs = 200;

        // The last completed route in GRID space; swapped atomically from the background task.
        private volatile List<Leg> routeResult;
        private int computing; // 0 = idle, 1 = a background compute is in flight
        private long lastScanMs;
        private string lastSignature = string.Empty;

        private struct Crystal
        {
            public uint Id;
            public Vector2 Grid;
            public float Height;
            public bool Active;
        }

        private sealed class Leg
        {
            public List<Vector2> Points;   // grid waypoints
            public Vector2 Dest;           // destination crystal grid pos (marker)
            public float DestHeight;
        }

        public void Draw(IContext ctx, SekhemaHelperSettings s)
        {
            // The route lives on the large area map, not the trial-choice panel.
            var largeMap = ctx.Ui.LargeMap;
            if (largeMap == null || !largeMap.IsVisible)
            {
                return;
            }

            var worldMap = ctx.Ui.WorldMapPanel;
            if (worldMap != null && worldMap.IsVisible)
            {
                return;
            }

            if (!ctx.Game.IsInGame)
            {
                return;
            }

            var terrain = ctx.Game.InGame.Terrain;
            var walkable = terrain?.WalkableData;
            if (walkable == null || walkable.Length == 0 || terrain.BytesPerRow <= 0)
            {
                return;
            }

            var player = ctx.Game.InGame.Player;
            if (player == null || !player.TryGetComponent<IRender>(out var pr))
            {
                return;
            }

            var playerGrid = new Vector2(pr.GridPosition.X, pr.GridPosition.Y);
            var playerHeight = pr.TerrainHeight;

            // Heavy work (entity scan + route trigger) is throttled; drawing the cached route is per-frame.
            long now = Environment.TickCount64;
            if (now - this.lastScanMs >= ScanIntervalMs)
            {
                this.lastScanMs = now;
                ScanAndMaybeRecompute(ctx, s, playerGrid, terrain.WalkableData, terrain.BytesPerRow);
            }

            var route = this.routeResult;
            if (route != null && route.Count > 0)
            {
                DrawRoute(ctx, largeMap, playerGrid, playerHeight, terrain.HeightData, s, route);
            }
        }

        // Render-thread: enumerate crystals, scope to the current room, and (if inputs changed) snapshot
        // the pure inputs and kick a background A* compute. SDK / Mem reads happen ONLY here.
        private void ScanAndMaybeRecompute(
            IContext ctx, SekhemaHelperSettings s, Vector2 playerGrid, byte[] walkable, int bytesPerRow)
        {
            var all = new List<Crystal>();
            foreach (var e in ctx.Entities.Awake)
            {
                if (e == null)
                {
                    continue;
                }

                var path = e.Path;
                if (string.IsNullOrEmpty(path) || !path.EndsWith(CrystalPathSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                all.Add(new Crystal
                {
                    Id = e.Id,
                    Grid = new Vector2(r.GridPosition.X, r.GridPosition.Y),
                    Height = r.TerrainHeight,
                    Active = IsActive(e),
                });
            }

            if (all.Count == 0)
            {
                this.routeResult = null;
                return;
            }

            var room = ScopeToRoom(all, playerGrid, Math.Max(1, s.CrystalIdGroupGap), Math.Max(0f, s.CrystalRoomMargin));
            if (room == null)
            {
                this.routeResult = null;
                return;
            }

            var route = room.Where(c => c.Active).ToList();
            if (route.Count == 0)
            {
                this.routeResult = null;
                return;
            }

            // Signature over inputs only (visit order is an OUTPUT, excluded).
            var sb = new StringBuilder();
            sb.Append(s.CrystalWalkablePath ? '1' : '0');
            sb.Append('|').Append((int)(playerGrid.X / 15)).Append(',').Append((int)(playerGrid.Y / 15));
            foreach (var c in route.OrderBy(c => c.Id))
            {
                sb.Append('|').Append((int)c.Grid.X).Append(',').Append((int)c.Grid.Y);
            }

            var sig = sb.ToString();
            if (sig == this.lastSignature)
            {
                return;
            }

            // Only one compute in flight at a time.
            if (Interlocked.CompareExchange(ref this.computing, 1, 0) != 0)
            {
                return;
            }

            this.lastSignature = sig;

            // Snapshot pure inputs for the background thread — NO SDK/Mem access past this point.
            var doors = LineWalker.BuildDoorOverrideMap(ctx.Entities.Awake);
            var crystals = route.Select(c => (c.Grid, c.Height)).ToList();
            var player = playerGrid;
            bool walkPath = s.CrystalWalkablePath;

            Task.Run(() =>
            {
                try
                {
                    this.routeResult = ComputeRoute(crystals, player, walkable, bytesPerRow, doors, walkPath);
                }
                catch
                {
                    // Best-effort overlay: drop this route rather than crash the render loop.
                }
                finally
                {
                    Interlocked.Exchange(ref this.computing, 0);
                }
            });
        }

        // Active = not yet collected. The StateMachine terminal byte at +0x10 flips 0->1 on collect;
        // the named "deactivated" state is authoritative when present, else the "targetable" flag.
        private static bool IsActive(IEntity e)
        {
            if (!e.TryGetComponent<IStateMachine>(out var sm))
            {
                return true;
            }

            if (sm.Address != IntPtr.Zero && Mem.Read<byte>(sm.Address + 0x10) != 0)
            {
                return false;
            }

            var states = sm.States;
            if (states != null && states.Count > 0)
            {
                foreach (var st in states)
                {
                    if (st.Name == "deactivated")
                    {
                        return st.Value == 0;
                    }
                }

                foreach (var st in states)
                {
                    if (st.Name == "targetable")
                    {
                        return st.Value != 0;
                    }
                }
            }

            return true;
        }

        // Cluster crystals by contiguous entity-id blocks (each room's crystals spawn in one id range),
        // pick the block holding the crystal nearest the player, then require the player be inside that
        // block's (margin-expanded) bounding box.
        private static List<Crystal> ScopeToRoom(List<Crystal> all, Vector2 player, int idGap, float margin)
        {
            var sorted = all.OrderBy(c => c.Id).ToList();
            var groups = new List<List<Crystal>>();
            var cur = new List<Crystal> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Id - sorted[i - 1].Id > (uint)idGap)
                {
                    groups.Add(cur);
                    cur = new List<Crystal>();
                }

                cur.Add(sorted[i]);
            }

            groups.Add(cur);

            List<Crystal> best = null;
            float bestDist = float.MaxValue;
            foreach (var g in groups)
            {
                foreach (var c in g)
                {
                    var d = Vector2.DistanceSquared(c.Grid, player);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = g;
                    }
                }
            }

            if (best == null)
            {
                return null;
            }

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in best)
            {
                minX = Math.Min(minX, c.Grid.X);
                minY = Math.Min(minY, c.Grid.Y);
                maxX = Math.Max(maxX, c.Grid.X);
                maxY = Math.Max(maxY, c.Grid.Y);
            }

            if (player.X < minX - margin || player.X > maxX + margin ||
                player.Y < minY - margin || player.Y > maxY + margin)
            {
                return null;
            }

            return best;
        }

        // Background thread: pure A* over snapshotted arrays. Pre-snaps each node ONCE to the nearest
        // walkable cell, orders greedily + 2-opt, then paths each leg.
        private static List<Leg> ComputeRoute(
            List<(Vector2 Grid, float Height)> crystals, Vector2 player,
            byte[] walkable, int bytesPerRow, HashSet<(int, int)> doors, bool walkPath)
        {
            // Pre-snap player + each crystal to the nearest walkable cell (once each, not per pair).
            Vector2 playerNode = player;
            if (walkPath)
            {
                GridPathfinder.SnapToWalkable(walkable, bytesPerRow, player, doors, out playerNode);
            }

            var nodes = new List<(Vector2 Snap, Vector2 Orig, float Height)>(crystals.Count);
            foreach (var c in crystals)
            {
                var snap = c.Grid;
                if (walkPath)
                {
                    GridPathfinder.SnapToWalkable(walkable, bytesPerRow, c.Grid, doors, out snap);
                }

                nodes.Add((snap, c.Grid, c.Height));
            }

            var cache = new Dictionary<long, (List<Vector2> path, float len)>();

            bool walkOrder = walkPath && nodes.Count <= MaxWalkableOrderCount;
            Func<Vector2, Vector2, float> dist = walkOrder
                ? (a, b) => LegOf(a, b, walkable, bytesPerRow, doors, cache).len
                : (a, b) => Vector2.Distance(a, b);

            // Greedy nearest-neighbour from the player, then 2-opt, over the snapped node positions.
            var order = GreedyOrder(nodes, playerNode, dist);
            TwoOpt(order, playerNode, dist);

            var legs = new List<Leg>(order.Count);
            var prev = playerNode;
            foreach (var n in order)
            {
                List<Vector2> pts = null;
                if (walkPath)
                {
                    pts = Oriented(LegOf(prev, n.Snap, walkable, bytesPerRow, doors, cache).path, prev);
                }

                if (pts == null || pts.Count < 2)
                {
                    pts = new List<Vector2> { prev, n.Snap };
                }

                // Close the small gap from the snapped cell to the actual crystal (marker position).
                if (Vector2.DistanceSquared(pts[^1], n.Orig) > 0.5f)
                {
                    pts.Add(n.Orig);
                }

                legs.Add(new Leg { Points = pts, Dest = n.Orig, DestHeight = n.Height });
                prev = n.Snap;
            }

            return legs;
        }

        // Cached A* path + length between two grid points (unordered key — A* length is ~symmetric).
        private static (List<Vector2> path, float len) LegOf(
            Vector2 a, Vector2 b, byte[] walkable, int bytesPerRow,
            HashSet<(int, int)> doors, Dictionary<long, (List<Vector2> path, float len)> cache)
        {
            long key = Key(a, b);
            if (cache.TryGetValue(key, out var hit))
            {
                return hit;
            }

            var path = GridPathfinder.FindPath(walkable, bytesPerRow, a, b, doors, LegMaxIterations);
            float len = (path != null && path.Count >= 2) ? PathLength(path) : Vector2.Distance(a, b) * 3f;
            var res = (path, len);
            cache[key] = res;
            return res;
        }

        private static List<(Vector2 Snap, Vector2 Orig, float Height)> GreedyOrder(
            List<(Vector2 Snap, Vector2 Orig, float Height)> nodes, Vector2 start, Func<Vector2, Vector2, float> dist)
        {
            var remaining = new List<(Vector2 Snap, Vector2 Orig, float Height)>(nodes);
            var order = new List<(Vector2 Snap, Vector2 Orig, float Height)>(remaining.Count);
            var cur = start;
            while (remaining.Count > 0)
            {
                int bi = 0;
                float bd = float.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var d = dist(cur, remaining[i].Snap);
                    if (d < bd)
                    {
                        bd = d;
                        bi = i;
                    }
                }

                order.Add(remaining[bi]);
                cur = remaining[bi].Snap;
                remaining.RemoveAt(bi);
            }

            return order;
        }

        private static void TwoOpt(
            List<(Vector2 Snap, Vector2 Orig, float Height)> order, Vector2 start, Func<Vector2, Vector2, float> dist)
        {
            if (order.Count < 3)
            {
                return;
            }

            bool improved = true;
            int guard = 0;
            while (improved && guard++ < 64)
            {
                improved = false;
                for (int i = 0; i < order.Count - 1; i++)
                {
                    for (int k = i + 1; k < order.Count; k++)
                    {
                        var a = i == 0 ? start : order[i - 1].Snap;
                        var b = order[i].Snap;
                        var c = order[k].Snap;
                        bool hasD = k + 1 < order.Count;
                        var d = hasD ? order[k + 1].Snap : default;

                        float before = dist(a, b) + (hasD ? dist(c, d) : 0f);
                        float after = dist(a, c) + (hasD ? dist(b, d) : 0f);
                        if (after + 0.01f < before)
                        {
                            order.Reverse(i, k - i + 1);
                            improved = true;
                        }
                    }
                }
            }
        }

        private void DrawRoute(
            IContext ctx, IMapElement largeMap, Vector2 player, float playerHeight,
            float[][] heightData, SekhemaHelperSettings s, List<Leg> legs)
        {
            var baseRes = ctx.Ui.BaseResolution;
            if (baseRes.Y <= 0)
            {
                return;
            }

            double baseDiag = Math.Sqrt((baseRes.X * baseRes.X) + (baseRes.Y * baseRes.Y));
            MapProjection.DiagonalLength = baseDiag * largeMap.Size.Y / baseRes.Y;
            MapProjection.Scale = largeMap.Zoom * 0.187812f;
            var center = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
            center.X += 0.6f;
            center.Y += 0.3f;

            var fg = ImGui.GetForegroundDrawList();
            uint lineColor = ExileBridge.Draw.Color(s.CrystalRouteColor);
            uint markerColor = ExileBridge.Draw.Color(s.CrystalMarkerColor);
            uint white = ExileBridge.Draw.Color(new Vector4(1f, 1f, 1f, 1f));

            float HeightAt(Vector2 g)
            {
                int x = (int)g.X;
                int y = (int)g.Y;
                if (heightData != null && y >= 0 && y < heightData.Length && heightData[y] != null
                    && x >= 0 && x < heightData[y].Length)
                {
                    return heightData[y][x];
                }

                return playerHeight;
            }

            Vector2 Project(Vector2 grid, float height)
                => center + MapProjection.DeltaInWorldToMapDelta(grid - player, height - playerHeight);

            for (int k = 0; k < legs.Count; k++)
            {
                var leg = legs[k];
                var pts = leg.Points;
                if (pts == null || pts.Count == 0)
                {
                    continue;
                }

                var prev = Project(pts[0], HeightAt(pts[0]));
                for (int i = 1; i < pts.Count; i++)
                {
                    var curScreen = Project(pts[i], HeightAt(pts[i]));
                    fg.AddLine(prev, curScreen, lineColor, s.CrystalRouteThickness);
                    prev = curScreen;
                }

                var dest = Project(leg.Dest, leg.DestHeight);
                fg.AddCircleFilled(dest, s.CrystalMarkerRadius, markerColor);
                fg.AddCircle(dest, s.CrystalMarkerRadius, lineColor, 0, 2f);

                var num = (k + 1).ToString();
                var ts = ImGui.CalcTextSize(num);
                fg.AddText(dest - (ts * 0.5f), white, num);
            }
        }

        private static float PathLength(List<Vector2> p)
        {
            float len = 0f;
            for (int i = 1; i < p.Count; i++)
            {
                len += Vector2.Distance(p[i - 1], p[i]);
            }

            return len;
        }

        // Returns the path oriented so its first point is the one nearest `start`.
        private static List<Vector2> Oriented(List<Vector2> path, Vector2 start)
        {
            if (path == null || path.Count < 2)
            {
                return path;
            }

            if (Vector2.DistanceSquared(path[0], start) <= Vector2.DistanceSquared(path[^1], start))
            {
                return path;
            }

            var rev = new List<Vector2>(path);
            rev.Reverse();
            return rev;
        }

        // Unordered 64-bit key from two grid points (smaller-first), so a<->b shares one cache slot.
        private static long Key(Vector2 a, Vector2 b)
        {
            uint pa = Pack(a);
            uint pb = Pack(b);
            (uint lo, uint hi) = pa <= pb ? (pa, pb) : (pb, pa);
            return ((long)hi << 32) | lo;
        }

        private static uint Pack(Vector2 v)
        {
            int x = (int)MathF.Round(v.X) + 8192;
            int y = (int)MathF.Round(v.Y) + 8192;
            return ((uint)(x & 0xFFFF) << 16) | (uint)(y & 0xFFFF);
        }
    }
}
