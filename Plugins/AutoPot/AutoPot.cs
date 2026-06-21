// <copyright file="AutoPot.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoPot
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Uses flasks automatically when a watched vital (life / energy shield /
    ///     mana) drops below a configurable percentage. Reads the player vitals via
    ///     the ExileBridge SDK; presses keys through the shared <see cref="Input" />.
    /// </summary>
    public sealed class AutoPot : Plugin<AutoPotSettings>
    {
        private readonly Dictionary<FlaskRule, DateTime> lastUsed = new();
        private string captureToken = string.Empty;

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonConvert.DeserializeObject<AutoPotSettings>(json) ?? new AutoPotSettings();
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

            if (this.Settings.PauseWhenPanelOpen && this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            var player = this.Ctx.Game.InGame.Player;
            if (player == null || !player.TryGetComponent<ILife>(out var life) || !life.IsAlive)
            {
                return;
            }

            var now = DateTime.Now;
            foreach (var rule in this.Settings.Flasks)
            {
                if (!rule.Enabled || rule.Key == 0)
                {
                    continue;
                }

                var vital = rule.Vital switch
                {
                    VitalKind.EnergyShield => life.EnergyShield,
                    VitalKind.Mana => life.Mana,
                    _ => life.Health,
                };

                if (vital.Total <= 0)
                {
                    continue; // pool does not exist on this build
                }

                if (vital.CurrentInPercent > rule.ThresholdPercent)
                {
                    continue;
                }

                this.lastUsed.TryGetValue(rule, out var last);
                if ((now - last).TotalMilliseconds < rule.CooldownMs)
                {
                    continue;
                }

                Input.PressKey(rule.Key);
                this.lastUsed[rule] = now;
            }
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Checkbox("Enabled", ref this.Settings.Enabled);
            ImGui.SameLine();
            ImGui.Checkbox("Pause when a panel is open", ref this.Settings.PauseWhenPanelOpen);
            ImGui.Separator();

            if (ImGui.Button("Add Flask"))
            {
                this.Settings.Flasks.Add(new FlaskRule());
                this.SaveSettings();
            }

            FlaskRule? toRemove = null;
            for (var i = 0; i < this.Settings.Flasks.Count; i++)
            {
                var rule = this.Settings.Flasks[i];
                ImGui.PushID(i);
                ImGui.Separator();

                ImGui.Checkbox("##en", ref rule.Enabled);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.InputText("Name", ref rule.Name, 32);

                ImGui.Text("Key:");
                ImGui.SameLine();
                var label = this.captureToken == $"k{i}" ? "press..." : Input.KeyName(rule.Key);
                if (ImGui.Button($"{label}##k{i}", new System.Numerics.Vector2(90, 0)))
                {
                    this.captureToken = $"k{i}";
                }

                if (this.captureToken == $"k{i}" && Input.TryCaptureKey(out var vk))
                {
                    rule.Key = vk;
                    this.captureToken = string.Empty;
                    this.SaveSettings();
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                var vital = rule.Vital;
                if (Draw.IEnumerableComboBox("##vital", new[] { VitalKind.Health, VitalKind.EnergyShield, VitalKind.Mana }, ref vital))
                {
                    rule.Vital = vital;
                    this.SaveSettings();
                }

                ImGui.SetNextItemWidth(160);
                if (ImGui.SliderInt("Threshold %", ref rule.ThresholdPercent, 1, 99))
                {
                    this.SaveSettings();
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("Cooldown ms", ref rule.CooldownMs))
                {
                    if (rule.CooldownMs < 100)
                    {
                        rule.CooldownMs = 100;
                    }

                    this.SaveSettings();
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    toRemove = rule;
                }

                ImGui.PopID();
            }

            if (toRemove != null)
            {
                this.Settings.Flasks.Remove(toRemove);
                this.SaveSettings();
            }
        }
    }
}
