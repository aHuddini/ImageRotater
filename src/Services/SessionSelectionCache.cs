using System;
using System.Collections.Generic;

namespace ImageRotater.Services
{
    // Remembers which image was chosen for a game, for the lifetime of the
    // Playnite process. This is what makes "pick once per session" actually
    // hold: without it, every revisit re-rolls and the artwork changes while
    // the user is just navigating their library.
    //
    // The decision is stored as a PATH, not an index and not a reference into
    // a candidate list. A list can be reordered or rebuilt underneath us -
    // storing "item 3" or a reference into that list is how a stable-looking
    // choice silently becomes unstable.
    //
    // A remembered path is re-validated against the current candidates on every
    // read, so an image the user deleted cannot pin a game to a missing file.
    public class SessionSelectionCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<Guid, string> _choices = new Dictionary<Guid, string>();

        public int Count
        {
            get { lock (_lock) { return _choices.Count; } }
        }

        // The remembered choice for this game, but only if it is still one of
        // the current candidates. Returns null when there is nothing valid to
        // reuse, which the caller treats as "pick fresh".
        public string GetRemembered(Guid gameId, IReadOnlyList<string> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            string remembered;
            lock (_lock)
            {
                if (!_choices.TryGetValue(gameId, out remembered))
                {
                    return null;
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], remembered, StringComparison.OrdinalIgnoreCase))
                {
                    return remembered;
                }
            }

            // Remembered image is gone (deleted, renamed, folder emptied).
            // Drop it so the next read picks fresh instead of returning a
            // path that no longer resolves.
            Forget(gameId);
            return null;
        }

        public void Remember(Guid gameId, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (_lock)
            {
                _choices[gameId] = path;
            }
        }

        public void Forget(Guid gameId)
        {
            lock (_lock)
            {
                _choices.Remove(gameId);
            }
        }

        // Used when the user changes selection mode, or edits a game's images,
        // so the next render reflects the change immediately.
        public void Clear()
        {
            lock (_lock)
            {
                _choices.Clear();
            }
        }
    }
}
