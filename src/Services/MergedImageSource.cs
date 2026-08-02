using System.Collections.Generic;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // Combines several sources into one candidate list, in the order given,
    // skipping duplicates. This is the composite the seam was built for:
    // plugin-owned folder images plus Playnite's own background, and later
    // downloaded artwork, all arriving as one list to the renderer.
    public class MergedImageSource : IBackgroundImageSource
    {
        private static readonly IReadOnlyList<string> Empty = new string[0];

        private readonly IReadOnlyList<IBackgroundImageSource> _sources;

        public MergedImageSource(params IBackgroundImageSource[] sources)
        {
            _sources = sources ?? new IBackgroundImageSource[0];
        }

        public IReadOnlyList<string> GetImagePaths(Game game)
        {
            if (game == null || _sources.Count == 0)
            {
                return Empty;
            }

            // Windows paths are case-insensitive, so the dedupe must be too -
            // otherwise the same file reached by two sources would be offered
            // twice and skew the random pick toward it.
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            for (int i = 0; i < _sources.Count; i++)
            {
                IReadOnlyList<string> paths = _sources[i] != null
                    ? _sources[i].GetImagePaths(game)
                    : null;

                if (paths == null)
                {
                    continue;
                }

                for (int j = 0; j < paths.Count; j++)
                {
                    if (!string.IsNullOrEmpty(paths[j]) && seen.Add(paths[j]))
                    {
                        result.Add(paths[j]);
                    }
                }
            }

            return result;
        }
    }
}
