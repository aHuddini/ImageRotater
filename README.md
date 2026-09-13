<p align="center">
  <img src="assets/banner.png" alt="ImageRotater — rotating artwork for Playnite" width="720">
</p>

<p align="center">
  Rotating artwork for <a href="https://playnite.link/">Playnite</a>. Give a game more than one cover or background and watch them change.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License">
  <img src="https://img.shields.io/badge/Playnite-10.57%2B-purple" alt="Playnite 10.57+">
  <img src="https://img.shields.io/github/downloads/aHuddini/ImageRotater/total?label=downloads&color=brightgreen" alt="Downloads">
  <img src="https://img.shields.io/github/downloads/aHuddini/ImageRotater/latest/total?label=latest%20release&color=blue" alt="Latest release downloads">
</p>

<p align="center">
  📥 <a href="#installation">Install</a>
  &nbsp;&middot;&nbsp;
  ⚙️ <a href="#settings">Settings</a>
  &nbsp;&middot;&nbsp;
  🎨 <a href="docs/THEME_INTEGRATION.md">Theme guide</a>
  &nbsp;&middot;&nbsp;
  📝 <a href="CHANGELOG.md">Changelog</a>
</p>

<p align="center">
  <a href="https://ko-fi.com/huddini">
    <img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="ko-fi">
  </a>
</p>

A library that never looks the same twice. ImageRotater keeps a collection of covers and
backgrounds for each game and shows a different one every time you visit — or cycles through them
while you linger. Bring in animated GIFs and video and your covers move.

## Highlights

- **Backgrounds and covers rotate on their own.** A new picture each time you land on a game, or
  a slideshow that changes every few seconds while you stay.
- **Animated artwork.** GIF and MP4 covers and backgrounds play in place — the selected game, or
  every tile at once if you like it lively.
- **Four places to find art.** Steam's own store art and trailers, SteamGridDB, web image search,
  and YouTube — one search dialog with filters and a live preview, one click to download.
- **Smooth changes.** Crossfade, fade through black, fade through white, or a clean cut, chosen
  separately for covers and backgrounds. Same look in Desktop and Fullscreen.
- **Works with your theme.** Rotating covers and backgrounds need nothing from a theme. Animated
  ones need a single line — many themes already have it.
- **Your art is safe.** Whatever a game had before joins the rotation instead of being replaced,
  and one click puts everything back the way it was.
- **Only the games you choose.** Nothing happens to a game until you give it artwork.

## Getting started

1. Install the extension and open a game's right-click menu.
2. **ImageRotater → Backgrounds → Search images online…** (or **Covers**).
3. Pick a few results and download them. That's it — the game rotates from now on.

| Command | What it does |
|---|---|
| Add artwork files… | Use images or video you already have |
| Search images online… | Steam, SteamGridDB, the web and YouTube, with preview |
| Download from SteamGridDB (automatic) | Grab the best match without asking |
| Open folder | See this game's artwork |
| Remove all | Take this game out of the rotation |

## Settings

### Setup

Optional extras. Everything below unlocks a feature; the extension works without any of it.

| Item | What it unlocks |
|---|---|
| SteamGridDB API key | The SteamGridDB tab. Free from steamgriddb.com → Preferences → API. |
| ffmpeg | Converting GIFs to MP4, repairing videos, and YouTube downloads. |
| yt-dlp | The YouTube tab. |
| deno | Needed alongside yt-dlp for YouTube. |

Leave a path blank if the tool is already on your system path.

### General

| Setting | Default | What it does |
|---|---|---|
| Enable rotation | On | The master switch. |
| Work with themes built for BackgroundChanger | On | Lets themes made for BackgroundChanger show animated art from ImageRotater. Disable BackgroundChanger itself. |
| Enable debug logging | Off | Writes a log for bug reports. |

### Backgrounds

| Setting | Default | What it does |
|---|---|---|
| Transition *(Animation page)* | Crossfade | How one background gives way to the next. |
| Letterbox odd-shaped backgrounds | On | Ultrawide and square art sits on a blurred, screen-shaped canvas of itself instead of being stretched. |
| Level background sizes | On | Keeps the blur consistent when backgrounds of different sizes swap. |
| When a game has several | Pick once per session | Or pick again every time you select the game, or always show the same one. |
| Slideshow | Off | Change the background every N seconds while a game stays selected. |

### Covers

| Setting | Default | What it does |
|---|---|---|
| Rotate cover art | Off | Turn on to rotate box art too. |
| Play animated covers on every tile | Off | Otherwise only the selected game's cover moves. Best left off for very large video libraries. |
| Transition *(Animation page)* | Crossfade | How one cover gives way to the next. |
| When a game has several | Pick once per session | As for backgrounds. |
| Slideshow | Off | Change the cover every N seconds while a game stays selected. |

### Library

One-click maintenance for everything ImageRotater holds: convert all GIFs to MP4, convert all
JPEGs to PNG, repair videos that show as black tiles, repair broken artwork links, or reset the
library to the original art.

## Theme authors

Rotating stills need nothing from a theme. For animated covers and backgrounds, add one line next
to your existing artwork element — the [theme guide](docs/THEME_INTEGRATION.md) shows exactly
where, with a worked example.

## Requirements

- Playnite **10.57 or newer**, Desktop or Fullscreen
- Windows with .NET Framework 4.6.2
- Optional: [ffmpeg](https://ffmpeg.org/), [yt-dlp](https://github.com/yt-dlp/yt-dlp) and
  [deno](https://deno.com/) for conversions and YouTube; a free
  [SteamGridDB API key](https://www.steamgriddb.com/profile/preferences/api) for that source

Use MP4 for video. Cannot run at the same time as BackgroundChanger.

## Installation

Download the `.pext` from [Releases](../../releases) and open it with Playnite, or drag it onto
the Playnite window.

## Troubleshooting

Turn on **Enable debug logging** in settings and attach `ImageRotater.log` from
`%AppData%\Playnite\ExtensionsData\72b7d457-0621-429b-8368-665bc53ff896\` to your report.

## Building from source

```bash
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```

Tests: `dotnet test -c Release`. Branding: `scripts/build_branding.ps1`.

## License

MIT — see [LICENSE](LICENSE).
