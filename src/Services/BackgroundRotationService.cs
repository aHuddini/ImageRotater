using System;
using System.Collections.Generic;
using System.IO;
using Playnite.SDK;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Drives write mode: picks an image for a game and pushes it into
    // Playnite's own BackgroundImage field.
    //
    // The theme-element mode does its own picking inside the control. This
    // service is the equivalent for themes that never place that element -
    // which is most of them.
    public class BackgroundRotationService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IBackgroundImageSource _source;
        private readonly IBackgroundImageSource _coverSource;
        private readonly ImageSelector _selector;
        private readonly PlayniteBackgroundWriter _writer;
        private readonly OriginalArtPreserver _preserver;
        private readonly GameImageStore _store;
        private readonly Func<ImageRotaterSettings> _settings;

        // Optional: absent in tests, present at runtime when debug logging is on.
        private readonly FileLogger _fileLogger;

        // Makes a pick reachable from theme XAML. Separate because rotation
        // decides WHICH image a game shows, while publishing decides how a
        // theme gets at it - they change for unrelated reasons.
        private readonly ArtworkPublisher _publisher;

        // Last path written per game and kind, so an unchanged pick does not
        // cause a pointless database write on every selection. May hold a
        // letterboxed composite rather than a source image.
        private readonly Dictionary<string, string> _lastWritten = new Dictionary<string, string>();

        // Last SOURCE image picked per game and kind - what the selector's
        // avoid-previous compares against. Kept separately from _lastWritten
        // because letterboxing swaps the written path for a composite, which
        // never matches any candidate and would quietly disable
        // repeat-avoidance for exactly the games that letterbox.
        private readonly Dictionary<string, string> _lastPicked = new Dictionary<string, string>();

        // The game each kind last rotated. Fullscreen re-raises the selection
        // event for the game already selected - on view changes and focus
        // shifts, not just navigation - so without this, EverySelection
        // re-picked several times while the user sat on one game.
        //
        // Per kind, because the two kinds now rotate at different moments:
        // backgrounds when a game is LEFT (a mid-transition background write
        // is the visible flash), covers when a game is ARRIVED AT (the user
        // wants to see the cover change, and the grid refresher makes the
        // tile re-read on demand). One shared guard would let either kind
        // suppress the other.
        private readonly Dictionary<ArtworkKind, Guid> _lastApplied =
            new Dictionary<ArtworkKind, Guid>();

        public BackgroundRotationService(
            IBackgroundImageSource source,
            IBackgroundImageSource coverSource,
            ImageSelector selector,
            PlayniteBackgroundWriter writer,
            OriginalArtPreserver preserver,
            GameImageStore store,
            Func<ImageRotaterSettings> settings,
            FileLogger fileLogger = null,
            ArtworkPublisher publisher = null)
        {
            _fileLogger = fileLogger;

            // Never null: publishing is how a pick reaches a theme, so a
            // missing publisher would silently disable the feature rather than
            // fail. The parameter exists so callers can supply one wired to a
            // logger, not so they can opt out.
            _publisher = publisher ?? new ArtworkPublisher(store, fileLogger);

            _source = source;
            _coverSource = coverSource;
            _selector = selector;
            _writer = writer;
            _preserver = preserver;
            _store = store;
            _settings = settings;

        }

        // Called when the selected game changes, and once per game at startup.
        public void ApplyTo(Game game)
        {
            if (game == null)
            {
                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation)
            {
                return;
            }

            // DisplayMode deliberately does not gate this: backgrounds always
            // write Game.BackgroundImage, and a theme element simply draws
            // over the top.
            ApplyTo(game, ArtworkKind.Background, settings);
            ApplyTo(game, ArtworkKind.Cover, settings);
        }

        // Rotates one kind for one game. The two kinds are driven at different
        // moments - backgrounds when a game is LEFT (a mid-transition
        // background write is the visible flash on switches), covers when a
        // game is ARRIVED AT (the user wants to watch the cover change, and
        // the grid refresher makes the tile re-read on demand) - so each must
        // be callable on its own.
        public void ApplyTo(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation)
            {
                return;
            }

            ApplyTo(game, kind, settings);
        }

        // Rotates one kind for one game RIGHT NOW, ignoring the repeat guard
        // and the configured selection mode.
        //
        // This is the slideshow tick: the game has stayed selected, the timer
        // elapsed, and the whole point is a new image - so Session mode's
        // "keep this pick" and the guard's "already did this game" must both
        // be overridden. EverySelection semantics give the avoid-previous
        // behaviour a slideshow wants.
        //
        // Ceiling: in the theme-element display mode with Session selection, a
        // theme-hosted control keeps its own remembered pick and will not
        // follow slideshow swaps. The write path - which is what nearly every
        // setup renders - follows them everywhere.
        public void ApplyNext(Game game, ArtworkKind kind)
        {
            if (game == null)
            {
                return;
            }

            ImageRotaterSettings settings = _settings != null ? _settings() : null;
            if (settings == null || !settings.EnableRotation)
            {
                return;
            }

            if (kind == ArtworkKind.Cover && !settings.RotateCovers)
            {
                return;
            }

            _lastApplied[kind] = game.Id;

            Apply(game, kind, kind == ArtworkKind.Cover ? _coverSource : _source, settings,
                SelectionMode.EverySelection);
        }

        private void ApplyTo(Game game, ArtworkKind kind, ImageRotaterSettings settings)
        {
            if (kind == ArtworkKind.Cover && !settings.RotateCovers)
            {
                return;
            }

            // Already rotated this kind for this game. Selection events repeat
            // for the game already selected - Fullscreen re-raises them on
            // view changes and focus shifts - and re-picking on those produced
            // several different images per game per session.
            Guid last;
            if (_lastApplied.TryGetValue(kind, out last) && last == game.Id)
            {
                return;
            }

            _lastApplied[kind] = game.Id;

            // Covers are ALWAYS written, even when a theme hosts the cover
            // control. The two are not alternatives: Game.CoverImage feeds
            // every view, while the control renders only where a theme places
            // it. The control picking independently for its own tile is a
            // second pick, not a conflicting one - nothing else draws from it.
            Apply(game, kind, kind == ArtworkKind.Cover ? _coverSource : _source, settings);
        }

        private void Apply(
            Game game,
            ArtworkKind kind,
            IBackgroundImageSource source,
            ImageRotaterSettings settings,
            SelectionMode? modeOverride = null)
        {
            if (source == null)
            {
                return;
            }

            // Do nothing at all unless the user has given this game artwork of
            // this kind. Merely browsing past a game must not cause the plugin
            // to touch it.
            //
            // This check cannot use the merged candidate list: that includes
            // Playnite's own existing image, so every game with any artwork
            // looks like it has candidates. Only the plugin's own folder
            // distinguishes a game the user set up from one they scrolled past.
            if (!HasPluginArtwork(game, kind))
            {
                return;
            }

            // Copy the game's pre-existing artwork into our folder before it is
            // replaced. It then rotates like any other candidate, and survives
            // even if Playnite later reclaims the now-unreferenced original.
            // Only reached for games the user opted in above.
            _preserver?.Preserve(game, kind);

            IReadOnlyList<string> candidates = source.GetImagePaths(game);
            if (candidates == null || candidates.Count == 0)
            {
                // No plugin-owned art of this kind. Leave Playnite's own value
                // alone rather than blanking it.
                return;
            }

            // Session-mode memory is per kind, so a game's cover and background
            // are chosen and remembered independently.
            Guid selectionKey = SelectionKey(game.Id, kind);

            string path = _selector.Select(
                selectionKey, candidates, PreviousFor(game.Id, kind),
                modeOverride
                    ?? (kind == ArtworkKind.Cover ? settings.CoverSelectionMode : settings.SelectionMode));

            // Fall back rather than showing nothing. A pick can go stale between
            // being chosen and being written - most often because the previous
            // rotation's own image lives in Playnite's library store and gets
            // deleted when it is replaced. Trying the remaining candidates means
            // a single unusable file costs one retry instead of an empty
            // background.
            if (!IsUsable(path))
            {
                path = FirstUsable(candidates, path);
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // Recorded before letterboxing swaps the path, so avoid-previous
            // keeps comparing source images against source images.
            _lastPicked[WrittenKey(game.Id, kind)] = path;

            // The pick exactly as chosen, before the still substitution below.
            //
            // Two consumers want different things from one pick. Playnite's
            // database needs a STILL: it decodes the stored value to a single
            // bitmap, so a moving file would show one frame while importing
            // megabytes per rotation. A THEME renders the published file
            // itself, and the plugin's controls animate it, so it needs the
            // real thing. Substituting for both meant motion could never reach
            // a theme at all.
            string picked = path;

            if (PosterFrame.IsMotion(path))
            {
                // Null for video - GDI+ cannot decode a container - and null
                // for a GIF whose first frame would not extract.
                string still = PosterFrame.For(path);

                if (still == null)
                {
                    still = FirstStillCandidate(candidates, path);

                    if (string.IsNullOrEmpty(still))
                    {
                        // Nothing static to write. The publish below is what a
                        // theme needs and it has not happened yet, so publish
                        // the motion pick before leaving rather than dropping
                        // the rotation entirely - a game whose only artwork is
                        // a video would otherwise never reach a theme.
                        _publisher?.Publish(game, picked, kind, settings);
                        return;
                    }

                    // Why the two motion kinds part company here.
                    //
                    // A VIDEO has no poster by nature, not by failure - GDI+
                    // cannot decode a container. The file is fine and the
                    // controls play it, so the still is a stand-in for the
                    // DATABASE only and "picked" keeps the video. Publishing
                    // the stand-in instead is precisely the bug that kept
                    // animated artwork off themes.
                    //
                    // A GIF that would not extract is genuinely broken: GDI+
                    // does read GIFs, so a failure means the file is corrupt.
                    // Handing that to a theme just moves the failure, so the
                    // replacement becomes the real pick.
                    if (!PosterFrame.IsVideo(path))
                    {
                        picked = still;
                    }
                }

                path = still;
            }

            // The rescue for picks the shape bias could not steer: a game with
            // only odd-shaped images gets its pick letterboxed into a
            // screen-shaped composite, so the stored value never triggers
            // Playnite's visible re-fit on the next switch. Returns the
            // original path untouched when it is already screen-shaped or the
            // compose fails.
            if (kind == ArtworkKind.Background && settings.LetterboxBackgrounds)
            {
                path = Letterboxer.For(path, ShapeBias.ScreenAspect);
            }

            // Publish the chosen cover for themes to bind directly.
            //
            // Set before the write-skip below, not after: when the pick is
            // unchanged we skip the database write, but the theme still needs
            // the current value - and on the first selection after startup the
            // skip would otherwise leave this empty.
            // Hand the pick to whatever makes it reachable from a theme.
            // Rotation decides WHICH image; the publisher decides how a theme
            // gets at it.
            //
            // Motion publishes the REAL file - a theme renders the published
            // copy itself, so an animated pick must arrive animated, not as
            // the poster substituted for Playnite's database.
            //
            // A STILL publishes the transformed path instead. It used to
            // publish the raw pick here too, which quietly meant letterboxing
            // never reached a theme at all: Fullscreen themes bind the
            // published tile, so the option visibly did nothing in Fullscreen
            // while Desktop (which renders the database value) obeyed it.
            _publisher?.Publish(
                game,
                PosterFrame.IsMotion(picked) ? picked : path,
                kind,
                settings);

            string previous;
            if (_lastWritten.TryGetValue(WrittenKey(game.Id, kind), out previous) &&
                string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
            {
                // Same image as last time - nothing to write. This is the common
                // case in Session mode and avoids a database update per
                // selection.
                return;
            }

            if (_writer.SetArtwork(game, path, kind))
            {
                _lastWritten[WrittenKey(game.Id, kind)] = path;
                ImageDiagnostics.LogApplied(game.Name, path, _settings, 0, 0, kind);
            }
        }

        // The session cache is keyed by Guid, so covers need a key distinct from
        // the game's own id or the two kinds would overwrite each other's
        // remembered choice. Deriving it from the id keeps that mapping stable
        // across restarts without a second cache.
        private static Guid SelectionKey(Guid gameId, ArtworkKind kind)
        {
            if (kind != ArtworkKind.Cover)
            {
                return gameId;
            }

            byte[] bytes = gameId.ToByteArray();
            bytes[0] ^= 0xC0;
            return new Guid(bytes);
        }

        private static string WrittenKey(Guid gameId, ArtworkKind kind)
        {
            return kind == ArtworkKind.Cover ? "cover:" + gameId : gameId.ToString();
        }

        // True only when the user has put artwork of this kind in the plugin's
        // own folder for this game - by adding files, downloading, or having
        // had a previous rotation preserve their original.
        //
        // Deliberately reads the store directly rather than the merged source,
        // because the merged source also offers Playnite's existing image and
        // would therefore report "yes" for every game in the library.
        private bool HasPluginArtwork(Game game, ArtworkKind kind)
        {
            if (_store == null)
            {
                // No store to consult - treat as not opted in rather than
                // touching games we cannot verify.
                return false;
            }

            try
            {
                return _store.GetImagePaths(game.Id, kind).Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsUsable(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch (Exception)
            {
                // A malformed path is unusable, not a reason to stop rendering.
                return false;
            }
        }

        // First candidate that still exists, skipping the one already rejected.
        private static string FirstUsable(IReadOnlyList<string> candidates, string skip)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];

                if (string.Equals(candidate, skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsUsable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        // A candidate the database write can actually store: on disk, and not
        // itself moving. Falling back from an unstillable pick to another GIF
        // or a video would just repeat the same failure one candidate later.
        private static string FirstStillCandidate(IReadOnlyList<string> candidates, string skip)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                string candidate = candidates[i];

                if (string.Equals(candidate, skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!PosterFrame.IsMotion(candidate) && IsUsable(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        // Reads the SOURCE pick, not the written path: letterboxing writes a
        // composite whose path matches no candidate, and avoid-previous only
        // works when it compares like with like.
        private string PreviousFor(Guid gameId, ArtworkKind kind)
        {
            string previous;
            return _lastPicked.TryGetValue(WrittenKey(gameId, kind), out previous) ? previous : null;
        }

        // Releases the repeat guard for one game, so the next selection event
        // rotates it again.
        //
        // Needed because adding or removing images changes the candidate list.
        // Without this, doing that to the game currently selected would leave
        // the guard set, and the new artwork would not appear until the user
        // selected another game and came back.
        public void Forget(Guid gameId)
        {
            foreach (ArtworkKind kind in new[] { ArtworkKind.Background, ArtworkKind.Cover })
            {
                Guid last;
                if (_lastApplied.TryGetValue(kind, out last) && last == gameId)
                {
                    _lastApplied.Remove(kind);
                }
            }
        }

        // After a restore, the plugin no longer owns any game's background.
        public void ForgetAll()
        {
            _lastWritten.Clear();
            _lastPicked.Clear();

            // Also clear the guards, or re-selecting the game that was showing
            // when the restore ran would be treated as a repeat and skipped -
            // leaving it on its restored artwork until the user selected
            // something else and came back.
            _lastApplied.Clear();
        }
    }
}
