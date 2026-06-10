using System.IO;
using System.Text.Json;

namespace FileRecoveryParser.Services;

public class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileRecoveryParser", "keys.json");

    public string? GoogleVisionKey { get; set; }
    public string? TmdbKey         { get; set; }

    /// <summary>Persisted state of the "Auto-apply matches" checkbox on the Scan-for-tags action bar.</summary>
    public bool AutoApplyScanResults { get; set; }

    /// <summary>Comma-separated folder names to skip during folder scan. Matched
    /// against each directory's name (case-insensitive). Default "rec" so newly
    /// created recovery-output folders don't get scanned automatically.</summary>
    public string ExcludedFolderNames { get; set; } = "rec";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
