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
    ///     A basic autonomous map-clearing bot (foundation). Per frame it runs a
    ///     simple priority state machine over the SDK entity list:
    ///     <list type="number">
    ///         <item>Combat — attack the nearest monster in range.</item>
    ///         <item>Loot — walk to and click the nearest ground item.</item>
    ///         <item>Explore — move toward the nearest monster or an area tile.</item>
    ///     </list>
    ///     This is intentionally a skeleton: no real pathfinding, portal/waypoint
    ///     handling, item filtering or stuck-detection yet (see ARCHITECTURE.md).
    /// </summary>
    public sealed class MapClearBot : Plugin<MapClearBotSettings>
    {
        private bool wasToggleDown;
        private DateTime lastAction = DateTime.MinValue;
        private string state = "idle";

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonConvert.DeserializeObject<MapClearBotSettings>(json) ?? new MapClearBotSettings();
            }
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
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
            this.HandleToggle();

            if (!this.Settings.Enabled || !this.Ctx.Game.IsInGame || !this.Ctx.Game.IsForeground)
            {
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            if ((DateTime.Now - this.lastAction).TotalMilliseconds < this.Settings.ActionDelayMs)
            {
                return;
            }

            var ig = this.Ctx.Game.InGame;
            var self = ig.Player;
            if (self == null || !self.TryGetComponent<IRender>(out var selfRender))
            {
                return;
            }

            var terrain = ig.Terrain;
            var playerPos = selfRender.GridPosition;
            var awake = this.Ctx.Entities.Awake;

            // 1) Combat.
            var monster = this.NearestMonster(awake, playerPos, terrain, this.Settings.CombatRange, requireLoS: this.Settings.UseLineOfSight);
            if (monster != null)
            {
                this.Aim(monster);
                if (this.Settings.AttackWithLeftClick)
                {
                    Input.Click(Input.MouseButton.Left);
                }
                else
                {
                    Input.PressKey(this.Settings.AttackKey);
                }

                this.lastAction = DateTime.Now;
                this.state = "combat";
                return;
            }

            // 2) Loot.
            if (this.Settings.PickItems)
            {
                var item = this.NearestItem(awake, playerPos, this.Settings.LootRange);
                if (item != null)
                {
                    this.Aim(item);
                    Input.Click(Input.MouseButton.Left);
                    this.lastAction = DateTime.Now;
                    this.state = "loot";
                    return;
                }
            }

            // 3) Explore.
            var target = this.ExploreTarget(awake, playerPos, terrain);
            if (target.HasValue)
            {
                this.MoveToward(target.Value);
                this.lastAction = DateTime.Now;
                this.state = "explore";
                return;
            }

            this.state = "idle (nothing to do)";
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

            ImGui.Separator();
            ImGui.TextWrapped("Foundation build: simple combat > loot > explore. No pathfinding/portals/item-filter yet.");
            ImGui.Separator();

            ImGui.Checkbox("Attack with left click", ref this.Settings.AttackWithLeftClick);
            if (!this.Settings.AttackWithLeftClick)
            {
                ImGui.SameLine();
                ImGui.Text($"Attack key: {Input.KeyName(this.Settings.AttackKey)}");
            }

            ImGui.Checkbox("Move with left click", ref this.Settings.MoveWithLeftClick);
            if (!this.Settings.MoveWithLeftClick)
            {
                ImGui.SameLine();
                ImGui.Text($"Move key: {Input.KeyName(this.Settings.MoveKey)}");
            }

            ImGui.SliderFloat("Combat range", ref this.Settings.CombatRange, 10f, 150f);
            ImGui.SliderFloat("Loot range", ref this.Settings.LootRange, 10f, 150f);
            ImGui.Checkbox("Pick items", ref this.Settings.PickItems);
            ImGui.Checkbox("Use line of sight", ref this.Settings.UseLineOfSight);
            ImGui.SliderInt("Action delay (ms)", ref this.Settings.ActionDelayMs, 50, 1000);

            if (ImGui.Button("Save"))
            {
                this.SaveSettings();
            }
        }

        private void HandleToggle()
        {
            var down = this.Settings.ToggleKey != 0 && Input.IsKeyDown(this.Settings.ToggleKey);
            if (down && !this.wasToggleDown)
            {
                this.Settings.Enabled = !this.Settings.Enabled;
            }

            this.wasToggleDown = down;
        }

        private IEntity? NearestMonster(IReadOnlyCollection<IEntity> awake, Vector2 playerPos, ITerrain terrain, float range, bool requireLoS)
        {
            IEntity? best = null;
            var bestDist = float.MaxValue;
            foreach (var e in awake)
            {
                if (!IsValidMonster(e) || !e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var dist = Vector2.Distance(playerPos, r.GridPosition);
                if (dist > range || dist >= bestDist)
                {
                    continue;
                }

                if (requireLoS && !Los.HasLineOfSight(terrain, playerPos, r.GridPosition))
                {
                    continue;
                }

                bestDist = dist;
                best = e;
            }

            return best;
        }

        private IEntity? NearestItem(IReadOnlyCollection<IEntity> awake, Vector2 playerPos, float range)
        {
            IEntity? best = null;
            var bestDist = float.MaxValue;
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

                if (!e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var dist = Vector2.Distance(playerPos, r.GridPosition);
                if (dist <= range && dist < bestDist)
                {
                    bestDist = dist;
                    best = e;
                }
            }

            return best;
        }

        private Vector3? ExploreTarget(IReadOnlyCollection<IEntity> awake, Vector2 playerPos, ITerrain terrain)
        {
            // Prefer the nearest monster anywhere on screen to push toward packs.
            IEntity? nearest = null;
            var bestDist = float.MaxValue;
            foreach (var e in awake)
            {
                if (!IsValidMonster(e) || !e.TryGetComponent<IRender>(out var r))
                {
                    continue;
                }

                var dist = Vector2.Distance(playerPos, r.GridPosition);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = e;
                }
            }

            if (nearest != null && nearest.TryGetComponent<IRender>(out var nr))
            {
                return nr.WorldPosition;
            }

            // Otherwise head toward the farthest known area tile (rough outward push).
            var conv = terrain.WorldToGridConvertor;
            if (conv > 0 && terrain.TgtTiles.Count > 0)
            {
                Vector2? farthest = null;
                var far = -1f;
                var scanned = 0;
                foreach (var kv in terrain.TgtTiles)
                {
                    foreach (var tile in kv.Value)
                    {
                        if (++scanned > 2000)
                        {
                            break;
                        }

                        var d = Vector2.Distance(playerPos, tile);
                        if (d > far)
                        {
                            far = d;
                            farthest = tile;
                        }
                    }
                }

                if (farthest.HasValue)
                {
                    return new Vector3(farthest.Value.X * conv, farthest.Value.Y * conv, 0f);
                }
            }

            return null;
        }

        private void Aim(IEntity entity)
        {
            if (entity.TryGetComponent<IRender>(out var r))
            {
                this.PointAt(r.WorldPosition);
            }
        }

        private void MoveToward(Vector3 world)
        {
            if (!this.PointAt(world))
            {
                return;
            }

            if (this.Settings.MoveWithLeftClick)
            {
                Input.Click(Input.MouseButton.Left);
            }
            else
            {
                Input.PressKey(this.Settings.MoveKey);
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

    /// <summary>Minimal Bresenham line-of-sight over the SDK walkable grid.</summary>
    internal static class Los
    {
        public static bool HasLineOfSight(ITerrain terrain, Vector2 from, Vector2 to)
        {
            int x0 = (int)from.X, y0 = (int)from.Y, x1 = (int)to.X, y1 = (int)to.Y;
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            var steps = 0;

            while (true)
            {
                if (++steps > 3 && Walkable(terrain, x0, y0) <= 2)
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

        private static int Walkable(ITerrain terrain, int x, int y)
        {
            var data = terrain?.WalkableData;
            var bpr = terrain?.BytesPerRow ?? 0;
            if (data == null || data.Length == 0 || bpr <= 0)
            {
                return 0;
            }

            var rows = data.Length / bpr;
            var width = bpr * 2;
            if (x < 0 || y < 0 || x >= width || y >= rows)
            {
                return 0;
            }

            var index = (y * bpr) + (x / 2);
            if (index >= data.Length)
            {
                return 0;
            }

            return (data[index] >> ((x % 2 == 0) ? 0 : 4)) & 0xF;
        }
    }
}
