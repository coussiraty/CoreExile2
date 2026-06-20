// <copyright file="SamplePlugin.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridgeSample
{
    using System;
    using System.IO;
    using System.Text.Json;
    using ExileBridge;
    using ImGuiNET;

    /// <summary>
    ///     A minimal reference plugin built entirely on the ExileBridge SDK. It
    ///     references only <c>ExileBridge</c> (plus ImGui for drawing) and reaches
    ///     the game exclusively through the injected <see cref="Plugin{T}.Ctx" />.
    /// </summary>
    public sealed class SamplePlugin : Plugin<SampleSettings>
    {
        private IDisposable? areaChangeToken;
        private int areaChangeCount;

        // ImGui.Checkbox needs ref-able fields, so settings use fields; tell
        // System.Text.Json to (de)serialize them.
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            IncludeFields = true,
            WriteIndented = true,
        };

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonSerializer.Deserialize<SampleSettings>(json, JsonOptions) ?? new SampleSettings();
            }

            // Subscribe via the SDK; the token unsubscribes on disable.
            this.areaChangeToken = this.Ctx.Events.OnAreaChange(() =>
            {
                this.areaChangeCount++;
                this.Ctx.Log.Info($"[ExileBridgeSample] area changed -> {this.Ctx.Game.InGame.AreaName}");
            });
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.areaChangeToken?.Dispose();
            this.areaChangeToken = null;
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
            File.WriteAllText(this.SettingsPath, JsonSerializer.Serialize(this.Settings, JsonOptions));
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Checkbox("Show demo window", ref this.Settings.ShowWindow);
            ImGui.Checkbox("List nearby monsters", ref this.Settings.ShowNearbyMonsters);
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            if (!this.Settings.ShowWindow)
            {
                return;
            }

            if (ImGui.Begin("ExileBridge Sample"))
            {
                var game = this.Ctx.Game;
                ImGui.Text($"State: {game.State}");
                ImGui.Text($"Attached: {game.IsAttached}   Foreground: {game.IsForeground}");
                ImGui.Text($"Area changes this session: {this.areaChangeCount}");

                if (game.IsInGame)
                {
                    var ig = game.InGame;
                    ImGui.Separator();
                    ImGui.Text($"Area: {ig.AreaName} (lvl {ig.AreaLevel})");
                    ImGui.Text($"Town: {ig.IsTown}   Hideout: {ig.IsHideout}");

                    if (ig.Player.TryGetComponent<ILife>(out var life))
                    {
                        ImGui.Text($"Player HP: {life.Health.Current}/{life.Health.Total}   ES: {life.EnergyShield.Current}/{life.EnergyShield.Total}");
                    }

                    if (this.Settings.ShowNearbyMonsters)
                    {
                        ImGui.Separator();
                        var monsters = 0;
                        foreach (var e in this.Ctx.Entities.Awake)
                        {
                            if (e.Type == EntityType.Monster && e.IsValid)
                            {
                                monsters++;
                            }
                        }

                        ImGui.Text($"Awake monsters nearby: {monsters}");
                    }
                }
            }

            ImGui.End();
        }
    }
}
