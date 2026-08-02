# Fullscreen grid covers do not update when `Game.CoverImage` changes

## Summary

`GamesCollectionViewEntry` raises `PropertyChanged` for the Desktop cover
properties when a game's `CoverImage` changes, but not for the property the
Fullscreen grid actually binds. A plugin that updates `Game.CoverImage` is
reflected everywhere except Fullscreen grid tiles, which keep showing the cover
they first rendered until the tiles are destroyed and rebuilt.

## Where

`source/Playnite/GamesCollectionViewEntry.cs`, in the property-changed handler:

```csharp
if (propertyName == nameof(Game.CoverImage))
{
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObject)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObjectCached)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GridViewCoverObjectCached)));
}
```

`FullscreenListItemCoverObject` (declared at line 124 in the same file) is not
included. Searching the file for `nameof(FullscreenListItemCoverObject)` returns
no matches — it is never notified from anywhere.

That property is exactly what the Fullscreen grid binds
(`source/Playnite.FullscreenApp/Controls/GameListItem.cs`):

```csharp
var sourceBinding = new PriorityBinding();
sourceBinding.Bindings.Add(new Binding()
{
    Path = new PropertyPath(nameof(GamesCollectionViewEntry.FullscreenListItemCoverObject)),
    IsAsync = mainModel.AppSettings.Fullscreen.AsyncImageLoading,
    Converter = new NullToDependencyPropertyUnsetConverter(),
    Mode = BindingMode.OneWay
});
```

The Desktop equivalent binds `GridViewCoverObjectCached`, which *is* notified —
which is why the same plugin code works there. The two `GameListItem` classes
are otherwise structurally identical; only the bound property name and the
notification differ.

## Reproduce

1. From a plugin, set `Game.CoverImage` to a new file id and call
   `Database.Games.Update(game)`.
2. Desktop grid, Desktop details, and Fullscreen details all show the new cover.
3. The Fullscreen grid tile does not, and will not until the tiles are rebuilt —
   switching grid modes forces this and makes the new cover appear.

Using a fresh file id per update (so `ImageSourceManager`'s cache cannot serve a
stale bitmap) does not help, which is what points at the notification rather than
the image cache. Marshalling the property change onto `MainView.UIDispatcher`
does not help either.

## Suggested fix

```csharp
if (propertyName == nameof(Game.CoverImage))
{
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObject)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImageObjectCached)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GridViewCoverObjectCached)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullscreenListItemCoverObject)));
}
```

## Possibly the same for backgrounds

`Game.BackgroundImage` notifies `DisplayBackgroundImage` and
`DisplayBackgroundImageObject`. Worth checking whether the Fullscreen views bind
anything else that is likewise missed — we have not confirmed a user-visible
symptom there, since background changes do reach Fullscreen in our testing.

## Workaround we ship (and why the fix is still worth one line)

A plugin CAN work around this, with visual-tree surgery: locate the Fullscreen
grid `ListBox`, find the changed game's realised container, and call
`BindingExpressionBase.UpdateTarget()` on the `PART_ImageCover` source binding —
which re-runs the getter and resolves the current `Game.CoverImage`.
Unrealised containers need nothing (they bind fresh on realisation), and a
whole-view `ICollectionView.Refresh()` with selection restore works as a blunt
fallback.

That this works confirms the diagnosis: the only thing missing is the
notification. But it should not take reflection over an internal item type and
visual-tree walking in every plugin that touches covers — the one added line
makes Fullscreen consistent with Desktop for everyone.

## Why this matters to plugins

Without the notification there is no supported way for a plugin to refresh a
Fullscreen grid cover:

- `IGameDatabaseAPI` exposes `AddFile`, `RemoveFile`, `SaveFile`,
  `GetFullFilePath`, `GetFileStoragePath`, the buffered-update trio, and the
  database path. Nothing invalidates a cached image or forces a re-read.
- `IMainViewAPI` offers navigation, filtering and sorting, but no refresh.
- A new file id per rotation does not help, per above.

The practical result is that artwork-rotating plugins work in Desktop and in
Fullscreen details, but not in the Fullscreen grid. BackgroundChanger documents
the same limitation and
[closed it as not fixable plugin-side](https://github.com/Lacro59/playnite-backgroundchanger-plugin/issues/77),
deferring to themes.

The workarounds available to themes each have a disqualifying cost:

- Hosting a plugin control per tile puts an image decode inside the layout pass;
  done synchronously in a 32-bit process this reliably crashes Fullscreen. It is
  survivable with `IsAsync=True`, but only where a theme places the element.
- Binding a plugin-written file by path requires either
  `Source="{Binding path}"`, which never releases WPF's file handle so the file
  can never be rewritten, or an inline `BitmapImage` with `CacheOption=OnLoad`,
  which releases the handle but is frozen at `EndInit` and so never updates.
- A theme cannot ask "is this published artwork for *my* tile?" — a WPF trigger
  compares a binding to a literal, and a tile's own game id is a binding. Custom
  converters do not resolve this either: a theme referencing a plugin assembly by
  `clr-namespace` fails to load the whole resource dictionary.

Adding the one missing notification removes the need for all of it, and makes
plugin behaviour consistent between Desktop and Fullscreen.

## Environment

- Playnite 10.56 (also present in the current `master` source)
- Reproduced with a plugin writing `Game.CoverImage` on `OnGameSelected`
- Theme: Aniki ReMake (Fullscreen); the same behaviour follows from the default
  Fullscreen theme's `ListGameItemTemplate.xaml`, which also binds
  `PART_ImageCover`
