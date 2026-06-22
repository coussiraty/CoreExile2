// <copyright file="AutoAim.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoAim
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     AutoAim plugin. Auto-targets nearby monsters and drives skills/combos,
    ///     culling strike and auto-chest. Built entirely on the ExileBridge SDK; all
    ///     game access goes through <see cref="Plugin{T}.Ctx" />. Synthetic input is
    ///     executed on a dedicated background worker so the overlay render thread is
    ///     never blocked.
    /// </summary>
    public sealed class AutoAim : Plugin<AutoAimSettings>
    {
        // ---- Win32 input (independent of the host) -------------------------------
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        private const int VK_CTRL = 17;
        private const int VK_SHIFT = 16;
        private const int VK_ALT = 18;
        private const int VK_ESC = 27;

        private const float CullingStrikeCooldown = 0.2f; // seconds
        private const float MIN_SKILL_DELAY = 0.1f; // seconds between any two parallel skills

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // ---- Frame-cached game state (set each DrawUI) ---------------------------
        private ITerrain terrain;
        private IReadOnlyCollection<IEntity> awakeEntities = Array.Empty<IEntity>();

        // ---- Runtime state -------------------------------------------------------
        private readonly InputExecutor input = new();
        private bool wasToggleKeyPressed;
        private IEntity targetedMonster;
        private string debugInfo = string.Empty;
        private string debugInfo2 = string.Empty;
        private string debugInfo3 = string.Empty;

        private DateTime lastSkillUse = DateTime.MinValue;
        private bool isSkillKeyHeld;
        private DateTime skillKeyPressTime = DateTime.MinValue;

        private DateTime lastCullingStrikeUse = DateTime.MinValue;

        private readonly List<SkillCombo> skillCombos = new();
        private int currentComboIndex = -1;
        private int currentSkillInCombo;
        private DateTime lastComboSkillTime = DateTime.MinValue;
        private bool isExecutingCombo;
        private IEntity currentComboTarget;

        private readonly Dictionary<int, DateTime> comboLastUsed = new();
        private readonly Dictionary<int, HashSet<uint>> comboUsedTargets = new();
        private DateTime lastBuffComboTime = DateTime.MinValue;

        private bool isExecutingParallelCombo;
        private int currentParallelComboIndex = -1;
        private IEntity currentParallelComboTarget;
        private DateTime lastAnySkillExecution = DateTime.MinValue;

        private DateTime lastChestInteraction = DateTime.MinValue;
        private IEntity targetedChest;

        private readonly Dictionary<string, bool> isCapturingKey = new();
        private readonly Dictionary<string, string> keyBindingLabels = new();
        private readonly Dictionary<string, KeyCombination> keyCombinations = new();

        private string SettingPathname => Path.Combine(this.DirectoryPath, "config", "AutoAimConfig.json");

        // ====================================================================
        //  Internal data structures
        // ====================================================================
        private struct KeyCombination
        {
            public int MainKey;
            public bool UseCtrl;
            public bool UseShift;
            public bool UseAlt;

            public KeyCombination(int mainKey, bool ctrl = false, bool shift = false, bool alt = false)
            {
                this.MainKey = mainKey;
                this.UseCtrl = ctrl;
                this.UseShift = shift;
                this.UseAlt = alt;
            }
        }

        private struct ComboAction
        {
            public ComboActionType ActionType;
            public KeyCombination KeyCombination;
            public string DisplayName;
            public float HoldDuration;

            public ComboAction(ComboActionType actionType, float holdDuration = 1.0f)
            {
                this.ActionType = actionType;
                this.KeyCombination = new KeyCombination(0);
                this.DisplayName = actionType.ToString();
                this.HoldDuration = holdDuration;
            }

            public ComboAction(int mainKey, bool ctrl = false, bool shift = false, bool alt = false, float holdDuration = 1.0f)
            {
                this.ActionType = ComboActionType.KeyPress;
                this.KeyCombination = new KeyCombination(mainKey, ctrl, shift, alt);
                this.DisplayName = "Key Press";
                this.HoldDuration = holdDuration;
            }
        }

        private struct ComboSkill
        {
            public ComboAction Action;
            public float Cooldown;
            public int Priority;
            public DateTime LastUsed;
            public bool Enabled;

            public ComboSkill(ComboAction action, float cooldown, int priority = 1)
            {
                this.Action = action;
                this.Cooldown = cooldown;
                this.Priority = priority;
                this.LastUsed = DateTime.MinValue;
                this.Enabled = true;
            }

            public readonly bool IsReady => (DateTime.Now - this.LastUsed).TotalSeconds >= this.Cooldown;
        }

        private struct SkillCombo
        {
            public string Name;
            public List<ComboAction> Actions;
            public List<float> Delays;
            public List<ComboSkill> ParallelSkills;
            public bool UseParallelMode;
            public Rarity TargetRarity;
            public bool Enabled;
            public float ComboCooldown;
            public bool OneTimePerTarget;
            public bool IsBuffCombo;
            public float BuffCooldown;
            public float MinDelayBetweenSkills;

            public SkillCombo(string name, Rarity rarity)
            {
                this.Name = name;
                this.Actions = new List<ComboAction>();
                this.Delays = new List<float>();
                this.ParallelSkills = new List<ComboSkill>();
                this.UseParallelMode = false;
                this.TargetRarity = rarity;
                this.Enabled = true;
                this.ComboCooldown = 5.0f;
                this.OneTimePerTarget = false;
                this.IsBuffCombo = false;
                this.BuffCooldown = 30.0f;
                this.MinDelayBetweenSkills = 0.2f;
            }
        }

        /// <summary>
        ///     Single-threaded FIFO worker that runs synthetic input off the render
        ///     thread. Key/mouse sequences that need to sleep (down/up gaps, holds)
        ///     run here so the overlay never stalls.
        /// </summary>
        private sealed class InputExecutor : IDisposable
        {
            private readonly BlockingCollection<Action> queue = new(new ConcurrentQueue<Action>());
            private readonly Thread worker;
            private int pending;

            public InputExecutor()
            {
                this.worker = new Thread(this.Loop)
                {
                    IsBackground = true,
                    Name = "AutoAim-Input",
                };
                this.worker.Start();
            }

            public int Pending => Volatile.Read(ref this.pending);

            public void Enqueue(Action action)
            {
                if (action == null || this.queue.IsAddingCompleted)
                {
                    return;
                }

                Interlocked.Increment(ref this.pending);
                try
                {
                    this.queue.Add(action);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Decrement(ref this.pending);
                }
            }

            public void Dispose()
            {
                try
                {
                    this.queue.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            private void Loop()
            {
                foreach (var action in this.queue.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        // Never let a bad input action kill the worker.
                    }
                    finally
                    {
                        Interlocked.Decrement(ref this.pending);
                    }
                }
            }
        }

        // ====================================================================
        //  Plugin lifecycle
        // ====================================================================

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname)!);
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var json = File.ReadAllText(this.SettingPathname);
                    this.Settings = JsonConvert.DeserializeObject<AutoAimSettings>(json) ?? new AutoAimSettings();
                }
                catch (Exception ex)
                {
                    this.Ctx.Log.Error($"[AutoAim] failed to load settings: {ex.Message}");
                    this.Settings = new AutoAimSettings();
                }
            }

            this.InitializeDefaultCombos();
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            // Make sure we never leave a key or mouse button stuck down.
            this.ReleaseSkillKeyIfHeld();
            this.StopCombo();
            this.StopParallelCombo();
            this.targetedMonster = null;
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname)!);
            var json = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, json);
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            this.HandleToggleKey();
            this.CleanupOldTargets();

            if (!this.Ctx.Game.IsInGame)
            {
                return;
            }

            // Snapshot the volatile game state once per frame.
            var game = this.Ctx.Game.InGame;
            this.terrain = game.Terrain;
            this.awakeEntities = this.Ctx.Entities.Awake;
            var player = game.Player;

            if (player == null || !player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var playerPos = playerRender.GridPosition;

            // Debug-only visuals can draw even when the game is in the background.
            this.DrawVisualizationForDebug(player, playerPos);

            if (!this.Settings.IsEnabled)
            {
                return;
            }

            if (!this.Ctx.Game.IsForeground)
            {
                return;
            }

            // Don't act while a large panel (inventory, passive tree, atlas...) is open.
            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            if (this.targetedMonster != null && !this.IsValidTarget(this.targetedMonster))
            {
                this.targetedMonster = null;
            }

            var bestTarget = this.FindBestTarget(playerPos);
            if (bestTarget != null)
            {
                this.targetedMonster = bestTarget;
                this.MoveMouseToTarget(this.targetedMonster);
            }

            var cullingStrikeUsed = this.HandleCullingStrikeAOE(playerPos);
            if (!cullingStrikeUsed)
            {
                this.HandleAutoSkillImproved(this.targetedMonster, playerPos);
            }
            else
            {
                this.debugInfo2 = "Auto-Skill blocked - Culling Strike has priority";
            }

            this.HandleAutoChest(playerPos);
        }

        // ====================================================================
        //  Input toggling
        // ====================================================================
        private void HandleToggleKey()
        {
            var isToggleKeyPressed = this.IsKeyCombinationPressed("toggleKey");
            if (isToggleKeyPressed && !this.wasToggleKeyPressed)
            {
                this.Settings.IsEnabled = !this.Settings.IsEnabled;
                if (!this.Settings.IsEnabled)
                {
                    this.targetedMonster = null;
                    this.ReleaseSkillKeyIfHeld();
                    this.StopCombo();
                    this.StopParallelCombo();
                }

                if (this.Settings.BeepSound)
                {
                    MessageBeep(0xFFFFFFFF);
                }
            }

            this.wasToggleKeyPressed = isToggleKeyPressed;
        }

        private bool IsKeyCombinationPressed(string keyId)
        {
            if (!this.keyCombinations.TryGetValue(keyId, out var combination))
            {
                return false;
            }

            if ((GetAsyncKeyState(combination.MainKey) & 0x8000) == 0)
            {
                return false;
            }

            var ctrlPressed = (GetAsyncKeyState(VK_CTRL) & 0x8000) != 0;
            var shiftPressed = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            var altPressed = (GetAsyncKeyState(VK_ALT) & 0x8000) != 0;

            return ctrlPressed == combination.UseCtrl &&
                   shiftPressed == combination.UseShift &&
                   altPressed == combination.UseAlt;
        }

        // ====================================================================
        //  Combo configuration / persistence
        // ====================================================================
        private void InitializeDefaultCombos()
        {
            this.LoadCombosFromSettings();
            if (this.skillCombos.Count > 0)
            {
                this.debugInfo = $"{this.skillCombos.Count} combos loaded from settings";
                return;
            }

            var basicCombo = new SkillCombo("Normal + Magic Monsters", Rarity.Normal);
            basicCombo.Actions.Add(new ComboAction(81)); // Q
            basicCombo.Delays.Add(0.5f);
            this.skillCombos.Add(basicCombo);

            var rareCombo = new SkillCombo("Rare Monsters", Rarity.Rare);
            rareCombo.Actions.Add(new ComboAction(81)); // Q
            rareCombo.Actions.Add(new ComboAction(87)); // W
            rareCombo.Delays.Add(0.5f);
            rareCombo.Delays.Add(0.3f);
            this.skillCombos.Add(rareCombo);

            var uniqueCombo = new SkillCombo("Unique Monsters - Parallel", Rarity.Unique)
            {
                UseParallelMode = true,
                MinDelayBetweenSkills = 0.2f,
            };
            uniqueCombo.ParallelSkills.Add(new ComboSkill(new ComboAction(84), 0.8f, 1)); // T
            uniqueCombo.ParallelSkills.Add(new ComboSkill(new ComboAction(ComboActionType.RightClick), 5.0f, 2));
            uniqueCombo.ParallelSkills.Add(new ComboSkill(new ComboAction(82), 2.0f, 3)); // R
            this.skillCombos.Add(uniqueCombo);

            this.debugInfo = $"{this.skillCombos.Count} default combos created";
            this.SaveCombosToSettings();
        }

        private void LoadCombosFromSettings()
        {
            this.skillCombos.Clear();

            foreach (var savedCombo in this.Settings.SkillCombos)
            {
                var combo = new SkillCombo(savedCombo.Name, (Rarity)savedCombo.TargetRarity)
                {
                    Enabled = savedCombo.Enabled,
                    ComboCooldown = savedCombo.ComboCooldown,
                    OneTimePerTarget = savedCombo.OneTimePerTarget,
                    IsBuffCombo = savedCombo.IsBuffCombo,
                    BuffCooldown = savedCombo.BuffCooldown,
                };

                for (var i = 0; i < savedCombo.Actions.Count; i++)
                {
                    var savedAction = savedCombo.Actions[i];
                    ComboAction action;
                    if (savedAction.ActionType is ComboActionType.KeyPress or ComboActionType.KeyPressAndHold)
                    {
                        action = new ComboAction(
                            savedAction.KeyCombination.MainKey,
                            savedAction.KeyCombination.UseCtrl,
                            savedAction.KeyCombination.UseShift,
                            savedAction.KeyCombination.UseAlt,
                            savedAction.HoldDuration)
                        {
                            ActionType = savedAction.ActionType,
                        };
                    }
                    else
                    {
                        action = new ComboAction(savedAction.ActionType, savedAction.HoldDuration);
                    }

                    combo.Actions.Add(action);
                    combo.Delays.Add(i < savedCombo.Delays.Count ? savedCombo.Delays[i] : 0.5f);
                }

                // Legacy fallback: old configs stored bare keys under Skills.
                if (combo.Actions.Count == 0)
                {
                    for (var i = 0; i < savedCombo.Skills.Count; i++)
                    {
                        var s = savedCombo.Skills[i];
                        combo.Actions.Add(new ComboAction(s.MainKey, s.UseCtrl, s.UseShift, s.UseAlt));
                        combo.Delays.Add(i < savedCombo.Delays.Count ? savedCombo.Delays[i] : 0.5f);
                    }
                }

                this.skillCombos.Add(combo);
            }

            this.LoadKeyCombination("toggleKey", this.Settings.ToggleKeyCombination, v => this.Settings.ToggleKey = v);
            this.LoadKeyCombination("autoSkillKey", this.Settings.AutoSkillKeyCombination, v => this.Settings.AutoSkillKey = v);
            this.LoadKeyCombination("cullingStrikeKey", this.Settings.CullingStrikeKeyCombination, v => this.Settings.CullingStrikeKey = v);
        }

        private void LoadKeyCombination(string id, SerializableKeyCombination saved, Action<int> mirrorLegacyKey)
        {
            if (saved == null)
            {
                return;
            }

            this.keyCombinations[id] = new KeyCombination(saved.MainKey, saved.UseCtrl, saved.UseShift, saved.UseAlt);
            mirrorLegacyKey(saved.MainKey);
        }

        private void SaveCombosToSettings()
        {
            this.Settings.SkillCombos.Clear();
            foreach (var combo in this.skillCombos)
            {
                var savedCombo = new SerializableSkillCombo(combo.Name, (int)combo.TargetRarity)
                {
                    Enabled = combo.Enabled,
                    ComboCooldown = combo.ComboCooldown,
                    OneTimePerTarget = combo.OneTimePerTarget,
                    IsBuffCombo = combo.IsBuffCombo,
                    BuffCooldown = combo.BuffCooldown,
                };

                for (var i = 0; i < combo.Actions.Count; i++)
                {
                    var action = combo.Actions[i];
                    SerializableComboAction savedAction;
                    if (action.ActionType is ComboActionType.KeyPress or ComboActionType.KeyPressAndHold)
                    {
                        savedAction = new SerializableComboAction(
                            action.KeyCombination.MainKey,
                            action.KeyCombination.UseCtrl,
                            action.KeyCombination.UseShift,
                            action.KeyCombination.UseAlt)
                        {
                            ActionType = action.ActionType,
                            HoldDuration = action.HoldDuration,
                        };
                    }
                    else
                    {
                        savedAction = new SerializableComboAction(action.ActionType)
                        {
                            HoldDuration = action.HoldDuration,
                        };
                    }

                    savedCombo.Actions.Add(savedAction);
                    savedCombo.Delays.Add(i < combo.Delays.Count ? combo.Delays[i] : 0.5f);
                }

                this.Settings.SkillCombos.Add(savedCombo);
            }

            this.SaveKeyCombination("toggleKey", c => this.Settings.ToggleKeyCombination = c);
            this.SaveKeyCombination("autoSkillKey", c => this.Settings.AutoSkillKeyCombination = c);
            this.SaveKeyCombination("cullingStrikeKey", c => this.Settings.CullingStrikeKeyCombination = c);

            this.SaveSettings();
        }

        private void SaveKeyCombination(string id, Action<SerializableKeyCombination> assign)
        {
            if (this.keyCombinations.TryGetValue(id, out var combo))
            {
                assign(new SerializableKeyCombination(combo.MainKey, combo.UseCtrl, combo.UseShift, combo.UseAlt));
            }
        }

        // ====================================================================
        //  Sequential combo execution
        // ====================================================================
        private bool TryStartCombo(IEntity target)
        {
            if (this.isExecutingCombo || target == null)
            {
                return false;
            }

            if (!target.TryGetComponent<IObjectMagicProperties>(out var magicProps))
            {
                return false;
            }

            var targetRarity = magicProps.Rarity;
            var targetId = target.Id;

            var fallbackOrder = targetRarity switch
            {
                Rarity.Unique => new[] { Rarity.Unique, Rarity.Rare, Rarity.Normal },
                Rarity.Rare => new[] { Rarity.Rare, Rarity.Normal },
                _ => new[] { Rarity.Normal },
            };

            foreach (var fallbackRarity in fallbackOrder)
            {
                for (var i = 0; i < this.skillCombos.Count; i++)
                {
                    var combo = this.skillCombos[i];
                    if (!combo.Enabled || combo.Actions.Count == 0 || combo.UseParallelMode)
                    {
                        continue;
                    }

                    if (combo.TargetRarity != fallbackRarity)
                    {
                        continue;
                    }

                    if (!this.CanExecuteCombo(i, targetId))
                    {
                        continue;
                    }

                    this.currentComboIndex = i;
                    this.currentSkillInCombo = 0;
                    this.isExecutingCombo = true;
                    this.currentComboTarget = target;
                    this.lastComboSkillTime = DateTime.MinValue;
                    this.RecordComboUsage(i, targetId);
                    this.debugInfo = $"Combo started: {combo.Name} for {targetRarity}";
                    return true;
                }
            }

            return false;
        }

        private void HandleComboExecution()
        {
            if (!this.isExecutingCombo || this.currentComboIndex < 0 || this.currentComboIndex >= this.skillCombos.Count)
            {
                return;
            }

            var combo = this.skillCombos[this.currentComboIndex];
            if (this.currentComboTarget == null || !this.IsValidTarget(this.currentComboTarget))
            {
                this.StopCombo();
                return;
            }

            var timeSinceLastAction = DateTime.Now - this.lastComboSkillTime;
            var requiredDelay = this.currentSkillInCombo > 0 ? combo.Delays[this.currentSkillInCombo - 1] : 0;
            if (timeSinceLastAction.TotalSeconds < requiredDelay)
            {
                return;
            }

            if (this.currentSkillInCombo < combo.Actions.Count)
            {
                this.ExecuteComboAction(combo.Actions[this.currentSkillInCombo]);
                this.lastComboSkillTime = DateTime.Now;
                this.currentSkillInCombo++;
            }
            else
            {
                this.StopCombo();
            }
        }

        private void StopCombo()
        {
            this.isExecutingCombo = false;
            this.currentComboIndex = -1;
            this.currentSkillInCombo = 0;
            this.currentComboTarget = null;
            this.lastComboSkillTime = DateTime.MinValue;
        }

        // ====================================================================
        //  Parallel combo execution
        // ====================================================================
        private bool TryStartParallelCombo(IEntity target)
        {
            if (this.isExecutingParallelCombo || target == null)
            {
                return false;
            }

            if (!target.TryGetComponent<IObjectMagicProperties>(out var magicProps))
            {
                return false;
            }

            var targetRarity = magicProps.Rarity;
            for (var i = 0; i < this.skillCombos.Count; i++)
            {
                var combo = this.skillCombos[i];
                if (!combo.Enabled || combo.TargetRarity != targetRarity)
                {
                    continue;
                }

                if (!combo.UseParallelMode && combo.Actions.Count > 1)
                {
                    this.ConvertSequentialToParallel(i);
                    combo = this.skillCombos[i];
                }

                if (combo.UseParallelMode && combo.ParallelSkills.Count > 0)
                {
                    this.currentParallelComboIndex = i;
                    this.isExecutingParallelCombo = true;
                    this.currentParallelComboTarget = target;
                    this.debugInfo3 = $"Parallel combo started: {combo.Name} ({combo.ParallelSkills.Count} skills)";
                    return true;
                }
            }

            return false;
        }

        private void HandleParallelComboExecution()
        {
            if (!this.isExecutingParallelCombo ||
                this.currentParallelComboIndex < 0 ||
                this.currentParallelComboIndex >= this.skillCombos.Count)
            {
                return;
            }

            var combo = this.skillCombos[this.currentParallelComboIndex];
            if (this.currentParallelComboTarget == null || !this.IsValidTarget(this.currentParallelComboTarget))
            {
                this.StopParallelCombo();
                return;
            }

            if ((DateTime.Now - this.lastAnySkillExecution).TotalSeconds < MIN_SKILL_DELAY)
            {
                return;
            }

            var skillIndex = -1;
            var earliestReadyTime = DateTime.MaxValue;
            for (var i = 0; i < combo.ParallelSkills.Count; i++)
            {
                var skill = combo.ParallelSkills[i];
                if (!skill.Enabled || !skill.IsReady)
                {
                    continue;
                }

                var skillReadyTime = skill.LastUsed.AddSeconds(skill.Cooldown);
                if (skillReadyTime < earliestReadyTime)
                {
                    earliestReadyTime = skillReadyTime;
                    skillIndex = i;
                }
            }

            if (skillIndex < 0)
            {
                return;
            }

            var chosen = combo.ParallelSkills[skillIndex];
            this.ExecuteComboAction(chosen.Action);
            chosen.LastUsed = DateTime.Now;
            combo.ParallelSkills[skillIndex] = chosen;
            this.lastAnySkillExecution = DateTime.Now;
            this.skillCombos[this.currentParallelComboIndex] = combo;
            this.debugInfo3 = $"Parallel: fired {this.GetActionName(chosen.Action)}";
        }

        private void StopParallelCombo()
        {
            this.isExecutingParallelCombo = false;
            this.currentParallelComboIndex = -1;
            this.currentParallelComboTarget = null;
        }

        private void ConvertSequentialToParallel(int comboIndex)
        {
            if (comboIndex < 0 || comboIndex >= this.skillCombos.Count)
            {
                return;
            }

            var combo = this.skillCombos[comboIndex];
            if (combo.UseParallelMode || combo.Actions.Count <= 1)
            {
                return;
            }

            combo.ParallelSkills.Clear();
            for (var i = 0; i < combo.Actions.Count; i++)
            {
                var cooldown = i < combo.Delays.Count ? combo.Delays[i] : 1.0f;
                combo.ParallelSkills.Add(new ComboSkill(combo.Actions[i], cooldown, i + 1));
            }

            combo.UseParallelMode = true;
            combo.MinDelayBetweenSkills = 0.2f;
            this.skillCombos[comboIndex] = combo;
        }

        // ====================================================================
        //  Combo control (cooldown / one-time-per-target)
        // ====================================================================
        private bool CanExecuteCombo(int comboIndex, uint targetId)
        {
            if (comboIndex < 0 || comboIndex >= this.skillCombos.Count)
            {
                return false;
            }

            var combo = this.skillCombos[comboIndex];
            var now = DateTime.Now;

            if (combo.IsBuffCombo && (now - this.lastBuffComboTime).TotalSeconds < combo.BuffCooldown)
            {
                return false;
            }

            if (this.comboLastUsed.TryGetValue(comboIndex, out var lastUsed) &&
                (now - lastUsed).TotalSeconds < combo.ComboCooldown)
            {
                return false;
            }

            if (combo.OneTimePerTarget &&
                this.comboUsedTargets.TryGetValue(comboIndex, out var used) &&
                used.Contains(targetId))
            {
                return false;
            }

            return true;
        }

        private void RecordComboUsage(int comboIndex, uint targetId)
        {
            if (comboIndex < 0 || comboIndex >= this.skillCombos.Count)
            {
                return;
            }

            var combo = this.skillCombos[comboIndex];
            var now = DateTime.Now;
            this.comboLastUsed[comboIndex] = now;

            if (combo.IsBuffCombo)
            {
                this.lastBuffComboTime = now;
            }

            if (combo.OneTimePerTarget)
            {
                if (!this.comboUsedTargets.TryGetValue(comboIndex, out var set))
                {
                    set = new HashSet<uint>();
                    this.comboUsedTargets[comboIndex] = set;
                }

                set.Add(targetId);
            }
        }

        private void CleanupOldTargets()
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            foreach (var kvp in this.comboLastUsed.ToList())
            {
                if (kvp.Value < cutoff)
                {
                    this.comboLastUsed.Remove(kvp.Key);
                    if (this.comboUsedTargets.TryGetValue(kvp.Key, out var set))
                    {
                        set.Clear();
                    }
                }
            }
        }

        // ====================================================================
        //  Synthetic input (all routed through the background worker)
        // ====================================================================
        private void ExecuteComboAction(ComboAction action)
        {
            switch (action.ActionType)
            {
                case ComboActionType.KeyPress:
                    this.ExecuteKeyPress(action.KeyCombination);
                    break;
                case ComboActionType.KeyPressAndHold:
                    this.ExecuteKeyPressAndHold(action.KeyCombination, action.HoldDuration);
                    break;
                case ComboActionType.LeftClick:
                    this.ExecuteMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                    break;
                case ComboActionType.RightClick:
                    this.ExecuteMouseClick(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
                    break;
                case ComboActionType.MiddleClick:
                    this.ExecuteMouseClick(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP);
                    break;
                case ComboActionType.HoldLeftClick:
                    this.input.Enqueue(() => mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero));
                    break;
                case ComboActionType.ReleaseLeftClick:
                    this.input.Enqueue(() => mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero));
                    break;
            }
        }

        private void ExecuteKeyPress(KeyCombination combination)
        {
            this.input.Enqueue(() =>
            {
                var skillKey = (byte)combination.MainKey;
                if (combination.UseCtrl) keybd_event(VK_CTRL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                if (combination.UseShift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                if (combination.UseAlt) keybd_event(VK_ALT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                keybd_event(skillKey, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                Thread.Sleep(30);
                keybd_event(skillKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseAlt) keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseShift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseCtrl) keybd_event(VK_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            });
        }

        private void ExecuteKeyPressAndHold(KeyCombination combination, float holdDuration)
        {
            this.input.Enqueue(() =>
            {
                var skillKey = (byte)combination.MainKey;
                if (combination.UseCtrl) keybd_event(VK_CTRL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                if (combination.UseShift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                if (combination.UseAlt) keybd_event(VK_ALT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                keybd_event(skillKey, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                Thread.Sleep(Math.Clamp((int)(holdDuration * 1000), 10, 10000));
                keybd_event(skillKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseAlt) keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseShift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseCtrl) keybd_event(VK_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            });
        }

        private void ExecuteMouseClick(uint downFlag, uint upFlag)
        {
            this.input.Enqueue(() =>
            {
                mouse_event(downFlag, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
            });
        }

        private string GetActionName(ComboAction action)
        {
            return action.ActionType switch
            {
                ComboActionType.KeyPress => $"Key {GetKeyName(action.KeyCombination.MainKey)}",
                ComboActionType.KeyPressAndHold => $"Hold {GetKeyName(action.KeyCombination.MainKey)}",
                ComboActionType.LeftClick => "Left Click",
                ComboActionType.RightClick => "Right Click",
                ComboActionType.MiddleClick => "Middle Click",
                ComboActionType.HoldLeftClick => "Hold Mouse",
                ComboActionType.ReleaseLeftClick => "Release Mouse",
                _ => "Unknown",
            };
        }

        // ====================================================================
        //  Auto-skill
        // ====================================================================
        private void HandleAutoSkillImproved(IEntity currentTarget, Vector2 playerPos)
        {
            if (!this.Settings.EnableAutoSkill)
            {
                this.ReleaseSkillKeyIfHeld();
                return;
            }

            this.HandleComboExecution();
            this.HandleParallelComboExecution();

            if (this.isExecutingCombo || this.isExecutingParallelCombo)
            {
                this.ReleaseSkillKeyIfHeld();
                return;
            }

            if (this.Settings.EnableComboSystem && currentTarget != null)
            {
                var hasSequential = this.skillCombos.Any(c => c.Enabled && c.Actions.Count > 0 && !c.UseParallelMode);
                var hasParallel = this.skillCombos.Any(c => c.Enabled &&
                    ((c.UseParallelMode && c.ParallelSkills.Count > 0) || (!c.UseParallelMode && c.Actions.Count > 1)));

                if (hasParallel && this.TryStartParallelCombo(currentTarget))
                {
                    this.ReleaseSkillKeyIfHeld();
                    return;
                }

                if (hasSequential && this.TryStartCombo(currentTarget))
                {
                    this.ReleaseSkillKeyIfHeld();
                    return;
                }
            }

            // Plain auto-skill fallback.
            var hasValidTarget = false;
            if (currentTarget != null && currentTarget.TryGetComponent<IRender>(out var render))
            {
                var distance = Vector2.Distance(playerPos, render.GridPosition);
                hasValidTarget = distance <= this.Settings.AutoSkillRange;
            }

            var shouldUseSkill = !this.Settings.AutoSkillOnlyInCombat || hasValidTarget;

            if (this.Settings.AutoSkillHoldKey)
            {
                if (shouldUseSkill)
                {
                    if (!this.isSkillKeyHeld)
                    {
                        this.PressAndHoldSkillKey();
                    }
                }
                else if (this.isSkillKeyHeld)
                {
                    this.ReleaseSkillKeyIfHeld();
                }
            }
            else if (shouldUseSkill)
            {
                var cooldownMs = Math.Max(50f, this.Settings.AutoSkillCooldown * 1000f);
                if ((DateTime.Now - this.skillKeyPressTime).TotalMilliseconds >= cooldownMs)
                {
                    this.PressAndReleaseSkillKey();
                    this.lastSkillUse = DateTime.Now;
                }
            }
        }

        private void PressAndHoldSkillKey()
        {
            if (this.isSkillKeyHeld || !this.keyCombinations.TryGetValue("autoSkillKey", out var combination))
            {
                return;
            }

            this.isSkillKeyHeld = true;
            this.skillKeyPressTime = DateTime.Now;
            this.input.Enqueue(() =>
            {
                if (combination.UseCtrl) keybd_event(VK_CTRL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                if (combination.UseShift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                if (combination.UseAlt) keybd_event(VK_ALT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                keybd_event((byte)combination.MainKey, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            });
        }

        private void PressAndReleaseSkillKey()
        {
            if (!this.keyCombinations.TryGetValue("autoSkillKey", out var combination))
            {
                return;
            }

            this.skillKeyPressTime = DateTime.Now;
            this.ExecuteKeyPress(combination);
        }

        private void ReleaseSkillKeyIfHeld()
        {
            if (!this.isSkillKeyHeld || !this.keyCombinations.TryGetValue("autoSkillKey", out var combination))
            {
                this.isSkillKeyHeld = false;
                return;
            }

            this.isSkillKeyHeld = false;
            this.skillKeyPressTime = DateTime.MinValue;
            this.input.Enqueue(() =>
            {
                keybd_event((byte)combination.MainKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseAlt) keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseShift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (combination.UseCtrl) keybd_event(VK_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            });
        }

        // ====================================================================
        //  Culling strike
        // ====================================================================
        private bool HandleCullingStrikeAOE(Vector2 playerPos)
        {
            if (!this.Settings.EnableCullingStrike)
            {
                return false;
            }

            if ((DateTime.Now - this.lastCullingStrikeUse).TotalSeconds < CullingStrikeCooldown)
            {
                return false;
            }

            var executableTarget = this.FindExecutableTarget(playerPos);
            if (executableTarget == null)
            {
                return false;
            }

            this.MoveMouseToTarget(executableTarget);
            this.UseCullingStrike();
            this.lastCullingStrikeUse = DateTime.Now;
            this.ReleaseSkillKeyIfHeld();
            this.debugInfo = "Culling Strike fired";
            return true;
        }

        private IEntity FindExecutableTarget(Vector2 playerPos)
        {
            IEntity bestTarget = null;
            var lowestHealthPercent = float.MaxValue;
            var bestDistance = float.MaxValue;

            foreach (var entity in this.awakeEntities)
            {
                if (!this.IsValidTarget(entity))
                {
                    continue;
                }

                if (!entity.TryGetComponent<ILife>(out var life) || !life.IsAlive)
                {
                    continue;
                }

                if (!entity.TryGetComponent<IRender>(out var render))
                {
                    continue;
                }

                var monsterPos = render.GridPosition;
                var distance = Vector2.Distance(playerPos, monsterPos);
                if (distance > this.Settings.CullingStrikeRange)
                {
                    continue;
                }

                if (!this.CanBeExecuted(entity, life))
                {
                    continue;
                }

                if (this.Settings.EnableLineOfSight &&
                    !RayCaster.IsMonsterTargetable(this.terrain, playerPos, monsterPos))
                {
                    continue;
                }

                var healthPercent = HealthPercent(life);
                if (healthPercent < lowestHealthPercent ||
                    (Math.Abs(healthPercent - lowestHealthPercent) < 1.0f && distance < bestDistance))
                {
                    lowestHealthPercent = healthPercent;
                    bestDistance = distance;
                    bestTarget = entity;
                }
            }

            return bestTarget;
        }

        private bool CanBeExecuted(IEntity monster, ILife life)
        {
            var rarity = Rarity.Normal;
            if (monster.TryGetComponent<IObjectMagicProperties>(out var magicProps))
            {
                rarity = magicProps.Rarity;
            }

            var rarityIndex = (int)rarity;
            if (rarityIndex < 0 || rarityIndex >= this.Settings.CullingStrikeThresholdPerRarity.Length)
            {
                rarityIndex = 0;
            }

            return HealthPercent(life) <= this.Settings.CullingStrikeThresholdPerRarity[rarityIndex];
        }

        private void UseCullingStrike()
        {
            this.ReleaseSkillKeyIfHeld();
            if (this.keyCombinations.TryGetValue("cullingStrikeKey", out var combination))
            {
                this.ExecuteKeyPress(combination);
            }
            else
            {
                this.ExecuteKeyPress(new KeyCombination(this.Settings.CullingStrikeKey));
            }
        }

        // ====================================================================
        //  Auto-chest
        // ====================================================================
        private void HandleAutoChest(Vector2 playerPos)
        {
            if (!this.Settings.EnableAutoChest)
            {
                return;
            }

            // Never open chests while actively fighting.
            if (this.targetedMonster != null)
            {
                this.targetedChest = null;
                return;
            }

            if ((DateTime.Now - this.lastChestInteraction).TotalSeconds < this.Settings.ChestCooldown)
            {
                return;
            }

            if (this.targetedChest != null && !this.IsValidChest(this.targetedChest))
            {
                this.targetedChest = null;
            }

            this.targetedChest ??= this.FindBestChest(playerPos);

            if (this.targetedChest != null)
            {
                this.OpenChest(this.targetedChest);
            }
        }

        private IEntity FindBestChest(Vector2 playerPos)
        {
            IEntity bestChest = null;
            var closestDistance = float.MaxValue;

            foreach (var entity in this.awakeEntities)
            {
                if (!this.IsValidChest(entity))
                {
                    continue;
                }

                if (!entity.TryGetComponent<IRender>(out var render))
                {
                    continue;
                }

                var chestPos = render.GridPosition;
                var distance = Vector2.Distance(playerPos, chestPos);
                if (distance > this.Settings.AutoChestRange)
                {
                    continue;
                }

                if (this.Settings.OnlyOpenWhenSafe && !this.IsAreaSafe(chestPos))
                {
                    continue;
                }

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    bestChest = entity;
                }
            }

            return bestChest;
        }

        private bool IsValidChest(IEntity entity)
        {
            if (entity == null || !entity.IsValid || entity.Type != EntityType.Chest)
            {
                return false;
            }

            if (!entity.TryGetComponent<IChest>(out var chest) || chest.IsOpened)
            {
                return false;
            }

            // A closed chest must not be hidden / not-yet-revealed.
            if (entity.TryGetComponent<ITargetable>(out var targetable) && targetable.IsHidden)
            {
                return false;
            }

            if (chest.IsStrongbox && !this.Settings.OpenStrongboxes)
            {
                return false;
            }

            if (!chest.IsStrongbox && !this.Settings.OpenRegularChests)
            {
                return false;
            }

            return true;
        }

        private bool IsAreaSafe(Vector2 chestPos)
        {
            foreach (var entity in this.awakeEntities)
            {
                if (!entity.IsValid ||
                    entity.Type != EntityType.Monster ||
                    entity.State == EntityState.MonsterFriendly)
                {
                    continue;
                }

                if (entity.TryGetComponent<ILife>(out var life) && !life.IsAlive)
                {
                    continue;
                }

                if (entity.TryGetComponent<IRender>(out var render) &&
                    Vector2.Distance(chestPos, render.GridPosition) <= this.Settings.SafetyCheckRange)
                {
                    return false;
                }
            }

            return true;
        }

        private void OpenChest(IEntity chest)
        {
            if (!chest.TryGetComponent<IRender>(out _))
            {
                return;
            }

            this.MoveMouseToEntity(chest);
            this.ExecuteMouseClick(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
            this.lastChestInteraction = DateTime.Now;
            this.targetedChest = null;
        }

        // ====================================================================
        //  Targeting
        // ====================================================================
        private IEntity FindBestTarget(Vector2 playerPos)
        {
            IEntity bestTarget = null;
            var bestScore = float.MaxValue;
            var totalMonsters = 0;
            var inRange = 0;

            foreach (var entity in this.awakeEntities)
            {
                if (!entity.IsValid ||
                    entity.State == EntityState.Useless ||
                    entity.Type != EntityType.Monster ||
                    entity.State == EntityState.MonsterFriendly)
                {
                    continue;
                }

                totalMonsters++;

                if (!this.IsValidTarget(entity) || !entity.TryGetComponent<IRender>(out var render))
                {
                    continue;
                }

                var monsterPos = render.GridPosition;
                var distance = Vector2.Distance(playerPos, monsterPos);
                if (distance > this.Settings.TargetingRange)
                {
                    continue;
                }

                inRange++;

                if (this.Settings.EnableLineOfSight &&
                    !RayCaster.IsMonsterTargetable(this.terrain, playerPos, monsterPos))
                {
                    continue;
                }

                if (!this.IsRarityAllowed(entity))
                {
                    continue;
                }

                if (distance < bestScore)
                {
                    bestScore = distance;
                    bestTarget = entity;
                }
            }

            this.debugInfo = $"Monsters: {totalMonsters} | InRange: {inRange} | Target: {(bestTarget != null ? "YES" : "NO")} | Range: {this.Settings.TargetingRange:F0}";
            return bestTarget;
        }

        private bool IsValidTarget(IEntity entity)
        {
            if (entity == null || !entity.IsValid || entity.Type != EntityType.Monster)
            {
                return false;
            }

            // Use IsHidden, not the strict IsTargetable (false for most ordinary
            // monsters due to quest/interaction conditions).
            if (entity.TryGetComponent<ITargetable>(out var targetable) && targetable.IsHidden)
            {
                return false;
            }

            if (entity.TryGetComponent<IBuffs>(out var buffs) && buffs.Has("hidden_monster_6B"))
            {
                return false;
            }

            if (entity.TryGetComponent<ILife>(out var life) && !life.IsAlive)
            {
                return false;
            }

            if (entity.TryGetComponent<IStats>(out var stats) &&
                stats.TryGetItemStat(GameStat.MonsterInsideMonolith, out var monolith) && monolith > 0)
            {
                return false;
            }

            return true;
        }

        private bool IsRarityAllowed(IEntity entity)
        {
            if (!entity.TryGetComponent<IObjectMagicProperties>(out var magicProps))
            {
                return this.Settings.TargetNormal;
            }

            return magicProps.Rarity switch
            {
                Rarity.Normal => this.Settings.TargetNormal,
                Rarity.Magic => this.Settings.TargetMagic,
                Rarity.Rare => this.Settings.TargetRare,
                Rarity.Unique => this.Settings.TargetUnique,
                _ => false,
            };
        }

        // ====================================================================
        //  Mouse movement
        // ====================================================================
        private void MoveMouseToTarget(IEntity target) => this.MoveMouseToEntity(target);

        private void MoveMouseToEntity(IEntity entity)
        {
            try
            {
                if (entity == null || !entity.TryGetComponent<IRender>(out var render))
                {
                    return;
                }

                var worldPos = render.WorldPosition;
                var screenPos = this.Ctx.Render.WorldToScreen(worldPos, worldPos.Z);
                var window = this.Ctx.Game.WindowArea;

                if (screenPos.X < 0 || screenPos.Y < 0 ||
                    screenPos.X > window.Width || screenPos.Y > window.Height)
                {
                    return;
                }

                SetCursorPos((int)(window.X + screenPos.X), (int)(window.Y + screenPos.Y));
            }
            catch (Exception ex)
            {
                this.debugInfo2 = $"MoveMouse error: {ex.Message}";
            }
        }

        // ====================================================================
        //  Visualization / debug
        // ====================================================================
        private void DrawVisualizationForDebug(IEntity player, Vector2 playerPos)
        {
            try
            {
                if (this.Settings.ShowRangeCircle || this.Settings.ShowWalkableGrid ||
                    (this.Settings.EnableAutoSkill && this.Settings.ShowAutoSkillRange) ||
                    (this.Settings.EnableCullingStrike && this.Settings.ShowCullingStrikeRange) ||
                    (this.Settings.EnableAutoChest && (this.Settings.ShowChestRange || this.Settings.ShowSafetyRange)) ||
                    this.Settings.ShowTargetLines)
                {
                    this.DrawRangeCircles(player, playerPos);
                }

                if (this.Settings.ShowWalkableGrid)
                {
                    this.DrawWalkableGrid(player);
                }

                if (this.Settings.ShowDebugWindow)
                {
                    this.DrawDebugWindow(player, playerPos);
                }
            }
            catch
            {
                // Visualization must never crash the overlay.
            }
        }

        private void DrawRangeCircles(IEntity player, Vector2 playerPos)
        {
            if (!player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var drawList = ImGui.GetBackgroundDrawList();
            var playerWorld = playerRender.WorldPosition;
            var playerScreen = this.Ctx.Render.WorldToScreen(playerWorld, playerWorld.Z);
            var gridConv = this.terrain?.WorldToGridConvertor ?? 1f;

            float RadiusFor(float gridRange)
            {
                var edge = new Vector3(playerWorld.X + (gridRange * gridConv), playerWorld.Y, playerWorld.Z);
                var edgeScreen = this.Ctx.Render.WorldToScreen(edge, playerWorld.Z);
                return Math.Abs(edgeScreen.X - playerScreen.X);
            }

            if (this.Settings.ShowRangeCircle)
            {
                var color = Draw.Color(this.Settings.IsEnabled
                    ? new Vector4(0, 1, 0, 0.8f)
                    : new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
                drawList.AddCircle(playerScreen, RadiusFor(this.Settings.TargetingRange), color, 48, 2.0f);
            }

            if (this.Settings.EnableAutoSkill && this.Settings.ShowAutoSkillRange)
            {
                drawList.AddCircle(playerScreen, RadiusFor(this.Settings.AutoSkillRange), Draw.Color(new Vector4(1f, 0f, 1f, 0.7f)), 48, 2.0f);
            }

            if (this.Settings.EnableCullingStrike && this.Settings.ShowCullingStrikeRange)
            {
                drawList.AddCircle(playerScreen, RadiusFor(this.Settings.CullingStrikeRange), Draw.Color(new Vector4(1f, 0f, 0f, 0.8f)), 48, 2.0f);
            }

            if (this.Settings.EnableAutoChest && this.Settings.ShowChestRange)
            {
                drawList.AddCircle(playerScreen, RadiusFor(this.Settings.AutoChestRange), Draw.Color(new Vector4(0.8f, 0.6f, 0.2f, 0.7f)), 48, 2.0f);
            }

            if (this.Settings.EnableAutoChest && this.Settings.ShowSafetyRange && this.Settings.OnlyOpenWhenSafe)
            {
                drawList.AddCircle(playerScreen, RadiusFor(this.Settings.SafetyCheckRange), Draw.Color(new Vector4(1f, 0.4f, 0f, 0.6f)), 64, 1.5f);
            }

            drawList.AddCircleFilled(playerScreen, 6.0f, Draw.Color(new Vector4(1f, 1f, 0f, 1f)));

            if (this.Settings.ShowTargetLines && this.targetedMonster != null &&
                this.targetedMonster.TryGetComponent<IRender>(out var targetRender))
            {
                var targetWorld = targetRender.WorldPosition;
                var targetScreen = this.Ctx.Render.WorldToScreen(targetWorld, targetWorld.Z);
                drawList.AddLine(playerScreen, targetScreen, Draw.Color(new Vector4(1f, 0.5f, 0f, 0.8f)), 2.0f);
                drawList.AddCircleFilled(targetScreen, 8.0f, Draw.Color(new Vector4(1f, 0f, 1f, 1f)));
            }
        }

        private void DrawWalkableGrid(IEntity player)
        {
            if (this.terrain == null || !player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var drawList = ImGui.GetForegroundDrawList();
            var gridConv = this.terrain.WorldToGridConvertor;
            if (gridConv <= 0)
            {
                return;
            }

            var playerWorld = playerRender.WorldPosition;
            var playerGridX = (int)(playerWorld.X / gridConv);
            var playerGridY = (int)(playerWorld.Y / gridConv);
            var gridSize = (int)(this.Settings.RayCastRange / gridConv);

            for (var y = -gridSize; y <= gridSize; y++)
            {
                for (var x = -gridSize; x <= gridSize; x++)
                {
                    if ((x * x) + (y * y) > gridSize * gridSize)
                    {
                        continue;
                    }

                    var gridX = playerGridX + x;
                    var gridY = playerGridY + y;
                    var walkable = RayCaster.GetWalkableValue(this.terrain, gridX, gridY);

                    var world = new Vector3(gridX * gridConv, gridY * gridConv, 0f);
                    var screen = this.Ctx.Render.WorldToScreen(world, 0f);

                    var color = (x == 0 && y == 0)
                        ? Draw.Color(new Vector4(1f, 1f, 0f, 1f))
                        : walkable switch
                        {
                            0 => Draw.Color(new Vector4(1f, 0f, 0f, 0.8f)),
                            1 => Draw.Color(new Vector4(0f, 1f, 0f, 0.8f)),
                            2 => Draw.Color(new Vector4(0f, 0.8f, 0.2f, 0.8f)),
                            3 => Draw.Color(new Vector4(0f, 0.6f, 0.4f, 0.8f)),
                            4 => Draw.Color(new Vector4(0.2f, 0.4f, 0.8f, 0.8f)),
                            5 => Draw.Color(new Vector4(0f, 0.2f, 1f, 0.8f)),
                            _ => Draw.Color(new Vector4(0.5f, 0.5f, 0.5f, 0.5f)),
                        };

                    drawList.AddCircleFilled(screen, (x == 0 && y == 0) ? 6f : 3f, color);
                }
            }
        }

        private void DrawDebugWindow(IEntity player, Vector2 playerPos)
        {
            ImGui.SetNextWindowSize(new Vector2(380, 420), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("AutoAim Debug"))
            {
                ImGui.End();
                return;
            }

            ImGui.PushTextWrapPos(ImGui.GetWindowWidth() - 20);
            ImGui.Text($"Auto Aim: {(this.Settings.IsEnabled ? "ENABLED" : "DISABLED")}");
            ImGui.Text($"Current Target: {(this.targetedMonster != null ? "Yes" : "No")}");

            if (!string.IsNullOrEmpty(this.debugInfo3))
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Parallel combo:");
                ImGui.TextWrapped(this.debugInfo3);
            }

            if (!string.IsNullOrEmpty(this.debugInfo))
            {
                ImGui.Separator();
                ImGui.Text("Targeting:");
                ImGui.TextWrapped(this.debugInfo);
            }

            if (!string.IsNullOrEmpty(this.debugInfo2))
            {
                ImGui.Separator();
                ImGui.Text("Skill / mouse:");
                ImGui.TextWrapped(this.debugInfo2);
            }

            ImGui.Separator();
            ImGui.Text($"Input queue: {this.input.Pending}");
            ImGui.Text($"Combos configured: {this.skillCombos.Count}");
            ImGui.Text($"Executing combo: {this.isExecutingCombo} | parallel: {this.isExecutingParallelCombo}");

            if (this.targetedMonster != null)
            {
                ImGui.Separator();
                if (this.targetedMonster.TryGetComponent<IRender>(out var tr))
                {
                    ImGui.Text($"Distance: {Vector2.Distance(playerPos, tr.GridPosition):F1}");
                }

                if (this.targetedMonster.TryGetComponent<IObjectMagicProperties>(out var omp))
                {
                    ImGui.Text($"Rarity: {omp.Rarity}");
                }

                if (this.targetedMonster.TryGetComponent<ILife>(out var life))
                {
                    ImGui.Text($"Health: {HealthPercent(life):F1}%");
                }
            }

            ImGui.PopTextWrapPos();
            ImGui.End();
        }

        // ====================================================================
        //  Settings UI
        // ====================================================================

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Text("=== AUTO AIM SETTINGS ===");
            ImGui.TextWrapped("Automatically aims at nearby monsters. Use the toggle key to enable/disable.");
            ImGui.Separator();

            var isEnabled = this.Settings.IsEnabled;
            if (ImGui.Checkbox("Enable Auto Aim", ref isEnabled))
            {
                this.Settings.IsEnabled = isEnabled;
            }

            Draw.ToolTip("Enable or disable the auto aim functionality.");

            ImGui.Separator();

            if (!ImGui.BeginTabBar("AutoAimTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("Keybind"))
            {
                ImGui.Text("Toggle Key:");
                this.DrawKeyBindButton("Toggle Key", "toggleKey");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Targeting"))
            {
                ImGui.SliderFloat("Targeting Range", ref this.Settings.TargetingRange, 10f, 200f);
                ImGui.SliderFloat("RayCast Range (Visual)", ref this.Settings.RayCastRange, 50f, 1000f);
                ImGui.Checkbox("Enable Line of Sight", ref this.Settings.EnableLineOfSight);
                Draw.ToolTip("Prevents targeting monsters behind walls.");
                ImGui.Separator();
                ImGui.Text("Monster Types to Target:");
                ImGui.Checkbox("Target Normal (White)", ref this.Settings.TargetNormal);
                ImGui.Checkbox("Target Magic (Blue)", ref this.Settings.TargetMagic);
                ImGui.Checkbox("Target Rare (Yellow)", ref this.Settings.TargetRare);
                ImGui.Checkbox("Target Unique (Orange)", ref this.Settings.TargetUnique);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Auto-Skill"))
            {
                this.DrawAutoSkillTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Auto-Chest"))
            {
                this.DrawAutoChestTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Advanced"))
            {
                ImGui.SliderFloat("Mouse Speed", ref this.Settings.MouseSpeed, 0.1f, 5.0f);
                ImGui.Checkbox("Smooth Movement", ref this.Settings.SmoothMovement);
                ImGui.SliderFloat("Target Switch Delay (ms)", ref this.Settings.TargetSwitchDelay, 0f, 1000f);
                ImGui.Checkbox("Prefer Closest Target", ref this.Settings.PreferClosest);
                ImGui.Checkbox("Beep On Toggle", ref this.Settings.BeepSound);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug"))
            {
                ImGui.Checkbox("Show Range Circle", ref this.Settings.ShowRangeCircle);
                ImGui.Checkbox("Show Walkable Grid", ref this.Settings.ShowWalkableGrid);
                ImGui.Checkbox("Show Target Lines", ref this.Settings.ShowTargetLines);
                ImGui.Checkbox("Show Debug Window", ref this.Settings.ShowDebugWindow);
                if (ImGui.Button("Clear Combo Tracking"))
                {
                    this.comboLastUsed.Clear();
                    this.comboUsedTargets.Clear();
                    this.lastBuffComboTime = DateTime.MinValue;
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private void DrawAutoSkillTab()
        {
            ImGui.Checkbox("Enable Auto-Skill", ref this.Settings.EnableAutoSkill);
            if (!this.Settings.EnableAutoSkill)
            {
                return;
            }

            ImGui.Separator();
            ImGui.Text("Skill Key:");
            this.DrawKeyBindButton("Auto-Skill Key", "autoSkillKey");
            ImGui.SliderFloat("Auto-Skill Range", ref this.Settings.AutoSkillRange, 10f, 150f);
            ImGui.SliderFloat("Skill Cooldown (s)", ref this.Settings.AutoSkillCooldown, 0.05f, 5.0f);
            ImGui.Checkbox("Hold Key (vs Press/Release)", ref this.Settings.AutoSkillHoldKey);
            ImGui.Checkbox("Only During Combat", ref this.Settings.AutoSkillOnlyInCombat);
            ImGui.Checkbox("Show Auto-Skill Range Circle", ref this.Settings.ShowAutoSkillRange);

            ImGui.Separator();
            ImGui.Text("Culling Strike:");
            ImGui.Checkbox("Enable Culling Strike", ref this.Settings.EnableCullingStrike);
            if (this.Settings.EnableCullingStrike)
            {
                this.DrawKeyBindButton("Culling Strike Key", "cullingStrikeKey");
                ImGui.Text("Health thresholds (%) by rarity:");
                ImGui.Indent(10);
                ImGui.SliderFloat("Normal", ref this.Settings.CullingStrikeThresholdPerRarity[0], 1f, 50f, "%.1f%%");
                ImGui.SliderFloat("Magic", ref this.Settings.CullingStrikeThresholdPerRarity[1], 1f, 50f, "%.1f%%");
                ImGui.SliderFloat("Rare", ref this.Settings.CullingStrikeThresholdPerRarity[2], 1f, 50f, "%.1f%%");
                ImGui.SliderFloat("Unique", ref this.Settings.CullingStrikeThresholdPerRarity[3], 1f, 50f, "%.1f%%");
                ImGui.Unindent(10);
                ImGui.SliderFloat("Culling Strike Range", ref this.Settings.CullingStrikeRange, 10f, 150f);
                ImGui.Checkbox("Show Culling Strike Range", ref this.Settings.ShowCullingStrikeRange);
            }

            ImGui.Separator();
            ImGui.Checkbox("Enable Combo System", ref this.Settings.EnableComboSystem);
            if (this.Settings.EnableComboSystem)
            {
                this.DrawSkillCombosInterface();
            }
            else
            {
                ImGui.TextDisabled("Enable the combo system to configure skill combos by rarity.");
            }
        }

        private void DrawAutoChestTab()
        {
            ImGui.Checkbox("Enable Auto-Chest", ref this.Settings.EnableAutoChest);
            if (!this.Settings.EnableAutoChest)
            {
                return;
            }

            ImGui.Separator();
            ImGui.Text("Chest Types:");
            ImGui.Checkbox("Regular Chests", ref this.Settings.OpenRegularChests);
            ImGui.Checkbox("Strongboxes", ref this.Settings.OpenStrongboxes);
            Draw.ToolTip("Strongboxes are more valuable but can spawn monsters.");
            ImGui.Separator();
            ImGui.SliderFloat("Chest Detection Range", ref this.Settings.AutoChestRange, 20f, 150f);
            ImGui.SliderFloat("Chest Interaction Cooldown", ref this.Settings.ChestCooldown, 0.1f, 2.0f);
            ImGui.Separator();
            ImGui.Checkbox("Only Open When Safe", ref this.Settings.OnlyOpenWhenSafe);
            if (this.Settings.OnlyOpenWhenSafe)
            {
                ImGui.SliderFloat("Safety Check Range", ref this.Settings.SafetyCheckRange, 30f, 200f);
                ImGui.Checkbox("Show Safety Range Circle", ref this.Settings.ShowSafetyRange);
            }

            ImGui.Checkbox("Show Chest Range Circle", ref this.Settings.ShowChestRange);
        }

        private void DrawSkillCombosInterface()
        {
            if (this.skillCombos.Count == 0)
            {
                this.InitializeDefaultCombos();
            }

            ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.5f, 1.0f), "COMBOS BY MONSTER RARITY");
            ImGui.Separator();

            for (var i = 0; i < this.skillCombos.Count; i++)
            {
                var combo = this.skillCombos[i];
                ImGui.PushID($"combo_{i}");

                var rarityColor = GetRarityColor(combo.TargetRarity);
                ImGui.PushStyleColor(ImGuiCol.Text, rarityColor);
                var enabled = combo.Enabled;
                var rarityName = combo.TargetRarity == Rarity.Normal ? "Normal + Magic" : combo.TargetRarity.ToString();
                if (ImGui.Checkbox($"[{rarityName}] {combo.Name}", ref enabled))
                {
                    combo.Enabled = enabled;
                    this.skillCombos[i] = combo;
                    this.SaveCombosToSettings();
                }

                ImGui.PopStyleColor();

                if (combo.Enabled)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"({combo.Actions.Count} actions{(combo.UseParallelMode ? ", parallel" : string.Empty)})");

                    ImGui.Indent(20);
                    for (var j = 0; j < combo.Actions.Count; j++)
                    {
                        ImGui.PushID($"action_{i}_{j}");
                        var action = combo.Actions[j];
                        ImGui.Text($"{j + 1}. {this.GetActionName(action)}");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80);
                        var delay = combo.Delays[j];
                        if (ImGui.SliderFloat("##delay", ref delay, 0.0f, 5.0f, "%.1fs"))
                        {
                            combo.Delays[j] = Math.Max(0.0f, delay);
                            this.skillCombos[i] = combo;
                            this.SaveCombosToSettings();
                        }

                        ImGui.SameLine();
                        if (ImGui.SmallButton("Remove"))
                        {
                            combo.Actions.RemoveAt(j);
                            combo.Delays.RemoveAt(j);
                            combo.ParallelSkills.Clear();
                            combo.UseParallelMode = false;
                            this.skillCombos[i] = combo;
                            this.SaveCombosToSettings();
                            ImGui.PopID();
                            break;
                        }

                        ImGui.PopID();
                    }

                    ImGui.Separator();
                    if (ImGui.Button("Add Key##" + i))
                    {
                        this.isCapturingKey[$"combo_{i}_keypress"] = true;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Add Left Click##" + i))
                    {
                        combo.Actions.Add(new ComboAction(ComboActionType.LeftClick));
                        combo.Delays.Add(0.5f);
                        this.skillCombos[i] = combo;
                        this.SaveCombosToSettings();
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Add Right Click##" + i))
                    {
                        combo.Actions.Add(new ComboAction(ComboActionType.RightClick));
                        combo.Delays.Add(0.5f);
                        this.skillCombos[i] = combo;
                        this.SaveCombosToSettings();
                    }

                    if (this.isCapturingKey.TryGetValue($"combo_{i}_keypress", out var capturing) && capturing)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.6f, 0f, 1f), "Press a key (ESC to cancel)...");
                        var captured = this.GetPressedKeyCombination();
                        if (captured.MainKey != 0)
                        {
                            combo.Actions.Add(new ComboAction(captured.MainKey, captured.UseCtrl, captured.UseShift, captured.UseAlt));
                            combo.Delays.Add(0.5f);
                            this.skillCombos[i] = combo;
                            this.isCapturingKey[$"combo_{i}_keypress"] = false;
                            this.SaveCombosToSettings();
                        }
                        else if ((GetAsyncKeyState(VK_ESC) & 0x8000) != 0)
                        {
                            this.isCapturingKey[$"combo_{i}_keypress"] = false;
                        }
                    }

                    ImGui.Unindent(20);
                }

                ImGui.PopID();
                ImGui.Separator();
            }
        }

        private void DrawKeyBindButton(string label, string keyId)
        {
            if (!this.keyCombinations.TryGetValue(keyId, out var combo))
            {
                combo = new KeyCombination(label.Contains("Toggle") ? this.Settings.ToggleKey : this.Settings.AutoSkillKey);
                this.keyCombinations[keyId] = combo;
            }

            this.isCapturingKey.TryAdd(keyId, false);
            var capturing = this.isCapturingKey[keyId];
            this.keyBindingLabels[keyId] = this.GetCombinationName(combo);

            var buttonText = capturing ? "Press key combination..." : this.keyBindingLabels[keyId];
            var color = capturing ? new Vector4(1f, 0.5f, 0f, 1f) : new Vector4(0.2f, 0.6f, 1f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, color);
            if (ImGui.Button($"{buttonText}##{keyId}", new Vector2(180, 25)))
            {
                this.isCapturingKey[keyId] = true;
            }

            ImGui.PopStyleColor();

            if (this.isCapturingKey[keyId])
            {
                var captured = this.GetPressedKeyCombination();
                if (captured.MainKey != 0)
                {
                    this.keyCombinations[keyId] = captured;
                    this.isCapturingKey[keyId] = false;
                    this.SaveCombosToSettings();
                }
                else if ((GetAsyncKeyState(VK_ESC) & 0x8000) != 0)
                {
                    this.isCapturingKey[keyId] = false;
                }
            }
        }

        private string GetCombinationName(KeyCombination combination)
        {
            var parts = new List<string>();
            if (combination.UseCtrl) parts.Add("Ctrl");
            if (combination.UseShift) parts.Add("Shift");
            if (combination.UseAlt) parts.Add("Alt");
            parts.Add(GetKeyName(combination.MainKey));
            return string.Join(" + ", parts);
        }

        private KeyCombination GetPressedKeyCombination()
        {
            var ctrl = (GetAsyncKeyState(VK_CTRL) & 0x8000) != 0;
            var shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            var alt = (GetAsyncKeyState(VK_ALT) & 0x8000) != 0;

            for (var vk = 1; vk <= 254; vk++)
            {
                if (vk is VK_SHIFT or VK_CTRL or VK_ALT or VK_ESC or 91 or 92 or 93 or 160 or 161 or 162 or 163 or 164 or 165)
                {
                    continue;
                }

                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    return new KeyCombination(vk, ctrl, shift, alt);
                }
            }

            return new KeyCombination(0);
        }

        // ====================================================================
        //  Helpers
        // ====================================================================
        private static float HealthPercent(ILife life)
        {
            var total = life.Health.Total;
            return total <= 0 ? 0f : life.Health.Current / (float)total * 100f;
        }

        private static Vector4 GetRarityColor(Rarity rarity) => rarity switch
        {
            Rarity.Normal => new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
            Rarity.Magic => new Vector4(0.3f, 0.3f, 1.0f, 1.0f),
            Rarity.Rare => new Vector4(1.0f, 1.0f, 0.0f, 1.0f),
            Rarity.Unique => new Vector4(1.0f, 0.5f, 0.0f, 1.0f),
            _ => new Vector4(0.7f, 0.7f, 0.7f, 1.0f),
        };

        private static string GetKeyName(int vkCode) => vkCode switch
        {
            112 => "F1", 113 => "F2", 114 => "F3", 115 => "F4",
            116 => "F5", 117 => "F6", 118 => "F7", 119 => "F8",
            120 => "F9", 121 => "F10", 122 => "F11", 123 => "F12",
            32 => "Space", 13 => "Enter", 9 => "Tab", 8 => "Backspace",
            1 => "LMouse", 2 => "RMouse", 4 => "MMouse", 5 => "Mouse4", 6 => "Mouse5",
            37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down",
            >= 65 and <= 90 => ((char)vkCode).ToString(),
            >= 48 and <= 57 => ((char)vkCode).ToString(),
            >= 96 and <= 105 => $"Num{vkCode - 96}",
            _ => $"Key{vkCode}",
        };
    }
}
