using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

public static class EpisodeIdentifier
{
    // S01E02 / s1e2
    private static readonly Regex SeCode =
        new(@"[Ss](\d{1,2})[Ee](\d{1,2})", RegexOptions.Compiled);
    // Season 1 Episode 2 / Season 1, Episode 2
    private static readonly Regex SeWords =
        new(@"[Ss]eason\s+(\d{1,2})\D{1,5}?[Ee]pisode\s+(\d{1,2})", RegexOptions.Compiled);
    // fandom.com/wiki/Episode_Title
    private static readonly Regex FandomSlug =
        new(@"\.fandom\.com/wiki/([^?#\s]+)", RegexOptions.Compiled);
    // Characters illegal in Windows file/folder names
    private static readonly Regex Illegal =
        new(@"[\\/:*?""<>|]", RegexOptions.Compiled);

    public static async Task<IList<SuggestionResult>> IdentifyAsync(
        BitmapSource frame, AppSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.GoogleVisionKey))
            throw new InvalidOperationException("Google Cloud Vision API key not configured.");

        VisionResult vision;
        try
        {
            vision = await GoogleVisionService.DetectWebAsync(frame, settings.GoogleVisionKey, ct)
                     ?? throw new InvalidOperationException("Vision API returned no results.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Vision API: {ex.Message}", ex);
        }

        var parsed = ParseRef(vision);
        if (parsed is null) return [];

        var suggestions = new List<SuggestionResult>();

        // ── TV episode — TMDB enrichment when S##E## is confirmed ────────────
        if (parsed is { Season: not null, Episode: not null, ShowName: not null }
            && !string.IsNullOrWhiteSpace(settings.TmdbKey))
        {
            try
            {
                var tmdb = await TmdbService.FindEpisodeAsync(
                    parsed.ShowName, parsed.Season.Value, parsed.Episode.Value,
                    settings.TmdbKey, ct);

                if (tmdb is not null)
                {
                    var code = $"S{tmdb.Season:D2}E{tmdb.Episode:D2}";
                    suggestions.Add(new SuggestionResult(
                        SuggestedName:   Clean($"{tmdb.ShowName} {code} {tmdb.Title}"),
                        SuggestedFolder: $"{Clean(tmdb.ShowName)}\\Season {tmdb.Season:D2}\\",
                        Confidence:      0.92f,
                        Source:          "vision+tmdb"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* TMDB is optional; fall through to vision-only */ }
        }

        // ── Movie — TMDB lookup ───────────────────────────────────────────────
        if (suggestions.Count == 0 && parsed.ShowName is not null && parsed.Season is null
            && !string.IsNullOrWhiteSpace(settings.TmdbKey))
        {
            try
            {
                var movie = await TmdbService.FindMovieAsync(parsed.ShowName, settings.TmdbKey, ct);
                if (movie is not null)
                {
                    var year = movie.AirDate?[..4];
                    suggestions.Add(new SuggestionResult(
                        SuggestedName:   Clean(year is not null ? $"{movie.Title} ({year})" : movie.Title),
                        SuggestedFolder: "Movies\\",
                        Confidence:      0.85f,
                        Source:          "vision+tmdb"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        // ── Vision-only fallback ──────────────────────────────────────────────
        if (suggestions.Count == 0 && parsed.ShowName is not null)
        {
            var parts = new List<string> { parsed.ShowName };

            if (parsed.Season is not null && parsed.Episode is not null)
                parts.Add($"S{parsed.Season:D2}E{parsed.Episode:D2}");

            if (parsed.EpisodeTitle is not null && parsed.EpisodeTitle != parsed.ShowName)
                parts.Add(parsed.EpisodeTitle);

            suggestions.Add(new SuggestionResult(
                SuggestedName:   Clean(string.Join(" ", parts)),
                SuggestedFolder: $"{Clean(parsed.ShowName)}\\",
                Confidence:      parsed.Season is not null ? 0.72f : 0.58f,
                Source:          "vision"));
        }

        return suggestions;
    }

    private static ParsedRef? ParseRef(VisionResult v)
    {
        // Gather all text for S##E## extraction
        var corpus = string.Join(" ",
            v.PageTitles.Concat(v.PageUrls).Prepend(v.BestGuessLabel ?? ""));

        int? season = null, episode = null;
        var m = SeCode.Match(corpus);
        if (m.Success)
        {
            season  = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
        }
        else
        {
            m = SeWords.Match(corpus);
            if (m.Success)
            {
                season  = int.Parse(m.Groups[1].Value);
                episode = int.Parse(m.Groups[2].Value);
            }
        }

        // Show name — highest-scoring non-trivial web entity
        var showName = v.Entities
            .OrderByDescending(e => e.Score)
            .Select(e => e.Text)
            .FirstOrDefault(t => t.Length > 2 && !int.TryParse(t, out _));

        // Episode title — prefer fandom wiki slug, fallback to second entity
        string? epTitle = null;
        foreach (var url in v.PageUrls)
        {
            var fm = FandomSlug.Match(url);
            if (!fm.Success) continue;
            var slug = Uri.UnescapeDataString(fm.Groups[1].Value.Replace('_', ' '));
            if (slug.StartsWith("List ") || slug.Contains("disambiguation")) continue;
            epTitle = slug;
            break;
        }

        if (epTitle is null)
            epTitle = v.Entities
                .OrderByDescending(e => e.Score)
                .Skip(1)
                .Select(e => e.Text)
                .FirstOrDefault(t => t != showName && t.Length > 2);

        return showName is null ? null : new ParsedRef(showName, epTitle, season, episode);
    }

    private static string Clean(string s) =>
        Illegal.Replace(s, "").Trim().TrimEnd('.');
}

internal record ParsedRef(string? ShowName, string? EpisodeTitle, int? Season, int? Episode);
