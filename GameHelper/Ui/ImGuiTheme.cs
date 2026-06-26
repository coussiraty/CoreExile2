// <copyright file="ImGuiTheme.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui
{
    using System.Numerics;
    using ImGuiNET;

    /// <summary>
    ///     Central "CoreExile" gold-on-black theme for all GameHelper windows —
    ///     matches the Path of Exile 2 banner aesthetic (warm near-black panels,
    ///     gold/amber accents, cream text).
    /// </summary>
    internal static class ImGuiTheme
    {
        // Gold ramp (matches assets/banner.svg).
        internal static readonly Vector4 Accent = new(0.85f, 0.66f, 0.30f, 1f);       // #d8a84d luminous gold — accents + section headers
        internal static readonly Vector4 AccentBright = new(0.94f, 0.83f, 0.53f, 1f); // #f0d488 brightest highlight (checkmarks, active grabs)
        internal static readonly Vector4 AccentMuted = new(0.58f, 0.43f, 0.18f, 1f);  // #946e2e dim gold — idle grabs / pressed fills
        internal static readonly Vector4 TextMuted = new(0.62f, 0.58f, 0.47f, 1f);    // warm grey-gold
        internal static readonly Vector4 Success = new(0.55f, 0.78f, 0.42f, 1f);      // #8cc76b — cheap/safe
        internal static readonly Vector4 Danger = new(0.88f, 0.35f, 0.30f, 1f);       // #e0584b — matches health bars
        internal static readonly Vector4 SectionBg = new(0.11f, 0.095f, 0.072f, 1f);  // warm panel fill

        internal static void Apply()
        {
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 6f;
            style.ChildRounding = 5f;
            style.FrameRounding = 4f;
            style.PopupRounding = 5f;
            style.ScrollbarRounding = 5f;
            style.GrabRounding = 4f;
            style.TabRounding = 4f;
            style.WindowBorderSize = 1f;
            style.FrameBorderSize = 0f;
            style.WindowPadding = new Vector2(14f, 12f);
            style.FramePadding = new Vector2(8f, 6f);
            style.ItemSpacing = new Vector2(10f, 8f);
            style.ItemInnerSpacing = new Vector2(8f, 5f);
            style.CellPadding = new Vector2(8f, 6f);
            style.ScrollbarSize = 14f;
            style.IndentSpacing = 18f;

            var gold = new Vector4(0.78f, 0.565f, 0.19f, 1f); // #c79030 base gold

            var colors = style.Colors;
            colors[(int)ImGuiCol.Text] = new Vector4(0.93f, 0.91f, 0.85f, 1f);          // cream
            colors[(int)ImGuiCol.TextDisabled] = TextMuted;
            colors[(int)ImGuiCol.WindowBg] = new Vector4(0.075f, 0.066f, 0.052f, 0.97f); // warm near-black
            colors[(int)ImGuiCol.ChildBg] = new Vector4(0.10f, 0.088f, 0.07f, 1f);
            colors[(int)ImGuiCol.PopupBg] = new Vector4(0.085f, 0.075f, 0.06f, 0.98f);
            colors[(int)ImGuiCol.Border] = new Vector4(0.78f, 0.565f, 0.19f, 0.30f);     // subtle gold edge
            colors[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);
            colors[(int)ImGuiCol.FrameBg] = new Vector4(0.15f, 0.128f, 0.095f, 1f);
            colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.20f, 0.165f, 0.115f, 1f);
            colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.26f, 0.21f, 0.135f, 1f);
            colors[(int)ImGuiCol.TitleBg] = new Vector4(0.07f, 0.06f, 0.048f, 1f);
            colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.16f, 0.125f, 0.07f, 1f);  // gold-tinted bar
            colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.07f, 0.06f, 0.048f, 0.8f);
            colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.09f, 0.08f, 0.062f, 1f);
            colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.07f, 0.06f, 0.048f, 0.6f);
            colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.30f, 0.26f, 0.18f, 1f);
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.44f, 0.36f, 0.22f, 1f);
            colors[(int)ImGuiCol.ScrollbarGrabActive] = gold;
            colors[(int)ImGuiCol.CheckMark] = AccentBright;
            colors[(int)ImGuiCol.SliderGrab] = AccentMuted;
            colors[(int)ImGuiCol.SliderGrabActive] = AccentBright;
            colors[(int)ImGuiCol.Button] = new Vector4(0.17f, 0.142f, 0.10f, 1f);
            colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.27f, 0.215f, 0.13f, 1f);
            colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.42f, 0.31f, 0.15f, 1f);
            colors[(int)ImGuiCol.Header] = new Vector4(0.21f, 0.17f, 0.105f, 1f);
            colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.31f, 0.24f, 0.135f, 1f);
            colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.42f, 0.31f, 0.16f, 1f);
            colors[(int)ImGuiCol.Separator] = new Vector4(0.78f, 0.565f, 0.19f, 0.28f);
            colors[(int)ImGuiCol.SeparatorHovered] = gold;
            colors[(int)ImGuiCol.SeparatorActive] = AccentBright;
            colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.78f, 0.565f, 0.19f, 0.22f);
            colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.78f, 0.565f, 0.19f, 0.55f);
            colors[(int)ImGuiCol.ResizeGripActive] = AccentBright;
            colors[(int)ImGuiCol.Tab] = new Vector4(0.12f, 0.105f, 0.08f, 1f);
            colors[(int)ImGuiCol.TabHovered] = new Vector4(0.31f, 0.24f, 0.135f, 1f);
            colors[(int)ImGuiCol.TabSelected] = new Vector4(0.23f, 0.18f, 0.11f, 1f);
            colors[(int)ImGuiCol.TabSelectedOverline] = AccentBright;
            colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.78f, 0.565f, 0.19f, 0.35f);
            colors[(int)ImGuiCol.NavCursor] = gold;
            colors[(int)ImGuiCol.PlotLines] = Accent;
            colors[(int)ImGuiCol.PlotHistogram] = gold;
        }

        internal static void SectionHeader(string title, string? subtitle = null)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Accent);
            ImGui.Text(title);
            ImGui.PopStyleColor();
            if (!string.IsNullOrEmpty(subtitle))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                ImGui.TextWrapped(subtitle);
                ImGui.PopStyleColor();
            }

            ImGui.Separator();
            ImGui.Spacing();
        }

        internal static void BeginPanel(string id)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, SectionBg);
            ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.Borders);
        }

        internal static void EndPanel()
        {
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        // ---- Modern settings building blocks --------------------------------

        internal static readonly Vector4 CardBg = new(0.087f, 0.075f, 0.053f, 1f);
        internal static readonly Vector4 CardBorder = new(0.78f, 0.565f, 0.19f, 0.16f);
        internal static readonly Vector4 RowLabelColor = new(0.80f, 0.76f, 0.66f, 1f);

        /// <summary>Begins a titled "card" panel (auto-height). Always pair with <see cref="EndCard" />.</summary>
        internal static void BeginCard(string id, string? title = null)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
            ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(15f, 13f));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 9f);
            ImGui.BeginChild(id, new Vector2(0f, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
            if (!string.IsNullOrEmpty(title))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Accent);
                ImGui.TextUnformatted(title!.ToUpperInvariant());
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }
        }

        internal static void EndCard()
        {
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            ImGui.Spacing();
        }

        /// <summary>Draws a pill toggle switch at the cursor. Returns true the frame it was flipped.</summary>
        internal static bool Toggle(string id, ref bool v)
        {
            var h = ImGui.GetFrameHeight() * 0.95f;
            var w = h * 1.85f;
            var p = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton(id, new Vector2(w, h));
            var changed = false;
            if (ImGui.IsItemClicked())
            {
                v = !v;
                changed = true;
            }

            var dl = ImGui.GetWindowDrawList();
            var r = h * 0.5f;
            var hov = ImGui.IsItemHovered();
            var trackOn = new Vector4(0.78f, 0.565f, 0.19f, hov ? 1f : 0.9f);
            var trackOff = new Vector4(0.18f, 0.155f, 0.11f, 1f);
            dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), ImGui.GetColorU32(v ? trackOn : trackOff), r);
            if (!v)
            {
                dl.AddRect(p, new Vector2(p.X + w, p.Y + h),
                    ImGui.GetColorU32(new Vector4(0.78f, 0.565f, 0.19f, 0.28f)), r, ImDrawFlags.None, 1f);
            }

            var kx = v ? p.X + w - r : p.X + r;
            var knob = v ? new Vector4(0.09f, 0.075f, 0.04f, 1f) : new Vector4(0.66f, 0.61f, 0.50f, 1f);
            dl.AddCircleFilled(new Vector2(kx, p.Y + r), r - 2.5f, ImGui.GetColorU32(knob));
            return changed;
        }

        /// <summary>A full settings row: label on the left, pill toggle flush to the right.</summary>
        internal static bool ToggleRow(string label, ref bool v)
        {
            RowLeft(label);
            var w = ImGui.GetFrameHeight() * 0.95f * 1.85f;
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - w);
            return Toggle("##tg_" + label, ref v);
        }

        /// <summary>Left-hand label of a row whose control is placed flush right afterwards.</summary>
        internal static void RowLeft(string label)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, RowLabelColor);
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
        }

        /// <summary>Right-aligns the next item (of width <paramref name="w" />) on the current row.</summary>
        internal static void RightAlignNext(float w)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - w);
            ImGui.SetNextItemWidth(w);
        }

        /// <summary>Draws a rounded badge pill flush to the right of the current row.</summary>
        internal static void BadgeRight(string value, Vector4 bg, Vector4 fg)
        {
            var sz = ImGui.CalcTextSize(value);
            var padX = 11f;
            var bh = ImGui.GetFrameHeight();
            var bw = sz.X + (padX * 2f);
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bw);
            var p = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(p, new Vector2(p.X + bw, p.Y + bh), ImGui.GetColorU32(bg), bh * 0.5f);
            dl.AddText(new Vector2(p.X + padX, p.Y + ((bh - sz.Y) * 0.5f)), ImGui.GetColorU32(fg), value);
            ImGui.Dummy(new Vector2(bw, bh));
        }
    }
}
