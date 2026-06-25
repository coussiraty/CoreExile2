namespace Launcher
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;

    /// <summary>
    ///     Updates individual plugins independently of the host. The latest GitHub Release
    ///     carries a <c>plugins.json</c> manifest (name -> version -> per-plugin zip URL).
    ///     For every plugin whose published version is newer than the installed DLL's
    ///     FileVersion — or that isn't installed yet — the matching <c>plugin-&lt;Name&gt;.zip</c>
    ///     is downloaded and extracted into <c>Plugins/&lt;Name&gt;/</c>.
    /// </summary>
    /// <remarks>
    ///     Runs from the Launcher, before GameHelper.exe starts, so no plugin DLL is locked.
    /// </remarks>
    public static class PluginUpdater
    {
        private static string ManifestApiUrl =>
            $"https://api.github.com/repos/{AutoUpdate.Repo}/releases/latest";

        private static readonly HttpClient HttpClient = new();

        static PluginUpdater()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "CoreExile2-Launcher");
            HttpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        }

        /// <summary>Checks the manifest and updates any out-of-date or missing plugins.</summary>
        /// <param name="gameHelperDir">Directory that contains GameHelper.exe (and the Plugins folder).</param>
        public static async Task CheckAndUpdatePluginsAsync(string gameHelperDir)
        {
            try
            {
                Console.WriteLine("Checking for plugin updates...");

                var manifest = await GetManifestAsync();
                if (manifest == null)
                {
                    Console.WriteLine("No plugin manifest found; skipping plugin updates.");
                    return;
                }

                var plugins = manifest["plugins"] as JArray;
                if (plugins == null || plugins.Count == 0)
                {
                    Console.WriteLine("Manifest lists no plugins.");
                    return;
                }

                var pluginsDir = Path.Combine(gameHelperDir, "Plugins");
                Directory.CreateDirectory(pluginsDir);

                var updated = 0;
                foreach (var plugin in plugins)
                {
                    var name = plugin["name"]?.ToString();
                    var version = plugin["version"]?.ToString();
                    var url = plugin["url"]?.ToString();
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    var pluginDir = Path.Combine(pluginsDir, name);
                    var dllPath = Path.Combine(pluginDir, $"{name}.dll");
                    var installedVersion = GetInstalledVersion(dllPath);

                    if (!IsNewerVersion(version, installedVersion))
                    {
                        continue;
                    }

                    Console.WriteLine(installedVersion == null
                        ? $"Installing new plugin '{name}' (v{version})..."
                        : $"Updating plugin '{name}' ({installedVersion} -> {version})...");

                    if (await DownloadAndInstallPluginAsync(name, url, pluginDir))
                    {
                        updated++;
                    }
                }

                Console.WriteLine(updated == 0
                    ? "All plugins are up to date."
                    : $"Updated {updated} plugin(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Plugin update check failed: {ex.Message}");
            }
        }

        private static async Task<JObject?> GetManifestAsync()
        {
            try
            {
                var json = await HttpClient.GetStringAsync(ManifestApiUrl);
                var release = JObject.Parse(json);
                var assets = release["assets"] as JArray;
                if (assets == null)
                {
                    return null;
                }

                foreach (var asset in assets)
                {
                    if (string.Equals(asset["name"]?.ToString(), "plugins.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var url = asset["browser_download_url"]?.ToString();
                        if (string.IsNullOrEmpty(url))
                        {
                            return null;
                        }

                        var manifestJson = await HttpClient.GetStringAsync(url);
                        return JObject.Parse(manifestJson);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch plugin manifest: {ex.Message}");
                return null;
            }
        }

        private static string? GetInstalledVersion(string dllPath)
        {
            if (!File.Exists(dllPath))
            {
                return null;
            }

            try
            {
                var v = FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
                return string.IsNullOrEmpty(v) ? "0.0.0.0" : v;
            }
            catch
            {
                return "0.0.0.0";
            }
        }

        private static bool IsNewerVersion(string? latest, string? installed)
        {
            if (string.IsNullOrEmpty(latest))
            {
                return false;
            }

            if (string.IsNullOrEmpty(installed))
            {
                return true; // not installed yet
            }

            var latestParts = latest.TrimStart('v').Split('.');
            var installedParts = installed.TrimStart('v').Split('.');
            for (var i = 0; i < Math.Max(latestParts.Length, installedParts.Length); i++)
            {
                var l = i < latestParts.Length && int.TryParse(latestParts[i], out var lp) ? lp : 0;
                var c = i < installedParts.Length && int.TryParse(installedParts[i], out var cp) ? cp : 0;
                if (l > c)
                {
                    return true;
                }

                if (l < c)
                {
                    return false;
                }
            }

            return false;
        }

        private static async Task<bool> DownloadAndInstallPluginAsync(string name, string url, string pluginDir)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "CoreExile2PluginUpdate", name);
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }

                Directory.CreateDirectory(tempDir);
                var zipPath = Path.Combine(tempDir, $"{name}.zip");

                using (var response = await HttpClient.GetAsync(url))
                {
                    response.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(fs);
                }

                var extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // Replace the plugin folder contents in place.
                Directory.CreateDirectory(pluginDir);
                CopyDirectory(extractDir, pluginDir);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to update plugin '{name}': {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                    // best effort cleanup
                }
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
            }
        }
    }
}
