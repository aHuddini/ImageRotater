using System.Collections.Generic;
using Playnite.SDK.Models;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Reads plugin-owned images from the per-game folder for one artwork kind.
    public class FolderImageSource : IBackgroundImageSource
    {
        private static readonly IReadOnlyList<string> Empty = new string[0];

        private readonly GameImageStore _store;
        private readonly ArtworkKind _kind;

        public FolderImageSource(GameImageStore store, ArtworkKind kind)
        {
            _store = store;
            _kind = kind;
        }

        public IReadOnlyList<string> GetImagePaths(Game game)
        {
            if (game == null || _store == null)
            {
                return Empty;
            }

            return _store.GetImagePaths(game.Id, _kind);
        }
    }
}
