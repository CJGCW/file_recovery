using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Services;

/// <summary>
/// 64-bit difference hash (dHash). Resilient to small variations in
/// resolution, compression, and minor color shifts — good for matching
/// the same visual content (logos, title cards, recurring scenes)
/// across different files.
/// </summary>
public static class PerceptualHashService
{
    /// <summary>
    /// Computes a 64-bit dHash by downscaling to 9×8 grayscale and
    /// emitting one bit per horizontal pixel-pair (8 bits × 8 rows = 64).
    /// </summary>
    public static ulong Compute(BitmapSource? src)
    {
        if (src is null) return 0;

        try
        {
            var grey   = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            var scaleX = 9.0 / grey.PixelWidth;
            var scaleY = 8.0 / grey.PixelHeight;
            var scaled = new TransformedBitmap(grey, new ScaleTransform(scaleX, scaleY));

            // Re-render at exact 9×8 to guarantee a consistent grid regardless of
            // rounding in TransformedBitmap.
            var rect = new System.Windows.Int32Rect(0, 0,
                Math.Min(scaled.PixelWidth, 9), Math.Min(scaled.PixelHeight, 8));
            if (rect.Width < 9 || rect.Height < 8) return 0;

            var pixels = new byte[9 * 8];
            scaled.CopyPixels(rect, pixels, 9, 0);

            ulong hash = 0;
            int   bit  = 63;
            for (int y = 0; y < 8; y++)
            {
                int rowStart = y * 9;
                for (int x = 0; x < 8; x++)
                {
                    if (pixels[rowStart + x] > pixels[rowStart + x + 1])
                        hash |= 1UL << bit;
                    bit--;
                }
            }
            return hash;
        }
        catch { return 0; }
    }

    public static int HammingDistance(ulong a, ulong b) =>
        BitOperations.PopCount(a ^ b);
}
