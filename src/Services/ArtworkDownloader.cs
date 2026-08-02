using System;
using System.IO;
using System.Threading.Tasks;
using Playnite.SDK;
using ImageRotater.Models;

namespace ImageRotater.Services
{
    // Downloads chosen artwork into a game's ImageRotater folder, so it becomes
    // a normal candidate like any manually added file.
    public class ArtworkDownloader
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly ISteamGridDbClient _client;
        private readonly GameImageStore _store;
        private readonly SessionSelectionCache _sessionCache;

        public ArtworkDownloader(
            ISteamGridDbClient client,
            GameImageStore store,
            SessionSelectionCache sessionCache)
        {
            _client = client;
            _store = store;
            _sessionCache = sessionCache;
        }

        // Saves one artwork into the game's folder. Returns the resulting path,
        // or null on failure.
        public async Task<string> DownloadAsync(
            Guid gameId,
            SteamGridDbArtwork artwork,
            ArtworkKind kind = ArtworkKind.Background)
        {
            if (artwork == null || string.IsNullOrEmpty(artwork.Url))
            {
                return null;
            }

            SteamGridDbResult<byte[]> download = await _client.DownloadAsync(artwork.Url).ConfigureAwait(false);
            if (!download.Success || download.Data == null || download.Data.Length == 0)
            {
                Logger.Warn($"ImageRotater: artwork {artwork.Id} download failed: {download.Error}");
                return null;
            }

            try
            {
                string folder = _store.GetGameFolder(gameId, kind);
                Directory.CreateDirectory(folder);

                // Named by SteamGridDB id so re-downloading the same artwork
                // replaces it rather than accumulating near-duplicates.
                //
                // Web results have no such id - theirs is just a position in
                // one search's results, so two different searches would both
                // yield "1" and silently overwrite each other. Those are named
                // from a hash of the URL instead, which is stable for the same
                // image and distinct for different ones.
                string fileName = artwork.IsFromWeb
                    ? "web_" + StableHash(artwork.Url) + GetExtension(artwork)
                    : "sgdb_" + artwork.Id + GetExtension(artwork);
                string target = Path.Combine(folder, fileName);

                // Write to a temp file then move, so an interrupted download
                // cannot leave a half-written image that renders as broken.
                string temp = target + ".tmp";
                File.WriteAllBytes(temp, download.Data);

                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(temp, target);

                // The candidate list changed, so a remembered choice for this
                // game is stale.
                _sessionCache?.Forget(gameId);

                return target;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"ImageRotater: could not save artwork {artwork.Id}");
                return null;
            }
        }

        // Extension from the MIME type, defaulting to .jpg. SteamGridDB serves
        // jpg/png/webp; the URL may carry query parameters, so the MIME is the
        // more reliable source.
        // Short, filename-safe, and the same every time for the same URL.
        //
        // Deliberately not string.GetHashCode(): that is not guaranteed stable
        // across runs, so the same image would land under a different name
        // after a restart and accumulate duplicates.
        private static string StableHash(string value)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));

                var text = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    text.Append(hash[i].ToString("x2"));
                }

                return text.ToString();
            }
        }

        private static string GetExtension(SteamGridDbArtwork artwork)
        {
            if (!string.IsNullOrEmpty(artwork.Mime))
            {
                // gif before png: "image/gif" must never fall through, or the
                // saved file loses the extension the poster/playback routing
                // keys on. The same reasoning covers video: a .webm saved as
                // .jpg would neither play nor be recognised as motion.
                if (artwork.Mime.IndexOf("gif", StringComparison.OrdinalIgnoreCase) >= 0) return ".gif";
                if (artwork.Mime.IndexOf("webm", StringComparison.OrdinalIgnoreCase) >= 0) return ".webm";
                if (artwork.Mime.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) >= 0) return ".mp4";
                if (artwork.Mime.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0) return ".png";
                if (artwork.Mime.IndexOf("webp", StringComparison.OrdinalIgnoreCase) >= 0) return ".webp";
                if (artwork.Mime.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0) return ".jpg";
                if (artwork.Mime.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0) return ".jpg";
            }

            try
            {
                string fromUrl = Path.GetExtension(new Uri(artwork.Url).AbsolutePath);
                if (!string.IsNullOrEmpty(fromUrl))
                {
                    return fromUrl;
                }
            }
            catch (Exception)
            {
                // Malformed URL - fall through to the default.
            }

            return ".jpg";
        }
    }
}
