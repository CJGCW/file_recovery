using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

public static class DocumentMetadataReader
{
    private static readonly XNamespace W  = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace P  = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A  = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","of","and","for","in","to","with","from","by","on","at",
        "is","it","its","be","as","or","are","was","were","this","that","these",
        "those","not","but","have","has","had","do","does","did","will","would",
        "could","should","may","might","can","about","into","than","then","them",
        "their","there","when","where","which","who","how","what","all","been"
    };

    // ── Public entry point ───────────────────────────────────────────────────

    public static DocumentContent? Read(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".docx" => ReadDocx(filePath),
            ".pptx" => ReadPptx(filePath),
            _       => null
        };

    // Kept for backward compatibility with FileScanner
    public static string? ReadTitle(string filePath) => Read(filePath)?.Title;

    // ── Docx ─────────────────────────────────────────────────────────────────

    private static DocumentContent? ReadDocx(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);

            var title      = ExtractDocxTitle(zip);
            var headerText = ExtractDocxHeaders(zip);
            var author     = ExtractCoreAuthor(zip);
            var keywords   = BuildKeywords(title, headerText);

            return new DocumentContent(title, headerText, author, keywords);
        }
        catch { return null; }
    }

    private static string? ExtractDocxTitle(ZipArchive zip)
    {
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return null;

        using var stream = entry.Open();
        var doc          = XDocument.Load(stream);
        var paragraphs   = doc.Descendants(W + "p").ToList();

        var heading = paragraphs.FirstOrDefault(p =>
        {
            var styleVal = p.Element(W + "pPr")
                            ?.Element(W + "pStyle")
                            ?.Attribute(W + "val")?.Value ?? string.Empty;
            return styleVal.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                || styleVal.Equals("Title", StringComparison.OrdinalIgnoreCase);
        }) ?? paragraphs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(ExtractText(p, W + "t")));

        var text = heading is null ? null : ExtractText(heading, W + "t");
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? ExtractDocxHeaders(ZipArchive zip)
    {
        var parts = new List<string>();
        foreach (var name in new[] { "word/header1.xml", "word/header2.xml", "word/header3.xml" })
        {
            var entry = zip.GetEntry(name);
            if (entry is null) continue;
            using var stream = entry.Open();
            var doc  = XDocument.Load(stream);
            var text = string.Join(" ", doc.Descendants(W + "t").Select(t => t.Value)).Trim();
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        return parts.Count > 0 ? string.Join(" | ", parts) : null;
    }

    // ── Pptx ─────────────────────────────────────────────────────────────────

    private static DocumentContent? ReadPptx(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);

            var title  = ExtractPptxTitle(zip);
            var author = ExtractCoreAuthor(zip);
            var keywords = BuildKeywords(title, null);

            return new DocumentContent(title, null, author, keywords);
        }
        catch { return null; }
    }

    private static string? ExtractPptxTitle(ZipArchive zip)
    {
        var entry = zip.GetEntry("ppt/slides/slide1.xml");
        if (entry is null) return null;

        using var stream = entry.Open();
        var doc    = XDocument.Load(stream);
        var shapes = doc.Descendants(P + "sp").ToList();

        var titleShape = shapes.FirstOrDefault(sp =>
        {
            var phType = sp.Descendants(P + "ph").FirstOrDefault()
                           ?.Attribute("type")?.Value ?? string.Empty;
            return phType.Equals("title", StringComparison.OrdinalIgnoreCase)
                || phType.Equals("ctrTitle", StringComparison.OrdinalIgnoreCase);
        }) ?? shapes.FirstOrDefault(sp => !string.IsNullOrWhiteSpace(ExtractText(sp, A + "t")));

        var text = titleShape is null ? null : ExtractText(titleShape, A + "t");
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static string? ExtractCoreAuthor(ZipArchive zip)
    {
        var entry = zip.GetEntry("docProps/core.xml");
        if (entry is null) return null;
        using var stream = entry.Open();
        var author = XDocument.Load(stream).Descendants(Dc + "creator").FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(author) ? null : author.Trim();
    }

    private static List<string> BuildKeywords(string? title, string? headerText)
    {
        var combined = string.Join(" ", new[] { title, headerText }.Where(s => s is not null));
        return combined
            .Split([' ', '\t', '\r', '\n', ',', '.', ':', ';', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant().Trim('\'', '"'))
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct()
            .ToList();
    }

    private static string ExtractText(XElement element, XName textTag) =>
        string.Concat(element.Descendants(textTag).Select(t => t.Value));
}
