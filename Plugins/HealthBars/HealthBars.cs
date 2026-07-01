// <copyright file="HealthBars.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace HealthBars
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     <see cref="HealthBars" /> plugin. Built entirely on the ExileBridge SDK.
    ///     Rendering ported from MordWraith's SimpleBars (circle-dot mode, ES-above / mana-below
    ///     bars, gradient-or-solid fills, borders, text background, per-bar scales, graduations).
    /// </summary>
    public sealed class HealthBars : Plugin<HealthBarsSettings>
    {
        private readonly List<string> textureToValidate = new()
        {
            "full_bar.png",
            "hollow_bar.png"
        };

        private int poiMonsterConfigToDelete = 0;
        private int poiMonsterConfigToAdd = 0;
        private float graduationsThickness = 0f;
        private Vector2 fontSize = Vector2.Zero;

        private string SettingPathname => Path.Join(this.DirectoryPath, "config", "settings.txt");

        private string TexturesPath => Path.Join(this.DirectoryPath, "Textures");

        private readonly TextureLoader textures = new();

        private readonly Dictionary<uint, Vector2> bPositions = new();

        private IDisposable? onAreaChange = null;

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Text("Turn off in game health bars for best result.");
            ImGui.Text("Enable/Disable plugin to reload textures.");
            ImGui.Text($"Total Textures loaded: {this.textures.TotalTexturesLoaded}");
            if (ImGui.CollapsingHeader("Common Configuration"))
            {
                if (ImGui.BeginTable("common_config_table", 2))
                {
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Draw healthbars in town", ref this.Settings.DrawInTown);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Draw healthbars in hideout", ref this.Settings.DrawInHideout);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Draw healthbars when game is in background", ref this.Settings.DrawWhenGameInBackground);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Interpolate position", ref this.Settings.InterpolatePosition);
                    Draw.ToolTip("Enable this if your healthbar is stuttering.");
                    if (this.Settings.InterpolatePosition)
                    {
                        if (ImGui.DragInt("Interpolation Rate", ref this.Settings.InterpolationRate, 1f, 1, 1000))
                        {
                            if (this.Settings.InterpolationRate <= 0)
                            {
                                this.Settings.InterpolationRate = 1;
                            }
                            else if (this.Settings.InterpolationRate >= 1000)
                            {
                                this.Settings.InterpolationRate = 1000;
                            }
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text("white       magic      rare         unique");
                    ImGui.DragInt4("Cull Strike (%health)", ref this.Settings.CullingStrikeRangePerRarity[0], 1, 0, 100);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox("Use gradient textures globally", ref this.Settings.UseGradientBarsGlobal);
                    ImGui.EndTable();
                }
            }

            if (ImGui.CollapsingHeader("Monster Configuration"))
            {
                if (ImGui.BeginTabBar("monster_config"))
                {
                    foreach (var item in this.Settings.Monster)
                    {
                        if (ImGui.BeginTabItem(item.Key))
                        {
                            item.Value.Draw(false, this.textures, this.Settings.UseGradientBarsGlobal);
                            ImGui.EndTabItem();
                        }
                    }

                    ImGui.EndTabBar();
                }
            }

            if (ImGui.CollapsingHeader("POI Configuration"))
            {
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                if(ImGui.InputInt("Group Number##poimonsterconfig", ref this.poiMonsterConfigToAdd) && this.poiMonsterConfigToAdd < 0)
                {
                    this.poiMonsterConfigToAdd = 0;
                }

                ImGui.SameLine();
                if (ImGui.Button("Add"))
                {
                    this.Settings.POIMonster.TryAdd(this.poiMonsterConfigToAdd, new());
                }

                if (ImGui.BeginTabBar("poimonster_config", ImGuiTabBarFlags.AutoSelectNewTabs))
                {
                    foreach (var conf in this.Settings.POIMonster)
                    {
                        var text = conf.Key < 0 ? "Default" : $"Group {conf.Key}";
                        var shouldNotDelete = true;
                        if (ImGui.BeginTabItem(text, ref shouldNotDelete, ImGuiTabItemFlags.NoAssumedClosure))
                        {
                            conf.Value.Draw(false, this.textures, this.Settings.UseGradientBarsGlobal);
                            ImGui.EndTabItem();
                        }

                        if (conf.Key >= 0 && !shouldNotDelete)
                        {
                            this.poiMonsterConfigToDelete = conf.Key;
                            ImGui.OpenPopup("POIConfigHealthbarDeleteConfirmation");
                        }
                    }

                    this.DrawConfirmationPopup();
                    ImGui.EndTabBar();
                }
            }

            if (ImGui.CollapsingHeader("Player Configuration"))
            {
                if (ImGui.BeginTabBar("player_config"))
                {
                    foreach (var item in this.Settings.Player)
                    {
                        if (ImGui.BeginTabItem(item.Key))
                        {
                            // Self tab exposes self-only controls (individual scales, mana bar, ES graduations).
                            item.Value.Draw(item.Key == "self", this.textures, this.Settings.UseGradientBarsGlobal);
                            ImGui.EndTabItem();
                        }
                    }

                    ImGui.EndTabBar();
                }
            }
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            if ((!this.Settings.DrawInTown && this.Ctx.Game.InGame.IsTown) ||
                (!this.Settings.DrawInHideout && this.Ctx.Game.InGame.IsHideout))
            {
                return;
            }

            if (!this.Ctx.Game.IsInGame)
            {
                return;
            }

            if (!this.Settings.DrawWhenGameInBackground && !this.Ctx.Game.IsForeground)
            {
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            this.UpdateOncePerDraw();
            foreach (var entity in this.Ctx.Entities.Awake)
            {
                if (!entity.IsValid || entity.State == EntityState.Useless ||
                    entity.Type == EntityType.Renderable ||
                    entity.State == EntityState.PinnacleBossHidden)
                {
                    continue;
                }

                switch (entity.Type)
                {
                    case EntityType.Player:
                        if (entity.Subtype == EntitySubtype.PlayerOther)
                        {
                            if (entity.State == EntityState.PlayerLeader)
                            {
                                this.DrawHealthbar(entity, this.Settings.Player["leader"], (int)Rarity.Rare);
                            }
                            else
                            {
                                this.DrawHealthbar(entity, this.Settings.Player["member"], (int)Rarity.Rare);
                            }
                        }
                        else
                        {
                            this.DrawHealthbar(entity, this.Settings.Player["self"], (int)Rarity.Rare, true);
                        }

                        break;
                    case EntityType.Monster:
                        if (entity.Subtype == EntitySubtype.PoiMonster)
                        {
                            if (!this.Settings.POIMonster.TryGetValue(entity.CustomGroup, out var poiConfig))
                            {
                                poiConfig = this.Settings.POIMonster[-1];
                            }

                            this.DrawHealthbar(entity, poiConfig,
                                entity.TryGetComponent<IObjectMagicProperties>(out var oComp) ?
                                (int)oComp.Rarity :
                                (int)Rarity.Rare);
                        }
                        else if (entity.State == EntityState.MonsterFriendly)
                        {
                            this.DrawHealthbar(entity, this.Settings.Monster["friendly"], (int)Rarity.Rare);
                        }
                        else if (entity.TryGetComponent<IObjectMagicProperties>(out var oComp))
                        {
                            switch (oComp.Rarity)
                            {
                                case Rarity.Normal:
                                    this.DrawHealthbar(entity, this.Settings.Monster["white"], (int)Rarity.Normal);
                                    break;
                                case Rarity.Magic:
                                    this.DrawHealthbar(entity, this.Settings.Monster["magic"], (int)Rarity.Magic);
                                    break;
                                case Rarity.Rare:
                                    this.DrawHealthbar(entity, this.Settings.Monster["rare"], (int)Rarity.Rare);
                                    break;
                                case Rarity.Unique:
                                    this.DrawHealthbar(entity, this.Settings.Monster["unique"], (int)Rarity.Unique);
                                    break;
                            }
                        }

                        break;
                }
            }
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.textures.cleanup(this.Ctx.Overlay, this.TexturesPath);
            this.onAreaChange?.Dispose();
            this.onAreaChange = null;
        }

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            this.textures.Load(this.Ctx.Overlay, this.TexturesPath);
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<HealthBarsSettings>(content) ?? new HealthBarsSettings();
            }

            for (var i = 0; i < this.textureToValidate.Count; i++)
            {
                if (!this.textures.TextureKeys.Contains(this.textureToValidate[i]))
                {
                    throw new Exception($"Missing texture file {this.textureToValidate[i]} in {this.TexturesPath} folder.");
                }
            }

            this.onAreaChange = this.Ctx.Events.OnAreaChange(() => this.bPositions.Clear());
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
            var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, settingsData);
        }

        private void DrawHealthbar(IEntity entity, Config healthbarConfig, int rarity, bool isSelf = false)
        {
            if (!healthbarConfig.Enable)
            {
                return;
            }

            if (!entity.TryGetComponent<IRender>(out var rComp))
            {
                return;
            }

            var curPos = rComp.WorldPosition;
            curPos.Z -= rComp.ModelBounds.Z + healthbarConfig.Shift.Y;
            var location = this.Ctx.Render.WorldToScreen(curPos, curPos.Z);
            location.X += healthbarConfig.Shift.X;
            if (!entity.TryGetComponent<ILife>(out var hComp))
            {
                return;
            }

            if (this.Settings.InterpolatePosition)
            {
                if (this.bPositions.TryGetValue(entity.Id, out var prevLocation))
                {
                    location = MathUtil.Lerp(prevLocation, location, this.Settings.InterpolationRate / 1000f);
                }

                this.bPositions[entity.Id] = location;
            }

            var ptr = ImGui.GetBackgroundDrawList();

            // Percentages (clamped to [0,100] floats for geometry; raw health % for the cull compare).
            var hPercent = hComp.Health.CurrentInPercent;
            float hPct = MathF.Max(0f, MathF.Min(100f, (float)hPercent));
            float esPct = MathF.Max(0f, MathF.Min(100f, (float)hComp.EnergyShield.CurrentInPercent));
            float manaPct = MathF.Max(0f, MathF.Min(100f, (float)hComp.Mana.CurrentInPercent));

            // Per-bar scales (self can override individually).
            var baseScale = (isSelf && healthbarConfig.UseIndividualBarScale) ? healthbarConfig.HealthScale : healthbarConfig.Scale;
            var baseHalf = baseScale / 2f;

            bool drawHealth = healthbarConfig.ShowHealthBar;
            bool drawES = hComp.EnergyShield.Total > 0 && healthbarConfig.ShowESBar;
            bool drawMana = isSelf && healthbarConfig.ShowManaBar && hComp.Mana.Total > 0;

            var start = location - baseHalf;
            var end = location + baseHalf;
            Vector2 textAnchor = start;

            var (hb_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[0]);
            var healthColor = (hPercent > this.Settings.CullingStrikeRangePerRarity[rarity] || !healthbarConfig.ShowCullStrike)
                ? Draw.Color(healthbarConfig.HealthbarColor) : 0xFFFFFFFF;
            bool useGradient = this.Settings.UseGradientBarsGlobal || healthbarConfig.UseGradientBars;

            // ===== Circle Dot Rendering Mode =====
            if (healthbarConfig.UseCircleDot)
            {
                var dlc = ImGui.GetBackgroundDrawList();
                float baseR = healthbarConfig.CircleRadius > 0 ? healthbarConfig.CircleRadius : MathF.Max(6f, baseScale.Y);
                float radius = baseR * healthbarConfig.CircleScale;
                float arcThick = MathF.Max(1f, healthbarConfig.CircleArcThickness * healthbarConfig.CircleScale);
                float borderThicknessCircle = MathF.Max(1f, ImGui.GetFontSize() / 12f) * healthbarConfig.BorderThicknessScale;
                if (healthbarConfig.CircleBackgroundRadius > 0f)
                {
                    dlc.AddCircleFilled(location, healthbarConfig.CircleBackgroundRadius, Draw.Color(healthbarConfig.CircleBackgroundColor));
                }

                dlc.AddCircleFilled(location, radius, Draw.Color(healthbarConfig.BackgroundColor));
                dlc.AddCircle(location, radius, 0xFF000000, 64, borderThicknessCircle);

                if (drawHealth)
                {
                    float angle = (MathF.PI * 2f) * (hPct / 100f);
                    float startAng = -MathF.PI / 2f;
                    dlc.PathClear();
                    dlc.PathArcTo(location, radius, startAng, startAng + angle, 64);
                    dlc.PathLineTo(location);
                    dlc.PathFillConvex(healthColor);
                }

                if (drawES)
                {
                    float angStart = -MathF.PI; // left
                    float angle = MathF.PI * (esPct / 100f);
                    float rArc = radius + MathF.Max(0f, healthbarConfig.CircleArcOffset) + (arcThick / 2f);
                    dlc.PathClear();
                    dlc.PathArcTo(location, rArc, angStart, angStart + angle, 32);
                    dlc.PathStroke(Draw.Color(healthbarConfig.ESColor), ImDrawFlags.None, arcThick);
                }

                if (drawMana)
                {
                    float angStart = 0f; // right
                    float angle = MathF.PI * (manaPct / 100f);
                    float rArc = radius + MathF.Max(0f, healthbarConfig.CircleArcOffset) + (arcThick / 2f);
                    dlc.PathClear();
                    dlc.PathArcTo(location, rArc, angStart, angStart + angle, 32);
                    dlc.PathStroke(Draw.Color(healthbarConfig.ManaColor), ImDrawFlags.None, arcThick);
                }

                textAnchor = location - new Vector2(radius, radius);
                goto AfterBars;
            }

            if (drawHealth)
            {
                ptr.AddRectFilled(start, end, Draw.Color(healthbarConfig.BackgroundColor));
                if (useGradient)
                {
                    ptr.AddImage(hb_ptr, start, end - (Vector2.UnitX * baseScale * (100f - hPct) / 100f), Vector2.Zero, Vector2.One, healthColor);
                }
                else
                {
                    var hFillEnd = start + new Vector2(baseScale.X * (hPct / 100f), baseScale.Y);
                    ptr.AddRectFilled(start, hFillEnd, healthColor);
                }
            }

            float pad = 1f;
            float thickness = MathF.Max(1f, ImGui.GetFontSize() / 12f);

            // Precompute ES bar geometry (needed for drawing and graduations).
            var esScale = (isSelf && healthbarConfig.UseIndividualBarScale) ? healthbarConfig.ESScale : healthbarConfig.Scale;
            var esHalf = esScale / 2f;
            var esCenter = drawHealth
                ? location - new Vector2(0f, (baseScale.Y * 0.5f) + healthbarConfig.BarGap + (esScale.Y * 0.5f))
                : (drawMana
                    ? location - new Vector2(0f, (esScale.Y + 0f) * 0.5f)
                    : location);
            Vector2 esStart = esCenter - esHalf;
            Vector2 esEnd = esCenter + esHalf;

            // ===== ES ABOVE =====
            if (drawES)
            {
                var (esTex, _, _) = this.textures.GetTexture(this.textureToValidate[0]); // full bar
                textAnchor = esStart;

                if (useGradient)
                {
                    ptr.AddImage(
                        esTex,
                        esStart,
                        esEnd - (Vector2.UnitX * esScale * (100f - esPct) / 100f),
                        Vector2.Zero, Vector2.One,
                        Draw.Color(healthbarConfig.ESColor));
                }
                else
                {
                    var esFillEnd = esStart + new Vector2(esScale.X * (esPct / 100f), esScale.Y);
                    ptr.AddRectFilled(esStart, esFillEnd, Draw.Color(healthbarConfig.ESColor));
                }

                ptr.AddRect(esStart - new Vector2(pad, pad), esEnd + new Vector2(pad, pad), 0xFF000000, 0f, ImDrawFlags.None, thickness * healthbarConfig.BorderThicknessScale);
            }

            // ===== MANA BELOW =====
            if (drawMana)
            {
                var (manaTex, _, _) = this.textures.GetTexture(this.textureToValidate[0]); // full bar
                float gap = healthbarConfig.BarGap;
                var manaScale = (isSelf && healthbarConfig.UseIndividualBarScale) ? healthbarConfig.ManaScale : healthbarConfig.Scale;
                var manaHalf = manaScale / 2f;
                var manaCenter = drawHealth
                    ? location + new Vector2(0f, (baseScale.Y * 0.5f) + gap + (manaScale.Y * 0.5f))
                    : (drawES
                        ? location + new Vector2(0f, (manaScale.Y + 0f) * 0.5f)
                        : location);
                Vector2 mStart = manaCenter - manaHalf;
                Vector2 mEnd = manaCenter + manaHalf;

                if (useGradient)
                {
                    ptr.AddImage(
                        manaTex,
                        mStart,
                        mEnd - (Vector2.UnitX * manaScale * (100f - manaPct) / 100f),
                        Vector2.Zero, Vector2.One,
                        Draw.Color(healthbarConfig.ManaColor));
                }
                else
                {
                    var mFillEnd = mStart + new Vector2(manaScale.X * (manaPct / 100f), manaScale.Y);
                    ptr.AddRectFilled(mStart, mFillEnd, Draw.Color(healthbarConfig.ManaColor));
                }

                ptr.AddRect(mStart - new Vector2(pad, pad), mEnd + new Vector2(pad, pad), 0xFF000000, 0f, ImDrawFlags.None, thickness * healthbarConfig.BorderThicknessScale);
            }

            var tmp = start - Vector2.UnitY;

            // Black border around health bar.
            float borderPad = 1f;
            float borderThickness = MathF.Max(1f, ImGui.GetFontSize() / 12f) * healthbarConfig.BorderThicknessScale;
            if (drawHealth)
            {
                ptr.AddRect(
                    start - new Vector2(borderPad, borderPad),
                    end + new Vector2(borderPad, borderPad),
                    0xFF000000,
                    0f,
                    ImDrawFlags.None,
                    borderThickness);
            }

            // HP Graduations
            float gradStep = (isSelf && healthbarConfig.UseIndividualBarScale) ? (baseScale.X / (healthbarConfig.Graduations + 1f)) : healthbarConfig.GraduationsLocationStart;
            Vector2 gradEnd = (isSelf && healthbarConfig.UseIndividualBarScale) ? (Vector2.UnitY * baseScale.Y) : healthbarConfig.GraduationsLocationEnd;
            if (drawHealth && healthbarConfig.ShowHPGraduations)
            {
                for (var i = 0; i < healthbarConfig.Graduations; i++)
                {
                    tmp.X += gradStep;
                    ptr.AddLine(tmp, tmp + gradEnd, 0xFF000000, this.graduationsThickness);
                }
            }

            // ES Graduations (self only)
            if (isSelf && drawES && healthbarConfig.ShowESGraduations && healthbarConfig.ESGraduations > 0)
            {
                float esGradStep = esScale.X / (healthbarConfig.ESGraduations + 1f);
                Vector2 esGradEnd = Vector2.UnitY * esScale.Y;
                var esGradTmp = esStart - Vector2.UnitY;
                for (var i = 0; i < healthbarConfig.ESGraduations; i++)
                {
                    esGradTmp.X += esGradStep;
                    ptr.AddLine(esGradTmp, esGradTmp + esGradEnd, 0xFF000000, this.graduationsThickness);
                }
            }

AfterBars:
            if (healthbarConfig.ShowText)
            {
                var text = this.healthToHumanReadable(hComp.Health.Current + hComp.EnergyShield.Current);
                var size = ImGui.CalcTextSize(text);
                var tPad = new Vector2(2f, 2f);
                float safeGap = 1f;
                var bgTopLeft = new Vector2(textAnchor.X, textAnchor.Y - (size.Y + (tPad.Y * 2f) + safeGap));
                var bgBottomRight = new Vector2(textAnchor.X + (size.X + (tPad.X * 2f)), textAnchor.Y - safeGap);
                var drawPos = bgTopLeft + tPad;
                if (healthbarConfig.ShowTextBackground)
                {
                    ptr.AddRectFilled(bgTopLeft, bgBottomRight, Draw.Color(healthbarConfig.TextBackgroundColor));
                }

                ptr.AddText(drawPos, Draw.Color(healthbarConfig.TextColor), text);
            }
        }

        private void UpdateOncePerDraw()
        {
            this.graduationsThickness = ImGui.GetFontSize() / 9f;
            this.fontSize = new(0f, ImGui.GetFontSize());
        }

        private string healthToHumanReadable(int value)
        {
            if (value >= 100000)
            {
                return $"{(value / 1000000f):0.00}M";
            }
            else if (value >= 100)
            {
                return $"{(value / 1000f):0.00}K";
            }
            else
            {
                return $"{value}";
            }
        }

        private void DrawConfirmationPopup()
        {
            ImGui.SetNextWindowPos(new Vector2(this.Ctx.Overlay.Size.X / 3f, this.Ctx.Overlay.Size.Y / 3f));
            if (ImGui.BeginPopup("POIConfigHealthbarDeleteConfirmation"))
            {
                ImGui.Text($"Do you want to delete group {this.poiMonsterConfigToDelete} POI Monster healthbar config?");
                ImGui.Separator();
                if (ImGui.Button("Yes",
                    new Vector2(ImGui.GetContentRegionAvail().X / 2f, ImGui.GetTextLineHeight() * 2)))
                {
                    _ = this.Settings.POIMonster.Remove(poiMonsterConfigToDelete);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 2)))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }
    }
}
