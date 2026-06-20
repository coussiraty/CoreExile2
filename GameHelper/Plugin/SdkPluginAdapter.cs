// <copyright file="SdkPluginAdapter.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin
{
    using ExileBridge;
    using GameHelper.Sdk;

    /// <summary>
    ///     Bridges an ExileBridge <see cref="IPlugin" /> into the host's legacy
    ///     <see cref="IPCore" /> pipeline, so the rest of <see cref="PManager" />
    ///     (which deals only in <see cref="IPCore" />) works unchanged. It also
    ///     binds the shared <see cref="GameHelperContext" /> into the plugin.
    /// </summary>
    internal sealed class SdkPluginAdapter : IPCore
    {
        private readonly IPlugin plugin;

        internal SdkPluginAdapter(IPlugin plugin) => this.plugin = plugin;

        /// <inheritdoc />
        public void SetPluginDllLocation(string dllLocation)
        {
            if (this.plugin is IHostBoundPlugin bound)
            {
                bound.Bind(GameHelperContext.Instance, dllLocation);
            }
        }

        /// <inheritdoc />
        public void OnEnable(bool isGameOpened) => this.plugin.OnEnable(isGameOpened);

        /// <inheritdoc />
        public void OnDisable() => this.plugin.OnDisable();

        /// <inheritdoc />
        public void DrawSettings() => this.plugin.DrawSettings();

        /// <inheritdoc />
        public void DrawUI() => this.plugin.DrawUI();

        /// <inheritdoc />
        public void SaveSettings() => this.plugin.SaveSettings();
    }
}
