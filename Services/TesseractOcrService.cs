using System.IO;
using System.Windows.Media.Imaging;
using Tesseract;

namespace FileRecoveryParser.Services;

/// <summary>
/// Tesseract-backed OCR — handles stylized fonts (yellow-on-black, perspective
/// title cards, etc.) noticeably better than Windows.Media.Ocr. Used as a
/// supplemental pass when Win OCR misses identifying text.
/// </summary>
public static class TesseractOcrService
{
    // tessdata\ sits alongside the .exe in publish output.
    private static readonly string TessDataPath =
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    private static readonly bool TessDataAvailable =
        File.Exists(Path.Combine(TessDataPath, "eng.traineddata"));

    public static bool IsAvailable => TessDataAvailable;

    public static Task<IList<string>> ScanLinesAsync(
        BitmapSource frame, CancellationToken ct = default) =>
        Task.Run<IList<string>>(() => ScanLines(frame, ct), ct);

    private static IList<string> ScanLines(BitmapSource frame, CancellationToken ct)
    {
        if (!TessDataAvailable || frame is null) return [];

        try
        {
            // Encode the BitmapSource to PNG bytes (in-memory; no temp file).
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var bytes = ms.ToArray();

            ct.ThrowIfCancellationRequested();

            using var engine = new TesseractEngine(TessDataPath, "eng", EngineMode.Default);
            // PSM 11 = "Sparse text. Find as much text as possible in no particular order."
            // Works well for title cards and crawls where text isn't laid out as a paragraph.
            engine.DefaultPageSegMode = PageSegMode.SparseText;

            using var img  = Pix.LoadFromMemory(bytes);
            using var page = engine.Process(img);

            var text = page.GetText() ?? "";
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.Trim())
                       .Where(l => l.Length >= 2 && l.Any(char.IsLetter))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }
        catch { return []; }
    }
}
