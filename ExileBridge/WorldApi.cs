// <copyright file="WorldApi.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>The current area's terrain data (walkable/height grids + target tiles).</summary>
    public interface ITerrain
    {
        /// <summary>Gets the packed walkable bitmap (one row every <see cref="BytesPerRow" /> bytes).</summary>
        byte[] WalkableData { get; }

        /// <summary>Gets the per-cell terrain height grid (indexed [y][x]).</summary>
        float[][] HeightData { get; }

        /// <summary>Gets the number of bytes per row in <see cref="WalkableData" />.</summary>
        int BytesPerRow { get; }

        /// <summary>Gets the area's total tile dimensions.</summary>
        (long X, long Y) TotalTiles { get; }

        /// <summary>Gets the tile height multiplier.</summary>
        int TileHeightMultiplier { get; }

        /// <summary>Gets the world-to-grid conversion factor.</summary>
        float WorldToGridConvertor { get; }

        /// <summary>Gets the named target-tile locations in the area (grid positions).</summary>
        IReadOnlyDictionary<string, IReadOnlyList<Vector2>> TgtTiles { get; }
    }

    /// <summary>A map UI element (mini-map or large map) with its live transform.</summary>
    public interface IMapElement
    {
        /// <summary>Gets a value indicating whether the map is visible.</summary>
        bool IsVisible { get; }

        /// <summary>Gets the element's top-left screen position.</summary>
        Vector2 Position { get; }

        /// <summary>Gets the element's size in pixels.</summary>
        Vector2 Size { get; }

        /// <summary>Gets the map center in screen space.</summary>
        Vector2 Center { get; }

        /// <summary>Gets the player-applied map shift (panning).</summary>
        Vector2 Shift { get; }

        /// <summary>Gets the default map shift.</summary>
        Vector2 DefaultShift { get; }

        /// <summary>Gets the current map zoom factor.</summary>
        float Zoom { get; }
    }

    /// <summary>Player component: the character name.</summary>
    public interface IPlayer : IComponent
    {
        /// <summary>Gets the player character name.</summary>
        string Name { get; }
    }

    /// <summary>Shrine component.</summary>
    public interface IShrine : IComponent
    {
        /// <summary>Gets a value indicating whether the shrine has been used/deactivated.</summary>
        bool IsUsed { get; }
    }

    /// <summary>Targetable component.</summary>
    public interface ITargetable : IComponent
    {
        /// <summary>
        ///     Gets a value indicating whether the entity is currently targetable.
        ///     Note: this is strict (factors in quest/interaction conditions) and is
        ///     often false for ordinary monsters — prefer <see cref="IsHidden" /> for
        ///     "can I attack this monster".
        /// </summary>
        bool IsTargetable { get; }

        /// <summary>Gets a value indicating whether the entity is hidden from the player.</summary>
        bool IsHidden { get; }
    }

    /// <summary>Buffs/debuffs (status effects) currently applied to the entity.</summary>
    public interface IBuffs : IComponent
    {
        /// <summary>Gets the number of active status effects.</summary>
        int Count { get; }

        /// <summary>
        ///     Gets a value indicating whether a status effect with the given
        ///     internal name is currently active on the entity.
        /// </summary>
        /// <param name="buffName">the internal status-effect name (e.g. <c>hidden_monster_6B</c>).</param>
        /// <returns>true if the buff/debuff is present.</returns>
        bool Has(string buffName);
    }

    /// <summary>Chest / strongbox component.</summary>
    public interface IChest : IComponent
    {
        /// <summary>Gets a value indicating whether the chest has been opened.</summary>
        bool IsOpened { get; }

        /// <summary>Gets a value indicating whether the chest is a strongbox.</summary>
        bool IsStrongbox { get; }
    }

    /// <summary>Minimap icon component.</summary>
    public interface IMinimapIcon : IComponent
    {
        /// <summary>Gets the icon name, if any.</summary>
        string? IconName { get; }
    }

    /// <summary>A single named state on a <see cref="IStateMachine" />.</summary>
    public interface IStateMachineState
    {
        /// <summary>Gets the state name.</summary>
        string Name { get; }

        /// <summary>Gets the state value.</summary>
        long Value { get; }
    }

    /// <summary>State machine component (exposes the entity's named states).</summary>
    public interface IStateMachine : IComponent
    {
        /// <summary>
        ///     Gets the raw memory address of the underlying component (escape hatch).
        ///     Combine with <see cref="IGameService.Pid" /> to walk the component's
        ///     listener graph directly.
        /// </summary>
        nint Address { get; }

        /// <summary>Gets the current named states.</summary>
        IReadOnlyList<IStateMachineState> States { get; }

        /// <summary>Tries to read the rune-station socket count for this entity.</summary>
        /// <param name="count">the socket count, when returning true.</param>
        /// <returns>true if available.</returns>
        bool TryGetRuneStationSocketCount(out int count);
    }

    /// <summary>Triggerable blockage component (e.g. closed doors / barriers).</summary>
    public interface ITriggerableBlockage : IComponent
    {
        /// <summary>Gets a value indicating whether the blockage is currently closed/blocking.</summary>
        bool IsBlocked { get; }
    }
}
