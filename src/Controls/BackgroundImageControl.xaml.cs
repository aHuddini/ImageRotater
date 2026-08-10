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
        // Kept as a constructor parameter but no longer used: still picks now
        // defer to Playnite's own background element, so this control never
        // decodes a bitmap. The parameter stays so the plugin's construction
        // call does not have to change shape for a dependency that may return
        // if the still path ever comes back.

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
            // Release the previous game's video before choosing for the new
            // one - see the note on the cover control's equivalent. A
            // video-to-video switch otherwise assigns a new Source with the
            // previous media still open.
            StopVideo();

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

        // Synchronous now that stills defer to Playnite's own background.
        //
        // This used to decode a bitmap here, which is why it was async void -
        // that decode is gone with the still branch, and the only work left is
        // handing a path to XamlAnimatedGif or a MediaElement, both of which
        // load on their own.
        //
        // The catch-all stays regardless: an exception escaping into Playnite's
        // layout pass is not caught above us and takes the process down.
        private void Refresh()
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
                if (game == null || _source == null || _selector == null)
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
                    // Refresh is async void and re-entrant, so a newer call may
                    // already have rendered while this one was between awaits.
                    // Without this check it would put a departed game's artwork
                    // back over the current one.
                    if (token != _requestToken)
                    {
                        return;
                    }

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
                    // Same reason as the video branch above.
                    if (token != _requestToken)
                    {
                        return;
                    }

                    StopVideo();

                    DisplayImage.Source = null;
                    XamlAnimatedGif.AnimationBehavior.SetSourceUri(DisplayImage, new Uri(path));

                    DisplayImage.Visibility = Visibility.Visible;
                    MissingImagePlaceholder.Visibility = Visibility.Collapsed;

                    ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
                    return;
                }

                // A STILL pick is left to Playnite.
                //
                // Backgrounds always write Game.BackgroundImage as well, and a
                // theme renders that through a FadeImage which crossfades a
                // source change for free. Drawing the same picture again here
                // just covers that up with an opaque layer that swaps
                // instantly - which is exactly what happened when this control
                // was added for animated artwork: the fade users had before did
                // not break, it was hidden.
                //
                // So this control renders only what the write path CANNOT: GIFs
                // and video, handled above. For everything else it stands down
                // and lets the theme's own transition through.
                //
                // Three attempts at reimplementing that fade here all traded
                // one artefact for another. There is nothing to reimplement.
                //
                // Faded out rather than cut, when a video was on screen: the
                // still underneath arrives through Playnite's own crossfade,
                // and this layer vanishing in one frame over that dissolve was
                // the one hard edge left in the transition.
                FadeOutVideoThenStop();
                ShowNothing();
                ImageDiagnostics.LogApplied(game.Name, path, _settings, bucket, 0);
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
        private void ShowNothing()
        {
            ClearImage();
            DisplayImage.Visibility = Visibility.Collapsed;
            MissingImagePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void ShowPlaceholder()
        {
            ClearImage();
            DisplayImage.Visibility = Visibility.Collapsed;
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

            // Starts invisible and fades up once the first frame exists (see
            // MediaOpened). A MediaElement renders nothing until then, so
            // showing it at full opacity meant a black rectangle covering
            // whatever Playnite was still displaying - and the still-to-video
            // switch read as a hard cut through black.
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 0;

            DisplayVideo.Source = new Uri(path);
            DisplayVideo.Visibility = Visibility.Visible;
            DisplayVideo.Play();
        }

        // Dissolves the video away over the still underneath, then releases
        // it. Playnite crossfades the incoming still at about this rate, so
        // the two fades read as one transition.
        private void FadeOutVideoThenStop()
        {
            if (DisplayVideo.Source == null || DisplayVideo.Visibility != Visibility.Visible)
            {
                return;
            }

            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                DisplayVideo.Opacity, 0.0,
                new Duration(TimeSpan.FromMilliseconds(400)));

            // Guarded by the request token: if another refresh started a NEW
            // video while this fade ran, tearing the element down now would
            // kill the wrong playback.
            int token = _requestToken;

            fade.Completed += (s, e) =>
            {
                if (token == _requestToken)
                {
                    StopVideo();
                }
            };

            DisplayVideo.BeginAnimation(OpacityProperty, fade);
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

            // Close as well as Stop: Stop halts playback but leaves the media
            // and its decoder open. Only one background renders at a time, so
            // this matters far less here than in a grid of covers - but a
            // decoder held for the life of the session is still a decoder held
            // for the life of the session.
            DisplayVideo.Close();

            DisplayVideo.Source = null;
            DisplayVideo.Visibility = Visibility.Collapsed;

            // Cleared, or a fade left mid-flight would pin the next video at
            // whatever opacity this one died on.
            DisplayVideo.BeginAnimation(OpacityProperty, null);
            DisplayVideo.Opacity = 1.0;
        }

        // Loop. Artwork clips are short and meant to repeat; MediaElement has
        // no repeat property, so the end of one playback seeds the next.
        // The first frame exists now, so the fade-up can start without ever
        // showing the pre-roll black a MediaElement renders before its media
        // opens.
        private void DisplayVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(400)));

            DisplayVideo.BeginAnimation(OpacityProperty, fade);
        }

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
