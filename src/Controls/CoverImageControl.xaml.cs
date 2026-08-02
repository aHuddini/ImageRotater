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

            if (handler == null)
            {
                return;
            }

            // Marshalled to the UI thread HERE rather than at each call site.
            //
            // The Fullscreen slideshow raises this from inside a Task.Run - the
            // cover write is done off-thread deliberately, so the fade window
            // contains only the binding re-read. Every handler then reads
            // GameContext, which is a WPF DependencyProperty and throws
            // "The calling thread cannot access this object because a different
            // thread owns it" when touched from anywhere else.
            //
            // Fixing it at the announcement means a future caller cannot
            // reintroduce it by forgetting to marshal.
            Application app = Application.Current;

            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => handler(gameId)));
                return;
            }

            handler(gameId);
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
            //
            // Routed through NotifyArtworkRotated rather than raising the event
            // directly, so this cannot bypass the UI-thread marshalling that
            // lives there.
            NotifyArtworkRotated(gameId);
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

            StagePreviousCover();
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

                // The pick the ROTATION made, when there is one for this game.
                //
                // Choosing again here meant two independent rolls for the same
                // tile: the rotation writes Game.CoverImage and the grid
                // refresher makes Playnite's own PART_ImageCover re-read it,
                // while this control selected separately and drew on top. Two
                // different covers for one game, updating at different moments -
                // which is the image seen flipping back and forth.
                //
                // The published value names the pick and the game it belongs
                // to, set together by the publisher precisely so the two cannot
                // drift apart.
                string path = null;

                if (string.Equals(settings.CurrentCoverGameId, game.Id.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = settings.CurrentCoverPath;
                }

                // No published pick for this game - a tile scrolled past
                // without ever being selected, so rotation has not run for it.
                // Choosing here is then the only way it shows anything.
                if (string.IsNullOrEmpty(path))
                {
                    path = _selector.Select(
                        game.Id, candidates, _previousPick, settings.CoverSelectionMode);
                }

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
                    StagePreviousCover();
                    _data.ImagePath = path;
                    _animating = false;

                    // The animation is released by the same TargetUpdated
                    // handler that runs the crossfade, so there is no separate
                    // deferral to get right.
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

        private void ShowNothing()
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            ClearPreviousCover();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void ShowPlaceholder()
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            StopVideo();
            ClearPreviousCover();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Visible;
        }

        // Parks the cover currently on screen on the layer underneath, so the
        // incoming one has something to dissolve FROM.
        //
        // Called before the bound path changes. A Fullscreen tile's own cover
        // is a plain Image rather than a FadeImage, so unlike backgrounds there
        // is no theme transition to defer to - the plugin has to do this or the
        // switch is a hard cut.
        private void StagePreviousCover()
        {
            try
            {
                if (DisplayImage.Source == null || DisplayImage.Visibility != Visibility.Visible)
                {
                    ClearPreviousCover();
                    return;
                }

                PreviousImage.Source = DisplayImage.Source;
                PreviousImage.Opacity = 1.0;
                PreviousImage.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not stage the previous cover");
            }
        }

        // Runs when the async binding actually delivers the new cover.
        //
        // This is the only moment the crossfade can start. The binding is
        // asynchronous, so at the point the path was set the picture did not
        // exist yet - an earlier version queued a dispatcher callback and hoped
        // it landed afterwards, which is guesswork this event replaces.
        private void DisplayImage_TargetUpdated(
            object sender, System.Windows.Data.DataTransferEventArgs e)
        {
            // The GIF behaviour owns Image.Source while attached, and a still
            // arriving means it is time to let go.
            if (!string.IsNullOrEmpty(_data.ImagePath))
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            }

            CrossfadePreviousCover();
        }

        // Dissolves the outgoing layer away, revealing the cover already opaque
        // beneath it.
        //
        // The OLD layer fades, not the new one: the incoming cover is fully
        // drawn underneath from the first frame, so nothing behind the control
        // is ever visible through the transition. Fading the new one up would
        // show the tile's own artwork through the gap.
        private void CrossfadePreviousCover()
        {
            try
            {
                if (PreviousImage.Source == null ||
                    PreviousImage.Visibility != Visibility.Visible)
                {
                    return;
                }

                var fade = new System.Windows.Media.Animation.DoubleAnimation(
                    1.0, 0.0, new Duration(CoverFadeDuration));

                // Completed fires even for a REPLACED animation, so without a
                // generation an older fade tears down the layer a newer one is
                // still using.
                int generation = ++_fadeGeneration;

                fade.Completed += (s, e) =>
                {
                    if (generation == _fadeGeneration)
                    {
                        ClearPreviousCover();
                    }
                };

                PreviousImage.BeginAnimation(OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                ClearPreviousCover();
                Logger.Warn(ex, "ImageRotater: could not crossfade the cover");
            }
        }

        private void ClearPreviousCover()
        {
            PreviousImage.BeginAnimation(OpacityProperty, null);
            PreviousImage.Opacity = 1.0;
            PreviousImage.Visibility = Visibility.Collapsed;
            PreviousImage.Source = null;
        }

        private int _fadeGeneration;

        // Matched to what Playnite's own FadeImage uses for backgrounds, so a
        // cover and a background switch at the same pace.
        private static readonly TimeSpan CoverFadeDuration =
            TimeSpan.FromMilliseconds(300);

        // Hands a video to the MediaElement and stands the Image down, so
        // exactly one renderer draws.
        private void ShowVideo(string path)
        {
            _data.ImagePath = string.Empty;
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;

            ClearPreviousCover();

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
