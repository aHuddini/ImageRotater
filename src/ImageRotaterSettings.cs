using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Playnite.SDK;

namespace ImageRotater
{
    // How the chosen image reaches the screen.
    public enum DisplayMode
    {
        // Write into Playnite's own Game.BackgroundImage. Works in every theme
        // with no theme support, because every theme already renders that field.
        UpdatePlayniteBackground,

        // Render through the plugin's own control. Higher quality - decode
        // sizing, caching - but only appears where a theme places
        // <ContentControl x:Name="ImageRotater_Background" />.
        ThemeElement
    }

    // When a game has several images, this decides how the shown one is chosen.
    public enum SelectionMode
    {
        Session,        // Pick once per Playnite session; stays put while browsing
        EverySelection, // Re-pick each time the game is selected
        Fixed           // Always the first image, alphabetically
    }

    public class ImageRotaterSettings : ObservableObject
    {
        private bool enableRotation = true;     // Master switch for the whole feature
        private bool enableDebugLogging = false; // Verbose log to ImageRotater.log
        private SelectionMode selectionMode = SelectionMode.Session;
        private DisplayMode displayMode = DisplayMode.UpdatePlayniteBackground;
        private bool rotateCovers = false;

        // Off by default: covers are box art the user has usually curated
        // deliberately, so replacing them is a bigger intrusion than swapping a
        // background and should be opted into.
        public bool RotateCovers
        {
            get => rotateCovers;
            set
            {
                rotateCovers = value;
                OnPropertyChanged();

                // EnableCoverImage is derived from this. Themes bind it to
                // decide whether to collapse their native cover, so without
                // this notification the tile would keep hiding (or showing)
                // the wrong element until something else refreshed it.
                OnPropertyChanged(nameof(EnableCoverImage));
            }
        }
        // On by default: it is the only way Fullscreen grid tiles can rotate,
        // and it changes nothing in themes that do not place our element.
        private bool useCoverControl = true;

        private bool hasDataCover = false;

        private string currentCoverPath = string.Empty;

        private string currentCoverGameId = string.Empty;
        private bool currentCoverIsVideo;

        private string imagesRoot = string.Empty;

        // On by default: it only changes anything for games that HAVE
        // screen-shaped images, and there it prevents a visible flash.
        // Off by default: a slideshow is a taste, and it costs a write (plus a
        // tile refresh for covers) per tick.
        private int backgroundSlideshowSeconds = 0;
        private int coverSlideshowSeconds = 0;

        // 300ms matches what Playnite's own FadeImage does, so a plugin
        // background and a theme's own switch at the same pace.
        private int backgroundFadeMilliseconds = 300;

        // Session by default: a new cover per Playnite launch. This is the
        // behaviour users asked for by name, and constant cover reshuffling
        // makes a grid read as noise.
        private SelectionMode coverSelectionMode = SelectionMode.Session;

        // On by default: it only activates for picks that would otherwise
        // trigger the visible re-fit, and the fill is a soft wash of the image
        // itself rather than bars. Acquiring screen-shaped art in the first
        // place is still the better path - the shape bias steers rotation
        // there whenever a game has any.
        private bool letterboxBackgrounds = true;

        private bool backgroundChangerCompatibility = true;
        private string steamGridDbApiKey = string.Empty;

        public DisplayMode DisplayMode
        {
            get => displayMode;
            set { displayMode = value; OnPropertyChanged(); }
        }

        // Also answer to the element names BackgroundChanger themes already use,
        // so a theme built for that plugin works here with no edits.
        //
        // On by default: the two plugins cannot run together anyway (Playnite
        // routes an element name to whichever plugin claimed it, so with both
        // enabled the winner depends on load order), and the documented
        // requirement is to disable BackgroundChanger first. The toggle stays
        // so a user comparing the two can turn this off temporarily.
        public bool BackgroundChangerCompatibility
        {
            get => backgroundChangerCompatibility;
            set { backgroundChangerCompatibility = value; OnPropertyChanged(); }
        }

        // Renders covers through the plugin's own control instead of writing
        // Game.CoverImage.
        //
        // Why this exists: Fullscreen grid tiles bind Playnite's native
        // PART_ImageCover, which resolves the image once through a cache keyed
        // on the image id and then holds it. Changing Game.CoverImage updates
        // the Desktop grid and both details views, but a Fullscreen grid tile
        // keeps its first cover until the tiles are rebuilt. There is no SDK
        // call to invalidate that cache - IGameDatabaseAPI has nine methods and
        // none of them refresh anything.
        //
        // Owning the Image.Source ourselves sidesteps the cache entirely. The
        // cost is that it only renders where a theme places our element.
        public bool UseCoverControl
        {
            get => useCoverControl;
            set
            {
                useCoverControl = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnableCoverImage));
            }
        }

        // Read by themes as {PluginSettings Plugin=ImageRotater, Path=EnableCoverImage}
        // to decide whether to collapse their native cover element in favour of
        // ours. Named to match what BackgroundChanger themes already query, so
        // a theme author adds a branch rather than learning new vocabulary.
        public bool EnableCoverImage
        {
            get => rotateCovers && useCoverControl;
        }

        // True when the CURRENTLY SELECTED game has plugin-owned covers.
        //
        // Per-game, despite being a plain property: themes read it through
        // PluginSettings, which binds to the settings object, not to a game. It
        // is updated as the selection changes so a tile only hides its native
        // cover when we actually have something to show in its place -
        // otherwise games the user never set up would render blank.
        public bool HasDataCover
        {
            get => hasDataCover;
            set
            {
                if (hasDataCover == value)
                {
                    return;
                }

                hasDataCover = value;
                OnPropertyChanged();
            }
        }

        // The cover file the current rotation chose, as a full path.
        //
        // This is how Fullscreen grid tiles rotate. Those tiles bind Playnite's
        // native PART_ImageCover, which caches its decoded bitmap and ignores
        // later changes to Game.CoverImage - and hosting a plugin control in
        // every tile instead is not viable: dozens of controls each running an
        // async decode is what took Playnite down.
        //
        // So the theme reads a path instead, exactly as it already does for
        // UniPlaySong's now-playing art. No plugin control is involved, and
        // because each rotation picks a different FILE, the path string
        // changes too - so WPF cannot serve a stale bitmap from its URI cache.
        //
        // Global rather than per game, and that is deliberate: only the
        // selected tile shows it, which is the same scope Playnite's own
        // animated-cover themes use.
        public string CurrentCoverPath
        {
            get => currentCoverPath;
            set
            {
                if (string.Equals(currentCoverPath, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                currentCoverPath = value;
                OnPropertyChanged();
            }
        }

        // True while the selected game's current cover pick is a video.
        //
        // A theme has to know this to decide whether to show its MediaElement,
        // and it cannot work it out from CurrentCoverPath: a WPF trigger
        // compares a binding to a LITERAL, so "does this path end in .mp4" is
        // not expressible in XAML. Publishing the answer as a bool is the one
        // form a DataTrigger can consume.
        //
        // Without it the MediaElement stays visible over PART_ImageCover for as
        // long as a video is published, and the still picks rotate invisibly
        // underneath - the tile appears frozen on one clip while the log shows
        // rotation working perfectly.
        public bool CurrentCoverIsVideo
        {
            get => currentCoverIsVideo;
            set
            {
                if (currentCoverIsVideo == value)
                {
                    return;
                }

                currentCoverIsVideo = value;
                OnPropertyChanged();
            }
        }

        // Id of the game CurrentCoverPath belongs to, as a string.
        //
        // Themes compare this against each tile's own game id so only that
        // tile swaps in the rotated cover. Keyboard focus does not work as the
        // signal - Fullscreen grid tiles never report IsKeyboardFocusWithin
        // (confirmed on a live grid), and GameListItem has no IsSelected at
        // all. Comparing ids is the one signal that is actually present in the
        // tile's own data.
        //
        // A string because theme XAML compares it to {Binding Id} through
        // MultiBinding, and Guid-to-string conversion there is not reliable.
        public string CurrentCoverGameId
        {
            get => currentCoverGameId;
            set
            {
                if (string.Equals(currentCoverGameId, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                currentCoverGameId = value;
                OnPropertyChanged();
            }
        }

        // Root of the plugin's per-game image folders, with no trailing slash.
        //
        // Themes join this with a game's own id in XAML to reach that game's
        // current artwork:
        //   {ImagesRoot}\{Id}\covers\current.tile
        //
        // This is what makes a grid work. The other published values describe
        // only the selected game, so a tile binding them would show that one
        // game's cover across the whole grid; building a path from the tile's
        // own id keeps every tile independent, with no per-tile coordination
        // from the plugin and no converter in the theme.
        //
        // Set once at startup and never changed, so it needs no notification.
        public string ImagesRoot
        {
            get => imagesRoot;
            set { imagesRoot = value; OnPropertyChanged(); }
        }

        // Change the selected game's background every N seconds while it stays
        // selected. 0 = off. Backgrounds crossfade for free: Playnite's own
        // background element animates source changes.
        public int BackgroundSlideshowSeconds
        {
            get => backgroundSlideshowSeconds;
            set { backgroundSlideshowSeconds = value; OnPropertyChanged(); }
        }

        // How long the plugin's own background crossfades for.
        //
        // Playnite's background element is a FadeImage and animates a source
        // change for free; the plugin's control holds a plain WPF Image and
        // does not, so without this a plugin background snapped in while every
        // other switch in the theme dissolved.
        //
        // A setting rather than something a theme sets in markup, because a
        // theme cannot reference a plugin assembly - a clr-namespace pointing
        // at ImageRotater makes the whole resource dictionary fail to load. And
        // it cannot be read from the theme either: FadeImage lives in
        // Playnite's own assembly rather than the SDK.
        //
        // 0 disables the fade, for a theme that would rather animate the
        // container itself.
        public int BackgroundFadeMilliseconds
        {
            get => backgroundFadeMilliseconds;
            set { backgroundFadeMilliseconds = value; OnPropertyChanged(); }
        }

        // Same, for the selected game's cover tile. Native tiles hard-swap -
        // only a theme-hosted ImageRotater_Cover element can fade.
        public int CoverSlideshowSeconds
        {
            get => coverSlideshowSeconds;
            set { coverSlideshowSeconds = value; OnPropertyChanged(); }
        }

        // How cover picks are chosen, independently of backgrounds.
        //
        // Split on user request: the common ask is backgrounds changing on
        // every selection while covers change only once per Playnite session
        // ("on startup") - covers are curated box art, and a grid that
        // reshuffles constantly reads as noise. Session mode IS the
        // once-per-startup behaviour.
        public SelectionMode CoverSelectionMode
        {
            get => coverSelectionMode;
            set { coverSelectionMode = value; OnPropertyChanged(); }
        }

        // Letterbox odd-shaped backgrounds over a blurred fill of themselves.
        //
        // The rescue for what PreferScreenShape cannot reach: a game whose
        // images are ALL odd-shaped still needs its stored background to be
        // screen-shaped, or switching to it re-fits the layout visibly. The
        // composite is written to a cache; source files are never modified.
        public bool LetterboxBackgrounds
        {
            get => letterboxBackgrounds;
            set { letterboxBackgrounds = value; OnPropertyChanged(); }
        }

        // Personal SteamGridDB API key, pasted by the user. Stored as plain
        // text in Playnite's settings file: the token is read-only artwork
        // scope, free, and user-revocable, so encrypting it at rest would cost
        // portability (roaming profiles) for little real protection. It is
        // never written to the log.
        public string SteamGridDbApiKey
        {
            get => steamGridDbApiKey;
            set { steamGridDbApiKey = value; OnPropertyChanged(); }
        }

        public bool EnableRotation
        {
            get => enableRotation;
            set { enableRotation = value; OnPropertyChanged(); }
        }

        public SelectionMode SelectionMode
        {
            get => selectionMode;
            set { selectionMode = value; OnPropertyChanged(); }
        }

        public bool EnableDebugLogging
        {
            get => enableDebugLogging;
            set { enableDebugLogging = value; OnPropertyChanged(); }
        }
    }

    public class ImageRotaterSettingsViewModel : ObservableObject, ISettings
    {
        private readonly ImageRotater plugin;
        private ImageRotaterSettings editingClone;
        private ImageRotaterSettings settings;

        public ImageRotaterSettings Settings
        {
            get => settings;
            set { settings = value; OnPropertyChanged(); }
        }

        public ImageRotaterSettingsViewModel(ImageRotater plugin)
        {
            this.plugin = plugin;

            var saved = plugin.LoadPluginSettings<ImageRotaterSettings>();
            Settings = saved ?? new ImageRotaterSettings();
        }

        // Snapshot for cancel. Deep clone via JSON so every property is covered
        // automatically as settings are added.
        public void BeginEdit()
        {
            editingClone = JsonConvert.DeserializeObject<ImageRotaterSettings>(
                JsonConvert.SerializeObject(Settings));
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            // Nothing to validate: both remaining settings are checkboxes.
            errors = new List<string>();
            return true;
        }
    }
}
