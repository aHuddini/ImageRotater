using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ImageRotater.Controls;
using ImageRotater.Models;
using ImageRotater.Services;

namespace ImageRotater
{
    public class ImageRotater : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // 150 MB of decoded bitmaps. Playnite is a 32-bit process sharing
        // ~2 GB of address space with Chromium, and decoded frames are large
        // contiguous allocations, so this budget is a fragmentation control as
        // much as a memory cap.
        private const long CacheBudgetBytes = 150L * 1024 * 1024;

        private readonly ImageRotaterSettingsViewModel _settingsViewModel;
        private readonly ImageCache _cache;
        private readonly ImageLoader _loader;
        private readonly ImageSelector _selector;
        private readonly SessionSelectionCache _sessionCache;
        private readonly GameImageStore _store;
        private readonly ImageMenuHandler _menuHandler;
        private readonly ISteamGridDbClient _steamGridDb;
        private readonly ArtworkDownloader _downloader;
        private readonly IBackgroundImageSource _imageSource;
        private readonly IBackgroundImageSource _coverSource;
        private readonly PlayniteBackgroundWriter _writer;
        private readonly OriginalArtPreserver _preserver;
        private readonly BackgroundRotationService _rotationService;
        private readonly FileLogger _fileLogger;
        private readonly ArtworkPublisher _publisher;
        private readonly FullscreenGridRefresher _gridRefresher;

        // Slideshow state: the game currently selected, and when each kind is
        // next due. One coarse timer serves both kinds - sub-second precision
        // is meaningless for something that ticks in tens of seconds.
        private System.Windows.Threading.DispatcherTimer _slideshowTimer;
        private Game _slideshowGame;

        // Bumped on every selection. A queued background write compares the
        // value it captured against this one to tell whether the user has moved
        // on again since - see PlayniteBackgroundWriter.SelectionGeneration.
        private int _selectionGeneration;
        private DateTime _backgroundDue = DateTime.MaxValue;
        private DateTime _coverDue = DateTime.MaxValue;

        // Below this, slideshow ticks become write churn - every tick imports
        // a file, updates the database and refreshes a tile.
        private const int MinimumSlideshowSeconds = 5;

        public override Guid Id { get; } = Guid.Parse("72b7d457-0621-429b-8368-665bc53ff896");

        // Public, not private, deliberately: this is the SettingsRoot that
        // theme {PluginSettings Plugin=ImageRotater, Path=...} bindings resolve
        // through (see AddSettingsSupport in the constructor), and WPF bindings
        // cannot read private members.
        public ImageRotaterSettings Settings => _settingsViewModel?.Settings;

        public ImageRotater(IPlayniteAPI api) : base(api)
        {
            _settingsViewModel = new ImageRotaterSettingsViewModel(this);

            _cache = new ImageCache(CacheBudgetBytes);
            _loader = new ImageLoader(_cache);
            _sessionCache = new SessionSelectionCache();
            _selector = new ImageSelector(new ImagePicker(), _sessionCache);

            _store = new GameImageStore(GetPluginUserDataPath());
            // The logger owns the enabled check, so no call site repeats it.
            _fileLogger = new FileLogger(
                GetPluginUserDataPath(), () => Settings?.EnableDebugLogging == true);

            // The key is read through an accessor: a settings save replaces the
            // whole settings object, so a captured string would go stale and the
            // user's newly entered key would appear not to work.
            _steamGridDb = new SteamGridDbClient(() => Settings?.SteamGridDbApiKey);
            _downloader = new ArtworkDownloader(_steamGridDb, _store, _sessionCache);

            // Plugin-owned images first, then Playnite's own background as a
            // fallback, deduplicated. Game.BackgroundImage is a database ID, so
            // GetFullFilePath turns it into a real path.
            //
            // Must be built before the rotation service, which captures it.
            // Candidates come from the plugin's own folder and NOWHERE else.
            //
            // Playnite's store must never be a source. In write mode it is our
            // output: each rotation imports a new copy and deletes the previous
            // one. Offering that copy back as a candidate meant a rotation could
            // pick the very file the next write was about to delete - valid when
            // chosen, gone by the time it was used. That is the blank/stretched
            // artwork, and no amount of File.Exists checking can fix it, because
            // the race is between our own read and our own delete.
            //
            // The user's pre-existing art is not lost by this: OriginalArtPreserver
            // copies it into our folder on first touch, so it rotates as an
            // ordinary candidate that we own and never delete.
            _imageSource = new FolderImageSource(_store, ArtworkKind.Background);
            _coverSource = new FolderImageSource(_store, ArtworkKind.Cover);

            _writer = new PlayniteBackgroundWriter(api, GetPluginUserDataPath());

            // A background write is queued to the UI thread, so it commits
            // after the selection that triggered it - and during fast scrolling
            // after the NEXT one too, which is what showed as another game's
            // background appearing under the selected one.
            //
            // The writer captures this when the write starts and asks again
            // when it commits. A background rotation is FOR the game being
            // left, so the departing game is never the current selection and an
            // id comparison would suppress every background write there is - a
            // counter is the only thing that distinguishes "one selection
            // behind", which is normal, from "two or more", which is the bug.
            _writer.SelectionGeneration = () => _selectionGeneration;
            // The writer is handed over so the preserver can recognise artwork
            // this plugin wrote and leave it alone.
            _preserver = new OriginalArtPreserver(api, _store, _writer);
            _publisher = new ArtworkPublisher(_store, _fileLogger);
            _gridRefresher = new FullscreenGridRefresher(_fileLogger);

            _rotationService = new BackgroundRotationService(
                _imageSource, _coverSource, _selector, _writer, _preserver, _store, () => Settings,
                _fileLogger, _publisher);

            // Adding or removing images changes what a game can rotate to, so
            // the rotation service must drop its once-per-game guard for it -
            // otherwise new artwork would not appear on the game currently
            // selected until the user navigated away and back.
            _menuHandler = new ImageMenuHandler(
                api, _store, _sessionCache, _steamGridDb, _downloader,
                gameId => _rotationService.Forget(gameId));

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            // Without this, {PluginSettings Plugin=ImageRotater, Path=...} in
            // theme XAML silently binds to nothing: Playnite resolves that
            // markup through a SettingsSupportList that only contains plugins
            // which registered here (verified in Playnite's own
            // Markup/PluginSettings.cs - unregistered plugins fall into the
            // "wait for ExtensionsLoaded" branch and never bind). Every
            // theme-side gate and cover binding depends on this call.
            AddSettingsSupport(new AddSettingsSupportArgs
            {
                SourceName = "ImageRotater",
                SettingsRoot = nameof(Settings)
            });

            // Publish where per-game artwork lives so theme XAML can join it
            // with a tile's own game id. This is what lets a grid work: every
            // other published value describes only the selected game.
            if (Settings != null)
            {
                Settings.ImagesRoot = _store.ImagesRoot;
            }

            // Themes place these as <ContentControl x:Name="ImageRotater_Background" />
            // and <ContentControl x:Name="ImageRotater_Cover" />.
            //
            // Cover is the only way a Fullscreen grid tile can rotate: those
            // tiles bind Playnite's native PART_ImageCover, which caches its
            // decoded bitmap and never re-reads Game.CoverImage.
            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = "ImageRotater",
                ElementList = new List<string> { "Background", "Cover" }
            });

            // Optional: also answer to the element names BackgroundChanger
            // themes already use, so such a theme works here unmodified.
            // Playnite routes a name to whichever plugin claimed it, so this
            // must stay opt-in - with both plugins enabled they would collide.
            if (Settings?.BackgroundChangerCompatibility == true)
            {
                AddCustomElementSupport(new AddCustomElementSupportArgs
                {
                    SourceName = "BackgroundChanger",
                    ElementList = new List<string> { "PluginBackgroundImage", "PluginCoverImage" }
                });
            }
        }

        // Write mode has no control to react to selection, so the plugin drives
        // it from the selection event instead.
        // B closes the search dialog.
        //
        // Playnite turns the rest of the pad into real key messages posted to
        // the active window - D-pad becomes the arrow keys, A becomes Enter -
        // so navigating and picking artwork needs no code from us at all. B is
        // the exception: Playnite maps it to nothing, and a source comment in
        // its own input handling concedes nobody remembers why. Without this a
        // controller user can open the dialog and then has no way out of it.
        //
        // Nothing happens when no dialog is open, so this cannot interfere with
        // B anywhere else in Playnite.
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            if (args?.Button == ControllerInput.B && args.State == ControllerInputState.Released)
            {
                Controls.SteamGridDbSearchView.CloseOpenDialog();
            }
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            Game selected = args?.NewValue?.FirstOrDefault();

            // Bumped FIRST, before any rotation runs.
            //
            // Backgrounds rotate for the game being LEFT, which pre-stages the
            // next image and avoids a flash mid-transition. The cost is that
            // the write is queued to the UI thread and commits after Playnite
            // has already moved on - and when the user scrolls quickly, after
            // they have moved on AGAIN. Playnite's background element then
            // renders a game two selections back. Observed in the log as:
            //
            //   12:22:07.345  selected "Persona 3 Reload"
            //   12:22:08.520  selected "Phantom Fury"
            //   12:22:08.538  Background: "Persona 3 Reload"   <- 18ms too late
            //
            // This counter is what lets such a write recognise it has been
            // overtaken and decline to commit.
            _selectionGeneration++;

            // Only the selected tile animates. Every other one showing motion
            // artwork renders its still frame instead - a screenful of tiles
            // each decoding frames on the UI thread, in a 32-bit process, is
            // the pressure that took Playnite down when a theme put its own
            // media element in every tile.
            CoverImageControl.NotifySelectionChanged(selected?.Id ?? Guid.Empty);

            if (selected != null)
            {
                // Tell themes whether this game has a plugin cover to show, so
                // a tile only collapses its native cover when we can replace
                // it. Without this, games the user never set up would render
                // blank.
                UpdateHasDataCover(selected);

                // Logged for EVERY selection, including ones that rotate
                // nothing - "the plugin never saw this game" and "it saw it
                // and declined" look identical otherwise. The IsEnabled guard
                // is not redundant: Log() gates internally, but the string
                // here is interpolated BEFORE the call, on every selection,
                // in the hottest path the plugin has.
                if (_fileLogger.IsEnabled)
                {
                    _fileLogger.Log(
                        $"selected \"{selected.Name}\" ({selected.Id}) hasDataCover={Settings?.HasDataCover}");
                }
            }

            // The two kinds rotate at different moments, deliberately.
            //
            // BACKGROUNDS rotate for the game being LEFT. Writing the arriving
            // game's background landed ~11ms after Playnite had started
            // rendering its stored image, so every switch painted it twice -
            // the second swap mid-transition was the crop/blur flash. Rotating
            // on the way out pre-stages the next image, and arrivals render
            // once.
            //
            // COVERS rotate for the game ARRIVED AT - the user wants to watch
            // the tile change, not discover later that it did. A cover swap is
            // not part of the switch transition, so it has no flash to cause;
            // it just needs the tile told to re-read, which is what the grid
            // refresher does. Playnite never notifies the property Fullscreen
            // tiles bind, and themes cannot refresh an items view themselves -
            // that takes a method call, and themes are XAML-only.
            Game left = args?.OldValue?.FirstOrDefault();
            if (left != null)
            {
                _rotationService.ApplyTo(left, ArtworkKind.Background);
            }

            if (selected != null)
            {
                _rotationService.ApplyTo(selected, ArtworkKind.Cover);

                // Only for a game the plugin actually has covers for.
                //
                // RefreshSoon calls UpdateTarget() on Playnite's OWN
                // PART_ImageCover binding, which re-resolves
                // FullscreenListItemCoverObject through its decoded-bitmap
                // cache. For a game we rotated, that re-read IS the feature -
                // it is the only way the tile ever sees a new cover.
                //
                // For a game the plugin has never touched it changes nothing
                // and costs a native image reload, with the target property
                // briefly between values - a black flash on a tile we had no
                // business touching. Guarding on RotateCovers alone fired this
                // on EVERY selection while browsing a library.
                //
                // HasDataCover is already computed for the selected game just
                // above, so this is free.
                if (Settings?.RotateCovers == true &&
                    Settings?.HasDataCover == true &&
                    PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                {
                    _gridRefresher.RefreshSoon(selected.Id);
                }
            }

            // Restart the slideshow clock for the new selection. Deliberately
            // reset on every selection change: a slideshow interval measures
            // "how long the user has been LOOKING at this game", not wall time.
            _slideshowGame = selected;
            ScheduleSlideshow();
        }

        // Arms the per-kind due times and makes sure the timer only runs when
        // there is something to wait for - an idle timer ticking forever for a
        // feature that is off would be pure waste.
        private void ScheduleSlideshow()
        {
            int bg = Settings?.BackgroundSlideshowSeconds ?? 0;
            int cover = Settings?.CoverSlideshowSeconds ?? 0;

            _backgroundDue = bg >= MinimumSlideshowSeconds && _slideshowGame != null
                ? DateTime.UtcNow.AddSeconds(bg)
                : DateTime.MaxValue;

            _coverDue = cover >= MinimumSlideshowSeconds && _slideshowGame != null
                ? DateTime.UtcNow.AddSeconds(cover)
                : DateTime.MaxValue;

            bool wanted = _backgroundDue != DateTime.MaxValue || _coverDue != DateTime.MaxValue;

            if (wanted && _slideshowTimer == null)
            {
                _slideshowTimer = new System.Windows.Threading.DispatcherTimer();
                _slideshowTimer.Tick += OnSlideshowTick;
            }

            if (_slideshowTimer != null)
            {
                _slideshowTimer.IsEnabled = wanted;
            }

            ArmTimerForNextDue();
        }

        // Wakes the timer when something is actually due, rather than once a
        // second to ask.
        //
        // A fixed one-second tick meant a 10-second interval fired somewhere in
        // [10.0, 11.0) - always late, never early, and visibly so at short
        // intervals. Sleeping until the nearest due time removes that bias and
        // stops the timer waking ~59 times out of 60 with nothing to do.
        //
        // Floored at a short minimum: a due time already in the past (the
        // window was unfocused, so ticks were skipped) must not ask the
        // dispatcher for a zero or negative interval.
        private void ArmTimerForNextDue()
        {
            if (_slideshowTimer == null || !_slideshowTimer.IsEnabled)
            {
                return;
            }

            // Away from the window, due times sit in the past and would ask for
            // the floor over and over. Poll slowly instead, purely to notice
            // that focus came back.
            if (_unfocused)
            {
                _slideshowTimer.Interval = UnfocusedPollInterval;
                return;
            }

            DateTime next = _backgroundDue < _coverDue ? _backgroundDue : _coverDue;

            if (next == DateTime.MaxValue)
            {
                return;
            }

            TimeSpan wait = next - DateTime.UtcNow;

            _slideshowTimer.Interval = wait > MinimumTimerInterval
                ? wait
                : MinimumTimerInterval;
        }

        // Short enough to be imperceptible, long enough that a due time in the
        // past cannot spin the dispatcher.
        private static readonly TimeSpan MinimumTimerInterval =
            TimeSpan.FromMilliseconds(50);

        // How often to check whether the window came back. Only latency on
        // resuming a slideshow nobody was watching, so a second is generous.
        private static readonly TimeSpan UnfocusedPollInterval =
            TimeSpan.FromSeconds(1);

        // True while the window is minimised or unfocused, so the timer polls
        // rather than chasing due times that are already in the past.
        private bool _unfocused;

        private void OnSlideshowTick(object sender, EventArgs e)
        {
            try
            {
                Game game = _slideshowGame;
                if (game == null)
                {
                    return;
                }

                // No slideshow for a window nobody is looking at. Rotating
                // while minimised or unfocused is file copies and database
                // writes for an invisible result; due times stay in the past,
                // so the next tick after focus returns rotates immediately.
                //
                // Polls slowly while away rather than re-arming to the due time
                // below: those due times are in the PAST, so the short floor
                // that keeps the timer honest for a real interval would instead
                // spin the dispatcher for as long as the window stayed
                // unfocused.
                Window main = Application.Current?.MainWindow;
                if (main == null || !main.IsActive ||
                    main.WindowState == WindowState.Minimized)
                {
                    _unfocused = true;
                    return;
                }

                _unfocused = false;

                DateTime now = DateTime.UtcNow;

                // Ticks are skipped outright for games with fewer than two
                // images of the kind: ApplyNext would re-pick the same file
                // and the write would be skipped, but the cover fade would
                // still run - a pulse to the same picture every N seconds.
                if (now >= _backgroundDue)
                {
                    if (_store.GetImagePaths(game.Id, ArtworkKind.Background).Count > 1)
                    {
                        // Playnite's own background element crossfades on
                        // source change, so this swap fades without any work
                        // from us.
                        _rotationService.ApplyNext(game, ArtworkKind.Background);

                        // The game has NOT changed, so no plugin control gets a
                        // context change - they would all keep showing the
                        // previous pick, and a video or GIF would keep playing
                        // it while the file underneath had already moved on.
                        CoverImageControl.NotifyArtworkRotated(game.Id);
                    }

                    _backgroundDue = now.AddSeconds(Settings.BackgroundSlideshowSeconds);
                }

                if (now >= _coverDue)
                {
                    if (_store.GetImagePaths(game.Id, ArtworkKind.Cover).Count > 1)
                    {
                        bool fullscreen =
                            PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen;

                        if (fullscreen)
                        {
                            // Fullscreen's missing notification is a gift
                            // here: the write can happen entirely BEFORE the
                            // fade, on a background thread, because nothing
                            // will snap the tile early. The fade window then
                            // contains only the binding re-read - the file
                            // copies and database write are already done. This
                            // is what removed the visible hitch between
                            // fade-out and fade-in.
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    _rotationService.ApplyNext(game, ArtworkKind.Cover);
                                    CoverImageControl.NotifyArtworkRotated(game.Id);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error(ex, "ImageRotater: slideshow rotation failed");
                                }
                            }).ContinueWith(
                                _ => _gridRefresher.AnimatedSwap(
                                    game.Id, () => { }, updateBinding: true),
                                System.Threading.Tasks.TaskScheduler.Default);
                        }
                        else
                        {
                            // Desktop notifies the tile the instant the
                            // database changes, so the write MUST stay inside
                            // the fade - done earlier, the tile snaps before
                            // any animation starts.
                            _gridRefresher.AnimatedSwap(
                                game.Id,
                                () =>
                                {
                                    _rotationService.ApplyNext(game, ArtworkKind.Cover);
                                    CoverImageControl.NotifyArtworkRotated(game.Id);
                                },
                                updateBinding: false);
                        }
                    }

                    _coverDue = now.AddSeconds(Settings.CoverSlideshowSeconds);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: slideshow tick failed");
            }
            finally
            {
                // Re-armed for whichever kind is due next, including after the
                // early returns above - an unfocused window skips its ticks, and
                // without this the timer would keep the interval it was last
                // given and drift.
                ArmTimerForNextDue();
            }
        }

        // Reads the store directly rather than the candidate source, for the
        // same reason HasPluginArtwork does: only the plugin's own folder
        // distinguishes a game the user set up from one they scrolled past.
        private void UpdateHasDataCover(Game game)
        {
            if (Settings == null)
            {
                return;
            }

            try
            {
                Settings.HasDataCover = _store.GetImagePaths(game.Id, ArtworkKind.Cover).Count > 0;
            }
            catch (Exception)
            {
                // An unreadable folder means we cannot promise a cover, so the
                // theme should keep showing its own.
                Settings.HasDataCover = false;
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            // Both plugins' element names arrive here. Playnite strips the
            // SourceName prefix, so a theme built for BackgroundChanger asks
            // for "PluginCoverImage" and one built for us asks for "Cover" -
            // the same control answers either.
            //
            // Settings are passed as an accessor, not a value: a settings save
            // replaces the whole object, so handing over the current one would
            // leave the control reading a stale copy forever.
            // Nothing may escape this method.
            //
            // Playnite builds these while laying out the library - once per
            // grid tile in Fullscreen - and an exception there is not caught
            // anywhere above us: the window goes black and the process exits
            // with no crash dialog and nothing in the log. Returning null
            // instead costs the plugin's artwork on that element and leaves
            // the theme's own rendering intact, which is always the better
            // failure.
            try
            {
                if (args.Name == "Background" || args.Name == "PluginBackgroundImage")
                {
                    return new BackgroundImageControl(_imageSource, _selector, _loader, () => Settings);
                }

                if (args.Name == "Cover" || args.Name == "PluginCoverImage")
                {
                    // No ImageLoader: this control publishes a path and the XAML
                // binds it with IsAsync=True, so decoding never touches the
                // layout thread.
                return new CoverImageControl(_coverSource, _selector, () => Settings);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"ImageRotater: could not create the \"{args.Name}\" view control");
            }

            return null;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            return GameMenuBuilder.Build(args.Games, _menuHandler);
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    MenuSection = "@ImageRotater",
                    Description = "Optimise stored images (reduce file sizes)",
                    Action = a => OptimiseStoredImages()
                },
                new MainMenuItem
                {
                    MenuSection = "@ImageRotater",
                    Description = "Restore original Playnite backgrounds",
                    Action = a => RestoreOriginalBackgrounds()
                }
            };
        }

        // Plumbing for whole-library image jobs: progress
        // dialog, off-thread work, and reporting the result back on the UI
        // thread.
        private void RunImageJob(
            string title,
            Func<ImageOptimizer, List<Guid>, Action<int, int>, ImageOptimizer.Result> job)
        {
            var optimizer = new ImageOptimizer(_store, _fileLogger);

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progress =>
                {
                    List<Guid> ids = PlayniteApi.Database.Games.Select(g => g.Id).ToList();
                    progress.ProgressMaxValue = ids.Count;

                    ImageOptimizer.Result result = job(
                        optimizer,
                        ids,
                        (done, total) =>
                        {
                            if (!progress.CancelToken.IsCancellationRequested)
                            {
                                progress.CurrentProgressValue = done;
                            }
                        });

                    PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        PlayniteApi.Dialogs.ShowMessage(result.Summary, "ImageRotater"));
                },
                new GlobalProgressOptions(title, true)
                {
                    IsIndeterminate = false
                });
        }

        // Re-encodes stored artwork to smaller files.
        //
        // Confirmed first, and the wording says "cannot be undone": this
        // replaces the stored files rather than copying, and a re-encode is a
        // small permanent quality loss. Preserved originals are affected too,
        // so a user who wants their downloaded art byte-identical should
        // decline.
        private void OptimiseStoredImages()
        {
            var confirm = PlayniteApi.Dialogs.ShowMessage(
                "Re-save stored artwork in a more compact form?\n\n"
                + "Large images are re-encoded to reduce their size. Files that "
                + "would not get meaningfully smaller are left alone, and images "
                + "with transparency are never touched.\n\n"
                + "This rewrites the stored files and cannot be undone.",
                "ImageRotater",
                System.Windows.MessageBoxButton.YesNo);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            RunImageJob(
                "ImageRotater: optimising images...",
                (optimizer, ids, report) => optimizer.OptimiseAll(ids, report));
        }

        // Undo for write mode. Write mode changes the user's library data, so
        // there has to be a way back that does not involve editing games by hand.
        private void RestoreOriginalBackgrounds()
        {
            int count = _writer.BackedUpCount;
            if (count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "ImageRotater has not changed any game backgrounds, so there is nothing to restore.",
                    "ImageRotater");
                return;
            }

            var confirm = PlayniteApi.Dialogs.ShowMessage(
                $"Restore the original background on {count} game(s)?\n\n" +
                "Artwork ImageRotater applied will be replaced by whatever each game had before.",
                "ImageRotater",
                System.Windows.MessageBoxButton.YesNo);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            int restored = _writer.RestoreAll();
            _rotationService.ForgetAll();

            PlayniteApi.Dialogs.ShowMessage(
                $"Restored {restored} game background(s).",
                "ImageRotater");
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new ImageRotaterSettingsView();
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Logger.Info($"ImageRotater loaded (mode: {PlayniteApi.ApplicationInfo.Mode})");

            _fileLogger.StartSession(
                typeof(ImageRotater).Assembly.GetName().Version.ToString(),
                PlayniteApi.ApplicationInfo.Mode.ToString(),
                Settings);

            // Before any tile renders. Themes load these with OnLoad, which
            // throws FileNotFoundException on a missing file - inside
            // FullscreenTilePanel.MeasureOverride, which is fatal. Seeding
            // guarantees the file exists for every game that has artwork, so
            // the throw is impossible rather than merely unlikely.
            try
            {
                _publisher.SeedEveryGame(PlayniteApi.Database.Games);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ImageRotater: could not seed published artwork files");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            // Stop the tick, drop the handler, forget the game. The timer dies
            // with the dispatcher anyway, but an armed timer during shutdown
            // can fire into half-disposed state, and leaving it is exactly the
            // "properly stop and dispose timers" class of leak.
            if (_slideshowTimer != null)
            {
                _slideshowTimer.IsEnabled = false;
                _slideshowTimer.Tick -= OnSlideshowTick;
                _slideshowTimer = null;
            }

            _slideshowGame = null;
        }
    }
}
