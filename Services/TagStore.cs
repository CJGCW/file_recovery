using System.IO;
using System.Text.Json;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Persists the user's tag palette and per-file tag assignments to LocalAppData.
/// </summary>
public class TagStore
{
    private static readonly string[] Palette =
    [
        "#7C6FF7", "#E06C6C", "#6CBF6C", "#E0A86C",
        "#9C6CE0", "#6CBFE0", "#E06CBF", "#808080"
    ];

    private readonly string _tagsPath;
    private readonly string _assignmentsPath;

    private Dictionary<string, HashSet<string>> _assignments = [];

    public List<TagDefinition> Definitions { get; private set; } = [];

    public TagStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileRecoveryParser");
        Directory.CreateDirectory(dir);
        _tagsPath        = Path.Combine(dir, "tags.json");
        _assignmentsPath = Path.Combine(dir, "tag_assignments.json");
        Load();
    }

    public TagDefinition GetOrCreate(string name)
    {
        var existing = Definitions.FirstOrDefault(
            t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var tag = new TagDefinition
        {
            Name  = name,
            Color = Palette[Definitions.Count % Palette.Length]
        };
        Definitions.Add(tag);
        SaveDefinitions();
        return tag;
    }

    public void AssignTag(string filePath, string tagName)
    {
        if (!_assignments.TryGetValue(filePath, out var set))
            _assignments[filePath] = set = [];
        if (set.Add(tagName)) SaveAssignments();
    }

    public void UnassignTag(string filePath, string tagName)
    {
        if (_assignments.TryGetValue(filePath, out var set) && set.Remove(tagName))
            SaveAssignments();
    }

    public IEnumerable<TagDefinition> GetTagsForPath(string filePath)
    {
        if (!_assignments.TryGetValue(filePath, out var names)) yield break;
        foreach (var name in names)
        {
            var def = Definitions.FirstOrDefault(t => t.Name == name);
            if (def is not null) yield return def;
        }
    }

    public void UpdatePath(string oldPath, string newPath)
    {
        if (_assignments.TryGetValue(oldPath, out var set))
        {
            _assignments.Remove(oldPath);
            _assignments[newPath] = set;
            SaveAssignments();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_tagsPath))
            {
                var dtos = JsonSerializer.Deserialize<List<TagDto>>(File.ReadAllText(_tagsPath));
                Definitions = dtos?.Select(d => new TagDefinition { Name = d.Name, Color = d.Color })
                                   .ToList() ?? [];
            }
        }
        catch { }

        try
        {
            if (File.Exists(_assignmentsPath))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                    File.ReadAllText(_assignmentsPath));
                _assignments = raw?.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)) ?? [];
            }
        }
        catch { }
    }

    private void SaveDefinitions()
    {
        try
        {
            File.WriteAllText(_tagsPath,
                JsonSerializer.Serialize(Definitions.Select(t => new TagDto(t.Name, t.Color)).ToList()));
        }
        catch { }
    }

    private void SaveAssignments()
    {
        try
        {
            File.WriteAllText(_assignmentsPath,
                JsonSerializer.Serialize(_assignments.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())));
        }
        catch { }
    }

    private record TagDto(string Name, string Color);
}
