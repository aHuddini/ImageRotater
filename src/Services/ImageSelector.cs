using System;
using System.Collections.Generic;

namespace ImageRotater.Services
{
    // Applies the user's SelectionMode on top of the random picker.
    //
    // This exists so the mode logic is in one place and unit-testable. Putting
    // it in the control would bury the plugin's main user-visible behaviour in
    // a class that needs a live WPF tree to exercise.
    public class ImageSelector
    {
        private readonly ImagePicker _picker;
        private readonly SessionSelectionCache _sessionCache;

        public ImageSelector(ImagePicker picker, SessionSelectionCache sessionCache)
        {
            _picker = picker;
            _sessionCache = sessionCache;
        }

        // Chooses the image to show. previousPick is only consulted in
        // EverySelection mode, where avoiding an immediate repeat is the point.
        public string Select(
            Guid gameId,
            IReadOnlyList<string> candidates,
            string previousPick,
            SelectionMode mode)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            if (mode == SelectionMode.Fixed)
            {
                // Candidates arrive in a stable sorted order, so "first" means
                // the same file every time rather than whatever the filesystem
                // happened to enumerate first.
                return candidates[0];
            }

            if (mode == SelectionMode.Session)
            {
                string remembered = _sessionCache?.GetRemembered(gameId, candidates);
                if (!string.IsNullOrEmpty(remembered))
                {
                    return remembered;
                }

                // First sight this session: pick, then remember. previousPick is
                // deliberately not passed - there is no "previous" for a game
                // being chosen for the first time, and passing one would
                // needlessly exclude a candidate.
                string picked = _picker != null ? _picker.Pick(candidates, null) : candidates[0];
                _sessionCache?.Remember(gameId, picked);
                return picked;
            }

            return _picker != null ? _picker.Pick(candidates, previousPick) : candidates[0];
        }
    }
}
