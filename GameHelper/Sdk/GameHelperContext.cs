// <copyright file="GameHelperContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Sdk
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using Coroutine;
    using ExileBridge;
    using GameHelper.CoroutineEvents;
    using GameHelper.RemoteEnums;

    /// <summary>
    ///     The host-side implementation of <see cref="IContext" />. It is a thin
    ///     facade over the static <see cref="Core" /> god-object and the
    ///     <c>RemoteObjects</c>, exposed to plugins as pure SDK interfaces.
    ///     A single shared instance is injected into every ExileBridge plugin.
    /// </summary>
    internal sealed class GameHelperContext : IContext
    {
        /// <summary>The shared context instance handed to all plugins.</summary>
        internal static readonly GameHelperContext Instance = new();

        private GameHelperContext()
        {
            this.Game = new GameServiceAdapter();
            this.Entities = new EntitiesServiceAdapter();
            this.Ui = new UiServiceAdapter();
            this.Render = new RenderServiceAdapter();
            this.Events = new EventsServiceAdapter();
            this.Overlay = new OverlayServiceAdapter();
            this.Log = new LogServiceAdapter();
        }

        /// <inheritdoc />
        public IGameService Game { get; }

        /// <inheritdoc />
        public IEntitiesService Entities { get; }

        /// <inheritdoc />
        public IUiService Ui { get; }

        /// <inheritdoc />
        public IRenderService Render { get; }

        /// <inheritdoc />
        public IEventsService Events { get; }

        /// <inheritdoc />
        public IOverlayService Overlay { get; }

        /// <inheritdoc />
        public ILogService Log { get; }

        /// <summary>Maps the host's detailed state enum to the curated SDK enum.</summary>
        /// <param name="state">host state.</param>
        /// <returns>curated SDK state.</returns>
        internal static GameState MapState(GameStateTypes state) => state switch
        {
            GameStateTypes.InGameState => GameState.InGame,
            GameStateTypes.EscapeState => GameState.Escape,
            GameStateTypes.AreaLoadingState or GameStateTypes.LoadingState => GameState.Loading,
            GameStateTypes.LoginState or GameStateTypes.PreGameState or GameStateTypes.WaitingState => GameState.Login,
            GameStateTypes.GameNotLoaded => GameState.NotLoaded,
            _ => GameState.Other,
        };
    }

    /// <summary>Game/process/window state adapter.</summary>
    internal sealed class GameServiceAdapter : IGameService
    {
        private readonly IInGame inGame = new InGameAdapter();

        /// <inheritdoc />
        public GameState State => GameHelperContext.MapState(Core.States.GameCurrentState);

        /// <inheritdoc />
        public bool IsAttached => Core.Process.Address != IntPtr.Zero;

        /// <inheritdoc />
        public bool IsInGame => Core.States.GameCurrentState == GameStateTypes.InGameState;

        /// <inheritdoc />
        public bool IsForeground => Core.Process.Foreground;

        /// <inheritdoc />
        public uint Pid => Core.Process.Pid;

        /// <inheritdoc />
        public bool IsControllerMode => Core.GHSettings.EnableControllerMode;

        /// <inheritdoc />
        public RectInfo WindowArea
        {
            get
            {
                var a = Core.Process.WindowArea;
                return new RectInfo(a.X, a.Y, a.Width, a.Height);
            }
        }

        /// <inheritdoc />
        public IInGame InGame => this.inGame;
    }

    /// <summary>In-game world/area adapter.</summary>
    internal sealed class InGameAdapter : IInGame
    {
        /// <inheritdoc />
        public string AreaName => Core.States.AreaLoading.CurrentAreaName;

        /// <inheritdoc />
        public string AreaHash => Core.States.InGameStateObject.CurrentAreaInstance.AreaHash;

        /// <inheritdoc />
        public int AreaLevel => Core.States.InGameStateObject.CurrentAreaInstance.CurrentAreaLevel;

        /// <inheritdoc />
        public bool IsTown => Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.IsTown;

        /// <inheritdoc />
        public bool IsHideout => Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.IsHideout;

        /// <inheritdoc />
        public string AreaId => Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.Id;

        /// <inheritdoc />
        public ITerrain Terrain => new TerrainAdapter(Core.States.InGameStateObject.CurrentAreaInstance);

        /// <inheritdoc />
        public IEntity Player => new EntityAdapter(Core.States.InGameStateObject.CurrentAreaInstance.Player);
    }

    /// <summary>World-to-screen projection adapter.</summary>
    internal sealed class RenderServiceAdapter : IRenderService
    {
        /// <inheritdoc />
        public Vector2 WorldToScreen(Vector3 worldPosition) =>
            Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(
                new GameOffsets.Natives.StdTuple3D<float>
                {
                    X = worldPosition.X,
                    Y = worldPosition.Y,
                    Z = worldPosition.Z,
                });

        /// <inheritdoc />
        public Vector2 WorldToScreen(Vector3 worldPosition, float height) =>
            Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(
                new GameOffsets.Natives.StdTuple3D<float>
                {
                    X = worldPosition.X,
                    Y = worldPosition.Y,
                    Z = worldPosition.Z,
                },
                height);
    }

    /// <summary>In-game UI adapter.</summary>
    internal sealed class UiServiceAdapter : IUiService
    {
        /// <inheritdoc />
        public bool IsAnyLargePanelOpen => Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen;

        /// <inheritdoc />
        public nint GameUiAddress => Core.States.InGameStateObject.GameUi.Address;

        /// <inheritdoc />
        public IUiElement SekhemaTrialPanel => new UiElementAdapter(Core.States.InGameStateObject.GameUi.SekhemasTrialMapPanel);

        /// <inheritdoc />
        public IUiElement Atlas => new UiElementAdapter(Core.States.InGameStateObject.GameUi.Atlas);

        /// <inheritdoc />
        public IUiElement RightPanel => new UiElementAdapter(Core.States.InGameStateObject.GameUi.RightPanel);

        /// <inheritdoc />
        public IUiElement WorldMapPanel => new UiElementAdapter(Core.States.InGameStateObject.GameUi.WorldMapPanel);

        /// <inheritdoc />
        public IReadOnlyList<IAtlasMapNode> AtlasMaps
        {
            get
            {
                var src = Core.States.InGameStateObject.GameUi.AtlasMaps;
                var list = new List<IAtlasMapNode>(src.Count);
                foreach (var node in src)
                {
                    list.Add(new AtlasMapNodeAdapter(node));
                }

                return list;
            }
        }

        /// <inheritdoc />
        public IMapElement MiniMap => new MapElementAdapter(Core.States.InGameStateObject.GameUi.MiniMap);

        /// <inheritdoc />
        public IMapElement LargeMap => new MapElementAdapter(Core.States.InGameStateObject.GameUi.LargeMap);

        /// <inheritdoc />
        public Vector2 BaseResolution => new(
            (float)GameOffsets.Objects.UiElement.UiElementBaseFuncs.BaseResolution.X,
            (float)GameOffsets.Objects.UiElement.UiElementBaseFuncs.BaseResolution.Y);

        /// <inheritdoc />
        public IReadOnlyList<IItemSlot> EnumerateOpenItemSlots() => ItemSlotScanner.Scan();

        /// <inheritdoc />
        public IUiElement? FindElementByStringId(string stringId, IUiElement? root = null)
        {
            if (string.IsNullOrEmpty(stringId))
            {
                return null;
            }

            var roots = new List<RemoteObjects.UiElement.UiElementBase>();
            if (root is UiElementAdapter adapter)
            {
                roots.Add(adapter.Element);
            }
            else
            {
                var gameUi = Core.States.InGameStateObject.GameUi;
                if (gameUi.LeftPanel != null)
                {
                    roots.Add(gameUi.LeftPanel);
                }

                if (gameUi.RightPanel != null)
                {
                    roots.Add(gameUi.RightPanel);
                }
            }

            var visited = new HashSet<nint>();
            foreach (var r in roots)
            {
                var found = FindByStringId(r, stringId, visited, 0);
                if (found != null)
                {
                    return new UiElementAdapter(found);
                }
            }

            return null;
        }

        private static RemoteObjects.UiElement.UiElementBase FindByStringId(
            RemoteObjects.UiElement.UiElementBase node, string stringId, HashSet<nint> visited, int depth)
        {
            if (node == null || node.Address == IntPtr.Zero || depth > 64 ||
                visited.Count > 20000 || !visited.Add(node.Address))
            {
                return null;
            }

            try
            {
                if (string.Equals(node.StringId, stringId, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
            catch
            {
                // unreadable StringId — skip this node, keep walking.
            }

            var count = node.TotalChildrens;
            for (var i = 0; i < count; i++)
            {
                RemoteObjects.UiElement.UiElementBase child;
                try
                {
                    child = node[i];
                }
                catch
                {
                    continue;
                }

                var found = FindByStringId(child, stringId, visited, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }

    /// <summary>Overlay metrics + texture management adapter.</summary>
    internal sealed class OverlayServiceAdapter : IOverlayService
    {
        /// <inheritdoc />
        public Vector2 Size
        {
            get
            {
                var s = Core.Overlay.Size;
                return new Vector2(s.Width, s.Height);
            }
        }

        /// <inheritdoc />
        public void AddOrGetImage(string filePath, out nint handle, out uint width, out uint height) =>
            Core.Overlay.AddOrGetImagePointer(filePath, false, out handle, out width, out height);

        /// <inheritdoc />
        public void AddOrGetImage(string key, SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, bool srgb, out nint handle) =>
            Core.Overlay.AddOrGetImagePointer(key, image, srgb, out handle);

        /// <inheritdoc />
        public bool RemoveImage(string filePath) => Core.Overlay.RemoveImage(filePath);
    }

    /// <summary>Logging adapter (writes to the host console/log).</summary>
    internal sealed class LogServiceAdapter : ILogService
    {
        /// <inheritdoc />
        public void Debug(string message) => Console.WriteLine($"[Plugin][DEBUG] {message}");

        /// <inheritdoc />
        public void Info(string message) => Console.WriteLine($"[Plugin][INFO] {message}");

        /// <inheritdoc />
        public void Warn(string message) => Console.WriteLine($"[Plugin][WARN] {message}");

        /// <inheritdoc />
        public void Error(string message) => Console.WriteLine($"[Plugin][ERROR] {message}");
    }

    /// <summary>
    ///     Event adapter. Each subscription starts a coroutine that waits on the
    ///     corresponding host event and invokes the handler; disposing the returned
    ///     token cancels that coroutine (i.e. unsubscribes).
    /// </summary>
    internal sealed class EventsServiceAdapter : IEventsService
    {
        /// <inheritdoc />
        public IDisposable OnAreaChange(Action handler) => Subscribe(RemoteEvents.AreaChanged, handler);

        /// <inheritdoc />
        public IDisposable OnFrame(Action handler) => Subscribe(GameHelperEvents.OnRender, handler);

        /// <inheritdoc />
        public IDisposable OnGameAttached(Action handler) => Subscribe(Core.Process.OnStaticAddressFound, handler);

        /// <inheritdoc />
        public IDisposable OnGameDetached(Action handler) => Subscribe(GameHelperEvents.OnClose, handler);

        /// <inheritdoc />
        public IDisposable OnWindowMove(Action handler) => Subscribe(GameHelperEvents.OnMoved, handler);

        /// <inheritdoc />
        public IDisposable OnForegroundChange(Action handler) => Subscribe(GameHelperEvents.OnForegroundChanged, handler);

        private static IDisposable Subscribe(Event hostEvent, Action handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var active = CoroutineHandler.Start(Loop(hostEvent, handler));
            return new CoroutineSubscription(active);
        }

        private static IEnumerator<Wait> Loop(Event hostEvent, Action handler)
        {
            while (true)
            {
                yield return new Wait(hostEvent);
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ExileBridge.Events] plugin handler threw: {ex}");
                }
            }
        }

        private sealed class CoroutineSubscription : IDisposable
        {
            private ActiveCoroutine? active;

            internal CoroutineSubscription(ActiveCoroutine active) => this.active = active;

            public void Dispose()
            {
                this.active?.Cancel();
                this.active = null;
            }
        }
    }
}
