using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Playnite.SDK;

namespace ImageRotater.Services
{
    // Retimes Playnite's own background crossfade so it stops dipping dark on
    // every change.
    //
    // Playnite's FadeImage crossfades by running fade-in (0 to 1) and fade-out
    // (1 to 0) SIMULTANEOUSLY. Two stacked layers at opacity t and 1-t let the
    // backdrop bleed through by t*(1-t) - a quarter of it at the midpoint of
    // every single transition. Over a heavily blurred background there is no
    // structure to watch, so that luminance dip IS the visible event: the
    // background pulses dark and recovers on every game change, and with the
    // control's BitmapCache re-rendering the full-window blur every animation
    // frame, the pulse drops frames and reads as a pop.
    //
    // The fix is easing the two fades against each other - see the note on
    // the Ease method for why sequencing them was tried first and reverted.
    //
    // No reflection. The four storyboards are ordinary entries in the
    // control's public Resources, and the control's internal fields hold those
    // same instances - retiming the resource retimes what Begin() runs. The
    // control is matched by type NAME, so this needs no reference to
    // Playnite's internals at all, and a Playnite update that renames anything
    // makes this a silent no-op rather than a break.
    public static class FadeImageTuner
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string FadeImageTypeName = "Playnite.Controls.FadeImage";

        // The dip is removed with EASING, not with sequencing - and the
        // distinction was learned the hard way.
        //
        // The first version made the fades sequential: fade-in over the first
        // half, fade-out delayed by a BeginTime into the second. Zero bleed on
        // paper - but a storyboard waiting on its BeginTime is a PENDING
        // clock, and Playnite Stop()s and re-Begin()s these storyboards
        // freely on rapid changes. A pending clock stranded by that
        // interleaving never delivers a value, which left the outgoing image
        // frozen at full opacity underneath the new one: two backgrounds
        // stacked on screen indefinitely.
        //
        // Easing has no waiting state. Both fades run exactly when stock ones
        // do, for the stock duration - the incoming image just rises fast
        // early (ease-out) while the outgoing holds high early (ease-in). At
        // the midpoint both sit near 0.875 instead of 0.5, which cuts the
        // backdrop bleed from 25% to under 2% - below what a radius-59 blur
        // makes visible. Any Stop/Begin interleaving behaves byte-for-byte
        // like stock, because structurally it IS stock.

        // Instances already retimed. Weak, so recycled or closed windows do
        // not pin dead controls for the session.
        private static readonly ConditionalWeakTable<UserControl, object> Patched =
            new ConditionalWeakTable<UserControl, object>();

        // Walks the main window and retimes every FadeImage found. Idempotent
        // and cheap to repeat: already-patched instances are skipped by the
        // weak table, and a tree with no FadeImage just walks and returns.
        public static int Apply()
        {
            try
            {
                Window window = Application.Current?.MainWindow;

                if (window == null)
                {
                    return 0;
                }

                return Patch(window);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not retime Playnite's background fade");
                return 0;
            }
        }

        private static int Patch(DependencyObject node)
        {
            int patched = 0;

            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);

                if (child is UserControl control &&
                    control.GetType().FullName == FadeImageTypeName)
                {
                    if (TryRetime(control))
                    {
                        patched++;
                    }

                    // A FadeImage does not nest another, but its subtree is
                    // tiny either way - no reason to special-case the walk.
                }

                patched += Patch(child);
            }

            return patched;
        }

        // Attaches the blur BEFORE the first image ever loads.
        //
        // Playnite creates the BlurEffect lazily, inside the first image load:
        // its blur-setting callback returns early while Source is still null,
        // so a freshly built FadeImage carries NO effect until the first
        // LoadNewSource attaches one - after the image is decoded. A radius-59
        // full-window Gaussian is a real shader that takes visible time on its
        // first use, so the image lands SHARP for a beat and then snaps to
        // blurred. That is the "crop appears, then the blur arrives" artefact.
        //
        // Attaching the effect up front means it exists, compiled and warm,
        // before any image does - and Playnite's own lazy branch then sees a
        // non-null effect and skips creating one, so nothing is ever attached
        // twice.
        private static void EnsureEffect(UserControl fadeImage)
        {
            try
            {
                Type type = fadeImage.GetType();

                if (!(fadeImage.FindName("ImageHolder") is Grid holder))
                {
                    return;
                }

                if (holder.Effect != null)
                {
                    return;
                }

                if (!(ReadDp(fadeImage, type, "IsBlurEnabledProperty") is bool enabled) || !enabled)
                {
                    return;
                }

                int radius = ReadDp(fadeImage, type, "BlurAmountProperty") is int amount
                    ? amount
                    : 10;

                bool highQuality =
                    ReadDp(fadeImage, type, "HighQualityBlurProperty") is bool hq && hq;

                holder.Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                    Radius = radius,
                    RenderingBias = highQuality
                        ? System.Windows.Media.Effects.RenderingBias.Quality
                        : System.Windows.Media.Effects.RenderingBias.Performance
                };

                Logger.Debug("ImageRotater: pre-attached the background blur");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ImageRotater: could not pre-attach the blur");
            }
        }

        // A dependency property's value, found by the name of its public
        // static field - no compile-time reference to Playnite's internals.
        private static object ReadDp(UserControl control, Type type, string fieldName)
        {
            var field = type.GetField(fieldName);

            return field?.GetValue(null) is DependencyProperty dp
                ? control.GetValue(dp)
                : null;
        }

        // Longer debounce than Playnite's default 150ms, so rapid selection
        // scrolling coalesces into one transition instead of queueing several.
        private static void SetSourceDelay(UserControl fadeImage)
        {
            try
            {
                fadeImage.GetType()
                    .GetProperty("SourceUpdateDelay")
                    ?.SetValue(fadeImage, 250.0);
            }
            catch (Exception)
            {
                // A rename makes this a no-op, never a break.
            }
        }

        private static bool TryRetime(UserControl fadeImage)
        {
            // These are re-asserted on every scan, cheaply: Playnite rewrites
            // SourceUpdateDelay when views change, and a rebuilt template can
            // arrive with no effect attached again.
            EnsureEffect(fadeImage);
            SetSourceDelay(fadeImage);

            if (Patched.TryGetValue(fadeImage, out _))
            {
                return false;
            }

            try
            {
                bool ok =
                    Ease(fadeImage, "Image1FadeIn", System.Windows.Media.Animation.EasingMode.EaseOut) &
                    Ease(fadeImage, "Image2FadeIn", System.Windows.Media.Animation.EasingMode.EaseOut) &
                    Ease(fadeImage, "Image1FadeOut", System.Windows.Media.Animation.EasingMode.EaseIn) &
                    Ease(fadeImage, "Image2FadeOut", System.Windows.Media.Animation.EasingMode.EaseIn);

                if (ok)
                {
                    Patched.Add(fadeImage, null);
                    Logger.Debug("ImageRotater: retimed a FadeImage crossfade");
                }

                return ok;
            }
            catch (Exception ex)
            {
                // A sealed storyboard, a renamed resource - either way, this
                // instance keeps stock behaviour and nothing is harmed.
                Logger.Warn(ex, "ImageRotater: FadeImage retime skipped");
                return false;
            }
        }

        private static bool Ease(
            UserControl fadeImage, string key, System.Windows.Media.Animation.EasingMode mode)
        {
            if (!(fadeImage.Resources[key] is Storyboard storyboard) ||
                storyboard.IsSealed)
            {
                return false;
            }

            foreach (Timeline timeline in storyboard.Children)
            {
                if (timeline is DoubleAnimation animation && !animation.IsSealed)
                {
                    // Timing untouched, deliberately - BeginTime stays zero and
                    // the duration stays stock, so there is no pending-clock
                    // state for Playnite's Stop/Begin churn to strand.
                    animation.BeginTime = TimeSpan.Zero;
                    animation.EasingFunction =
                        new System.Windows.Media.Animation.CubicEase { EasingMode = mode };
                }
            }

            return true;
        }
    }
}
