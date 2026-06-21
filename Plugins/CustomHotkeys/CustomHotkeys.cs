// <copyright file="CustomHotkeys.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace CustomHotkeys
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Maps a key to a sequence of synthetic actions (key presses, clicks,
    ///     delays). Edge-triggered: the macro fires once per key press. All input is
    ///     dispatched through the shared <see cref="Input" /> worker.
    /// </summary>
    public sealed class CustomHotkeys : Plugin<CustomHotkeysSettings>
    {
        private readonly Dictionary<Macro, bool> wasPressed = new();
        private string captureToken = string.Empty;

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                var json = File.ReadAllText(this.SettingsPath);
                this.Settings = JsonConvert.DeserializeObject<CustomHotkeysSettings>(json) ?? new CustomHotkeysSettings();
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
            if (!this.Settings.Enabled)
            {
                return;
            }

            if (this.Settings.OnlyWhenForeground && !this.Ctx.Game.IsForeground)
            {
                return;
            }

            foreach (var macro in this.Settings.Macros)
            {
                this.wasPressed.TryGetValue(macro, out var prev);
                var now = macro.Enabled && macro.TriggerKey != 0 && Input.IsKeyDown(macro.TriggerKey);
                if (now && !prev)
                {
                    this.Run(macro);
                }

                this.wasPressed[macro] = now;
            }
        }

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Checkbox("Enabled", ref this.Settings.Enabled);
            ImGui.SameLine();
            ImGui.Checkbox("Only when game focused", ref this.Settings.OnlyWhenForeground);
            ImGui.Separator();

            if (ImGui.Button("Add Macro"))
            {
                this.Settings.Macros.Add(new Macro());
                this.SaveSettings();
            }

            Macro? toRemove = null;
            for (var i = 0; i < this.Settings.Macros.Count; i++)
            {
                var macro = this.Settings.Macros[i];
                ImGui.PushID(i);
                ImGui.Separator();

                var name = macro.Name;
                ImGui.SetNextItemWidth(160);
                if (ImGui.InputText("Name", ref name, 64))
                {
                    macro.Name = name;
                }

                ImGui.SameLine();
                var en = macro.Enabled;
                if (ImGui.Checkbox("On", ref en))
                {
                    macro.Enabled = en;
                    this.SaveSettings();
                }

                // Trigger key bind.
                ImGui.Text("Trigger:");
                ImGui.SameLine();
                var triggerLabel = this.captureToken == $"trig{i}" ? "press a key..." : Input.KeyName(macro.TriggerKey);
                if (ImGui.Button($"{triggerLabel}##trig{i}", new System.Numerics.Vector2(120, 0)))
                {
                    this.captureToken = $"trig{i}";
                }

                if (this.captureToken == $"trig{i}" && Input.TryCaptureKey(out var tvk))
                {
                    macro.TriggerKey = tvk;
                    this.captureToken = string.Empty;
                    this.SaveSettings();
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Remove Macro"))
                {
                    toRemove = macro;
                }

                this.DrawSteps(i, macro);
                ImGui.PopID();
            }

            if (toRemove != null)
            {
                this.Settings.Macros.Remove(toRemove);
                this.SaveSettings();
            }
        }

        private void DrawSteps(int macroIndex, Macro macro)
        {
            ImGui.Indent(16);
            MacroStep? stepToRemove = null;
            for (var j = 0; j < macro.Steps.Count; j++)
            {
                var step = macro.Steps[j];
                ImGui.PushID($"s{j}");
                ImGui.Text($"{j + 1}.");
                ImGui.SameLine();
                ImGui.Text(step.Type.ToString());

                if (step.Type == MacroStepType.KeyPress)
                {
                    ImGui.SameLine();
                    var token = $"step{macroIndex}_{j}";
                    var label = this.captureToken == token ? "press..." : Input.KeyName(step.Key);
                    if (ImGui.Button($"{label}##{token}", new System.Numerics.Vector2(90, 0)))
                    {
                        this.captureToken = token;
                    }

                    if (this.captureToken == token && Input.TryCaptureKey(out var kvk))
                    {
                        step.Key = kvk;
                        this.captureToken = string.Empty;
                        this.SaveSettings();
                    }
                }

                if (step.Type == MacroStepType.Delay)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100);
                    var ms = step.DelayMs;
                    if (ImGui.InputInt("ms", ref ms))
                    {
                        step.DelayMs = ms;
                        this.SaveSettings();
                    }
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("x"))
                {
                    stepToRemove = step;
                }

                ImGui.PopID();
            }

            if (stepToRemove != null)
            {
                macro.Steps.Remove(stepToRemove);
                this.SaveSettings();
            }

            if (ImGui.SmallButton("+Key"))
            {
                macro.Steps.Add(new MacroStep { Type = MacroStepType.KeyPress });
                this.SaveSettings();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("+LClick"))
            {
                macro.Steps.Add(new MacroStep { Type = MacroStepType.LeftClick });
                this.SaveSettings();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("+RClick"))
            {
                macro.Steps.Add(new MacroStep { Type = MacroStepType.RightClick });
                this.SaveSettings();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("+Delay"))
            {
                macro.Steps.Add(new MacroStep { Type = MacroStepType.Delay, DelayMs = 100 });
                this.SaveSettings();
            }

            ImGui.Unindent(16);
        }

        private void Run(Macro macro)
        {
            foreach (var step in macro.Steps)
            {
                switch (step.Type)
                {
                    case MacroStepType.KeyPress:
                        if (step.Key != 0)
                        {
                            Input.PressKey(step.Key);
                        }

                        break;
                    case MacroStepType.LeftClick:
                        Input.Click(Input.MouseButton.Left);
                        break;
                    case MacroStepType.RightClick:
                        Input.Click(Input.MouseButton.Right);
                        break;
                    case MacroStepType.MiddleClick:
                        Input.Click(Input.MouseButton.Middle);
                        break;
                    case MacroStepType.Delay:
                        var ms = step.DelayMs;
                        Input.Enqueue(() => Thread.Sleep(System.Math.Clamp(ms, 1, 10000)));
                        break;
                }
            }
        }
    }
}
