using System.IO;
using System.Net.Http;

namespace FileRecoveryParser.Services;

public static class ModelDownloader
{
    private static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileRecoveryParser", "models");

    // InsightFace buffalo_s — SCRFD-500M detector + MobileFaceNet recogniser
    private const string DetectionUrl    = "https://huggingface.co/deepinsight/insightface/resolve/main/models/buffalo_s/det_500m.onnx";
    private const string RecognitionUrl  = "https://huggingface.co/deepinsight/insightface/resolve/main/models/buffalo_s/w600k_mbf.onnx";

    public static string DetectionModelPath   => Path.Combine(ModelsDir, "det_500m.onnx");
    public static string RecognitionModelPath => Path.Combine(ModelsDir, "w600k_mbf.onnx");

    public static bool ModelsExist =>
        File.Exists(DetectionModelPath) && File.Exists(RecognitionModelPath);

    public static async Task EnsureModelsAsync(Action<string> status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        await DownloadIfMissingAsync(http, DetectionUrl,   DetectionModelPath,   "face detection model",   status, ct);
        await DownloadIfMissingAsync(http, RecognitionUrl, RecognitionModelPath, "face recognition model", status, ct);
    }

    private static async Task DownloadIfMissingAsync(
        HttpClient http, string url, string dest, string label,
        Action<string> status, CancellationToken ct)
    {
        if (File.Exists(dest)) return;

        status($"Downloading {label}…");
        var tmp = dest + ".tmp";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total    = response.Content.Headers.ContentLength ?? -1;
            await using var src  = await response.Content.ReadAsStreamAsync(ct);
            await using var dst  = File.Create(tmp);

            var buffer   = new byte[81920];
            long written = 0;
            int  read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (total > 0)
                    status($"Downloading {label}… {written * 100 / total}%");
            }

            dst.Close();
            File.Move(tmp, dest, overwrite: true);
            status($"Downloaded {label}.");
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }
}
