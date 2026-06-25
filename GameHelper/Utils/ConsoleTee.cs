// <copyright file="ConsoleTee.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    ///     Mirrors everything written to <see cref="Console.Out" /> / <see cref="Console.Error" />
    ///     into <c>logs/console.log</c> (next to the executable), so console diagnostics survive
    ///     even when the app runs as a windowed (no-console) process. The original writers still
    ///     receive everything, so an attached console behaves as before.
    /// </summary>
    internal static class ConsoleTee
    {
        private const long MaxLogBytes = 10 * 1024 * 1024; // rotate at 10 MB

        private static readonly object Gate = new();
        private static StreamWriter? fileWriter;

        /// <summary>Installs the tee. Safe to call once at startup; no-ops on failure.</summary>
        internal static void Install()
        {
            try
            {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logsDir);
                var logPath = Path.Combine(logsDir, "console.log");

                if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxLogBytes)
                {
                    var backup = logPath + ".1";
                    File.Delete(backup);
                    File.Move(logPath, backup);
                }

                fileWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };

                Console.SetOut(new TeeTextWriter(Console.Out));
                Console.SetError(new TeeTextWriter(Console.Error));
                fileWriter.WriteLine($"===== console log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            }
            catch
            {
                // Diagnostics are best-effort; never let logging break startup.
            }
        }

        private static void WriteToFile(string? text)
        {
            if (fileWriter == null || text == null)
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    fileWriter.Write(text);
                }
            }
            catch
            {
                // ignore I/O failures
            }
        }

        /// <summary>A <see cref="TextWriter" /> that forwards to the original writer and the log file.</summary>
        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter inner;

            internal TeeTextWriter(TextWriter inner) => this.inner = inner;

            public override Encoding Encoding => this.inner.Encoding;

            public override void Write(char value)
            {
                this.inner.Write(value);
                WriteToFile(value.ToString());
            }

            public override void Write(string? value)
            {
                this.inner.Write(value);
                WriteToFile(value);
            }

            public override void WriteLine(string? value)
            {
                this.inner.WriteLine(value);
                WriteToFile((value ?? string.Empty) + Environment.NewLine);
            }

            public override void Flush() => this.inner.Flush();
        }
    }
}
