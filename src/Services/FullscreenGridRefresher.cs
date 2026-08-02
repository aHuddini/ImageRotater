using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Forces the Fullscreen library grid to rebuild its tiles, so a cover
    // written by rotation actually appears.
    //
    // Needed because Playnite never raises PropertyChanged for
    // FullscreenListItemCoverObject - the property those tiles bind - when
    // Game.CoverImage changes. Desktop's equivalent IS notified, which is why
    // the same write works there. Until that one-line fix lands upstream, the
    // only lever is rebuilding the tiles, and themes cannot do it: refreshing
    // an items collection takes a method call, and Playnite themes are
    // XAML-only.
    //
    // Refreshing the view resets selection - the known cost - so the previous
    // selection is put back immediately afterwards.
    public class FullscreenGridRefresher
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly FileLogger _fileLogger;

        // Found once, remembered. The grid does not change identity within a
        // session, and walking the visual tree on every switch would be waste.
        private ListBox _grid;
        private bool _searched;

        public FullscreenGridRefresher(FileLogger fileLogger = null)
        {
            _fileLogger = fileLogger;
        }

        // Make the given game's tile re-read its cover. Safe to call from any
        // thread; the work happens on the dispatcher at background priority so
        // it lands after Playnite's own transition rather than inside it.
        //
        public void RefreshSoon(Guid gameId)
        {
            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(
                    DispatcherPriority.Background, new Action(() => RefreshNow(gameId)));
            }
            catch (Exception)
            {
                // No dispatcher means no UI to refresh.
            }
        }

        private void RefreshNow(Guid gameId)
        {
            try
            {
                ListBox grid = FindGrid();
                if (grid?.ItemsSource == null)
                {
                    return;
                }

                // Just the one stale tile, when it can be reached. Refreshing
                // the whole view works but rebuilds every visible tile - the
                // grid visibly blinks and re-decodes everything for one
                // changed cover.
                if (RefreshSingleTile(grid, gameId))
                {
                    return;
                }

                RefreshWholeView(grid);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not refresh the Fullscreen grid");
            }
        }

        // Re-evaluates the cover binding on one game's tile. UpdateTarget
        // re-runs the binding's getter, which resolves the CURRENT
        // Game.CoverImage - exactly the re-read Playnite's missing property
        // notification would have caused.
        //
        // False when the tile has no realised container, which needs no fix at
        // all: a virtualised-away tile binds fresh when it scrolls back in.
        private bool RefreshSingleTile(ListBox grid, Guid gameId)
        {
            try
            {
                object item = FindItem(grid, gameId);
                if (item == null)
                {
                    // Not in the current view (filtered out, other library
                    // view). Nothing is stale on screen.
                    return true;
                }

                var container = grid.ItemContainerGenerator.ContainerFromItem(item)
                    as FrameworkElement;

                if (container == null)
                {
                    // Virtualised away - will bind fresh on realisation.
                    return true;
                }

                Image cover = FindCoverImage(container);
                if (cover == null)
                {
                    return false;
                }

                BindingExpressionBase binding =
                    BindingOperations.GetBindingExpressionBase(cover, Image.SourceProperty);

                if (binding == null)
                {
                    return false;
                }

                binding.UpdateTarget();
                return true;
            }
            catch (Exception)
            {
                // Fall back to the blunt instrument.
                return false;
            }
        }

        // Fade the tile's cover out, run the swap while it is invisible, fade
        // back in.
        //
        // The swap runs BETWEEN the fades, not before them. In Desktop mode
        // Playnite's own change notification updates the tile the instant the
        // database write lands - any fade started after the write animates a
        // tile that has already snapped. Writing behind opacity 0 is the only
        // ordering that fades in both modes.
        //
        // Animating Playnite's own tile Image from the plugin: no theme
        // support needed, nothing injected into the tile's tree, and opacity
        // is not set by the tile's template so the animation fights nothing.
        // The animation is explicitly detached at the end (BeginAnimation with
        // null) so a lingering clock cannot pin a recycled container's opacity
        // for the session, and every failure path restores opacity 1 - a tile
        // must never stay invisible.
        public void AnimatedSwap(Guid gameId, Action swap, bool updateBinding)
        {
            if (swap == null)
            {
                return;
            }

            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => AnimatedSwapNow(gameId, swap, updateBinding)));
            }
            catch (Exception)
            {
                // No dispatcher, no UI - but the rotation itself must still
                // happen.
                RunSwapSafely(swap);
            }
        }

        private void AnimatedSwapNow(Guid gameId, Action swap, bool updateBinding)
        {
            Image cover = null;

            try
            {
                ListBox grid = FindGrid();

                object item = grid?.ItemsSource != null ? FindItem(grid, gameId) : null;
                var container = item != null
                    ? grid.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement
                    : null;

                cover = container != null ? FindCoverImage(container) : null;

                // A tiny Image is not a cover. Desktop's DETAILS layout put a
                // 26px PART_ImageIcon inside the list item and the fade ran on
                // that - invisible from the couch - while the real cover sat
                // in the details pane, outside the list entirely.
                if (cover != null && cover.ActualWidth * cover.ActualHeight < 64 * 64)
                {
                    cover = null;
                }

                // The visible cover can live outside the items container:
                // Desktop's details pane hosts its own PART_ImageCover
                // (GameOverview names it exactly that). Largest visible match
                // in the window wins.
                if (cover == null)
                {
                    cover = FindWindowCover();
                }

                // Which link broke - or which element was chosen - decides
                // where a fix goes. A fade running on the WRONG Image (some
                // layer that is not the visible cover) looks identical to "no
                // fade" from the couch, so the success path logs its choice
                // too.
                if (_fileLogger != null && _fileLogger.IsEnabled)
                {
                    if (cover == null)
                    {
                        _fileLogger.Log(
                            $"animated swap fallback: grid={(grid != null)} item={(item != null)} "
                            + $"container={(container != null)} cover=False");
                    }
                    else
                    {
                        _fileLogger.Log(
                            $"animated swap target: name='{cover.Name}' "
                            + $"{(int)cover.ActualWidth}x{(int)cover.ActualHeight} "
                            + $"visible={cover.IsVisible} opacity={cover.Opacity:0.##}");
                    }
                }
            }
            catch (Exception)
            {
                cover = null;
            }

            // Tile not on screen: nothing to animate, just rotate.
            if (cover == null)
            {
                RunSwapSafely(swap);

                if (updateBinding)
                {
                    RefreshNow(gameId);
                }

                return;
            }

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(180)));

            fadeOut.Completed += (s, e) =>
            {
                try
                {
                    RunSwapSafely(swap);

                    // Fullscreen tiles get no notification, so the binding is
                    // told to re-read while the tile is invisible. Desktop
                    // needs nothing: Playnite's own notification already
                    // swapped the source behind opacity 0.
                    if (updateBinding)
                    {
                        RefreshNow(gameId);
                    }

                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(
                        0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(240)));

                    fadeIn.Completed += (s2, e2) =>
                    {
                        cover.BeginAnimation(UIElement.OpacityProperty, null);
                        cover.Opacity = 1.0;
                    };

                    cover.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch (Exception)
                {
                    cover.BeginAnimation(UIElement.OpacityProperty, null);
                    cover.Opacity = 1.0;
                }
            };

            cover.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private static void RunSwapSafely(Action swap)
        {
            try
            {
                swap();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: slideshow swap failed");
            }
        }

        // The whole-view rebuild: every visible tile re-created. Kept as the
        // fallback because it is known to work - it just blinks.
        private static void RefreshWholeView(ListBox grid)
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            if (view == null)
            {
                return;
            }

            // Refresh drops the selection - the known cost - so it is captured
            // first and put straight back.
            object selected = grid.SelectedItem;

            view.Refresh();

            if (selected != null)
            {
                grid.SelectedItem = selected;
            }
        }

        // The Id getter, resolved once. Every cover arrival scans the grid's
        // items - hundreds in a real library - and a GetProperty lookup per
        // item per scan is reflection cost paid for nothing: the item type
        // never changes within a session.
        private static System.Reflection.PropertyInfo _idProperty;

        // The entry whose game id matches, via reflection: the item type is
        // Playnite-internal and not referencable from a plugin.
        private static object FindItem(ListBox grid, Guid gameId)
        {
            foreach (object item in grid.Items)
            {
                try
                {
                    if (item == null)
                    {
                        continue;
                    }

                    // ReflectedType, not DeclaringType: Id could be declared on
                    // a base class, and DeclaringType would then never equal
                    // the item's runtime type - re-resolving on every item.
                    if (_idProperty == null || _idProperty.ReflectedType != item.GetType())
                    {
                        _idProperty = item.GetType().GetProperty("Id");
                    }

                    var id = _idProperty?.GetValue(item) as Guid?;
                    if (id == gameId)
                    {
                        return item;
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        // The biggest visible PART_ImageCover anywhere in the main window.
        //
        // For layouts where the cover is not inside the game's item container:
        // Desktop's details view renders it in the details pane. Name-required
        // here, unlike the tile search - a window-wide "largest image" grab
        // would happily fade a full-screen background.
        private static Image FindWindowCover()
        {
            try
            {
                Window window = Application.Current?.MainWindow;
                if (window == null)
                {
                    return null;
                }

                Image best = null;
                double bestArea = 0;

                FindNamedCovers(window, ref best, ref bestArea);

                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void FindNamedCovers(DependencyObject root, ref Image best, ref double bestArea)
        {
            if (root == null)
            {
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                var image = child as Image;
                if (image != null && image.Name == "PART_ImageCover" && image.IsVisible)
                {
                    double area = image.ActualWidth * image.ActualHeight;
                    if (area > bestArea)
                    {
                        best = image;
                        bestArea = area;
                    }
                }

                FindNamedCovers(child, ref best, ref bestArea);
            }
        }

        // The tile's cover element. PART_ImageCover by name when the theme
        // kept Playnite's naming; otherwise the largest rendered Image in the
        // tile - which in a grid tile is the cover by construction. Matching
        // on name alone broke on Desktop themes that rename the part: every
        // other link resolved and the fade silently fell back to a snap.
        private static Image FindCoverImage(DependencyObject root)
        {
            Image named = null;
            Image largest = null;
            double largestArea = 0;

            CollectImages(root, ref named, ref largest, ref largestArea);

            return named ?? largest;
        }

        private static void CollectImages(
            DependencyObject root, ref Image named, ref Image largest, ref double largestArea)
        {
            if (root == null || named != null)
            {
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                var image = child as Image;
                if (image != null)
                {
                    if (image.Name == "PART_ImageCover")
                    {
                        named = image;
                        return;
                    }

                    double area = image.ActualWidth * image.ActualHeight;
                    if (area > largestArea)
                    {
                        largest = image;
                        largestArea = area;
                    }
                }

                CollectImages(child, ref named, ref largest, ref largestArea);
            }
        }

        private ListBox FindGrid()
        {
            if (_grid != null || _searched)
            {
                // A grid found once can still be discarded by a view change;
                // re-search only when the cached one has left the tree.
                if (_grid != null && PresentationSource.FromVisual(_grid) != null)
                {
                    return _grid;
                }

                _grid = null;
                _searched = false;
            }

            _searched = true;

            Window window = Application.Current?.MainWindow;
            if (window == null)
            {
                return null;
            }

            _grid = FindGameGrid(window);

            _fileLogger?.Log(_grid != null
                ? "fullscreen grid located for cover refresh"
                : "fullscreen grid NOT found - covers will not refresh on tiles");

            return _grid;
        }

        // The library grid is the ListBox whose items are Playnite's
        // GamesCollectionViewEntry. Matched by item type rather than by
        // control name, so a renamed template part in some Playnite version
        // does not silently break this.
        private static ListBox FindGameGrid(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                var listBox = child as ListBox;
                if (listBox?.Items != null && listBox.Items.Count > 0 &&
                    string.Equals(
                        listBox.Items[0]?.GetType().Name,
                        "GamesCollectionViewEntry",
                        StringComparison.Ordinal))
                {
                    return listBox;
                }

                ListBox found = FindGameGrid(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
