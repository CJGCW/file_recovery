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

    public static async Task<string?> ScanForTitleAsync(WpfBitmapSource? thumbnail, CancellationToken ct = default)
    {
        if (_engine is null || thumbnail is null) return null;

        try
        {
            // Encode the WPF BitmapSource to PNG bytes in memory
            var encoder = new WpfPngEncoder();
            encoder.Frames.Add(WpfBitmapFrame.Create(thumbnail));
            using var ms = new MemoryStream();
            encoder.Save(ms);

            ct.ThrowIfCancellationRequested();

            // Decode via WinRT (InMemoryRandomAccessStream → BitmapDecoder → SoftwareBitmap)
            using var ims = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(ims.GetOutputStreamAt(0));
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync().AsTask(ct);

            ims.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ims).AsTask(ct);
            using var softBmp = await decoder.GetSoftwareBitmapAsync().AsTask(ct);

            ct.ThrowIfCancellationRequested();

            var result = await _engine.RecognizeAsync(softBmp).AsTask(ct);
            if (result.Lines.Count == 0) return null;

            // Find the line with the largest average word bounding-box area —
            // title cards use big fonts, subtitle/credits text is small
            var best = result.Lines
                .Where(l => l.Words.Count > 0)
                .Select(l => new
                {
                    Text    = l.Text.Trim(),
                    AvgArea = l.Words.Average(w => (double)(w.BoundingRect.Width * w.BoundingRect.Height)),
                })
                .Where(l => l.Text.Length is >= 2 and <= 80 && l.Text.Any(char.IsLetter))
                .OrderByDescending(l => l.AvgArea)
                .FirstOrDefault();

            return best?.Text;
        }
        catch { return null; }
    }
}
