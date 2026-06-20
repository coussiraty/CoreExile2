// <copyright file="AutoAimSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoAim
{
    using System.Collections.Generic;
    using ExileBridge;

    /// <summary>Type of action a combo step performs.</summary>
    public enum ComboActionType
    {
        /// <summary>Press a key (with optional modifiers).</summary>
        KeyPress = 0,

        /// <summary>Press and hold a key for a duration.</summary>
        KeyPressAndHold = 1,

        /// <summary>Left mouse click.</summary>
        LeftClick = 2,

        /// <summary>Right mouse click.</summary>
        RightClick = 3,

        /// <summary>Middle mouse click.</summary>
        MiddleClick = 4,

        /// <summary>Press and hold the left mouse button (channelling).</summary>
        HoldLeftClick = 5,

        /// <summary>Release a held left mouse button.</summary>
        ReleaseLeftClick = 6,
    }

    /// <summary>Target selection priority (kept for forward compatibility).</summary>
    public enum TargetPriority
    {
        /// <summary>Prefer the closest monster.</summary>
        Closest,

        /// <summary>Prefer the lowest-health monster.</summary>
        LowestHealth,

        /// <summary>Prefer the highest-rarity monster.</summary>
        HighestRarity,
    }

    /// <summary>A serializable key + modifier combination.</summary>
    public class SerializableKeyCombination
    {
        /// <summary>Initializes a new instance of the <see cref="SerializableKeyCombination" /> class.</summary>
        public SerializableKeyCombination()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="SerializableKeyCombination" /> class.</summary>
        /// <param name="mainKey">virtual-key code.</param>
        /// <param name="ctrl">whether Ctrl is required.</param>
        /// <param name="shift">whether Shift is required.</param>
        /// <param name="alt">whether Alt is required.</param>
        public SerializableKeyCombination(int mainKey, bool ctrl = false, bool shift = false, bool alt = false)
        {
            this.MainKey = mainKey;
            this.UseCtrl = ctrl;
            this.UseShift = shift;
            this.UseAlt = alt;
        }

        /// <summary>Gets or sets the virtual-key code of the main key.</summary>
        public int MainKey { get; set; }

        /// <summary>Gets or sets a value indicating whether Ctrl is required.</summary>
        public bool UseCtrl { get; set; }

        /// <summary>Gets or sets a value indicating whether Shift is required.</summary>
        public bool UseShift { get; set; }

        /// <summary>Gets or sets a value indicating whether Alt is required.</summary>
        public bool UseAlt { get; set; }
    }

    /// <summary>A serializable combo step.</summary>
    public class SerializableComboAction
    {
        /// <summary>Initializes a new instance of the <see cref="SerializableComboAction" /> class.</summary>
        public SerializableComboAction()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="SerializableComboAction" /> class.</summary>
        /// <param name="actionType">the action type.</param>
        public SerializableComboAction(ComboActionType actionType)
        {
            this.ActionType = actionType;
            this.KeyCombination = new SerializableKeyCombination();
            this.DisplayName = actionType.ToString();
            this.HoldDuration = 1.0f;
        }

        /// <summary>Initializes a new instance of the <see cref="SerializableComboAction" /> class.</summary>
        /// <param name="mainKey">virtual-key code.</param>
        /// <param name="ctrl">whether Ctrl is required.</param>
        /// <param name="shift">whether Shift is required.</param>
        /// <param name="alt">whether Alt is required.</param>
        public SerializableComboAction(int mainKey, bool ctrl = false, bool shift = false, bool alt = false)
        {
            this.ActionType = ComboActionType.KeyPress;
            this.KeyCombination = new SerializableKeyCombination(mainKey, ctrl, shift, alt);
            this.DisplayName = "Key Press";
            this.HoldDuration = 1.0f;
        }

        /// <summary>Gets or sets the action type.</summary>
        public ComboActionType ActionType { get; set; } = ComboActionType.KeyPress;

        /// <summary>Gets or sets the key combination (for key actions).</summary>
        public SerializableKeyCombination KeyCombination { get; set; } = new SerializableKeyCombination();

        /// <summary>Gets or sets the display name.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Gets or sets the hold duration (seconds) for press-and-hold actions.</summary>
        public float HoldDuration { get; set; } = 1.0f;
    }

    /// <summary>A serializable per-rarity skill combo.</summary>
    public class SerializableSkillCombo
    {
        /// <summary>Initializes a new instance of the <see cref="SerializableSkillCombo" /> class.</summary>
        public SerializableSkillCombo()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="SerializableSkillCombo" /> class.</summary>
        /// <param name="name">combo name.</param>
        /// <param name="rarity">target rarity (stored as int).</param>
        public SerializableSkillCombo(string name, int rarity)
        {
            this.Name = name;
            this.TargetRarity = rarity;
        }

        /// <summary>Gets or sets the combo name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the ordered combo actions.</summary>
        public List<SerializableComboAction> Actions { get; set; } = new();

        /// <summary>Gets or sets the per-action delays (seconds).</summary>
        public List<float> Delays { get; set; } = new();

        /// <summary>Gets or sets the target rarity (stored as int for serialization).</summary>
        public int TargetRarity { get; set; }

        /// <summary>Gets or sets a value indicating whether the combo is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the cooldown (seconds) between combo uses.</summary>
        public float ComboCooldown { get; set; } = 5.0f;

        /// <summary>Gets or sets a value indicating whether the combo runs once per target.</summary>
        public bool OneTimePerTarget { get; set; }

        /// <summary>Gets or sets a value indicating whether this is a buff/weapon-swap combo.</summary>
        public bool IsBuffCombo { get; set; }

        /// <summary>Gets or sets the global cooldown (seconds) for buff combos.</summary>
        public float BuffCooldown { get; set; } = 30.0f;

        /// <summary>Gets or sets the legacy skill list (kept for old-config migration).</summary>
        public List<SerializableKeyCombination> Skills { get; set; } = new();
    }

    /// <summary>
    ///     Persisted AutoAim settings. Scalar options are public fields so they can
    ///     be edited in-place by ImGui widgets (which require <c>ref</c>). Newtonsoft
    ///     serializes public fields by default.
    /// </summary>
    public sealed class AutoAimSettings : IPluginSettings
    {
        /// <summary>Whether auto-aim is enabled.</summary>
        public bool EnableAutoAim;

        /// <summary>Legacy toggle key (mirrors <see cref="ToggleKeyCombination" />).</summary>
        public int ToggleKey = 114; // F3

        /// <summary>Mouse movement speed multiplier.</summary>
        public float MouseSpeed = 1.0f;

        /// <summary>Whether smooth mouse movement is used.</summary>
        public bool SmoothMovement = true;

        /// <summary>Whether the walkable grid is drawn.</summary>
        public bool ShowWalkableGrid;

        /// <summary>Whether line-of-sight checks are visualized.</summary>
        public bool ShowLineOfSight;

        /// <summary>Whether line-of-sight filtering is enabled.</summary>
        public bool EnableLineOfSight = true;

        /// <summary>Whether the targeting range circle is drawn.</summary>
        public bool ShowRangeCircle;

        /// <summary>Whether target lines are drawn.</summary>
        public bool ShowTargetLines;

        /// <summary>Whether the debug window is shown.</summary>
        public bool ShowDebugWindow;

        /// <summary>Whether a beep plays when toggling.</summary>
        public bool BeepSound = true;

        /// <summary>Targeting range (grid units).</summary>
        public float TargetingRange = 90f;

        /// <summary>Raycast/visualization range.</summary>
        public float RayCastRange = 60f;

        /// <summary>Whether Normal monsters are targeted.</summary>
        public bool TargetNormal = true;

        /// <summary>Whether Magic monsters are targeted.</summary>
        public bool TargetMagic = true;

        /// <summary>Whether Rare monsters are targeted.</summary>
        public bool TargetRare = true;

        /// <summary>Whether Unique monsters are targeted.</summary>
        public bool TargetUnique = true;

        /// <summary>Target-priority mode.</summary>
        public TargetPriority TargetPriority = TargetPriority.Closest;

        /// <summary>Delay (ms) before switching to a new target.</summary>
        public float TargetSwitchDelay = 100f;

        /// <summary>Whether the closest target is preferred.</summary>
        public bool PreferClosest = true;

        // Auto-Skill

        /// <summary>Whether auto-skill is enabled.</summary>
        public bool EnableAutoSkill;

        /// <summary>Legacy auto-skill key (mirrors <see cref="AutoSkillKeyCombination" />).</summary>
        public int AutoSkillKey = 81; // Q

        /// <summary>Auto-skill range.</summary>
        public float AutoSkillRange = 50f;

        /// <summary>Auto-skill cooldown (seconds).</summary>
        public float AutoSkillCooldown = 1.0f;

        /// <summary>Whether the auto-skill key is held vs press/release.</summary>
        public bool AutoSkillHoldKey;

        /// <summary>Whether auto-skill only fires while targeting.</summary>
        public bool AutoSkillOnlyInCombat = true;

        /// <summary>Key hold duration (ms) in press/release mode.</summary>
        public int AutoSkillKeyHoldDuration = 100;

        /// <summary>Whether the auto-skill range circle is shown.</summary>
        public bool ShowAutoSkillRange;

        // Culling Strike

        /// <summary>Whether culling strike is enabled.</summary>
        public bool EnableCullingStrike;

        /// <summary>Legacy culling key (mirrors <see cref="CullingStrikeKeyCombination" />).</summary>
        public int CullingStrikeKey = 88; // X

        /// <summary>Culling strike range.</summary>
        public float CullingStrikeRange = 60f;

        /// <summary>Whether culling only fires while targeting.</summary>
        public bool CullingStrikeOnlyInCombat = true;

        /// <summary>Whether the culling range circle is shown.</summary>
        public bool ShowCullingStrikeRange;

        /// <summary>Culling health thresholds per rarity (Normal, Magic, Rare, Unique).</summary>
        public float[] CullingStrikeThresholdPerRarity = { 30.0f, 20.0f, 10.0f, 5.0f };

        // Auto-Chest

        /// <summary>Whether auto-chest is enabled.</summary>
        public bool EnableAutoChest;

        /// <summary>Whether regular chests are opened.</summary>
        public bool OpenRegularChests = true;

        /// <summary>Whether strongboxes are opened.</summary>
        public bool OpenStrongboxes;

        /// <summary>Chest detection range.</summary>
        public float AutoChestRange = 60f;

        /// <summary>Whether chests are opened only when safe.</summary>
        public bool OnlyOpenWhenSafe = true;

        /// <summary>Safety-check range.</summary>
        public float SafetyCheckRange = 80f;

        /// <summary>Whether the chest range circle is shown.</summary>
        public bool ShowChestRange;

        /// <summary>Whether the safety range circle is shown.</summary>
        public bool ShowSafetyRange;

        /// <summary>Chest interaction cooldown (seconds).</summary>
        public float ChestCooldown = 0.5f;

        // Combo system

        /// <summary>Whether the combo system is enabled.</summary>
        public bool EnableComboSystem = true;

        /// <summary>Combo range.</summary>
        public float ComboRange = 70f;

        /// <summary>Whether combos only fire while the target is in range.</summary>
        public bool ComboOnlyInRange = true;

        /// <summary>Whether combo status is shown.</summary>
        public bool ShowComboStatus = true;

        /// <summary>Whether combos reset on target change.</summary>
        public bool ResetComboOnTargetChange = true;

        /// <summary>Whether mouse movement is forced even with keys held.</summary>
        public bool ForceMouseMovement;

        /// <summary>Gets or sets a value indicating whether auto-aim is enabled (alias of <see cref="EnableAutoAim" />).</summary>
        public bool IsEnabled
        {
            get => this.EnableAutoAim;
            set => this.EnableAutoAim = value;
        }

        /// <summary>Gets or sets the configured per-rarity skill combos.</summary>
        public List<SerializableSkillCombo> SkillCombos { get; set; } = new();

        /// <summary>Gets or sets the toggle key combination.</summary>
        public SerializableKeyCombination ToggleKeyCombination { get; set; } = new(114); // F3

        /// <summary>Gets or sets the auto-skill key combination.</summary>
        public SerializableKeyCombination AutoSkillKeyCombination { get; set; } = new(81); // Q

        /// <summary>Gets or sets the culling strike key combination.</summary>
        public SerializableKeyCombination CullingStrikeKeyCombination { get; set; } = new(88); // X
    }
}
