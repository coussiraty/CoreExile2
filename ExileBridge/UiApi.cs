// <copyright file="UiApi.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>An integer grid coordinate (e.g. an Atlas node position).</summary>
    /// <param name="X">grid X.</param>
    /// <param name="Y">grid Y.</param>
    public readonly record struct GridPoint(int X, int Y);

    /// <summary>Observable Atlas map node states.</summary>
    public enum AtlasMapNodeState
    {
        /// <summary>Not accessible / not yet completed.</summary>
        None,

        /// <summary>Accessible to travel to now.</summary>
        AccessibleNow,

        /// <summary>The base map has been completed.</summary>
        CompletedBase,
    }

    /// <summary>A read-only view of a game UI element.</summary>
    public interface IUiElement
    {
        /// <summary>
        ///     Gets the raw memory address of the underlying UI element (escape hatch).
        ///     Combine with <see cref="IGameService.Pid" /> to walk the element's
        ///     pointer graph directly for data the curated services do not expose.
        /// </summary>
        nint Address { get; }

        /// <summary>Gets a value indicating whether the element is resolved in memory.</summary>
        bool Exists { get; }

        /// <summary>Gets a value indicating whether the element is currently visible.</summary>
        bool IsVisible { get; }

        /// <summary>Gets the element's top-left screen position.</summary>
        Vector2 Position { get; }

        /// <summary>Gets the element's size in pixels.</summary>
        Vector2 Size { get; }

        /// <summary>
        ///     Gets the element's StringId — a stable string identifier the game assigns to
        ///     many panels/containers (e.g. the inventory or stash grid). Empty when none.
        ///     Far more robust than positional/offset heuristics for locating a known panel.
        /// </summary>
        string StringId { get; }

        /// <summary>Gets the number of child elements.</summary>
        int ChildCount { get; }

        /// <summary>Gets the child element at <paramref name="index" />, or null.</summary>
        /// <param name="index">child index.</param>
        /// <returns>the child element, or null when out of range / unresolved.</returns>
        IUiElement? Child(int index);
    }

    /// <summary>A read-only view of an endgame Atlas map node.</summary>
    public interface IAtlasMapNode
    {
        /// <summary>Gets the child index under the Atlas UI element.</summary>
        int Index { get; }

        /// <summary>Gets the node's Atlas grid position.</summary>
        GridPoint GridPosition { get; }

        /// <summary>Gets the grid positions this node connects to.</summary>
        IReadOnlyList<GridPoint> ConnectedGridPositions { get; }

        /// <summary>Gets the internal (locale-independent) map id.</summary>
        string MapId { get; }

        /// <summary>Gets the human-readable display name.</summary>
        string DisplayName { get; }

        /// <summary>Gets the biome id.</summary>
        byte BiomeId { get; }

        /// <summary>Gets the discovered completion/accessibility state.</summary>
        AtlasMapNodeState State { get; }

        /// <summary>Gets the number of content badges on this node.</summary>
        int BadgeCount { get; }

        /// <summary>Gets the raw content/badge names attached to this node.</summary>
        IReadOnlyList<string> ContentNames { get; }

        /// <summary>Gets the map type classification ("normal" or "unique").</summary>
        string Type { get; }

        /// <summary>Gets the feature tags (e.g. "lineage", "arbiter") for this map.</summary>
        IReadOnlyList<string> Tags { get; }

        /// <summary>Returns the merged, de-duplicated content display names.</summary>
        /// <param name="includeUnmapped">include unmapped values as raw hex.</param>
        /// <returns>the content display names.</returns>
        IReadOnlyList<string> GetContentDisplayNames(bool includeUnmapped = true);
    }
}
