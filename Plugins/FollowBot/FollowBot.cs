// <copyright file="FollowBot.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace FollowBot
{
    using System;
    using System.IO;
    using System.Numerics;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Follows another player (by character name): when the leader is farther
    ///     than the configured distance, moves toward them by pointing the cursor and
    ///     issuing a move command. Built entirely on the ExileBridge SDK.
    /// </summary>
    public sealed class FollowBot : Plugin<FollowBotSettings>
    {
        private DateTime lastMove = DateTime.MinValue;
        private string status = "idle";

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonConvert.DeserializeObject<FollowBotSettings>(json) ?? new FollowBotSettings();
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
            if (!this.Settings.Enabled || !this.Ctx.Game.IsInGame || !this.Ctx.Game.IsForeground)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(this.Settings.LeaderName) || this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            var self = this.Ctx.Game.InGame.Player;
            if (self == null || !self.TryGetComponent<IRender>(out var selfRender))
            {
                return;
            }

            var leader = this.FindLeader();
            if (leader == null)
            {
                this.status = "leader not found";
                return;
            }

            if (!leader.TryGetComponent<IRender>(out var leaderRender))
            {
                return;
            }

            var distance = Vector2.Distance(selfRender.GridPosition, leaderRender.GridPosition);
            if (distance <= this.Settings.FollowDistance)
            {
                this.status = $"in range ({distance:F0})";
                return;
            }

            if ((DateTime.Now - this.lastMove).TotalMilliseconds < this.Settings.RepathMs)
            {
                return;
            }

            var world = leaderRender.WorldPosition;
            var screen = this.Ctx.Render.WorldToScreen(world, world.Z);
            var window = this.Ctx.Game.WindowArea;
            if (screen.X < 0 || screen.Y < 0 || screen.X > window.Width || screen.Y > window.Height)
            {
                this.status = "leader off-screen";
                return;
            }

            Input.MoveMouse((int)(window.X + screen.X), (int)(window.Y + screen.Y));
            if (this.Settings.UseLeftClick)
            {
                Input.Click(Input.MouseButton.Left);
            }
            else if (this.Settings.MoveKey != 0)
            {
                Input.PressKey(this.Settings.MoveKey);
            }

            this.lastMove = DateTime.Now;
            this.status = $"following ({distance:F0})";
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Checkbox("Enabled", ref this.Settings.Enabled);
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("Leader name", ref this.Settings.LeaderName, 64))
            {
                this.SaveSettings();
            }

            if (ImGui.SliderFloat("Follow distance", ref this.Settings.FollowDistance, 5f, 100f))
            {
                this.SaveSettings();
            }

            if (ImGui.Checkbox("Move with left click", ref this.Settings.UseLeftClick))
            {
                this.SaveSettings();
            }

            if (!this.Settings.UseLeftClick)
            {
                ImGui.SameLine();
                ImGui.Text($"Move key: {Input.KeyName(this.Settings.MoveKey)}");
            }

            if (ImGui.InputInt("Repath delay (ms)", ref this.Settings.RepathMs))
            {
                if (this.Settings.RepathMs < 50)
                {
                    this.Settings.RepathMs = 50;
                }

                this.SaveSettings();
            }

            ImGui.Separator();
            ImGui.Text($"Status: {this.status}");
        }

        private IEntity? FindLeader()
        {
            foreach (var e in this.Ctx.Entities.Awake)
            {
                if (!e.IsValid || e.Type != EntityType.Player || e.Subtype != EntitySubtype.PlayerOther)
                {
                    continue;
                }

                if (e.TryGetComponent<IPlayer>(out var player) &&
                    string.Equals(player.Name, this.Settings.LeaderName, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }

            return null;
        }
    }
}
