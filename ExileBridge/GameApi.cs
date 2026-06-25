// <copyright file="GameApi.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    ///     High-level state the game can be in (curated; a subset/grouping of the
    ///     host's internal states).
    /// </summary>
    public enum GameState
    {
        /// <summary>Game process not attached / not started.</summary>
        NotLoaded,

        /// <summary>Login screen / pre-login flow.</summary>
        Login,

        /// <summary>Loading / area transition.</summary>
        Loading,

        /// <summary>Player is in a town, hideout, map or zone.</summary>
        InGame,

        /// <summary>The in-game escape menu is open.</summary>
        Escape,

        /// <summary>Any other known state not relevant to most plugins.</summary>
        Other,
    }

    /// <summary>Entity category (mirrors the host's entity categorization).</summary>
    public enum EntityType
    {
        /// <summary>Not categorized yet.</summary>
        Unidentified,

        /// <summary>A chest / strongbox.</summary>
        Chest,

        /// <summary>A non-player character.</summary>
        Npc,

        /// <summary>A player entity.</summary>
        Player,

        /// <summary>A shrine.</summary>
        Shrine,

        /// <summary>A monster.</summary>
        Monster,

        /// <summary>A delirium bomb spawner object.</summary>
        DeliriumBomb,

        /// <summary>A delirium monster spawner object.</summary>
        DeliriumSpawner,

        /// <summary>A user-flagged important object (league/terrain objects).</summary>
        OtherImportantObjects,

        /// <summary>A ground/inventory item.</summary>
        Item,

        /// <summary>A renderable ground object.</summary>
        Renderable,
    }

    /// <summary>Entity sub-category (mirrors the host's entity sub-types).</summary>
    public enum EntitySubtype
    {
        /// <summary>Not sub-categorized yet.</summary>
        Unidentified,

        /// <summary>No sub-type required.</summary>
        None,

        /// <summary>A player controlled by the current user.</summary>
        PlayerSelf,

        /// <summary>A player controlled by another user.</summary>
        PlayerOther,

        /// <summary>A chest of magic rarity.</summary>
        ChestWithMagicRarity,

        /// <summary>A chest of rare rarity.</summary>
        ChestWithRareRarity,

        /// <summary>An expedition chest.</summary>
        ExpeditionChest,

        /// <summary>A breach chest.</summary>
        BreachChest,

        /// <summary>A strongbox.</summary>
        Strongbox,

        /// <summary>A specially important NPC.</summary>
        SpecialNpc,

        /// <summary>A point-of-interest monster.</summary>
        PoiMonster,

        /// <summary>An atlas pinnacle boss.</summary>
        PinnacleBoss,

        /// <summary>An item on the ground.</summary>
        WorldItem,

        /// <summary>An item in an inventory.</summary>
        InventoryItem,
    }

    /// <summary>Coarse entity state (host-derived; not an in-game concept).</summary>
    public enum EntityState
    {
        /// <summary>State not identified yet.</summary>
        None,

        /// <summary>Entity is no longer updated and should be skipped by plugins.</summary>
        Useless,

        /// <summary>A player important to the local player (party leader).</summary>
        PlayerLeader,

        /// <summary>A monster friendly to the player.</summary>
        MonsterFriendly,

        /// <summary>A pinnacle boss that is hidden / not attackable.</summary>
        PinnacleBossHidden,
    }

    /// <summary>Item/monster rarity.</summary>
    public enum Rarity
    {
        /// <summary>Normal.</summary>
        Normal,

        /// <summary>Magic.</summary>
        Magic,

        /// <summary>Rare.</summary>
        Rare,

        /// <summary>Unique.</summary>
        Unique,
    }

    /// <summary>An integer rectangle (game window / screen region).</summary>
    /// <param name="X">left.</param>
    /// <param name="Y">top.</param>
    /// <param name="Width">width.</param>
    /// <param name="Height">height.</param>
    public readonly record struct RectInfo(int X, int Y, int Width, int Height);

    /// <summary>
    ///     Aggregates every host service available to a plugin. Mirrors the
    ///     service-oriented <c>Context</c> design of POEFixer, adapted to GameHelper.
    /// </summary>
    public interface IContext
    {
        /// <summary>Game/process/window state and the current snapshot.</summary>
        IGameService Game { get; }

        /// <summary>Nearby entity enumeration and lookup.</summary>
        IEntitiesService Entities { get; }

        /// <summary>In-game UI panels / state.</summary>
        IUiService Ui { get; }

        /// <summary>World/grid to screen projection.</summary>
        IRenderService Render { get; }

        /// <summary>Subscribe to host lifecycle events.</summary>
        IEventsService Events { get; }

        /// <summary>Overlay/window metrics.</summary>
        IOverlayService Overlay { get; }

        /// <summary>Diagnostic logging.</summary>
        ILogService Log { get; }
    }

    /// <summary>Game, process and window state.</summary>
    public interface IGameService
    {
        /// <summary>Gets the current high-level game state.</summary>
        GameState State { get; }

        /// <summary>Gets a value indicating whether the game process is attached.</summary>
        bool IsAttached { get; }

        /// <summary>Gets a value indicating whether the player is currently in-game.</summary>
        bool IsInGame { get; }

        /// <summary>Gets a value indicating whether the game window is in the foreground.</summary>
        bool IsForeground { get; }

        /// <summary>Gets the game process id, or 0 when not attached.</summary>
        uint Pid { get; }

        /// <summary>Gets a value indicating whether the game is in controller/gamepad mode.</summary>
        bool IsControllerMode { get; }

        /// <summary>Gets the game window position and size relative to the monitor.</summary>
        RectInfo WindowArea { get; }

        /// <summary>
        ///     Gets the in-game view. Only meaningful while <see cref="IsInGame" /> is
        ///     true; members return safe defaults otherwise.
        /// </summary>
        IInGame InGame { get; }
    }

    /// <summary>The current in-game world / area view.</summary>
    public interface IInGame
    {
        /// <summary>Gets the current area display name.</summary>
        string AreaName { get; }

        /// <summary>Gets the server-provided area hash for the current instance.</summary>
        string AreaHash { get; }

        /// <summary>Gets the monster level of the current area.</summary>
        int AreaLevel { get; }

        /// <summary>Gets a value indicating whether the current area is a town.</summary>
        bool IsTown { get; }

        /// <summary>Gets a value indicating whether the current area is a hideout.</summary>
        bool IsHideout { get; }

        /// <summary>Gets the internal (locale-independent) area id.</summary>
        string AreaId { get; }

        /// <summary>Gets the current area terrain data.</summary>
        ITerrain Terrain { get; }

        /// <summary>Gets the local player entity.</summary>
        IEntity Player { get; }
    }

    /// <summary>Enumerates the entities the game currently has loaded.</summary>
    public interface IEntitiesService
    {
        /// <summary>Gets the awake (active, near-player) entities.</summary>
        IReadOnlyCollection<IEntity> Awake { get; }

        /// <summary>Gets the sleeping (dormant, far) entities.</summary>
        IReadOnlyCollection<IEntity> Sleeping { get; }

        /// <summary>Tries to find an awake entity by its area-unique id.</summary>
        /// <param name="id">the entity id.</param>
        /// <param name="entity">the found entity, when returning true.</param>
        /// <returns>true if found; otherwise false.</returns>
        bool TryGetById(uint id, out IEntity entity);

        /// <summary>
        ///     Scans sleeping (dormant/far) entities whose metadata path matches
        ///     <paramref name="pathFilter" />, invoking <paramref name="onMatch" /> per match.
        ///     Intended for background scans.
        /// </summary>
        /// <param name="pathFilter">predicate on the entity metadata path.</param>
        /// <param name="onMatch">callback invoked for each matching entity.</param>
        void ScanSleeping(System.Func<string, bool> pathFilter, System.Action<IEntity> onMatch);
    }

    /// <summary>A read-only view of a game entity.</summary>
    public interface IEntity
    {
        /// <summary>
        ///     Gets the raw memory address of the underlying entity (escape hatch).
        ///     Combine with <see cref="IGameService.Pid" /> to read entity data the
        ///     curated component views do not expose.
        /// </summary>
        nint Address { get; }

        /// <summary>Gets the area-unique entity id.</summary>
        uint Id { get; }

        /// <summary>Gets the metadata path (e.g. <c>Metadata/Monsters/...</c>).</summary>
        string Path { get; }

        /// <summary>Gets a value indicating whether the entity still exists in game memory.</summary>
        bool IsValid { get; }

        /// <summary>Gets the entity category.</summary>
        EntityType Type { get; }

        /// <summary>Gets the entity sub-category.</summary>
        EntitySubtype Subtype { get; }

        /// <summary>Gets the coarse entity state.</summary>
        EntityState State { get; }

        /// <summary>
        ///     Gets the user-assigned custom group for POI monsters / important
        ///     objects (0 when not applicable).
        /// </summary>
        int CustomGroup { get; }

        /// <summary>Gets the entity's position on the area grid (X, Y).</summary>
        Vector2 GridPosition { get; }

        /// <summary>
        ///     Tries to read a component from this entity by its SDK interface type
        ///     (e.g. <c>TryGetComponent&lt;ILife&gt;(out var life)</c>).
        /// </summary>
        /// <typeparam name="TComponent">the component interface to read.</typeparam>
        /// <param name="component">the component, when returning true.</param>
        /// <returns>true if the entity has the component; otherwise false.</returns>
        bool TryGetComponent<TComponent>(out TComponent component)
            where TComponent : class, IComponent;
    }

    /// <summary>Marker for an entity component view.</summary>
    public interface IComponent
    {
    }

    /// <summary>A single vital pool (health, energy shield, mana, ward, ...).</summary>
    public interface IVital
    {
        /// <summary>Gets the current value.</summary>
        int Current { get; }

        /// <summary>Gets the maximum (total) value.</summary>
        int Total { get; }

        /// <summary>Gets the unreserved total (total minus reservations).</summary>
        int Unreserved { get; }

        /// <summary>Gets the flat reserved amount.</summary>
        int ReservedFlat { get; }

        /// <summary>Gets the percentage reserved (in 1/100th of a percent units).</summary>
        int ReservedPercent { get; }

        /// <summary>Gets the regeneration per second.</summary>
        float Regeneration { get; }

        /// <summary>Gets <see cref="Current" /> as a percentage of the unreserved pool (0-100).</summary>
        int CurrentInPercent { get; }
    }

    /// <summary>Life component: health, energy shield, mana, ward and divinity pools.</summary>
    public interface ILife : IComponent
    {
        /// <summary>Gets a value indicating whether the entity is alive.</summary>
        bool IsAlive { get; }

        /// <summary>Gets the health pool.</summary>
        IVital Health { get; }

        /// <summary>Gets the energy shield pool.</summary>
        IVital EnergyShield { get; }

        /// <summary>Gets the mana pool.</summary>
        IVital Mana { get; }

        /// <summary>Gets the ward pool.</summary>
        IVital Ward { get; }

        /// <summary>Gets the divinity pool.</summary>
        IVital Divinity { get; }
    }

    /// <summary>
    ///     A curated subset of game stat ids exposed through <see cref="IStats" />.
    ///     The host maps these to its internal stat enum, keeping the volatile
    ///     numeric ids out of the plugin contract.
    /// </summary>
    public enum GameStat
    {
        /// <summary>Effective evasion rating.</summary>
        EvasionRating,

        /// <summary>Maximum energy shield.</summary>
        MaximumEnergyShield,

        /// <summary>Armour.</summary>
        Armour,

        /// <summary>Maximum life.</summary>
        MaximumLife,

        /// <summary>
        ///     Queen-of-the-Forest stat (movement speed is only base, +1% per X
        ///     evasion rating). Non-zero when the relevant item is equipped.
        /// </summary>
        MovementSpeedFromEvasion,

        /// <summary>
        ///     Non-zero while the monster is sealed inside a Harvest/Legion-style
        ///     monolith and therefore not yet attackable.
        /// </summary>
        MonsterInsideMonolith,
    }

    /// <summary>Stats component: the entity's computed game stats.</summary>
    public interface IStats : IComponent
    {
        /// <summary>
        ///     Tries to read a stat from the items layer (matches the in-game
        ///     character sheet; excludes transient buff/aura externals).
        /// </summary>
        /// <param name="stat">the stat to read.</param>
        /// <param name="value">the value, when returning true.</param>
        /// <returns>true if the stat is present in the items layer.</returns>
        bool TryGetItemStat(GameStat stat, out int value);

        /// <summary>
        ///     Reads a stat summed across both the items and the buff/action layers
        ///     (used for buff/action-granted stats).
        /// </summary>
        /// <param name="stat">the stat to read.</param>
        /// <returns>the summed value (0 when absent from both layers).</returns>
        int GetTotalStat(GameStat stat);
    }

    /// <summary>Object magic properties component: rarity and mods.</summary>
    public interface IObjectMagicProperties : IComponent
    {
        /// <summary>Gets the rarity of the entity.</summary>
        Rarity Rarity { get; }

        /// <summary>Gets the internal mod names applied to the entity.</summary>
        IReadOnlyCollection<string> ModNames { get; }
    }

    /// <summary>Positioned component: reaction / friendliness.</summary>
    public interface IPositioned : IComponent
    {
        /// <summary>Gets a value indicating whether the entity is friendly to the player.</summary>
        bool IsFriendly { get; }
    }

    /// <summary>Render component: world/grid positions and terrain height.</summary>
    public interface IRender : IComponent
    {
        /// <summary>Gets the position on the area grid.</summary>
        Vector2 GridPosition { get; }

        /// <summary>Gets the position in the rendered game world.</summary>
        Vector3 WorldPosition { get; }

        /// <summary>Gets the entity's model bounds (used to offset overlays above it).</summary>
        Vector3 ModelBounds { get; }

        /// <summary>Gets the terrain height the entity stands on.</summary>
        float TerrainHeight { get; }
    }

    /// <summary>In-game UI panels / state.</summary>
    public interface IUiService
    {
        /// <summary>Gets a value indicating whether any large game panel is open.</summary>
        bool IsAnyLargePanelOpen { get; }

        /// <summary>
        ///     Gets the raw memory address of the root in-game UI element (escape
        ///     hatch). Combine with <see cref="IGameService.Pid" /> to walk the UI
        ///     tree directly for panels the curated services do not expose.
        /// </summary>
        nint GameUiAddress { get; }

        /// <summary>Gets the Trial of the Sekhemas (Sanctum) map panel UI element.</summary>
        IUiElement SekhemaTrialPanel { get; }

        /// <summary>Gets the endgame Atlas panel UI element.</summary>
        IUiElement Atlas { get; }

        /// <summary>Gets the right-side panel UI element.</summary>
        IUiElement RightPanel { get; }

        /// <summary>Gets the world map panel UI element.</summary>
        IUiElement WorldMapPanel { get; }

        /// <summary>Gets the current Atlas map nodes.</summary>
        IReadOnlyList<IAtlasMapNode> AtlasMaps { get; }

        /// <summary>Gets the mini-map UI element.</summary>
        IMapElement MiniMap { get; }

        /// <summary>Gets the large-map UI element.</summary>
        IMapElement LargeMap { get; }

        /// <summary>Gets the game's base UI resolution (used for map scaling math).</summary>
        Vector2 BaseResolution { get; }

        /// <summary>
        ///     Enumerates the item slots in the currently open stash/inventory panels,
        ///     each with its on-screen rectangle and price-relevant item data. Returns an
        ///     empty list when no such panel is open. Intended to be called once per frame.
        /// </summary>
        /// <returns>the visible item slots (left = stash, right = inventory).</returns>
        IReadOnlyList<IItemSlot> EnumerateOpenItemSlots();
    }

    /// <summary>Projects world/grid coordinates to screen space.</summary>
    public interface IRenderService
    {
        /// <summary>Projects a world-space position to screen pixels.</summary>
        /// <param name="worldPosition">the world position.</param>
        /// <returns>screen-space pixel coordinates.</returns>
        Vector2 WorldToScreen(Vector3 worldPosition);

        /// <summary>Projects a world-space position to screen pixels using an explicit height.</summary>
        /// <param name="worldPosition">the world position.</param>
        /// <param name="height">the height (e.g. terrain height) to project at.</param>
        /// <returns>screen-space pixel coordinates.</returns>
        Vector2 WorldToScreen(Vector3 worldPosition, float height);
    }

    /// <summary>
    ///     Subscribe to host lifecycle events. Each call returns an
    ///     <see cref="IDisposable" /> token; dispose it to unsubscribe.
    /// </summary>
    public interface IEventsService
    {
        /// <summary>Raised when the player changes area/zone.</summary>
        /// <param name="handler">callback.</param>
        /// <returns>subscription token; dispose to unsubscribe.</returns>
        IDisposable OnAreaChange(Action handler);

        /// <summary>Raised once per rendered frame.</summary>
        /// <param name="handler">callback.</param>
        /// <returns>subscription token; dispose to unsubscribe.</returns>
        IDisposable OnFrame(Action handler);

        /// <summary>Raised when the game process becomes attached.</summary>
        /// <param name="handler">callback.</param>
        /// <returns>subscription token; dispose to unsubscribe.</returns>
        IDisposable OnGameAttached(Action handler);

        /// <summary>Raised when the game process is closed/detached.</summary>
        /// <param name="handler">callback.</param>
        /// <returns>subscription token; dispose to unsubscribe.</returns>
        IDisposable OnGameDetached(Action handler);

        /// <summary>Raised when the game window is moved/resized.</summary>
        /// <param name="handler">callback.</param>
        /// <returns>subscription token; dispose to unsubscribe.</returns>
        IDisposable OnWindowMove(Action handler);

        /// <summary>Raised when the game window foreground state changes.</summary>
        /// <param name="handler">callback.</param>
        /// <returns>subscription token; dispose to unsubscribe.</returns>
        IDisposable OnForegroundChange(Action handler);
    }

    /// <summary>Overlay / window metrics and image (texture) management.</summary>
    public interface IOverlayService
    {
        /// <summary>Gets the overlay drawable size in pixels.</summary>
        Vector2 Size { get; }

        /// <summary>
        ///     Loads (or returns the already-loaded) texture for the given file and
        ///     yields an ImGui image handle plus its pixel dimensions.
        /// </summary>
        /// <param name="filePath">absolute path of the image file.</param>
        /// <param name="handle">the ImGui texture handle.</param>
        /// <param name="width">image width in pixels.</param>
        /// <param name="height">image height in pixels.</param>
        void AddOrGetImage(string filePath, out nint handle, out uint width, out uint height);

        /// <summary>
        ///     Registers (or replaces) a texture from an in-memory image under
        ///     <paramref name="key" />, yielding an ImGui image handle.
        /// </summary>
        /// <param name="key">unique texture key.</param>
        /// <param name="image">the source image (RGBA32).</param>
        /// <param name="srgb">whether the image is sRGB.</param>
        /// <param name="handle">the ImGui texture handle.</param>
        void AddOrGetImage(string key, SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, bool srgb, out nint handle);

        /// <summary>Unloads a previously loaded texture.</summary>
        /// <param name="filePath">absolute path or key used when loading.</param>
        /// <returns>true if an image was removed.</returns>
        bool RemoveImage(string filePath);
    }

    /// <summary>Diagnostic logging to the host.</summary>
    public interface ILogService
    {
        /// <summary>Logs a debug message.</summary>
        /// <param name="message">the message.</param>
        void Debug(string message);

        /// <summary>Logs an informational message.</summary>
        /// <param name="message">the message.</param>
        void Info(string message);

        /// <summary>Logs a warning.</summary>
        /// <param name="message">the message.</param>
        void Warn(string message);

        /// <summary>Logs an error.</summary>
        /// <param name="message">the message.</param>
        void Error(string message);
    }
}
