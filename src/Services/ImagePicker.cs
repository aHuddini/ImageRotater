using System;
using System.Collections.Generic;

namespace ImageRotater.Services
{
    // Chooses which of a game's images to show. With no timer in v0.1, this is
    // the whole user-visible rotation behaviour, so it is kept pure and
    // injectable rather than embedded in the control.
    public class ImagePicker
    {
        private readonly Random _random;

        public ImagePicker(Random random = null)
        {
            // One instance reused for the life of the picker. Constructing
            // Random per call seeds from the clock and produces correlated
            // sequences when called in quick succession.
            _random = random ?? new Random();
        }

        // Returns a path from candidates, avoiding previousPick when an
        // alternative exists. Returns null when there is nothing to show.
        public string Pick(IReadOnlyList<string> candidates, string previousPick)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // Build the set of allowed choices once, then pick uniformly from
            // it. A retry loop would be unbounded when every candidate equals
            // previousPick (a list containing duplicates).
            var allowed = new List<string>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                // OrdinalIgnoreCase: Windows paths are case-insensitive, so two
                // spellings of the same file are the same image.
                if (!string.Equals(candidates[i], previousPick, StringComparison.OrdinalIgnoreCase))
                {
                    allowed.Add(candidates[i]);
                }
            }

            if (allowed.Count == 0)
            {
                // Every candidate is the previous pick. Showing it again beats
                // showing nothing.
                return candidates[0];
            }

            return allowed[_random.Next(allowed.Count)];
        }
    }
}
