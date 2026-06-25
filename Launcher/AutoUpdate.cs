namespace Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;

    /// <summary>
    ///     Self-updates the app from the latest GitHub Release of the repo. The release
    ///     (published by .github/workflows/release.yml on a version tag) carries a full
    ///     app zip named <c>CoreExile2-&lt;tag&gt;.zip</c>.
    /// </summary>
    public static class AutoUpdate
    {
        // Owner/repo to pull releases from. Change this to fork the updater elsewhere.
        public const string Repo = "coussiraty/CoreExile2";

        private static string LatestReleaseApi => $"https://api.github.com/repos/{Repo}/releases/latest";

        private static readonly HttpClient HttpClient = new();
        private static string? extractedPath;
        private static string? newVersion;

        static AutoUpdate()
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "CoreExile2-Launcher");
            HttpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        }

        public static async Task<bool> CheckAndUpdateAsync(string gameHelperExePath)
        {
            try
            {
                Console.WriteLine("Checking for app updates...");

                var release = await GetLatestReleaseAsync();
                var latestVersion = release?["tag_name"]?.ToString();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    Console.WriteLine("No releases found (or update check failed).");
                    return false;
                }

                var currentVersion = GetCurrentVersion(gameHelperExePath);
                if (!IsNewerVersion(latestVersion, currentVersion))
                {
                    Console.WriteLine($"App is up to date ({currentVersion}).");
                    return false;
                }

                Console.WriteLine($"New app version available: {latestVersion} (current {currentVersion}).");
                var url = GetAppZipUrl(release!, latestVersion);
                if (string.IsNullOrEmpty(url))
                {
                    Console.WriteLine("Release has no app zip asset; skipping app update.");
                    return false;
                }

                return await DownloadAndStageAsync(url, latestVersion);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"App update check failed: {ex.Message}");
                return false;
            }
        }

        internal static async Task<JObject?> GetLatestReleaseAsync()
        {
            try
            {
                var json = await HttpClient.GetStringAsync(LatestReleaseApi);
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch latest release: {ex.Message}");
                return null;
            }
        }

        private static string GetCurrentVersion(string gameHelperExePath)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(gameHelperExePath).FileVersion;
                if (string.IsNullOrEmpty(version) || version == "1.0.0.0")
                {
                    return "v0.0.0";
                }

                var parts = version.Split('.');
                return $"v{parts[0]}.{parts[1]}.{parts[2]}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read version from {gameHelperExePath}: {ex.Message}");
                return "v0.0.0";
            }
        }

        private static bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                var latestParts = latestVersion.TrimStart('v').Split('.');
                var currentParts = currentVersion.TrimStart('v').Split('.');
                for (var i = 0; i < Math.Max(latestParts.Length, currentParts.Length); i++)
                {
                    var l = i < latestParts.Length && int.TryParse(latestParts[i], out var lp) ? lp : 0;
                    var c = i < currentParts.Length && int.TryParse(currentParts[i], out var cp) ? cp : 0;
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
            catch
            {
                return false;
            }
        }

        private static string? GetAppZipUrl(JObject release, string version)
        {
            var assets = release["assets"] as JArray;
            if (assets == null)
            {
                return null;
            }

            var wanted = $"CoreExile2-{version}.zip";
            foreach (var asset in assets)
            {
                if (string.Equals(asset["name"]?.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return asset["browser_download_url"]?.ToString();
                }
            }

            return null;
        }

        private static async Task<bool> DownloadAndStageAsync(string url, string version)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "CoreExile2Update");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }

                Directory.CreateDirectory(tempDir);
                var zipPath = Path.Combine(tempDir, "update.zip");

                Console.WriteLine("Downloading app update...");
                await DownloadFileWithProgressAsync(url, zipPath);

                Console.WriteLine("Extracting...");
                var extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                extractedPath = extractDir;
                newVersion = version;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"App update preparation failed: {ex.Message}");
                return false;
            }
        }

        private static async Task DownloadFileWithProgressAsync(string url, string destinationPath)
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[8192];
            long totalDownloaded = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalDownloaded += bytesRead;
                if (totalBytes > 0)
                {
                    DrawProgressBar((int)(totalDownloaded * 100 / totalBytes), 40);
                }
            }

            Console.WriteLine();
        }

        private static void DrawProgressBar(int percentage, int barLength)
        {
            var filled = (int)(percentage / 100.0 * barLength);
            Console.Write($"\r[{new string('#', filled)}{new string('-', barLength - filled)}] {percentage}%");
        }

        public static void LaunchUpdateAndExit()
        {
            try
            {
                if (string.IsNullOrEmpty(extractedPath) || string.IsNullOrEmpty(newVersion))
                {
                    return;
                }

                var currentDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var launcherPath = Path.Combine(currentDir, "Launcher.exe");
                var tempDir = Path.Combine(Path.GetTempPath(), "CoreExile2Update");

                var psCommand = $@"
Write-Host 'Waiting for Launcher to exit...';
Wait-Process -Name 'Launcher' -ErrorAction SilentlyContinue;
Write-Host 'Installing app update...';
try {{
  Copy-Item -Path '{extractedPath}\*' -Destination '{currentDir}' -Recurse -Force;
  Write-Host 'Update complete. Restarting...';
  Start-Process -FilePath '{launcherPath}' -WorkingDirectory '{currentDir}';
  Remove-Item -Path '{tempDir}' -Recurse -Force -ErrorAction SilentlyContinue;
}} catch {{
  Write-Host 'Update failed:' $_.Exception.Message;
  Read-Host 'Press Enter to continue';
}}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                });

                Console.WriteLine("Updating app. The Launcher will restart afterwards.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch update process: {ex.Message}");
            }
        }
    }
}
