// <copyright file="PluginContract.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    /// <summary>
    ///     Versioning information for the ExileBridge SDK. The host compares this
    ///     against its own expected version at plugin load time (mismatch =&gt; refused),
    ///     mirroring the <c>PLUGIN_SDK_VERSION</c> contract used by POEFixer.
    /// </summary>
    public static class SdkInfo
    {
        /// <summary>
        ///     The current ExileBridge contract version. Bump on breaking changes.
        /// </summary>
        public const int Version = 1;
    }

    /// <summary>
    ///     Marker interface for a plugin's strongly-typed, serializable settings.
    /// </summary>
    public interface IPluginSettings
    {
    }

    /// <summary>
    ///     The contract every ExileBridge plugin implements. Authors normally do not
    ///     implement this directly; they derive from <see cref="Plugin{TSettings}" />.
    /// </summary>
    public interface IPlugin
    {
        /// <summary>
        ///     Called when the plugin is enabled (at startup if already enabled, or
        ///     when the user enables it). Subscribe to events / load state here.
        /// </summary>
        /// <param name="isGameAttached">whether the game process is already attached.</param>
        void OnEnable(bool isGameAttached);

        /// <summary>Called when the plugin is disabled. Unsubscribe / release here.</summary>
        void OnDisable();

        /// <summary>Draws the plugin's configuration UI in the host settings window (ImGui).</summary>
        void DrawSettings();

        /// <summary>Draws the plugin's per-frame overlay UI (ImGui). Not called while disabled.</summary>
        void DrawUI();

        /// <summary>Persists the plugin's settings to disk.</summary>
        void SaveSettings();
    }

    /// <summary>
    ///     Host-facing binding contract. Implemented explicitly by
    ///     <see cref="Plugin{TSettings}" /> so it does not appear on the author's
    ///     <c>this</c>. The host calls <see cref="Bind" /> once, right after creating
    ///     the plugin instance, before <see cref="IPlugin.OnEnable" />.
    /// </summary>
    public interface IHostBoundPlugin
    {
        /// <summary>Injects the game context and the plugin's on-disk directory.</summary>
        /// <param name="context">the host-provided game context.</param>
        /// <param name="directoryPath">absolute path of the plugin's folder.</param>
        void Bind(IContext context, string directoryPath);
    }

    /// <summary>
    ///     Recommended base class for ExileBridge plugins. Provides the injected
    ///     <see cref="Ctx" /> game context, the plugin <see cref="DirectoryPath" />,
    ///     and strongly-typed <see cref="Settings" />.
    /// </summary>
    /// <typeparam name="TSettings">the plugin's settings type.</typeparam>
    public abstract class Plugin<TSettings> : IPlugin, IHostBoundPlugin
        where TSettings : IPluginSettings, new()
    {
        /// <summary>Gets the host game context (entities, render, events, etc.).</summary>
        protected IContext Ctx { get; private set; } = null!;

        /// <summary>Gets the absolute path of this plugin's folder on disk.</summary>
        protected string DirectoryPath { get; private set; } = null!;

        /// <summary>Gets or sets this plugin's strongly-typed settings.</summary>
        protected TSettings Settings { get; set; } = new();

        /// <inheritdoc />
        public abstract void OnEnable(bool isGameAttached);

        /// <inheritdoc />
        public abstract void OnDisable();

        /// <inheritdoc />
        public abstract void DrawSettings();

        /// <inheritdoc />
        public abstract void DrawUI();

        /// <inheritdoc />
        public abstract void SaveSettings();

        /// <inheritdoc />
        void IHostBoundPlugin.Bind(IContext context, string directoryPath)
        {
            this.Ctx = context;
            this.DirectoryPath = directoryPath;
        }
    }
}
