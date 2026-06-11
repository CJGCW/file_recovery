using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace FileRecoveryParser.Services;

/// <summary>
/// Looks up a video file's OSDB movie-hash against OpenSubtitles' legacy
/// XML-RPC API. No API key required — uses the documented anonymous
/// "TemporaryUserAgent" identity, which is rate-limited but free.
/// Returns the resolved title, year, and (for TV) season/episode.
/// </summary>
public static class OpenSubtitlesService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Endpoint  = "https://api.opensubtitles.org/xml-rpc";
    private const string UserAgent = "TemporaryUserAgent";

    public record Match(string? MovieName, int? Season, int? Episode, int? Year, string? ImdbId, string? Kind);

    public static async Task<Match?> LookupByHashAsync(
        OsdbHashService.HashResult hash, CancellationToken ct = default)
    {
        var token = await LogInAsync(ct);
        if (token is null) return null;
        try
        {
            return await SearchAsync(token, hash, ct);
        }
        finally
        {
            _ = LogOutAsync(token);
        }
    }

    // ── XML-RPC method wrappers ───────────────────────────────────────────────

    private static async Task<string?> LogInAsync(CancellationToken ct)
    {
        var body = $@"<?xml version=""1.0""?>
<methodCall>
  <methodName>LogIn</methodName>
  <params>
    <param><value><string></string></value></param>
    <param><value><string></string></value></param>
    <param><value><string>en</string></value></param>
    <param><value><string>{UserAgent}</string></value></param>
  </params>
</methodCall>";
        var resp = await PostAsync(body, ct);
        return resp is null ? null : ExtractMemberString(resp, "token");
    }

    private static async Task LogOutAsync(string token)
    {
        var body = $@"<?xml version=""1.0""?>
<methodCall>
  <methodName>LogOut</methodName>
  <params>
    <param><value><string>{token}</string></value></param>
  </params>
</methodCall>";
        try { await PostAsync(body, CancellationToken.None); } catch { }
    }

    private static async Task<Match?> SearchAsync(
        string token, OsdbHashService.HashResult hash, CancellationToken ct)
    {
        var body = $@"<?xml version=""1.0""?>
<methodCall>
  <methodName>SearchSubtitles</methodName>
  <params>
    <param><value><string>{token}</string></value></param>
    <param>
      <value><array><data>
        <value><struct>
          <member><name>moviehash</name><value><string>{hash.Hex}</string></value></member>
          <member><name>moviebytesize</name><value><string>{hash.FileSize}</string></value></member>
        </struct></value>
      </data></array></value>
    </param>
    <param>
      <value><struct>
        <member><name>limit</name><value><int>1</int></value></member>
      </struct></value>
    </param>
  </params>
</methodCall>";
        var resp = await PostAsync(body, ct);
        return resp is null ? null : ParseSearchResponse(resp);
    }

    // ── HTTP plumbing ─────────────────────────────────────────────────────────

    private static async Task<string?> PostAsync(string body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "text/xml");
            var resp = await Http.PostAsync(Endpoint, content, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    // ── Tiny XML-RPC response parsers (struct-of-scalars only) ────────────────

    private static string? ExtractMemberString(string xml, string memberName)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var m in doc.Descendants("member"))
            {
                var name = m.Element("name")?.Value;
                if (name != memberName) continue;
                return m.Element("value")?.Element("string")?.Value;
            }
        }
        catch { }
        return null;
    }

    private static Match? ParseSearchResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            // SearchSubtitles returns a struct whose "data" member is an array
            // of subtitle records; each record is a struct of scalars.
            var dataArray = doc.Descendants("member")
                .FirstOrDefault(m => m.Element("name")?.Value == "data")
                ?.Element("value")?.Element("array")?.Element("data");
            if (dataArray is null) return null;

            var firstRec = dataArray.Element("value")?.Element("struct");
            if (firstRec is null) return null;

            string? Get(string name) => firstRec.Elements("member")
                .FirstOrDefault(m => m.Element("name")?.Value == name)
                ?.Element("value")?.Descendants().FirstOrDefault()?.Value;

            var movieName = Get("MovieName");
            var kind      = Get("MovieKind");
            var imdb      = Get("IDMovieImdb") ?? Get("ImdbId");
            int? year     = int.TryParse(Get("MovieYear"),     out var y) ? y : null;
            int? season   = int.TryParse(Get("SeriesSeason"),  out var s) && s > 0 ? s : null;
            int? episode  = int.TryParse(Get("SeriesEpisode"), out var e) && e > 0 ? e : null;

            if (string.IsNullOrWhiteSpace(movieName)) return null;
            return new Match(movieName, season, episode, year, imdb, kind);
        }
        catch { return null; }
    }
}
