using System.Collections.Generic;
using Playnite.SDK.Models;

namespace ImageRotater.Services
{
    // The extension seam. v0.1 ships one implementation reading Playnite's own
    // BackgroundImage. Plugin-owned folders and downloaded art become further
    // implementations behind this interface, composed by a merging source,
    // without touching rendering.
    public interface IBackgroundImageSource
    {
        // All candidate image paths for this game. Never null; may be empty.
        IReadOnlyList<string> GetImagePaths(Game game);
    }
}
