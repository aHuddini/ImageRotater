using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ImageRotater.Services
{
    // How a still replaces the still before it.
    public enum TransitionStyle
    {
        Crossfade,          // The old picture dissolves into the new one
        FadeThroughBlack,   // Dip to black between the two
        FadeThroughWhite,   // Flash to white between the two
        Cut                 // No animation at all
    }

    // The one place the still transition is decided, so a background in
    // Desktop, a cover tile in Fullscreen and a theme-hosted cover all move
    // the same way. Three renderers draw stills - Playnite's FadeImage,
    // Playnite's cover Image, and the plugin's own cover control - and each
    // reads its style and timing from here.
    public static class Transition
    {
        public static TransitionStyle Style { get; set; } = TransitionStyle.Crossfade;

        // One duration for every renderer, so a cover and a background
        // changing together finish together. A flash spends half going up
        // and half coming down.
        public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(400);
        public static TimeSpan Half => TimeSpan.FromMilliseconds(Duration.TotalMilliseconds / 2);

        public static bool IsFlash =>
            Style == TransitionStyle.FadeThroughBlack || Style == TransitionStyle.FadeThroughWhite;

        public static Color FlashColor =>
            Style == TransitionStyle.FadeThroughWhite ? Colors.White : Colors.Black;

        // Transitions an element the plugin does not own - Playnite's cover
        // Image - around a swap of its source, without touching the element's
        // tree: the veil is an adorner, WPF's own overlay layer.
        //
        // Crossfade holds a snapshot of the current picture over the element,
        // swaps underneath it, and dissolves the snapshot. A flash raises a
        // colour, swaps behind it, and lowers it. Returns false when the
        // element has no adorner layer to draw in, so the caller can fall
        // back to something that needs none.
        public static bool Run(FrameworkElement target, Action swap)
        {
            if (target == null || swap == null)
            {
                return false;
            }

            if (Style == TransitionStyle.Cut)
            {
                swap();
                return true;
            }

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(target);
            if (layer == null)
            {
                return false;
            }

            Brush brush;
            if (IsFlash)
            {
                brush = new SolidColorBrush(FlashColor);
            }
            else
            {
                // A crossfade needs the OLD picture, and only an Image has one
                // to snapshot.
                var image = target as Image;
                if (image?.Source == null)
                {
                    return false;
                }

                brush = new ImageBrush(image.Source) { Stretch = image.Stretch };
            }

            brush.Freeze();
            var veil = new VeilAdorner(target, brush);
            layer.Add(veil);

            void Remove()
            {
                veil.BeginAnimation(UIElement.OpacityProperty, null);
                layer.Remove(veil);
            }

            void Lower(TimeSpan over)
            {
                var down = new DoubleAnimation(0.0, new Duration(over));
                down.Completed += (s, e) => Remove();
                veil.BeginAnimation(UIElement.OpacityProperty, down);
            }

            try
            {
                if (IsFlash)
                {
                    veil.Opacity = 0.0;
                    var up = new DoubleAnimation(1.0, new Duration(Half));
                    up.Completed += (s, e) =>
                    {
                        try
                        {
                            swap();
                        }
                        finally
                        {
                            Lower(Half);
                        }
                    };
                    veil.BeginAnimation(UIElement.OpacityProperty, up);
                }
                else
                {
                    veil.Opacity = 1.0;
                    swap();
                    Lower(Duration);
                }
            }
            catch
            {
                Remove();
                throw;
            }

            return true;
        }

        // A flat brush drawn over exactly the adorned element's bounds. Follows
        // the element's transforms (a theme's selected-tile scale, for one)
        // because that is what an adorner layer does.
        private sealed class VeilAdorner : Adorner
        {
            private readonly Brush _brush;

            public VeilAdorner(UIElement adorned, Brush brush) : base(adorned)
            {
                _brush = brush;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(_brush, null, new Rect(AdornedElement.RenderSize));
            }
        }
    }
}
