// <copyright file="HealthBars.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>


namespace HealthBars
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ExileBridge;

    /// <summary>
    ///     Loads and cleans up the textures.
    /// </summary>
    public class TextureLoader
    {
        private readonly Dictionary<string, (IntPtr, int w, int h)> loadedTextures = new();

        /// <summary>
        ///     Gets all the keys of the loaded textures.
        /// </summary>
        public List<string> TextureKeys => new(this.loadedTextures.Keys);

        /// <summary>
        ///     Gets the total number of loaded textures.
        /// </summary>
        public int TotalTexturesLoaded => this.loadedTextures.Count;

        /// <summary>
        ///     Unloads all the textures.
        /// </summary>
        /// <param name="overlay">the SDK overlay service.</param>
        /// <param name="texturesPath">path to texture folder.</param>
        public void cleanup(IOverlayService overlay, string texturesPath)
        {
            foreach (var filename in this.loadedTextures.Keys)
            {
                var pathname = Path.Join(texturesPath, filename);
                if (overlay.RemoveImage(pathname))
                {
                    this.loadedTextures.Remove(filename);
                }
            }
        }

        /// <summary>
        ///     Loads all the textures.
        /// </summary>
        /// <param name="overlay">the SDK overlay service.</param>
        /// <param name="texturesPath">Path to texture folder.</param>
        public void Load(IOverlayService overlay, string texturesPath)
        {
            if (Directory.Exists(texturesPath))
            {
                foreach (var pathname in Directory.EnumerateFiles(texturesPath))
                {
                    var filename = Path.GetFileName(pathname);
                    overlay.AddOrGetImage(pathname, out var handle, out var w, out var h);
                    this.loadedTextures.Add(filename, (handle, (int)w, (int)h));
                }
            }
        }

        /// <summary>
        ///     Gets the texture along with width and height.
        /// </summary>
        /// <param name="key">texture identifier</param>
        /// <returns></returns>
        public (IntPtr, int w, int h) GetTexture(string key) => this.loadedTextures[key];
    }
}