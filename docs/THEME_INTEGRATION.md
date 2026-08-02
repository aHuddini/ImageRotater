# Theme integration

ImageRotater needs no theme support for most things.

**Backgrounds, and covers in details views, work everywhere.** The plugin writes
Playnite's own `Game.BackgroundImage` / `Game.CoverImage`, and those views re-read
the value when it changes. Nothing to add.

**Covers on Fullscreen grid tiles rotate too, with no theme support needed** —
the plugin locates the tile and re-evaluates its cover binding itself. The
section below records the underlying Playnite bug and what each attempted
approach actually cost, for anyone maintaining this or attempting the same
elsewhere.

**ANIMATED covers on Fullscreen grid tiles are possible but not viable, and the
plugin does not ship them.** Rotation and animation are separate problems with
separate answers:

| | Fullscreen grid tile | Details view / Desktop | Background |
| --- | --- | --- | --- |
| Rotating between still covers | works (plugin refreshes the tile) | works | works |
| Animated GIF / video | achievable, unstable — see below | works | works |

Playnite's grid tile renders `Game.CoverImage` through a WPF `Image`, which
shows a GIF's first frame and cannot decode video at all. Animating one means
putting a *different renderer* in the tile.

That has been done, and it worked: a `MediaElement` in the tile template, its
source derived from the tile's own game id, playing smoothly — smoother than
the GIF path, since it is hardware-assisted H.264 rather than frame-by-frame
decoding on the UI thread. So this is not impossible, and anyone who reports
having done it is not mistaken.

It is not shipped because the cost showed up immediately after: Playnite
crashed once a second element was added per tile, backgrounds turned choppy
from the extra file copies per rotation, and GIFs — the format most artwork
actually uses — still would not animate. Details in
[What theme authors should NOT do](#what-theme-authors-should-not-do).

**A theme author should not have to patch a tile template for this.** Getting
cover art to rotate and display is the plugin's job, and it already does it
with no theme support. If you find yourself editing `ListGameItemTemplate.xaml`
to make covers work, that is a gap in the plugin or in Playnite — not something
your theme is expected to solve.

---

## The Fullscreen grid cover problem

**Status: Playnite bug. Still covers are worked around in-plugin. Animated ones
can be made to work from a theme, but not stably. Theme authors need to do
nothing in either case.**

The working mechanism: on arrival at a game, the plugin rotates its cover,
finds the game's tile in the grid (matched by item type, not control name),
and calls `UpdateTarget()` on the `PART_ImageCover` binding — which re-runs
the binding getter and resolves the current `Game.CoverImage`. Exactly the
re-read the missing notification should have caused. Unrealised (virtualised)
tiles need nothing: they bind fresh when scrolled back in. A whole-view
`ICollectionView.Refresh()` (rebuilds every tile, restores selection) is kept
as fallback.

A theme cannot do any of this itself — refreshing an items view or updating a
binding takes a method call, and Playnite themes are XAML-only.

Playnite's `GamesCollectionViewEntry` raises `PropertyChanged` for the Desktop
cover properties when `Game.CoverImage` changes, but not for the property the
Fullscreen grid binds:

```csharp
// source/Playnite/GamesCollectionViewEntry.cs
if (propertyName == nameof(Game.CoverImage))
{
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObject)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObjectCached)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GridViewCoverObjectCached)));
}
```

The Fullscreen grid binds `FullscreenListItemCoverObject`
(`source/Playnite.FullscreenApp/Controls/GameListItem.cs`). Searching that file
for `nameof(FullscreenListItemCoverObject)` returns no matches — it is never
notified from anywhere. The Desktop `GameListItem` is otherwise structurally
identical; only the bound property and the notification differ, which is exactly
why the same plugin code works in Desktop and not in Fullscreen.

**Symptom:** a Fullscreen grid tile shows whatever cover was current when the
tile was built, and never updates. Switching grid modes rebuilds the tiles and
they pick up new covers — which looks like partial success, but is just
reconstruction, not rotation.

**The fix is one line upstream.** Full write-up, with the reproduction and the
workarounds ruled out: [PLAYNITE_ISSUE_DRAFT.md](PLAYNITE_ISSUE_DRAFT.md).

For *still* covers the plugin works around it, so a theme needs to do nothing.
For *animated* ones a theme-side renderer does work, but costs more than it
buys — see
[What theme authors should NOT do](#what-theme-authors-should-not-do).

## What theme authors should NOT do

Everything in this section was built and run against a live Fullscreen grid.
None of it is hypothetical, and none of it is recommended.

**Do not add a `MediaElement` to your tile template.** It is the only way to
animate a grid tile from XAML alone, and it genuinely works: video plays in the
tile, smoothly, and looks better than the GIF path does elsewhere. The problem
is everything that comes with it.

- **It crashes Playnite.** Each realised tile opens its own media file. One
  element per tile is survivable; a second for a different format is not. A
  32-bit process sharing its address space with Chromium runs out well before
  a library finishes scrolling.
- **It hides the rotation it was meant to show.** A `MediaElement` drawn over
  `PART_ImageCover` covers it for as long as it has a source, so the still
  picks rotate underneath, invisibly. Gating it on "is the current pick a
  video" needs a published boolean, because a WPF `DataTrigger` compares a
  binding to a *literal* and "does this path end in .mp4" cannot be written in
  XAML.
- **It costs a file copy per rotation.** Serving both a `MediaElement` and an
  image element means publishing the same pick twice, under two names. On a
  4.7 MB GIF, on the selection path, that is visible as stutter.

**Do not reuse the BackgroundChanger cover pattern for animation.** Aniki hosts
`BackgroundChanger_PluginCoverImage` as a 1×1 collapsed control and pulls
`Content.Source` out of it into its own `FadeImage`. That is a good pattern and
it works — for *stills*. It yields a single `BitmapSource`, so it strips the
animation: GIF playback lives in an attached property on the control's own
`Image`, not in `Source`. Copying it gets clean cover rotation and specifically
not animated covers.

**Do not put a `Binding` inside a template trigger's `Storyboard`.** Template
triggers must be freezable and a binding is not, so the theme fails to load
with *"Cannot freeze this Storyboard timeline tree"* — which in Fullscreen is a
black window. A `MediaTimeline` with `RepeatBehavior="Forever"` carries no
binding and does seal.

**Do not write `<Trigger Property="IsSelected">` in a `ControlTemplate` whose
`TargetType` is `GameListItem`.** The property resolves against the target
type, and `GameListItem` has no `IsSelected` — the theme dies with *"Property
can not be null on Trigger"*. Bind the containing `ListBoxItem` instead:

```xml
<DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=ListBoxItem}, Path=IsSelected}"
             Value="True">
```

**What to do instead:** place `ImageRotater_Cover` if you want the plugin to own
cover rendering, and otherwise nothing. Still-cover rotation already works with
no theme support at all.

Animated grid covers are a different matter. They are reachable from a theme -
that has been demonstrated, and it looked good - but every version of it traded
a crash, a regression, or a format that still would not animate for a partial
result. The stable version needs the upstream notification, at which point the
plugin can own the renderer and a theme goes back to needing nothing.

### Why theme-side workarounds do not exist

Each of these was built and tested against a live Fullscreen grid. They are
recorded so nobody spends the evening rediscovering them.

**A plugin control per tile.** Registering `ImageRotater_Cover` via
`AddCustomElementSupport` and placing it in the tile template resolves cleanly
and does not crash, provided the control binds its image path with
`IsAsync=True`. An earlier version decoded synchronously in code-behind and
reliably killed Fullscreen: dozens of tiles decoding inside the layout pass, in
a 32-bit process.

It still does not work, and the failure is more complete than "renders once".
Instrumented against a live grid: Playnite requested the element **49 times**
and the control's `Refresh()` ran **zero times**. The controls are constructed
and then never told anything — no context change, no notification — so they
never reach the code that would draw a cover at all.

This is the difference from BackgroundChanger, whose control shows the artwork
of the *selected* game and therefore updates whenever selection changes. A
rotating cover has to change while a tile stays put, which is precisely the
case the missing notification kills.

**A theme-side converter.** WPF triggers compare a binding to a *literal*, and a
tile's own game id is a binding, so a tile cannot ask "is this published cover
mine?". Shipping an `IMultiValueConverter` in the plugin does not help either:
themes cannot reference a plugin assembly. A `clr-namespace` pointing at
`ImageRotater` makes the whole resource dictionary fail to load, and Playnite
falls back to the default theme.

**A file the theme binds by path.** The plugin publishes each game's current
artwork to a fixed path, but neither XAML binding form works:

| Binding form | Re-reads when the file changes | Holds the file open |
| --- | --- | --- |
| `Source="{Binding path}"` | only if the *path* changes | yes, for the bitmap's lifetime |
| Inline `BitmapImage` + `CacheOption=OnLoad` | never — frozen at `EndInit()` | no |

`CreateOptions=IgnoreImageCache` does not change this. It stops WPF serving a
previously cached bitmap *at load time*; it does not make a binding watch a file.

A generation counter in the filename (`current.1.tile`, `current.2.tile`, …) does
force the re-read, at the cost of one locked file per rotation until Playnite
restarts. Viable, but unbuilt, and it only papers over the missing notification.

**One trap worth naming.** Playnite's `{Settings}` markup extension is illegal in
a `Setter.Value`. A theme can carry such a setter indefinitely without symptom,
because a `Style` is only sealed when it is actually applied — so a
`ContentControl.Resources` block for a plugin that is not installed never throws.
Install the plugin, the element resolves, the style seals, and Fullscreen crashes
with `'Settings' is not valid for Setter.Value`. Style the plugin's own control
instead.

---

## Settings themes can bind

These work today. The plugin registers `AddSettingsSupport` with
`SourceName = "ImageRotater"`, so themes can read:

| Path | Meaning |
| --- | --- |
| `EnableCoverImage` | cover rotation is on *and* the theme-rendered path is enabled |
| `HasDataCover` | the **currently selected** game has plugin covers |
| `CurrentCoverPath` | full path to the cover the last rotation chose |
| `CurrentCoverGameId` | the game that path belongs to |
| `ImagesRoot` | root of the plugin's per-game image folders |

```xml
<Condition Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataCover}" Value="True"/>
```

`PluginStatus` matches the **installed folder name**, which for this plugin uses a
dot rather than an underscore:

```xml
<Condition Binding="{PluginStatus Plugin=ImageRotater.72b7d457-0621-429b-8368-665bc53ff896, Status=Installed}" Value="True"/>
```

`HasDataCover`, `CurrentCoverPath` and `CurrentCoverGameId` describe only the
selected game. They cannot drive a grid on their own — every tile would show the
same cover.

## Per-game published artwork

Every game has a published file at:

```
{ImagesRoot}\{game id}\covers\current.tile
{ImagesRoot}\{game id}\backgrounds\current.tile
```

It is written on every rotation, and seeded at startup for the whole library so
the path always resolves — games with no plugin artwork get a 70-byte transparent
placeholder rather than a copy of their artwork. `.tile` is deliberately not a
recognised image extension, so the published copy is never offered back as a
rotation candidate; WPF decodes by content, not by name.

This exists for themes that want to draw artwork themselves. It does not solve
the Fullscreen grid issue above, for the binding reasons already listed.

## Elements

```xml
<ContentControl x:Name="ImageRotater_Background"/>
<ContentControl x:Name="ImageRotater_Cover"/>
```

Backgrounds work without a theme hook; the element is only needed if a theme
wants to control sizing or layering itself.

## Themes built for BackgroundChanger

While the "BackgroundChanger compatibility" setting is on, ImageRotater also
answers to `BackgroundChanger_PluginBackgroundImage` and
`BackgroundChanger_PluginCoverImage`.

The *elements* resolve, but the *conditions* around them do not: such a theme
gates on `{PluginStatus Plugin=playnite-backgroundchanger-plugin}` and
`{PluginSettings Plugin=BackgroundChanger}`, which only that plugin can satisfy.
ImageRotater cannot answer to another plugin's identity without colliding with a
real BackgroundChanger install — Playnite keys settings, data paths and the addon
registry on that id.

A theme supporting both should add a parallel branch using ImageRotater's own id
and name. The two plugins should not be enabled together in any case: Playnite
routes a shared element name to whichever plugin claimed it first, so the winner
depends on load order.
