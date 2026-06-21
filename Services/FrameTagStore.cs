using System.IO;
using System.Text.Json;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Persists user-tagged frames (perceptual hash + tag name + source pointer)
/// to LocalAppData. Match(hash) returns the names of every tag whose stored
/// frames are within the configured Hamming distance.
/// </summary>
public class FrameTagStore
{
    public const int DefaultMaxDistance     = 10;  // whole-frame / whole-image dHash
    public const int DefaultCropMaxDistance = 6;   // stricter for multi-scale crops; small crops collide too often at 10

    private readonly string _path;

    // Guards every read and write of Frames + every Save(). Tag-frame creation
    // happens on the UI thread; scan-for-tags' ApplyMatchedTags runs on a
    // worker thread. Without the lock, List mutation during JSON enumeration
    // throws InvalidOperationException.
    private readonly object _lock = new();
    public List<TaggedFrame> Frames { get; private set; } = [];

    public FrameTagStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileRecoveryParser");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "frame_tags.json");
        Load();
    }

    public const float DefaultCosineThreshold = 0.4f;

    /// <summary>
    /// Persists a new TaggedFrame and returns it (or null if the inputs were
    /// invalid). Returning the instance lets UI callers append it to their
    /// row list directly instead of having to look it up by Last(), which
    /// would race against concurrent auto-apply during a scan-for-tags run.
    /// </summary>
    public TaggedFrame? Add(ulong hash, string tagName, string sourceFile, TimeSpan position,
                            float[]? embedding = null, byte[]? thumbnailPng = null)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;
        if (hash == 0 && embedding is null) return null;

        var frame = new TaggedFrame
        {
            Hash                  = hash,
            TagName               = tagName.Trim(),
            SourceFile            = sourceFile,
            SourcePositionSeconds = position.TotalSeconds,
            AddedAt               = DateTime.UtcNow,
            Embedding             = embedding,
            ThumbnailPng          = thumbnailPng,
        };
        lock (_lock)
        {
            Frames.Add(frame);
            Save();
        }
        return frame;
    }

    public void Remove(TaggedFrame frame)
    {
        lock (_lock) { if (Frames.Remove(frame)) Save(); }
    }

    public void RemoveMany(IEnumerable<TaggedFrame> frames)
    {
        lock (_lock)
        {
            bool any = false;
            foreach (var f in frames.ToList())
                if (Frames.Remove(f)) any = true;
            if (any) Save();
        }
    }

    public void Rename(TaggedFrame frame, string newName)
    {
        var trimmed = newName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        lock (_lock)
        {
            if (frame.TagName == trimmed) return;
            frame.TagName = trimmed;
            Save();
        }
    }

    /// <summary>
    /// Returns each distinct tag name whose stored frames sit within
    /// <paramref name="maxDistance"/> Hamming bits of <paramref name="hash"/>.
    /// Reads work on a snapshot of Frames so concurrent writers don't trip the
    /// LINQ enumeration.
    /// </summary>
    public IEnumerable<string> Match(ulong hash, int maxDistance = DefaultMaxDistance)
    {
        if (hash == 0) return [];
        TaggedFrame[] snap;
        lock (_lock) { if (Frames.Count == 0) return []; snap = Frames.ToArray(); }
        return snap
            .Where(f => PerceptualHashService.HammingDistance(f.Hash, hash) <= maxDistance)
            .Select(f => f.TagName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the closest stored frame per tag name (best Hamming distance)
    /// for diagnostic logging. Includes near-misses, not just matches.
    /// </summary>
    public IEnumerable<(string TagName, int Distance, TaggedFrame Frame)> BestDistancePerTag(ulong hash)
    {
        if (hash == 0) yield break;
        TaggedFrame[] snap;
        lock (_lock) { if (Frames.Count == 0) yield break; snap = Frames.ToArray(); }
        foreach (var grp in snap.GroupBy(f => f.TagName, StringComparer.OrdinalIgnoreCase))
        {
            TaggedFrame? best = null;
            int bestDist = int.MaxValue;
            foreach (var f in grp)
            {
                int d = PerceptualHashService.HammingDistance(f.Hash, hash);
                if (d < bestDist) { bestDist = d; best = f; }
            }
            if (best is not null) yield return (grp.Key, bestDist, best);
        }
    }

    /// <summary>Returns the closest cosine similarity per tag name (for tags whose frames carry an embedding).</summary>
    public IEnumerable<(string TagName, float Similarity, TaggedFrame Frame)> BestSimilarityPerTag(float[] embedding)
    {
        if (embedding is null || embedding.Length == 0) yield break;
        TaggedFrame[] snap;
        lock (_lock) { if (Frames.Count == 0) yield break; snap = Frames.ToArray(); }
        foreach (var grp in snap.Where(f => f.Embedding is { Length: > 0 })
                                 .GroupBy(f => f.TagName, StringComparer.OrdinalIgnoreCase))
        {
            TaggedFrame? best = null;
            float bestSim = -1;
            foreach (var f in grp)
            {
                float sim = CosineSimilarity(f.Embedding!, embedding);
                if (sim > bestSim) { bestSim = sim; best = f; }
            }
            if (best is not null) yield return (grp.Key, bestSim, best);
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < n; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return magA == 0 || magB == 0 ? 0 : dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<TaggedFrame>>(File.ReadAllText(_path)) ?? [];
            lock (_lock) Frames = loaded;
        }
        catch
        {
            lock (_lock) Frames = [];
        }
    }

    /// <summary>Persist any external mutations to existing TaggedFrame objects
    /// (e.g. ThumbnailPng backfilled from the source file).</summary>
    public void Flush() { lock (_lock) Save(); }

    // Must be called with _lock held. Atomic write: serialize → tmp file →
    // File.Move with overwrite, so a crash mid-write can't corrupt the real
    // file. Thumbnails are base64-encoded inside this JSON, so WriteIndented
    // is off — the file is machine-only, no readability cost.
    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Frames,
                new JsonSerializerOptions { WriteIndented = false });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
}
