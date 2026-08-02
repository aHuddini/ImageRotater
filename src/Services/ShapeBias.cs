using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace ImageRotater.Services
{
    // Screen shape and image-header shape, for the letterboxer to compare.
    //
    // This once also filtered rotation candidates toward screen-shaped images,
    // but that filter dominated: with it on, a game's odd-shaped artwork
    // simply never appeared. Letterboxing solves the same flash without hiding
    // anything, so the filter went and the shape arithmetic stayed.
    public static class ShapeBias
    {
        // Keyed by path, invalidated by write time: downloads overwrite files
        // under the same name, and a cached ratio for replaced content would
        // mis-bias until restart. Same invalidation rule the letterbox cache
        // uses.
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, double>> AspectCache =
            new ConcurrentDictionary<string, Tuple<DateTime, double>>(StringComparer.OrdinalIgnoreCase);

        // The shape backgrounds are displayed at. DIPs, but a ratio cancels the
        // scaling out.
        public static double ScreenAspect
        {
            get
            {
                double h = System.Windows.SystemParameters.PrimaryScreenHeight;
                return h > 0 ? System.Windows.SystemParameters.PrimaryScreenWidth / h : 16.0 / 9.0;
            }
        }

        // Width/height ratio from the file header, without decoding pixels.
        // Zero when unreadable, which the filter treats as "not a match" -
        // an unreadable file should not be excluded from the fallback list,
        // just never preferred.
        public static double AspectOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(path);

                Tuple<DateTime, double> cached;
                if (AspectCache.TryGetValue(path, out cached) && cached.Item1 == stamp)
                {
                    return cached.Item2;
                }

                double aspect;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (Image image = Image.FromStream(stream, false, false))
                {
                    aspect = image.Height > 0 ? (double)image.Width / image.Height : 0;
                }

                AspectCache[path] = Tuple.Create(stamp, aspect);
                return aspect;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
