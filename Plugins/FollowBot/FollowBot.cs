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
        private readonly MovementController mover = new();
        private DateTime lastMove = DateTime.MinValue;
        private string status = "idle";
        private string captureToken = string.Empty;

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
            this.mover.Stop();
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
                this.mover.Stop();
                throw;
            }
        }

        private void Step()
        {
            if (!this.Settings.Enabled || !this.Ctx.Game.IsInGame || !this.Ctx.Game.IsForeground)
            {
                this.mover.Stop();
                return;
            }

            if (string.IsNullOrWhiteSpace(this.Settings.LeaderName) || this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                this.mover.Stop();
                return;
            }

            var self = this.Ctx.Game.InGame.Player;
            if (self == null || !self.TryGetComponent<IRender>(out var selfRender))
            {
                this.mover.Stop();
                return;
            }

            var leader = this.FindLeader();
            if (leader == null || !leader.TryGetComponent<IRender>(out var leaderRender))
            {
                this.mover.Stop();
                this.status = "leader not found";
                return;
            }

            var distance = Vector2.Distance(selfRender.GridPosition, leaderRender.GridPosition);
            if (distance <= this.Settings.FollowDistance)
            {
                this.mover.Stop();
                this.status = $"in range ({distance:F0})";
                return;
            }

            var leaderWorld = leaderRender.WorldPosition;
            var leaderScreen = this.Ctx.Render.WorldToScreen(leaderWorld, leaderWorld.Z);

            if (this.Settings.Movement == MovementMode.Wasd)
            {
                this.mover.Configure(this.Settings.MoveUpKey, this.Settings.MoveDownKey, this.Settings.MoveLeftKey, this.Settings.MoveRightKey);
                var selfWorld = selfRender.WorldPosition;
                var selfScreen = this.Ctx.Render.WorldToScreen(selfWorld, selfWorld.Z);
                this.mover.MoveToward(selfScreen, leaderScreen, this.Settings.MoveDeadzonePx);
                this.status = $"following WASD ({distance:F0})";
                return;
            }

            // Mouse mode (throttled click-to-move).
            if ((DateTime.Now - this.lastMove).TotalMilliseconds < this.Settings.RepathMs)
            {
                return;
            }

            var window = this.Ctx.Game.WindowArea;
            if (leaderScreen.X < 0 || leaderScreen.Y < 0 || leaderScreen.X > window.Width || leaderScreen.Y > window.Height)
            {
                this.status = "leader off-screen";
                return;
            }

            Input.MoveMouse((int)(window.X + leaderScreen.X), (int)(window.Y + leaderScreen.Y));
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
            }
            else
            {
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
            }

            ImGui.Separator();
            ImGui.Text($"Status: {this.status}");
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
