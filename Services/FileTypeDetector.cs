using FileRecoveryParser.Data;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Identifies a file's type purely from its extension.
/// Returns null for files with no extension or an unrecognised one — the
/// scanner will skip those entirely.
/// </summary>
public class FileTypeDetector
{
    public (string MimeType, FileCategory Category)? Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        return ExtensionMap.Entries.TryGetValue(ext, out var result) ? result : null;
    }
}
