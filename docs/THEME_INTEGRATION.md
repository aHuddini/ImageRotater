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

**ANIMATED covers on Fullscreen grid tiles need one element from the theme**,
and nothing else:

| | Fullscreen grid tile | Details view / Desktop | Background |
| --- | --- | --- | --- |
| Rotating between still covers | works, no theme support | works | works |
| Animated GIF / video | place `ImageRotater_Cover` | works | works |

Playnite's own tile renders `Game.CoverImage` through a WPF `Image`, which shows
a GIF's first frame and cannot decode video at all, so animating one means a
different renderer. The plugin's cover control IS that renderer - it carries an
`Image`, XamlAnimatedGif and a `MediaElement`, and picks between them per pick.
A theme hosts it; it does not need to build one.

See [Animated covers](#animated-covers-place-the-plugins-control).

**Do not build a renderer in the theme.** It can be made to work - that has been
demonstrated - but it duplicates one the plugin already has, and a second media
pipeline per tile is what crashed Playnite. The details are recorded under
[What theme authors should NOT do](#what-theme-authors-should-not-do) so nobody
repeats them.

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

## Animated covers: place the plugin's control

**One line. Nothing else.**

```xml
<ContentControl x:Name="ImageRotater_Cover"
                HorizontalAlignment="Stretch"
                VerticalAlignment="Stretch"/>
```

Put that in the game tile template and the plugin renders the cover itself —
stills, animated GIFs, and MP4/WebM video, whichever the rotation picked. No
`MediaElement`, no path bindings, no triggers, no converters.

This works because the control's lifecycle inside a Fullscreen tile is healthy,
which was worth confirming rather than assuming. Instrumented across 67
instances while scrolling a live grid: every one was constructed, received
`GameContextChanged` with the correct game, fired `Loaded`, ran its refresh,
picked artwork and rendered it at full tile size. The earlier belief that these
controls were built and never loaded came from a faulty measurement.

The control already contains every renderer needed — an `Image` for stills,
XamlAnimatedGif for GIFs, and a `MediaElement` for video — and switches between
them per pick, keeping exactly one active so two cannot fight over the same
tile.

**Hide your own cover element while the plugin has one**, or the two stack:

```xml
<Condition Binding="{PluginSettings Plugin=ImageRotater, Path=EnableCoverImage}" Value="True"/>
<Condition Binding="{PluginSettings Plugin=ImageRotater, Path=HasDataCover}" Value="True"/>
```

`HasDataCover` describes only the SELECTED game, so it cannot gate a whole grid
— see the warning under [Settings themes can bind](#settings-themes-can-bind).
The plugin's control renders nothing for a game with no artwork, so in a grid
the simplest correct answer is to leave your own element alone and let the
control draw over it only where it has something.

## What theme authors should NOT do

Everything in this section was built and run against a live Fullscreen grid.
None of it is hypothetical, and none of it is recommended.

**Do not add your own `MediaElement` to a tile template.** It animates — that
much is real, and video plays smoothly. But it is solving a problem the plugin
already solves, and it brings its own:

- **It crashes Playnite when it duplicates the plugin's.** Each realised tile
  opens its own media file, and the plugin's control already has a
  `MediaElement` of its own. Two media pipelines per tile, in a 32-bit process
  sharing its address space with Chromium, runs out well before a library
  finishes scrolling. This is what actually took Playnite down - not the
  presence of a renderer, but two of them.
- **It hides the rotation it was meant to show.** A `MediaElement` drawn over
  `PART_ImageCover` covers it for as long as it has a source, so the still
  picks rotate underneath, invisibly. Gating it on "is the current pick a
  video" needs a published boolean, because a WPF `DataTrigger` compares a
  binding to a *literal* and "does this path end in .mp4" cannot be written in
  XAML.
- **It costs a file copy per rotation.** A theme binds a PATH, so the plugin has
  to publish the pick as a file - and to serve both a `MediaElement` and an
  image element, twice, under two names. On a 4.7 MB GIF, on the selection
  path, that is visible as stutter. A hosted control reads the store directly
  and needs neither copy.

- **GIFs still will not animate.** A `MediaElement` picks its decoder by file
  extension, so it needs `current.gif` rather than the extensionless published
  name - which is why the double publish existed in the first place. The
  plugin's control uses XamlAnimatedGif and has no such constraint.

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

**What to do instead:** place `ImageRotater_Cover` and stop there. Still-cover
rotation already works with no theme support at all; animation needs only that
one element, because the renderer lives in the plugin where a single instance
per tile is guaranteed.

Every problem in this section came from putting the renderer in the THEME. Once
it lives in the plugin, the crash (two media pipelines per tile), the stutter
(a published file copy per rotation) and the dead GIFs (a decoder chosen by file
extension) all stop being problems rather than being worked around.

### Why theme-side workarounds do not exist

Each of these was built and tested against a live Fullscreen grid. They are
recorded so nobody spends the evening rediscovering them.

**A plugin control per tile.** Registering `ImageRotater_Cover` via
`AddCustomElementSupport` and placing it in the tile template resolves cleanly
and does not crash, provided the control binds its image path with
`IsAsync=True`. An earlier version decoded synchronously in code-behind and
reliably killed Fullscreen: dozens of tiles decoding inside the layout pass, in
a 32-bit process.

**It works.** An earlier version of this document claimed the opposite, on the
strength of a measurement that turned out to be wrong: it reported 49 element
requests and zero refreshes, and concluded the controls were built and never
loaded.

Re-instrumented properly, across 67 instances while scrolling a live grid,
every one of them: constructed, received `GameContextChanged` with the correct
game, fired `Loaded` with that context already set, ran its refresh, picked
artwork, and rendered at full tile size (`234x234, visible=True`). The early
`0x0, visible=False` entries are the pre-layout pass, not a failure.

So a plugin control in a Fullscreen tile is a working integration point, and it
is the right one for animated covers - see
[Animated covers](#animated-covers-place-the-plugins-control).

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
