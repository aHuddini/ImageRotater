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
    // Renders MOTION covers - video and GIF - where a theme places it.
    //
    // Still covers never come through here. Rotation writes Game.CoverImage,
    // Playnite notifies its own tile (Desktop always did; Fullscreen since
    // 10.57), and Playnite's PART_ImageCover draws the new picture, with the
    // transition drawn over it by CoverTileTransition. No plugin control, no
    // theme support, every theme, both modes. This control sits ABOVE that
    // tile and is transparent whenever the pick is a still, so the two never
    // disagree: there is only ever one picture of a still on screen.
    //
    // An earlier version drew stills here too, with its own crossfade layered
    // over Playnite's - two renderers for the same tile, each transitioning on
    // its own schedule. A recycled tile dissolved from the previous game's
    // cover; a selection announcement re-resolved the same path and left a
    // fade half-run; every landing flashed. All of it went with the still
    // path. What is left is the cost of a media pipeline inside a virtualised
    // grid: surviving tile recycling, fading video up only once its first
    // frame exists, animating the selected tile only.
    //
    // The pick is still published as a path on the DataContext, so a theme
    // that prefers to draw the still with its own element can bind
    // Content.ImagePath - the pattern Aniki uses for BackgroundChanger.
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

        // What a theme binds when it draws the still itself.
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

            // An unloaded tile must not keep a GIF decoding frames forever.
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);

            // Video is released LATER, and only if this really was a teardown.
            //
            // Selecting a tile unloads and immediately reloads it - the panel
            // re-measures and re-inserts its containers - so releasing here
            // killed the video of the tile the user had just selected. That is
            // the whole bug. Scrolling a tile out of view unloads it and it
            // does NOT come back, and that case still has to release the
            // decoder: a grid of retained decoders is what exhausts a 32-bit
            // process.
            //
            // Checked at Background priority, after layout has settled: by
            // then a recycle has reloaded the control and IsLoaded is true
            // again, while a genuine teardown is still unloaded.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!IsLoaded)
                    {
                        StopVideo();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // Playnite calls this when the tile is bound to a different game -
        // which for a virtualised grid is every time it scrolls into reuse.
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            // A cut, never a transition: this tile IS a different game now,
            // and Playnite's own tile cuts too. Tear the previous game's media
            // down before picking for the new one - a video-to-video recycle
            // otherwise assigns a new Source while the old media is still
            // open.
            ShowNothing();

            _previousPick = null;
            Refresh();
        }

        // A slideshow tick rotates artwork while the SAME game stays selected,
        // so Playnite raises no context change and nothing above would ever
        // tell this control to re-read. Static because the controls are built
        // by Playnite on demand and there is no reference to hand them: the
        // rotation service announces, whoever is alive listens.
        //
        // A MediaElement keeps PLAYING the previous pick, so without this the
        // slideshow would appear to do nothing at all while the file
        // underneath it changed.
        public static event Action<Guid> ArtworkRotated;

        public static void NotifyArtworkRotated(Guid gameId)
        {
            Action<Guid> handler = ArtworkRotated;

            if (handler == null)
            {
                return;
            }

            // Marshalled to the UI thread HERE rather than at each call site.
            // Every handler reads GameContext, which is a WPF DependencyProperty
            // and throws when touched from anywhere else. Fixing it at the
            // announcement means a future caller cannot reintroduce it by
            // forgetting to marshal.
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

        private void OnArtworkRotated(Guid gameId)
        {
            // Normally only this tile's own game - a grid raises this for one
            // game while dozens of controls listen.
            //
            // The exception is a tile that is currently ANIMATING but is no
            // longer the selected one. Selection moving away is announced
            // against the ARRIVING game, so the departing tile would never hear
            // it and would keep decoding frames forever.
            //
            // UNLESS unfocused tiles are allowed to animate - then a tile that
            // lost selection has nothing to stop.
            bool mine = GameContext != null && GameContext.Id == gameId;

            ImageRotaterSettings settings = _settings != null ? _settings() : null;

            bool mustStandDown = _animating && !IsSelectedTile
                && settings?.AnimateUnfocusedCovers != true;

            if (!mine && !mustStandDown)
            {
                return;
            }

            // Only a tile whose PICK actually changed forgets its history.
            //
            // Clearing this unconditionally was a regression: every wake -
            // including the selection announcement, which fires on every move -
            // wiped the avoid-previous memory, so in EverySelection mode tiles
            // re-rolled their artwork constantly. A stand-down tile is being
            // told to stop, not that its pick changed.
            if (mine)
            {
                _previousPick = null;
            }

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
                    // The common case: this game has no plugin cover. Playnite's
                    // own artwork shows through, and it is not an error.
                    ShowNothing();
                    return;
                }

                // The pick the ROTATION made, when there is one for this game.
                //
                // One source of truth for the pick: the rotation writes
                // Game.CoverImage, Playnite's tile draws that, and this control
                // must agree with it or a video plays over the poster of a
                // different picture. The published value names the pick and
                // the game it belongs to, set together by the publisher
                // precisely so the two cannot drift apart.
                string path = null;

                if (string.Equals(settings.CurrentCoverGameId, game.Id.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    path = settings.CurrentCoverPath;
                }

                // No published pick for this game - a tile scrolled past
                // without ever being selected, so rotation has not run for it.
                // Choosing here is the only way an unselected tile can animate.
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
                    // Every candidate is missing. Worth saying once per path;
                    // Playnite's tile shows what it has, and the Library page
                    // has the repair for it.
                    if (_previousPick != null && _loggedFailures.Add(_previousPick))
                    {
                        Logger.Warn($"ImageRotater: no usable cover image for \"{game.Name}\"");
                    }

                    ShowNothing();
                    return;
                }

                // A still is Playnite's to draw. Published for themes that
                // want it, and nothing rendered here.
                if (!PosterFrame.IsMotion(path))
                {
                    ShowNothing();
                    _data.ImagePath = path;
                    return;
                }

                // Moving artwork plays on the SELECTED tile only, unless the
                // user asks otherwise. An unselected tile shows Playnite's
                // poster frame of the same pick, which the publisher wrote for
                // exactly this.
                //
                // Default is selected-only because a grid realises a screenful
                // of these at once, and every animated one decodes continuously
                // on the UI thread in a 32-bit process - the same pressure that
                // took Playnite down when a theme put its own media element in
                // every tile. BackgroundChanger plays them everywhere and people
                // like it, so the restriction is a setting rather than a rule.
                if (!IsSelectedTile && !settings.AnimateUnfocusedCovers)
                {
                    ShowNothing();
                    return;
                }

                if (PosterFrame.IsVideo(path))
                {
                    ShowVideo(path);
                }
                else if (PosterFrame.IsAnimated(path))
                {
                    ShowGif(path);
                }
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

        // Transparent: Playnite's tile is what shows. Both media stop, so a
        // hidden tile is not decoding anything.
        private void ShowNothing()
        {
            StopVideo();
            ClearGif();
            _data.ImagePath = string.Empty;
        }

        // XamlAnimatedGif owns Image.Source while its attached property is
        // set; this is the one place that sets it.
        private void ShowGif(string path)
        {
            StopVideo();

            var uri = new Uri(path);

            // The same GIF asked for again - a selection announcement, a
            // background slideshow tick - keeps playing rather than restarting
            // from its first frame.
            if (_animating && DisplayImage.Visibility == Visibility.Visible &&
                uri.Equals(XamlAnimatedGif.AnimationBehavior.GetSourceUri(DisplayImage)))
            {
                return;
            }

            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, uri);
            DisplayImage.Visibility = Visibility.Visible;
            _animating = true;
        }

        private void ClearGif()
        {
            XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, null);
            DisplayImage.Visibility = Visibility.Collapsed;

            if (DisplayVideo.Visibility != Visibility.Visible)
            {
                _animating = false;
            }
        }

        // Hands a video to the MediaElement, so exactly one renderer draws.
        private void ShowVideo(string path)
        {
            ClearGif();

            var uri = new Uri(path);

            // Already playing this file: leave it be. WPF does not re-raise
            // MediaOpened for a Source assigned again, so restarting from
            // opacity 0 here left the video playing invisibly - hovering a tile
            // made the animation "disappear" while nothing had stopped it.
            if (DisplayVideo.Source != null &&
                DisplayVideo.Visibility == Visibility.Visible &&
                uri.Equals(DisplayVideo.Source))
            {
                return;
            }

            // Invisible until the first frame exists - MediaOpened fades it
            // up. A MediaElement renders nothing before its media opens, so at
            // full opacity the still-to-video switch was a hard cut through a
            // black rectangle. Playnite's poster shows through until then.
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 0.0;

            DisplayVideo.Source = uri;
            DisplayVideo.Visibility = Visibility.Visible;
            DisplayVideo.Play();
            _animating = true;

            // Backstop: if MediaOpened never arrives, nothing else would ever
            // make this visible.
            var reveal = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700)
            };

            reveal.Tick += (s, e) =>
            {
                reveal.Stop();

                if (DisplayVideo.Source != null &&
                    DisplayVideo.Visibility == Visibility.Visible &&
                    DisplayVideo.Opacity < 1.0)
                {
                    DisplayVideo.BeginAnimation(OpacityProperty, null);
                    DisplayVideo.Opacity = 1.0;
                }
            };

            reveal.Start();

            // Start somewhere other than the beginning.
            //
            // Every tile otherwise opens on the same first second of its clip,
            // and revisiting a game replays the same opening frames - which
            // reads as the video being stuck rather than looping. Applied once
            // the duration is known, since it is not available until the media
            // opens.
            _startAtRandomPoint = true;
        }

        // Restarts playback after the element is re-inserted into the tree.
        //
        // Selecting a Fullscreen tile calls Focus() then BringIntoView(),
        // which re-measures the tile panel; the panel removes and re-inserts
        // its containers, so this element is unloaded and loaded again. With
        // UnloadedBehavior=Manual the media survives that, but playback does
        // not resume on its own - and a Play() issued while the element was
        // detached is silently swallowed, which is exactly why the video
        // stopped the moment a tile became selected.
        private void DisplayVideo_Loaded(object sender, RoutedEventArgs e)
        {
            if (DisplayVideo.Source == null ||
                DisplayVideo.Visibility != Visibility.Visible)
            {
                return;
            }

            try
            {
                DisplayVideo.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not resume video after a reload");
            }
        }

        private void StopVideo()
        {
            if (DisplayVideo.Source == null && DisplayVideo.Visibility == Visibility.Collapsed)
            {
                return;
            }

            DisplayVideo.Stop();

            // Close() as well as Stop(), and the difference is not cosmetic.
            // Stop halts playback but leaves the media open; Close releases it
            // and the decoder behind it. In a virtualised grid with unfocused
            // covers animating, that is one held decoder per realised tile, in
            // a 32-bit process - the difference between "a screenful of videos"
            // and "every video the user has scrolled past this session".
            DisplayVideo.Close();

            DisplayVideo.Source = null;
            DisplayVideo.Visibility = Visibility.Collapsed;

            // Cleared, or a fade left mid-flight pins the next video at
            // whatever opacity this one died on.
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 1.0;

            if (DisplayImage.Visibility != Visibility.Visible)
            {
                _animating = false;
            }
        }

        // Set when playback starts, consumed when the media reports its length.
        private bool _startAtRandomPoint;

        // One generator for every control. Constructing Random per call seeds
        // from the clock, and a screenful of tiles opening in the same
        // millisecond would all pick the same "random" offset.
        private static readonly Random StartPoint = new Random();

        // Fades the video up now that its first frame exists, and seeks to a
        // random point once the duration is known. Skips the last quarter, or
        // a clip could open a moment before it loops - which looks like it
        // failed to play.
        private void DisplayVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            DisplayVideo.BeginAnimation(
                OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    0.0, 1.0, new Duration(Transition.Duration)));

            if (!_startAtRandomPoint)
            {
                return;
            }

            _startAtRandomPoint = false;

            try
            {
                if (!DisplayVideo.NaturalDuration.HasTimeSpan)
                {
                    return;
                }

                double seconds = DisplayVideo.NaturalDuration.TimeSpan.TotalSeconds;

                // Too short to be worth seeking into.
                if (seconds < 2.0)
                {
                    return;
                }

                double offset;
                lock (StartPoint)
                {
                    offset = StartPoint.NextDouble() * (seconds * 0.75);
                }

                DisplayVideo.Position = TimeSpan.FromSeconds(offset);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not set a random video start point");
            }
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
        // Playnite's poster rather than a black rectangle.
        private void DisplayVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string path = DisplayVideo.Source?.LocalPath;

            if (!string.IsNullOrEmpty(path) && _loggedFailures.Add(path))
            {
                Logger.Warn(
                    $"ImageRotater: cover video failed for {GameContext?.Name} (missing codec?): {path} - "
                    + (e.ErrorException == null ? "no detail" : e.ErrorException.Message));
            }

            ShowNothing();
        }
    }
}
