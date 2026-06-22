// <copyright file="Diagnostics.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace ExileBridge
{
    using System;
    using System.IO;

    /// <summary>
    ///     Lightweight file diagnostics sink shared by plugins. Writes timestamped,
    ///     source-tagged lines to a single rolling log file so the developer (or a
    ///     tool) can analyse live behaviour without reading the on-screen overlay.
    ///     Never throws — logging failures are swallowed.
    /// </summary>
    public static class Diagnostics
    {
        private static readonly object Lock = new();
        private static string path = DefaultPath();
        private static long maxBytes = 1_000_000;

        /// <summary>Gets or sets a value indicating whether logging is enabled.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Gets the absolute path of the diagnostics log file.</summary>
        public static string FilePath
        {
            get
            {
                lock (Lock)
                {
                    return path;
                }
            }
        }

        /// <summary>Overrides the diagnostics log file path.</summary>
        /// <param name="filePath">absolute path to write to.</param>
        public static void SetFile(string filePath)
        {
            lock (Lock)
            {
                path = filePath;
            }
        }

        /// <summary>Appends a timestamped, source-tagged line to the log.</summary>
        /// <param name="source">the plugin/source name.</param>
        /// <param name="message">the message.</param>
        public static void Log(string source, string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                lock (Lock)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                    {
                        var lines = File.ReadAllLines(path);
                        File.WriteAllLines(path, lines[(lines.Length / 2)..]);
                    }

                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{source}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never break a plugin.
            }
        }

        /// <summary>Empties the log file.</summary>
        public static void Clear()
        {
            try
            {
                lock (Lock)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(path, string.Empty);
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string DefaultPath()
        {
            try
            {
                return Path.Combine(AppContext.BaseDirectory, "logs", "coreexile-diagnostics.log");
            }
            catch
            {
                return "coreexile-diagnostics.log";
            }
        }
    }
}
