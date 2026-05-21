using System.IO;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

public static class ImageClassifier
{
    private static readonly HashSet<string> IconSegments = new(StringComparer.OrdinalIgnoreCase)
        { "icons", "icon", "resources", "res", "toolbar", "toolbaricons", "ico" };

    private static readonly string[] ScreenKeywords = ["screenshot", "screenshots", "screengrabs", "screen shot", "capture"];
    private static readonly string[] WallKeywords   = ["wallpaper", "wallpapers", "background", "backgrounds"];
    private static readonly string[] GameKeywords   = ["textures", "texture", "sprites", "sprite", "gamedata", "game_data", "assets"];

    public static ImageSubcategory Classify(string fullPath, string extension)
    {
        var ext = extension.ToLowerInvariant();
        if (ext is ".ico" or ".icns") return ImageSubcategory.Icon;
        if (ext == ".png" && IsPngIcon(fullPath)) return ImageSubcategory.Icon;

        var segs = fullPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var seg in segs)
        {
            if (IconSegments.Contains(seg)) return ImageSubcategory.Icon;

            var s = seg.ToLowerInvariant();
            foreach (var kw in ScreenKeywords) if (s.Contains(kw)) return ImageSubcategory.Screenshot;
            foreach (var kw in WallKeywords)   if (s.Contains(kw)) return ImageSubcategory.Wallpaper;
            foreach (var kw in GameKeywords)   if (s.Contains(kw)) return ImageSubcategory.GameAsset;
        }

        return ImageSubcategory.Other;
    }

    // Reads 26 bytes of the PNG IHDR to detect square, ≤512px, alpha-channel images.
    // These are the hallmarks of app icons shipped as PNG.
    private static bool IsPngIcon(string path)
    {
        try
        {
            Span<byte> hdr = stackalloc byte[26];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 26, FileOptions.SequentialScan);
            if (fs.Read(hdr) < 26) return false;

            // PNG signature
            if (hdr[0] != 0x89 || hdr[1] != 0x50 || hdr[2] != 0x4E || hdr[3] != 0x47)
                return false;

            // IHDR: width [16..19], height [20..23] — big-endian uint32
            uint w = (uint)(hdr[16] << 24 | hdr[17] << 16 | hdr[18] << 8 | hdr[19]);
            uint h = (uint)(hdr[20] << 24 | hdr[21] << 16 | hdr[22] << 8 | hdr[23]);

            // Color type [25]: bit 2 set = alpha (types 4 = greyscale+alpha, 6 = RGBA)
            bool hasAlpha = (hdr[25] & 0x04) != 0;
            if (!hasAlpha) return false;

            uint maxDim = Math.Max(w, h);
            uint minDim = Math.Min(w, h);
            if (maxDim == 0 || maxDim > 512) return false;

            // Square within 5%
            return (float)minDim / maxDim > 0.95f;
        }
        catch { return false; }
    }
}
