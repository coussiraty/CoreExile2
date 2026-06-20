// <copyright file="WorldAdapters.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Sdk
{
    using System.Collections.Generic;
    using System.Numerics;
    using ExileBridge;
    using GameHelper.RemoteObjects.Components;
    using HostAreaInstance = GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance;
    using HostLargeMap = GameHelper.RemoteObjects.UiElement.LargeMapUiElement;
    using HostMapElement = GameHelper.RemoteObjects.UiElement.MapUiElement;

    /// <summary>Adapter exposing a host area's terrain as an SDK <see cref="ITerrain" />.</summary>
    internal sealed class TerrainAdapter : ITerrain
    {
        private readonly HostAreaInstance area;

        internal TerrainAdapter(HostAreaInstance area) => this.area = area;

        /// <inheritdoc />
        public byte[] WalkableData => this.area.GridWalkableData;

        /// <inheritdoc />
        public float[][] HeightData => this.area.GridHeightData;

        /// <inheritdoc />
        public int BytesPerRow => this.area.TerrainMetadata.BytesPerRow;

        /// <inheritdoc />
        public (long X, long Y) TotalTiles =>
            (this.area.TerrainMetadata.TotalTiles.X, this.area.TerrainMetadata.TotalTiles.Y);

        /// <inheritdoc />
        public int TileHeightMultiplier => this.area.TerrainMetadata.TileHeightMultiplier;

        /// <inheritdoc />
        public float WorldToGridConvertor => this.area.WorldToGridConvertor;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, IReadOnlyList<Vector2>> TgtTiles
        {
            get
            {
                var src = this.area.TgtTilesLocations;
                var dict = new Dictionary<string, IReadOnlyList<Vector2>>(src.Count);
                foreach (var kv in src)
                {
                    dict[kv.Key] = kv.Value;
                }

                return dict;
            }
        }
    }

    /// <summary>Adapter exposing a host map UI element as an SDK <see cref="IMapElement" />.</summary>
    internal sealed class MapElementAdapter : IMapElement
    {
        private readonly HostMapElement map;

        internal MapElementAdapter(HostMapElement map) => this.map = map;

        /// <inheritdoc />
        public bool IsVisible => this.map.IsVisible;

        /// <inheritdoc />
        public Vector2 Position => this.map.Position;

        /// <inheritdoc />
        public Vector2 Size => this.map.Size;

        /// <inheritdoc />
        public Vector2 Center => this.map is HostLargeMap large ? large.Center : this.map.Position;

        /// <inheritdoc />
        public Vector2 Shift => this.map.Shift;

        /// <inheritdoc />
        public Vector2 DefaultShift => this.map.DefaultShift;

        /// <inheritdoc />
        public float Zoom => this.map.Zoom;
    }

    /// <summary>Adapter for the <see cref="Player" /> component.</summary>
    internal sealed class PlayerAdapter : IPlayer
    {
        private readonly Player player;

        internal PlayerAdapter(Player player) => this.player = player;

        /// <inheritdoc />
        public string Name => this.player.Name;
    }

    /// <summary>Adapter for the <see cref="Shrine" /> component.</summary>
    internal sealed class ShrineAdapter : IShrine
    {
        private readonly Shrine shrine;

        internal ShrineAdapter(Shrine shrine) => this.shrine = shrine;

        /// <inheritdoc />
        public bool IsUsed => this.shrine.IsUsed;
    }

    /// <summary>Adapter for the <see cref="Targetable" /> component.</summary>
    internal sealed class TargetableAdapter : ITargetable
    {
        private readonly Targetable targetable;

        internal TargetableAdapter(Targetable targetable) => this.targetable = targetable;

        /// <inheritdoc />
        public bool IsTargetable => this.targetable.IsTargetable;
    }

    /// <summary>Adapter for the <see cref="Buffs" /> component.</summary>
    internal sealed class BuffsAdapter : IBuffs
    {
        private readonly Buffs buffs;

        internal BuffsAdapter(Buffs buffs) => this.buffs = buffs;

        /// <inheritdoc />
        public int Count => this.buffs.StatusEffects.Count;

        /// <inheritdoc />
        public bool Has(string buffName) =>
            !string.IsNullOrEmpty(buffName) && this.buffs.StatusEffects.ContainsKey(buffName);
    }

    /// <summary>Adapter for the <see cref="Chest" /> component.</summary>
    internal sealed class ChestAdapter : IChest
    {
        private readonly Chest chest;

        internal ChestAdapter(Chest chest) => this.chest = chest;

        /// <inheritdoc />
        public bool IsOpened => this.chest.IsOpened;

        /// <inheritdoc />
        public bool IsStrongbox => this.chest.IsStrongbox;
    }

    /// <summary>Adapter for the <see cref="MinimapIcon" /> component.</summary>
    internal sealed class MinimapIconAdapter : IMinimapIcon
    {
        private readonly MinimapIcon icon;

        internal MinimapIconAdapter(MinimapIcon icon) => this.icon = icon;

        /// <inheritdoc />
        public string? IconName => this.icon.IconName;
    }

    /// <summary>Adapter for the <see cref="TriggerableBlockage" /> component.</summary>
    internal sealed class TriggerableBlockageAdapter : ITriggerableBlockage
    {
        private readonly TriggerableBlockage blockage;

        internal TriggerableBlockageAdapter(TriggerableBlockage blockage) => this.blockage = blockage;

        /// <inheritdoc />
        public bool IsBlocked => this.blockage.IsBlocked;
    }

    /// <summary>Adapter exposing a host state-machine state as an SDK <see cref="IStateMachineState" />.</summary>
    internal sealed class StateMachineStateAdapter : IStateMachineState
    {
        private readonly StateMachineState state;

        internal StateMachineStateAdapter(StateMachineState state) => this.state = state;

        /// <inheritdoc />
        public string Name => this.state.Name;

        /// <inheritdoc />
        public long Value => this.state.Value;
    }

    /// <summary>Adapter for the <see cref="StateMachine" /> component.</summary>
    internal sealed class StateMachineAdapter : IStateMachine
    {
        private readonly StateMachine machine;

        internal StateMachineAdapter(StateMachine machine) => this.machine = machine;

        /// <inheritdoc />
        public nint Address => this.machine.Address;

        /// <inheritdoc />
        public IReadOnlyList<IStateMachineState> States
        {
            get
            {
                var src = this.machine.States;
                var list = new List<IStateMachineState>(src.Count);
                foreach (var s in src)
                {
                    list.Add(new StateMachineStateAdapter(s));
                }

                return list;
            }
        }

        /// <inheritdoc />
        public bool TryGetRuneStationSocketCount(out int count) =>
            this.machine.TryGetRuneStationSocketCount(out count);
    }
}
