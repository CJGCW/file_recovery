using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Searches document and code files for user-defined text patterns.
/// Extracts body text from .docx/.pptx ZIP archives; reads other text-based
/// files directly (capped at 1 MB). All I/O runs on thread-pool threads;
/// only MatchedPatterns mutation is marshalled to the UI thread.
/// </summary>
public static class DocumentSearchService
{
    private static readonly (string Symbol, string Color)[] PatternStyles =
    [
        ("●", "#7C6FF7"),
        ("▲", "#E06C6C"),
        ("■", "#6CBF6C"),
        ("◆", "#E0A86C"),
        ("★", "#6CBFE0"),
        ("▶", "#E06CBF"),
        ("◉", "#BA7517"),
        ("⬟", "#808080"),
    ];

    public static (string Symbol, string Color) GetPatternStyle(int index)
        => PatternStyles[index % PatternStyles.Length];

    public static async Task RunSearchAsync(
        IReadOnlyList<FileRecord>    files,
        IReadOnlyList<SearchPattern> patterns,
        Action<string>               statusCallback,
        CancellationToken            ct)
    {
        if (patterns.Count == 0) return;

        // Build compiled matchers once per pattern
        var matchers = patterns
            .Select(p => (pattern: p, match: BuildMatcher(p)))
            .ToList();

        // Clear stale results synchronously before starting
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var f in files) f.MatchedPatterns.Clear();
        });

        var searchable = files
            .Where(f => f.Category is FileCategory.Document or FileCategory.Code)
            .ToList();

        int searched = 0, matched = 0;

        await Parallel.ForEachAsync(searchable,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
                CancellationToken      = ct
            },
            async (file, innerCt) =>
            {
                innerCt.ThrowIfCancellationRequested();

                var text = await Task.Run(() => GetSearchableText(file), innerCt);
                var idx  = Interlocked.Increment(ref searched);

                if (text is null) return;

                var hits = matchers
                    .Where(m => m.match(text))
                    .Select(m => m.pattern)
                    .ToList();

                if (hits.Count > 0)
                {
                    Interlocked.Increment(ref matched);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        file.MatchedPatterns.Clear();
                        foreach (var p in hits)
                            file.MatchedPatterns.Add(p);
                    });
                }

                if (idx % 100 == 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        statusCallback(
                            $"Searching… {idx:N0}/{searchable.Count:N0} checked, {matched:N0} matched"));
            });

        Application.Current.Dispatcher.Invoke(() =>
            statusCallback(
                $"Search complete — {matched:N0} file(s) matched out of {searchable.Count:N0} searched."));
    }

    // ── Matcher factory ──────────────────────────────────────────────────────

    private static Func<string, bool> BuildMatcher(SearchPattern p)
    {
        if (p.IsWholeWord)
        {
            var opts  = p.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex($@"\b{Regex.Escape(p.Text)}\b", opts | RegexOptions.Compiled);
            return text => regex.IsMatch(text);
        }

        var cmp = p.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return text => text.Contains(p.Text, cmp);
    }

    // ── Text extraction ──────────────────────────────────────────────────────

    private static string? GetSearchableText(FileRecord record)
    {
        try
        {
            return record.Extension.ToLowerInvariant() switch
            {
                ".docx" => ExtractDocxText(record.FullPath),
                ".pptx" => ExtractPptxText(record.FullPath),
                _       => ReadTextFile(record.FullPath),
            };
        }
        catch { return null; }
    }

    private static string? ExtractDocxText(string path)
    {
        try
        {
            using var zip   = ZipFile.OpenRead(path);
            var       entry = zip.GetEntry("word/document.xml");
            if (entry is null) return null;
            using var stream = entry.Open();
            return ExtractXmlText(stream);
        }
        catch { return null; }
    }

    private static string? ExtractPptxText(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var sb = new StringBuilder();

            foreach (var entry in zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide",
                                StringComparison.OrdinalIgnoreCase)
                         && e.FullName.EndsWith(".xml",
                                StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName))
            {
                using var stream = entry.Open();
                sb.Append(ExtractXmlText(stream)).Append(' ');
            }

            return sb.Length == 0 ? null : sb.ToString();
        }
        catch { return null; }
    }

    private static string ExtractXmlText(Stream xmlStream)
    {
        var sb = new StringBuilder();
        using var reader = XmlReader.Create(xmlStream,
            new XmlReaderSettings { IgnoreWhitespace = false });

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                sb.Append(reader.ReadElementContentAsString()).Append(' ');
        }
        return sb.ToString();
    }

    private static string? ReadTextFile(string path)
    {
        try
        {
            const long maxBytes = 1024 * 1024; // 1 MB cap
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return null;

            if (info.Length <= maxBytes)
                return File.ReadAllText(path);

            using var fs     = File.OpenRead(path);
            var       buffer = new byte[maxBytes];
            int       read   = fs.Read(buffer, 0, (int)maxBytes);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch { return null; }
    }
}
