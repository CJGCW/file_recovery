using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WpfBitmapFrame  = System.Windows.Media.Imaging.BitmapFrame;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;
using WpfPngEncoder   = System.Windows.Media.Imaging.PngBitmapEncoder;

namespace FileRecoveryParser.Services;

/// <summary>
/// Runs Windows.Media.Ocr (built-in WinRT, no extra NuGet) on a video thumbnail
/// to detect title-card text — the largest text block on the frame.
/// </summary>
public static class VideoTitleOcrService
{
    private static readonly OcrEngine? _engine;

    static VideoTitleOcrService()
    {
        try { _engine = OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { _engine = null; }
    }

    /// Returns all text lines from the frame, sorted largest-font first.
    public static async Task<IList<string>> ScanAllTextAsync(
        WpfBitmapSource? thumbnail, CancellationToken ct = default)
    {
        if (_engine is null || thumbnail is null) return [];

        try
        {
            using var softBmp = await ToBitmapAsync(thumbnail, ct);
            ct.ThrowIfCancellationRequested();
            var result = await _engine.RecognizeAsync(softBmp).AsTask(ct);

            return result.Lines
                .Where(l => l.Words.Count > 0)
                .Select(l => new
                {
                    Text    = l.Text.Trim(),
                    AvgArea = l.Words.Average(w => (double)(w.BoundingRect.Width * w.BoundingRect.Height)),
                })
                .Where(l => l.Text.Length is >= 2 and <= 120 && l.Text.Any(char.IsLetter))
                .OrderByDescending(l => l.AvgArea)
                .Select(l => l.Text)
                .ToList();
        }
        catch { return []; }
    }

    /// Original single-best-line method kept for the video title parsing path.
    public static async Task<string?> ScanForTitleAsync(
        WpfBitmapSource? thumbnail, CancellationToken ct = default)
    {
        var lines = await ScanAllTextAsync(thumbnail, ct);
        return lines.Count > 0 ? lines[0] : null;
    }

    private static async Task<Windows.Graphics.Imaging.SoftwareBitmap> ToBitmapAsync(
        WpfBitmapSource src, CancellationToken ct)
    {
        var encoder = new WpfPngEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        using var ims = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(ims.GetOutputStreamAt(0));
        writer.WriteBytes(ms.ToArray());
        await writer.StoreAsync().AsTask(ct);
        ims.Seek(0);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ims).AsTask(ct);
        return await decoder.GetSoftwareBitmapAsync().AsTask(ct);
    }
}
