using System;
using System.Collections.Generic;
using System.Linq;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // What the user has chosen to narrow a result list by. Empty selections
    // mean "no constraint" rather than "match nothing" - a filter nobody has
    // touched must not hide everything.
    public class ArtworkFilterState
    {
        public HashSet<string> Dimensions { get; set; }
        public HashSet<string> Styles { get; set; }

        public bool ShowNsfw { get; set; }
        public bool ShowHumor { get; set; }
        public bool ShowEpilepsy { get; set; }

        // Animated formats cannot be rendered yet, so they are hidden unless
        // the user deliberately asks to see them.
        public bool ShowAnimated { get; set; }

        public ArtworkFilterState()
        {
            Dimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Styles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Filters artwork results, and derives the available filter options FROM
    // those results.
    //
    // Deriving the options is the point. The reference plugin keeps hardcoded
    // option lists and also clones persisted ones into the same combo boxes,
    // so options appear twice and can offer values no result actually has.
    // Options computed from the current results cannot duplicate and cannot go
    // stale.
    public static class ArtworkFilter
    {
        // Distinct dimensions present in these results, largest first - a user
        // hunting for a background almost always wants the biggest.
        public static IReadOnlyList<string> AvailableDimensions(IReadOnlyList<SteamGridDbArtwork> artwork)
        {
            if (artwork == null)
            {
                return new string[0];
            }

            return artwork
                .Where(a => a != null && a.Width > 0 && a.Height > 0)
                .Select(a => new { a.Dimensions, Area = (long)a.Width * a.Height })
                .GroupBy(x => x.Dimensions, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.First().Area)
                .Select(g => g.Key)
                .ToList();
        }

        public static IReadOnlyList<string> AvailableStyles(IReadOnlyList<SteamGridDbArtwork> artwork)
        {
            if (artwork == null)
            {
                return new string[0];
            }

            return artwork
                .Where(a => a != null && !string.IsNullOrEmpty(a.Style))
                .Select(a => a.Style)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<SteamGridDbArtwork> Apply(
            IReadOnlyList<SteamGridDbArtwork> artwork,
            ArtworkFilterState filter)
        {
            if (artwork == null)
            {
                return new SteamGridDbArtwork[0];
            }

            if (filter == null)
            {
                return artwork;
            }

            var result = new List<SteamGridDbArtwork>();

            foreach (SteamGridDbArtwork item in artwork)
            {
                if (item == null)
                {
                    continue;
                }

                // An untouched dimension/style selection imposes no constraint.
                if (filter.Dimensions.Count > 0 && !filter.Dimensions.Contains(item.Dimensions))
                {
                    continue;
                }

                if (filter.Styles.Count > 0 &&
                    (string.IsNullOrEmpty(item.Style) || !filter.Styles.Contains(item.Style)))
                {
                    continue;
                }

                // Content flags are opt-in: hidden unless explicitly enabled.
                if (item.Nsfw && !filter.ShowNsfw)
                {
                    continue;
                }

                if (item.Humor && !filter.ShowHumor)
                {
                    continue;
                }

                if (item.Epilepsy && !filter.ShowEpilepsy)
                {
                    continue;
                }

                if (item.IsAnimated && !filter.ShowAnimated)
                {
                    continue;
                }

                result.Add(item);
            }

            return result;
        }
    }
}
