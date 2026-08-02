using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageRotater.Services
{
    // Decodes an image at a bucketed width, off the UI thread, and caches the
    // frozen result.
    //
    // All five decode settings below are load-bearing:
    //   DecodePixelWidth  - WIC's JPEG decoder does true DCT-domain scaled
    //                       decoding (1/2, 1/4, 1/8); PNG streams without
    //                       materialising a full-size intermediate. Omitting
    //                       this is the defect this project exists to fix.
    //   OnLoad            - releases the file handle immediately, so users can
    //                       still delete/replace their own images.
    //   IgnoreColorProfile- skips colour-profile work.
    //   Freeze()          - makes the bitmap safe to hand to the UI thread.
    //   Task.Run          - keeps decode off the UI thread.
    public class ImageLoader
    {
        private readonly ImageCache _cache;

        public ImageLoader(ImageCache cache)
        {
            _cache = cache;
        }

        public async Task<BitmapSource> LoadAsync(string path, int bucket)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            // A cache hit must still be backed by a real file. Write mode
            // replaces a game's artwork and deletes the file it replaced, so a
            // decoded bitmap can outlive its source. Returning it would show
            // artwork the user has effectively removed, and the caller would
            // report success for a path that no longer resolves.
            //
            // Decode() checks existence too, but a cache hit never reaches it.
            if (!File.Exists(path))
            {
                _cache?.Forget(path);
                return null;
            }

            BitmapSource cached = _cache != null ? _cache.Get(path, bucket) : null;
            if (cached != null)
            {
                return cached;
            }

            BitmapSource decoded = await Task.Run(() => Decode(path, bucket)).ConfigureAwait(false);

            if (decoded != null && _cache != null)
            {
                _cache.Put(path, bucket, decoded);
            }

            return decoded;
        }

        private static BitmapSource Decode(string path, int bucket)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.DecodePixelWidth = bucket;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception)
            {
                // A missing or corrupt image is a data problem, not a crash.
                // The caller decides what to show; the control keeps its
                // previous image rather than flashing to blank.
                return null;
            }
        }
    }
}
