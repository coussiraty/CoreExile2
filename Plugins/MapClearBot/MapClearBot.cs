// <copyright file="MapClearBot.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace MapClearBot
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Autonomous map-clearing bot built on the ExileBridge SDK. Per frame it runs
    ///     a priority state machine — flee, combat, loot, explore (frontier search +
    ///     A* over the walkable grid), then optionally head to an area transition.
    ///     Movement follows a real path (no wall-clipping) with stuck detection. All
    ///     work is bounded/throttled and input runs on the shared background worker.
    /// </summary>
    public sealed class MapClearBot : Plugin<MapClearBotSettings>
    {
        private readonly Pathfinder pf = new();
        private readonly HashSet<(int, int)> explored = new();
        private readonly MovementController mover = new();
        private Vector2 playerScreenRel;
        private float playerHeight;
        private string captureToken = string.Empty;

        private bool wasToggleDown;
        private DateTime lastAction = DateTime.MinValue;
        private string state = "idle";

        private (int X, int Y) lastProgressPos;
        private DateTime lastProgressTime = DateTime.MinValue;

        private List<(int X, int Y)>? path;
        private (int X, int Y) pathGoal;
        private DateTime pathAt = DateTime.MinValue;
        private float conv = 1f;

        private IDisposable? areaToken;

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonConvert.DeserializeObject<MapClearBotSettings>(json) ?? new MapClearBotSettings();
            }

            this.areaToken = this.Ctx.Events.OnAreaChange(this.ResetRun);
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.mover.Stop();
            this.areaToken?.Dispose();
            this.areaToken = null;
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            try
            {
                this.Step();
            }
            catch
            {
                // Never leave a movement key held if a frame throws.
                this.mover.Stop();
                throw;
            }
        }

        private void Step()
        {
            this.HandleToggle();

            if (!this.Settings.Enabled || !this.Ctx.Game.IsInGame || !this.Ctx.Game.IsForeground)
            {
                this.mover.Stop();
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                this.mover.Stop();
                return;
            }

            var ig = this.Ctx.Game.InGame;
            var self = ig.Player;
            if (self == null || !self.TryGetComponent<IRender>(out var selfRender))
            {
                this.mover.Stop();
                return;
            }

            var terrain = ig.Terrain;
            this.pf.Bind(terrain);
            this.conv = terrain.WorldToGridConvertor <= 0 ? 1f : terrain.WorldToGridConvertor;
            this.mover.Configure(this.Settings.MoveUpKey, this.Settings.MoveDownKey, this.Settings.MoveLeftKey, this.Settings.MoveRightKey);

            var pw0 = selfRender.WorldPosition;
            this.playerHeight = pw0.Z;
            this.playerScreenRel = this.Ctx.Render.WorldToScreen(pw0, pw0.Z);

            var playerGrid = selfRender.GridPosition;
            var pg = ((int)playerGrid.X, (int)playerGrid.Y);

            this.MarkExplored(pg);
            this.UpdateProgress(playerGrid);

            if (this.Settings.ShowPath)
            {
                this.DrawPath();
            }

            // Discrete actions (attacks, clicks, mouse-move steps) are rate-limited.
            // WASD movement is a held-key state that must update EVERY frame, so it is
            // not gated here — only mouse mode (click-to-move) returns early.
            var actionReady = (DateTime.Now - this.lastAction).TotalMilliseconds >= this.Settings.ActionDelayMs;
            if (this.Settings.Movement == MovementMode.MouseClick && !actionReady)
            {
                return;
            }

            var awake = this.Ctx.Entities.Awake;

            // 1) Flee on low life.
            if (this.TryFlee(self, selfRender, awake, playerGrid))
            {
                return;
            }

            // 2) Combat.
            var monster = this.BestMonster(awake, playerGrid, pg);
            if (monster != null)
            {
                this.mover.Stop(); // stand still to attack (released every frame)
                if (actionReady)
                {
                    if (this.Settings.AimMouseOnAttack || this.Settings.Movement == MovementMode.MouseClick)
                    {
                        this.AimAt(monster);
                    }

                    this.Attack();
                    this.path = null; // re-path after the fight
                    this.Act("combat");
                }
                else
                {
                    this.state = "combat";
                }

                return;
            }

            // 3) Loot.
            if (this.Settings.PickItems)
            {
                var item = this.NearestItem(awake, playerGrid);
                if (item != null)
                {
                    this.mover.Stop();
                    if (actionReady)
                    {
                        this.AimAt(item); // pickup needs a click on the item
                        Input.Click(Input.MouseButton.Left);
                        this.Act("loot");
                    }
                    else
                    {
                        this.state = "loot";
                    }

                    return;
                }
            }

            // 4) Explore (and 5) transition when cleared).
            this.ExploreStep(pg);
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            if (ImGui.Checkbox("Enabled", ref this.Settings.Enabled))
            {
                this.SaveSettings();
            }

            ImGui.SameLine();
            ImGui.Text($"Toggle: {Input.KeyName(this.Settings.ToggleKey)}   State: {this.state}");
            ImGui.Text($"Explored cells: {this.explored.Count}   Path: {(this.path?.Count ?? 0)}");
            ImGui.Separator();

            ImGui.Checkbox("Attack with left click", ref this.Settings.AttackWithLeftClick);
            if (!this.Settings.AttackWithLeftClick)
            {
                ImGui.SameLine();
                ImGui.Text($"Attack key: {Input.KeyName(this.Settings.AttackKey)}");
            }

            var mode = this.Settings.Movement;
            if (Draw.IEnumerableComboBox("Movement mode", new[] { MovementMode.MouseClick, MovementMode.Wasd }, ref mode))
            {
                this.Settings.Movement = mode;
                this.mover.Stop();
                this.SaveSettings();
            }

            if (this.Settings.Movement == MovementMode.Wasd)
            {
                ImGui.TextWrapped("Requires WASD movement enabled & bound in Path of Exile 2. The mouse stays free.");
                this.KeyBind("Up", ref this.Settings.MoveUpKey, "mu");
                ImGui.SameLine();
                this.KeyBind("Left", ref this.Settings.MoveLeftKey, "ml");
                ImGui.SameLine();
                this.KeyBind("Down", ref this.Settings.MoveDownKey, "mdn");
                ImGui.SameLine();
                this.KeyBind("Right", ref this.Settings.MoveRightKey, "mr");
                ImGui.SliderInt("Arrival deadzone (px)", ref this.Settings.MoveDeadzonePx, 6, 80);
                ImGui.Checkbox("Aim mouse on attack", ref this.Settings.AimMouseOnAttack);
                Draw.ToolTip("Off = never touch the mouse (melee/self-cast). On = point at the target for aimed skills.");
            }
            else
            {
                ImGui.Checkbox("Move with left click", ref this.Settings.MoveWithLeftClick);
                if (!this.Settings.MoveWithLeftClick)
                {
                    ImGui.SameLine();
                    ImGui.Text($"Move key: {Input.KeyName(this.Settings.MoveKey)}");
                }
            }

            ImGui.SliderFloat("Combat range", ref this.Settings.CombatRange, 10f, 150f);
            ImGui.SliderFloat("Loot range", ref this.Settings.LootRange, 10f, 150f);
            ImGui.Checkbox("Pick items", ref this.Settings.PickItems);
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("Loot filter (path contains)", ref this.Settings.LootFilter, 64);
            ImGui.Checkbox("Use line of sight", ref this.Settings.UseLineOfSight);
            ImGui.SliderInt("Flee life % (0 = off)", ref this.Settings.FleeLifePercent, 0, 90);
            ImGui.Separator();
            ImGui.SliderInt("Action delay (ms)", ref this.Settings.ActionDelayMs, 50, 1000);
            ImGui.SliderInt("Lookahead tiles", ref this.Settings.LookaheadTiles, 4, 40);
            ImGui.SliderInt("Path recompute (ms)", ref this.Settings.PathRecomputeMs, 100, 2000);
            ImGui.SliderFloat("Stuck seconds", ref this.Settings.StuckSeconds, 0.5f, 8f);
            ImGui.SliderInt("Explore cell size", ref this.Settings.ExploreCellSize, 4, 40);
            ImGui.SliderInt("Max path nodes", ref this.Settings.MaxPathNodes, 2000, 80000);
            ImGui.Checkbox("Go to transition when cleared", ref this.Settings.GoToTransitionWhenCleared);
            ImGui.Checkbox("Draw path", ref this.Settings.ShowPath);

            if (ImGui.Button("Save"))
            {
                this.SaveSettings();
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset exploration"))
            {
                this.ResetRun();
            }
        }

        // ====================================================================
        //  State machine steps
        // ====================================================================
        private bool TryFlee(IEntity self, IRender selfRender, IReadOnlyCollection<IEntity> awake, Vector2 playerGrid)
        {
            if (this.Settings.FleeLifePercent <= 0 ||
                !self.TryGetComponent<ILife>(out var life) ||
                life.Health.Total <= 0 ||
                life.Health.CurrentInPercent > this.Settings.FleeLifePercent)
            {
                return false;
            }

            IEntity? threat = null;
            var best = float.MaxValue;
            var dangerRange = this.Settings.CombatRange * 1.5f;
            foreach (var e in awake)
            {
                if (!IsValidMonster(e) || !e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var d = Vector2.Distance(playerGrid, r.GridPosition);
                if (d < best && d <= dangerRange)
                {
                    best = d;
                    threat = e;
                }
            }

            if (threat == null || !threat.TryGetComponent<IRender>(out var tr))
            {
                return false;
            }

            // Run directly away from the nearest threat.
            var pw = selfRender.WorldPosition;
            var tw = tr.WorldPosition;
            var away = new Vector2(pw.X - tw.X, pw.Y - tw.Y);
            if (away.LengthSquared() < 0.001f)
            {
                away = new Vector2(1, 0);
            }

            away = Vector2.Normalize(away) * (this.Settings.CombatRange * this.conv);
            var target = new Vector3(pw.X + away.X, pw.Y + away.Y, pw.Z);
            this.Travel(target);

            this.path = null;
            this.Moved("flee");
            return true;
        }

        private IEntity? BestMonster(IReadOnlyCollection<IEntity> awake, Vector2 playerGrid, (int X, int Y) pg)
        {
            IEntity? best = null;
            var bestScore = float.MinValue;
            foreach (var e in awake)
            {
                if (!IsValidMonster(e) || !e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var dist = Vector2.Distance(playerGrid, r.GridPosition);
                if (dist > this.Settings.CombatRange)
                {
                    continue;
                }

                if (this.Settings.UseLineOfSight &&
                    !this.pf.HasLineOfSight(pg.X, pg.Y, (int)r.GridPosition.X, (int)r.GridPosition.Y))
                {
                    continue;
                }

                var rarity = e.TryGetComponent<IObjectMagicProperties>(out var omp) ? (int)omp.Rarity : 0;
                var score = (rarity * 1000f) - dist; // higher rarity first, then nearer
                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            return best;
        }

        private IEntity? NearestItem(IReadOnlyCollection<IEntity> awake, Vector2 playerGrid)
        {
            IEntity? best = null;
            var bestDist = float.MaxValue;
            var filter = this.Settings.LootFilter;
            foreach (var e in awake)
            {
                if (!e.IsValid || e.Type != EntityType.Item)
                {
                    continue;
                }

                if (e.TryGetComponent<ITargetable>(out var t) && !t.IsTargetable)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(filter) &&
                    (e.Path == null || e.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                if (!e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var dist = Vector2.Distance(playerGrid, r.GridPosition);
                if (dist <= this.Settings.LootRange && dist < bestDist)
                {
                    bestDist = dist;
                    best = e;
                }
            }

            return best;
        }

        private void ExploreStep((int X, int Y) pg)
        {
            var now = DateTime.Now;
            var stuck = (now - this.lastProgressTime).TotalSeconds > this.Settings.StuckSeconds;
            if (stuck)
            {
                // Give up on the current frontier and skip a block around it.
                this.MarkBlockExplored(this.pathGoal);
                this.path = null;
                this.lastProgressTime = now;
            }

            var needPath = this.path == null || this.path.Count < 2 ||
                           (now - this.pathAt).TotalMilliseconds > this.Settings.PathRecomputeMs;

            if (needPath)
            {
                if (this.pf.TryNearestFrontier(pg, this.explored, this.Settings.ExploreCellSize, this.Settings.MaxPathNodes, out var frontier))
                {
                    var p = this.pf.FindPath(pg, frontier, this.Settings.MaxPathNodes);
                    if (p != null && p.Count >= 2)
                    {
                        this.path = p;
                        this.pathGoal = frontier;
                        this.pathAt = now;
                    }
                    else
                    {
                        this.MarkBlockExplored(frontier); // unreachable; skip it
                        this.path = null;
                    }
                }
                else if (this.Settings.GoToTransitionWhenCleared && this.TryPathToTransition(pg, now))
                {
                    // path set to nearest transition tile
                }
                else
                {
                    this.mover.Stop();
                    this.state = "cleared";
                    return;
                }
            }

            if (this.path != null && this.path.Count >= 2)
            {
                this.MoveAlongPath(pg);
                this.Moved("explore");
            }
            else
            {
                this.mover.Stop();
            }
        }

        private bool TryPathToTransition((int X, int Y) pg, DateTime now)
        {
            var tiles = this.Ctx.Game.InGame.Terrain.TgtTiles;
            (int X, int Y)? nearest = null;
            var bestDist = float.MaxValue;
            var scanned = 0;
            foreach (var kv in tiles)
            {
                foreach (var tile in kv.Value)
                {
                    if (++scanned > 4000)
                    {
                        break;
                    }

                    var d = MathF.Abs(tile.X - pg.X) + MathF.Abs(tile.Y - pg.Y);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        nearest = ((int)tile.X, (int)tile.Y);
                    }
                }
            }

            if (nearest == null)
            {
                return false;
            }

            var p = this.pf.FindPath(pg, nearest.Value, this.Settings.MaxPathNodes);
            if (p == null || p.Count < 2)
            {
                return false;
            }

            this.path = p;
            this.pathGoal = nearest.Value;
            this.pathAt = now;
            return true;
        }

        // ====================================================================
        //  Movement / input
        // ====================================================================
        private void MoveAlongPath((int X, int Y) pg)
        {
            if (this.path == null || this.path.Count == 0)
            {
                return;
            }

            var lookahead = this.Settings.LookaheadTiles;

            if (this.Settings.Movement == MovementMode.Wasd)
            {
                // Direction only: the first path point ~lookahead tiles ahead
                // (works even when that point is off-screen).
                var wp = this.path[this.path.Count - 1];
                foreach (var pt in this.path)
                {
                    if (Math.Abs(pt.X - pg.X) + Math.Abs(pt.Y - pg.Y) >= lookahead)
                    {
                        wp = pt;
                        break;
                    }
                }

                // Project the waypoint at the player's height so the W/S (vertical)
                // component matches playerScreenRel (also projected at playerHeight),
                // avoiding a height-induced direction bias on sloped terrain.
                var world = new Vector3(wp.X * this.conv, wp.Y * this.conv, this.playerHeight);
                var screen = this.Ctx.Render.WorldToScreen(world, this.playerHeight);
                this.mover.MoveToward(this.playerScreenRel, screen, this.Settings.MoveDeadzonePx);
                return;
            }

            // Mouse mode: click the farthest on-screen point within lookahead.
            (int X, int Y)? chosen = null;
            (int X, int Y)? firstOnScreen = null;
            foreach (var pt in this.path)
            {
                var d = Math.Abs(pt.X - pg.X) + Math.Abs(pt.Y - pg.Y);
                if (!this.GridToScreen(pt, out _))
                {
                    continue;
                }

                firstOnScreen ??= pt;
                if (d <= lookahead)
                {
                    chosen = pt; // keep the farthest within lookahead
                }
            }

            var target = chosen ?? firstOnScreen;
            if (target == null)
            {
                return;
            }

            if (this.GridToScreen(target.Value, out var abs))
            {
                Input.MoveMouse((int)abs.X, (int)abs.Y);
                this.Move();
            }
        }

        // Moves toward a single world point, dispatching on the movement mode.
        private void Travel(Vector3 world)
        {
            var screen = this.Ctx.Render.WorldToScreen(world, world.Z);
            if (this.Settings.Movement == MovementMode.Wasd)
            {
                this.mover.MoveToward(this.playerScreenRel, screen, this.Settings.MoveDeadzonePx);
                return;
            }

            var window = this.Ctx.Game.WindowArea;
            if (screen.X < 0 || screen.Y < 0 || screen.X > window.Width || screen.Y > window.Height)
            {
                return;
            }

            Input.MoveMouse((int)(window.X + screen.X), (int)(window.Y + screen.Y));
            this.Move();
        }

        private void Move()
        {
            if (this.Settings.MoveWithLeftClick)
            {
                Input.Click(Input.MouseButton.Left);
            }
            else if (this.Settings.MoveKey != 0)
            {
                Input.PressKey(this.Settings.MoveKey);
            }
        }

        private void Attack()
        {
            if (this.Settings.AttackWithLeftClick)
            {
                Input.Click(Input.MouseButton.Left);
            }
            else if (this.Settings.AttackKey != 0)
            {
                Input.PressKey(this.Settings.AttackKey);
            }
        }

        private void AimAt(IEntity entity)
        {
            if (entity.TryGetComponent<IRender>(out var r))
            {
                this.PointAt(r.WorldPosition);
            }
        }

        private bool PointAt(Vector3 world)
        {
            var screen = this.Ctx.Render.WorldToScreen(world, world.Z);
            var window = this.Ctx.Game.WindowArea;
            if (screen.X < 0 || screen.Y < 0 || screen.X > window.Width || screen.Y > window.Height)
            {
                return false;
            }

            Input.MoveMouse((int)(window.X + screen.X), (int)(window.Y + screen.Y));
            return true;
        }

        // Converts a grid cell to an absolute desktop point, returning false if off-screen.
        private bool GridToScreen((int X, int Y) cell, out Vector2 abs)
        {
            var world = new Vector3(cell.X * this.conv, cell.Y * this.conv, 0f);
            var screen = this.Ctx.Render.WorldToScreen(world, 0f);
            var window = this.Ctx.Game.WindowArea;
            abs = new Vector2(window.X + screen.X, window.Y + screen.Y);
            return screen.X >= 0 && screen.Y >= 0 && screen.X <= window.Width && screen.Y <= window.Height;
        }

        // ====================================================================
        //  Bookkeeping
        // ====================================================================
        private void HandleToggle()
        {
            var down = this.Settings.ToggleKey != 0 && Input.IsKeyDown(this.Settings.ToggleKey);
            if (down && !this.wasToggleDown)
            {
                this.Settings.Enabled = !this.Settings.Enabled;
                if (!this.Settings.Enabled)
                {
                    this.path = null;
                    this.mover.Stop();
                }
            }

            this.wasToggleDown = down;
        }

        private void Act(string newState)
        {
            this.lastAction = DateTime.Now;
            this.state = newState;
        }

        // Movement bookkeeping. Only mouse-mode movement is a discrete (click)
        // action that should reset the throttle; WASD movement is a held-key state
        // updated every frame, so it must NOT keep resetting lastAction (that would
        // starve the discrete combat/loot actions).
        private void Moved(string newState)
        {
            this.state = newState;
            if (this.Settings.Movement == MovementMode.MouseClick)
            {
                this.lastAction = DateTime.Now;
            }
        }

        private void KeyBind(string label, ref int key, string token)
        {
            var capturing = this.captureToken == token;
            var text = capturing ? "press..." : $"{label}: {Input.KeyName(key)}";
            if (ImGui.Button($"{text}##{token}", new Vector2(96, 0)))
            {
                this.captureToken = capturing ? string.Empty : token;
            }

            if (!capturing)
            {
                return;
            }

            if (Input.TryCaptureKey(out var vk))
            {
                key = vk;
                this.captureToken = string.Empty;
                this.SaveSettings();
            }
            else if (Input.IsKeyDown(Input.VkEscape))
            {
                this.captureToken = string.Empty;
            }
        }

        private void MarkExplored((int X, int Y) pg)
        {
            var size = Math.Max(1, this.Settings.ExploreCellSize);
            this.explored.Add((pg.X / size, pg.Y / size));
        }

        private void MarkBlockExplored((int X, int Y) cell)
        {
            var size = Math.Max(1, this.Settings.ExploreCellSize);
            var cx = cell.X / size;
            var cy = cell.Y / size;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    this.explored.Add((cx + dx, cy + dy));
                }
            }
        }

        private void UpdateProgress(Vector2 playerGrid)
        {
            var pg = ((int)playerGrid.X, (int)playerGrid.Y);
            if (this.lastProgressTime == DateTime.MinValue ||
                Math.Abs(pg.Item1 - this.lastProgressPos.X) + Math.Abs(pg.Item2 - this.lastProgressPos.Y) > 3)
            {
                this.lastProgressPos = pg;
                this.lastProgressTime = DateTime.Now;
            }
        }

        private void ResetRun()
        {
            this.explored.Clear();
            this.path = null;
            this.lastProgressTime = DateTime.MinValue;
            this.state = "idle";
        }

        private void DrawPath()
        {
            if (this.path == null || this.path.Count < 2)
            {
                return;
            }

            var dl = ImGui.GetBackgroundDrawList();
            var window = this.Ctx.Game.WindowArea;
            var line = Draw.Color(new Vector4(0.2f, 0.9f, 1f, 0.8f));
            Vector2? prev = null;
            foreach (var pt in this.path)
            {
                var world = new Vector3(pt.X * this.conv, pt.Y * this.conv, 0f);
                var s = this.Ctx.Render.WorldToScreen(world, 0f);
                var p = new Vector2(window.X + s.X, window.Y + s.Y);
                if (prev.HasValue)
                {
                    dl.AddLine(prev.Value, p, line, 2f);
                }

                prev = p;
            }

            var goalWorld = new Vector3(this.pathGoal.X * this.conv, this.pathGoal.Y * this.conv, 0f);
            var gs = this.Ctx.Render.WorldToScreen(goalWorld, 0f);
            dl.AddCircleFilled(new Vector2(window.X + gs.X, window.Y + gs.Y), 6f, Draw.Color(new Vector4(1f, 0.5f, 0f, 1f)));
        }

        private static bool IsValidMonster(IEntity e)
        {
            if (!e.IsValid || e.Type != EntityType.Monster ||
                e.State == EntityState.MonsterFriendly || e.State == EntityState.Useless)
            {
                return false;
            }

            if (e.TryGetComponent<ITargetable>(out var t) && !t.IsTargetable)
            {
                return false;
            }

            if (e.TryGetComponent<ILife>(out var l) && !l.IsAlive)
            {
                return false;
            }

            return true;
        }
    }
}
