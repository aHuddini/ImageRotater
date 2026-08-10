using System;

namespace ImageRotater.Models
{
    // One YouTube search result.
    //
    // Kept separate from SteamGridDbArtwork rather than folded into it: a video
    // has a duration, a channel and a view count, and no width or height until
    // it has actually been downloaded. Forcing it into the artwork model would
    // mean a row of properties that are meaningless on one side or the other.
    public class YouTubeVideo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Channel { get; set; }
        public string ThumbnailUrl { get; set; }
        public string Url { get; set; }

        public long DurationSeconds { get; set; }
        public long ViewCount { get; set; }

        // Shown on the tile. Video length matters more than usual here: a
        // background loops, so a 20-second clip reads very differently from a
        // ten-minute one, and the length is the only hint before downloading.
        public string DurationText
        {
            get
            {
                if (DurationSeconds <= 0)
                {
                    return string.Empty;
                }

                TimeSpan span = TimeSpan.FromSeconds(DurationSeconds);

                return span.TotalHours >= 1
                    ? string.Format("{0}:{1:mm\\:ss}", (int)span.TotalHours, span)
                    : string.Format("{0}:{1:00}", (int)span.TotalMinutes, span.Seconds);
            }
        }

        public string ViewCountText
        {
            get
            {
                if (ViewCount <= 0)
                {
                    return string.Empty;
                }

                if (ViewCount >= 1000000)
                {
                    return (ViewCount / 1000000.0).ToString("0.#") + "M views";
                }

                if (ViewCount >= 1000)
                {
                    return (ViewCount / 1000.0).ToString("0.#") + "K views";
                }

                return ViewCount + " views";
            }
        }
    }
}
