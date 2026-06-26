// <copyright file="SettingsWindow.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Settings
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ClickableTransparentOverlay;
    using ClickableTransparentOverlay.Win32;
    using Coroutine;
    using CoroutineEvents;
    using ImGuiNET;
    using Plugin;
    using Utils;
    using GameOffsets.Objects.States.InGameState;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteEnums;
    using GameHelper.Ui;

    /// <summary>
    ///     Creates the MainMenu on the UI.
    /// </summary>
    internal static class SettingsWindow
    {
        private static bool isOverlayRunningLocal = true;
        private static bool isSettingsWindowVisible = true;
        private static string navPage = "General";
        private static bool coreExpanded = true;
        private static bool pluginsExpanded = true;

        private static EntityFilterType efilterType = EntityFilterType.PATH;
        private static string filterText = string.Empty;
        private static Rarity erarity = Rarity.Normal;
        private static GameStats eStats = 0;
        private static int filterGroup = 0;

        private static string specialNpcPath = string.Empty;

        private static string specialMiscObjPath = string.Empty;

        private static string monterPathToIgnore = string.Empty;

#if DEBUG
        private static string pluginForHotReload = string.Empty;
        private static bool pluginLoaded = true;
        private static bool showImGuiDemo = false;
#endif

        /// <summary>
        ///     Initializes the Main Menu.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            HideOnStartCheck();
            CoroutineHandler.Start(SaveCoroutine());
            CoroutineHandler.Start(AutoSaveCoroutine());
            Core.CoroutinesRegistrar.Add(CoroutineHandler.Start(
                RenderCoroutine(),
                "[Settings] Draw Core/Plugin settings",
                int.MaxValue));
        }

        /// <summary>
        ///     Draws the window body: left navigation rail + right content pane.
        /// </summary>
        private static void DrawShell()
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.06f, 0.052f, 0.038f, 1f));
            ImGui.BeginChild("ce_sidebar", new Vector2(190f, 0f), ImGuiChildFlags.Borders);
            DrawSidebar();
            ImGui.EndChild();
            ImGui.PopStyleColor();

            ImGui.SameLine(0f, 0f);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28f, 22f));
            ImGui.BeginChild("ce_content", new Vector2(0f, 0f), ImGuiChildFlags.AlwaysUseWindowPadding);
            DrawContent();
            ImGui.EndChild();
            ImGui.PopStyleVar();
        }

        private static void DrawSidebar()
        {
            ImGui.Dummy(new Vector2(0f, 9f));
            var hp = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();

            // gold "gem" emblem (gold diamond with a dark core), matching the banner motif.
            var dcx = hp.X + 9f;
            var dcy = hp.Y + 11f;
            const float ds = 8f;
            var goldB = ImGui.GetColorU32(ImGuiTheme.AccentBright);
            dl.AddQuadFilled(
                new Vector2(dcx, dcy - ds), new Vector2(dcx + ds, dcy),
                new Vector2(dcx, dcy + ds), new Vector2(dcx - ds, dcy), goldB);
            dl.AddQuadFilled(
                new Vector2(dcx, dcy - 3.5f), new Vector2(dcx + 3.5f, dcy),
                new Vector2(dcx, dcy + 3.5f), new Vector2(dcx - 3.5f, dcy),
                ImGui.GetColorU32(new Vector4(0.10f, 0.085f, 0.05f, 1f)));

            ImGui.SetCursorPosX(32f);
            ImGui.SetWindowFontScale(1.2f);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.Accent);
            ImGui.TextUnformatted("CoreExile2");
            ImGui.PopStyleColor();
            ImGui.SetWindowFontScale(1f);

            ImGui.SetCursorPosX(32f);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.TextMuted);
            ImGui.TextUnformatted($"{Core.GetVersion()}  ·  by Coussiraty");
            ImGui.PopStyleColor();

            ImGui.Dummy(new Vector2(0f, 11f));
            var sepY = ImGui.GetCursorScreenPos().Y;
            var wx = ImGui.GetWindowPos().X;
            var ww = ImGui.GetWindowWidth();
            dl.AddLine(
                new Vector2(wx + 12f, sepY), new Vector2(wx + ww - 12f, sepY),
                ImGui.GetColorU32(new Vector4(0.78f, 0.565f, 0.19f, 0.22f)), 1f);
            ImGui.Dummy(new Vector2(0f, 6f));

            if (NavSection("CORE", ref coreExpanded))
            {
                NavItem("General", "General");
                NavItem("Plugins", "Plugins");
                NavItem("Developer", "Developer");
                NavItem("About", "About");
            }

            if (NavSection("PLUGINS", ref pluginsExpanded))
            {
                foreach (var container in PManager.Plugins)
                {
                    if (container.Metadata.Enable)
                    {
                        NavPluginItem(container.Name);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws a collapsible sidebar section label with a caret. Returns the expanded state.
        /// </summary>
        private static bool NavSection(string text, ref bool expanded)
        {
            ImGui.Dummy(new Vector2(0f, 6f));
            var p = ImGui.GetCursorScreenPos();
            var avail = ImGui.GetContentRegionAvail().X;
            var h = ImGui.GetTextLineHeight() + 6f;

            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.78f, 0.565f, 0.19f, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.78f, 0.565f, 0.19f, 0.14f));
            if (ImGui.Selectable("##sect_" + text, false, ImGuiSelectableFlags.None, new Vector2(avail, h)))
            {
                expanded = !expanded;
            }

            ImGui.PopStyleColor(3);

            var dl = ImGui.GetWindowDrawList();
            var col = ImGui.GetColorU32(new Vector4(0.52f, 0.47f, 0.36f, 1f));
            var cy = p.Y + (h * 0.5f);
            if (expanded)
            {
                dl.AddTriangleFilled(
                    new Vector2(p.X + 4f, cy - 3f), new Vector2(p.X + 12f, cy - 3f), new Vector2(p.X + 8f, cy + 3f), col);
            }
            else
            {
                dl.AddTriangleFilled(
                    new Vector2(p.X + 5f, cy - 4f), new Vector2(p.X + 5f, cy + 4f), new Vector2(p.X + 11f, cy), col);
            }

            dl.AddText(new Vector2(p.X + 22f, p.Y + ((h - ImGui.GetTextLineHeight()) * 0.5f)), col, text);
            ImGui.Dummy(new Vector2(0f, 2f));
            return expanded;
        }

        private static void NavItem(string page, string label)
        {
            DrawNavSelectable(page, "      " + label, navPage == page);
        }

        private static void NavPluginItem(string name)
        {
            var page = "plugin:" + name;
            var p0 = ImGui.GetCursorScreenPos();
            var rowH = ImGui.GetTextLineHeight() + 12f;
            DrawNavSelectable(page, "        " + name, navPage == page);
            ImGui.GetWindowDrawList().AddCircleFilled(
                new Vector2(p0.X + 16f, p0.Y + (rowH * 0.5f)), 3.5f, ImGui.GetColorU32(ImGuiTheme.Success));
        }

        private static void DrawNavSelectable(string page, string label, bool selected)
        {
            var p0 = ImGui.GetCursorScreenPos();
            var rowH = ImGui.GetTextLineHeight() + 12f;
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.78f, 0.565f, 0.19f, 0.14f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.78f, 0.565f, 0.19f, 0.10f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.78f, 0.565f, 0.19f, 0.20f));
            ImGui.PushStyleColor(ImGuiCol.Text, selected
                ? new Vector4(0.96f, 0.90f, 0.76f, 1f)
                : new Vector4(0.72f, 0.68f, 0.57f, 1f));
            if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, new Vector2(0f, rowH)))
            {
                navPage = page;
            }

            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar();
            if (selected)
            {
                ImGui.GetWindowDrawList().AddRectFilled(
                    new Vector2(p0.X, p0.Y + (rowH * 0.2f)),
                    new Vector2(p0.X + 3f, p0.Y + (rowH * 0.8f)),
                    ImGui.GetColorU32(ImGuiTheme.AccentBright), 2f);
            }
        }

        private static void DrawContent()
        {
            if (navPage.StartsWith("plugin:"))
            {
                var name = navPage.Substring("plugin:".Length);
                var container = PManager.Plugins.FirstOrDefault(x => x.Name == name && x.Metadata.Enable);
                if (container != null)
                {
                    PageHeader(container.Name, "Plugin settings.");
                    container.Plugin.DrawSettings();
                    return;
                }

                navPage = "General";
            }

            switch (navPage)
            {
                case "Plugins":
                    PageHeader("Plugins", "Enable or disable plugins. Each enabled plugin gets a sidebar entry.");
                    DrawPluginManager();
                    break;
                case "Developer":
                    PageHeader("Developer", "Reverse-engineering and debugging tools.");
                    DrawDevTools();
                    break;
                case "About":
                    PageHeader("About");
                    DrawAbout();
                    break;
                default:
                    PageHeader("General", $"Settings save automatically when you hide the overlay ({Core.GHSettings.MainMenuHotKey}).");
                    DrawGeneralPage();
                    break;
            }
        }

        private static void PageHeader(string title, string? subtitle = null)
        {
            ImGui.SetWindowFontScale(1.28f);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.AccentBright);
            ImGui.TextUnformatted(title);
            ImGui.PopStyleColor();
            ImGui.SetWindowFontScale(1f);
            if (!string.IsNullOrEmpty(subtitle))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.TextMuted);
                ImGui.TextWrapped(subtitle);
                ImGui.PopStyleColor();
            }

            ImGui.Dummy(new Vector2(0f, 12f));
        }

        /// <summary>
        ///     Draws the plugin manager table (enable/disable plugins).
        /// </summary>
        private static void DrawPluginManager()
        {
            var enabledCount = PManager.Plugins.Count(p => p.Metadata.Enable);
            ImGui.TextDisabled($"Active: {enabledCount} / {PManager.Plugins.Count}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Enable all"))
            {
                SetAllPlugins(true);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Disable all"))
            {
                SetAllPlugins(false);
            }

            ImGui.Spacing();

            if (!ImGui.BeginTable(
                "pluginTable",
                3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new Vector2(0, 0)))
            {
                return;
            }

            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            foreach (var container in PManager.Plugins)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text(container.Name);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (container.Metadata.Enable)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.Success);
                    ImGui.Text("Active");
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.TextMuted);
                    ImGui.Text("Off");
                    ImGui.PopStyleColor();
                }

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                var enabled = container.Metadata.Enable;
                if (ImGui.Checkbox($"##enable_{container.Name}", ref enabled))
                {
                    SetPluginEnabled(container, enabled);
                }
            }

            ImGui.EndTable();
        }

        private static void SetAllPlugins(bool enabled)
        {
            foreach (var container in PManager.Plugins)
            {
                SetPluginEnabled(container, enabled);
            }
        }

        private static void SetPluginEnabled(PluginContainer container, bool enabled)
        {
            if (container.Metadata.Enable == enabled)
            {
                return;
            }

            container.Metadata.Enable = enabled;
            if (enabled)
            {
                container.Plugin.OnEnable(Core.Process.Address != IntPtr.Zero);
            }
            else
            {
                container.Plugin.SaveSettings();
                container.Plugin.OnDisable();
            }

            CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
        }

        /// <summary>
        ///     Draws the General page: everyday settings as cards, advanced behind collapsibles.
        /// </summary>
        private static void DrawGeneralPage()
        {
            ImGuiTheme.BeginCard("ce_card_status", "Status");
            ImGuiTheme.RowLeft("Game state");
            ImGuiTheme.BadgeRight($"{Core.States.GameCurrentState}", ImGuiTheme.AccentBright, new Vector4(0.09f, 0.07f, 0.03f, 1f));
            ImGuiTheme.RowLeft("Party leader");
            ImGuiTheme.RightAlignNext(220f);
            ImGui.InputTextWithHint("##LeaderName", "character name", ref Core.GHSettings.LeaderName, 200);
            ImGuiHelper.ToolTip("Character name the FollowBot / party-aware plugins follow.");
            ImGuiTheme.EndCard();

            ImGuiTheme.BeginCard("ce_card_keys", "Hotkeys");
            ImGuiTheme.RowLeft("Settings window");
            ImGuiTheme.RightAlignNext(140f);
            ImGuiHelper.NonContinuousEnumComboBox("##key_menu", ref Core.GHSettings.MainMenuHotKey);
            ImGuiTheme.RowLeft("Toggle rendering");
            ImGuiTheme.RightAlignNext(140f);
            ImGuiHelper.NonContinuousEnumComboBox("##key_render", ref Core.GHSettings.DisableAllRenderingKey);
            ImGuiTheme.EndCard();

            ImGuiTheme.BeginCard("ce_card_overlay", "Overlay");
            ImGuiTheme.ToggleRow("Hide settings window on startup", ref Core.GHSettings.HideSettingWindowOnStart);
            ImGuiTheme.ToggleRow("Close CoreExile2 when the game exits", ref Core.GHSettings.CloseWhenGameExit);
            ImGuiTheme.ToggleRow("Pause entity processing in town / hideout", ref Core.GHSettings.DisableEntityProcessingInTownOrHideout);

            var vsync = Core.Overlay.VSync;
            if (ImGuiTheme.ToggleRow("V-Sync", ref vsync))
            {
                Core.Overlay.VSync = vsync;
                Core.GHSettings.Vsync = vsync;
            }

            ImGui.BeginDisabled(Core.Overlay.VSync);
            ImGuiTheme.RowLeft("FPS limit (0 = off)");
            ImGuiTheme.RightAlignNext(110f);
            if (ImGui.InputInt("##fpslimit", ref Core.GHSettings.FPSLimit, 0))
            {
                Core.Overlay.FPSLimit = Core.GHSettings.FPSLimit;
            }

            ImGui.EndDisabled();
            ImGuiHelper.ToolTip("With V-Sync off and no FPS limit, use an external limiter " +
                "(e.g. NVIDIA Control Panel -> Manage 3D Settings -> Max Frame Rate).");

            ImGuiTheme.ToggleRow("Show performance stats (FPS)", ref Core.GHSettings.ShowPerfStats);
            if (Core.GHSettings.ShowPerfStats)
            {
                ImGui.Indent();
                ImGuiTheme.ToggleRow("Hide when game is in background", ref Core.GHSettings.HidePerfStatsWhenBg);
                ImGuiTheme.ToggleRow("Minimal stats only", ref Core.GHSettings.MinimumPerfStats);
                ImGui.Unindent();
            }

            ImGuiTheme.EndCard();

            ImGuiTheme.SectionHeader("More");
            DrawNearbyWidget();
            DrawAdvancedConfig();
            ChangeFontWidget();

            ImGui.Dummy(new Vector2(0f, 2f));
            ImGuiTheme.SectionHeader(
                "Entity filters",
                "Highlight or ignore monsters / NPCs / objects by metadata path. Change zone or restart after edits.");
            DrawPoiWidget();
            DrawMonstersToIgnore();
            DrawNPCWidget();
            DrawMiscObjWidget();
        }

        private static void DrawNearbyWidget()
        {
            if (ImGui.CollapsingHeader("Nearby Monster Config"))
            {
                ImGui.DragInt($"Small Range", ref Core.GHSettings.InnerCircle.Meaning,
                    1f, 0, Core.GHSettings.OuterCircle.Meaning);
                ImGui.SameLine();
                ImGui.Checkbox($"Visible##small", ref Core.GHSettings.InnerCircle.IsVisible);

                ImGui.DragInt($"Large Range", ref Core.GHSettings.OuterCircle.Meaning,
                    1f, Core.GHSettings.InnerCircle.Meaning, AreaInstanceConstants.NETWORK_BUBBLE_RADIUS);
                ImGui.SameLine();
                ImGui.Checkbox($"Visible##large", ref Core.GHSettings.OuterCircle.IsVisible);

                // ImGui.SameLine(0f, 30f);
                // ImGui.Checkbox($"Follow Mouse##{name}", ref value.FollowMouse);
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for changing fonts.
        /// </summary>
        private static void ChangeFontWidget()
        {
            if (ImGui.CollapsingHeader("Fonts & language"))
            {
                ImGui.Checkbox("Universal Font (render any language across the whole overlay)", ref Core.GHSettings.UniversalFont);
                ImGuiHelper.ToolTip("Loads a bundled merged font (DejaVuSans + the font below + GNU Unifont over the whole " +
                    "Unicode BMP) so text in any language renders everywhere. The font below is still merged in as the " +
                    "priority for its language. Building the full atlas is heavier, so this is off by default.");

                ImGui.InputText("Pathname", ref Core.GHSettings.FontPathName, 300);
                ImGui.DragInt("Size", ref Core.GHSettings.FontSize, 0.1f, 13, 40);
                var languageChanged = ImGuiHelper.EnumComboBox("Language", ref Core.GHSettings.FontLanguage);
                var customLanguage = ImGui.InputText("Custom Glyph Ranges", ref Core.GHSettings.FontCustomGlyphRange, 100);
                ImGuiHelper.ToolTip("This is advance level feature. Do not modify this if you don't know what you are doing. " +
                    "Example usage:- If you have downloaded and pointed to the ArialUnicodeMS.ttf font, you can use " +
                    "0x0020, 0xFFFF, 0x00 text in this field to load all of the font texture in ImGui. Note the 0x00" +
                    " as the last item in the range.");
                if (languageChanged)
                {
                    Core.GHSettings.FontCustomGlyphRange = string.Empty;
                }

                if (customLanguage)
                {
                    Core.GHSettings.FontLanguage = FontGlyphRangeType.English;
                }

                if (ImGui.Button("Apply Changes"))
                {
                    UniversalFont.ApplyFromSettings();
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for changing POI monsters.
        /// </summary>
        private static void DrawPoiWidget()
        {
            var isOpened = ImGui.CollapsingHeader("Special Monster Tracker (A.K.A Monster POI)");
            ImGuiHelper.ToolTip("In order to figure out the path/mod to add " +
                "please open DV -> States -> InGameState -> CurrentAreaInstance -> " +
                "Awake Entities -> click dump button against the entity you want to add. " +
                "This will create a new file in entity_dumps folder with all mod names and " +
                "path of that entity.");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                for (var i = Core.GHSettings.PoiMonstersCategories2.Count - 1; i >= 0; i--)
                {
                    var (filtertype, filter, rarity, stat, group) = Core.GHSettings.PoiMonstersCategories2[i];
                    var isChanged = false;
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                    if (ImGuiHelper.EnumComboBox($"Filter type     ##{i}MonsterPoiWidget", ref filtertype))
                    {
                        isChanged = true;
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 27);
                    if (ImGui.InputText($"Filter     ##{i}MonsterPoiWidget", ref filter, 200))
                    {
                        isChanged = true;
                    }

                    ImGuiHelper.ToolTip(filtertype == EntityFilterType.PATH ||
                        filtertype == EntityFilterType.PATHANDRARITY ||
                        filtertype == EntityFilterType.PATHANDSTAT ?
                        "Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length." :
                        "Mod name is fully checked, it need to be 100% match.");
                    ImGui.SameLine();
                    if (filtertype == EntityFilterType.PATHANDRARITY || filtertype == EntityFilterType.MODANDRARITY)
                    {
                        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                        if (ImGuiHelper.EnumComboBox($"Rarity     ##{i}MonsterPoiWidget", ref rarity))
                        {
                            isChanged = true;
                        }

                        ImGui.SameLine();
                    }

                    if (filtertype == EntityFilterType.PATHANDSTAT)
                    {
                        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                        if (ImGuiHelper.NonContinuousEnumComboBox($"Stat        ##{i}MonsterPoiWidget", ref stat))
                        {
                            isChanged = true;
                        }

                        ImGui.SameLine();
                    }

                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                    if (ImGui.InputInt($"Group Number##{i}MonsterPoiWidget", ref group))
                    {
                        if (group < 0)
                        {
                            group = 0;
                        }

                        isChanged = true;
                    }

                    if (isChanged)
                    {
                        Core.GHSettings.PoiMonstersCategories2[i] = new(filtertype, filter, rarity, stat, group);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button($"delete##{i}MonsterPoiWidget"))
                    {
                        Core.GHSettings.PoiMonstersCategories2.RemoveAt(i);
                    }
                }

                ImGui.Separator();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                ImGuiHelper.EnumComboBox($"Filter type     ##addMonsterPoiWidget", ref efilterType);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 17);
                ImGui.InputText($"Filter     ##addMonsterPoiWidget", ref filterText, 200);
                ImGuiHelper.ToolTip(efilterType == EntityFilterType.PATH ||
                    efilterType == EntityFilterType.PATHANDRARITY ||
                    efilterType == EntityFilterType.PATHANDSTAT ?
                    "Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length." :
                    "Mod name is fully checked, it need to be 100% match.");
                ImGui.SameLine();
                if (efilterType == EntityFilterType.PATHANDRARITY || efilterType == EntityFilterType.MODANDRARITY)
                {
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                    ImGuiHelper.EnumComboBox($"Rarity     ##addMonsterPoiWidget", ref erarity);
                    ImGui.SameLine();
                }

                if (efilterType == EntityFilterType.PATHANDSTAT)
                {
                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                    ImGuiHelper.NonContinuousEnumComboBox($"Stat        ##addMonsterPoiWidget", ref eStats);
                    ImGui.SameLine();
                }

                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                if (ImGui.InputInt($"Group Number##addMonsterPoiWidget", ref filterGroup) && filterGroup < 0)
                {
                    filterGroup = 0;
                }

                ImGui.SameLine();
                if(ImGui.Button("add##MonsterPoiWidget"))
                {
                    Core.GHSettings.PoiMonstersCategories2.Add(new(efilterType, filterText, erarity, eStats, filterGroup));
                    efilterType = EntityFilterType.PATH;
                    eStats = GameStats.is_capturable_monster;
                    filterText = string.Empty;
                    filterGroup = 0;
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for ignoring monsters.
        /// </summary>
        private static void DrawMonstersToIgnore()
        {
            var isOpened = ImGui.CollapsingHeader("Ignore Monsters");
            ImGuiHelper.ToolTip("In order to figure out the path, please open " +
                "DV -> States -> InGameState -> CurrentAreaInstance -> Awake Entities -> " +
                "Click Path -> see NPC path in the game world");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                ImGui.InputText("Monster metadata path##ToRemove", ref monterPathToIgnore, 200);
                ImGuiHelper.ToolTip("Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length.");
                ImGui.SameLine();
                if (ImGui.Button("Add##monsterPathToRemove") && !string.IsNullOrEmpty(monterPathToIgnore))
                {
                    Core.GHSettings.MonstersPathsToIgnore.Add(monterPathToIgnore);
                    monterPathToIgnore = string.Empty;
                }

                for (var i = Core.GHSettings.MonstersPathsToIgnore.Count - 1; i >= 0; i--)
                {
                    ImGui.Text($"Path: {Core.GHSettings.MonstersPathsToIgnore[i]}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##{i}monsterPathToRemove"))
                    {
                        Core.GHSettings.MonstersPathsToIgnore.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for defining important NPCs.
        /// </summary>
        private static void DrawNPCWidget()
        {
            var isOpened = ImGui.CollapsingHeader("Special NPC Metadata Paths");
            ImGuiHelper.ToolTip("In order to figure out the path, please open " +
                "DV -> States -> InGameState -> CurrentAreaInstance -> Awake Entities -> " +
                "Click Path -> see NPC path in the game world");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                ImGui.InputText("NPC Path##specialNPCPath", ref specialNpcPath, 200);
                ImGuiHelper.ToolTip("Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length.");
                ImGui.SameLine();
                if (ImGui.Button("Add##specialNPCPath") && !string.IsNullOrEmpty(specialNpcPath))
                {
                    Core.GHSettings.SpecialNPCPaths.Add(specialNpcPath);
                    specialNpcPath = string.Empty;
                }

                for (var i = Core.GHSettings.SpecialNPCPaths.Count - 1; i >= 0; i--)
                {
                    ImGui.Text($"Path: {Core.GHSettings.SpecialNPCPaths[i]}");
                    ImGui.SameLine();
                    if(ImGui.Button($"Delete##{i}specialNPCPath"))
                    {
                        Core.GHSettings.SpecialNPCPaths.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the ImGui widget for defining important MiscellaneousObjects.
        /// </summary>
        private static void DrawMiscObjWidget()
        {
            var isOpened = ImGui.CollapsingHeader("Special Objects Metadata Paths");
            ImGuiHelper.ToolTip("In order to figure out the path, please open " +
                "DV -> States -> InGameState -> CurrentAreaInstance -> Awake Entities -> " +
                "Click Path -> see objects path in the game world");
            if (isOpened)
            {
                ImGui.TextWrapped("Please restart gamehelper or change area/zone if you make any changes over here.");
                ImGui.InputText("Object Path##MiscObjWidget", ref specialMiscObjPath, 200);
                ImGuiHelper.ToolTip("Path is going to be checked from left to right (i.e. String.StartsWith), up till the filter length.");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
                if (ImGui.InputInt($"Group Number##MiscObjgroup", ref filterGroup) && filterGroup < 0)
                {
                    filterGroup = 0;
                }

                ImGui.SameLine();
                if (ImGui.Button("add##MiscObjadd"))
                {
                    Core.GHSettings.SpecialMiscObjPaths.Add(new(specialMiscObjPath, filterGroup));
                    specialMiscObjPath = string.Empty;
                    filterGroup = 0;
                }

                for (var i = Core.GHSettings.SpecialMiscObjPaths.Count - 1; i >= 0; i--)
                {
                    ImGui.Text($"Path: {Core.GHSettings.SpecialMiscObjPaths[i].path}, GroupId: {Core.GHSettings.SpecialMiscObjPaths[i].group}");
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##MiscObjDel{i}"))
                    {
                        Core.GHSettings.SpecialMiscObjPaths.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        ///     Draws the About section.
        /// </summary>
        private static void DrawAbout()
        {
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.Accent);
            ImGui.Text($"CoreExile2 {Core.GetVersion()}");
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.AccentMuted);
            ImGui.TextUnformatted("by Coussiraty");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiTheme.TextMuted);
            ImGui.TextWrapped("Path of Exile 2 overlay · built on the GameHelper2 engine.");
            ImGui.Spacing();
            ImGui.TextWrapped("Use at your own risk. Overlays that read game memory can violate the " +
                              "game's Terms of Service. The developer is not responsible for any loss " +
                              "resulting from the use of this software.");
            ImGui.PopStyleColor();
            ImGui.PopTextWrapPos();
        }

        /// <summary>
        ///     Draws the developer / reverse-engineering tools tab.
        /// </summary>
        private static void DrawDevTools()
        {
            ImGuiTheme.BeginCard("ce_card_inspect", "Inspectors");
            ImGuiTheme.ToggleRow("Game UI Explorer", ref Core.GHSettings.ShowGameUiExplorer);
            ImGuiTheme.ToggleRow("Data Visualization (DV)", ref Core.GHSettings.ShowDataVisualization);
            ImGuiTheme.ToggleRow("Item Slot Debug (stash / inventory / merchant)", ref Core.GHSettings.ShowItemSlotDebug);
            ImGuiTheme.ToggleRow("Element Finder", ref Core.GHSettings.ShowElementFinder);
            ImGuiTheme.RowLeft("Element Finder key");
            ImGuiTheme.RightAlignNext(140f);
            ImGuiHelper.NonContinuousEnumComboBox("##key_elem", ref Core.GHSettings.ElementFinderHotKey);
            ImGuiTheme.EndCard();

            ImGuiTheme.BeginCard("ce_card_prof", "Profiling");
            ImGuiTheme.ToggleRow("Performance Profiler", ref Core.GHSettings.ShowPerfProfiler);
            ImGuiTheme.EndCard();

#if DEBUG
            ImGuiTheme.BeginCard("ce_card_debug", "Debug build only");
            ImGuiTheme.ToggleRow("Krangled Passive Detector", ref Core.GHSettings.ShowKrangledPassiveDetector);
            ImGuiTheme.ToggleRow("ImGui demo window", ref showImGuiDemo);
            if (showImGuiDemo)
            {
                ImGui.ShowDemoWindow(ref showImGuiDemo);
            }

            DrawReloadPluginWidget();
            ImGuiTheme.EndCard();
#endif
        }

        /// <summary>
        ///     Draws the collapsed "Advanced" block: performance tuning, staleness fixes,
        ///     and client-compatibility toggles most users never touch.
        /// </summary>
        private static void DrawAdvancedConfig()
        {
            if (!ImGui.CollapsingHeader("Advanced (performance & compatibility)"))
            {
                return;
            }

            ImGui.TextDisabled("Most people never need these — the defaults are safe.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(220f);
            ImGui.DragInt("Key send timeout (ms)", ref Core.GHSettings.KeyPressTimeout, 0.2f, 60, 300);
            ImGuiHelper.ToolTip("Time the overlay waits between key presses in-game (~ latency x 3). " +
                "e.g. 30ms latency -> 90ms. Don't go below 60 (server ticks).");

            ImGui.Text("Entity reader CPU limit");
            ImGuiHelper.ToolTip("Limits the entity-reading algorithm to N CPUs. -1 disables the limit.");
            ImGui.SameLine();
            if (ImGui.RadioButton("-1", Core.GHSettings.EntityReaderMaxDegreeOfParallelism == -1))
            {
                Core.GHSettings.EntityReaderMaxDegreeOfParallelism = -1;
            }

            ImGui.SameLine();
            for (var i = 2; i < 128; i *= 2)
            {
                if (ImGui.RadioButton(i.ToString(), Core.GHSettings.EntityReaderMaxDegreeOfParallelism == i))
                {
                    Core.GHSettings.EntityReaderMaxDegreeOfParallelism = i;
                }

                if (i * 2 < 128)
                {
                    ImGui.SameLine();
                }
            }

            ImGui.Checkbox("Process all renderable entities", ref Core.GHSettings.ProcessAllRenderableEntities);
            ImGuiHelper.ToolTip("WARNING: greatly reduces speed and increases crashes/glitches. Keep this off.");
            ImGui.Checkbox("Disable debug counters (6-man party + juiced maps only)", ref Core.GHSettings.DisableAllCounters);

            ImGui.Separator();
            ImGui.Text("Entity staleness fixes");
            ImGuiHelper.ToolTip("Detect and fix stale entity data (e.g. NPCs that teleport but keep an old position).");
            ImGui.Checkbox("Clean up invalid NPC entities", ref Core.GHSettings.EnableNpcEntityCleanup);
            ImGui.Checkbox("Clean up any long-invalid entity", ref Core.GHSettings.EnableStaleEntityCleanup);
            if (Core.GHSettings.EnableStaleEntityCleanup)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f);
                ImGui.InputInt("threshold (frames)", ref Core.GHSettings.StaleEntityFrameThreshold);
                if (Core.GHSettings.StaleEntityFrameThreshold < 10)
                {
                    Core.GHSettings.StaleEntityFrameThreshold = 10;
                }
            }

            ImGui.Separator();
            ImGui.Text("Client compatibility");
            if (ImGui.Checkbox("Fix taskbar not showing", ref Core.GHSettings.FixTaskbarNotShowing))
            {
                if (Core.States.GameCurrentState != GameStateTypes.GameNotLoaded)
                {
                    CoroutineHandler.RaiseEvent(GameHelperEvents.OnMoved);
                }
            }

            ImGui.Checkbox("Taiwan client", ref Core.GHSettings.IsTaiwanClient);
            ImGuiHelper.ToolTip("Enable only if you play on the Taiwan realm (different memory layout).");
        }

        /// <summary>
        ///     Draws the imgui widget for reloading plugins
        /// </summary>
        private static void DrawReloadPluginWidget()
        {
#if DEBUG
            if (ImGui.CollapsingHeader("Reload Plugin"))
            {
                ImGuiHelper.IEnumerableComboBox<string>("Plugins", PManager.PluginNames, ref pluginForHotReload);
                ImGui.BeginDisabled(!pluginLoaded || string.IsNullOrEmpty(pluginForHotReload));
                if (ImGui.Button("Unload Plugin"))
                {
                    if (PManager.UnloadPlugin(pluginForHotReload))
                    {
                        pluginLoaded = false;
                    }
                }

                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(pluginLoaded || string.IsNullOrEmpty(pluginForHotReload));
                if (ImGui.Button("Load Plugin"))
                {
                    if (PManager.LoadPlugin(pluginForHotReload))
                    {
                        pluginLoaded = true;
                    }
                }

                ImGui.EndDisabled();
            }
#endif
        }

        /// <summary>
        ///     Draws the closing confirmation popup on ImGui.
        /// </summary>
        private static void DrawConfirmationPopup()
        {
            ImGui.SetNextWindowPos(new Vector2(Core.Overlay.Size.Width / 3f, Core.Overlay.Size.Height / 3f));
            if (ImGui.BeginPopup("GameHelperCloseConfirmation"))
            {
                ImGui.Text("Do you want to quit the GameHelper overlay?");
                ImGui.Separator();
                if (ImGui.Button("Yes", new Vector2(ImGui.GetContentRegionAvail().X / 2f, ImGui.GetTextLineHeight() * 2)))
                {
                    Core.GHSettings.IsOverlayRunning = false;
                    ImGui.CloseCurrentPopup();
                    isOverlayRunningLocal = true;
                }

                ImGui.SameLine();
                if (ImGui.Button("No", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 2)))
                {
                    ImGui.CloseCurrentPopup();
                    isOverlayRunningLocal = true;
                }

                ImGui.EndPopup();
            }
        }

        /// <summary>
        ///     Hides the overlay on startup.
        /// </summary>
        private static void HideOnStartCheck()
        {
            if (Core.GHSettings.HideSettingWindowOnStart)
            {
                isSettingsWindowVisible = false;
            }
        }

        /// <summary>
        ///     Draws the Settings Window.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> RenderCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);
                if (Utils.IsKeyPressedAndNotTimeout(Core.GHSettings.MainMenuHotKey))
                {
                    isSettingsWindowVisible = !isSettingsWindowVisible;
                    ImGui.GetIO().WantCaptureMouse = true;
                    if (!isSettingsWindowVisible)
                    {
                        CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
                    }
                }

                Core.IsSettingsMenuOpen = isSettingsWindowVisible;
                if (!isSettingsWindowVisible)
                {
                    continue;
                }

                ImGui.SetNextWindowSizeConstraints(new Vector2(820, 560), Vector2.One * float.MaxValue);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                var isMainMenuExpanded = ImGui.Begin(
                    $"CoreExile2  {Core.GetVersion()}",
                    ref isOverlayRunningLocal);
                ImGui.PopStyleVar();

                if (!isOverlayRunningLocal)
                {
                    ImGui.OpenPopup("GameHelperCloseConfirmation");
                }

                DrawConfirmationPopup();
                if (!Core.GHSettings.IsOverlayRunning)
                {
                    CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
                }

                if (!isMainMenuExpanded)
                {
                    ImGui.End();
                    continue;
                }

                DrawShell();
                ImGui.End();
            }
        }

        /// <summary>
        ///     Saves the GameHelper settings to disk.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> SaveCoroutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.TimeToSaveAllSettings);
                JsonHelper.SafeToFile(Core.GHSettings, State.CoreSettingFile);
            }
        }

        /// <summary>
        ///     Periodically raises <see cref="GameHelperEvents.TimeToSaveAllSettings" /> so core and
        ///     plugin settings persist even if the process is killed or crashes without a graceful
        ///     close / F12-hide (which are the only other triggers).
        /// </summary>
        private static IEnumerator<Wait> AutoSaveCoroutine()
        {
            while (true)
            {
                yield return new Wait(10d);
                CoroutineHandler.RaiseEvent(GameHelperEvents.TimeToSaveAllSettings);
            }
        }
    }
}
