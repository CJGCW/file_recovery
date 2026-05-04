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
}
