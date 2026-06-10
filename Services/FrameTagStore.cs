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

    public void Add(ulong hash, string tagName, string sourceFile, TimeSpan position,
                    float[]? embedding = null, byte[]? thumbnailPng = null)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        if (hash == 0 && embedding is null) return;
        Frames.Add(new TaggedFrame
        {
            Hash                  = hash,
            TagName               = tagName.Trim(),
            SourceFile            = sourceFile,
            SourcePositionSeconds = position.TotalSeconds,
            AddedAt               = DateTime.UtcNow,
            Embedding             = embedding,
            ThumbnailPng          = thumbnailPng,
        });
        Save();
    }

    public void Remove(TaggedFrame frame)
    {
        if (Frames.Remove(frame)) Save();
    }

    public void RemoveMany(IEnumerable<TaggedFrame> frames)
    {
        bool any = false;
        foreach (var f in frames.ToList())
            if (Frames.Remove(f)) any = true;
        if (any) Save();
    }

    public void Rename(TaggedFrame frame, string newName)
    {
        var trimmed = newName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        if (frame.TagName == trimmed) return;
        frame.TagName = trimmed;
        Save();
    }

    /// <summary>
    /// Returns each distinct tag name whose stored frames sit within
    /// <paramref name="maxDistance"/> Hamming bits of <paramref name="hash"/>.
    /// </summary>
    public IEnumerable<string> Match(ulong hash, int maxDistance = DefaultMaxDistance)
    {
        if (hash == 0 || Frames.Count == 0) return [];
        return Frames
            .Where(f => PerceptualHashService.HammingDistance(f.Hash, hash) <= maxDistance)
            .Select(f => f.TagName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns each distinct tag name whose stored embedding has cosine
    /// similarity ≥ <paramref name="threshold"/> with <paramref name="embedding"/>.
    /// Used for cross-image person matching independent of dHash.
    /// </summary>
    public IEnumerable<string> MatchByEmbedding(float[] embedding,
                                                float threshold = DefaultCosineThreshold)
    {
        if (embedding is null || embedding.Length == 0 || Frames.Count == 0) return [];
        return Frames
            .Where(f => f.Embedding is { Length: > 0 } &&
                        CosineSimilarity(f.Embedding, embedding) >= threshold)
            .Select(f => f.TagName)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the closest stored frame per tag name (best Hamming distance)
    /// for diagnostic logging. Includes near-misses, not just matches.
    /// </summary>
    public IEnumerable<(string TagName, int Distance, TaggedFrame Frame)> BestDistancePerTag(ulong hash)
    {
        if (hash == 0 || Frames.Count == 0) yield break;
        foreach (var grp in Frames.GroupBy(f => f.TagName, StringComparer.OrdinalIgnoreCase))
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
        if (embedding is null || embedding.Length == 0 || Frames.Count == 0) yield break;
        foreach (var grp in Frames.Where(f => f.Embedding is { Length: > 0 })
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
            if (File.Exists(_path))
            {
                Frames = JsonSerializer.Deserialize<List<TaggedFrame>>(File.ReadAllText(_path))
                         ?? [];
            }
        }
        catch { Frames = []; }
    }

    /// <summary>Persist any external mutations to existing TaggedFrame objects
    /// (e.g. ThumbnailPng backfilled from the source file).</summary>
    public void Flush() => Save();

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(Frames,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
