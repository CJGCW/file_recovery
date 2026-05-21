using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Services;

/// <summary>
/// Posts a frame to Google's image-search upload endpoint and extracts
/// the text description Google derives from it.  No API key required.
/// Falls back gracefully (returns null) if Google blocks the request.
/// </summary>
public static class ReverseImageSearchService
{
    // AllowAutoRedirect=false so we can intercept redirect URLs
    private static readonly HttpClient _http = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly Regex QueryParam =
        new(@"[?&]q=([^&]+)", RegexOptions.Compiled);

    static ReverseImageSearchService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    public static async Task<string?> SearchAsync(BitmapSource frame, CancellationToken ct = default)
    {
        try
        {
            // Encode frame to JPEG
            byte[] jpeg;
            var enc = new JpegBitmapEncoder { QualityLevel = 80 };
            enc.Frames.Add(BitmapFrame.Create(frame));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                jpeg = ms.ToArray();
            }

            // Build multipart POST
            using var form = new MultipartFormDataContent();
            var imgPart = new ByteArrayContent(jpeg);
            imgPart.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            form.Add(imgPart, "encoded_image", "frame.jpg");

            // First POST → Google replies with a 303 redirect
            using var r1 = await _http.PostAsync(
                "https://www.google.com/searchbyimage/upload", form, ct);

            if (r1.Headers.Location is not { } loc1) return null;

            // GET the redirect target → another redirect to the actual search URL
            using var r2 = await _http.GetAsync(loc1, ct);

            var searchUrl = r2.Headers.Location?.ToString()
                         ?? r2.RequestMessage?.RequestUri?.ToString();

            if (searchUrl is null) return null;

            // Pull the q= parameter — this is Google's text description of the image
            var m = QueryParam.Match(searchUrl);
            if (!m.Success) return null;

            var query = Uri.UnescapeDataString(m.Groups[1].Value.Replace('+', ' ')).Trim();
            return query.Length > 2 ? query : null;
        }
        catch { return null; }
    }
}
