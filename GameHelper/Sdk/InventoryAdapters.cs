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
        internal InventoryItemAdapter(string path, string displayName, Rarity rarity, int stackCount, IReadOnlyList<string> modLines)
        {
            this.Path = path;
            this.DisplayName = displayName;
            this.Rarity = rarity;
            this.StackCount = stackCount;
            this.ModLines = modLines;
        }

        /// <inheritdoc />
        public string Path { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

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
        private const int ParentPtrOffset = 0xB8;
        private const int FlagsOffset = 0x180;

        // Bound the per-frame UI tree walk so a corrupt tree cannot hang the overlay.
        // Generous: premium stash tabs (quad / special) have large UI trees.
        private const int MaxVisited = 40000;
        private const int MaxDepth = 64;
        private const int MaxChildrenPerNode = 8192;

        // Set true to log per-scan coverage to the console (logs/console.log), throttled.
        internal static bool Diagnostics = false;

        private static long lastDiagTick;

        /// <summary>A located item-slot element: its on-screen rect and the item pointer it holds.</summary>
        private readonly struct Candidate
        {
            public Candidate(IntPtr itemPtr, ItemPanel panel, Vector2 pos, Vector2 size)
            {
                this.ItemPtr = itemPtr;
                this.Panel = panel;
                this.Pos = pos;
                this.Size = size;
            }

            public IntPtr ItemPtr { get; }

            public ItemPanel Panel { get; }

            public Vector2 Pos { get; }

            public Vector2 Size { get; }
        }

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
                var merchant = gameUi.MerchantPanel;
                var leftVisible = IsValidUiElement(left);
                var rightVisible = IsValidUiElement(right);
                var merchantVisible = IsValidUiElement(merchant);

                // Resolve real localized item names from the game's BaseItemTypes table (once).
                if (!ItemBaseNames.Built && (leftVisible || rightVisible || merchantVisible))
                {
                    ItemBaseNames.EnsureBuilt(
                        left?.Address ?? IntPtr.Zero,
                        right?.Address ?? IntPtr.Zero,
                        gameUi.Address);
                }

                // Pass 1: walk both panels and collect EVERY element that holds a real item
                // pointer at +0x4F8, paired with that element's own on-screen rect. No dedup yet.
                var candidates = new List<Candidate>();
                var visited = new HashSet<IntPtr>();
                if (leftVisible)
                {
                    WalkCollect(left, null, ItemPanel.Left, candidates, visited, 0);
                }

                if (rightVisible)
                {
                    WalkCollect(right, null, ItemPanel.Right, candidates, visited, 0);
                }

                if (merchantVisible)
                {
                    WalkCollect(merchant, null, ItemPanel.Merchant, candidates, visited, 0);
                }

                // Each item is usually referenced by several UI elements: the visible slot, an
                // inner image, and — on premium tabs — off-panel "ghost" copies positioned far
                // off-screen. For each unique item pick the single element whose rect actually
                // sits inside the panel: that is the real, on-screen slot. Items with no
                // on-panel element have nowhere valid to draw and are dropped. This replaces
                // both the double-price dedup AND the old all-or-nothing special-tab suppression.
                var leftRect = PanelRect(leftVisible ? left : null);
                var rightRect = PanelRect(rightVisible ? right : null);
                var merchantRect = PanelRect(merchantVisible ? merchant : null);

                var best = new Dictionary<IntPtr, Candidate>();
                foreach (var c in candidates)
                {
                    if (!best.TryGetValue(c.ItemPtr, out var prev))
                    {
                        best[c.ItemPtr] = c;
                    }
                    else if (IsOnPanel(c, leftRect, rightRect, merchantRect) && !IsOnPanel(prev, leftRect, rightRect, merchantRect))
                    {
                        best[c.ItemPtr] = c;
                    }
                }

                var dropped = 0;
                foreach (var c in best.Values)
                {
                    if (!IsOnPanel(c, leftRect, rightRect, merchantRect) || c.Size.X <= 0f || c.Size.Y <= 0f)
                    {
                        dropped++;
                        continue;
                    }

                    var slot = BuildSlot(c);
                    if (slot != null)
                    {
                        slots.Add(slot);
                    }
                    else
                    {
                        dropped++;
                    }
                }

                if (Diagnostics && (leftVisible || rightVisible))
                {
                    var now = Environment.TickCount64;
                    if (now - lastDiagTick > 1000)
                    {
                        lastDiagTick = now;
                        Console.WriteLine($"[StashValue scan] candidates={candidates.Count} " +
                                          $"unique={best.Count} shown={slots.Count} dropped={dropped}");
                    }
                }
            }
            catch
            {
                // Any read failure: return whatever we gathered so far (possibly empty).
            }

            return slots;
        }

        /// <summary>True when the candidate's center lies inside its panel's on-screen rect.</summary>
        private static bool IsOnPanel(Candidate c, (Vector2 min, Vector2 max)? leftRect, (Vector2 min, Vector2 max)? rightRect, (Vector2 min, Vector2 max)? merchantRect)
        {
            var rect = c.Panel switch
            {
                ItemPanel.Left => leftRect,
                ItemPanel.Merchant => merchantRect,
                _ => rightRect,
            };
            if (rect == null)
            {
                return false;
            }

            var center = c.Pos + (c.Size * 0.5f);
            return center.X >= rect.Value.min.X && center.X <= rect.Value.max.X &&
                   center.Y >= rect.Value.min.Y && center.Y <= rect.Value.max.Y;
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

        /// <summary>
        ///     Walks <paramref name="node" /> and its visible descendants via the host indexer,
        ///     so each materialized child uses the host's live <c>rootCache</c> for positioning
        ///     (correct, refreshed every tick). Children addresses are validated with raw reads
        ///     first so the indexer is only handed genuine, visible elements. The loop is bounded
        ///     by the fresh children vector so a stale cached child count cannot truncate it.
        ///     Collects (item pointer + element rect) candidates; dedup/positioning happen later.
        /// </summary>
        private static void WalkCollect(HostUiElement node, HostUiElement? parent, ItemPanel panel, List<Candidate> candidates, HashSet<IntPtr> visited, int depth)
        {
            if (node == null || node.Address == IntPtr.Zero || depth > MaxDepth ||
                visited.Count >= MaxVisited || !visited.Add(node.Address))
            {
                return;
            }

            CollectCandidate(node, parent, panel, candidates);

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
                    WalkCollect(child, node, panel, candidates, visited, depth + 1);
                }
            }
        }

        /// <summary>
        ///     True only when EVERY ancestor up the parent chain (read raw, not via the
        ///     rootCache) also has its local visible bit set. An element that belongs to an
        ///     INACTIVE stash tab still passes the single-level <see cref="IsValidUiElementAddress" />
        ///     check (its own visible bit stays set), but one of its ancestors — the inactive
        ///     tab's container — is hidden. Walking the chain rejects those, which is what stops
        ///     a background currency/fragment tab's slots from drawing on top of the active tab.
        /// </summary>
        internal static bool IsChainVisible(IntPtr addr)
        {
            try
            {
                var handle = Core.Process.Handle;
                var cur = addr;
                for (var i = 0; i < 32; i++)
                {
                    if (!handle.TryReadMemory<uint>(cur + FlagsOffset, out var flags) ||
                        !UiElementBaseFuncs.IsVisibleChecker(flags))
                    {
                        return false;
                    }

                    if (!handle.TryReadMemory<IntPtr>(cur + ParentPtrOffset, out var parent))
                    {
                        return true;
                    }

                    var praw = parent.ToInt64();
                    if (praw <= 0x10000 || praw > 0x7FFFFFFFFFFF)
                    {
                        // Reached the top (no further parent): the whole chain checked out.
                        return true;
                    }

                    // Stop if the parent isn't a genuine UI element (don't walk garbage).
                    if (!handle.TryReadMemory<IntPtr>(parent + SelfPtrOffset, out var self) || self != parent)
                    {
                        return true;
                    }

                    cur = parent;
                }

                return true;
            }
            catch
            {
                return false;
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

        /// <summary>
        ///     If <paramref name="el" /> holds a real item pointer at <c>+0x4F8</c>, records a
        ///     <see cref="Candidate" /> pairing that item with the element's live rect. No dedup
        ///     and no host <c>Item</c> construction yet — both happen once per unique item in
        ///     <see cref="BuildSlot" />, after the on-panel element has been chosen.
        /// </summary>
        private static void CollectCandidate(HostUiElement el, HostUiElement? parent, ItemPanel panel, List<Candidate> candidates)
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

                // ...and that it really is an item. Most UI elements hold some other readable
                // pointer at 0x4F8; constructing a host Item on one makes its UpdateData parse a
                // garbage component list. So we confirm the metadata path silently first.
                if (!LooksLikeRealItem(handle, itemPtr))
                {
                    return;
                }

                // Reject slots that belong to an inactive stash tab still cached in the UI tree
                // (a background currency/fragment tab whose container is hidden). Their own
                // visible bit is set, so only the full parent-chain walk excludes them.
                if (!IsChainVisible(el.Address))
                {
                    return;
                }

                // The element that physically holds the item pointer can drift away from its
                // visible slot — premium currency tabs park it in the tab's centre, which made
                // prices float in empty cells. Its immediate parent IS the cell and is positioned
                // correctly, so when that parent is cell-sized use ITS rect. A large parent (a
                // whole grid container) is not a cell, so for normal grid tabs we keep self.
                var pos = el.Position;
                var size = el.Size;
                if (parent != null)
                {
                    var psize = parent.Size;
                    if (psize.X >= 20f && psize.X <= 160f && psize.Y >= 20f && psize.Y <= 160f)
                    {
                        pos = parent.Position;
                        size = psize;
                    }
                }

                candidates.Add(new Candidate(itemPtr, panel, pos, size));
            }
            catch
            {
                // Stale/garbage element: not a slot.
            }
        }

        /// <summary>
        ///     Builds the SDK slot for a chosen on-panel <see cref="Candidate" />: constructs the
        ///     host item, verifies its metadata path, and pulls rarity / mod lines / stack count.
        ///     Returns null when the pointer does not resolve to a real item.
        /// </summary>
        private static IItemSlot? BuildSlot(Candidate c)
        {
            try
            {
                var sdkItem = BuildItem(new HostItem(c.ItemPtr));
                return sdkItem == null ? null : new ItemSlotAdapter(c.Pos, c.Size, c.Panel, sdkItem);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     Builds an SDK item view from a host item: verifies the metadata path, resolves the
        ///     localized display name from the game's BaseItemTypes table, and pulls rarity /
        ///     mod lines / stack count. Returns null when the item does not resolve to a real item.
        /// </summary>
        private static IInventoryItem? BuildItem(HostItem item)
        {
            var path = item.Path;
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("Metadata/Items", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var slash = path.LastIndexOf('/');
            var baseName = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
            var displayName = ItemBaseNames.TryGetName(baseName, out var resolved) ? resolved : string.Empty;

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

            return new InventoryItemAdapter(path, displayName, rarity, stack, modLines);
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
