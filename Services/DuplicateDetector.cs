using System.IO;
using System.Security.Cryptography;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

public static class DuplicateDetector
{
    /// <summary>
    /// Returns groups of files whose content is identical (same size AND same SHA-256).
    /// Each group contains two or more FileRecords.
    /// </summary>
    public static Task<List<List<FileRecord>>> FindAsync(
        IReadOnlyList<FileRecord> files,
        Action<string>? progress,
        CancellationToken ct) => Task.Run(() =>
    {
        // Phase 1 — bucket by size (O(n), no IO)
        var bySize = files
            .Where(f => f.FileSizeBytes > 0 && File.Exists(f.FullPath))
            .GroupBy(f => f.FileSizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        if (bySize.Count == 0) return [];

        // Phase 2 — hash only the candidates that share a size
        int total     = bySize.Sum(g => g.Count());
        int processed = 0;
        var result    = new List<List<FileRecord>>();

        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();

            var hashMap = new Dictionary<string, List<FileRecord>>(StringComparer.Ordinal);

            foreach (var record in sizeGroup)
            {
                ct.ThrowIfCancellationRequested();

                var hash = HashFile(record.FullPath);
                if (hash is null) continue;

                if (!hashMap.TryGetValue(hash, out var list))
                    hashMap[hash] = list = [];
                list.Add(record);

                processed++;
                if (processed % 20 == 0)
                    progress?.Invoke($"Checking for duplicates… {processed} / {total}");
            }

            foreach (var group in hashMap.Values.Where(g => g.Count > 1))
                result.Add(group);
        }

        progress?.Invoke($"Done — checked {processed} file(s).");
        return result;
    }, ct);

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536);
            var bytes = SHA256.HashData(stream);
            return Convert.ToHexString(bytes);
        }
        catch { return null; }
    }
}
