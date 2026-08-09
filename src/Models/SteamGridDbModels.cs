using System.Collections.Generic;

namespace ImageRotater.Models
{
    // Which kind of artwork to fetch. SteamGridDB calls backgrounds "heroes".
    public enum SteamGridDbArtworkType
    {
        Hero,   // Wide background art - what ImageRotater displays today
        Grid,   // Vertical/horizontal cover art
        Logo
    }

    // One artwork result. Only the fields ImageRotater actually uses: enough to
    // show a thumbnail, filter the list, and download the full image.
    public class SteamGridDbArtwork : ObservableObject
    {
        private bool isSelected;

        // Ticked in the results list. Selection lives on the model rather than
        // in ListBox.SelectedItems so it survives filtering: applying a filter
        // rebuilds the list, and anything already ticked but filtered out would
        // otherwise be silently dropped from the download.
        //
        // It also makes multi-select discoverable. Ctrl-click worked, but
        // nothing on screen said so, and it is awkward with a controller.
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                OnPropertyChanged();
            }
        }

        public int Id { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Style { get; set; }
        public string Mime { get; set; }

        // Content flags, so a user can exclude what they do not want to see.
        public bool Nsfw { get; set; }
        public bool Humor { get; set; }
        public bool Epilepsy { get; set; }

        // True for results that came from a web image search rather than
        // SteamGridDB.
        //
        // Their Id is only a position within one search's results, so it cannot
        // be used to name a file - two searches would both produce "1". The
        // downloader hashes the URL for those instead.
        public bool IsFromWeb { get; set; }

        // "1920x1080" - used as a filter value and shown in the UI.
        public string Dimensions
        {
            get { return Width + "x" + Height; }
        }

        // True when the file actually moves.
        //
        // The thumbnail extension leads, because the MIME cannot answer this.
        // SteamGridDB reports animated WebP as "image/webp" - identical to a
        // still WebP - so matching the MIME alone badged every static WebP
        // cover as animated. What distinguishes them is the thumbnail: animated
        // entries get a .webm thumb, stills get .jpg or .png.
        //
        // Sampled to be sure rather than assumed: 23 of 23 animated results
        // across two games were image/webp with .webm thumbs, and none were
        // GIF. WPF decodes neither of those - its codec list is BMP, GIF, Icon,
        // JPEG, PNG, TIFF, WMP - which is why these need converting before they
        // can be shown moving at all.
        public bool IsAnimated
        {
            get
            {
                if (IsGif)
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(ThumbnailUrl) &&
                    ThumbnailUrl.IndexOf(".webm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                return !string.IsNullOrEmpty(Mime)
                    && (Mime.IndexOf("apng", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || Mime.IndexOf("webm", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || Mime.IndexOf("mp4", System.StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        // Playable directly by XamlAnimatedGif, which handles GIF and nothing
        // else.
        public bool IsGif
        {
            get
            {
                return !string.IsNullOrEmpty(Mime)
                    && Mime.IndexOf("gif", System.StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }


        // The URL worth fetching to SHOW motion.
        //
        // For an animated SteamGridDB entry that is the .webm thumbnail rather
        // than the full .webp: it is what actually contains video, and it is
        // far smaller than the full artwork - which matters when the point is
        // a preview rather than the download.
        public string MotionPreviewUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(ThumbnailUrl) &&
                    ThumbnailUrl.IndexOf(".webm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ThumbnailUrl;
                }

                return Url;
            }
        }

        // For XamlAnimatedGif's SourceUri, which wants a Uri rather than a
        // string. Null when the URL cannot parse - the binding then does
        // nothing rather than throwing inside a template.
        public System.Uri UrlUri
        {
            get
            {
                System.Uri parsed;
                return System.Uri.TryCreate(Url, System.UriKind.Absolute, out parsed) ? parsed : null;
            }
        }
    }

    // A game matched on SteamGridDB, used to resolve a Playnite game to an id
    // before fetching its artwork.
    public class SteamGridDbGame
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    // Result of any call: either data or a reason it failed. Network work fails
    // routinely (no key, rate limit, offline), and callers need to tell the
    // user which - so failure is part of the return value rather than an
    // exception to catch at every call site.
    public class SteamGridDbResult<T>
    {
        public bool Success { get; private set; }
        public T Data { get; private set; }
        public string Error { get; private set; }

        public static SteamGridDbResult<T> Ok(T data)
        {
            return new SteamGridDbResult<T> { Success = true, Data = data };
        }

        public static SteamGridDbResult<T> Fail(string error)
        {
            return new SteamGridDbResult<T> { Success = false, Error = error };
        }
    }
}
