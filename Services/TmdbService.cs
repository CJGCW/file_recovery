using System.Net.Http;
using System.Text.Json;

namespace FileRecoveryParser.Services;

public static class TmdbService
{
    private static readonly HttpClient Http = new();
    private const string Base = "https://api.themoviedb.org/3";

    public static async Task<TmdbResult?> FindEpisodeAsync(
        string showName, int season, int episode, string apiKey, CancellationToken ct = default)
    {
        var showId = await FindShowIdAsync(showName, apiKey, ct);
        if (showId is null) return null;

        var url  = $"{Base}/tv/{showId}/season/{season}/episode/{episode}?api_key={apiKey}";
        var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root       = doc.RootElement;
        var title      = root.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "";
        var overview   = root.TryGetProperty("overview", out var o) ? o.GetString()       : null;
        var airDate    = root.TryGetProperty("air_date", out var d) ? d.GetString()       : null;

        return new TmdbResult(showName, season, episode, title, overview, airDate);
    }

    public static async Task<TmdbResult?> FindMovieAsync(
        string title, string apiKey, CancellationToken ct = default)
    {
        var url  = $"{Base}/search/movie?api_key={apiKey}&query={Uri.EscapeDataString(title)}";
        var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc     = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var results       = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;

        var movie    = results[0];
        var name     = movie.TryGetProperty("title",        out var t) ? t.GetString() ?? title : title;
        var released = movie.TryGetProperty("release_date", out var d) ? d.GetString()          : null;

        return new TmdbResult(name, null, null, name, null, released);
    }

    private static async Task<int?> FindShowIdAsync(string name, string apiKey, CancellationToken ct)
    {
        var url  = $"{Base}/search/tv?api_key={apiKey}&query={Uri.EscapeDataString(name)}";
        var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var results   = doc.RootElement.GetProperty("results");
        return results.GetArrayLength() > 0
            ? results[0].GetProperty("id").GetInt32()
            : (int?)null;
    }
}

public record TmdbResult(
    string ShowName, int? Season, int? Episode,
    string Title, string? Overview, string? AirDate);
