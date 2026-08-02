using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // The user-facing side of image storage: add files, open the folder,
    // clear a game's images. Without this the plugin has no way to be
    // populated and is unusable regardless of how well it renders.
    public class ImageMenuHandler
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IPlayniteAPI _api;
        private readonly GameImageStore _store;
        private readonly SessionSelectionCache _sessionCache;
        private readonly ISteamGridDbClient _steamGridDb;
        private readonly ArtworkDownloader _downloader;

        // Called when a game's candidate images have changed. Write mode
        // rotates a game only once per selection, so without this the game
        // currently selected would keep its old artwork until the user
        // navigated away and back. A callback rather than the rotation service
        // itself: this class only needs to announce the change, not drive it.
        private readonly Action<Guid> _onImagesChanged;

        public ImageMenuHandler(
            IPlayniteAPI api,
            GameImageStore store,
            SessionSelectionCache sessionCache,
            ISteamGridDbClient steamGridDb,
            ArtworkDownloader downloader,
            Action<Guid> onImagesChanged = null)
        {
            _api = api;
            _store = store;
            _sessionCache = sessionCache;
            _steamGridDb = steamGridDb;
            _downloader = downloader;
            _onImagesChanged = onImagesChanged;
        }

        // Opens the browse-and-pick dialog for one game. The auto-download path
        // takes the highest-resolution match without asking; this is for when
        // that guess is not the one you wanted.
        public void BrowseSteamGridDb(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            if (_steamGridDb == null || !_steamGridDb.IsConfigured)
            {
                _api.Dialogs.ShowErrorMessage(
                    "Add your SteamGridDB API key in ImageRotater settings first.\n\n" +
                    "You can get a free key from steamgriddb.com under Preferences > API.",
                    "ImageRotater");
                return;
            }

            try
            {
                Window window = _api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true
                });

                string label = kind == ArtworkKind.Cover ? "covers" : "backgrounds";
                window.Title = $"ImageRotater - search {label}: {game.Name}";
                window.Width = 1000;
                window.Height = 640;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Content = new Controls.SteamGridDbSearchView(
                    _api, _steamGridDb, _downloader, game.Id, game.Name, kind);

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not open the SteamGridDB browser");
                _api.Dialogs.ShowErrorMessage(
                    "Could not open the SteamGridDB browser. See the Playnite log for details.",
                    "ImageRotater");
            }
        }

        // Fetches backgrounds from SteamGridDB for each selected game and saves
        // the highest-resolution match into that game's folder.
        //
        // Runs on Playnite's progress dialog because it is network-bound over
        // potentially many games, and the user needs to be able to cancel.
        public void DownloadFromSteamGridDb(IEnumerable<Game> games, ArtworkKind kind)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            if (_steamGridDb == null || !_steamGridDb.IsConfigured)
            {
                _api.Dialogs.ShowErrorMessage(
                    "Add your SteamGridDB API key in ImageRotater settings first.\n\n" +
                    "You can get a free key from steamgriddb.com under Preferences > API.",
                    "ImageRotater");
                return;
            }

            int downloaded = 0;
            var failures = new List<string>();

            _api.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = targets.Count;

                foreach (Game game in targets)
                {
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    progress.Text = $"ImageRotater: {game.Name}";

                    try
                    {
                        string saved = DownloadBestForGame(game, kind).GetAwaiter().GetResult();
                        if (saved != null)
                        {
                            downloaded++;
                        }
                        else
                        {
                            failures.Add(game.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, $"ImageRotater: SteamGridDB download failed for {game.Name}");
                        failures.Add(game.Name);
                    }

                    progress.CurrentProgressValue++;
                }
            },
            new GlobalProgressOptions("ImageRotater: downloading artwork", true)
            {
                IsIndeterminate = false
            });

            ShowDownloadSummary(downloaded, failures);
        }

        private async Task<string> DownloadBestForGame(Game game, ArtworkKind kind)
        {
            SteamGridDbResult<List<SteamGridDbGame>> matches =
                await _steamGridDb.SearchGamesAsync(game.Name).ConfigureAwait(false);

            if (!matches.Success || matches.Data == null || matches.Data.Count == 0)
            {
                return null;
            }

            // Covers come from grids, backgrounds from heroes - different
            // endpoints with different dimension conventions.
            SteamGridDbArtworkType type = kind == ArtworkKind.Cover
                ? SteamGridDbArtworkType.Grid
                : SteamGridDbArtworkType.Hero;

            SteamGridDbResult<List<SteamGridDbArtwork>> artwork =
                await _steamGridDb.GetArtworkAsync(matches.Data[0].Id, type).ConfigureAwait(false);

            if (!artwork.Success || artwork.Data == null)
            {
                return null;
            }

            // Default filter: no adult/humor/epilepsy-tagged art, and nothing
            // animated - ImageRotater cannot render animated formats yet, so
            // downloading one would produce a broken-image placeholder.
            IReadOnlyList<SteamGridDbArtwork> usable =
                ArtworkFilter.Apply(artwork.Data, new ArtworkFilterState());

            SteamGridDbArtwork best;

            if (kind == ArtworkKind.Cover)
            {
                // Covers are drawn into a fixed-aspect card, so shape matters
                // more than pixel count: a correctly-proportioned 600x900 looks
                // right where a sharper 16:9 image would be cropped or letterboxed.
                best = CoverAspect.BestCover(usable, GetTargetCoverAspect());
            }
            else
            {
                // Backgrounds fill the screen, so more pixels is simply better.
                best = usable
                    .OrderByDescending(a => (long)a.Width * a.Height)
                    .FirstOrDefault();
            }

            if (best == null)
            {
                return null;
            }

            return await _downloader.DownloadAsync(game.Id, best, kind).ConfigureAwait(false);
        }

        // Playnite exposes the grid cell ratio the user has actually configured,
        // so the cover target does not have to be a setting we invent and ask
        // them to tune.
        private double GetTargetCoverAspect()
        {
            try
            {
                return CoverAspect.FromGridRatio(
                    _api.ApplicationSettings.GridItemWidthRatio,
                    _api.ApplicationSettings.GridItemHeightRatio);
            }
            catch (Exception)
            {
                return CoverAspect.DefaultAspect;
            }
        }

        private void ShowDownloadSummary(int downloaded, List<string> failures)
        {
            if (downloaded == 0 && failures.Count == 0)
            {
                return;
            }

            string message = $"Downloaded artwork for {downloaded} game(s).";

            if (failures.Count > 0)
            {
                // Name the games that failed rather than only counting them, so
                // the user can retry or add art manually for those specific
                // titles.
                const int maxNamed = 10;
                message += $"\n\nNo artwork found for {failures.Count}:\n"
                    + string.Join("\n", failures.Take(maxNamed));

                if (failures.Count > maxNamed)
                {
                    message += $"\n...and {failures.Count - maxNamed} more.";
                }
            }

            _api.Dialogs.ShowMessage(message, "ImageRotater");
        }

        // Opens a file picker and copies the chosen images into the game's
        // folder. Returns how many were added.
        public int AddImages(IEnumerable<Game> games, ArtworkKind kind)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return 0;
            }

            List<string> selected = _api.Dialogs.SelectFiles(
                "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif");

            if (selected == null || selected.Count == 0)
            {
                return 0;
            }

            int added = 0;
            foreach (Game game in targets)
            {
                foreach (string source in selected)
                {
                    if (_store.AddImage(game.Id, source, kind) != null)
                    {
                        added++;
                    }
                }

                // The candidate list changed, so any remembered choice for this
                // game is stale - drop it so the new image can be picked.
                _sessionCache?.Forget(game.Id);
                _onImagesChanged?.Invoke(game.Id);
            }

            if (added > 0)
            {
                _api.Dialogs.ShowMessage(
                    $"Added {added} image(s) to {targets.Count} game(s).",
                    "ImageRotater");
            }

            return added;
        }

        // Opens the game's image folder in Explorer, creating it if needed so
        // the user can drop files straight in.
        public void OpenImageFolder(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            try
            {
                string folder = _store.GetGameFolder(game.Id, kind);
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not open image folder for {game.Name}");
            }
        }

        public void ClearImages(IEnumerable<Game> games, ArtworkKind kind)
        {
            var targets = games?.ToList();
            if (targets == null || targets.Count == 0)
            {
                return;
            }

            int total = targets.Sum(g => _store.GetImagePaths(g.Id, kind).Count);
            if (total == 0)
            {
                _api.Dialogs.ShowMessage("No ImageRotater images to remove.", "ImageRotater");
                return;
            }

            // Deleting the user's files is not undoable, so confirm first and
            // say exactly how many are going.
            MessageBoxResult confirm = _api.Dialogs.ShowMessage(
                $"Remove {total} image(s) from {targets.Count} game(s)?\n\nThe files will be deleted.",
                "ImageRotater",
                MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            int removed = 0;
            foreach (Game game in targets)
            {
                foreach (string path in _store.GetImagePaths(game.Id, kind))
                {
                    if (_store.RemoveImage(path))
                    {
                        removed++;
                    }
                }

                _sessionCache?.Forget(game.Id);
                _onImagesChanged?.Invoke(game.Id);
            }

            _api.Dialogs.ShowMessage($"Removed {removed} image(s).", "ImageRotater");
        }
    }
}
