// <copyright file="Draw.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System.Collections.Generic;
    using System.Numerics;
    using ImGuiNET;

    /// <summary>
    ///     Small ImGui helpers shared by the host and plugins (color packing,
    ///     tooltips, custom widgets). Pure UI utilities with no game dependency.
    /// </summary>
    public static class Draw
    {
        /// <summary>Window flags for a transparent, non-interactive overlay window.</summary>
        public const ImGuiWindowFlags TransparentWindowFlags =
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoTitleBar;

        /// <summary>A combo box bound to an enumerable of items.</summary>
        /// <typeparam name="T">item type.</typeparam>
        /// <param name="displayText">combo label.</param>
        /// <param name="items">items to choose from.</param>
        /// <param name="current">the current selection (updated in place).</param>
        /// <returns>true if the selection changed.</returns>
        public static bool IEnumerableComboBox<T>(string displayText, IEnumerable<T> items, ref T current)
        {
            var ret = false;
            if (ImGui.BeginCombo(displayText, $"{current}"))
            {
                foreach (var item in items)
                {
                    var isSelected = EqualityComparer<T>.Default.Equals(item, current);
                    if (ImGui.Selectable($"{item}", isSelected))
                    {
                        current = item;
                        ret = true;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            return ret;
        }
        /// <summary>Packs 0-255 RGBA channels into a uint32 ImGui color.</summary>
        /// <param name="r">red (0-255).</param>
        /// <param name="g">green (0-255).</param>
        /// <param name="b">blue (0-255).</param>
        /// <param name="a">alpha (0-255).</param>
        /// <returns>color as uint32.</returns>
        public static uint Color(uint r, uint g, uint b, uint a) => (a << 24) | (b << 16) | (g << 8) | r;

        /// <summary>Packs a 0-1 RGBA <see cref="Vector4" /> into a uint32 ImGui color.</summary>
        /// <param name="color">rgba in 0-1 range.</param>
        /// <returns>color as uint32.</returns>
        public static uint Color(Vector4 color)
        {
            color *= 255f;
            return ((uint)color.W << 24) | ((uint)color.Z << 16) | ((uint)color.Y << 8) | (uint)color.X;
        }

        /// <summary>Unpacks a uint32 ImGui color into a 0-1 RGBA <see cref="Vector4" />.</summary>
        /// <param name="color">color as uint32.</param>
        /// <returns>rgba in 0-1 range.</returns>
        public static Vector4 Color(uint color)
        {
            var ret = Vector4.Zero;
            ret.Z = (color & 0xFF) / 255f;
            color >>= 8;
            ret.Y = (color & 0xFF) / 255f;
            color >>= 8;
            ret.X = (color & 0xFF) / 255f;
            color >>= 8;
            ret.W = (color & 0xFF) / 255f;
            return ret;
        }

        /// <summary>Shows a wrapped tooltip when the previous item is hovered.</summary>
        /// <param name="text">tooltip text.</param>
        /// <param name="maxWidth">wrap width, in font-size units.</param>
        public static void ToolTip(string text, float maxWidth = 35.0f)
        {
            if (ImGui.IsItemHovered() && ImGui.BeginTooltip())
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * maxWidth);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        /// <summary>A two-component integer slider editing a <see cref="Vector2" />.</summary>
        /// <param name="text">label shown to the right.</param>
        /// <param name="itemWidth">total width available.</param>
        /// <param name="data">the vector edited in place.</param>
        /// <param name="min0">min for X.</param>
        /// <param name="max0">max for X.</param>
        /// <param name="min1">min for Y.</param>
        /// <param name="max1">max for Y.</param>
        /// <param name="flags">slider flags.</param>
        /// <returns>true if the value changed.</returns>
        public static bool Vector2SliderInt(
            string text, float itemWidth, ref Vector2 data,
            int min0, int max0, int min1, int max1, ImGuiSliderFlags flags)
        {
            var dataChanged = false;
            var dataX = (int)data.X;
            var dataY = (int)data.Y;
            ImGui.PushItemWidth(itemWidth / 3.1f);
            if (ImGui.SliderInt($"##{text}111", ref dataX, min0, max0, "%d", flags))
            {
                dataChanged = true;
                data.X = dataX;
            }

            ImGui.SameLine(0f, 5f);
            if (ImGui.SliderInt($"{text}##{text}222", ref dataY, min1, max1, "%d", flags))
            {
                dataChanged = true;
                data.Y = dataY;
            }

            ImGui.PopItemWidth();
            return dataChanged;
        }
    }

    /// <summary>Small math helpers shared with plugins.</summary>
    public static class MathUtil
    {
        /// <summary>Linearly interpolates between two scalars.</summary>
        /// <param name="a">start.</param>
        /// <param name="b">end.</param>
        /// <param name="t">factor (0-1).</param>
        /// <returns>interpolated value.</returns>
        public static float Lerp(float a, float b, float t) => a + ((b - a) * t);

        /// <summary>Linearly interpolates between two vectors.</summary>
        /// <param name="a">start.</param>
        /// <param name="b">end.</param>
        /// <param name="t">factor (0-1).</param>
        /// <returns>interpolated vector.</returns>
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t));
    }
}
