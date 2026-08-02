using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    public partial class BackgroundImageControl : PluginUserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly ImageSelector _selector;
        private readonly ImageLoader _loader;

        // An accessor, not the settings object: saving settings replaces the
        // whole object, so a captured reference would go stale immediately.
        private readonly Func<ImageRotaterSettings> _settings;

        // Last path shown for the CURRENT game, so a revisit can avoid
        // repeating it.
        private string _previousPick;

        // Paths already reported as unloadable. The spec asks for one log per
        // path, not one per refresh.
        private readonly HashSet<string> _loggedFailures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Guards against a slow decode landing after the user has already
        // moved on to another game.
        private int _requestToken;

        private int _currentBucket = 0;

        public BackgroundImageControl(IBackgroundImageSource source, ImageSelector selector, ImageLoader loader, Func<ImageRotaterSettings> settings)
        {
            InitializeComponent();

            _source = source;
            _selector = selector;
            _loader = loader;
            _settings = settings;

            // Subscribe in Loaded, unsubscribe in Unloaded. Never in the
            // constructor - a control whose visual tree is rebuilt would
            // otherwise accumulate handlers and pin every dead instance.
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SizeChanged += OnSizeChanged;

            // Static event, so the unsubscribe in Unloaded is mandatory - see
            // CoverImageControl for why this exists at all.
            CoverImageControl.ArtworkRotated += OnArtworkRotated;

            Refresh();
        }

        private void OnArtworkRotated(Guid gameId)
        {
            if (GameContext == null || GameContext.Id != gameId)
            {
                return;
            }

            // The pick changed underneath us; the avoid-previous memory would
            // otherwise veto the file the rotation just chose.
            _previousPick = null;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SizeChanged -= OnSizeChanged;
            CoverImageControl.ArtworkRotated -= OnArtworkRotated;

            // Drop the bitmap reference. The cache owns the lifetime; the
            // control only borrows. The GIF behaviour is released too - an
            // unloaded control must not keep an animation decoding frames.
            DisplayImage.Source = null;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);

            // Invalidate any decode still in flight.
            _requestToken++;
        }

        // The SDK calls this when the selected game changes. This is the
        // rotation trigger - v0.1 has no timer.
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // A different game means the previous pick no longer applies.
            _previousPick = null;
            Refresh();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Only re-decode when the width crosses into a different bucket.
            // Ordinary resizes stay in-bucket and cost nothing.
            int bucket = WidthBucket.ForWidth(ActualWidth);
            if (bucket != _currentBucket)
            {
                Refresh();
            }
        }

        // async void is unavoidable - this is event-handler shaped - so the
        // catch-all is the standard mitigation. Without it an exception after
        // the await (a disposed dispatcher during shutdown, say) is rethrown on
        // the SynchronizationContext and takes Playnite down.
        private async void Refresh()
        {
            try
            {
                int token = ++_requestToken;

                // Recorded up front, before any early-out. A game with no image
                // or a failed load must still update the bucket, otherwise it
                // stays 0, never matches ForWidth, and every SizeChanged during
                // a window drag calls Refresh again.
                int bucket = WidthBucket.ForWidth(ActualWidth);
                _currentBucket = bucket;

                ImageRotaterSettings settings = _settings != null ? _settings() : null;
                if (settings != null && !settings.EnableRotation)
                {
                    ShowNothing();
                    return;
                }

                Game game = GameContext;
                if (game == null || _source == null || _selector == null || _loader == null)
                {
                    ShowNothing();
                    return;
                }

                IReadOnlyList<string> candidates = _source.GetImagePaths(game);
                if (candidates == null || candidates.Count == 0)
                {
                    // The common case: this game simply has no background. Render
                    // nothing so the theme's own background shows through, and do
                    // not log - it is not an error.
                    ShowNothing();
                    return;
                }

                SelectionMode mode = settings != null ? settings.SelectionMode : SelectionMode.Session;
                string path = _selector.Select(game.Id, candidates, _previousPick, mode);

                // Recorded before the load, not after: a pick that fails to load
                // still counts as tried, so rotation moves past a broken file
                // instead of selecting it forever.
                _previousPick = path;

                if (string.IsNullOrEmpty(path))
                {
                    ShowNothing();
                    return;
                }

                // Video plays in the MediaElement, which is a different
                // renderer from the Image entirely - GDI+ and WPF's imaging
                // stack cannot decode a container at all. Only one of the two
                // elements is ever visible.
                if (PosterFrame.IsVideo(path))
                {
                    ShowVideo(path);
                    ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
                    return;
                }

                // GIFs play here, where the write path cannot make them move.
                //
                // Game.BackgroundImage decodes to a single BitmapSource, so a
                // rotation writes a first-frame poster instead - correct, but it
                // means an animated background is only ever animated by THIS
                // control. The cover control has worked this way since the GIF
                // work landed; backgrounds simply never got the same branch.
                //
                // Exactly one channel drives the Image at a time: the attached
                // property owns Image.Source while active, so the static branch
                // must clear it or a GIF keeps playing under the next pick.
                if (PosterFrame.IsAnimated(path))
                {
                    StopVideo();

                    DisplayImage.Source = null;
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, new Uri(path));

                    ShowControl();

                    DisplayImage.Visibility = Visibility.Visible;
                    MissingImagePlaceholder.Visibility = Visibility.Collapsed;

                    ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
                    return;
                }

                StopVideo();

                // The animation is NOT released here.
                //
                // The decode below is awaited, so releasing first left
                // Image.Source empty for its entire duration - a visible blank
                // on every animated-to-still rotation, and longer for a large
                // background than for a cover. The previous frame stays up
                // until the replacement is actually in hand.
                BitmapSource image = await _loader.LoadAsync(path, bucket);

                // The user moved on while this was decoding.
                if (token != _requestToken)
                {
                    return;
                }

                // Try the other candidates before giving up. The write path has
                // done this since 0.5.4; this one never did, so a single
                // unloadable file here showed a placeholder instead of the
                // artwork that was sitting right beside it - which is what made
                // the blank look intermittent rather than tied to one game.
                if (image == null)
                {
                    foreach (string alternate in candidates)
                    {
                        if (string.Equals(alternate, path, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        image = await _loader.LoadAsync(alternate, bucket);

                        if (token != _requestToken)
                        {
                            return;
                        }

                        if (image != null)
                        {
                            path = alternate;
                            _previousPick = alternate;
                            break;
                        }
                    }
                }

                if (image == null)
                {
                    // A path existed but did not load: the file is missing or
                    // corrupt. That is a real problem worth surfacing - once.
                    if (_loggedFailures.Add(path))
                    {
                        Logger.Warn($"ImageRotater: could not load background image: {path}");
                    }

                    ShowPlaceholder();
                    return;
                }

                // Released here, with the replacement already in hand, so the
                // two assignments happen in the same frame and nothing blanks
                // in between. The attached property owns Image.Source while
                // set, so it has to go before the decoded bitmap is assigned -
                // just not any earlier than that.
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);

                DisplayImage.Source = image;
                ShowControl();
                DisplayImage.Visibility = Visibility.Visible;
                MissingImagePlaceholder.Visibility = Visibility.Collapsed;

                // Reports the source file's real dimensions, plus the bucket it
                // was decoded at, so a soft-looking background can be traced to
                // either a small source or the wrong decode size.
                ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, image.PixelWidth);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater refresh failed");
            }
        }

        // Both hide states stop playback. A collapsed Image still decodes
        // frames while the behaviour holds a source, so a game with no
        // background would otherwise keep the previous game's GIF running
        // invisibly.
        // Reveals the control, called only by paths about to DRAW.
        //
        // Not at the top of Refresh: that made the control visible before
        // anyone knew whether this game had artwork, so a game with none went
        // visible -> laid out -> collapsed one frame later. Recycled tiles were
        // already collapsed and never flashed, which is why it showed on a
        // first pass through a library and not the second.
        private void ShowControl()
        {
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }
        }

        // Nothing to show, so the control stands down ENTIRELY - see the note
        // on CoverImageControl.ShowNothing. A visible empty control still
        // measures, arranges and paints whatever Background a theme's implicit
        // Control style gave it, over the theme's own artwork.
        private void ShowNothing()
        {
            ClearImage();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
        }

        private void ShowPlaceholder()
        {
            ClearImage();
            DisplayImage.Visibility = Visibility.Collapsed;
            ShowControl();
            MissingImagePlaceholder.Visibility = Visibility.Visible;
        }

        private void ClearImage()
        {
            DisplayImage.Source = null;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
        }

        // Hands a video to the MediaElement and hides the Image, so exactly one
        // renderer is drawing.
        private void ShowVideo(string path)
        {
            DisplayImage.Source = null;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;

            DisplayVideo.Source = new Uri(path);
            ShowControl();
            DisplayVideo.Visibility = Visibility.Visible;
            DisplayVideo.Play();
        }

        // Stop AND drop the source. Stop alone leaves the file open, and the
        // rotation deletes and replaces these files underneath us.
        private void StopVideo()
        {
            if (DisplayVideo.Source == null && DisplayVideo.Visibility == Visibility.Collapsed)
            {
                return;
            }

            DisplayVideo.Stop();
            DisplayVideo.Source = null;
            DisplayVideo.Visibility = Visibility.Collapsed;
        }

        // Loop. Artwork clips are short and meant to repeat; MediaElement has
        // no repeat property, so the end of one playback seeds the next.
        private void DisplayVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                DisplayVideo.Position = TimeSpan.Zero;
                DisplayVideo.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop background video");
            }
        }

        // A codec the machine does not have is the common case here - .webm
        // needs a filter Windows does not ship. Fall back to the placeholder
        // rather than leaving a black rectangle, and say so once per path.
        private void DisplayVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string path = DisplayVideo.Source?.LocalPath;

            if (!string.IsNullOrEmpty(path) && _loggedFailures.Add(path))
            {
                Logger.Warn($"ImageRotater: could not play background video (missing codec?): {path}");
            }

            StopVideo();
            ShowPlaceholder();
        }
    }
}
