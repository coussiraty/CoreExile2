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
        private HashSet<(int, int)>? walkableCoarse;
        private int totalWalkable;
        private int exploredWalkable;
        private bool areaComputed;
        private readonly MovementController mover = new();
        private Vector2 playerScreenRel;
        private float playerHeight;
        private string captureToken = string.Empty;

        private bool wasToggleDown;
        private int rotationIndex;
        private DateTime lastAction = DateTime.MinValue;
        private DateTime lastLog = DateTime.MinValue;
        private string state = "idle";
        private string combatDebug = string.Empty;

        private (int X, int Y) lastProgressPos;
        private DateTime lastProgressTime = DateTime.MinValue;

        private List<(int X, int Y)>? path;
        private (int X, int Y) pathGoal;
        private DateTime pathAt = DateTime.MinValue;
        private bool offPath;
        private bool recovering;
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

            // Seed the skill rotation from the legacy single attack key.
            if (this.Settings.SkillKeys.Count == 0 && this.Settings.AttackKey != 0)
            {
                this.Settings.SkillKeys.Add(this.Settings.AttackKey);
            }

            Diagnostics.Clear();
            var skillNames = string.Join(",", this.Settings.SkillKeys.ConvertAll(Input.KeyName));
            Diagnostics.Log("MapClearBot", $"OnEnable: movement={this.Settings.Movement} leftClickAtk={this.Settings.AttackWithLeftClick} skills=[{skillNames}] combatRange={this.Settings.CombatRange:F0} flee%={this.Settings.FleeLifePercent} actionDelay={this.Settings.ActionDelayMs} log={Diagnostics.FilePath}");

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

            // Measure the area's walkable extent once (for the completion %).
            if (!this.areaComputed && this.pf.Ready)
            {
                this.ComputeWalkableCoarse();
                this.areaComputed = true;
            }

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

            // Diagnostic HUD (drawn even without window focus while Draw path is on).
            if (this.Settings.ShowPath)
            {
                int total = 0, monsters = 0, valid = 0, notTgt = 0, dead = 0, frUse = 0;
                var nd = -1f;
                var byType = new Dictionary<EntityType, int>();
                foreach (var e in awake)
                {
                    if (!e.IsValid)
                    {
                        continue;
                    }

                    total++;
                    byType.TryGetValue(e.Type, out var c);
                    byType[e.Type] = c + 1;

                    if (e.Type != EntityType.Monster)
                    {
                        continue;
                    }

                    monsters++;
                    if (e.State == EntityState.MonsterFriendly || e.State == EntityState.Useless)
                    {
                        frUse++;
                        continue;
                    }

                    if (e.TryGetComponent<ITargetable>(out var t) && t.IsHidden)
                    {
                        notTgt++;
                        continue;
                    }

                    if (e.TryGetComponent<ILife>(out var l) && !l.IsAlive)
                    {
                        dead++;
                        continue;
                    }

                    valid++;
                    if (e.TryGetComponent<IRender>(out var r))
                    {
                        var d = Vector2.Distance(playerGrid, r.GridPosition);
                        if (nd < 0 || d < nd)
                        {
                            nd = d;
                        }
                    }
                }

                var types = string.Empty;
                if (monsters == 0)
                {
                    foreach (var kv in byType)
                    {
                        types += $" {kv.Key}:{kv.Value}";
                    }
                }

                var lifePct = self.TryGetComponent<ILife>(out var lf) ? lf.Health.CurrentInPercent : -1;
                this.combatDebug =
                    $"map:{this.MapPercent}%  awake:{total} mon:{monsters} valid:{valid}(notTgt:{notTgt} dead:{dead} fr/use:{frUse}) " +
                    $"nearest:{(nd < 0 ? "none" : nd.ToString("F0"))} range:{this.Settings.CombatRange:F0} " +
                    $"state:{this.state} life%:{lifePct} q:{Input.Pending}{(types.Length > 0 ? " | types:" + types : string.Empty)}";
                var dl = ImGui.GetBackgroundDrawList();
                dl.AddText(new Vector2(20, 140), Draw.Color(new Vector4(1f, 1f, 0f, 1f)), this.combatDebug);
                dl.AddText(new Vector2(20, 158), Draw.Color(new Vector4(0.3f, 1f, 0.4f, 1f)), $"Map cleared: {this.MapPercent}%");

                if ((DateTime.Now - this.lastLog).TotalMilliseconds > 500)
                {
                    Diagnostics.Log("MapClearBot", this.combatDebug);
                    this.lastLog = DateTime.Now;
                }
            }

            // 1) Flee on low life.
            if (this.TryFlee(self, selfRender, awake, playerGrid))
            {
                return;
            }

            // 2) Combat. Attack the best in-range monster: prefer one with line of
            //    sight, but otherwise just hit the NEAREST one so bosses/packs that
            //    sit on awkward (non-walkable) cells still get attacked. Standing on
            //    any in-range monster also prevents combat<->walk flicker.
            var target = this.BestMonster(awake, playerGrid, pg)
                         ?? this.NearestMonsterInRange(awake, playerGrid, this.Settings.CombatRange);
            if (target != null)
            {
                this.mover.Stop();
                if (actionReady)
                {
                    if (this.Settings.AimMouseOnAttack || this.Settings.Movement == MovementMode.MouseClick)
                    {
                        this.AimAt(target);
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
            this.KeyBind("Toggle", ref this.Settings.ToggleKey, "tg");
            ImGui.SameLine();
            ImGui.Text($"State: {this.state}");
            ImGui.Text($"Map cleared: {this.MapPercent}%  ({this.exploredWalkable}/{this.totalWalkable})   Path: {(this.path?.Count ?? 0)}");
            ImGui.Separator();

            ImGui.Checkbox("Attack with left click", ref this.Settings.AttackWithLeftClick);
            if (!this.Settings.AttackWithLeftClick)
            {
                this.DrawSkillKeys();
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
                    this.KeyBind("Move", ref this.Settings.MoveKey, "mvk");
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

        private IEntity? NearestMonsterInRange(IReadOnlyCollection<IEntity> awake, Vector2 playerGrid, float range)
        {
            IEntity? best = null;
            var bestDist = float.MaxValue;
            foreach (var e in awake)
            {
                if (!IsValidMonster(e) || !e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var d = Vector2.Distance(playerGrid, r.GridPosition);
                if (d <= range && d < bestDist)
                {
                    bestDist = d;
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

            // Two-stage stuck handling. First, if we stop making progress, enter
            // "recovery" (hug the path cell-by-cell) to push through tight corridors
            // instead of immediately bailing. Only if we stay stuck well past that
            // do we abandon the frontier and re-path elsewhere.
            var noProgress = (now - this.lastProgressTime).TotalSeconds;
            this.recovering = noProgress > this.Settings.StuckSeconds;
            if (noProgress > this.Settings.StuckSeconds * 2.5)
            {
                this.MarkBlockExplored(this.pathGoal);
                this.path = null;
                this.recovering = false;
                this.lastProgressTime = now;
            }

            // Recompute only when needed (NOT on a fixed timer) so movement stays
            // smooth: when there is no path, when we've reached the frontier, when
            // we've strayed off it (no LoS to any point), when stuck, or as a rare
            // safety refresh. This avoids the periodic "snap" that caused stutter.
            var reachedGoal = this.path != null &&
                Math.Abs(pg.X - this.pathGoal.X) + Math.Abs(pg.Y - this.pathGoal.Y) <= 4;
            var safety = (now - this.pathAt).TotalMilliseconds > Math.Max(1500, this.Settings.PathRecomputeMs);
            var needPath = this.path == null || this.path.Count < 2 || reachedGoal || this.offPath || safety;

            if (needPath)
            {
                if (this.pf.TryNearestFrontier(pg, this.explored, this.Settings.ExploreCellSize, this.Settings.MaxPathNodes, out var frontier))
                {
                    var p = this.pf.FindPath(pg, frontier, this.Settings.MaxPathNodes);
                    if (p != null && p.Count >= 2)
                    {
                        this.path = this.pf.SimplifyLos(p);
                        this.pathGoal = frontier;
                        this.pathAt = now;
                        this.offPath = false;
                    }
                    else
                    {
                        this.MarkBlockExplored(frontier); // unreachable; skip it
                        this.path = null;
                    }
                }
                else if (this.Settings.GoToTransitionWhenCleared && this.TryPathToTransition(pg, now))
                {
                    this.path = this.pf.SimplifyLos(this.path);
                    this.offPath = false;
                }
                else
                {
                    this.mover.Stop();
                    this.state = "cleared";
                    return;
                }
            }

            if (this.path != null && this.path.Count >= 1)
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

            // Normally funnel toward the farthest visible point (smooth straight
            // runs). When recovering from a stuck, hug the path cell-by-cell so the
            // 8-way heading matches a tight corridor.
            var target = this.recovering ? this.NearWaypoint(pg) : this.FunnelWaypoint(pg);
            if (target == null)
            {
                // Strayed off the path (nothing visible): trigger a re-path.
                this.offPath = true;
                return;
            }

            this.offPath = false;

            if (this.Settings.Movement == MovementMode.Wasd)
            {
                // Project the waypoint at the player's height so the W/S (vertical)
                // component matches playerScreenRel, avoiding a height-induced
                // direction bias on sloped terrain.
                var world = new Vector3(target.Value.X * this.conv, target.Value.Y * this.conv, this.playerHeight);
                var screen = this.Ctx.Render.WorldToScreen(world, this.playerHeight);
                this.mover.MoveToward(this.playerScreenRel, screen, this.Settings.MoveDeadzonePx);
            }
            else if (this.GridToScreen(target.Value, out var abs))
            {
                Input.MoveMouse((int)abs.X, (int)abs.Y);
                this.Move();
            }
        }

        // Farthest path point still in line of sight (smooth steering).
        private (int X, int Y)? FunnelWaypoint((int X, int Y) pg)
        {
            for (var i = this.path!.Count - 1; i >= 0; i--)
            {
                var pt = this.path[i];
                if (!this.pf.HasLineOfSight(pg.X, pg.Y, pt.X, pt.Y))
                {
                    continue;
                }

                if (this.Settings.Movement == MovementMode.Wasd || this.GridToScreen(pt, out _))
                {
                    return pt;
                }
            }

            return null;
        }

        // Closest path point a few tiles ahead (tight-corridor hugging).
        private (int X, int Y)? NearWaypoint((int X, int Y) pg)
        {
            foreach (var pt in this.path!)
            {
                if (Math.Abs(pt.X - pg.X) + Math.Abs(pt.Y - pg.Y) >= 3)
                {
                    return pt;
                }
            }

            return this.path![this.path.Count - 1];
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
            if (!this.Settings.AttackWithLeftClick)
            {
                var keys = this.Settings.SkillKeys;
                if (keys != null && keys.Count > 0)
                {
                    var vk = keys[this.rotationIndex % keys.Count];
                    this.rotationIndex = (this.rotationIndex + 1) % keys.Count;
                    if (vk != 0)
                    {
                        Input.PressKey(vk);
                        Diagnostics.Log("MapClearBot", $"attack -> key {Input.KeyName(vk)} (aimMouse={this.Settings.AimMouseOnAttack})");
                    }

                    return;
                }
            }

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

        private void DrawSkillKeys()
        {
            var keys = this.Settings.SkillKeys;
            ImGui.Text("Skill keys (pressed in turn during combat):");
            int? removeAt = null;
            for (var i = 0; i < keys.Count; i++)
            {
                var token = $"sk{i}";
                var capturing = this.captureToken == token;
                var label = capturing ? "press..." : $"{i + 1}: {Input.KeyName(keys[i])}";
                if (ImGui.Button($"{label}##{token}", new Vector2(90, 0)))
                {
                    this.captureToken = capturing ? string.Empty : token;
                }

                if (capturing)
                {
                    if (Input.TryCaptureKey(out var vk))
                    {
                        keys[i] = vk;
                        this.captureToken = string.Empty;
                        this.SaveSettings();
                    }
                    else if (Input.IsKeyDown(Input.VkEscape))
                    {
                        this.captureToken = string.Empty;
                    }
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"remove##rm{i}"))
                {
                    removeAt = i;
                }
            }

            if (removeAt.HasValue)
            {
                keys.RemoveAt(removeAt.Value);
                this.SaveSettings();
            }

            if (ImGui.Button("Add skill"))
            {
                keys.Add(70); // default F
                this.SaveSettings();
            }

            Draw.ToolTip("Add a skill key (default F). The bot cycles through these one per attack tick.");
        }

        private void MarkExplored((int X, int Y) pg)
        {
            // Mark a BLOB of coarse cells around the player (everything within the
            // combat/clear radius is "seen"), not just the current cell. This keeps
            // the nearest unexplored frontier consistently AHEAD of the player, so
            // the heading stays stable instead of flipping to a just-passed cell.
            var size = Math.Max(1, this.Settings.ExploreCellSize);
            var r = Math.Max(1, (int)(this.Settings.CombatRange / size));
            var cx = pg.X / size;
            var cy = pg.Y / size;
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if ((dx * dx) + (dy * dy) > r * r)
                    {
                        continue;
                    }

                    var cell = (cx + dx, cy + dy);
                    if (this.explored.Add(cell) && this.walkableCoarse != null && this.walkableCoarse.Contains(cell))
                    {
                        this.exploredWalkable++;
                    }
                }
            }
        }

        // Samples the walkable grid at coarse-cell resolution to estimate how much
        // of the area is walkable, for the map-completion percentage.
        private void ComputeWalkableCoarse()
        {
            var size = Math.Max(1, this.Settings.ExploreCellSize);
            var cols = this.pf.Width / size;
            var rows = this.pf.Rows / size;
            var set = new HashSet<(int, int)>();
            for (var cy = 0; cy <= rows; cy++)
            {
                for (var cx = 0; cx <= cols; cx++)
                {
                    var bx = cx * size;
                    var by = cy * size;
                    if (this.pf.IsWalkable(bx + (size / 2), by + (size / 2)) ||
                        this.pf.IsWalkable(bx + (size / 4), by + (size / 4)) ||
                        this.pf.IsWalkable(bx + (3 * size / 4), by + (3 * size / 4)))
                    {
                        set.Add((cx, cy));
                    }
                }
            }

            this.walkableCoarse = set;
            this.totalWalkable = set.Count;
            this.exploredWalkable = 0;
            foreach (var c in this.explored)
            {
                if (set.Contains(c))
                {
                    this.exploredWalkable++;
                }
            }
        }

        private int MapPercent => this.totalWalkable > 0
            ? (int)(100L * this.exploredWalkable / this.totalWalkable)
            : 0;

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
            this.areaComputed = false;
            this.walkableCoarse = null;
            this.totalWalkable = 0;
            this.exploredWalkable = 0;
        }

        private void DrawPath()
        {
            if (this.path == null || this.path.Count < 2)
            {
                return;
            }

            var dl = ImGui.GetBackgroundDrawList();
            var line = Draw.Color(new Vector4(0.2f, 0.9f, 1f, 0.8f));
            Vector2? prev = null;
            foreach (var pt in this.path)
            {
                // WorldToScreen is already overlay-relative; do NOT add the window
                // offset (that double-shifts the drawing off to the side).
                var world = new Vector3(pt.X * this.conv, pt.Y * this.conv, this.playerHeight);
                var p = this.Ctx.Render.WorldToScreen(world, this.playerHeight);
                if (prev.HasValue)
                {
                    dl.AddLine(prev.Value, p, line, 2f);
                }

                prev = p;
            }

            var goalWorld = new Vector3(this.pathGoal.X * this.conv, this.pathGoal.Y * this.conv, this.playerHeight);
            var gs = this.Ctx.Render.WorldToScreen(goalWorld, this.playerHeight);
            dl.AddCircleFilled(gs, 6f, Draw.Color(new Vector4(1f, 0.5f, 0f, 1f)));
        }

        private static bool IsValidMonster(IEntity e)
        {
            if (!e.IsValid || e.Type != EntityType.Monster ||
                e.State == EntityState.MonsterFriendly || e.State == EntityState.Useless)
            {
                return false;
            }

            // Use IsHidden, not the strict IsTargetable (which is false for most
            // ordinary monsters because of quest/interaction conditions).
            if (e.TryGetComponent<ITargetable>(out var t) && t.IsHidden)
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
