using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ImageRotater.Services
{
    // Validates an external CLI tool by RUNNING it, not by checking the file
    // exists.
    //
    // File.Exists passes for a corrupt download, a wrong-architecture build, or
    // a zero-byte placeholder - all of which then fail at the moment the user
    // actually wanted the feature, with no clue why. Running it with a version
    // flag answers the only question that matters: will this work.
    //
    //   "Found - v<version>"  ran, version parsed
    //   "Found"               ran (exit 0) but the version line was unfamiliar
    //   "Not found"           no path, or nothing there
    //   "Will not run"        the file exists but failed to execute
    //
    // Ported from FullVid's ToolProbe, which had already paid for several of
    // these details: the timeout, killing a hung process, draining BOTH streams
    // on background tasks so a full stderr pipe cannot deadlock WaitForExit,
    // and caching per path+mtime so reopening Settings does not re-shell.
    public class ToolProbe
    {
        private readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private struct CacheEntry
        {
            public long MtimeTicks;
            public string Version;
        }

        // ffmpeg wants a SINGLE dash. "--version" exits non-zero and prints the
        // banner to stderr, so asking the wrong way reports a working install
        // as broken. yt-dlp wants the double dash.
        public const string FfmpegVersionFlag = "-version";
        public const string YtDlpVersionFlag = "--version";

        public string Probe(string toolPath, string versionFlag)
        {
            if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
            {
                return "Not found";
            }

            long mtimeTicks = 0;
            try { mtimeTicks = File.GetLastWriteTimeUtc(toolPath).Ticks; } catch { }

            CacheEntry cached;
            if (mtimeTicks != 0
                && _cache.TryGetValue(toolPath, out cached)
                && cached.MtimeTicks == mtimeTicks)
            {
                return FormatStatus(cached.Version);
            }

            string version = Run(toolPath, versionFlag);

            if (version == null)
            {
                return "Will not run";
            }

            if (mtimeTicks != 0)
            {
                _cache[toolPath] = new CacheEntry { MtimeTicks = mtimeTicks, Version = version };
            }

            return FormatStatus(version);
        }

        public bool Works(string toolPath, string versionFlag)
        {
            string status = Probe(toolPath, versionFlag);
            return status.StartsWith("Found", StringComparison.Ordinal);
        }

        private static string FormatStatus(string version)
        {
            return string.IsNullOrEmpty(version) ? "Found" : $"Found - v{version}";
        }

        // null when the process would not start, timed out, or exited non-zero.
        // "" when it ran but the version was unparseable. Otherwise the version.
        private static string Run(string toolPath, string versionFlag)
        {
            try
            {
                var psi = new ProcessStartInfo(toolPath, versionFlag)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        return null;
                    }

                    // Both streams, on background tasks. A full stderr pipe
                    // deadlocks WaitForExit, and some tools print their version
                    // to stderr rather than stdout.
                    System.Threading.Tasks.Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errTask = proc.StandardError.ReadToEndAsync();

                    // Bounded: a hung tool must not hang the settings page.
                    if (!proc.WaitForExit(3000))
                    {
                        try { proc.Kill(); } catch { }
                        return null;
                    }

                    if (proc.ExitCode != 0)
                    {
                        return null;
                    }

                    return ParseVersion(SafeResult(outTask))
                        ?? ParseVersion(SafeResult(errTask))
                        ?? string.Empty;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeResult(System.Threading.Tasks.Task<string> t)
        {
            try { return t.Wait(1000) ? (t.Result ?? string.Empty) : string.Empty; }
            catch { return string.Empty; }
        }

        // yt-dlp prints just the version ("2025.11.12"). ffmpeg prints a block
        // whose first line is "ffmpeg version 8.0-full_build ... Copyright ...".
        private static string ParseVersion(string stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            string firstLine = null;

            foreach (string line in stdout.Split('\n'))
            {
                string t = line.Trim();

                if (t.Length > 0)
                {
                    firstLine = t;
                    break;
                }
            }

            if (string.IsNullOrEmpty(firstLine))
            {
                return null;
            }

            string[] parts = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 3 &&
                parts[0].Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                return parts[2];
            }

            return firstLine;
        }
    }
}
