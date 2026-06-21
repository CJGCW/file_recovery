using System.IO;
using System.Text.Json;

namespace FileRecoveryParser.Services;

/// <summary>
/// Persistent cache of whole-frame perceptual hashes for videos that have
/// been deep-scanned. Stored in LocalAppData as a single JSON file keyed by
/// the file's full path; validated on read by file size + last-write time
/// so any modification to the source video invalidates the cached entry.
/// </summary>
/// <remarks>
/// Why this exists: scan-for-tags re-decodes every frame of every video on
/// every run. For 2000 videos that's hours of ffmpeg time even when the
/// content hasn't changed. With this cache, repeat tag-scans of already-
/// scanned files do a O(N×M) hash comparison against in-memory data
/// instead of relaunching ffmpeg — typical 20-50× speedup.
///
/// Limitations of this first cut:
/// - Only whole-frame dHash is cached. Crop dHash + face embedding caching
///   would need per-frame BitmapSource access, which we don't have once a
///   cache hit shortcuts decoding. Crop / embedding matching falls back to
///   "if you want it, force-rescan the file."
/// - On a cache-hit tag-scan, ScanResult.ThumbnailPng is null (we don't have
///   the bmp to encode). The Scan Results window will render the row with a
///   "cached" badge instead of a thumbnail.
/// </remarks>
public class ScannedVideoStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, ScannedVideo> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    // Batch writes: a tag-scan run can call Save() N times. Rewriting the
    // full JSON each time = N × file_size disk writes. Instead we accumulate
    // in memory and flush every PersistEveryNSaves OR every PersistInterval,
    // whichever fires first. Callers can force a flush via Flush().
    private const int PersistEveryNSaves = 25;
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(10);
    private int      _unflushed;
    private DateTime _lastFlushUtc = DateTime.UtcNow;

    public ScannedVideoStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileRecoveryParser");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "scanned_videos.json");
        Load();
    }

    /// <summary>
    /// Look up a previously-cached scan. Returns null when:
    /// - the path has no cached entry, OR
    /// - the file no longer exists on disk, OR
    /// - the file's size or last-write time differs from the cached values
    ///   (i.e. the file was modified since the cache was written).
    /// </summary>
    public ScannedVideo? TryGet(string fullPath)
    {
        ScannedVideo? entry;
        lock (_lock) _byPath.TryGetValue(fullPath, out entry);
        if (entry is null) return null;

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;
            if (info.Length != entry.FileSize) return null;
            if (Math.Abs((info.LastWriteTimeUtc - entry.LastModifiedUtc).TotalSeconds) > 1)
                return null;
            return entry;
        }
        catch { return null; }
    }

    /// <summary>
    /// Persist (or overwrite) the cached frames for a single video. Called
    /// at the end of a fresh deep-scan, once we know we have a complete set.
    /// </summary>
    public void Save(string fullPath, IEnumerable<(TimeSpan Position, ulong Hash)> frames)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists) return;
        }
        catch { return; }

        var entry = new ScannedVideo
        {
            FullPath        = fullPath,
            FileSize        = info.Length,
            LastModifiedUtc = info.LastWriteTimeUtc,
            ScannedAtUtc    = DateTime.UtcNow,
            Frames          = frames
                .Select(f => new ScannedFrame
                {
                    PositionSeconds = f.Position.TotalSeconds,
                    Hash            = f.Hash,
                })
                .ToList(),
        };

        lock (_lock)
        {
            _byPath[fullPath] = entry;
            _unflushed++;
            if (_unflushed >= PersistEveryNSaves ||
                DateTime.UtcNow - _lastFlushUtc >= PersistInterval)
            {
                PersistLocked();
                _unflushed = 0;
                _lastFlushUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Drop the cached entry for a single path, e.g. after a delete
    /// or move. Persists immediately so the catalog doesn't grow with stale
    /// entries.</summary>
    public void Remove(string fullPath)
    {
        lock (_lock)
        {
            if (!_byPath.Remove(fullPath)) return;
            PersistLocked();
            _unflushed = 0;
            _lastFlushUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Force any buffered saves to disk. Call this at the end of a
    /// batch operation (e.g. after a scan-for-tags run) so a crash before
    /// the next threshold-fire doesn't lose the buffered entries.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_unflushed == 0) return;
            PersistLocked();
            _unflushed = 0;
            _lastFlushUtc = DateTime.UtcNow;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<ScannedVideo>>(json) ?? [];
            var dict = new Dictionary<string, ScannedVideo>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list)
                if (!string.IsNullOrEmpty(e.FullPath))
                    dict[e.FullPath] = e;
            lock (_lock) _byPath = dict;
        }
        catch
        {
            lock (_lock) _byPath = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Must be called with _lock held. Atomic write: serialize → tmp file →
    // File.Move with overwrite, so a crash mid-write can't corrupt the real
    // cache file.
    private void PersistLocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_byPath.Values,
                new JsonSerializerOptions { WriteIndented = false });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
}

public class ScannedVideo
{
    public string   FullPath        { get; set; } = string.Empty;
    public long     FileSize        { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public DateTime ScannedAtUtc    { get; set; }
    public List<ScannedFrame> Frames { get; set; } = [];
}

public class ScannedFrame
{
    public double PositionSeconds { get; set; }
    public ulong  Hash            { get; set; }
}
