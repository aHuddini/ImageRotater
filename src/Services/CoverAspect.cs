using System;
using System.Collections.Generic;
using System.Linq;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Picks cover art by SHAPE first, resolution second.
    //
    // Backgrounds and covers need different rules. A background fills the
    // screen, so more pixels is always better. A cover is drawn into a
    // fixed-aspect card, so a correctly-shaped 600x900 looks right where a
    // sharper 3840x2160 gets letterboxed or cropped. Ranking covers by size
    // alone is how you end up with a crisp image of the wrong shape.
    public static class CoverAspect
    {
        // Playnite's default grid cell is 2:3. The real ratio is read from the
        // user's own settings at runtime; this is only the fallback when that
        // is unavailable or nonsensical.
        public const double DefaultAspect = 2.0 / 3.0;

        // Turns Playnite's configured grid ratio into a width/height number.
        // Guards against zero or negative values, which would otherwise produce
        // a NaN or infinite target that matches nothing.
        public static double FromGridRatio(int widthRatio, int heightRatio)
        {
            if (widthRatio <= 0 || heightRatio <= 0)
            {
                return DefaultAspect;
            }

            return (double)widthRatio / heightRatio;
        }

        // How far an image's shape is from the target, as a relative
        // difference. 0 is an exact match. Using a ratio rather than a raw
        // subtraction keeps the measure meaningful across very different sizes.
        public static double AspectDistance(int width, int height, double targetAspect)
        {
            if (width <= 0 || height <= 0 || targetAspect <= 0)
            {
                return double.MaxValue;
            }

            double aspect = (double)width / height;
            return Math.Abs(aspect - targetAspect) / targetAspect;
        }

        // Best cover from a set: closest shape wins, and among images of
        // effectively the same shape the largest wins.
        //
        // tolerance is how much shape difference counts as "the same shape".
        // 0.05 means within 5% - enough to treat 600x900 and 1000x1500 as
        // equivalent so the larger is chosen, while still rejecting a 16:9
        // image outright.
        public static SteamGridDbArtwork BestCover(
            IReadOnlyList<SteamGridDbArtwork> artwork,
            double targetAspect,
            double tolerance = 0.05)
        {
            if (artwork == null || artwork.Count == 0)
            {
                return null;
            }

            SteamGridDbArtwork best = null;
            double bestDistance = double.MaxValue;
            long bestArea = 0;

            foreach (SteamGridDbArtwork item in artwork)
            {
                if (item == null || item.Width <= 0 || item.Height <= 0)
                {
                    continue;
                }

                // Reject only genuinely WIDE art, not square.
                //
                // SteamGridDB's grid category carries Steam's old capsule
                // banners (920x430, 460x215), which are crops of larger key art
                // and look wrong as box art. But it also carries square art
                // (1024x1024, 512x512) that users legitimately want. An earlier
                // "width >= height" test excluded both, so squares could never
                // be chosen.
                //
                // The threshold sits between square and the widest plausible
                // cover, so a square is kept and a 2:1 banner is not.
                const double WidestAcceptableCover = 1.2;

                if (targetAspect < 1.0 &&
                    (double)item.Width / item.Height > WidestAcceptableCover)
                {
                    continue;
                }

                double distance = AspectDistance(item.Width, item.Height, targetAspect);
                long area = (long)item.Width * item.Height;

                if (best == null)
                {
                    best = item;
                    bestDistance = distance;
                    bestArea = area;
                    continue;
                }

                // Clearly better shape wins outright.
                if (distance < bestDistance - tolerance)
                {
                    best = item;
                    bestDistance = distance;
                    bestArea = area;
                    continue;
                }

                // Same shape within tolerance: format first, then size.
                //
                // A lossless cover survives being scaled up by a Fullscreen
                // theme; a JPEG of the same shape shows its compression once
                // the tile is big enough. Only applied when the shapes already
                // match, so this never trades the right proportions away.
                if (Math.Abs(distance - bestDistance) <= tolerance)
                {
                    bool itemLossless = IsLossless(item);
                    bool bestLossless = IsLossless(best);

                    bool better = itemLossless != bestLossless
                        // A lossy image has to be substantially bigger to beat
                        // a lossless one, matching the background rule.
                        ? (itemLossless ? area * 14 >= bestArea * 10 : area * 10 > bestArea * 14)
                        : area > bestArea;

                    if (better)
                    {
                        best = item;
                        bestDistance = distance;
                        bestArea = area;
                    }
                }
            }

            return best;
        }

        // PNG and WebP keep their pixels; JPEG does not.
        //
        // Judged by MIME first and URL second: SteamGridDB reports the type,
        // but a web search result often has only the file name to go on.
        //
        // Public because the background ranking applies the same preference -
        // one definition of "lossless" rather than two that can drift.
        public static bool IsLossless(SteamGridDbArtwork artwork)
        {
            if (artwork == null)
            {
                return false;
            }

            string mime = artwork.Mime ?? string.Empty;
            string url = artwork.Url ?? string.Empty;

            if (mime.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mime.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (mime.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mime.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return url.IndexOf(".png", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf(".webp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Same rule applied to files already on disk, so rotation between a
        // game's stored covers does not swing between shapes. Takes a resolver
        // so this stays testable without touching the filesystem.
        public static IReadOnlyList<string> OrderCoversByFit(
            IReadOnlyList<string> paths,
            Func<string, Tuple<int, int>> measure,
            double targetAspect)
        {
            if (paths == null || paths.Count == 0 || measure == null)
            {
                return paths ?? (IReadOnlyList<string>)new string[0];
            }

            return paths
                .Select(p =>
                {
                    Tuple<int, int> size = measure(p);
                    int w = size != null ? size.Item1 : 0;
                    int h = size != null ? size.Item2 : 0;
                    return new
                    {
                        Path = p,
                        Distance = AspectDistance(w, h, targetAspect),
                        Area = (long)w * h
                    };
                })
                .OrderBy(x => x.Distance)
                .ThenByDescending(x => x.Area)
                .Select(x => x.Path)
                .ToList();
        }
    }
}
