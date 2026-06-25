// <copyright file="WorldItem.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.Components
{
    using System;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using ImGuiNET;

    /// <summary>
    ///     The <see cref="WorldItem" /> component, found on a dropped (ground) item entity.
    ///     A ground item is a "WorldItem" entity that wraps the real item entity; the game
    ///     stores the wrapped item-entity pointer at <c>component + 0x28</c>. This component
    ///     resolves that inner item and surfaces the data needed to identify/loot it.
    /// </summary>
    public class WorldItem : ComponentBase
    {
        // Offset of the wrapped item-entity pointer inside the WorldItem component.
        // Verified 2026-06-25 (consistent across many ground items).
        private const int ItemEntityOffset = 0x28;

        /// <summary>Initializes a new instance of the <see cref="WorldItem" /> class.</summary>
        /// <param name="address">address of the WorldItem component.</param>
        public WorldItem(IntPtr address)
            : base(address) { }

        /// <summary>Gets the wrapped item's metadata path (e.g. <c>Metadata/Items/Currency/...</c>).</summary>
        public string ItemPath { get; private set; } = string.Empty;

        /// <summary>Gets the wrapped item's rarity.</summary>
        public Rarity Rarity { get; private set; } = Rarity.Normal;

        /// <summary>Gets the wrapped item's stack size (1 for non-stackable).</summary>
        public int StackCount { get; private set; } = 1;

        /// <inheritdoc />
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Item Path: {this.ItemPath}");
            ImGui.Text($"Rarity: {this.Rarity}");
            ImGui.Text($"Stack: {this.StackCount}");
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            base.UpdateData(hasAddressChanged);

            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<IntPtr>(this.Address + ItemEntityOffset, out var itemPtr) ||
                itemPtr == IntPtr.Zero)
            {
                this.ItemPath = string.Empty;
                this.Rarity = Rarity.Normal;
                this.StackCount = 1;
                return;
            }

            try
            {
                var item = new Item(itemPtr);
                this.ItemPath = item.Path ?? string.Empty;
                this.Rarity = item.TryGetComponent<Mods>(out var mods) ? mods.Rarity : Rarity.Normal;
                this.StackCount = item.TryGetComponent<Stack>(out var stack) && stack.Count > 1 ? stack.Count : 1;
            }
            catch
            {
                this.ItemPath = string.Empty;
                this.Rarity = Rarity.Normal;
                this.StackCount = 1;
            }
        }
    }
}
