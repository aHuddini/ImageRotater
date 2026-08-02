# ImageRotater

> ### ⚠️ Work in progress — initial release is still ongoing
>
> This is pre-1.0 and under active development. It is usable and tested, but:
>
> - **Expect breaking changes.** Settings, published file names and the theme
>   integration surface can still move between versions.
> - **Animated covers in the Fullscreen grid do not work yet.** Still-cover and
>   background rotation do. See
>   [Theme integration](docs/THEME_INTEGRATION.md) for exactly what works where
>   and why.
> - **Back up your library before trying it** if your artwork matters to you.
>   The plugin copies a game's existing art into its own folder before replacing
>   anything, and *Restore original Playnite backgrounds* undoes its writes —
>   but a backup costs nothing and this has not been through many hands yet.
>
> Bug reports are welcome and useful at this stage. Please include the
> `ImageRotater.log` produced with debug logging enabled in settings.

A Playnite extension that rotates game background and cover artwork. Give a game
several images and it shows a different one as you browse.

Built as a clean-room alternative to
[BackgroundChanger](https://github.com/Lacro59/playnite-backgroundchanger-plugin),
with an emphasis on not touching games you have not set up, and on never taking
Playnite down.

> **The two cannot run together.** Playnite routes a shared theme element name to
> whichever plugin claimed it first, so with both enabled the result depends on
> load order. Disable BackgroundChanger before using this.

## Features

- **Background and cover rotation** — per game, from a folder you control
- **SteamGridDB integration** — browse and pick artwork, or auto-download the
  best match, with aspect-ratio filters for box art vs banners
- **Selection modes** — one image per session, a new one on every selection, or
  a fixed pick
- **Your own artwork is preserved** — art a game already had is copied into the
  plugin's folder before anything is replaced, so it stays in the rotation and
  can be restored
- **Opt-in per game** — games you never set up are left completely alone
- **Undo** — *Restore original Playnite backgrounds* in the main menu puts every
  touched game back

## Requirements

- Playnite 10 (Desktop or Fullscreen)
- A [SteamGridDB API key](https://www.steamgriddb.com/profile/preferences/api)
  for the download features (free; artwork-read scope only)

## Install

Grab the `.pext` from [Releases](../../releases) and open it with Playnite, or
drag it onto a running Playnite window.

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

## How rotation behaves

- **Backgrounds** rotate when you *leave* a game, so the next visit renders the
  new image cleanly — nothing swaps mid-transition.
- **Covers** rotate when you *arrive*, and the grid tile updates while you
  watch. Backgrounds and covers each have their own rotation mode, so
  "backgrounds every selection, covers once per Playnite launch" works.
- Odd-shaped backgrounds (ultrawide banners, squares) can be letterboxed over
  a blurred fill of themselves so switching games never visibly re-fits the
  layout. Optional, on by default, originals never modified.

**A note on Fullscreen grid covers:** Playnite itself never refreshes those
tiles when a cover changes — `FullscreenListItemCoverObject` is never notified
(`GamesCollectionViewEntry.cs`), which is why
[BackgroundChanger gave up on this](https://github.com/Lacro59/playnite-backgroundchanger-plugin/issues/77).
ImageRotater works around it by telling the tile's cover binding to re-read
directly. The upstream one-line fix is still worth having:
[docs/PLAYNITE_ISSUE_DRAFT.md](docs/PLAYNITE_ISSUE_DRAFT.md).

That workaround covers **still** covers. Animated ones are a separate problem:
Playnite renders a grid tile through a WPF `Image`, which shows a GIF's first
frame and cannot decode video at all. Animating a tile has been demonstrated
from a theme and it looked good, but every version of it cost a crash, a
regression, or a format that still would not move — so the plugin does not ship
it. GIFs and video **do** animate as backgrounds, and in any theme that hosts
the plugin's own cover control. Details:
[docs/THEME_INTEGRATION.md](docs/THEME_INTEGRATION.md).

## For theme authors

[docs/THEME_INTEGRATION.md](docs/THEME_INTEGRATION.md) covers the settings themes
can bind, the per-game published artwork files, and the element names — including
a `{Settings}`-in-`Setter.Value` trap that crashes Fullscreen once a plugin
element actually resolves.

## Building

```bash
dotnet clean -c Release
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```

Run all three in order. `version.txt` is the single source of truth; the
packaging script stamps `extension.yaml` and `AssemblyInfo.cs` from it.

**The stamp applies to the next build, not the current one.** Bumping the version
means build, package, then build and package again — or the `.pext` ships the
previous assembly. The script fails rather than warns if the two disagree.

Tests: `dotnet test -c Release`

## Troubleshooting

Turn on **Enable debug logging** in settings. The plugin writes its own log to
`%AppData%\Playnite\ExtensionsData\72b7d457-…\ImageRotater.log`, fresh each
session, recording every selection and every rotation. Playnite's shared
`extension.log` interleaves every plugin and is far harder to read.

The first line gives the plugin version — worth checking it matches what you
installed.

## License

MIT. See [LICENSE](LICENSE).
