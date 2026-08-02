using System;
using System.Collections.Generic;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater.Controls
{
    // Carries a rotated cover to whatever wants to draw it.
    //
    // This control decides WHICH cover a game shows and publishes it as a path
    // on its DataContext. It does not decode anything: the XAML binds that path
    // with IsAsync=True, and a theme may instead keep this control hidden and
    // bind Content.ImagePath to render the cover with its own element - which
    // is how Aniki integrates BackgroundChanger.
    //
    // The earlier version assigned DisplayImage.Source in code-behind, which
    // had two consequences. Nothing outside could bind to it, so themes could
    // not reach the value at all. And it decoded during layout, so placing it
    // in a Fullscreen grid template - dozens of tiles realising at once, in a
    // 32-bit process - took Playnite down. That crash was the synchronous
    // decode, not the presence of a plugin control: BackgroundChanger's
    // equivalent survives the same placement precisely because it binds a path
    // asynchronously.
    public partial class CoverImageControl : PluginUserControl
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly ImageSelector _selector;
        private readonly Func<ImageRotaterSettings> _settings;

        private readonly CoverImageDataContext _data = new CoverImageDataContext();

        // Last path shown for the current game, so a revisit can avoid
        // repeating it.
        private string _previousPick;

        private readonly HashSet<string> _loggedFailures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CoverImageControl(
            IBackgroundImageSource source,
            ImageSelector selector,
            Func<ImageRotaterSettings> settings)
        {
            InitializeComponent();

            _source = source;
            _selector = selector;
            _settings = settings;

            DataContext = _data;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // What a theme binds when it hosts this control hidden and draws the
        // cover itself.
        //
        // Deliberately NOT called "Content": PluginUserControl inherits
        // ContentControl.Content, so that name would shadow an existing
        // dependency property and a theme binding "Content.X" would silently
        // resolve against WPF's property instead of this one. The DataContext
        // is set to the same object, so themes can also bind straight through
        // without naming this at all.
        public CoverImageDataContext CoverData
        {
            get { return _data; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Subscribed here and dropped in Unloaded, never in the
            // constructor: this is a STATIC event, so a control that never
            // unsubscribed would be pinned for the session - and a virtualised
            // grid builds these by the dozen.
            ArtworkRotated += OnArtworkRotated;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ArtworkRotated -= OnArtworkRotated;

            // Release the path so a recycled tile does not briefly show the
            // previous game's cover. The binding owns the bitmap's lifetime.
            // The GIF behaviour is released too - an unloaded tile must not
            // keep an animation decoding frames forever.
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
        }

        // Playnite calls this when the tile is bound to a different game -
        // which for a virtualised grid is every time it scrolls into reuse.
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            _previousPick = null;
            Refresh();
        }

        // A slideshow tick rotates artwork while the SAME game stays selected,
        // so Playnite raises no context change and nothing above would ever
        // tell this control to re-read. Static because the controls are built
        // by Playnite on demand and there is no reference to hand them: the
        // rotation service announces, whoever is alive listens.
        //
        // Matters most for video and GIFs. A still would merely be stale; a
        // MediaElement keeps PLAYING the previous pick, so the slideshow would
        // appear to do nothing at all while the file underneath it changed.
        public static event Action<Guid> ArtworkRotated;

        public static void NotifyArtworkRotated(Guid gameId)
        {
            Action<Guid> handler = ArtworkRotated;

            if (handler != null)
            {
                handler(gameId);
            }
        }

        // Which game is selected right now, so only that tile animates.
        //
        // A grid realises a screenful of these at once, and every one holding
        // an animated cover decodes frames continuously on the UI thread - in a
        // 32-bit process shared with Chromium. Dozens of simultaneous decoders
        // is the same pressure that took Playnite down during the theme
        // experiments, and it buys nothing: a wall of moving thumbnails is
        // harder to read than one.
        //
        // Static and set by the plugin, because a control has no way to ask
        // whether its own tile is selected - GameListItem has no IsSelected,
        // and the containing ListBoxItem is not reachable from the control's
        // own code without walking the visual tree on every refresh.
        private static Guid _selectedGame;

        public static void NotifySelectionChanged(Guid gameId)
        {
            if (_selectedGame == gameId)
            {
                return;
            }

            _selectedGame = gameId;

            // Both the tile gaining selection and the one losing it need to
            // re-decide, and neither gets a context change for it.
            Action<Guid> handler = ArtworkRotated;

            if (handler != null)
            {
                handler(gameId);
            }
        }

        private bool IsSelectedTile
        {
            get { return GameContext != null && GameContext.Id == _selectedGame; }
        }

        // True while this tile is actually decoding something. Lets a tile that
        // loses selection recognise it has work to stop, since the announcement
        // names the arriving game rather than this one.
        private bool _animating;

        // Renders a motion pick as its own still frame, for a tile that is not
        // selected. Same channel a static pick uses, so nothing else changes.
        private void ShowStill(string path)
        {
            StopVideo();
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            _animating = false;

            _data.ImagePath = path;

            DisplayImage.Visibility = Visibility.Visible;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void OnArtworkRotated(Guid gameId)
        {
            // Normally only this tile's own game - a grid raises this for one
            // game while dozens of controls listen.
            //
            // The exception is a tile that is currently ANIMATING but is no
            // longer the selected one. Selection moving away is announced
            // against the ARRIVING game, so the departing tile would never hear
            // it and would keep decoding frames forever.
            bool mine = GameContext != null && GameContext.Id == gameId;
            bool mustStandDown = _animating && !IsSelectedTile;

            if (!mine && !mustStandDown)
            {
                return;
            }

            // The pick just changed underneath us, so the avoid-previous memory
            // would otherwise veto the file the rotation actually chose.
            _previousPick = null;
            Refresh();
        }

        private void Refresh()
        {
            try
            {
                // Undone here rather than in each of the four paths that draw
                // something, so a new path cannot forget it and leave the
                // control collapsed for the rest of the session. ShowNothing
                // sets it again on the way out if there is still nothing.
                Visibility = Visibility.Visible;

                ImageRotaterSettings settings = _settings != null ? _settings() : null;

                if (settings == null || !settings.EnableRotation || !settings.RotateCovers)
                {
                    ShowNothing();
                    return;
                }

                Game game = GameContext;
                if (game == null || _source == null || _selector == null)
                {
                    ShowNothing();
                    return;
                }

                IReadOnlyList<string> candidates = _source.GetImagePaths(game);
                if (candidates == null || candidates.Count == 0)
                {
                    // The common case: this game has no plugin cover. Render
                    // nothing so the theme's own artwork shows through, and do
                    // not log - it is not an error.
                    ShowNothing();
                    return;
                }

                string path = _selector.Select(
                    game.Id, candidates, _previousPick, settings.CoverSelectionMode);

                // Recorded before use, so a pick that turns out to be unusable
                // still counts as tried and rotation moves past it.
                _previousPick = path;

                if (!IsUsable(path))
                {
                    path = FirstUsable(candidates, path);
                }

                if (string.IsNullOrEmpty(path))
                {
                    // Every candidate is missing. Worth saying once per path.
                    if (_previousPick != null && _loggedFailures.Add(_previousPick))
                    {
                        Logger.Warn($"ImageRotater: no usable cover image for \"{game.Name}\"");
                    }

                    ShowPlaceholder();
                    return;
                }

                // Moving artwork plays on the SELECTED tile only.
                //
                // Everywhere else it renders as its own still frame. A grid
                // realises a screenful of these at once, and every animated one
                // decodes continuously on the UI thread in a 32-bit process -
                // the same pressure that took Playnite down when a theme added
                // its own media element per tile. One moving cover reads better
                // than twenty anyway.
                if (PosterFrame.IsMotion(path) && !IsSelectedTile)
                {
                    string still = PosterFrame.For(path);

                    if (!string.IsNullOrEmpty(still))
                    {
                        ShowStill(still);
                        return;
                    }

                    // No still could be extracted - video, whose container GDI+
                    // cannot open. Render nothing rather than start playback on
                    // an unselected tile.
                    ShowNothing();
                    return;
                }

                // Video is a third channel, and a different renderer: WPF's
                // imaging stack cannot decode a container, so this cannot be a
                // mode of the Image.
                if (PosterFrame.IsVideo(path))
                {
                    ShowVideo(path);
                    return;
                }

                // Exactly one channel drives the Image at a time. Static picks
                // go through the DataContext path binding; GIFs go through
                // XamlAnimatedGif's attached property, which owns Image.Source
                // while active. Setting both would race - the one-channel rule
                // that already bit this control once (the Content shadowing).
                StopVideo();

                if (PosterFrame.IsAnimated(path))
                {
                    // Order matters. The attached property takes Image.Source
                    // synchronously, so it goes FIRST - clearing the binding
                    // first would blank the tile until the animation loaded.
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, new Uri(path));
                    _data.ImagePath = string.Empty;
                    _animating = true;
                }
                else
                {
                    // And the other way round here, for the opposite reason.
                    //
                    // The still arrives through a binding marked IsAsync=True,
                    // so it lands some time AFTER this returns. Releasing the
                    // animation first left Image.Source empty for that whole
                    // gap - a visible blank on every animated-to-still
                    // rotation. Handing over the path first means the old frame
                    // stays up until the new image is decoded and ready.
                    //
                    // IsAsync is not negotiable: a synchronous decode inside a
                    // Fullscreen tile's layout pass is what took Playnite down
                    // before, so the fix has to work with the delay rather than
                    // remove it.
                    _data.ImagePath = path;
                    _animating = false;
                    ReleaseAnimationWhenStillArrives();
                }

                DisplayImage.Visibility = Visibility.Visible;
                MissingImagePlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                // Nothing may escape into Playnite's layout pass: an exception
                // there is not caught above us and takes the process down with
                // no dialog and nothing in the log.
                Logger.Error(ex, "ImageRotater cover refresh failed");
            }
        }

        private static bool IsUsable(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // First candidate that still exists, skipping the one already rejected.
        // A single unloadable file should cost one retry, not the whole tile.
        private static string FirstUsable(IReadOnlyList<string> candidates, string skip)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUsable(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return null;
        }

        // Nothing to show for this game, so the control stands down ENTIRELY -
        // not just its children.
        //
        // Collapsing only the Image left a visible, stretched, empty control
        // over the tile's own cover. It painted nothing itself, but it still
        // measured, arranged and composited on every tile realisation, and any
        // Background a theme's implicit Control style handed it was drawn.
        // Scrolling past games nobody had set up flickered black.
        //
        // A collapsed control is skipped by layout altogether, which is the
        // correct answer for "this game is not ours".
        private void ShowNothing()
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
        }

        private void ShowPlaceholder()
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Visible;
        }

        // Drops the GIF behaviour once the still it is being replaced by has
        // actually decoded.
        //
        // The still comes through an IsAsync binding, so it is not on screen
        // when Refresh returns. Releasing the animation synchronously therefore
        // blanked the tile for the length of that decode, which is the flicker
        // that appeared on every animated-to-still rotation.
        //
        // Queued at Loaded priority: the async binding's own callback runs at
        // that level, so this lands after it rather than racing it. If the
        // decode is slower still, the worst case is an extra moment of the
        // previous frame - the old behaviour's worst case was a blank.
        private void ReleaseAnimationWhenStillArrives()
        {
            try
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        // Another rotation may have chosen a GIF while this was
                        // queued. Clearing then would kill an animation that is
                        // legitimately playing.
                        if (string.IsNullOrEmpty(_data.ImagePath))
                        {
                            return;
                        }

                        XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
                    }));
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not release the cover animation");
            }
        }

        // Hands a video to the MediaElement and stands the Image down, so
        // exactly one renderer draws.
        private void ShowVideo(string path)
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;

            DisplayVideo.Source = new Uri(path);
            DisplayVideo.Visibility = Visibility.Visible;
            DisplayVideo.Play();
            _animating = true;
        }

        // Stop AND drop the source. Stop alone keeps the file open, and
        // rotation replaces these files underneath us. A recycled tile must
        // also not keep decoding the previous game's video.
        private void StopVideo()
        {
            if (DisplayVideo.Source == null && DisplayVideo.Visibility == Visibility.Collapsed)
            {
                return;
            }

            DisplayVideo.Stop();
            DisplayVideo.Source = null;
            DisplayVideo.Visibility = Visibility.Collapsed;
            _animating = false;
        }

        // Loop: artwork clips are short and meant to repeat, and MediaElement
        // has no repeat property of its own.
        private void DisplayVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                DisplayVideo.Position = TimeSpan.Zero;
                DisplayVideo.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not loop cover video");
            }
        }

        // Usually a missing codec - Windows ships no .webm filter. Fall back to
        // the theme's own artwork rather than a black rectangle.
        private void DisplayVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string path = DisplayVideo.Source?.LocalPath;

            if (!string.IsNullOrEmpty(path) && _loggedFailures.Add(path))
            {
                Logger.Warn($"ImageRotater: could not play cover video (missing codec?): {path}");
            }

            StopVideo();
            ShowNothing();
        }
    }
}
