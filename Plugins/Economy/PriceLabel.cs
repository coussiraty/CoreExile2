namespace Economy
{
    using System.Numerics;
    using ImGuiNET;

    /// <summary>
    ///     The single price-chip style shared by every Economy pricer (stash / inventory,
    ///     ritual, runecraft) so the overlay looks consistent everywhere: a dark rounded
    ///     background, a 1px dark shadow for legibility, then the value text.
    /// </summary>
    internal static class PriceLabel
    {
        private const uint BackgroundColor = 0xDD151515u; // dark, slightly transparent
        private const uint BorderColor = 0x8819B0E0u;     // gold/amber rounded border
        private const uint ShadowColor = 0xCC000000u;
        private const float Rounding = 3f;

        /// <summary>Draws a rounded, gold-bordered price chip whose text top-left sits at <paramref name="textPos" />.</summary>
        internal static void Draw(ImDrawListPtr fg, ImFontPtr font, float fontSize, Vector2 textPos, string label, uint textColor)
        {
            if (string.IsNullOrEmpty(label))
            {
                return;
            }

            var baseSize = ImGui.GetFontSize();
            var textW = ImGui.CalcTextSize(label).X * (baseSize > 0f ? fontSize / baseSize : 1f);

            var pad = new Vector2(4f, 2f);
            var min = textPos - pad;
            var max = textPos + new Vector2(textW, fontSize) + pad;

            fg.AddRectFilled(min, max, BackgroundColor, Rounding);
            fg.AddRect(min, max, BorderColor, Rounding, ImDrawFlags.None, 1f);
            fg.AddText(font, fontSize, textPos + new Vector2(1f, 1f), ShadowColor, label);
            fg.AddText(font, fontSize, textPos, textColor, label);
        }
    }
}
