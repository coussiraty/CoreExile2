// <copyright file="InventoryApi.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>Which open panel an item slot belongs to.</summary>
    public enum ItemPanel
    {
        /// <summary>The left-side panel (stash when a stash is open).</summary>
        Left,

        /// <summary>The right-side panel (the player inventory).</summary>
        Right,
    }

    /// <summary>
    ///     A read-only view of an item sitting in an open stash/inventory slot,
    ///     carrying just the data needed to price it (no native types leak to plugins).
    /// </summary>
    public interface IInventoryItem
    {
        /// <summary>Gets the metadata path (e.g. <c>Metadata/Items/Currency/CurrencyChaosOrb</c>).</summary>
        string Path { get; }

        /// <summary>Gets the item rarity.</summary>
        Rarity Rarity { get; }

        /// <summary>Gets the stack size (1 for non-stackable items).</summary>
        int StackCount { get; }

        /// <summary>
        ///     Gets the item's mod lines (implicit + explicit + enchant), formatted with
        ///     their numeric values substituted. Empty for items without mods.
        /// </summary>
        IReadOnlyList<string> ModLines { get; }
    }

    /// <summary>
    ///     An item occupying a visible slot in an open stash/inventory panel, with the
    ///     slot's on-screen rectangle so a plugin can draw over it.
    /// </summary>
    public interface IItemSlot
    {
        /// <summary>Gets the slot's top-left position in screen pixels.</summary>
        Vector2 Position { get; }

        /// <summary>Gets the slot's size in screen pixels.</summary>
        Vector2 Size { get; }

        /// <summary>Gets which panel the slot belongs to.</summary>
        ItemPanel Panel { get; }

        /// <summary>Gets the item in the slot.</summary>
        IInventoryItem Item { get; }
    }
}
