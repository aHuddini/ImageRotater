using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    // Browse SteamGridDB artwork for a game, filter it, and download the ones
    // you pick. The auto-download menu item takes the highest-resolution match
    // without asking; this is for when that guess is not what you wanted.
    public partial class SteamGridDbSearchView : UserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly SteamGridDbSearchViewModel _model;
        private readonly ArtworkDownloader _downloader;
        private readonly IPlayniteAPI _api;
        private readonly Guid _gameId;
        private readonly ArtworkKind _kind;

        // Peer source to SteamGridDB, used when the checkbox is ticked.
        private readonly WebImageSearch _webSearch;

        public SteamGridDbSearchView(
            IPlayniteAPI api,
            ISteamGridDbClient client,
            ArtworkDownloader downloader,
            Guid gameId,
            string gameName,
            ArtworkKind kind = ArtworkKind.Background)
        {
            InitializeComponent();

            _api = api;
            _downloader = downloader;
            _gameId = gameId;
            _kind = kind;
            _webSearch = new WebImageSearch(api);

            _model = new SteamGridDbSearchViewModel(client);
            DataContext = _model;

            SearchBox.Text = gameName ?? string.Empty;

            // Search straight away - the user opened this from a specific game,
            // so making them press Search first is a pointless extra step.
            Loaded += async (s, e) => await RunSearch();
        }

        private async Task RunSearch()
        {
            SearchButton.IsEnabled = false;
            try
            {
                if (ReferenceEquals(SourceTabs.SelectedItem, WebTab))
                {
                    // NOT wrapped in Task.Run: the method puts only the
                    // browsing off-thread itself, because the collections it
                    // updates afterwards are bound to the UI and WPF rejects
                    // collection changes from any other thread.
                    await _model.SearchWebAsync(SearchBox.Text, _webSearch);
                    return;
                }

                // Covers come from grids, backgrounds from heroes.
                SteamGridDbArtworkType type = _kind == ArtworkKind.Cover
                    ? SteamGridDbArtworkType.Grid
                    : SteamGridDbArtworkType.Hero;

                await _model.SearchAsync(SearchBox.Text, type);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: SteamGridDB search failed");
                _model.Status = "Search failed. See the Playnite log for details.";
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSearch();
        }

        // Re-run on tab change so the results always match the visible tab.
        // Leaving the previous source's results on screen under a different
        // heading is worse than a moment's wait.
        private async void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Fires once while the tabs are being built, before the rest of the
            // dialog exists. The Loaded handler runs the first search.
            if (!IsLoaded || !ReferenceEquals(e.OriginalSource, SourceTabs))
            {
                return;
            }

            await RunSearch();
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await RunSearch();
            }
        }

        // Starts (or clears) GIF playback for one result tile.
        //
        // This lives in code-behind rather than a DataTrigger on purpose. A
        // gif: xmlns in the XAML makes BAML resolve XamlAnimatedGif through
        // Assembly.Load, which probes only Playnite's own folder and never the
        // extension folder our DLL sits in - InitializeComponent then throws
        // XamlParseException and the dialog never opens, animated results or
        // not. Reached from code, the same reference resolves in the LoadFrom
        // context that Playnite loaded us with, which does know where we live.
        //
        // The results panel virtualizes, so this Image may have been showing a
        // different result a moment ago. Both branches must therefore assign:
        // clearing on the static branch is what stops a recycled tile from
        // playing the previous item's GIF underneath a new thumbnail. Exactly
        // one channel drives the Image at a time - the same rule the cover
        // control follows.
        private void ResultImage_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as System.Windows.Controls.Image;
            if (image == null)
            {
                return;
            }

            var item = image.DataContext as SteamGridDbArtwork;
            var uri = item != null && item.IsGif ? item.UrlUri : null;

            try
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(image, uri);
            }
            catch (Exception ex)
            {
                // A single unplayable result must not take the dialog down: the
                // static thumbnail underneath is still perfectly good.
                Logger.Warn(ex, "ImageRotater: could not start GIF preview");
            }
        }

        private void ApplyFilters_Click(object sender, RoutedEventArgs e)
        {
            _model.Filter.ShowNsfw = ShowNsfwBox.IsChecked == true;
            _model.Filter.ShowHumor = ShowHumorBox.IsChecked == true;
            _model.Filter.ShowEpilepsy = ShowEpilepsyBox.IsChecked == true;
            _model.Filter.ShowAnimated = ShowAnimatedBox.IsChecked == true;

            _model.ApplyFilter();
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            // Read from the model, not ListBox.SelectedItems: a filter rebuilds
            // the visible list, and anything ticked but currently filtered out
            // would otherwise be dropped from the download without saying so.
            var chosen = _model.SelectedArtwork();
            if (chosen.Count == 0)
            {
                _model.Status = "Tick one or more images first.";
                return;
            }

            DownloadButton.IsEnabled = false;
            _model.Status = $"Downloading {chosen.Count} image(s)...";

            int saved = 0;
            try
            {
                foreach (SteamGridDbArtwork artwork in chosen)
                {
                    if (await _downloader.DownloadAsync(_gameId, artwork, _kind) != null)
                    {
                        saved++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: SteamGridDB download failed");
            }
            finally
            {
                DownloadButton.IsEnabled = true;
            }

            _model.Status = saved == chosen.Count
                ? $"Downloaded {saved} image(s)."
                : $"Downloaded {saved} of {chosen.Count}. See the Playnite log for the rest.";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
