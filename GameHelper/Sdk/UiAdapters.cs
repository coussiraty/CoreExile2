// <copyright file="UiAdapters.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Sdk
{
    using System;
    using System.Collections.Generic;
    using ExileBridge;
    using HostAtlasNode = GameHelper.RemoteObjects.States.InGameStateObjects.AtlasMapNode;
    using HostAtlasNodeState = GameHelper.RemoteObjects.States.InGameStateObjects.AtlasMapNodeState;
    using HostUiElement = GameHelper.RemoteObjects.UiElement.UiElementBase;

    /// <summary>Adapter exposing a host <see cref="HostUiElement" /> as an SDK <see cref="IUiElement" />.</summary>
    internal sealed class UiElementAdapter : IUiElement
    {
        private readonly HostUiElement element;

        internal UiElementAdapter(HostUiElement element) => this.element = element;

        /// <inheritdoc />
        public nint Address => this.element.Address;

        /// <inheritdoc />
        public bool Exists => this.element.Address != IntPtr.Zero;

        /// <inheritdoc />
        public bool IsVisible => this.element.IsVisible;

        /// <inheritdoc />
        public System.Numerics.Vector2 Position => this.element.Position;

        /// <inheritdoc />
        public System.Numerics.Vector2 Size => this.element.Size;

        /// <inheritdoc />
        public int ChildCount => this.element.TotalChildrens;

        /// <inheritdoc />
        public IUiElement? Child(int index)
        {
            var child = this.element[index];
            return child == null ? null : new UiElementAdapter(child);
        }
    }

    /// <summary>Adapter exposing a host <see cref="HostAtlasNode" /> as an SDK <see cref="IAtlasMapNode" />.</summary>
    internal sealed class AtlasMapNodeAdapter : IAtlasMapNode
    {
        private readonly HostAtlasNode node;

        internal AtlasMapNodeAdapter(HostAtlasNode node) => this.node = node;

        /// <inheritdoc />
        public int Index => this.node.Index;

        /// <inheritdoc />
        public GridPoint GridPosition => new(this.node.GridPosition.X, this.node.GridPosition.Y);

        /// <inheritdoc />
        public IReadOnlyList<GridPoint> ConnectedGridPositions
        {
            get
            {
                var src = this.node.ConnectedGridPositions;
                var list = new List<GridPoint>(src.Count);
                foreach (var p in src)
                {
                    list.Add(new GridPoint(p.X, p.Y));
                }

                return list;
            }
        }

        /// <inheritdoc />
        public string MapId => this.node.MapId;

        /// <inheritdoc />
        public string DisplayName => this.node.DisplayName;

        /// <inheritdoc />
        public byte BiomeId => this.node.BiomeId;

        /// <inheritdoc />
        public AtlasMapNodeState State => this.node.State switch
        {
            HostAtlasNodeState.AccessibleNow => AtlasMapNodeState.AccessibleNow,
            HostAtlasNodeState.CompletedBase => AtlasMapNodeState.CompletedBase,
            _ => AtlasMapNodeState.None,
        };

        /// <inheritdoc />
        public int BadgeCount => this.node.BadgeCount;

        /// <inheritdoc />
        public IReadOnlyList<string> ContentNames => this.node.ContentNames;

        /// <inheritdoc />
        public string Type => this.node.Type;

        /// <inheritdoc />
        public IReadOnlyList<string> Tags => this.node.Tags;

        /// <inheritdoc />
        public IReadOnlyList<string> GetContentDisplayNames(bool includeUnmapped = true) =>
            this.node.GetContentDisplayNames(includeUnmapped);
    }
}
