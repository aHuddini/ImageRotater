using System;
using System.IO;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Finds an external executable: an explicit path if the user set one,
    // otherwise the first match on PATH.
    //
    // ffmpeg and yt-dlp are both GPL and this plugin is MIT, so neither can be
    // bundled - shipping either binary would relicense the whole project. The
    // plugin therefore uses what the user already has, and the settings page
    // says plainly which features are unavailable without them rather than
    // leaving a control that quietly does nothing.
    //
    // Shared so both tools resolve identically, including the rules that are
    // easy to get subtly different: an explicit path is honoured even when it
    // is wrong (so the user sees their own mistake rather than a silent
    // fallback), and PATH is only consulted when no path is set.
    public static class ExternalTool
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Resolves one tool. Returns null when it cannot be found.
        //
        // configured: what the user typed, which may be empty, a full path, or
        // a directory containing the executable.
        public static string Resolve(string configured, string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    string explicitPath = configured.Trim().Trim('"');

                    // A full path to the executable itself.
                    if (File.Exists(explicitPath))
                    {
                        return explicitPath;
                    }

                    // A directory holding it - the likelier mistake when
                    // someone browses to a folder rather than the exe.
                    string inDirectory = Path.Combine(explicitPath, executableName);

                    if (File.Exists(inDirectory))
                    {
                        return inDirectory;
                    }

                    // Deliberately NOT falling through to PATH here. A user who
                    // set a path and typed it wrong should see that it is
                    // wrong, not silently get a different copy of the tool.
                    return null;
                }

                return FindOnPath(executableName);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not resolve {executableName}");
                return null;
            }
        }

        public static string FindOnPath(string executableName)
        {
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

                foreach (string dir in pathVar.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }

                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), executableName);

                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (Exception)
                    {
                        // One malformed PATH entry must not stop the search.
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not search PATH for {executableName}");
            }

            return null;
        }

        public const string FfmpegExe = "ffmpeg.exe";
        public const string YtDlpExe = "yt-dlp.exe";
    }
}
