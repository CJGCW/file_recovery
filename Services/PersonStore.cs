using System.IO;
using System.Text.Json;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

public class PersonStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileRecoveryParser");

    private static readonly string PersonsFile = Path.Combine(DataDir, "persons.json");
    private static readonly string TopicsFile  = Path.Combine(DataDir, "topics.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","of","and","for","in","to","with","from","by","on","at",
        "is","it","its","be","as","or","are","was","were","report","document","file"
    };

    public List<PersonRecord>  Persons { get; private set; } = [];
    public List<TopicCluster>  Topics  { get; private set; } = [];

    // ── Persistence ──────────────────────────────────────────────────────────

    public void Load()
    {
        Directory.CreateDirectory(DataDir);
        Persons = DeserializeFile<List<PersonRecord>>(PersonsFile) ?? [];
        Topics  = DeserializeFile<List<TopicCluster>>(TopicsFile)  ?? [];
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(PersonsFile, JsonSerializer.Serialize(Persons, JsonOpts));
        File.WriteAllText(TopicsFile,  JsonSerializer.Serialize(Topics,  JsonOpts));
    }

    private static T? DeserializeFile<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch { return default; }
    }

    // ── Face matching ─────────────────────────────────────────────────────────

    public (PersonRecord Person, float Confidence)? Match(float[] embedding, float threshold = 0.45f)
    {
        PersonRecord? best     = null;
        float         bestSim  = threshold;

        foreach (var person in Persons)
        {
            if (person.Embeddings.Count == 0) continue;
            var avgSim = person.Embeddings.Average(e => CosineSimilarity(embedding, e));
            if (avgSim > bestSim) { bestSim = avgSim; best = person; }
        }

        return best is null ? null : (best, bestSim);
    }

    public PersonRecord CreatePerson(float[] embedding)
    {
        var person = new PersonRecord { LastSeen = DateTime.UtcNow };
        AddEmbedding(embedding, person);
        Persons.Add(person);
        return person;
    }

    public void AddEmbedding(float[] embedding, PersonRecord person)
    {
        person.Embeddings.Add(embedding);
        if (person.Embeddings.Count > 20)
            person.Embeddings.RemoveAt(0);
        person.LastSeen = DateTime.UtcNow;
    }

    public void LearnFromFile(FileRecord file, PersonRecord person)
    {
        if (!string.IsNullOrWhiteSpace(file.FileName))
            person.Name ??= System.IO.Path.GetFileNameWithoutExtension(file.FileName);
        var folder = System.IO.Path.GetDirectoryName(file.FullPath);
        if (!string.IsNullOrWhiteSpace(folder))
            person.PreferredFolder = folder;
        Save();
    }

    public void SetPersonName(PersonRecord person, string name)
    {
        person.Name = name;
        Save();
    }

    public void SetPersonFolder(PersonRecord person, string folder)
    {
        person.PreferredFolder = folder;
        Save();
    }

    // ── Document name matching ────────────────────────────────────────────────

    public (PersonRecord Person, float Confidence)? MatchDocumentText(string? title, string? header, string? author)
    {
        // Prefer exact author match
        if (!string.IsNullOrWhiteSpace(author))
        {
            var byAuthor = Persons
                .Where(p => p.Name is not null &&
                            author.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Name!.Length)
                .FirstOrDefault();
            if (byAuthor is not null) return (byAuthor, 0.9f);
        }

        // Token overlap against known names
        var tokens = Tokenise(string.Join(" ", new[] { title, header }.Where(s => s is not null)));
        PersonRecord? best   = null;
        float         bestScore = 0.3f;

        foreach (var person in Persons.Where(p => p.Name is not null))
        {
            var nameTokens = Tokenise(person.Name!);
            if (nameTokens.Count == 0) continue;
            float overlap = (float)nameTokens.Intersect(tokens, StringComparer.OrdinalIgnoreCase).Count()
                            / nameTokens.Count;
            if (overlap > bestScore) { bestScore = overlap; best = person; }
        }

        return best is null ? null : (best, bestScore);
    }

    // ── Topic clustering ──────────────────────────────────────────────────────

    public TopicCluster? MatchTopic(List<string> keywords, float threshold = 0.3f)
    {
        TopicCluster? best      = null;
        float          bestScore = threshold;

        foreach (var cluster in Topics)
        {
            if (cluster.Keywords.Count == 0) continue;
            float overlap = (float)cluster.Keywords.Intersect(keywords, StringComparer.OrdinalIgnoreCase).Count()
                            / cluster.Keywords.Count;
            if (overlap > bestScore) { bestScore = overlap; best = cluster; }
        }
        return best;
    }

    public void LearnTopicFolder(List<string> keywords, string folder)
    {
        if (keywords.Count == 0) return;

        var existing = MatchTopic(keywords, threshold: 0.5f);
        if (existing is not null)
        {
            existing.PreferredFolder = folder;
            existing.FileCount++;
            // Merge any new keywords
            foreach (var kw in keywords)
                if (!existing.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    existing.Keywords.Add(kw);
        }
        else
        {
            Topics.Add(new TopicCluster { Keywords = keywords, PreferredFolder = folder, FileCount = 1 });
        }
        Save();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> Tokenise(string text) =>
        text.Split([' ', '\t', ',', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant().Trim('\'', '"'))
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct()
            .ToList();

    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return magA == 0 || magB == 0 ? 0f : dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }
}
