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

        // Result tile geometry, read by the tile template through a
        // RelativeSource binding - the DataContext there is one search result,
        // so these cannot live on the view model.
        //
        // Dependency properties rather than plain ones because the template
        // binds them; a CLR property with no change notification would work
        // here only by accident of being set before the first measure.
        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(
                nameof(TileWidth), typeof(double), typeof(SteamGridDbSearchView),
                new PropertyMetadata(240.0));

        public static readonly DependencyProperty ThumbnailHeightProperty =
            DependencyProperty.Register(
                nameof(ThumbnailHeight), typeof(double), typeof(SteamGridDbSearchView),
                new PropertyMetadata(112.0));

        public double TileWidth
        {
            get { return (double)GetValue(TileWidthProperty); }
            set { SetValue(TileWidthProperty, value); }
        }

        public double ThumbnailHeight
        {
            get { return (double)GetValue(ThumbnailHeightProperty); }
            set { SetValue(ThumbnailHeightProperty, value); }
        }

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

            // Fullscreen is read from across a room, so the tiles grow and the
            // filter column gets out of the way. Desktop keeps the sizes that
            // suit a monitor at arm's length.
            if (api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen)
            {
                TileWidth = 340;
                ThumbnailHeight = 160;
                FiltersColumn.Width = new GridLength(0);
                FilterToggleRow.Visibility = Visibility.Visible;
            }

            // Search straight away - the user opened this from a specific game,
            // so making them press Search first is a pointless extra step.
            //
            // Focus goes to the RESULTS, not the search box. The box is already
            // filled with the game's name and the search runs on its own, so a
            // controller user wants to be browsing results immediately - and
            // landing in a text box with a controller means an on-screen
            // keyboard nobody asked for.
            Loaded += async (s, e) =>
            {
                _open = this;
                await RunSearch();
                FocusFirstResult();
            };

            // Cleared on unload, so a controller press after the dialog has
            // gone cannot reach a dead window.
            Unloaded += (s, e) =>
            {
                if (ReferenceEquals(_open, this))
                {
                    _open = null;
                }
            };
        }

        // Puts keyboard focus on the first result tile, so D-pad navigation has
        // somewhere to start. WPF will not move focus from nowhere, so without
        // this the first press of a direction does nothing at all.
        private void FocusFirstResult()
        {
            try
            {
                ResultsList.UpdateLayout();

                var first = FindFirstFocusable(ResultsList);

                if (first != null)
                {
                    first.Focus();
                    return;
                }

                // No results yet - the search may have found nothing. Put focus
                // somewhere reachable rather than leaving the window with none.
                SearchButton.Focus();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not set initial focus in the search dialog");
            }
        }

        private static System.Windows.Controls.Primitives.ToggleButton FindFirstFocusable(
            DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                var toggle = child as System.Windows.Controls.Primitives.ToggleButton;
                if (toggle != null && toggle.Focusable && toggle.IsVisible)
                {
                    return toggle;
                }

                var found = FindFirstFocusable(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
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

        // Shows or hides the filter column.
        //
        // Only reachable in Fullscreen, where the column starts collapsed
        // because it would otherwise take a third of a TV for controls a
        // controller user touches rarely.
        private void FilterToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool show = FilterToggleRow.IsChecked == true;

            FiltersColumn.Width = show ? new GridLength(260) : new GridLength(0);
        }

        // Escape closes, and so does the controller's B.
        //
        // Playnite synthesises real key messages from the pad - D-pad becomes
        // the arrow keys and A becomes Enter, which is why navigation and
        // activation need no code at all. B is the exception: Playnite maps it
        // to nothing (a comment in its own source admits nobody remembers why),
        // so a dialog that does not handle it strands a controller user with no
        // way out.
        //
        // OnPreviewKeyDown rather than the KeyDown event, so this still fires
        // when focus is inside a child that handles keys itself - the search
        // box swallows Escape otherwise.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseWindow();
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        // Closes whichever search dialog is currently open.
        //
        // Static because the controller event arrives at the PLUGIN, which has
        // no reference to a dialog it did not construct. B is the one button
        // Playnite does not synthesise a key for, so this is the only route
        // from "user pressed B" to "the dialog closes".
        public static void CloseOpenDialog()
        {
            SteamGridDbSearchView open = _open;

            if (open == null)
            {
                return;
            }

            try
            {
                open.Dispatcher.Invoke(() => open.CloseWindow());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not close the search dialog from the controller");
            }
        }

        // The dialog is modal and there is only ever one, so a single reference
        // is enough - and it is cleared on unload so a closed dialog cannot be
        // told to close again.
        private static SteamGridDbSearchView _open;

        private void CloseWindow()
        {
            try
            {
                Window.GetWindow(this)?.Close();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not close the search dialog");
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
