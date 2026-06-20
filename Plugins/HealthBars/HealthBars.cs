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
                    ImGui.Checkbox("Show mana rather than ES on self player", ref this.Settings.ShowManaRatherThanESOnSelf);
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
                            item.Value.Draw();
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
                            conf.Value.Draw();
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
                            item.Value.Draw();
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
            var location = this.Ctx.Render.WorldToScreen(curPos);
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
            var start = location - healthbarConfig.HalfOfScale;
            var end = location + healthbarConfig.HalfOfScale;

            ptr.AddRectFilled(start, end, Draw.Color(healthbarConfig.BackgroundColor));
            var (hb_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[0]);

            // Ward behaves like Life (only lost once HP hits 1), so fold it into the health
            // bar: a 50 Life / 50 Ward entity reads as a single 100-health pool.
            var hPercent = CombinedHealthPercent(hComp);
            ptr.AddImage(hb_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - hPercent) / 100f), Vector2.Zero, Vector2.One,
                (hPercent > this.Settings.CullingStrikeRangePerRarity[rarity] || !healthbarConfig.ShowCullStrike) ?
                Draw.Color(healthbarConfig.HealthbarColor) :
                0xFFFFFFFF);

            if (isSelf && this.Settings.ShowManaRatherThanESOnSelf)
            {
                var (es_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[1]);
                ptr.AddImage(es_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - hComp.Mana.CurrentInPercent) / 100f),
                    Vector2.Zero, Vector2.One,
                    Draw.Color(healthbarConfig.ESColor));
            }
            else
            {
                if (hComp.EnergyShield.Total > 0)
                {
                    var (es_ptr, _, _) = this.textures.GetTexture(this.textureToValidate[1]);
                    ptr.AddImage(es_ptr, start, end - (Vector2.UnitX * healthbarConfig.Scale * (100 - hComp.EnergyShield.CurrentInPercent) / 100f),
                        Vector2.Zero, Vector2.One,
                        Draw.Color(healthbarConfig.ESColor));
                }
            }

            var tmp = start - Vector2.UnitY;
            for (var i = 0; i < healthbarConfig.Graduations; i++)
            {
                tmp.X += healthbarConfig.GraduationsLocationStart;
                ptr.AddLine(tmp, tmp + healthbarConfig.GraduationsLocationEnd, 0xFF000000, this.graduationsThickness);
            }

            if (healthbarConfig.ShowText)
            {
                ptr.AddText(start - this.fontSize, Draw.Color(healthbarConfig.TextColor),
                    this.healthToHumanReadable(hComp.Health.Current + hComp.Ward.Current + hComp.EnergyShield.Current));
            }
        }

        private void UpdateOncePerDraw()
        {
            this.graduationsThickness = ImGui.GetFontSize() / 9f;
            this.fontSize = new(0f, ImGui.GetFontSize());
        }

        private static int CombinedHealthPercent(ILife life)
        {
            // Combine Health and Ward into a single pool (mirrors VitalStruct.CurrentInPercent,
            // using Unreserved so reserved Health is excluded just like the plain health bar).
            var total = life.Health.Unreserved + life.Ward.Unreserved;
            if (total <= 0)
            {
                return 0;
            }

            return (int)Math.Round(100d * (life.Health.Current + life.Ward.Current) / total);
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