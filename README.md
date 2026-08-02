# ImageRotater

A Playnite extension that rotates game background and cover artwork. Give a game
several images and it shows a different one as you browse.

> **⚠️ Work in progress — pre-1.0, initial release ongoing.** Usable and tested
> (257 tests), but settings, published file names and the theme integration
> surface can still change between versions. Back up your library first if your
> artwork matters.

## Theme authors start here

**[docs/THEME_INTEGRATION.md](docs/THEME_INTEGRATION.md)** — read before editing
any theme file.

Most themes need **no changes at all**. Background and cover rotation both work
out of the box. The guide covers the settings themes can bind, the element
names, what each attempted workaround actually cost, and two traps that
black-screen Fullscreen if you hit them.

If you find yourself patching a tile template to make covers rotate or display,
stop — that is the plugin's job, and it already does it.

## Known issues

| Issue | Status |
| --- | --- |
| **Animated covers on Fullscreen grid tiles** don't animate | Playnite renders those tiles through a WPF `Image`, which shows a GIF's first frame and cannot decode video. Doing it from a theme works but costs a crash or a regression every way it has been tried — [details](docs/THEME_INTEGRATION.md#what-theme-authors-should-not-do). Backgrounds animate fine, as do covers in any theme hosting the plugin's own cover control. |
| **Fullscreen grid covers need a plugin-side workaround** to rotate at all | Playnite never notifies `FullscreenListItemCoverObject` when a cover changes, so the plugin re-evaluates the tile binding itself. Works, but the real fix is [one line upstream](docs/PLAYNITE_ISSUE_DRAFT.md). |
| **WebM may not play** on a stock Windows install | No bundled decoder. MP4/H.264 plays everywhere. |
| **Cannot run alongside BackgroundChanger** | Playnite routes a shared theme element name to whichever plugin claimed it first, so the result depends on load order. Disable BackgroundChanger before using this. |

## Features

- **Background and cover rotation** — per game, from a folder you control, with
  independent selection modes and optional slideshow timers
- **Animated artwork** — GIF, MP4 and WebM backgrounds; the same for covers in
  themes that host the plugin's cover control
- **SteamGridDB integration** — browse and pick, or auto-download the best
  match, with shape and content filters
- **Your artwork is preserved** — existing art is copied into the plugin's
  folder before anything is replaced, so it rotates as a normal candidate
- **Opt-in per game** — games you never set up are left completely alone
- **Undo** — *Restore original Playnite backgrounds* puts every touched game back

## Install

Grab the `.pext` from [Releases](../../releases) and open it with Playnite, or
drag it onto a running Playnite window.

Requires Playnite 10 (Desktop or Fullscreen). Download features need a free
[SteamGridDB API key](https://www.steamgriddb.com/profile/preferences/api).

## Usage

Right-click a game → **ImageRotater** → *Backgrounds* or *Covers*:

| Command | What it does |
| --- | --- |
| Add images… | Copy files from disk into this game's folder |
| Browse SteamGridDB… | Pick artwork yourself, with filters |
| Download from SteamGridDB | Take the best match without asking |
| Open folder | Open this game's image folder |
| Remove all | Clear this game's images |

Artwork lives in
`%AppData%\Playnite\ExtensionsData\72b7d457-…\Images\{game id}\`.

**Backgrounds** rotate when you *leave* a game, so the next visit renders
cleanly with nothing swapping mid-transition. **Covers** rotate when you
*arrive*, and the tile updates while you watch. Odd-shaped backgrounds can be
letterboxed over a blurred fill of themselves so switching never re-fits the
layout — optional, on by default, originals never modified.

## Troubleshooting

Turn on **Enable debug logging** in settings. The plugin writes its own log to
`%AppData%\Playnite\ExtensionsData\72b7d457-…\ImageRotater.log`, fresh each
session, recording every selection and every rotation — far easier to read than
Playnite's shared `extension.log`. The first line gives the plugin version.

Bug reports are welcome at this stage. Please attach that log.

## Building

```bash
dotnet clean -c Release
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```

`version.txt` is the single source of truth; the packaging script stamps
`extension.yaml` and `AssemblyInfo.cs` from it. **The stamp applies to the next
build, not the current one** — bumping the version means build, package, then
build and package again, or the `.pext` ships the previous assembly. The script
fails rather than warns if the two disagree.

Tests: `dotnet test -c Release`

## License

MIT. See [LICENSE](LICENSE).
