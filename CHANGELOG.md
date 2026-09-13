# Changelog

All notable changes to ImageRotater. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0] - 2026-09-12

First release built on Playnite 10.57.

### Added
- Transition setting for still artwork — crossfade, fade through black, fade through white, or
  cut — chosen separately for covers and backgrounds and applied identically in Desktop and
  Fullscreen.
- Steam tab in the search dialog: store artwork, trailers and store-page clips for any game
  Steam sells, no key needed.
- YouTube tab: search and download as MP4 through yt-dlp, with the video played in YouTube's own
  player for preview.
- In-dialog preview panel with a live format label on every result.
- Library page: convert all GIFs to MP4, convert all JPEGs to PNG, repair videos that render as
  black tiles, repair dangling artwork references, reset the library to the original art.
- Settings page redesigned: navigation rail, per-page strips, tool status with checkmarks.
- Background size levelling, so Playnite's blur stops jumping between backgrounds of different
  resolutions.

### Changed
- Fullscreen grid covers rotate through Playnite's own tile. The notification Playnite was missing
  shipped in 10.57 (submitted from this project); the plugin-side refresh workaround is gone.
- Playnite's background crossfade is eased so it no longer dips dark on every change.

### Fixed
- Cover transitions in Fullscreen finishing over an empty tile after the selection reload.
- Slideshow clock restarting on repeat selection events and waiting behind rendering.
- Search dialog tiles squashed by themed toggle-button styles; unreadable section headers.
- Fragmented (DASH) MP4 downloads rendering black — remuxed on download and repairable in bulk.

[1.0.0]: https://github.com/aHuddini/ImageRotater/releases/tag/v1.0.0
