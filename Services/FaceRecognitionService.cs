using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileRecoveryParser.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FileRecoveryParser.Services;

/// <summary>
/// Wraps SCRFD-500M (detection) + MobileFaceNet (recognition) ONNX models.
/// Both models are from InsightFace's buffalo_s pack.
/// Thread-safety: use one instance per thread, or guard calls with a lock.
/// </summary>
public sealed class FaceRecognitionService : IDisposable
{
    private readonly InferenceSession _detSession;
    private readonly InferenceSession _recSession;
    private bool _disposed;

    // SCRFD strides and anchors per stride
    private static readonly int[] Strides  = [8, 16, 32];
    private const int AnchorsPerStride = 2;
    private const int DetInputSize     = 640;
    private const int RecInputSize     = 112;
    private const float DetThreshold   = 0.5f;
    private const float NmsThreshold   = 0.4f;

    public FaceRecognitionService(string detModelPath, string recModelPath)
    {
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _detSession = new InferenceSession(detModelPath, opts);
        _recSession = new InferenceSession(recModelPath, opts);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Detect faces and return bounding boxes (pixel coords, original image space).</summary>
    public IReadOnlyList<FaceDetection> DetectFaces(string imagePath)
    {
        var (pixels, origW, origH) = LoadAndResizeBgr(imagePath, DetInputSize, DetInputSize);
        var data      = NormaliseToTensor(pixels, DetInputSize, DetInputSize, mean: 127.5f, std: 128f);
        var tensor    = new DenseTensor<float>(data, [1, 3, DetInputSize, DetInputSize]);
        var inputName = _detSession.InputMetadata.Keys.First();
        var outNames  = _detSession.OutputMetadata.Keys.ToArray();

        using var results = _detSession.Run(
            [NamedOnnxValue.CreateFromTensor(inputName, tensor)], outNames);

        var boxes  = DecodeScrfd(results, outNames, DetInputSize, DetInputSize);
        var scaled = ScaleBoxes(boxes, DetInputSize, DetInputSize, origW, origH);
        return ApplyNms(scaled, NmsThreshold);
    }

    /// <summary>Compute 512-D face embedding for the given crop region.</summary>
    public float[] GetEmbedding(string imagePath, FaceDetection face)
    {
        var (pixels, _, _) = LoadCropBgr(imagePath, face.Box, RecInputSize, RecInputSize);
        var data       = NormaliseToTensor(pixels, RecInputSize, RecInputSize, mean: 127.5f, std: 128f);
        var tensor     = new DenseTensor<float>(data, [1, 3, RecInputSize, RecInputSize]);
        var inputName  = _recSession.InputMetadata.Keys.First();
        var outputName = _recSession.OutputMetadata.Keys.First();

        using var results = _recSession.Run(
            [NamedOnnxValue.CreateFromTensor(inputName, tensor)], [outputName]);

        return results[0].AsTensor<float>().ToArray();
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    private static (float[] pixels, int origW, int origH) LoadAndResizeBgr(string path, int targetW, int targetH)
    {
        var frame   = LoadBitmapFrame(path);
        int origW   = frame.PixelWidth;
        int origH   = frame.PixelHeight;
        var resized = ResizeBitmap(frame, targetW, targetH);
        var pixels  = GetBgrPixels(resized, targetW, targetH);
        return (pixels, origW, origH);
    }

    private static (float[] pixels, int origW, int origH) LoadCropBgr(string path, System.Windows.Rect box, int targetW, int targetH)
    {
        var frame = LoadBitmapFrame(path);
        int origW = frame.PixelWidth;
        int origH = frame.PixelHeight;

        var x = (int)Math.Max(0, box.X);
        var y = (int)Math.Max(0, box.Y);
        var w = (int)Math.Min(box.Width,  origW - x);
        var h = (int)Math.Min(box.Height, origH - y);

        var cropped = new CroppedBitmap(frame, new System.Windows.Int32Rect(x, y, w, h));
        var resized = ResizeBitmap(cropped, targetW, targetH);
        var pixels  = GetBgrPixels(resized, targetW, targetH);
        return (pixels, origW, origH);
    }

    private static BitmapFrame LoadBitmapFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static BitmapSource ResizeBitmap(BitmapSource src, int targetW, int targetH)
    {
        var scaleX = targetW / (double)src.PixelWidth;
        var scaleY = targetH / (double)src.PixelHeight;
        return new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scaleX, scaleY));
    }

    private static float[] GetBgrPixels(BitmapSource src, int w, int h)
    {
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        int stride    = w * 3;
        var raw       = new byte[stride * h];
        converted.CopyPixels(raw, stride, 0);

        var floats = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++) floats[i] = raw[i];
        return floats;
    }

    // ── Tensor normalisation ──────────────────────────────────────────────────

    private static float[] NormaliseToTensor(float[] bgrPixels, int w, int h, float mean, float std)
    {
        var tensor = new float[3 * h * w];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int src = (y * w + x) * 3;
            tensor[0 * h * w + y * w + x] = (bgrPixels[src + 0] - mean) / std;  // B
            tensor[1 * h * w + y * w + x] = (bgrPixels[src + 1] - mean) / std;  // G
            tensor[2 * h * w + y * w + x] = (bgrPixels[src + 2] - mean) / std;  // R
        }
        return tensor;
    }

    // ── SCRFD post-processing ─────────────────────────────────────────────────

    private static List<(System.Windows.Rect Box, float Score)> DecodeScrfd(
        IReadOnlyList<DisposableNamedOnnxValue> results,
        string[] outputNames, int imgW, int imgH)
    {
        var detections = new List<(System.Windows.Rect, float)>();

        // Outputs are ordered: score_stride8, score_stride16, score_stride32,
        //                      bbox_stride8,  bbox_stride16,  bbox_stride32
        for (int si = 0; si < Strides.Length; si++)
        {
            int scoreIdx = si;
            int bboxIdx  = si + Strides.Length;
            if (bboxIdx >= results.Count) break;

            var scores = results[scoreIdx].AsTensor<float>().ToArray();
            var boxes  = results[bboxIdx].AsTensor<float>().ToArray();
            int stride = Strides[si];
            int fH     = imgH / stride;
            int fW     = imgW / stride;

            int anchorIdx = 0;
            for (int row = 0; row < fH; row++)
            for (int col = 0; col < fW; col++)
            for (int a   = 0; a   < AnchorsPerStride; a++, anchorIdx++)
            {
                if (anchorIdx >= scores.Length) break;
                float score = scores[anchorIdx];
                if (score < DetThreshold) continue;

                float cx = col * stride + stride * 0.5f;
                float cy = row * stride + stride * 0.5f;

                int bi = anchorIdx * 4;
                if (bi + 3 >= boxes.Length) continue;
                float x1 = cx - boxes[bi + 0] * stride;
                float y1 = cy - boxes[bi + 1] * stride;
                float x2 = cx + boxes[bi + 2] * stride;
                float y2 = cy + boxes[bi + 3] * stride;

                detections.Add((new System.Windows.Rect(x1, y1, x2 - x1, y2 - y1), score));
            }
        }
        return detections;
    }

    private static List<(System.Windows.Rect Box, float Score)> ScaleBoxes(
        List<(System.Windows.Rect Box, float Score)> boxes,
        int modelW, int modelH, int origW, int origH)
    {
        float sx = (float)origW / modelW;
        float sy = (float)origH / modelH;
        return boxes.Select(d => (
            new System.Windows.Rect(d.Box.X * sx, d.Box.Y * sy, d.Box.Width * sx, d.Box.Height * sy),
            d.Score)).ToList();
    }

    private static List<FaceDetection> ApplyNms(
        List<(System.Windows.Rect Box, float Score)> detections, float threshold)
    {
        var sorted   = detections.OrderByDescending(d => d.Score).ToList();
        var selected = new List<FaceDetection>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            selected.Add(new FaceDetection(best.Box, best.Score));
            sorted.RemoveAt(0);
            sorted = sorted.Where(d => IoU(best.Box, d.Box) < threshold).ToList();
        }
        return selected;
    }

    private static float IoU(System.Windows.Rect a, System.Windows.Rect b)
    {
        var inter = System.Windows.Rect.Intersect(a, b);
        if (inter.IsEmpty) return 0f;
        float interArea = (float)(inter.Width * inter.Height);
        float unionArea = (float)(a.Width * a.Height + b.Width * b.Height) - interArea;
        return unionArea > 0 ? interArea / unionArea : 0f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _detSession.Dispose();
        _recSession.Dispose();
        _disposed = true;
    }
}

public record FaceDetection(System.Windows.Rect Box, float Confidence);
