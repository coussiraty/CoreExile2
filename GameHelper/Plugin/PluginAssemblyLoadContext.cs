namespace GameHelper.Plugin
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.Loader;
    using GameOffsets;

    internal class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private static readonly IReadOnlyDictionary<string, Assembly> SharedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
            {
                [typeof(IPCore).Assembly.GetName().Name!] = typeof(IPCore).Assembly,
                [typeof(GameProcessDetails).Assembly.GetName().Name!] = typeof(GameProcessDetails).Assembly,
                // ExileBridge must resolve to the host-loaded (Default ALC) assembly so
                // that IPlugin/IContext have a single type identity across all plugins;
                // otherwise reflection-based plugin discovery in PManager fails.
                [typeof(ExileBridge.IPlugin).Assembly.GetName().Name!] = typeof(ExileBridge.IPlugin).Assembly,
                // ImageSharp is shared because the SDK's overlay texture API exposes
                // Image<Rgba32> across the plugin boundary; a per-plugin copy would give
                // it a distinct type identity and break the cast at the boundary.
                [typeof(SixLabors.ImageSharp.Image).Assembly.GetName().Name!] = typeof(SixLabors.ImageSharp.Image).Assembly,
            };

        private readonly AssemblyDependencyResolver resolver;

        public PluginAssemblyLoadContext(string assemblyLocation)
            : base(isCollectible: true)
        {
            this.resolver = new AssemblyDependencyResolver(assemblyLocation);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null &&
                SharedAssemblies.TryGetValue(assemblyName.Name, out var sharedAssembly))
            {
                return sharedAssembly;
            }

            var path = this.resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
            {
                return this.LoadFromAssemblyPath(path);
            }

            return null;
        }
    }
}
