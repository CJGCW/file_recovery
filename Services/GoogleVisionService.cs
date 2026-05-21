using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Services;

public static class GoogleVisionService
{
    private static readonly HttpClient Http = new();

    public static async Task<VisionResult?> DetectWebAsync(
        BitmapSource frame, string apiKey, CancellationToken ct = default)
    {
        byte[] jpegBytes;
        var enc = new JpegBitmapEncoder { QualityLevel = 80 };
        enc.Frames.Add(BitmapFrame.Create(frame));
        using (var ms = new MemoryStream())
        {
            enc.Save(ms);
            jpegBytes = ms.ToArray();
        }

        var body = JsonSerializer.Serialize(new
        {
            requests = new[]
            {
                new
                {
                    image    = new { content = Convert.ToBase64String(jpegBytes) },
                    features = new[] { new { type = "WEB_DETECTION", maxResults = 15 } }
                }
            }
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(
            $"https://vision.googleapis.com/v1/images:annotate?key={apiKey}", content, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Vision API {(int)resp.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        return ParseResponse(await resp.Content.ReadAsStringAsync(ct));
    }

    public static async Task<IList<(string Name, float Score)>> DetectLogoAsync(
        BitmapSource frame, string apiKey, CancellationToken ct = default)
    {
        byte[] jpegBytes;
        var enc = new JpegBitmapEncoder { QualityLevel = 80 };
        enc.Frames.Add(BitmapFrame.Create(frame));
        using (var ms = new MemoryStream())
        {
            enc.Save(ms);
            jpegBytes = ms.ToArray();
        }

        var body = JsonSerializer.Serialize(new
        {
            requests = new[]
            {
                new
                {
                    image    = new { content = Convert.ToBase64String(jpegBytes) },
                    features = new[] { new { type = "LOGO_DETECTION", maxResults = 5 } }
                }
            }
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(
            $"https://vision.googleapis.com/v1/images:annotate?key={apiKey}", content, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Vision API {(int)resp.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        return ParseLogoResponse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static IList<(string Name, float Score)> ParseLogoResponse(string json)
    {
        var results = new List<(string, float)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("responses", out var responses)
            || responses.GetArrayLength() == 0) return results;

        var r = responses[0];
        if (!r.TryGetProperty("logoAnnotations", out var logos)) return results;

        foreach (var logo in logos.EnumerateArray())
        {
            var name  = logo.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var score = logo.TryGetProperty("score",       out var s) ? s.GetSingle()       : 0f;
            if (name.Length > 0)
                results.Add((name, score));
        }
        return results;
    }

    private static VisionResult? ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("responses", out var responses)
            || responses.GetArrayLength() == 0) return null;

        var r = responses[0];
        if (!r.TryGetProperty("webDetection", out var wd)) return null;

        var entities   = new List<(string Text, float Score)>();
        var pageTitles = new List<string>();
        var pageUrls   = new List<string>();
        string? bestGuess = null;

        if (wd.TryGetProperty("webEntities", out var ents))
            foreach (var e in ents.EnumerateArray())
            {
                var text  = e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var score = e.TryGetProperty("score",       out var s) ? s.GetSingle()       : 0f;
                if (text.Length > 0 && score > 0.2f)
                    entities.Add((text, score));
            }

        if (wd.TryGetProperty("pagesWithMatchingImages", out var pages))
            foreach (var p in pages.EnumerateArray())
            {
                if (p.TryGetProperty("pageTitle", out var t) && t.GetString() is string title)
                    pageTitles.Add(title);
                if (p.TryGetProperty("url",       out var u) && u.GetString() is string url)
                    pageUrls.Add(url);
            }

        if (wd.TryGetProperty("bestGuessLabels", out var bgl) && bgl.GetArrayLength() > 0
            && bgl[0].TryGetProperty("label", out var lbl))
            bestGuess = lbl.GetString();

        return new VisionResult(entities, pageTitles, pageUrls, bestGuess);
    }
}

public record VisionResult(
    IReadOnlyList<(string Text, float Score)> Entities,
    IReadOnlyList<string> PageTitles,
    IReadOnlyList<string> PageUrls,
    string? BestGuessLabel);
