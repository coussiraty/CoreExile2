// <copyright file="StashValue.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashValue
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Numerics;
    using ExileBridge;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>
    ///     Prices the items in an open stash/inventory and overlays the value on each
    ///     slot. An ExileBridge port of zx0CF1's StashValue: the host SDK locates the
    ///     item slots (rect + path/rarity/mods/stack) via
    ///     <see cref="IUiService.EnumerateOpenItemSlots" />, and the self-contained
    ///     <see cref="PoeNinjaPriceFetcher" /> resolves prices from poe.ninja / poe2scout.
    /// </summary>
    public sealed class StashValue : Plugin<StashValueSettings>
    {
        // The UI-tree scan + per-slot item read is expensive; run it a few times per second
        // and redraw the cached result every frame so the stash stays smooth.
        private const int ScanIntervalMs = 250;

        private readonly Stopwatch scanClock = Stopwatch.StartNew();
        private readonly List<CachedSlot> cachedSlots = new();

        private string SettingsPath => Path.Combine(this.DirectoryPath, "config", "settings.json");

        private readonly struct CachedSlot
        {
            public CachedSlot(Vector2 pos, Vector2 size, ItemPanel panel, string text, string debug)
            {
                this.Pos = pos;
                this.Size = size;
                this.Panel = panel;
                this.Text = text;
                this.Debug = debug;
            }

            public Vector2 Pos { get; }

            public Vector2 Size { get; }

            public ItemPanel Panel { get; }

            public string Text { get; }

            public string Debug { get; }
        }

        /// <inheritdoc />
        public override void OnEnable(bool isGameAttached)
        {
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(this.SettingsPath);
                    this.Settings = JsonConvert.DeserializeObject<StashValueSettings>(json) ?? new StashValueSettings();
                }
                catch
                {
                    this.Settings = new StashValueSettings();
                }
            }

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.Initialize(this.DirectoryPath);
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
        public override void DrawSettings()
        {
            var changed = false;
            changed |= ImGui.Checkbox("Show stash item prices", ref this.Settings.ShowOverlay);
            changed |= ImGui.Checkbox("Show inventory item prices", ref this.Settings.ShowInventoryOverlay);
            changed |= ImGui.Checkbox("Hide price when hovering item", ref this.Settings.HidePriceOnHover);

            var maxThreshold = this.Settings.DisplayCurrency switch
            {
                0 => 100f,   // Divine
                1 => 200f,   // Exalted
                _ => 1000f,  // Chaos
            };
            var currencyLabel = this.Settings.DisplayCurrency switch
            {
                0 => "div",
                1 => "ex",
                _ => "c",
            };
            this.Settings.MinValueEx = Math.Clamp(this.Settings.MinValueEx, 0.0f, maxThreshold);
            changed |= ImGui.SliderFloat($"Min Price Threshold ({currencyLabel})##threshold", ref this.Settings.MinValueEx, 0.0f, maxThreshold, $"%.2f {currencyLabel}");

            changed |= ImGui.Checkbox("Show debug boxes over detected slots", ref this.Settings.ShowDebugInfo);

            ImGui.Separator();
            ImGui.Text("Display Currency");
            if (ImGui.RadioButton("Chaos", this.Settings.DisplayCurrency == 2))
            {
                this.Settings.DisplayCurrency = 2;
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("Exalted", this.Settings.DisplayCurrency == 1))
            {
                this.Settings.DisplayCurrency = 1;
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("Divine", this.Settings.DisplayCurrency == 0))
            {
                this.Settings.DisplayCurrency = 0;
                changed = true;
            }

            changed |= ImGui.SliderFloat("Font Scale", ref this.Settings.PriceFontScale, 0.5f, 2f, "%.2f");
            changed |= ImGui.SliderFloat("Horizontal Offset", ref this.Settings.PriceOffsetX, -50f, 50f);
            changed |= ImGui.SliderFloat("Vertical Offset", ref this.Settings.PriceOffsetY, -50f, 50f);
            changed |= ImGui.ColorEdit4("Text Color", ref this.Settings.TextColor);

            ImGui.Separator();
            ImGui.Text("Price Source");
            if (ImGui.RadioButton("poe2scout", this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoe2Scout))
            {
                this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoe2Scout;
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("poe.ninja", this.Settings.PriceSource == PoeNinjaPriceFetcher.SourcePoeNinja))
            {
                this.Settings.PriceSource = PoeNinjaPriceFetcher.SourcePoeNinja;
                changed = true;
            }

            changed |= ImGui.InputText("League", ref this.Settings.League, 64);
            changed |= ImGui.SliderInt("Refresh interval (min)", ref this.Settings.RefreshIntervalMin, 1, 120);

            if (changed)
            {
                this.SaveSettings();
            }

            if (ImGui.Button("Refresh prices now"))
            {
                PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
                PoeNinjaPriceFetcher.ForceRefresh(this.DirectoryPath, ignoreCooldown: true);
            }

            ImGui.SameLine();
            if (PoeNinjaPriceFetcher.IsFetching)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.2f, 1f), "Loading...");
            }
            else if (PoeNinjaPriceFetcher.LastFetchUtc > DateTime.MinValue)
            {
                var mins = Math.Max(0, (int)(DateTime.UtcNow - PoeNinjaPriceFetcher.LastFetchUtc).TotalMinutes);
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), $"{PoeNinjaPriceFetcher.LoadedItemCount} items | {mins} min ago");
            }
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            if (!this.Ctx.Game.IsInGame)
            {
                return;
            }

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.RefreshIfNeeded();

            if (!this.Settings.ShowOverlay && !this.Settings.ShowInventoryOverlay && !this.Settings.ShowDebugInfo)
            {
                return;
            }

            // Cheap per-frame gate: nothing to scan unless a stash/inventory panel is open.
            if (!this.Ctx.Ui.IsAnyLargePanelOpen)
            {
                this.cachedSlots.Clear();
                return;
            }

            // Throttle the expensive scan; redraw the cache every frame.
            if (this.scanClock.ElapsedMilliseconds >= ScanIntervalMs)
            {
                this.Rescan();
                this.scanClock.Restart();
            }

            if (this.cachedSlots.Count == 0)
            {
                return;
            }

            // Is the mouse over any cached slot? (so hover can suppress labels).
            var hideOnHover = false;
            if (this.Settings.HidePriceOnHover)
            {
                var mouse = ImGui.GetIO().MousePos;
                foreach (var slot in this.cachedSlots)
                {
                    if (mouse.X >= slot.Pos.X && mouse.X <= slot.Pos.X + slot.Size.X &&
                        mouse.Y >= slot.Pos.Y && mouse.Y <= slot.Pos.Y + slot.Size.Y)
                    {
                        hideOnHover = true;
                        break;
                    }
                }
            }

            var fg = ImGui.GetForegroundDrawList();
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize() * this.Settings.PriceFontScale;

            foreach (var slot in this.cachedSlots)
            {
                var drawPrices = slot.Panel == ItemPanel.Left ? this.Settings.ShowOverlay : this.Settings.ShowInventoryOverlay;

                if (this.Settings.ShowDebugInfo)
                {
                    var boxColor = slot.Panel == ItemPanel.Left ? 0xFF00FF00u : 0xFFFF00FFu;
                    fg.AddRect(slot.Pos, slot.Pos + slot.Size, boxColor, 0f, ImDrawFlags.None, 2f);
                    if (!string.IsNullOrEmpty(slot.Debug))
                    {
                        var dbgSize = fontSize * 0.7f;
                        fg.AddText(font, dbgSize, slot.Pos + new Vector2(1f, 1f), 0xFF000000u, slot.Debug);
                        fg.AddText(font, dbgSize, slot.Pos, 0xFF00FFFFu, slot.Debug);
                    }
                }

                if (!drawPrices || hideOnHover || string.IsNullOrEmpty(slot.Text))
                {
                    continue;
                }

                var textWidth = ImGui.CalcTextSize(slot.Text).X * this.Settings.PriceFontScale;
                var drawPos = new Vector2(
                    slot.Pos.X + this.Settings.PriceOffsetX,
                    slot.Pos.Y + slot.Size.Y - fontSize + this.Settings.PriceOffsetY);

                fg.AddRectFilled(
                    drawPos - new Vector2(3f, 1f),
                    drawPos + new Vector2(textWidth + 3f, fontSize + 1f),
                    0xB0000000u,
                    3f);

                fg.AddText(font, fontSize, drawPos + new Vector2(1f, 1f), 0xCC000000u, slot.Text);
                fg.AddText(font, fontSize, drawPos, ImGui.ColorConvertFloat4ToU32(this.Settings.TextColor), slot.Text);
            }
        }

        /// <summary>Re-walks the open panels and rebuilds the cached priced-slot list.</summary>
        private void Rescan()
        {
            this.cachedSlots.Clear();
            var slots = this.Ctx.Ui.EnumerateOpenItemSlots();
            foreach (var slot in slots)
            {
                var text = this.TryPriceItem(slot.Item, out var valueText) ? valueText : string.Empty;
                var path = slot.Item.Path ?? string.Empty;
                var debug = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
                this.cachedSlots.Add(new CachedSlot(slot.Position, slot.Size, slot.Panel, text, debug));
            }
        }

        private bool TryPriceItem(IInventoryItem item, out string valueText)
        {
            valueText = string.Empty;

            var fullItemPath = item.Path ?? string.Empty;
            if (string.IsNullOrEmpty(fullItemPath))
            {
                return false;
            }

            var internalName = fullItemPath.Contains('/') ? fullItemPath[(fullItemPath.LastIndexOf('/') + 1)..] : fullItemPath;

            // Prefer the item's REAL localized display name (resolved host-side from the game's
            // BaseItemTypes table) — it matches the price sites directly, so currencies, runes,
            // gems, fragments etc. all resolve without per-item metadata mapping. Fall back to the
            // metadata base name + path-segment resolution for anything the table didn't cover.
            var displayName = item.DisplayName ?? string.Empty;
            var price = PoeNinjaPriceFetcher.GetPrice(internalName, item.ModLines, internalName, fullItemPath, scoutText: displayName);
            if (price == null)
            {
                return false;
            }

            var stack = item.StackCount > 1 ? item.StackCount : 1;
            var priced = new PoeNinjaPrice { PriceChaos = price.PriceChaos * stack };
            var (displayValue, displayCurrency) = PoeNinjaPriceFetcher.GetDisplayPrice(priced, this.Settings.DisplayCurrency);

            if (this.Settings.MinValueEx > 0f && displayValue < this.Settings.MinValueEx)
            {
                return false;
            }

            valueText = FormatValue(displayValue, displayCurrency);
            return true;
        }

        private static string FormatValue(double value, string currency) => currency switch
        {
            "divine" => value.ToString("0.00", CultureInfo.InvariantCulture) + " div",
            "chaos" => value.ToString("0.#", CultureInfo.InvariantCulture) + " c",
            _ => value.ToString("0.#", CultureInfo.InvariantCulture) + " ex",
        };
    }
}
