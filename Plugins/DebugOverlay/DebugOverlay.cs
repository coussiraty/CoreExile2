// <copyright file="DebugOverlay.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace DebugOverlay
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     A read-only diagnostics window: game/area state, player vitals, entity
    ///     counts and a nearby-monster list. A pure-read showcase of the ExileBridge
    ///     SDK (no synthetic input).
    /// </summary>
    public sealed class DebugOverlay : Plugin<DebugOverlaySettings>
    {
        private IDisposable? areaChangeToken;
        private int areaChanges;

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonConvert.DeserializeObject<DebugOverlaySettings>(json) ?? new DebugOverlaySettings();
            }

            this.areaChangeToken = this.Ctx.Events.OnAreaChange(() => this.areaChanges++);
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
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Checkbox("Show window", ref this.Settings.ShowWindow);
            ImGui.Checkbox("List nearby monsters", ref this.Settings.ListMonsters);
            ImGui.SliderInt("Max monsters", ref this.Settings.MaxMonsters, 1, 50);
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            if (!this.Settings.ShowWindow)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(360, 460), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Debug Overlay"))
            {
                ImGui.End();
                return;
            }

            var game = this.Ctx.Game;
            ImGui.Text($"State: {game.State}");
            ImGui.Text($"Attached: {game.IsAttached}   Foreground: {game.IsForeground}");
            ImGui.Text($"Area changes: {this.areaChanges}");

            if (!game.IsInGame)
            {
                ImGui.End();
                return;
            }

            var ig = game.InGame;
            ImGui.Separator();
            ImGui.Text($"Area: {ig.AreaName} (lvl {ig.AreaLevel})");
            ImGui.Text($"Town: {ig.IsTown}  Hideout: {ig.IsHideout}");

            var player = ig.Player;
            if (player != null && player.TryGetComponent<ILife>(out var life))
            {
                ImGui.Separator();
                ImGui.Text($"HP:   {life.Health.Current}/{life.Health.Total} ({life.Health.CurrentInPercent}%)");
                ImGui.Text($"ES:   {life.EnergyShield.Current}/{life.EnergyShield.Total}");
                ImGui.Text($"Mana: {life.Mana.Current}/{life.Mana.Total}");
            }

            var awake = this.Ctx.Entities.Awake;
            var counts = new Dictionary<EntityType, int>();
            foreach (var e in awake)
            {
                if (!e.IsValid)
                {
                    continue;
                }

                counts.TryGetValue(e.Type, out var c);
                counts[e.Type] = c + 1;
            }

            ImGui.Separator();
            ImGui.Text($"Awake entities: {awake.Count}");
            foreach (var kv in counts.OrderByDescending(k => k.Value))
            {
                ImGui.BulletText($"{kv.Key}: {kv.Value}");
            }

            if (this.Settings.ListMonsters && player != null && player.TryGetComponent<IRender>(out var pr))
            {
                ImGui.Separator();
                ImGui.Text("Nearby monsters:");
                var rows = new List<(float Dist, string Text)>();
                foreach (var e in awake)
                {
                    if (!e.IsValid || e.Type != EntityType.Monster || e.State == EntityState.MonsterFriendly)
                    {
                        continue;
                    }

                    if (!e.TryGetComponent<IRender>(out var r))
                    {
                        continue;
                    }

                    var dist = Vector2.Distance(pr.GridPosition, r.GridPosition);
                    var rarity = e.TryGetComponent<IObjectMagicProperties>(out var omp) ? omp.Rarity.ToString() : "?";
                    var hp = e.TryGetComponent<ILife>(out var l) && l.Health.Total > 0 ? $"{l.Health.CurrentInPercent}%" : "-";
                    rows.Add((dist, $"{dist,5:F0}  {rarity,-7} {hp,4}  {Short(e.Path)}"));
                }

                foreach (var row in rows.OrderBy(r => r.Dist).Take(this.Settings.MaxMonsters))
                {
                    ImGui.TextUnformatted(row.Text);
                }
            }

            ImGui.End();
        }

        private static string Short(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "?";
            }

            var idx = path.LastIndexOf('/');
            return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
        }
    }
}
