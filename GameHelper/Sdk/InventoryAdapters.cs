// <copyright file="InventoryAdapters.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Sdk
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Numerics;
    using System.Text;
    using ExileBridge;
    using GameOffsets.Natives;
    using GameOffsets.Objects.States.InGameState;
    using GameOffsets.Objects.UiElement;
    using HostHandle = GameHelper.Utils.SafeMemoryHandle;
    using HostItem = GameHelper.RemoteObjects.States.InGameStateObjects.Item;
    using HostMods = GameHelper.RemoteObjects.Components.Mods;
    using HostRarity = GameHelper.RemoteEnums.Rarity;
    using HostStack = GameHelper.RemoteObjects.Components.Stack;
    using HostUiElement = GameHelper.RemoteObjects.UiElement.UiElementBase;

    /// <summary>SDK view of an item in an open stash/inventory slot.</summary>
    internal sealed class InventoryItemAdapter : IInventoryItem
    {
        internal InventoryItemAdapter(string path, Rarity rarity, int stackCount, IReadOnlyList<string> modLines)
        {
            this.Path = path;
            this.Rarity = rarity;
            this.StackCount = stackCount;
            this.ModLines = modLines;
        }

        /// <inheritdoc />
        public string Path { get; }

        /// <inheritdoc />
        public Rarity Rarity { get; }

        /// <inheritdoc />
        public int StackCount { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> ModLines { get; }
    }

    /// <summary>SDK view of a visible item slot (rect + item).</summary>
    internal sealed class ItemSlotAdapter : IItemSlot
    {
        internal ItemSlotAdapter(Vector2 position, Vector2 size, ItemPanel panel, IInventoryItem item)
        {
            this.Position = position;
            this.Size = size;
            this.Panel = panel;
            this.Item = item;
        }

        /// <inheritdoc />
        public Vector2 Position { get; }

        /// <inheritdoc />
        public Vector2 Size { get; }

        /// <inheritdoc />
        public ItemPanel Panel { get; }

        /// <inheritdoc />
        public IInventoryItem Item { get; }
    }

    /// <summary>
    ///     Walks the open stash/inventory UI panels and produces <see cref="IItemSlot" />
    ///     entries. For each visible UI element it reads the item pointer the game stores
    ///     at <c>element + 0x4F8</c>; when that points at a valid item, the element's
    ///     on-screen rectangle becomes the slot rect. This mirrors how upstream item
    ///     overlays (LootValue / StashValue) locate slots.
    /// </summary>
    internal static class ItemSlotScanner
    {
        // Offset of the item pointer inside an item-slot UI element. Build-specific.
        private const int ItemPointerOffset = 0x4F8;

        // Field offsets inside a UI element (mirror UiElementBaseOffset). Used to
        // validate a child address with cheap raw reads BEFORE asking the host to
        // materialize it — the host's UiElementBase throws+logs on an invalid address,
        // so we must never hand it one.
        private const int SelfPtrOffset = 0x08;
        private const int ChildrenVecOffset = 0x10;
        private const int FlagsOffset = 0x180;

        // Bound the per-frame UI tree walk so a corrupt tree cannot hang the overlay.
        // Generous: premium stash tabs (quad / special) have large UI trees.
        private const int MaxVisited = 40000;
        private const int MaxDepth = 64;
        private const int MaxChildrenPerNode = 8192;

        // Set true to log per-scan coverage to the console (logs/console.log), throttled.
        internal static bool Diagnostics = false;

        private static long lastDiagTick;

        internal static IReadOnlyList<IItemSlot> Scan()
        {
            var slots = new List<IItemSlot>();
            try
            {
                var gameUi = Core.States.InGameStateObject.GameUi;
                if (gameUi == null || gameUi.Address == IntPtr.Zero)
                {
                    return slots;
                }

                var left = gameUi.LeftPanel;
                var right = gameUi.RightPanel;
                var visited = new HashSet<IntPtr>();

                // The same item is often held by more than one UI element (the slot plus an
                // inner image), which would draw its price twice ("232 ex ex"). Dedup by the
                // item pointer so each item is priced once.
                var seenItems = new HashSet<IntPtr>();

                var leftVisible = IsValidUiElement(left);
                var rightVisible = IsValidUiElement(right);
                if (leftVisible)
                {
                    WalkNode(left, ItemPanel.Left, slots, visited, seenItems, 0);
                }

                if (rightVisible)
                {
                    WalkNode(right, ItemPanel.Right, slots, visited, seenItems, 0);
                }

                // Clip each slot to its panel's on-screen rect, counting per-panel hits/misses.
                var leftRect = PanelRect(leftVisible ? left : null);
                var rightRect = PanelRect(rightVisible ? right : null);
                var keptLeft = new List<IItemSlot>();
                var keptRight = new List<IItemSlot>();
                var clipLeft = 0;
                var clipRight = 0;
                foreach (var s in slots)
                {
                    if (s.Panel == ItemPanel.Left)
                    {
                        if (leftRect == null || WithinRect(s, leftRect.Value.min, leftRect.Value.max))
                        {
                            keptLeft.Add(s);
                        }
                        else
                        {
                            clipLeft++;
                        }
                    }
                    else
                    {
                        if (rightRect == null || WithinRect(s, rightRect.Value.min, rightRect.Value.max))
                        {
                            keptRight.Add(s);
                        }
                        else
                        {
                            clipRight++;
                        }
                    }
                }

                // Special-tab guard: when most of the left panel's slots land OUTSIDE the panel,
                // the host is mis-positioning this tab (premium currency/fragment/etc. use a
                // non-standard layout). Hide the entire left overlay so we never draw a partial /
                // misplaced mess. Normal grid tabs position correctly (few/no clips) and show.
                var leftReliable = keptLeft.Count > 0 && keptLeft.Count >= clipLeft;

                var result = new List<IItemSlot>(keptRight.Count + keptLeft.Count);
                result.AddRange(keptRight);
                if (leftReliable)
                {
                    result.AddRange(keptLeft);
                }

                slots = result;

                if (Diagnostics)
                {
                    var now = Environment.TickCount64;
                    if ((leftVisible || rightVisible) && now - lastDiagTick > 1000)
                    {
                        lastDiagTick = now;
                        Console.WriteLine($"[StashValue scan] visited={visited.Count} | " +
                                          $"left: kept={keptLeft.Count} clip={clipLeft} " +
                                          $"{(leftReliable ? "shown" : "SUPPRESSED(special tab)")} | " +
                                          $"right: kept={keptRight.Count} clip={clipRight} | shown={slots.Count}");
                    }
                }
            }
            catch
            {
                // Any read failure: return whatever we gathered so far (possibly empty).
            }

            return slots;
        }

        /// <summary>Returns a panel's on-screen rectangle (with a small margin), or null if unreadable.</summary>
        private static (Vector2 min, Vector2 max)? PanelRect(HostUiElement? panel)
        {
            if (panel == null)
            {
                return null;
            }

            Vector2 pos, size;
            try
            {
                pos = panel.Position;
                size = panel.Size;
            }
            catch
            {
                return null;
            }

            if (size.X < 50f || size.Y < 50f)
            {
                return null;
            }

            var margin = new Vector2(16f, 16f);
            return (pos - margin, pos + size + margin);
        }

        private static bool WithinRect(IItemSlot slot, Vector2 min, Vector2 max)
        {
            var center = slot.Position + (slot.Size * 0.5f);
            return center.X >= min.X && center.X <= max.X && center.Y >= min.Y && center.Y <= max.Y;
        }

        /// <summary>
        ///     Walks <paramref name="node" /> and its visible descendants via the host indexer,
        ///     so each materialized child uses the host's live <c>rootCache</c> for positioning
        ///     (correct, refreshed every tick). Children addresses are validated with raw reads
        ///     first so the indexer is only handed genuine, visible elements. The loop is bounded
        ///     by the fresh children vector so a stale cached child count cannot truncate it.
        /// </summary>
        private static void WalkNode(HostUiElement node, ItemPanel panel, List<IItemSlot> slots, HashSet<IntPtr> visited, HashSet<IntPtr> seenItems, int depth)
        {
            if (node == null || node.Address == IntPtr.Zero || depth > MaxDepth ||
                visited.Count >= MaxVisited || !visited.Add(node.Address))
            {
                return;
            }

            TryAddSlot(node, panel, slots, seenItems);

            if (!Core.Process.Handle.TryReadMemory<StdVector>(node.Address + ChildrenVecOffset, out var childVec))
            {
                return;
            }

            IntPtr[] kids;
            try
            {
                kids = Core.Process.Handle.ReadStdVector<IntPtr>(childVec, logOnError: false);
            }
            catch
            {
                return;
            }

            var count = Math.Min(kids.Length, MaxChildrenPerNode);
            for (var i = 0; i < count && visited.Count < MaxVisited; i++)
            {
                if (!IsValidUiElementAddress(kids[i]))
                {
                    continue;
                }

                HostUiElement? child;
                try
                {
                    child = node[i];
                }
                catch
                {
                    continue;
                }

                if (child != null)
                {
                    WalkNode(child, panel, slots, visited, seenItems, depth + 1);
                }
            }
        }

        private static bool IsValidUiElement(HostUiElement? el) =>
            el != null && IsValidUiElementAddress(el.Address);

        /// <summary>
        ///     True if <paramref name="addr" /> backs a real, visible UI element. Reads only
        ///     the self-pointer (a valid element stores its own address there) and the visibility
        ///     flag — both via the silent <see cref="GameHelper.Utils.SafeMemoryHandle.TryReadMemory{T}" />,
        ///     so a bad address is rejected without logging.
        /// </summary>
        private static bool IsValidUiElementAddress(IntPtr addr)
        {
            var raw = addr.ToInt64();
            if (raw <= 0x10000 || raw > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            var handle = Core.Process.Handle;
            if (!handle.TryReadMemory<IntPtr>(addr + SelfPtrOffset, out var self) || self != addr)
            {
                return false;
            }

            return handle.TryReadMemory<uint>(addr + FlagsOffset, out var flags) &&
                   UiElementBaseFuncs.IsVisibleChecker(flags);
        }

        private static void TryAddSlot(HostUiElement el, ItemPanel panel, List<IItemSlot> slots, HashSet<IntPtr> seenItems)
        {
            try
            {
                var handle = Core.Process.Handle;
                if (!handle.TryReadMemory<IntPtr>(el.Address + ItemPointerOffset, out var itemPtr))
                {
                    return;
                }

                // Sanity-check the pointer is plausibly a user-space address...
                var raw = itemPtr.ToInt64();
                if (raw <= 0x10000 || raw > 0x7FFFFFFFFFFF)
                {
                    return;
                }

                // Each item only once, even if several UI elements reference it.
                if (!seenItems.Add(itemPtr))
                {
                    return;
                }

                // ...and that it really is an item. Most UI elements hold some other
                // readable pointer at 0x4F8; constructing a host Item on one makes its
                // UpdateData parse a garbage component list. So we first confirm the
                // metadata path silently.
                if (!LooksLikeRealItem(handle, itemPtr))
                {
                    return;
                }

                var item = new HostItem(itemPtr);
                var path = item.Path;
                if (string.IsNullOrEmpty(path) ||
                    !path.StartsWith("Metadata/Items", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var size = el.Size;
                if (size.X <= 0f || size.Y <= 0f)
                {
                    return;
                }

                var rarity = Rarity.Normal;
                var modLines = new List<string>();
                if (item.TryGetComponent<HostMods>(out var mods) && mods != null)
                {
                    rarity = MapRarity(mods.Rarity);
                    AddModLines(modLines, mods.ImplicitMods);
                    AddModLines(modLines, mods.ExplicitMods);
                    AddModLines(modLines, mods.EnchantMods);
                }

                var stack = item.TryGetComponent<HostStack>(out var stackComp) && stackComp != null && stackComp.Count > 1
                    ? stackComp.Count
                    : 1;

                var sdkItem = new InventoryItemAdapter(path, rarity, stack, modLines);
                slots.Add(new ItemSlotAdapter(el.Position, size, panel, sdkItem));
            }
            catch
            {
                // Stale/garbage element: not a slot.
            }
        }

        /// <summary>
        ///     Silently confirms <paramref name="itemPtr" /> points at a real item by reading
        ///     just enough of its entity-details name to check the <c>Metadata/Items</c> prefix.
        ///     Uses only <see cref="HostHandle.TryReadMemory{T}" /> (silent), so a bogus pointer
        ///     is rejected without the logging that full host parsing would emit.
        /// </summary>
        private static bool LooksLikeRealItem(HostHandle handle, IntPtr itemPtr)
        {
            if (!handle.TryReadMemory<ItemStruct>(itemPtr, out var its))
            {
                return false;
            }

            var edRaw = its.EntityDetailsPtr.ToInt64();
            if (edRaw <= 0x10000 || edRaw > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            if (!handle.TryReadMemory<EntityDetails>(its.EntityDetailsPtr, out var ed))
            {
                return false;
            }

            var name = ed.name;

            // Item metadata paths ("Metadata/Items/...") are always long, so they live in a
            // heap buffer (capacity > SSO threshold), never inline. Reject anything else.
            if (name.Length <= 8 || name.Length > 1000 || name.Capacity <= 8 || name.Capacity > 1000)
            {
                return false;
            }

            var bufRaw = name.Buffer.ToInt64();
            if (bufRaw <= 0x10000 || bufRaw > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            // Read 32 bytes (16 UTF-16 chars) of the name silently — enough to cover the
            // 14-char "Metadata/Items" prefix.
            var bytes = new byte[32];
            for (var off = 0; off < 32; off += 8)
            {
                if (!handle.TryReadMemory<ulong>(name.Buffer + off, out var chunk))
                {
                    return false;
                }

                BitConverter.GetBytes(chunk).CopyTo(bytes, off);
            }

            var prefix = Encoding.Unicode.GetString(bytes);
            return prefix.StartsWith("Metadata/Items", StringComparison.OrdinalIgnoreCase);
        }

        private static Rarity MapRarity(HostRarity rarity) => rarity switch
        {
            HostRarity.Normal => Rarity.Normal,
            HostRarity.Magic => Rarity.Magic,
            HostRarity.Rare => Rarity.Rare,
            HostRarity.Unique => Rarity.Unique,
            _ => Rarity.Normal,
        };

        private static void AddModLines(List<string> lines, List<(string name, (float value0, float value1) values)> mods)
        {
            if (mods == null)
            {
                return;
            }

            foreach (var (name, values) in mods)
            {
                var formatted = FormatModLine(name, values);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    lines.Add(formatted);
                }
            }
        }

        private static string FormatModLine(string template, (float value0, float value1) values)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return string.Empty;
            }

            var line = template;
            if (!float.IsNaN(values.value0))
            {
                line = line.Replace("{0}", FormatNumber(values.value0), StringComparison.Ordinal);
                if (!float.IsNaN(values.value1))
                {
                    line = line.Replace("{1}", FormatNumber(values.value1), StringComparison.Ordinal);
                }
            }

            return line.Trim();
        }

        private static string FormatNumber(float value)
        {
            if (Math.Abs(value - MathF.Round(value)) < 0.001f)
            {
                return ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
