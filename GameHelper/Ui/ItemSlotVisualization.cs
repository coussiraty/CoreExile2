// <copyright file="ItemSlotVisualization.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Numerics;
    using Coroutine;
    using ExileBridge;
    using GameHelper.CoroutineEvents;
    using GameHelper.Sdk;
    using ImGuiNET;

    /// <summary>
    ///     Core diagnostic: draws a box (with the item's base name) over every item slot the
    ///     inventory/stash/merchant scanner detects. This visualizes the host-side slot reader
    ///     (<see cref="ItemSlotScanner" />) itself, so it lives in the core — independent of any
    ///     plugin that consumes the slots. Toggle: Settings → "Item Slot Debug".
    /// </summary>
    public static class ItemSlotVisualization
    {
        private const int RescanIntervalMs = 200;

        private static readonly Stopwatch ScanClock = Stopwatch.StartNew();
        private static readonly List<(Vector2 Pos, Vector2 Size, ItemPanel Panel, string Label)> Cached = new();

        /// <summary>Initializes the co-routines.</summary>
        internal static void InitializeCoroutines()
        {
            CoroutineHandler.Start(RenderCoRoutine());
        }

        private static IEnumerator<Wait> RenderCoRoutine()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnRender);
                try
                {
                    if (!Core.GHSettings.ShowItemSlotDebug)
                    {
                        if (Cached.Count > 0)
                        {
                            Cached.Clear();
                        }

                        continue;
                    }

                    if (ScanClock.ElapsedMilliseconds >= RescanIntervalMs)
                    {
                        ScanClock.Restart();
                        Refresh();
                    }

                    Draw();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ItemSlotVisualization.RenderCoRoutine] {ex}");
                }
            }
        }

        private static void Refresh()
        {
            Cached.Clear();
            foreach (var slot in ItemSlotScanner.Scan())
            {
                var path = slot.Item?.Path ?? string.Empty;
                var slash = path.LastIndexOf('/');
                var label = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
                if (string.IsNullOrEmpty(label))
                {
                    label = slot.Item?.DisplayName ?? string.Empty;
                }

                Cached.Add((slot.Position, slot.Size, slot.Panel, label));
            }
        }

        private static void Draw()
        {
            if (Cached.Count == 0)
            {
                return;
            }

            var fg = ImGui.GetForegroundDrawList();
            foreach (var (pos, size, panel, label) in Cached)
            {
                var color = panel switch
                {
                    ItemPanel.Left => 0xFF00FF00u,      // green   = stash
                    ItemPanel.Merchant => 0xFFFFFF00u,  // cyan    = merchant
                    _ => 0xFFFF00FFu,                    // magenta = inventory
                };

                fg.AddRect(pos, pos + size, color, 0f, ImDrawFlags.None, 2f);
                if (!string.IsNullOrEmpty(label))
                {
                    fg.AddText(pos + new Vector2(2f, 1f), 0xFF000000u, label);
                    fg.AddText(pos + new Vector2(1f, 0f), 0xFFFFFFFFu, label);
                }
            }
        }
    }
}
