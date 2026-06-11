using System.IO;
using FileRecoveryParser.Data;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Identifies a file's type from its extension. Handles PhotoRec-style
/// recovery filenames (e.g. <c>f0002227_memdiag_exe</c>) by treating the
/// last underscore-separated suffix as the extension when the file has
/// no real dot-extension. Returns null for unknown extensions — the
/// scanner skips those entirely.
/// </summary>
public class FileTypeDetector
{
    public (string MimeType, FileCategory Category)? Detect(string filePath)
    {
        var ext = GetEffectiveExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;
        return ExtensionMap.Entries.TryGetValue(ext, out var result) ? result : null;
    }

    /// <summary>
    /// Returns a dot-prefixed lowercase extension, or empty string when
    /// nothing recognisable can be inferred.
    /// </summary>
    public static string GetEffectiveExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(ext)) return ext.ToLowerInvariant();

        // PhotoRec convention: trailing _<ext> appended to a name with no dot.
        var name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(name)) return string.Empty;

        int underscore = name.LastIndexOf('_');
        if (underscore <= 0 || underscore >= name.Length - 1) return string.Empty;

        var suffix = name[(underscore + 1)..];
        if (suffix.Length is < 1 or > 5) return string.Empty;
        if (!suffix.All(char.IsLetterOrDigit)) return string.Empty;

        var candidate = "." + suffix.ToLowerInvariant();
        return ExtensionMap.Entries.ContainsKey(candidate) ? candidate : string.Empty;
    }
}
