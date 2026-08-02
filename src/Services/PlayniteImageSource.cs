using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Reads the background image Playnite already holds for a game.
    //
    // Game.BackgroundImage is a database ID, not a file path, so it must be
    // resolved through IGameDatabaseAPI.GetFullFilePath. That resolver is
    // injected rather than taking IPlayniteAPI directly, so this class is
    // testable without a running Playnite.
    public class PlayniteImageSource : IBackgroundImageSource
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private static readonly IReadOnlyList<string> Empty = new string[0];

        private readonly Func<string, string> _resolveFullPath;
        private readonly ArtworkKind _kind;

        public PlayniteImageSource(
            Func<string, string> resolveFullPath,
            ArtworkKind kind = ArtworkKind.Background)
        {
            _resolveFullPath = resolveFullPath;
            _kind = kind;
        }

        public IReadOnlyList<string> GetImagePaths(Game game)
        {
            if (game == null || _resolveFullPath == null)
            {
                return Empty;
            }

            string imageId = _kind == ArtworkKind.Cover ? game.CoverImage : game.BackgroundImage;
            if (string.IsNullOrEmpty(imageId))
            {
                return Empty;
            }

            try
            {
                string full = _resolveFullPath(imageId);
                if (string.IsNullOrEmpty(full))
                {
                    return Empty;
                }

                // The id can resolve to a path whose file is gone. Write mode
                // replaces game.BackgroundImage and deletes the file it
                // replaced, so a stale id keeps resolving to a deleted file and
                // would stay in the rotation pool forever - picked, then shown
                // as a missing image.
                if (!File.Exists(full))
                {
                    return Empty;
                }

                return new[] { full };
            }
            catch (Exception ex)
            {
                // A resolver failure is a library-data problem. Show nothing
                // rather than taking the control down - but say so, otherwise
                // the symptom is a blank background with nothing in the log.
                Logger.Warn(ex, $"ImageRotater: could not resolve background image path for '{game.Name}'");
                return Empty;
            }
        }
    }
}
