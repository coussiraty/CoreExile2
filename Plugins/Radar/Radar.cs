// <copyright file="Radar.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Radar
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Threading;
    using System.Threading.Tasks;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;
    using SixLabors.ImageSharp.Processing.Processors.Transforms;
    using SixLabors.ImageSharp.Processing;

    /// <summary>
    /// <see cref="Radar"/> plugin.
    /// </summary>
    public sealed class Radar : Plugin<RadarSettings>
    {
        private const string TempleTgtPrefix = "Metadata/Terrain/Leagues/Incursion/Tiles/Features/Waygates/WaygateDevice";

        // All campaign rune terrain tiles (e.g. GrimTangle_Runestones, and other
        // terrain variants) live under this folder; matching the prefix combines them all.
        private const string RunestoneTgtPrefix = "Metadata/Terrain/Leagues/Expedition/Tiles/CampaignRunes/";

        private readonly string delveChestStarting = "Metadata/Chests/DelveChests/";
        private readonly Dictionary<uint, string> delveChestCache = new();

        /// <summary>
        /// If we don't do this, user will be asked to
        /// setup the culling window everytime they open the game.
        /// </summary>
        private bool skipOneSettingChange = false;
        private bool isAddNewPOIHeaderOpened = false;
        private IDisposable? onMove;
        private IDisposable? onForegroundChange;
        private IDisposable? onGameClose;
        private IDisposable? onAreaChange;

        private string currentAreaName = string.Empty;
        private string tmpTileName = string.Empty;
        private string tmpDisplayName = string.Empty;
        private int tmpTgtSelectionCounter = 0;
        private string tmpTileFilter = string.Empty;
        private bool addTileForAllAreas = false;

        private double miniMapDiagonalLength = 0x00;

        private double largeMapDiagonalLength = 0x00;

        private IntPtr walkableMapTexture = IntPtr.Zero;
        private Vector2 walkableMapDimension = Vector2.Zero;
        private readonly Dictionary<string, Vector2> textHalfSizeCache = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Vector2> poiIndexHalfSizeCache = new();

        // Pathfinding: cache computed paths and throttle recomputation
        private long nextPoiRecomputeTime = 0;
        private long nextPoiFullRecomputeTime = 0;
        private Dictionary<string, List<Vector2>?> poiPathCache = new();
        private Task? pendingPathTask = null;

        // Entity pathfinding: cache and throttle for entity-icon-based paths
        private long nextEntityRecomputeTime = 0;
        private long nextEntityFullRecomputeTime = 0;
        private Dictionary<uint, List<Vector2>?> entityPathCache = new();
        private Task? pendingEntityPathTask = null;
        private readonly List<(uint entityId, Vector2 gridPos, Vector4 color)> entityPathSnapshot = new();
        private readonly List<(string cacheKey, Vector2 gridPos, Vector4 color)> tileIconPathSnapshot = new();
        private Dictionary<string, List<Vector2>?> tileIconPathCache = new();

        // Path targets the player has gotten close to, remembered per map instance.
        // Keyed by AreaHash (stable across leaving/returning to the same instance,
        // differs for a freshly-generated instance), then by the same cache keys used
        // by the path caches (entity|<id>, tile|..., area|..., common|...).
        // reachedPathKeys points at the active instance's set. Render-thread only.
        private readonly Dictionary<string, HashSet<string>> reachedPathKeysByArea = new();
        private HashSet<string> reachedPathKeys = new();

        // Abyss cracks/pit remembered per map instance (keyed by AreaHash, then by a stable
        // position+type key). Fed by both the awake-entity pass and a throttled background scan
        // of the game's larger-range SleepingEntities map, so the abyss line persists once seen.
        // Static "tracked" map objects (Abyss cracks/pit, specific Strongboxes) remembered per map
        // instance. Fed by the awake-entity pass and a throttled scan of the game's larger-range
        // SleepingEntities map, so they appear well beyond the network bubble and persist once seen.
        private const int TrackedScanIntervalMs = 1000;
        private readonly Dictionary<string, ConcurrentDictionary<string, (Vector2 gridPos, float height, string category, string iconKey)>> trackedNodesByArea = new();
        private ConcurrentDictionary<string, (Vector2 gridPos, float height, string category, string iconKey)> trackedNodes = new();
        private long nextTrackedScanTime;
        private Task? pendingTrackedScanTask;

        private string SettingPathname => Path.Join(this.DirectoryPath, "config", "settings.txt");

        private string ImportantTgtPathName => Path.Join(this.DirectoryPath, "important_tgt_files.txt");

        private string BossArenaTgtPathName => Path.Join(this.DirectoryPath, "boss_arena_tgt_files.txt");

        private string StairsTgtPathName => Path.Join(this.DirectoryPath, "stairs_tgt_files.txt");

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.TextWrapped("If your mini/large map icon are not working/visible. Open this " +
                "setting window, click anywhere on it and then hide this setting window. It will fix the issue.");
            ImGui.DragFloat("Large Map Fix", ref this.Settings.LargeMapScaleMultiplier, 0.001f, 0.1f, 2.0f);
            Draw.ToolTip("This slider is for fixing large map (icons) offset. " +
                "You have to use it if you feel that LargeMap Icons " +
                "are moving while your player is moving. You only have " +
                "to find a value that works for you per game window resolution. " +
                "Basically, you don't have to change it unless you change your " +
                "game window resolution. Also, please contribute back, let me know " +
                "what resolution you use and what value works best for you. " +
                "This slider has no impact on mini-map icons. For windowed-full-screen " +
                "default value should be good enough. If you want to add precise value " +
                "(e.g. 0.137345) press CTRL + LMB");
            ImGui.DragFloat("Large Map X Offset", ref this.Settings.LargeMapXOffset, 0.1f);
            Draw.ToolTip("Adjusts only the large map overlay horizontally. Negative moves it left, positive moves it right.");
            ImGui.DragFloat("Large Map Y Offset", ref this.Settings.LargeMapYOffset, 0.1f);
            Draw.ToolTip("Adjusts only the large map overlay vertically. Negative moves it up, positive moves it down.");
            ImGui.DragFloat("Mini Map X Offset", ref this.Settings.MiniMapXOffset, 0.1f);
            Draw.ToolTip("Adjusts only the mini-map overlay horizontally. Negative moves it left, positive moves it right.");
            ImGui.DragFloat("Mini Map Zoom", ref this.Settings.MiniMapZoomMultiplier, 0.001f, 0.01f, 3f, "%.3f");
            Draw.ToolTip("Controls how far mini-map icons sit from your character (the mini-map's effective zoom).");

            ImGui.Checkbox("Auto-Detect Local Co-op Mode", ref this.Settings.AutoDetectCoopMode);
            Draw.ToolTip("Automatically detects when you are playing local co-op in controller mode and adjusts map centering.");
            if (!this.Settings.AutoDetectCoopMode)
            {
                ImGui.Checkbox("Enable Local Co-op Map Hack Centering", ref this.Settings.EnableCoopMode);
                Draw.ToolTip("Centers the map/maphack on the midpoint of P1 and P2 when playing co-op.");
            }

            ImGui.Checkbox("Hide Radar when in Hideout/Town", ref this.Settings.DrawWhenNotInHideoutOrTown);
            ImGui.Checkbox("Hide Radar when game is in the background", ref this.Settings.DrawWhenForeground);
            ImGui.Checkbox("Hide Radar when game is paused", ref this.Settings.DrawWhenNotPaused);
            if (ImGui.Checkbox("Modify Large Map Culling Window", ref this.Settings.ModifyCullWindow))
            {
                if (this.Settings.ModifyCullWindow)
                {
                    this.Settings.MakeCullWindowFullScreen = false;
                }
            }

            ImGui.TreePush("radar_culling_window");
            if (ImGui.Checkbox("Make Culling Window Cover Whole Game", ref this.Settings.MakeCullWindowFullScreen))
            {
                this.Settings.ModifyCullWindow = !this.Settings.MakeCullWindowFullScreen;
                this.Settings.CullWindowPos = Vector2.Zero;
                this.Settings.CullWindowSize.X = this.Ctx.Game.WindowArea.Width;
                this.Settings.CullWindowSize.Y = this.Ctx.Game.WindowArea.Height;
            }

            if (ImGui.TreeNode("Culling window advance options"))
            {
                ImGui.Checkbox("Draw maphack in culling window", ref this.Settings.DrawMapInCull);
                ImGui.Checkbox("Draw POIs in culling window", ref this.Settings.DrawPOIInCull);
                ImGui.TreePop();
            }

            ImGui.TreePop();
            ImGui.Separator();
            ImGui.NewLine();
            if (ImGui.Checkbox("Draw Area/Zone Map (maphack)", ref this.Settings.DrawWalkableMap))
            {
                if (this.Settings.DrawWalkableMap)
                {
                    if (this.walkableMapTexture == IntPtr.Zero)
                    {
                        this.ReloadMapTexture();
                    }
                }
                else
                {
                    this.RemoveMapTexture();
                }
            }

            if (ImGui.ColorEdit4("Drawn Map Color", ref this.Settings.WalkableMapColor))
            {
                if (this.walkableMapTexture != IntPtr.Zero)
                {
                    this.ReloadMapTexture();
                }
            }

            ImGui.Separator();
            ImGui.NewLine();
            ImGui.Checkbox("Show terrain points of interest (A.K.A Terrain POI)", ref this.Settings.ShowImportantPOI);
            ImGui.ColorEdit4("Terrain POI text color", ref this.Settings.POIColor);
            ImGui.Checkbox("Add black background to Terrain POI text", ref this.Settings.EnablePOIBackground);
            ImGui.Checkbox("Show Straight-Line Arrow to POIs", ref this.Settings.ShowStraightLine);
            Draw.ToolTip("Draws a straight line+arrow to each POI. Green = clear, Red = blocked.");
            ImGui.Checkbox("Show A* Smooth Path to POIs", ref this.Settings.ShowSmoothPath);
            Draw.ToolTip("Computes and draws the actual shortest walkable path (cyan).");
            ImGui.DragFloat("POI Path Thickness", ref this.Settings.DirectionLineThickness, 0.1f, 0.1f, 10.0f, "%.1f");
            ImGui.DragInt("Path Recompute Segments", ref this.Settings.PathRecomputeSegments, 0.1f, 0, 20);
            Draw.ToolTip("0 = full recompute every cycle. Set to 3-5 to only recompute the first few segments of a cached path, reusing the tail. Higher values = faster but paths may be slightly stale when the player moves.");
            ImGui.DragInt("Path Recompute Interval (ms)", ref this.Settings.PathRecomputeIntervalMs, 1f, 5, 1000);
            Draw.ToolTip("How often paths are recomputed. Lower = more responsive, higher = less CPU usage.");
            ImGui.DragInt("Full Recompute Interval (ms)", ref this.Settings.PathFullRecomputeIntervalMs, 100f, 1000, 10000);
            Draw.ToolTip("How often a full path recompute is forced, ignoring the segment-skip optimization. Ensures paths never get stuck stale.");
            this.isAddNewPOIHeaderOpened = ImGui.CollapsingHeader("Add or Modify Terrain POI");
            if (this.isAddNewPOIHeaderOpened)
            {
                this.AddNewPOIWidget();
                this.ShowPOIWidget();
            }

            ImGui.Separator();
            ImGui.NewLine();
            ImGui.Checkbox("Hide Entities outside the network bubble", ref this.Settings.HideOutsideNetworkBubble);
            ImGui.Checkbox("Show Player Names", ref this.Settings.ShowPlayersNames);
            Draw.ToolTip("This button will not work while Player is in the Scourge.");
            ImGui.Checkbox("Show Paths to Icons", ref this.Settings.ShowEntityPaths);
            Draw.ToolTip("Global on/off for entity-icon pathing. Does not affect individual icon path settings.");
            ImGui.DragFloat("Icon Path Thickness", ref this.Settings.IconPathThickness, 0.1f, 0.1f, 10.0f, "%.1f");
            ImGui.Checkbox("Hide reached paths for current map", ref this.Settings.HideReachedPaths);
            Draw.ToolTip("When you get close to a path target (entity, terrain POI or tile), its path stops being drawn for the rest of the current map. Resets automatically on area change.");
            ImGui.Checkbox("Hide Runestone socket count when near", ref this.Settings.HideRunestoneSocketsWhenNear);
            Draw.ToolTip("When you get close to a Runestone Encounter, its socket-count label disappears for the rest of the map. Uses the Reached Distance below. Independent of 'Hide reached paths'.");
            ImGui.DragFloat("Runestone Socket X Offset", ref this.Settings.RunestoneSocketOffsetX, 0.5f);
            ImGui.DragFloat("Runestone Socket Y Offset", ref this.Settings.RunestoneSocketOffsetY, 0.5f);
            Draw.ToolTip("Screen-pixel offset for the Runestone socket-count number.");
            if (this.Settings.HideReachedPaths || this.Settings.HideRunestoneSocketsWhenNear)
            {
                ImGui.DragFloat("Reached Distance", ref this.Settings.ReachedPathDistance, 1f, 1f, 500f, "%.0f");
                Draw.ToolTip("Grid distance at which a path target / runestone counts as reached.");
            }

            if (ImGui.Button("Reset Reached Paths"))
            {
                this.reachedPathKeys.Clear();
            }

            Draw.ToolTip("Show all paths and runestone socket counts for the current map again.");
            if (ImGui.CollapsingHeader("Icons Setting"))
            {
                this.Settings.DrawIconsSettingToImGui(
                    "BaseGame Icons",
                    this.Settings.BaseIcons,
                    "Blockages icon can be set from Delve Icons category i.e. 'Blockage OR DelveWall'");

                this.Settings.DrawPOIMonsterSettingToImGui(this.DirectoryPath);
                this.Settings.OtherImportantObjectsSettingToImGui(this.DirectoryPath);
                this.Settings.DrawIconsSettingToImGui(
                    "Breach Icons",
                    this.Settings.BreachIcons,
                    "Breach bosses are same as BaseGame Icons -> Unique Monsters.");

                this.Settings.DrawIconsSettingToImGui(
                    "Delirium Icons",
                    this.Settings.DeliriumIcons,
                    string.Empty);

                this.Settings.DrawIconsSettingToImGui(
                    "Expedition Icons",
                    this.Settings.ExpeditionIcons,
                    string.Empty);

                this.Settings.DrawIconsSettingToImGui(
                    "Temple Icons",
                    this.Settings.TempleIcons,
                    "Icons for Incursion Waygate devices (Vaal Ruins).");

                this.Settings.DrawIconsSettingToImGui(
                    "Expedition Marker Icons",
                    this.Settings.ExpeditionMarkerIcons,
                    "Icons for expedition markers, keyed by MinimapIcon name. Set size to 0 to disable.");

                this.Settings.DrawIconsSettingToImGui(
                    "Expedition Remnant Icons",
                    this.Settings.ExpeditionRemnantIcons,
                    "Icons for expedition remnants with specific mods. Set size to 0 to disable.");

                this.Settings.DrawIconsSettingToImGui(
                    "Runestone Icons",
                    this.Settings.RunestoneIcons,
                    "Icons for runestone encounters.");

                this.Settings.DrawIconsSettingToImGui(
                    "Ritual Icons",
                    this.Settings.RitualIcons,
                    "Icon for Ritual rune objects.");

                this.Settings.DrawIconsSettingToImGui(
                    "Abyss Icons",
                    this.Settings.AbyssIcons,
                    "Icons for Abyss nodes. 'Other' matches any remaining Abyss-path entity.");

                this.Settings.DrawIconsSettingToImGui(
                    "Strongboxes",
                    this.Settings.StrongboxIcons,
                    "Icons for specific Strongbox chests, matched by entity path.");

                this.Settings.DrawIconsSettingToImGui(
                    "Sekhemas",
                    this.Settings.SekhemasIcons,
                    "Icons for Sanctum (Trial of the Sekhemas) objects.");

                this.Settings.DrawIconsSettingToImGui(
                    "Boss Icons",
                    this.Settings.BossIcons,
                    "Icons for map boss arenas.");
            }
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            var largeMap = this.Ctx.Ui.LargeMap;
            var miniMap = this.Ctx.Ui.MiniMap;
            if (this.Settings.ModifyCullWindow)
            {
                ImGui.SetNextWindowPos(largeMap.Center, ImGuiCond.Appearing);
                ImGui.SetNextWindowSize(new Vector2(400f), ImGuiCond.Appearing);
                ImGui.Begin("Large Map Culling Window");
                ImGui.TextWrapped("This is a culling window for the large map icons. " +
                                  "Any large map icons outside of this window will be hidden automatically. " +
                                  "Feel free to change the position/size of this window. " +
                                  "Once you are happy with the dimensions, double click this window. " +
                                  "You can bring this window back from the settings menu.");
                this.Settings.CullWindowPos = ImGui.GetWindowPos();
                this.Settings.CullWindowSize = ImGui.GetWindowSize();
                if (ImGui.IsWindowHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    this.Settings.ModifyCullWindow = false;
                }

                ImGui.End();
            }
            
            if (this.Settings.DrawWhenNotPaused && !this.Ctx.Game.IsInGame)
            {
                return;
            }

            if (this.Ctx.Game.State is not (GameState.InGame or GameState.Escape))
            {
                return;
            }

            if (this.Settings.DrawWhenForeground && !this.Ctx.Game.IsForeground)
            {
                return;
            }

            if (this.Settings.DrawWhenNotInHideoutOrTown &&
                (this.Ctx.Game.InGame.IsHideout || this.Ctx.Game.InGame.IsTown))
            {
                return;
            }

            if (this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                return;
            }

            if (this.Settings.MakeCullWindowFullScreen)
            {
                this.Settings.CullWindowPos = Vector2.Zero;
                this.Settings.CullWindowSize.X = this.Ctx.Game.WindowArea.Width;
                this.Settings.CullWindowSize.Y = this.Ctx.Game.WindowArea.Height;
            }

            if (!this.Ctx.Game.InGame.Player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var trackingPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            var trackingHeight = playerRender.TerrainHeight;

            var playerOther = this.Ctx.Entities.Awake
                .FirstOrDefault(e => e.Subtype == EntitySubtype.PlayerOther);
            if (this.IsLocalCoopActive(playerRender, playerOther != null))
            {
                if (playerOther != null && playerOther.TryGetComponent<IRender>(out var pOtherRender))
                {
                    trackingPos = (trackingPos + new Vector2(pOtherRender.GridPosition.X, pOtherRender.GridPosition.Y)) / 2f;
                    trackingHeight = (trackingHeight + pOtherRender.TerrainHeight) / 2f;
                }
            }

            this.CollectEntityPaths();
            this.RebuildEntityPaths();
            this.RebuildTrackedNodes();

            if (largeMap.IsVisible && !this.Ctx.Ui.WorldMapPanel.IsVisible)
            {
                if (this.largeMapDiagonalLength <= 0)
                {
                    this.UpdateLargeMapDetails();
                }

                var largeMapRealCenter = largeMap.Center + largeMap.Shift + largeMap.DefaultShift;
                // Calibrated biases baked in so LargeMapXOffset/LargeMapYOffset default to 0.
                const float LargeMapXBias = 0.6f;
                const float LargeMapYBias = 0.3f;
                largeMapRealCenter.X += LargeMapXBias + this.Settings.LargeMapXOffset;
                largeMapRealCenter.Y += LargeMapYBias + this.Settings.LargeMapYOffset;
                // Scale factor calibrated so LargeMapScaleMultiplier = 1.0 produces correct placement.
                const float LargeMapScaleBaseline = 0.187812f;
                var largeMapModifiedZoom = this.Settings.LargeMapScaleMultiplier * largeMap.Zoom * LargeMapScaleBaseline;
                Helper.DiagonalLength = this.largeMapDiagonalLength;
                Helper.Scale = largeMapModifiedZoom;
                ImGui.SetNextWindowPos(this.Settings.CullWindowPos);
                ImGui.SetNextWindowSize(this.Settings.CullWindowSize);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("Large Map Culling Window", Draw.TransparentWindowFlags);
                ImGui.PopStyleVar();
                this.DrawLargeMap(largeMapRealCenter, trackingPos, trackingHeight);
                this.DrawTgtFiles(largeMapRealCenter, trackingPos, trackingHeight);
                this.DrawDirectionLines(largeMapRealCenter, trackingPos, trackingHeight);
                this.DrawTgtIcons(largeMapRealCenter, trackingPos, trackingHeight, largeMapModifiedZoom * 5f);
                this.DrawMapIcons(largeMapRealCenter, trackingPos, trackingHeight, largeMapModifiedZoom * 5f);
                this.DrawEntityPaths(largeMapRealCenter, trackingPos, trackingHeight);
                ImGui.End();
            }

            if (miniMap.IsVisible)
            {
                if (this.miniMapDiagonalLength <= 0)
                {
                    this.UpdateMiniMapDetails();
                }

                Helper.DiagonalLength = this.miniMapDiagonalLength;
                // Calibrated baseline baked in so MiniMapZoomMultiplier = 1.0 produces correct placement.
                const float MiniMapZoomBaseline = 0.748f;
                Helper.Scale = miniMap.Zoom * this.Settings.MiniMapZoomMultiplier * MiniMapZoomBaseline;
                var miniMapCenter = miniMap.Position +
                    (miniMap.Size / 2) +
                    miniMap.DefaultShift +
                    miniMap.Shift;
                // Calibrated X bias baked in so MiniMapXOffset defaults to 0.
                const float MiniMapXBias = -5f;
                miniMapCenter.X += MiniMapXBias + this.Settings.MiniMapXOffset;
                ImGui.SetNextWindowPos(miniMap.Position);
                ImGui.SetNextWindowSize(miniMap.Size);
                ImGui.SetNextWindowBgAlpha(0f);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
                ImGui.Begin("###minimapRadar", Draw.TransparentWindowFlags);
                ImGui.PopStyleVar();
                this.DrawTgtIcons(miniMapCenter, trackingPos, trackingHeight, miniMap.Zoom);
                this.DrawMapIcons(miniMapCenter, trackingPos, trackingHeight, miniMap.Zoom);
                this.DrawEntityPaths(miniMapCenter, trackingPos, trackingHeight);
                ImGui.End();
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.onMove?.Dispose();
            this.onForegroundChange?.Dispose();
            this.onGameClose?.Dispose();
            this.onAreaChange?.Dispose();
            this.onMove = null;
            this.onForegroundChange = null;
            this.onGameClose = null;
            this.onAreaChange = null;
            this.CleanUpRadarPluginCaches();
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (!isGameOpened)
            {
                this.skipOneSettingChange = true;
            }

            // Must be set BEFORE deserializing settings: the JSON contains IconPicker objects whose
            // constructor uploads their sprite via IconPicker.Overlay, so the overlay service has to
            // be wired first (otherwise a NullReferenceException aborts plugin init and the overlay).
            IconPicker.Overlay = this.Ctx.Overlay;

            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonConvert.DeserializeObject<RadarSettings>(content) ?? new RadarSettings();
            }

            if (File.Exists(this.ImportantTgtPathName))
            {
                var tgtfiles = File.ReadAllText(this.ImportantTgtPathName);
                this.Settings.ImportantTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, Dictionary<string, string>>>(tgtfiles)
                    ?? new Dictionary<string, Dictionary<string, string>>();
            }

            if (File.Exists(this.BossArenaTgtPathName))
            {
                var bossfiles = File.ReadAllText(this.BossArenaTgtPathName);
                this.Settings.BossArenaTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, string>>(bossfiles) ?? new Dictionary<string, string>();
            }

            if (File.Exists(this.StairsTgtPathName))
            {
                var stairsfiles = File.ReadAllText(this.StairsTgtPathName);
                this.Settings.StairsTgts = JsonConvert.DeserializeObject
                    <Dictionary<string, string>>(stairsfiles) ?? new Dictionary<string, string>();
            }

            this.Settings.AddDefaultIcons(this.DirectoryPath);

            this.onMove = this.Ctx.Events.OnWindowMove(this.OnMove);
            this.onForegroundChange = this.Ctx.Events.OnForegroundChange(this.OnForegroundChange);
            this.onGameClose = this.Ctx.Events.OnGameDetached(this.OnClose);
            this.onAreaChange = this.Ctx.Events.OnAreaChange(this.ClearCachesAndUpdateAreaInfo);
            if (isGameOpened)
            {
                this.SwitchReachedPathsToCurrentArea();
            }

            this.GenerateMapTexture();
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
            var settingsData = JsonConvert.SerializeObject(this.Settings, Formatting.Indented);
            File.WriteAllText(this.SettingPathname, settingsData);

            if (this.Settings.ImportantTgts.Count > 0)
            {
                var tgtfiles = JsonConvert.SerializeObject(
                    this.Settings.ImportantTgts, Formatting.Indented);
                File.WriteAllText(this.ImportantTgtPathName, tgtfiles);
            }

            if (this.Settings.BossArenaTgts.Count > 0)
            {
                var bossfiles = JsonConvert.SerializeObject(
                    this.Settings.BossArenaTgts, Formatting.Indented);
                File.WriteAllText(this.BossArenaTgtPathName, bossfiles);
            }

            if (this.Settings.StairsTgts.Count > 0)
            {
                var stairsfiles = JsonConvert.SerializeObject(
                    this.Settings.StairsTgts, Formatting.Indented);
                File.WriteAllText(this.StairsTgtPathName, stairsfiles);
            }
        }

        private void DrawLargeMap(Vector2 mapCenter, Vector2 trackingPos, float trackingHeight)
        {
            if (!this.Settings.DrawWalkableMap)
            {
                return;
            }

            if (this.walkableMapTexture == IntPtr.Zero)
            {
                return;
            }

            var rectf = new RectangleF(
                -trackingPos.X,
                -trackingPos.Y,
                this.walkableMapDimension.X,
                this.walkableMapDimension.Y);

            var p1 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Left, rectf.Top), -trackingHeight);
            var p2 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Right, rectf.Top), -trackingHeight);
            var p3 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Right, rectf.Bottom), -trackingHeight);
            var p4 = Helper.DeltaInWorldToMapDelta(
                new Vector2(rectf.Left, rectf.Bottom), -trackingHeight);
            p1 += mapCenter;
            p2 += mapCenter;
            p3 += mapCenter;
            p4 += mapCenter;

            if (this.Settings.DrawMapInCull)
            {
                ImGui.GetWindowDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            }
            else
            {
                ImGui.GetBackgroundDrawList().AddImageQuad(this.walkableMapTexture, p1, p2, p3, p4);
            }
        }

        private void DrawTgtFiles(Vector2 mapCenter, Vector2 trackingPos, float trackingHeight)
        {
            var col = Draw.Color(
                (uint)(this.Settings.POIColor.X * 255),
                (uint)(this.Settings.POIColor.Y * 255),
                (uint)(this.Settings.POIColor.Z * 255),
                (uint)(this.Settings.POIColor.W * 255));

            ImDrawListPtr fgDraw;
            if (this.Settings.DrawPOIInCull)
            {
                fgDraw = ImGui.GetWindowDrawList();
            }
            else
            {
                fgDraw = ImGui.GetBackgroundDrawList();
            }

            var pPos = trackingPos;
            var clipMin = ImGui.GetWindowPos();
            var clipMax = clipMin + ImGui.GetWindowSize();

            void drawString(string text, Vector2 location, Vector2 stringImGuiSize, bool drawBackground)
            {
                float height = 0;
                if (location.X < this.Ctx.Game.InGame.Terrain.HeightData[0].Length &&
                    location.Y < this.Ctx.Game.InGame.Terrain.HeightData.Length)
                {
                    height = this.Ctx.Game.InGame.Terrain.HeightData[(int)location.Y][(int)location.X];
                }

                var fpos = Helper.DeltaInWorldToMapDelta(
                    location - pPos, -trackingHeight + height);
                var textMin = mapCenter + fpos - stringImGuiSize;
                var textMax = mapCenter + fpos + stringImGuiSize;
                if (textMax.X < clipMin.X || textMin.X > clipMax.X || textMax.Y < clipMin.Y || textMin.Y > clipMax.Y)
                {
                    return;
                }

                if (drawBackground)
                {
                    fgDraw.AddRectFilled(
                        textMin,
                        textMax,
                        Draw.Color(0, 0, 0, 200));
                }

                fgDraw.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize(),
                    textMin,
                    col,
                    text);
            }

            if (this.isAddNewPOIHeaderOpened)
            {
                var counter = 0;
                foreach (var tgtKV in this.Ctx.Game.InGame.Terrain.TgtTiles)
                {
                    if (!(this.Settings.POIFrequencyFilter > 0 &&
                        tgtKV.Value.Count > this.Settings.POIFrequencyFilter))
                    {
                        if (!this.poiIndexHalfSizeCache.TryGetValue(counter, out var tgtKImGuiSize))
                        {
                            tgtKImGuiSize = ImGui.CalcTextSize(counter.ToString()) / 2;
                            this.poiIndexHalfSizeCache[counter] = tgtKImGuiSize;
                        }

                        for (var i = 0; i < tgtKV.Value.Count; i++)
                        {
                            drawString(counter.ToString(), tgtKV.Value[i], tgtKImGuiSize, false);
                        }
                    }

                    counter++;
                }
            }
            else if (this.Settings.ShowImportantPOI)
            {
                if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var importantTgtsOfCurrentArea))
                {
                    foreach (var tile in importantTgtsOfCurrentArea)
                    {
                        if (this.Ctx.Game.InGame.Terrain.TgtTiles.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = this.GetTextHalfSize(tile.Value);
                            for (var i = 0; i < locations.Count; i++)
                            {
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                            }
                        }
                    }
                }

                if (this.Settings.ImportantTgts.TryGetValue("common", out var importantTgtsOfAllAreas))
                {
                    foreach (var tile in importantTgtsOfAllAreas)
                    {
                        if (this.Ctx.Game.InGame.Terrain.TgtTiles.TryGetValue(tile.Key, out var locations))
                        {
                            var strSize = this.GetTextHalfSize(tile.Value);
                            for (var i = 0; i < locations.Count; i++)
                            {
                                drawString(tile.Value, locations[i], strSize, this.Settings.EnablePOIBackground);
                            }
                        }
                    }
                }
            }
        }

        private void DrawDirectionLines(Vector2 mapCenter, Vector2 trackingPos, float trackingHeight)
        {
            var showStraight = this.Settings.ShowStraightLine;
            var showSmooth = this.Settings.ShowSmoothPath;
            if ((!showStraight && !showSmooth) || !this.Settings.ShowImportantPOI)
            {
                return;
            }

            if (!this.Ctx.Game.InGame.Player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var walkableData = this.Ctx.Game.InGame.Terrain.WalkableData;
            var bytesPerRow = this.Ctx.Game.InGame.Terrain.BytesPerRow;
            if (walkableData == null || walkableData.Length == 0 || bytesPerRow <= 0)
            {
                return;
            }

            var actualPlayerPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            var pPos = actualPlayerPos;
            var gridHeightData = this.Ctx.Game.InGame.Terrain.HeightData;

            // Build door-override map: open doors force their cells to walkable
            var doorOverrides = LineWalker.BuildDoorOverrideMap(this.Ctx.Entities.Awake);

            ImDrawListPtr fgDraw;
            if (this.Settings.DrawPOIInCull)
            {
                fgDraw = ImGui.GetWindowDrawList();
            }
            else
            {
                fgDraw = ImGui.GetBackgroundDrawList();
            }

            var clearColor = Draw.Color(0, 255, 0, 220);
            var blockedColor = Draw.Color(255, 60, 60, 220);
            var thickness = this.Settings.DirectionLineThickness;
            const float ArrowSize = 9f;

            // Predefined palette of distinct colors for POI paths, cycled per POI
            var poiPalette = new[]
            {
                Draw.Color(0, 220, 255, 220),   // cyan
                Draw.Color(255, 200, 0, 220),    // gold
                Draw.Color(255, 105, 180, 220),  // hot pink
                Draw.Color(0, 255, 127, 220),    // spring green
                Draw.Color(255, 99, 71, 220),    // tomato
                Draw.Color(123, 104, 238, 220),  // medium slate blue
                Draw.Color(255, 165, 0, 220),    // orange
                Draw.Color(50, 205, 50, 220),    // lime green
            };
            var poiColorIndex = 0;

            // --- Collect POI snapshot ---
            var poiSnapshot = new List<(string cacheKey, Vector2 gridPos)>();

            void CollectFrom(Dictionary<string, string> tileDict, string prefix)
            {
                foreach (var tile in tileDict)
                {
                    if (this.Ctx.Game.InGame.Terrain.TgtTiles.TryGetValue(tile.Key, out var locations))
                    {
                        for (var i = 0; i < locations.Count; i++)
                        {
                            var poiKey = $"{prefix}|{tile.Key}|{i}";
                            this.MarkReachedIfClose(poiKey, pPos, locations[i]);
                            poiSnapshot.Add((poiKey, locations[i]));
                        }
                    }
                }
            }

            if (this.Settings.ImportantTgts.TryGetValue(this.currentAreaName, out var importantTgtsOfCurrentArea))
            {
                CollectFrom(importantTgtsOfCurrentArea, "area");
            }

            if (this.Settings.ImportantTgts.TryGetValue("common", out var importantTgtsOfAllAreas))
            {
                CollectFrom(importantTgtsOfAllAreas, "common");
            }

            if (poiSnapshot.Count == 0)
            {
                return;
            }

            // --- Throttled background pathfinding ---
            var now = Environment.TickCount64;
            var forceFull = now >= this.nextPoiFullRecomputeTime;
            var shouldRecompute = now >= this.nextPoiRecomputeTime;
            if (forceFull)
            {
                this.nextPoiFullRecomputeTime = now + this.Settings.PathFullRecomputeIntervalMs;
                shouldRecompute = true;
            }
            if (shouldRecompute)
            {
                this.nextPoiRecomputeTime = now + this.Settings.PathRecomputeIntervalMs;

                // Only launch if no previous task is still running
                if (this.pendingPathTask == null || this.pendingPathTask.IsCompleted)
                {
                    // Skip reached POIs — see the note in RebuildEntityPaths. Safe because the
                    // previous task has completed and this is a private list for the new task.
                    var snap = poiSnapshot.Where(p => !this.IsReached(p.cacheKey)).ToList();
                    var wd = walkableData;
                    var bpr = bytesPerRow;
                    var pp = pPos;
                    var doors = doorOverrides;
                    var segs = forceFull ? 0 : this.Settings.PathRecomputeSegments;
                    var oldCache = this.poiPathCache;
                    this.pendingPathTask = Task.Run(() =>
                    {
                        var newCache = new Dictionary<string, List<Vector2>?>();
                        foreach (var (key, pos) in snap)
                        {
                            oldCache.TryGetValue(key, out var prev);
                            newCache[key] = ComputePath(wd, bpr, pp, pos, doors, prev, segs);
                        }

                        // Atomically swap the cache — render thread sees old or new, never torn
                        Interlocked.Exchange(ref this.poiPathCache, newCache);
                    });
                }
            }

            // --- Draw each POI from cache ---
            foreach (var (cacheKey, gridPos) in poiSnapshot)
            {
                var paletteColor = poiPalette[poiColorIndex % poiPalette.Length];
                poiColorIndex++;

                // Reached POIs aren't recomputed (so the smooth path drops out of the cache),
                // but the straight-line arrow below is computed inline and isn't cache-gated,
                // so this explicit skip is still required to hide reached POIs.
                if (this.IsReached(cacheKey))
                {
                    continue;
                }

                // Get terrain height at the POI location
                float poiHeight = 0;
                if (gridPos.X < gridHeightData[0].Length &&
                    gridPos.Y < gridHeightData.Length)
                {
                    poiHeight = gridHeightData[(int)gridPos.Y][(int)gridPos.X];
                }

                var poiFpos = Helper.DeltaInWorldToMapDelta(
                    gridPos - trackingPos, -trackingHeight + poiHeight);
                var poiScreen = mapCenter + poiFpos;
                var playerScreen = mapCenter + Helper.DeltaInWorldToMapDelta(
                    actualPlayerPos - trackingPos, -trackingHeight + playerRender.TerrainHeight);

                // --- Straight-line arrow ---
                if (showStraight)
                {
                    var lineResult = LineWalker.CheckLine(walkableData, bytesPerRow, pPos, gridPos, doorOverrides);
                    var color = lineResult.IsClear ? clearColor : blockedColor;

                    fgDraw.AddLine(playerScreen, poiScreen, color, thickness);

                    var dir = poiScreen - playerScreen;
                    var len = dir.Length();
                    if (len > 0.5f)
                    {
                        dir /= len;
                        var perp = new Vector2(-dir.Y, dir.X);
                        var tip = poiScreen;
                        var baseCenter = tip - (dir * ArrowSize);
                        var leftWing = baseCenter + (perp * (ArrowSize * 0.45f));
                        var rightWing = baseCenter - (perp * (ArrowSize * 0.45f));
                        fgDraw.AddTriangleFilled(leftWing, rightWing, tip, color);
                    }
                }

                // --- A* smooth path from cache ---
                if (showSmooth &&
                    this.poiPathCache.TryGetValue(cacheKey, out var cachedPath) &&
                    cachedPath != null && cachedPath.Count > 1)
                {
                    var prevScreen = playerScreen;
                    for (var pi = 1; pi < cachedPath.Count; pi++)
                    {
                        var pt = cachedPath[pi];
                        float ptHeight = 0;
                        var ix = (int)pt.X;
                        var iy = (int)pt.Y;
                        if (ix >= 0 && ix < gridHeightData[0].Length &&
                            iy >= 0 && iy < gridHeightData.Length)
                        {
                            ptHeight = gridHeightData[iy][ix];
                        }

                        var ptFpos = Helper.DeltaInWorldToMapDelta(pt - trackingPos, -trackingHeight + ptHeight);
                        var ptScreen = mapCenter + ptFpos;
                        fgDraw.AddLine(prevScreen, ptScreen, paletteColor, thickness + 1f);
                        prevScreen = ptScreen;
                    }
                }
            }
        }

        private void DrawTgtIcons(Vector2 mapCenter, Vector2 trackingPos, float trackingHeight, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();

            foreach (var tgtKV in this.Ctx.Game.InGame.Terrain.TgtTiles)
            {
                if (tgtKV.Key.StartsWith(TempleTgtPrefix) && tgtKV.Key.EndsWith(":1-y:1"))
                {
                    if (!this.Settings.TempleIcons.TryGetValue("Vaal Ruins", out var templeIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, trackingPos, trackingHeight, tgtKV.Value, templeIcon, iconSizeMultiplier, shiftUp: true);
                }
                else if (tgtKV.Key.StartsWith(RunestoneTgtPrefix) && tgtKV.Key.EndsWith(":1-y:1"))
                {
                    if (!this.Settings.RunestoneIcons.TryGetValue("Runestones", out var runestoneIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, trackingPos, trackingHeight, tgtKV.Value, runestoneIcon, iconSizeMultiplier, shiftUp: true);
                }
                else if (this.Settings.BossArenaTgts.ContainsKey(tgtKV.Key))
                {
                    if (!this.Settings.BossIcons.TryGetValue("Boss Arena", out var bossIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, trackingPos, trackingHeight, tgtKV.Value, bossIcon, iconSizeMultiplier);
                }
                else if (this.Settings.StairsTgts.ContainsKey(tgtKV.Key))
                {
                    if (!this.Settings.BaseIcons.TryGetValue("Stairs", out var stairsIcon))
                    {
                        continue;
                    }

                    this.DrawIconAtTgtLocations(fgDraw, mapCenter, trackingPos, trackingHeight, tgtKV.Value, stairsIcon, iconSizeMultiplier);
                }
            }
        }

        private void DrawIconAtTgtLocations(
            ImDrawListPtr fgDraw,
            Vector2 mapCenter,
            Vector2 trackingPos,
            float trackingHeight,
            IReadOnlyList<Vector2> locations,
            IconPicker icon,
            float iconSizeMultiplier,
            bool shiftUp = false)
        {
            if (!icon.Draw)
            {
                return;
            }

            for (var i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                float height = 0;
                if (location.X < this.Ctx.Game.InGame.Terrain.HeightData[0].Length &&
                    location.Y < this.Ctx.Game.InGame.Terrain.HeightData.Length)
                {
                    height = this.Ctx.Game.InGame.Terrain.HeightData[(int)location.Y][(int)location.X];
                }

                var fpos = Helper.DeltaInWorldToMapDelta(
                    location - trackingPos, -trackingHeight + height);
                var iconSizeMultiplierVector = new Vector2(iconSizeMultiplier);
                iconSizeMultiplierVector *= icon.IconScale;
                var offset = shiftUp ? new Vector2(0, iconSizeMultiplierVector.Y) : Vector2.Zero;
                fgDraw.AddImage(
                    icon.TexturePtr,
                    mapCenter + fpos - iconSizeMultiplierVector - offset,
                    mapCenter + fpos + iconSizeMultiplierVector - offset,
                    icon.UV0,
                    icon.UV1);
            }
        }

        private void DrawMapIcons(Vector2 mapCenter, Vector2 trackingPos, float trackingHeight, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();

            var clipMin = ImGui.GetWindowPos();
            var clipMax = clipMin + ImGui.GetWindowSize();
            var clipPadding = iconSizeMultiplier * 4f;
            var pPos = trackingPos;

            var baseIcons = this.Settings.BaseIcons;
            var expeditionIcons = this.Settings.ExpeditionIcons;
            var breachIcons = this.Settings.BreachIcons;
            var deliriumIcons = this.Settings.DeliriumIcons;
            var poiMonsterIcons = this.Settings.POIMonsters;
            var otherImportantObjects = this.Settings.OtherImportantObjects;

            var npcIcon = baseIcons["NPC"];
            var specialNpcIcon = baseIcons["Special NPC"];
            var leaderIcon = baseIcons["Leader"];
            var playerIcon = baseIcons["Player"];
            var selfIcon = baseIcons["Self"];
            var allOtherChestIcon = baseIcons["All Other Chest"];
            var rareChestIcon = baseIcons["Rare Chests"];
            var magicChestIcon = baseIcons["Magic Chests"];
            var expeditionChestIcon = expeditionIcons["Generic Expedition Chests"];
            var breachChestIcon = breachIcons["Breach Chest"];
            var strongboxIcon = baseIcons["Strongbox"];
            var shrineIcon = baseIcons["Shrine"];
            var pinnacleBossHiddenIcon = baseIcons["Pinnacle Boss Not Attackable"];
            var friendlyIcon = baseIcons["Friendly"];
            var deliriumBombIcon = deliriumIcons["Delirium Bomb"];
            var deliriumSpawnerIcon = deliriumIcons["Delirium Spawner"];
            var normalMonsterIcon = baseIcons["Normal Monster"];
            var magicMonsterIcon = baseIcons["Magic Monster"];
            var rareMonsterIcon = baseIcons["Rare Monster"];
            var uniqueMonsterIcon = baseIcons["Unique Monster"];

            foreach (var entityValue in this.Ctx.Entities.Awake)
            {
                if (this.Settings.HideOutsideNetworkBubble && !entityValue.IsValid)
                {
                    continue;
                }

                if (entityValue.State == EntityState.Useless)
                {
                    continue;
                }

                if (!entityValue.TryGetComponent<IRender>(out var entityRender))
                {
                    continue;
                }

                var ePos = new Vector2(entityRender.GridPosition.X, entityRender.GridPosition.Y);
                var fpos = Helper.DeltaInWorldToMapDelta(ePos - pPos, entityRender.TerrainHeight - trackingHeight);
                var screenPos = mapCenter + fpos;
                if (screenPos.X < clipMin.X - clipPadding || screenPos.X > clipMax.X + clipPadding ||
                    screenPos.Y < clipMin.Y - clipPadding || screenPos.Y > clipMax.Y + clipPadding)
                {
                    continue;
                }

                var iconSizeMultiplierVector = Vector2.One * iconSizeMultiplier;

                void DrawIcon(IconPicker icon)
                {
                    if (!icon.Draw)
                    {
                        return;
                    }

                    var scaled = iconSizeMultiplierVector * icon.IconScale;
                    fgDraw.AddImage(
                        icon.TexturePtr,
                        screenPos - scaled,
                        screenPos + scaled,
                        icon.UV0,
                        icon.UV1);
                }

                switch (entityValue.Type)
                {
                    case EntityType.Npc:
                        DrawIcon(entityValue.Subtype == EntitySubtype.SpecialNpc ? specialNpcIcon : npcIcon);
                        break;
                    case EntityType.Player:
                        if (entityValue.Subtype == EntitySubtype.PlayerOther)
                        {
                            if (this.Settings.ShowPlayersNames && entityValue.TryGetComponent<IPlayer>(out var playerComp))
                            {
                                var pNameSizeH = this.GetTextHalfSize(playerComp.Name);
                                fgDraw.AddRectFilled(screenPos - pNameSizeH, screenPos + pNameSizeH,
                                    Draw.Color(0, 0, 0, 200));
                                fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), screenPos - pNameSizeH,
                                    Draw.Color(255, 128, 128, 255), playerComp.Name);
                            }
                            else
                            {
                                DrawIcon(entityValue.State == EntityState.PlayerLeader ? leaderIcon : playerIcon);
                            }
                        }
                        else
                        {
                            DrawIcon(selfIcon);
                        }

                        break;
                    case EntityType.Chest:
                        var strongboxKey = RadarSettings.StrongboxKeyForPath(entityValue.Path);
                        if (strongboxKey != null)
                        {
                            // Record into the tracked cache (drawn from there so it persists and
                            // merges with the sleeping scan — strongboxes are static).
                            this.RecordTrackedNode("strongbox", strongboxKey, ePos, entityRender.TerrainHeight);
                            break;
                        }

                        switch (entityValue.Subtype)
                        {
                            case EntitySubtype.None:
                                DrawIcon(allOtherChestIcon);
                                break;
                            case EntitySubtype.ChestWithRareRarity:
                                DrawIcon(rareChestIcon);
                                break;
                            case EntitySubtype.ChestWithMagicRarity:
                                DrawIcon(magicChestIcon);
                                break;
                            case EntitySubtype.ExpeditionChest:
                                if (entityValue.Path.Contains("LeagueFaction") &&
                                    this.Settings.ExpeditionMarkerIcons.TryGetValue("Logbook", out var logbookIcon) &&
                                    logbookIcon.Draw)
                                {
                                    DrawIcon(logbookIcon);
                                }
                                else
                                {
                                    DrawIcon(expeditionChestIcon);
                                }

                                break;
                            case EntitySubtype.BreachChest:
                                DrawIcon(breachChestIcon);
                                break;
                            case EntitySubtype.Strongbox:
                                DrawIcon(strongboxIcon);
                                break;
                        }

                        break;
                    case EntityType.Shrine:
                        if ((entityValue.TryGetComponent<IShrine>(out var shrineComp) && shrineComp.IsUsed) ||
                            (entityValue.TryGetComponent<ITargetable>(out var targ) && !targ.IsTargetable))
                        {
                            break;
                        }

                        DrawIcon(shrineIcon);
                        break;
                    case EntityType.Monster:
                        switch (entityValue.State)
                        {
                            case EntityState.None:
                                if (entityValue.Subtype == EntitySubtype.PoiMonster)
                                {
                                    if (!poiMonsterIcons.TryGetValue(entityValue.CustomGroup, out var poiIcon))
                                    {
                                        poiIcon = poiMonsterIcons[-1];
                                    }

                                    DrawIcon(poiIcon);
                                }
                                else if (entityValue.TryGetComponent<IObjectMagicProperties>(out var omp))
                                {
                                    DrawIcon(this.RarityToIconMapping(omp.Rarity, normalMonsterIcon, magicMonsterIcon, rareMonsterIcon, uniqueMonsterIcon));
                                }

                                break;
                            case EntityState.PinnacleBossHidden:
                                DrawIcon(pinnacleBossHiddenIcon);
                                break;
                            case EntityState.MonsterFriendly:
                                DrawIcon(friendlyIcon);
                                break;
                            default:
                                break;
                        }

                        break;
                    case EntityType.DeliriumBomb:
                        DrawIcon(deliriumBombIcon);
                        break;
                    case EntityType.DeliriumSpawner:
                        DrawIcon(deliriumSpawnerIcon);
                        break;
                    case EntityType.OtherImportantObjects:
                        if (entityValue.CustomGroup == RadarSettings.ExpeditionMarkerGroup)
                        {
                            if (entityValue.TryGetComponent<IMinimapIcon>(out var minimapIcon) &&
                                !string.IsNullOrEmpty(minimapIcon.IconName) &&
                                RadarSettings.ExpeditionMarkerIconNameMap.TryGetValue(minimapIcon.IconName, out var displayName) &&
                                this.Settings.ExpeditionMarkerIcons.TryGetValue(displayName, out var expMarkerIcon) &&
                                expMarkerIcon.Draw)
                            {
                                DrawIcon(expMarkerIcon);
                            }
                        }
                        else if (entityValue.CustomGroup == RadarSettings.ExpeditionRemnantGroup)
                        {
                            if (entityValue.TryGetComponent<IObjectMagicProperties>(out var remnantOmp))
                            {
                                foreach (var modName in remnantOmp.ModNames)
                                {
                                    foreach (var (modSubstring, remnantDisplayName) in RadarSettings.ExpeditionRemnantModMap)
                                    {
                                        if (modName.Contains(modSubstring) &&
                                            this.Settings.ExpeditionRemnantIcons.TryGetValue(remnantDisplayName, out var remnantIcon) &&
                                            remnantIcon.Draw)
                                        {
                                            DrawIcon(remnantIcon);
                                            goto doneRemnant;
                                        }
                                    }
                                }
                                doneRemnant:;
                            }
                        }
                        else if (entityValue.CustomGroup == RadarSettings.RuneEncounterGroup)
                        {
                            var hasMinimapIcon = entityValue.TryGetComponent<IMinimapIcon>(out var runeMmIcon);

                            IconPicker? drawnRuneIcon = null;
                            if (hasMinimapIcon &&
                                !string.IsNullOrEmpty(runeMmIcon!.IconName) &&
                                RadarSettings.RunestoneIconNameMap.TryGetValue(runeMmIcon.IconName, out var runeDisplayName) &&
                                this.Settings.RunestoneIcons.TryGetValue(runeDisplayName, out var runeIcon) &&
                                runeIcon.Draw)
                            {
                                DrawIcon(runeIcon);
                                drawnRuneIcon = runeIcon;
                            }
                            else if (this.Settings.RunestoneIcons.TryGetValue("Runestone Encounter", out runeIcon) &&
                                     runeIcon.Draw)
                            {
                                DrawIcon(runeIcon);
                                drawnRuneIcon = runeIcon;
                            }

                            // Show the runestone's socket count (StateMachine state "sockets")
                            // just to the right of the icon.
                            if (drawnRuneIcon != null &&
                                entityValue.TryGetComponent<IStateMachine>(out var runeSm))
                            {
                                // Mark the runestone reached so its path hides like other paths.
                                this.MarkReachedIfClose($"entity|{entityValue.Id}", pPos, ePos);

                                // Prefer the authoritative RuneStation count; the "sockets"
                                // state caps at 6 and under-reports higher-hole recipes.
                                long sockets;
                                if (runeSm.TryGetRuneStationSocketCount(out var stationSockets))
                                {
                                    sockets = stationSockets;
                                }
                                else
                                {
                                    sockets = 0;
                                    foreach (var state in runeSm.States)
                                    {
                                        if (state.Name == "sockets")
                                        {
                                            sockets = state.Value;
                                            break;
                                        }
                                    }
                                }

                                if (sockets > 0 && !this.IsRunestoneSocketHidden(entityValue.Id, pPos, ePos))
                                {
                                    var socketText = sockets.ToString();

                                    // Bigger text overall; 5+ sockets get an even larger, red label.
                                    var highSockets = sockets >= 5;
                                    var fontScale = highSockets ? 3f : 1.8f;
                                    var fontSize = ImGui.GetFontSize() * fontScale;
                                    var textColor = highSockets
                                        ? Draw.Color(255, 40, 40, 255)
                                        : Draw.Color(255, 255, 255, 255);

                                    var textSize = ImGui.CalcTextSize(socketText) * fontScale;
                                    var textHalf = textSize / 2f;
                                    var iconHalfWidth = iconSizeMultiplier * drawnRuneIcon.IconScale;
                                    var textPos = new Vector2(
                                        screenPos.X + iconHalfWidth + 2f + this.Settings.RunestoneSocketOffsetX,
                                        screenPos.Y - textHalf.Y + this.Settings.RunestoneSocketOffsetY);
                                    fgDraw.AddText(ImGui.GetFont(), fontSize, textPos,
                                        textColor, socketText);
                                }
                            }
                        }
                        else if (entityValue.CustomGroup == RadarSettings.RitualGroup)
                        {
                            if (this.Settings.RitualIcons.TryGetValue("Ritual", out var ritualIcon) &&
                                ritualIcon.Draw)
                            {
                                DrawIcon(ritualIcon);
                            }
                        }
                        else if (entityValue.CustomGroup == RadarSettings.AbyssGroup)
                        {
                            // Cracks and the pit are static: record them into the per-map cache
                            // (drawn from there so they persist and merge with the sleeping scan).
                            if (entityValue.Path.Contains("AbyssCrack"))
                            {
                                this.RecordTrackedNode("abyss", "Abyss Crack", ePos, entityRender.TerrainHeight);
                            }
                            else if (entityValue.Path.Contains("AbyssFinalNodeBase"))
                            {
                                this.RecordTrackedNode("abyss", "Abyss Pit", ePos, entityRender.TerrainHeight);
                            }
                        }
                        else if (entityValue.CustomGroup == RadarSettings.BreachInitiatorGroup)
                        {
                            if (this.Settings.BreachIcons.TryGetValue("Breach", out var breachIcon) &&
                                breachIcon.Draw)
                            {
                                DrawIcon(breachIcon);
                            }
                        }
                        else if (entityValue.CustomGroup == RadarSettings.SekhemasGroup)
                        {
                            if (this.Settings.SekhemasIcons.TryGetValue("Sacred Water", out var sacredWaterIcon) &&
                                sacredWaterIcon.Draw)
                            {
                                DrawIcon(sacredWaterIcon);
                            }
                        }
                        else
                        {
                            if (!otherImportantObjects.TryGetValue(entityValue.CustomGroup, out var mopoiIcon))
                            {
                                mopoiIcon = otherImportantObjects[-1];
                            }

                            DrawIcon(mopoiIcon);
                        }

                        break;
                    case EntityType.Renderable:
                        fgDraw.AddCircleFilled(screenPos, 3f, 0xFFFFFFFF);
                        break;
                }
            }

            // Draw cached tracked nodes (Abyss cracks/pit, specific Strongboxes) — persisted per-map,
            // fed by the awake pass above and the throttled sleeping-entity scan. Static objects, so
            // cached positions stay valid.
            foreach (var node in this.trackedNodes)
            {
                var (gridPos, height, category, iconKey) = node.Value;
                var icon = this.ResolveTrackedIcon(category, iconKey);
                if (icon == null || !icon.Draw)
                {
                    continue;
                }

                var fpos = Helper.DeltaInWorldToMapDelta(gridPos - pPos, height - trackingHeight);
                var sp = mapCenter + fpos;
                if (sp.X < clipMin.X - clipPadding || sp.X > clipMax.X + clipPadding ||
                    sp.Y < clipMin.Y - clipPadding || sp.Y > clipMax.Y + clipPadding)
                {
                    continue;
                }

                var scaled = Vector2.One * iconSizeMultiplier * icon.IconScale;
                fgDraw.AddImage(icon.TexturePtr, sp - scaled, sp + scaled, icon.UV0, icon.UV1);
            }
        }

        /// <summary>
        /// Collects entities whose icon has ShowPath enabled.
        /// Mirrors the icon-selection logic from DrawMapIcons but only gathers
        /// entities that need paths, without drawing anything.
        /// </summary>
        private void CollectEntityPaths()
        {
            this.entityPathSnapshot.Clear();
            this.tileIconPathSnapshot.Clear();

            if (!this.Settings.ShowEntityPaths)
            {
                return;
            }

            if (!this.Ctx.Game.InGame.Player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            var baseIcons = this.Settings.BaseIcons;

            // Helper: if icon exists and has ShowPath, add to snapshot
            void TryAdd(uint entityId, Vector2 ePos, IconPicker icon)
            {
                if (icon != null && icon.ShowPath && icon.Draw)
                {
                    // Record "reached" here (player position is available), but keep the
                    // target in the snapshot so the background recompute pipeline stays
                    // stable. Reached paths are skipped at draw time instead.
                    this.MarkReachedIfClose($"entity|{entityId}", pPos, ePos);
                    this.entityPathSnapshot.Add((entityId, ePos, icon.PathColor));
                }
            }

            foreach (var ev in this.Ctx.Entities.Awake)
            {
                if (this.Settings.HideOutsideNetworkBubble && !ev.IsValid)
                {
                    continue;
                }

                if (ev.State == EntityState.Useless)
                {
                    continue;
                }

                if (!ev.TryGetComponent<IRender>(out var er))
                {
                    continue;
                }

                var ePos = new Vector2(er.GridPosition.X, er.GridPosition.Y);
                var eId = ev.Id;

                switch (ev.Type)
                {
                    case EntityType.Npc:
                        TryAdd(eId, ePos,
                            ev.Subtype == EntitySubtype.SpecialNpc
                                ? baseIcons["Special NPC"]
                                : baseIcons["NPC"]);
                        break;

                    case EntityType.Player:
                        if (ev.Subtype == EntitySubtype.PlayerOther)
                        {
                            TryAdd(eId, ePos,
                                ev.State == EntityState.PlayerLeader
                                    ? baseIcons["Leader"]
                                    : baseIcons["Player"]);
                        }

                        break;

                    case EntityType.Chest:
                        // Tracked strongboxes get their paths from the per-map cache pass below
                        // (covers far, sleeping-only ones), so skip them here.
                        if (RadarSettings.StrongboxKeyForPath(ev.Path) != null)
                        {
                            break;
                        }

                        IconPicker? chestIcon = ev.Subtype switch
                        {
                            EntitySubtype.ChestWithRareRarity => baseIcons["Rare Chests"],
                            EntitySubtype.ChestWithMagicRarity => baseIcons["Magic Chests"],
                            EntitySubtype.BreachChest => this.Settings.BreachIcons["Breach Chest"],
                            EntitySubtype.Strongbox => baseIcons["Strongbox"],
                            EntitySubtype.ExpeditionChest => ev.Path.Contains("LeagueFaction") &&
                                this.Settings.ExpeditionMarkerIcons.TryGetValue("Logbook", out var lb) && lb.Draw
                                    ? lb
                                    : this.Settings.ExpeditionIcons["Generic Expedition Chests"],
                            _ => baseIcons["All Other Chest"],
                        };
                        TryAdd(eId, ePos, chestIcon);
                        break;

                    case EntityType.Shrine:
                        if ((ev.TryGetComponent<IShrine>(out var sc) && sc.IsUsed) ||
                            (ev.TryGetComponent<ITargetable>(out var t) && !t.IsTargetable))
                        {
                            break;
                        }

                        TryAdd(eId, ePos, baseIcons["Shrine"]);
                        break;

                    case EntityType.Monster:
                        switch (ev.State)
                        {
                            case EntityState.None:
                                if (ev.Subtype == EntitySubtype.PoiMonster)
                                {
                                    if (!this.Settings.POIMonsters.TryGetValue(ev.CustomGroup, out var poiIcon))
                                    {
                                        poiIcon = this.Settings.POIMonsters[-1];
                                    }

                                    TryAdd(eId, ePos, poiIcon);
                                }
                                else if (ev.TryGetComponent<IObjectMagicProperties>(out var omp))
                                {
                                    TryAdd(eId, ePos, this.RarityToIconMapping(
                                        omp.Rarity,
                                        baseIcons["Normal Monster"],
                                        baseIcons["Magic Monster"],
                                        baseIcons["Rare Monster"],
                                        baseIcons["Unique Monster"]));
                                }

                                break;

                            case EntityState.PinnacleBossHidden:
                                TryAdd(eId, ePos, baseIcons["Pinnacle Boss Not Attackable"]);
                                break;

                            case EntityState.MonsterFriendly:
                                TryAdd(eId, ePos, baseIcons["Friendly"]);
                                break;
                        }

                        break;

                    case EntityType.DeliriumBomb:
                        TryAdd(eId, ePos, this.Settings.DeliriumIcons["Delirium Bomb"]);
                        break;

                    case EntityType.DeliriumSpawner:
                        TryAdd(eId, ePos, this.Settings.DeliriumIcons["Delirium Spawner"]);
                        break;

                    case EntityType.OtherImportantObjects:
                        if (ev.CustomGroup == RadarSettings.ExpeditionMarkerGroup)
                        {
                            if (ev.TryGetComponent<IMinimapIcon>(out var mmIcon) &&
                                !string.IsNullOrEmpty(mmIcon.IconName) &&
                                RadarSettings.ExpeditionMarkerIconNameMap.TryGetValue(
                                    mmIcon.IconName, out var displayName) &&
                                this.Settings.ExpeditionMarkerIcons.TryGetValue(
                                    displayName, out var expIcon) &&
                                expIcon.Draw)
                            {
                                TryAdd(eId, ePos, expIcon);
                            }
                        }
                        else if (ev.CustomGroup == RadarSettings.ExpeditionRemnantGroup)
                        {
                            if (ev.TryGetComponent<IObjectMagicProperties>(out var remnantOmp))
                            {
                                foreach (var modName in remnantOmp.ModNames)
                                {
                                    foreach (var (modSubstring, remnantDisplayName) in
                                        RadarSettings.ExpeditionRemnantModMap)
                                    {
                                        if (modName.Contains(modSubstring) &&
                                            this.Settings.ExpeditionRemnantIcons.TryGetValue(
                                                remnantDisplayName, out var remnantIcon) &&
                                            remnantIcon.Draw)
                                        {
                                            TryAdd(eId, ePos, remnantIcon);
                                            goto doneRemnantCollect;
                                        }
                                    }
                                }

                                doneRemnantCollect:;
                            }
                        }
                        else if (ev.CustomGroup == RadarSettings.RuneEncounterGroup)
                        {
                            if (ev.TryGetComponent<IMinimapIcon>(out var runeMmIcon) &&
                                !string.IsNullOrEmpty(runeMmIcon.IconName) &&
                                RadarSettings.RunestoneIconNameMap.TryGetValue(
                                    runeMmIcon.IconName, out var runeDisplayName) &&
                                this.Settings.RunestoneIcons.TryGetValue(
                                    runeDisplayName, out var runeIcon) &&
                                runeIcon.Draw)
                            {
                                TryAdd(eId, ePos, runeIcon);
                            }
                            else if (this.Settings.RunestoneIcons.TryGetValue(
                                         "Runestone Encounter", out runeIcon) &&
                                     runeIcon.Draw)
                            {
                                TryAdd(eId, ePos, runeIcon);
                            }
                        }
                        else if (ev.CustomGroup == RadarSettings.RitualGroup)
                        {
                            if (this.Settings.RitualIcons.TryGetValue("Ritual", out var ritualIcon) &&
                                ritualIcon.Draw)
                            {
                                TryAdd(eId, ePos, ritualIcon);
                            }
                        }
                        else if (ev.CustomGroup == RadarSettings.BreachInitiatorGroup)
                        {
                            if (this.Settings.BreachIcons.TryGetValue("Breach", out var breachIcon) &&
                                breachIcon.Draw)
                            {
                                TryAdd(eId, ePos, breachIcon);
                            }
                        }
                        else if (ev.CustomGroup == RadarSettings.SekhemasGroup)
                        {
                            if (this.Settings.SekhemasIcons.TryGetValue("Sacred Water", out var sacredWaterIcon) &&
                                sacredWaterIcon.Draw)
                            {
                                TryAdd(eId, ePos, sacredWaterIcon);
                            }
                        }
                        else
                        {
                            if (!this.Settings.OtherImportantObjects.TryGetValue(
                                    ev.CustomGroup, out var mopoiIcon))
                            {
                                mopoiIcon = this.Settings.OtherImportantObjects[-1];
                            }

                            TryAdd(eId, ePos, mopoiIcon);
                        }

                        break;

                    // Renderable entities have no icon — skip
                }
            }

            // --- Terrain-tile icon paths (Temple, Runestone, Boss Arena, Stairs) ---
            var tgtLocations = this.Ctx.Game.InGame.Terrain.TgtTiles;
            foreach (var tgtKV in tgtLocations)
            {
                IconPicker? tileIcon = null;

                if (tgtKV.Key.StartsWith(TempleTgtPrefix) && tgtKV.Key.EndsWith(":1-y:1"))
                {
                    this.Settings.TempleIcons.TryGetValue("Vaal Ruins", out tileIcon);
                }
                else if (tgtKV.Key.StartsWith(RunestoneTgtPrefix) && tgtKV.Key.EndsWith(":1-y:1"))
                {
                    this.Settings.RunestoneIcons.TryGetValue("Runestones", out tileIcon);
                }
                else if (this.Settings.BossArenaTgts.ContainsKey(tgtKV.Key))
                {
                    this.Settings.BossIcons.TryGetValue("Boss Arena", out tileIcon);
                }
                else if (this.Settings.StairsTgts.ContainsKey(tgtKV.Key))
                {
                    this.Settings.BaseIcons.TryGetValue("Stairs", out tileIcon);
                }

                if (tileIcon != null && tileIcon.ShowPath && tileIcon.Draw)
                {
                    for (var i = 0; i < tgtKV.Value.Count; i++)
                    {
                        var tileKey = $"tile|{tgtKV.Key}|{i}";
                        this.MarkReachedIfClose(tileKey, pPos, tgtKV.Value[i]);
                        this.tileIconPathSnapshot.Add((
                            tileKey,
                            tgtKV.Value[i],
                            tileIcon.PathColor));
                    }
                }
            }

            // --- Tracked-node paths (Abyss cracks/pit, Strongboxes) from the persisted cache ---
            // Paths come from the cache rather than the awake-entity pass, so far nodes — which
            // usually live only in SleepingEntities and never become awake — still get a path.
            // Reused through the tile-path machinery (computed + drawn the same way).
            foreach (var node in this.trackedNodes)
            {
                var (gridPos, _, category, iconKey) = node.Value;
                var icon = this.ResolveTrackedIcon(category, iconKey);
                if (icon == null || !icon.ShowPath || !icon.Draw)
                {
                    continue;
                }

                this.MarkReachedIfClose(node.Key, pPos, gridPos);
                this.tileIconPathSnapshot.Add((node.Key, gridPos, icon.PathColor));
            }
        }

        /// <summary>
        /// Rebuilds entity paths on a background thread (throttled).
        /// </summary>
        private void RebuildEntityPaths()
        {
            if (!this.Settings.ShowEntityPaths)
            {
                return;
            }

            var now = Environment.TickCount64;
            var forceFull = now >= this.nextEntityFullRecomputeTime;
            if (now < this.nextEntityRecomputeTime && !forceFull)
            {
                return;
            }

            if (forceFull)
            {
                this.nextEntityFullRecomputeTime = now + this.Settings.PathFullRecomputeIntervalMs;
            }

            this.nextEntityRecomputeTime = now + this.Settings.PathRecomputeIntervalMs;

            if (this.entityPathSnapshot.Count == 0 && this.tileIconPathSnapshot.Count == 0)
            {
                return;
            }

            if (this.pendingEntityPathTask != null && !this.pendingEntityPathTask.IsCompleted)
            {
                return;
            }

            if (!this.Ctx.Game.InGame.Player.TryGetComponent<IRender>(out var playerRender))
            {
                return;
            }

            var walkableData = this.Ctx.Game.InGame.Terrain.WalkableData;
            var bytesPerRow = this.Ctx.Game.InGame.Terrain.BytesPerRow;
            if (walkableData == null || walkableData.Length == 0 || bytesPerRow <= 0)
            {
                return;
            }

            var pPos = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            var doorOverrides = LineWalker.BuildDoorOverrideMap(this.Ctx.Entities.Awake);

            // Exclude reached targets from the work the background task does. This is safe:
            // the task gets its own private copy here, and the throttle above guarantees the
            // previous task has already completed (the pipeline is effectively "restarted"
            // each cycle), so we never mutate data a running task is reading. Reached entries
            // drop out of the cache on the next swap, and the draw path skips them regardless.
            var snap = this.entityPathSnapshot
                .Where(e => !this.IsReached($"entity|{e.entityId}")).ToArray();
            var tileSnap = this.tileIconPathSnapshot
                .Where(t => !this.IsReached(t.cacheKey)).ToArray();
            var segs = forceFull ? 0 : this.Settings.PathRecomputeSegments;
            var oldEntityCache = this.entityPathCache;
            var oldTileCache = this.tileIconPathCache;
            var wd = walkableData;
            var bpr = bytesPerRow;
            var pp = pPos;
            var doors = doorOverrides;

            this.pendingEntityPathTask = Task.Run(() =>
            {
                var newCache = new Dictionary<uint, List<Vector2>?>();
                foreach (var (id, pos, _) in snap)
                {
                    oldEntityCache.TryGetValue(id, out var prev);
                    newCache[id] = ComputePath(wd, bpr, pp, pos, doors, prev, segs);
                }

                var newTileCache = new Dictionary<string, List<Vector2>?>();
                foreach (var (key, pos, _) in tileSnap)
                {
                    oldTileCache.TryGetValue(key, out var prev);
                    newTileCache[key] = ComputePath(wd, bpr, pp, pos, doors, prev, segs);
                }

                Interlocked.Exchange(ref this.entityPathCache, newCache);
                Interlocked.Exchange(ref this.tileIconPathCache, newTileCache);
            });
        }

        /// <summary>
        /// Draws cached entity paths. Must be called after CollectEntityPaths.
        /// </summary>
        private void DrawEntityPaths(Vector2 mapCenter, Vector2 trackingPos, float trackingHeight)
        {
            if (!this.Settings.ShowEntityPaths ||
                (this.entityPathSnapshot.Count == 0 && this.tileIconPathSnapshot.Count == 0))
            {
                return;
            }

            var gridHeightData = this.Ctx.Game.InGame.Terrain.HeightData;

            ImDrawListPtr fgDraw;
            if (this.Settings.DrawPOIInCull)
            {
                fgDraw = ImGui.GetWindowDrawList();
            }
            else
            {
                fgDraw = ImGui.GetBackgroundDrawList();
            }

            var thickness = this.Settings.IconPathThickness;

            foreach (var (entityId, _, color) in this.entityPathSnapshot)
            {
                // Reached targets are excluded from recompute, so they fall out of the cache
                // on the next swap and the lookup below skips them — no explicit check needed.
                if (!this.entityPathCache.TryGetValue(entityId, out var cachedPath) ||
                    cachedPath == null || cachedPath.Count <= 1)
                {
                    continue;
                }

                var pathColor = Draw.Color(
                    (uint)(color.X * 255),
                    (uint)(color.Y * 255),
                    (uint)(color.Z * 255),
                    (uint)(color.W * 255));

                var startPt = cachedPath[0];
                float startPtHeight = 0;
                var sx = (int)startPt.X;
                var sy = (int)startPt.Y;
                if (sx >= 0 && sx < gridHeightData[0].Length &&
                    sy >= 0 && sy < gridHeightData.Length)
                {
                    startPtHeight = gridHeightData[sy][sx];
                }
                var prevScreen = mapCenter + Helper.DeltaInWorldToMapDelta(startPt - trackingPos, -trackingHeight + startPtHeight);

                for (var pi = 1; pi < cachedPath.Count; pi++)
                {
                    var pt = cachedPath[pi];
                    float ptHeight = 0;
                    var ix = (int)pt.X;
                    var iy = (int)pt.Y;
                    if (ix >= 0 && ix < gridHeightData[0].Length &&
                        iy >= 0 && iy < gridHeightData.Length)
                    {
                        ptHeight = gridHeightData[iy][ix];
                    }

                    var ptFpos = Helper.DeltaInWorldToMapDelta(pt - trackingPos, -trackingHeight + ptHeight);
                    var ptScreen = mapCenter + ptFpos;
                    fgDraw.AddLine(prevScreen, ptScreen, pathColor, thickness + 1f);
                    prevScreen = ptScreen;
                }
            }

            // --- Terrain-tile icon paths ---
            foreach (var (cacheKey, _, color) in this.tileIconPathSnapshot)
            {
                // Reached tiles fall out of the cache on the next swap; the lookup skips them.
                if (!this.tileIconPathCache.TryGetValue(cacheKey, out var cachedPath) ||
                    cachedPath == null || cachedPath.Count <= 1)
                {
                    continue;
                }

                var pathColor = Draw.Color(
                    (uint)(color.X * 255),
                    (uint)(color.Y * 255),
                    (uint)(color.Z * 255),
                    (uint)(color.W * 255));

                var startPt = cachedPath[0];
                float startPtHeight = 0;
                var sx = (int)startPt.X;
                var sy = (int)startPt.Y;
                if (sx >= 0 && sx < gridHeightData[0].Length &&
                    sy >= 0 && sy < gridHeightData.Length)
                {
                    startPtHeight = gridHeightData[sy][sx];
                }
                var prevScreen = mapCenter + Helper.DeltaInWorldToMapDelta(startPt - trackingPos, -trackingHeight + startPtHeight);

                for (var pi = 1; pi < cachedPath.Count; pi++)
                {
                    var pt = cachedPath[pi];
                    float ptHeight = 0;
                    var ix = (int)pt.X;
                    var iy = (int)pt.Y;
                    if (ix >= 0 && ix < gridHeightData[0].Length &&
                        iy >= 0 && iy < gridHeightData.Length)
                    {
                        ptHeight = gridHeightData[iy][ix];
                    }

                    var ptFpos = Helper.DeltaInWorldToMapDelta(pt - trackingPos, -trackingHeight + ptHeight);
                    var ptScreen = mapCenter + ptFpos;
                    fgDraw.AddLine(prevScreen, ptScreen, pathColor, thickness + 1f);
                    prevScreen = ptScreen;
                }
            }
        }

        /// <summary>
        /// Points <see cref="reachedPathKeys"/> at the reached-set for the current map instance,
        /// keyed by <c>AreaInstance.AreaHash</c>. The hash is stable when leaving and returning to
        /// the same instance (e.g. a town round-trip), so reached paths stay hidden, but differs for
        /// a freshly-generated instance of the same area, which starts with an empty set.
        /// </summary>
        private void SwitchReachedPathsToCurrentArea()
        {
            var areaHash = this.Ctx.Game.InGame.AreaHash;
            if (string.IsNullOrEmpty(areaHash))
            {
                this.reachedPathKeys = new HashSet<string>();
                this.trackedNodes = new ConcurrentDictionary<string, (Vector2, float, string, string)>();
                return;
            }

            if (!this.reachedPathKeysByArea.TryGetValue(areaHash, out var set))
            {
                set = new HashSet<string>();
                this.reachedPathKeysByArea[areaHash] = set;
            }

            this.reachedPathKeys = set;

            if (!this.trackedNodesByArea.TryGetValue(areaHash, out var tracked))
            {
                tracked = new ConcurrentDictionary<string, (Vector2, float, string, string)>();
                this.trackedNodesByArea[areaHash] = tracked;
            }

            this.trackedNodes = tracked;
        }

        /// <summary>
        /// Records a static tracked node (Abyss crack/pit or a Strongbox) into the per-map cache
        /// so it stays drawn for the rest of the map. Keyed by category + icon key + rounded grid
        /// position (stable for static objects). Safe to call from any thread.
        /// </summary>
        private void RecordTrackedNode(string category, string iconKey, Vector2 gridPos, float height)
        {
            var key = $"{category}|{iconKey}|{(int)gridPos.X}|{(int)gridPos.Y}";
            this.trackedNodes[key] = (gridPos, height, category, iconKey);
        }

        /// <summary>
        /// Classifies an entity path into a tracked (category, iconKey), or null if not tracked.
        /// Static + side-effect-free so it is safe to call from the background scan thread.
        /// </summary>
        private static (string category, string iconKey)? ClassifyTrackedPath(string path)
        {
            if (path.Contains("AbyssCrack"))
            {
                return ("abyss", "Abyss Crack");
            }

            if (path.Contains("AbyssFinalNodeBase"))
            {
                return ("abyss", "Abyss Pit");
            }

            var strongboxKey = RadarSettings.StrongboxKeyForPath(path);
            if (strongboxKey != null)
            {
                return ("strongbox", strongboxKey);
            }

            return null;
        }

        /// <summary>
        /// Resolves the configured icon for a tracked node's category + key.
        /// </summary>
        private IconPicker? ResolveTrackedIcon(string category, string iconKey)
        {
            var dict = category == "strongbox" ? this.Settings.StrongboxIcons : this.Settings.AbyssIcons;
            return dict.TryGetValue(iconKey, out var icon) ? icon : null;
        }

        /// <summary>
        /// Throttled background scan of the game's SleepingEntities map for tracked static objects
        /// only (Abyss cracks/pit and specific Strongboxes — never monsters/other, which move).
        /// Found nodes are merged into the per-map cache so they're revealed at the larger
        /// sleeping-entity radius and persist once seen.
        /// </summary>
        private void RebuildTrackedNodes()
        {
            var anyEnabled =
                (this.Settings.AbyssIcons.TryGetValue("Abyss Crack", out var crackIcon) && crackIcon.Draw) ||
                (this.Settings.AbyssIcons.TryGetValue("Abyss Pit", out var pitIcon) && pitIcon.Draw) ||
                this.Settings.StrongboxIcons.Values.Any(i => i.Draw);
            if (!anyEnabled)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (now < this.nextTrackedScanTime)
            {
                return;
            }

            this.nextTrackedScanTime = now + TrackedScanIntervalMs;

            if (this.pendingTrackedScanTask != null && !this.pendingTrackedScanTask.IsCompleted)
            {
                return;
            }

            var target = this.trackedNodes;
            this.pendingTrackedScanTask = Task.Run(() =>
            {
                this.Ctx.Entities.ScanSleeping(
                    p => ClassifyTrackedPath(p) != null,
                    entity =>
                    {
                        if (!entity.TryGetComponent<IRender>(out var r))
                        {
                            return;
                        }

                        var classified = ClassifyTrackedPath(entity.Path);
                        if (classified == null)
                        {
                            return;
                        }

                        var (category, iconKey) = classified.Value;
                        var gridPos = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                        var k = $"{category}|{iconKey}|{(int)gridPos.X}|{(int)gridPos.Y}";
                        target[k] = (gridPos, r.TerrainHeight, category, iconKey);
                    });
            });
        }

        /// <summary>
        /// If the feature is enabled and the player is within
        /// <see cref="RadarSettings.ReachedPathDistance"/> of the target, records
        /// <paramref name="key"/> as reached for the current map instance so its path stays
        /// hidden for the rest of the map (even after the player moves away). Called during
        /// path collection, where the player position is available.
        /// </summary>
        private void MarkReachedIfClose(string key, Vector2 playerPos, Vector2 targetPos)
        {
            if (!this.Settings.HideReachedPaths || this.reachedPathKeys.Contains(key))
            {
                return;
            }

            var threshold = this.Settings.ReachedPathDistance;
            if (Vector2.DistanceSquared(playerPos, targetPos) <= threshold * threshold)
            {
                this.reachedPathKeys.Add(key);
            }
        }

        /// <summary>
        /// Whether the path target identified by <paramref name="key"/> has been reached and
        /// should therefore not be drawn. Used at draw time so the recompute pipeline keeps
        /// seeing a stable snapshot. Honours the <see cref="RadarSettings.HideReachedPaths"/> toggle.
        /// </summary>
        private bool IsReached(string key) =>
            this.Settings.HideReachedPaths && this.reachedPathKeys.Contains(key);

        /// <summary>
        /// Whether a Runestone Encounter's socket-count label should be hidden because the player
        /// has gotten close to it. Gated by <see cref="RadarSettings.HideRunestoneSocketsWhenNear"/>
        /// and uses the single <see cref="RadarSettings.ReachedPathDistance"/>. Remembered per map
        /// (its own key namespace in <see cref="reachedPathKeys"/>), independent of path hiding.
        /// </summary>
        private bool IsRunestoneSocketHidden(uint entityId, Vector2 playerPos, Vector2 runestonePos)
        {
            if (!this.Settings.HideRunestoneSocketsWhenNear)
            {
                return false;
            }

            var key = $"rune-socket|{entityId}";
            if (this.reachedPathKeys.Contains(key))
            {
                return true;
            }

            var threshold = this.Settings.ReachedPathDistance;
            if (Vector2.DistanceSquared(playerPos, runestonePos) <= threshold * threshold)
            {
                this.reachedPathKeys.Add(key);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Smart path computation with optional partial recompute.
        /// If segments > 0 and a previous path exists, only recomputes the first
        /// N segments and splices the tail of the old path.
        /// </summary>
        private static List<Vector2>? ComputePath(
            byte[] walkableData,
            int bytesPerRow,
            Vector2 playerPos,
            Vector2 targetPos,
            HashSet<(int, int)>? doorOverrides,
            List<Vector2>? previousPath,
            int segments)
        {
            // Full recompute if segments is 0, no previous path, or path is too short
            if (segments <= 0 || previousPath == null || previousPath.Count <= segments + 1)
            {
                var lineResult = LineWalker.CheckLine(walkableData, bytesPerRow, playerPos, targetPos, doorOverrides);
                if (lineResult.IsClear)
                {
                    return new List<Vector2> { playerPos, targetPos };
                }

                return Pathfinder.FindPath(walkableData, bytesPerRow, playerPos, targetPos, doorOverrides);
            }

            // Partial recompute: path from player to old path point N, then splice
            var splicePoint = previousPath[segments];
            var partialPath = Pathfinder.FindPath(walkableData, bytesPerRow, playerPos, splicePoint, doorOverrides);

            if (partialPath == null || partialPath.Count == 0)
            {
                // Partial path failed — fall back to full recompute
                var lineResult = LineWalker.CheckLine(walkableData, bytesPerRow, playerPos, targetPos, doorOverrides);
                if (lineResult.IsClear)
                {
                    return new List<Vector2> { playerPos, targetPos };
                }

                return Pathfinder.FindPath(walkableData, bytesPerRow, playerPos, targetPos, doorOverrides);
            }

            // Splice: partial path + tail of old path (skip the splice point to avoid duplicate)
            var result = new List<Vector2>(partialPath.Count + previousPath.Count - segments);
            result.AddRange(partialPath);
            for (var i = segments + 1; i < previousPath.Count; i++)
            {
                result.Add(previousPath[i]);
            }

            return result;
        }

        private void ClearCachesAndUpdateAreaInfo()
        {
            this.CleanUpRadarPluginCaches();
            this.currentAreaName = this.Ctx.Game.InGame.AreaId;
            this.SwitchReachedPathsToCurrentArea();
            this.GenerateMapTexture();
            this.LogBossArenaTgtMatches();
        }

        private void LogBossArenaTgtMatches()
        {
            Console.WriteLine($"BossArena: area={this.currentAreaName}, TgtTilesLocations count={this.Ctx.Game.InGame.Terrain.TgtTiles.Count}, BossArenaTgts count={this.Settings.BossArenaTgts.Count}");
            foreach (var bossTgt in this.Settings.BossArenaTgts)
            {
                if (this.Ctx.Game.InGame.Terrain.TgtTiles.ContainsKey(bossTgt.Key))
                {
                    Console.WriteLine($"  BossArena MATCH: \"{bossTgt.Key}\"");
                }
            }
        }

        private void OnMove()
        {
            this.UpdateMiniMapDetails();
            this.UpdateLargeMapDetails();
            if (this.Settings.MakeCullWindowFullScreen)
            {
                this.Settings.CullWindowPos = Vector2.Zero;

                this.Settings.CullWindowSize.X = this.Ctx.Game.WindowArea.Width;
                this.Settings.CullWindowSize.Y = this.Ctx.Game.WindowArea.Height;
                this.skipOneSettingChange = false;
            }
            else if (this.skipOneSettingChange)
            {
                this.skipOneSettingChange = false;
            }
            else
            {
                this.Settings.ModifyCullWindow = true;
            }
        }

        private void OnClose()
        {
            this.skipOneSettingChange = true;
            this.CleanUpRadarPluginCaches();
        }

        private void OnForegroundChange()
        {
            this.UpdateMiniMapDetails();
            this.UpdateLargeMapDetails();
        }

        private void UpdateMiniMapDetails()
        {
            // Scale the base-resolution diagonal with the window height so that
            // the world-to-pixel ratio tracks the game's own vertical scaling.
            // Changing only the window width does not affect the map scale.
            var map = this.Ctx.Ui.MiniMap;
            var baseRes = this.Ctx.Ui.BaseResolution;
            var baseDiag = Math.Sqrt((baseRes.X * baseRes.X) + (baseRes.Y * baseRes.Y));
            this.miniMapDiagonalLength = baseDiag * map.Size.Y / baseRes.Y;
        }

        private void UpdateLargeMapDetails()
        {
            var map = this.Ctx.Ui.LargeMap;
            var baseRes = this.Ctx.Ui.BaseResolution;
            var baseDiag = Math.Sqrt((baseRes.X * baseRes.X) + (baseRes.Y * baseRes.Y));
            this.largeMapDiagonalLength = baseDiag * map.Size.Y / baseRes.Y;
        }

        private void ReloadMapTexture()
        {
            this.RemoveMapTexture();
            this.GenerateMapTexture();
        }

        private void RemoveMapTexture()
        {
            this.walkableMapTexture = IntPtr.Zero;
            this.walkableMapDimension = Vector2.Zero;
            this.Ctx.Overlay.RemoveImage("walkable_map");
        }

        private void GenerateMapTexture()
        {
            if (this.Ctx.Game.State is not (GameState.InGame or GameState.Escape))
            {
                return;
            }

            var gridHeightData = this.Ctx.Game.InGame.Terrain.HeightData;
            var mapWalkableData = this.Ctx.Game.InGame.Terrain.WalkableData;
            var bytesPerRow = this.Ctx.Game.InGame.Terrain.BytesPerRow;
            var worldToGridHeightMultiplier = this.Ctx.Game.InGame.Terrain.WorldToGridConvertor * 2f;
            if (bytesPerRow <= 0 || mapWalkableData == null || mapWalkableData.Length == 0 ||
                gridHeightData == null || gridHeightData.Length == 0)
            {
                // Diagnostic: log terrain metadata for areas with missing data (e.g. Sekhemas)
                var meta = this.Ctx.Game.InGame.Terrain;
                Console.WriteLine(
                    $"[Radar] No terrain data for area {this.Ctx.Game.InGame.AreaHash}. " +
                    $"TotalTiles=({meta.TotalTiles.X},{meta.TotalTiles.Y}) " +
                    $"BytesPerRow={meta.BytesPerRow} " +
                    $"WalkableLen={mapWalkableData?.Length ?? -1} " +
                    $"HeightLen={gridHeightData?.Length ?? -1} " +
                    $"TileHeightMul={meta.TileHeightMultiplier}");
                return;
            }

            var mapEdgeDetector = new MapEdgeDetector(mapWalkableData, bytesPerRow);
            var imageWidth = bytesPerRow * 2;
            var imageHeight = mapEdgeDetector.TotalRows;

            // Guard against enormous images (e.g. procedurally generated areas)
            if (imageWidth <= 0 || imageHeight <= 0 ||
                (long)imageWidth * imageHeight > 200_000_000)
            {
                return;
            }

            var configuration = Configuration.Default.Clone();
            configuration.PreferContiguousImageBuffers = true;
            using Image<Rgba32> image = new(configuration, imageWidth, imageHeight);
            Parallel.For(0, gridHeightData.Length, y =>
            {
                for (var x = 1; x < gridHeightData[y].Length - 1; x++)
                {
                    if (!mapEdgeDetector.IsBorder(x, y))
                    {
                        continue;
                    }

                    var height = (int)(gridHeightData[y][x] / worldToGridHeightMultiplier);
                    var imageX = x - height;
                    var imageY = y - height;

                    if (mapEdgeDetector.IsInsideMapBoundary(imageX, imageY))
                    {
                        image[imageX, imageY] = new Rgba32(this.Settings.WalkableMapColor);
                    }
                }
            });
#if DEBUG
            image.Save(this.DirectoryPath +
                       @$"/current_map_{this.Ctx.Game.InGame.AreaHash}.jpeg");
#endif
            this.walkableMapDimension = new Vector2(image.Width, image.Height);
            if (Math.Max(image.Width, image.Height) > 8192)
            {
                var (newWidth, newHeight) = (image.Width, image.Height);
                if (image.Height > image.Width)
                {
                    newWidth = newWidth * 8192 / newHeight;
                    newHeight = 8192;
                }
                else
                {
                    newHeight = newHeight * 8192 / newWidth;
                    newWidth = 8192;
                }

                var targetSize = new Size(newWidth, newHeight);
                var resizer = new ResizeProcessor(new ResizeOptions { Size = targetSize }, image.Size)
                    .CreatePixelSpecificCloningProcessor(configuration, image, image.Bounds);
                resizer.Execute();
            }

            this.Ctx.Overlay.AddOrGetImage("walkable_map", image, false, out var t);
            this.walkableMapTexture = t;
        }

        private IconPicker RarityToIconMapping(
            Rarity rarity,
            IconPicker normalMonsterIcon,
            IconPicker magicMonsterIcon,
            IconPicker rareMonsterIcon,
            IconPicker uniqueMonsterIcon)
        {
            return rarity switch
            {
                Rarity.Magic => magicMonsterIcon,
                Rarity.Rare => rareMonsterIcon,
                Rarity.Unique => uniqueMonsterIcon,
                _ => normalMonsterIcon,
            };
        }

        private Vector2 GetTextHalfSize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Vector2.Zero;
            }

            if (!this.textHalfSizeCache.TryGetValue(text, out var size))
            {
                size = ImGui.CalcTextSize(text) / 2;
                this.textHalfSizeCache[text] = size;
            }

            return size;
        }

        private string DelveChestPathToIcon(string path)
        {
            return path.Replace(this.delveChestStarting, null, StringComparison.Ordinal);
        }

        private void DrawEntityPathEnding(string path, ImDrawListPtr fgDraw, Vector2 pos)
        {
            var lastIndex = path.LastIndexOf('/') + 1;
            if (lastIndex < 0 || lastIndex >= path.Length)
            {
                lastIndex = 0;
            }

            var displayName = path.AsSpan(lastIndex, path.Length - lastIndex);
            var pNameSizeH = ImGui.CalcTextSize(displayName) / 2;
            fgDraw.AddRectFilled(pos - pNameSizeH, pos + pNameSizeH,
                Draw.Color(0, 0, 0, 200));
            fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), pos - pNameSizeH,
                Draw.Color(255, 128, 128, 255), displayName);

        }

        private void AddNewPOIWidget()
        {
            var tgttilesInArea = this.Ctx.Game.InGame.Terrain.TgtTiles;
            ImGui.InputText("Area Name", ref this.currentAreaName, 200, ImGuiInputTextFlags.ReadOnly);
            ImGui.NewLine();
            ImGui.InputInt("Filter on Max POI frenquency", ref this.Settings.POIFrequencyFilter);
            ImGui.InputText("Filter by text", ref this.tmpTileFilter, 200);
            if (ImGui.InputInt("Select POI via Index###tgtSelectorCounter", ref this.tmpTgtSelectionCounter) &&
                this.tmpTgtSelectionCounter < tgttilesInArea.Count)
            {
                this.tmpTileName = tgttilesInArea.Keys.ElementAt(this.tmpTgtSelectionCounter);
            }

            ImGui.NewLine();
            if (Draw.IEnumerableComboBox<string>("POI Path",
                tgttilesInArea.Keys.Where(k => string.IsNullOrEmpty(this.tmpTileFilter) ||
                k.Contains(this.tmpTileFilter, StringComparison.OrdinalIgnoreCase)),
                ref this.tmpTileName))
            {
                Console.WriteLine($"POI Path selected: {this.tmpTileName}");
            }
            ImGui.InputText("POI Display Name", ref this.tmpDisplayName, 200);
            ImGui.Checkbox("Add for all Areas", ref this.addTileForAllAreas);
            ImGui.SameLine();
            if (ImGui.Button("Add POI"))
            {
                var key = this.addTileForAllAreas ? "common" : this.currentAreaName;
                if (!string.IsNullOrEmpty(key) &&
                    !string.IsNullOrEmpty(this.tmpTileName) &&
                    !string.IsNullOrEmpty(this.tmpDisplayName))
                {
                    if (!this.Settings.ImportantTgts.ContainsKey(key))
                    {
                        this.Settings.ImportantTgts[key] = new();
                    }

                    this.Settings.ImportantTgts[key]
                        [this.tmpTileName] = this.tmpDisplayName;

                    this.tmpTileName = string.Empty;
                    this.tmpDisplayName = string.Empty;
                }
            }
        }

        private void ShowPOIWidget()
        {
            if (ImGui.TreeNode($"Important Terrain POIs common for all Areas"))
            {
                if (this.Settings.ImportantTgts.ContainsKey("common"))
                {
                    foreach (var tgt in this.Settings.ImportantTgts["common"])
                    {
                        if (ImGui.SmallButton($"Delete##{tgt.Key}"))
                        {
                            this.Settings.ImportantTgts["common"].Remove(tgt.Key);
                        }

                        ImGui.SameLine();
                        ImGui.Text($"POI Path: {tgt.Key}, Display: {tgt.Value}");
                        Draw.ToolTip("Click me to Modify.");
                        if (ImGui.IsItemClicked())
                        {
                            this.tmpTileName = tgt.Key;
                            this.tmpDisplayName = tgt.Value;
                        }
                    }
                }

                ImGui.TreePop();
            }

            if (ImGui.TreeNode($"Important Terrain POIs in Area: {this.currentAreaName}##import_time_in_area"))
            {
                if (this.Settings.ImportantTgts.ContainsKey(this.currentAreaName))
                {
                    foreach (var tgt in this.Settings.ImportantTgts[this.currentAreaName])
                    {
                        if (ImGui.SmallButton($"Delete##{tgt.Key}"))
                        {
                            this.Settings.ImportantTgts[this.currentAreaName].Remove(tgt.Key);
                        }

                        ImGui.SameLine();
                        ImGui.Text($"POI Path: {tgt.Key}, Display: {tgt.Value}");
                        Draw.ToolTip("Click me to Modify.");
                        if (ImGui.IsItemClicked())
                        {
                            this.tmpTileName = tgt.Key;
                            this.tmpDisplayName = tgt.Value;
                        }
                    }
                }

                ImGui.TreePop();
            }
        }

        private void CleanUpRadarPluginCaches()
        {
            this.delveChestCache.Clear();
            this.textHalfSizeCache.Clear();
            this.poiIndexHalfSizeCache.Clear();
            this.poiPathCache.Clear();
            this.nextPoiRecomputeTime = 0;
            this.nextPoiFullRecomputeTime = 0;
            this.pendingPathTask = null;
            this.entityPathCache.Clear();
            this.nextEntityRecomputeTime = 0;
            this.nextEntityFullRecomputeTime = 0;
            this.pendingEntityPathTask = null;
            this.entityPathSnapshot.Clear();
            this.tileIconPathCache.Clear();
            this.tileIconPathSnapshot.Clear();
            this.RemoveMapTexture();
            this.currentAreaName = string.Empty;
        }

        private bool IsLocalCoopActive(IRender playerRender, bool hasOtherPlayer)
        {
            if (!this.Settings.AutoDetectCoopMode)
            {
                return this.Settings.EnableCoopMode;
            }

            if (!this.Ctx.Game.IsControllerMode || !hasOtherPlayer)
            {
                return false;
            }

            var screenPos = this.Ctx.Render.WorldToScreen(playerRender.WorldPosition, playerRender.TerrainHeight);
            if (screenPos == Vector2.Zero)
            {
                return false;
            }

            var screenCenter = new Vector2(
                this.Ctx.Game.WindowArea.Width / 2f,
                this.Ctx.Game.WindowArea.Height / 2f);
            return Vector2.Distance(screenPos, screenCenter) > 35f;
        }
    }
}
