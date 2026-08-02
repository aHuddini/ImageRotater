namespace ImageRotater.Models
{
    // Decode widths are bucketed so ordinary resizes stay within a bucket and
    // hit the cache instead of forcing a re-decode. DecodePixelWidth is applied
    // before EndInit() and a frozen bitmap cannot be resized, so every width
    // change costs a full decode.
    public static class WidthBucket
    {
        public static readonly int[] Buckets = { 480, 960, 1920, 3840 };

        public static int ForWidth(double measuredWidth)
        {
            // Layout reports 0/NaN before the first measure pass. Falling back
            // to the smallest bucket is safe; falling back to 0 would tell WPF
            // "decode at full size".
            if (double.IsNaN(measuredWidth) || double.IsInfinity(measuredWidth) || measuredWidth <= 0)
            {
                return Buckets[0];
            }

            for (int i = 0; i < Buckets.Length; i++)
            {
                if (measuredWidth <= Buckets[i])
                {
                    return Buckets[i];
                }
            }

            return Buckets[Buckets.Length - 1];
        }
    }
}
