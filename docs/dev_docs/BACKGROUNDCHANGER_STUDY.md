# BackgroundChanger — Behavior & Performance Study

**Subject:** [playnite-backgroundchanger-plugin](https://github.com/Lacro59/playnite-backgroundchanger-plugin) by Lacro59
**License:** MIT (Copyright (c) 2020 Lacro59) — permissive; studying and citing is fine
**Studied at:** `master` @ `fffb800`, submodule `wpf-animatedimage` @ `0bd45ae`
**Date:** 2026-07-30

**Purpose:** understand what BackgroundChanger does and where it underperforms in
Fullscreen themes, to inform a clean-room replacement. ImageRotater copies no code
from it. This document records observed behavior and measured-by-inspection
characteristics only.

---

## Architecture in one paragraph

Theme XAML embeds `PluginBackgroundImage` / `PluginCoverImage` via Playnite's
`GetGameViewControl`. Each control instance owns its own rotation timer. Image paths
are handed to an `AnimatedImage` control (separate repo, vendored as a git submodule)
which performs the actual decode. Backgrounds crossfade between two stacked layers;
covers hard-swap with no animation. `.mp4` paths route to a `MediaElement` instead.

---

## What it does (feature inventory)

| Feature | Notes |
|---|---|
| Per-game background images | Multiple images per game, user-managed |
| Per-game cover images | Same, separate collection |
| Timed auto-rotation | Default 10s, sequential or random |
| Rotation on game selection | Via `SetData(Game, ...)` |
| Crossfade transition | Backgrounds only; 0.5s opacity animation, two-layer ping-pong |
| Video backgrounds | `.mp4` via `MediaElement`, looped, volume from settings |
| Video delay | Optional: show image first, swap to video after N seconds (default 5s) |
| Image sourcing | SteamGridDB and Google Image integrations |
| Pause on window deactivate | Video pauses when Playnite loses OS focus |

Formats: static images, APNG, WebP (including animated), MP4.

---

## The decode path — the important finding

`AnimatedImage.LoadStaticImageAsync` is the single most relevant method in the
codebase for our purposes.

**What it does correctly** — worth stating plainly, because it is better than assumed:

- Decodes inside `Task.Run`, i.e. **off the UI thread**
- Sets `CacheOption = BitmapCacheOption.OnLoad`, so the file stream is released promptly
- Calls `Freeze()` before use, making the bitmap cross-thread-safe and cheaper for WPF
- Marshals back to the UI thread via `Dispatcher.Invoke` only to assign `Source`
- Uses `BitmapCreateOptions.IgnoreColorProfile`, skipping colour-profile work

That is the textbook pattern. Any rewrite should keep all five of these properties.

**What is missing:**

1. **No `DecodePixelWidth` / `DecodePixelHeight`.** Absent from the entire file.
   Every image decodes at its native resolution regardless of display size. A 3840×2160
   background becomes roughly **32 MB** of frozen bitmap at 32bpp, then the GPU scales it
   down to fit. On a 1080p display that is about 4× the necessary memory and decode cost,
   paid on every single load.

2. **No image cache of any kind.** No dictionary, no LRU, nothing keyed by path. The only
   short-circuit is a consecutive same-path check in the calling control. Rotating through
   N images and returning to the first re-reads and re-decodes it from disk. Re-selecting a
   previously-viewed game does the same.

These two compound: full-resolution decode is expensive, and nothing amortizes it.

---

## Performance hazards observed

Ordered by likely Fullscreen impact.

### 1. Full-resolution decode (see above)
The dominant cost. Scales with source image dimensions, not display dimensions.

### 2. No cache (see above)
Turns a one-time cost into a per-rotation cost.

### 3. Timers ignore control visibility
Rotation timers are gated only on OS-level window activation, never on the control's own
`IsVisible`. A Fullscreen theme with several views each hosting a background control keeps
every off-screen instance ticking and swapping images that nobody can see. This is the
most Fullscreen-specific defect found.

### 4. Event subscriptions without matching unsubscribes — confirmed, not mitigated upstream
`Application.Current.Activated` / `.Deactivated` and `MainWindow.StateChanged` are
subscribed in the control's `Loaded` handler with no corresponding `Unloaded` removal.
`Loaded` can fire repeatedly as themes tear down and rebuild visual trees, so handlers
accumulate. Because the publishers are application-lifetime singletons, every past control
instance stays reachable — along with its timers and decoded images. Plugin-database
events are likewise subscribed in the constructor and never removed.

**Verified against the base class.** `PluginUserControlExtend` and
`PluginUserControlExtendBase` (`playnite-plugincommon` @ `b5df828`) total 364 lines and
contain **one** `+=`, **zero** `-=`, and no `Unloaded`, `OnUnloaded`, or `Dispose` member
at all. The base class performs no teardown, so nothing upstream cleans up what the
controls subscribe. This was the one open question in the first draft of this study; it
resolves against the plugin, meaning the leak is real rather than merely apparent.

### 5. Repeated filesystem stats
The available-images collections filter by an `Exist` property that calls `File.Exists` on
every access, and the filtering LINQ re-runs on every read — including on each timer tick.
Cost scales with image count per tick rather than being computed once per selection.

### 6. `Thread.Sleep(1000)` as a debounce
Runs inside `Task.Run` on every window activate and deactivate, per control instance.
Does not block the UI, but occupies a thread-pool thread for a second each time, scaling
with the number of live instances.

### 7. `MediaElement` never explicitly closed
Video teardown relies on nulling `Source`. Elements are paused rather than released when
the window deactivates, so decoder resources stay held while hidden.

### 8. `new Random()` per call
Constructed inside rotation methods despite a static instance existing on the class.
Time-seeded instances created in quick succession can correlate. Minor, but it means
"random" rotation may repeat more than it should.

---

## Design implications for ImageRotater

Keep what works:

- Off-thread decode with `OnLoad` + `Freeze()` + `IgnoreColorProfile`
- Two-layer opacity crossfade (animating `Opacity` is GPU-composited and cheap)
- Nulling sources on fade-out completion to release the hidden layer

Fix what does not:

| Problem | Approach |
|---|---|
| Full-resolution decode | Set `DecodePixelWidth` to the actual render target width |
| No cache | Bounded LRU keyed by absolute path + requested decode width |
| Timers run while hidden | Gate on the control's own visibility, not just window activation |
| Handler accumulation | Subscribe in `Loaded`, unsubscribe symmetrically in `Unloaded` |
| Repeated `File.Exists` | Resolve the image list once per selection, not per tick |
| Sleep-based debounce | Use a `DispatcherTimer` debounce |
| Video held while hidden | Release rather than pause when not visible |

### Decode width — decided

Render width is not a property of the image or the screen; it is a property of **the theme's
layout and the user's settings**. The same control can be a full-bleed 3840px background
behind the grid in one view and a 400px card backdrop in another, within the same theme,
and Playnite's own zoom/column settings change it live. There is no single correct width.

A WPF constraint shapes the solution: `DecodePixelWidth` is applied *before* `EndInit()`.
It is a decode-time parameter, and a frozen `BitmapSource` cannot be resized afterward.
"Decode small, upgrade later" therefore means a second full decode, not an adjustment.
So the real problem is not measuring the width — it is avoiding a re-decode when the width
changes.

**Decision: measure, then round up to a bucket.** Read the control's actual render width,
round up to the next of `480 / 960 / 1920 / 3840`, and decode to that. Cache key is
`(absolute path, bucket)`.

Rationale:
- Adapts automatically to any theme layout, which a fixed setting cannot.
- Ordinary resizes stay inside the same bucket, so they are cache hits rather than decodes.
- Bounds cache-key growth to four values per image instead of one per distinct pixel width,
  which is what an exact-measure approach would produce while dragging a window edge.
- A small panel gets a small bitmap; a full-bleed 4K background still gets full detail.

**Decision: bound the cache by total bytes, not item count.** An LRU with a memory budget
(default on the order of 150 MB, user-adjustable), evicting least-recently-used. Decoded
size is known at decode time, so the ceiling holds regardless of whether the library is
full of thumbnails or 4K art — a count-based bound would differ by ~30x between those two
cases while reporting the same "limit".

---

---

## Decode engine — evaluated, no dependency added

Before committing to WPF's built-in imaging, alternatives were researched against this
project's actual constraints: **.NET Framework 4.6.2**, **32-bit host process**
(Playnite's `Playnite.DesktopApp.exe` is x86 — confirmed from its PE header), WPF
rendering, and a .pext that must load native binaries reliably on arbitrary machines.

**Finding: the built-in path already does decode-time downscaling.** Microsoft's
documentation for `DecodePixelWidth` states that the JPEG and PNG codecs natively decode
to the requested size rather than decoding full-size and scaling. `BitmapImage` is a thin
wrapper over WIC, whose JPEG decoder implements `IWICBitmapSourceTransform` and performs
DCT-domain scaled decoding at 1/2, 1/4 and 1/8. PNG has no DCT structure to exploit, but
still avoids materializing a full-size intermediate.

So the gap in BackgroundChanger is not a weak decode engine — it is **one unset property**.

| Option | Verdict |
|---|---|
| WPF built-in + `DecodePixelWidth` | **Chosen.** No dependency; decode-time downscale confirmed |
| SkiaSharp | Viable (net462, x86 native, ~7.6 MB). Only if animated WebP/APNG becomes a real requirement |
| Magick.NET | Dropped explicit net462 targeting; pulls ~100 facade assemblies |
| ImageSharp | Rejected: Six Labors Split License requires a paid commercial licence, and its netfx-targetable line needs net472 — fails on both counts |
| System.Drawing / GDI+ | Rejected: no decode-time downscale at all; always decodes fully then resamples. Worse than baseline |
| Direct WIC COM interop | Marginal: `BitmapImage` already is WIC. Buys exact non-power-of-two ratios for COM lifetime-management cost |

**Decision: add no imaging dependency.** Adding ~7.6 MB of native binaries — plus x86
native-load risk on end users' machines — to replicate what one property already does
would be unjustified. Revisit only if animated WebP/APNG is actually requested.

### 32-bit host: why the byte-bounded cache matters more than it first appears

Playnite is x86, so the process has roughly 2 GB of user address space, shared with
CefSharp/Chromium. Decoded bitmaps are large **contiguous** allocations — a 4K frame needs
~32 MB of unbroken address space. Long-running 32-bit WPF processes fragment their heap,
so `OutOfMemoryException` can occur with hundreds of MB nominally free simply because no
single hole is large enough.

This is a second, independent argument for the byte-bounded LRU: it is not only about
being polite with RAM, it is about not fragmenting a constrained address space into
uselessness. It also argues for a conservative default budget.

### Unverified

- No published benchmark exists for the full "JPEG on disk → downscaled frozen
  `BitmapSource`" pipeline across these libraries; only component-level measurements. The
  recommendation rests on documented decode-path behaviour, not measured throughput.
- Exact Magick.NET-Q16-x86 native payload size was approximated, not confirmed.

---

## Verification notes

Claims here come from reading source at the commits named above. Neither submodule was
checked out in the working copy initially. Both were checked out specifically for this
study:

- `wpf-animatedimage` @ `0bd45ae` — answered the decode question. Without it the single
  most important finding (off-thread decode is already correct; sizing is what is missing)
  would have been guesswork.
- `playnite-plugincommon` @ `b5df828` — answered the teardown question, confirming the base
  class does no cleanup.

Both answers changed the conclusions. The first corrected an assumption *in the plugin's
favour*; the second confirmed a problem that had been provisional.
