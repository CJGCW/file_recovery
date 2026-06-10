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
    private const float DetThreshold   = 0.4f;
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
        // Letterbox preprocessing: resize the image preserving aspect ratio so
        // the longer side reaches DetInputSize (640), then paste at top-left of
        // a black 640×640 canvas. This matches InsightFace's reference Python
        // pipeline; the previous version stretched the image, distorting faces
        // and causing SCRFD-500M to miss them entirely.
        var frame = LoadBitmapFrame(imagePath);
        var (pixels, letterboxScale) = LetterboxBgr(frame, DetInputSize);

        var data      = NormaliseToTensor(pixels, DetInputSize, DetInputSize, mean: 127.5f, std: 128f);
        var tensor    = new DenseTensor<float>(data, [1, 3, DetInputSize, DetInputSize]);
        var inputName = _detSession.InputMetadata.Keys.First();
        var outNames  = _detSession.OutputMetadata.Keys.ToArray();

        using var results = _detSession.Run(
            [NamedOnnxValue.CreateFromTensor(inputName, tensor)], outNames);

        var boxes  = DecodeScrfd(results, outNames, DetInputSize, DetInputSize);

        // Unscale from letterboxed 640×640 space back to original image coords.
        var scaled = boxes
            .Select(d => (
                new System.Windows.Rect(d.Box.X / letterboxScale,     d.Box.Y / letterboxScale,
                                        d.Box.Width / letterboxScale, d.Box.Height / letterboxScale),
                d.Score))
            .ToList();
        return ApplyNms(scaled, NmsThreshold);
    }

    /// <summary>Compute 512-D face embedding for the given crop region.</summary>
    public float[] GetEmbedding(string imagePath, FaceDetection face)
    {
        var (pixels, _, _) = LoadCropBgr(imagePath, face.Box, RecInputSize, RecInputSize);
        return RunEmbeddingModel(pixels);
    }

    /// <summary>Compute 512-D embedding for a pre-cropped bitmap. Used by the
    /// manual-region tagging flow and by per-crop scan matching, neither of
    /// which has gone through SCRFD detection.</summary>
    public float[] GetEmbeddingFromBitmap(BitmapSource crop)
    {
        var resized = ResizeBitmap(crop, RecInputSize, RecInputSize);
        var pixels  = GetBgrPixels(resized, RecInputSize, RecInputSize);
        return RunEmbeddingModel(pixels);
    }

    private float[] RunEmbeddingModel(float[] pixels)
    {
        var data       = NormaliseToTensor(pixels, RecInputSize, RecInputSize, mean: 127.5f, std: 128f);
        var tensor     = new DenseTensor<float>(data, [1, 3, RecInputSize, RecInputSize]);
        var inputName  = _recSession.InputMetadata.Keys.First();
        var outputName = _recSession.OutputMetadata.Keys.First();

        using var results = _recSession.Run(
            [NamedOnnxValue.CreateFromTensor(inputName, tensor)], [outputName]);

        return results[0].AsTensor<float>().ToArray();
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    // Letterbox a source bitmap into a square targetSize×targetSize BGR float
    // canvas, preserving aspect ratio. Returns the canvas plus the scale used
    // so callers can map detection coordinates back to the original image.
    private static (float[] pixels, float scale) LetterboxBgr(BitmapSource src, int targetSize)
    {
        int origW = src.PixelWidth;
        int origH = src.PixelHeight;

        float scale = Math.Min((float)targetSize / origW, (float)targetSize / origH);
        int   newW  = Math.Max(1, (int)Math.Round(origW * scale));
        int   newH  = Math.Max(1, (int)Math.Round(origH * scale));

        var resized   = new TransformedBitmap(src,
            new System.Windows.Media.ScaleTransform((double)newW / origW, (double)newH / origH));
        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgr24, null, 0);

        int resizedStride = newW * 3;
        var resizedRaw    = new byte[resizedStride * newH];
        converted.CopyPixels(resizedRaw, resizedStride, 0);

        var pixels = new float[targetSize * targetSize * 3];   // zero-padded canvas
        for (int y = 0; y < newH; y++)
        {
            int srcRow = y * resizedStride;
            int dstRow = y * targetSize * 3;
            for (int x = 0; x < newW; x++)
            {
                int srcIdx = srcRow + x * 3;
                int dstIdx = dstRow + x * 3;
                pixels[dstIdx + 0] = resizedRaw[srcIdx + 0];
                pixels[dstIdx + 1] = resizedRaw[srcIdx + 1];
                pixels[dstIdx + 2] = resizedRaw[srcIdx + 2];
            }
        }
        return (pixels, scale);
    }

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

    public static BitmapSource LoadBitmapFrame(string path) => LoadOrientedBitmap(path);

    /// <summary>
    /// Loads a bitmap from disk and applies its EXIF orientation tag so the
    /// returned image is upright. Used by both the face detector and the
    /// image preview so their pixel coordinate spaces stay aligned.
    /// </summary>
    public static BitmapSource LoadOrientedBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame   = decoder.Frames[0];

        // Apply EXIF orientation so phone-captured portrait photos are
        // upright before face detection. WPF's decoder does not do this
        // automatically, and SCRFD scores tank on rotated faces.
        int orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata md &&
                md.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var raw = md.GetQuery("/app1/ifd/{ushort=274}");
                if      (raw is ushort u) orientation = u;
                else if (raw is short  s) orientation = s;
                else if (raw is int    i) orientation = i;
            }
        }
        catch { /* metadata missing/corrupt — treat as upright */ }

        double angle = orientation switch
        {
            3 => 180.0,
            6 => 90.0,
            8 => 270.0,
            _ => 0.0,
        };
        if (angle == 0.0)
        {
            BitmapSource result = frame;
            result.Freeze();
            return result;
        }

        var rotated = new TransformedBitmap(frame,
            new System.Windows.Media.RotateTransform(angle));
        rotated.Freeze();
        return rotated;
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
        // Identify each output tensor by its trailing dimension (which is what
        // distinguishes scores=1, bboxes=4, keypoints=10 in SCRFD), and group
        // them by anchor count so we don't depend on the model's output ORDER.
        // Older InsightFace exports interleave by stride (s8,b8,k8,s16,…);
        // newer ones group by type (s8,s16,s32,b8,…). This handles both.
        var scoresByAnchorCount = new Dictionary<int, float[]>();
        var bboxesByAnchorCount = new Dictionary<int, float[]>();

        foreach (var output in results)
        {
            var t    = output.AsTensor<float>();
            var dims = t.Dimensions.ToArray();
            if (dims.Length < 2) continue;
            int lastDim     = dims[dims.Length - 1];
            int anchorCount = dims.Length >= 3 ? dims[1] : dims[0];

            if (lastDim == 1) scoresByAnchorCount[anchorCount] = t.ToArray();
            else if (lastDim == 4) bboxesByAnchorCount[anchorCount] = t.ToArray();
            // lastDim == 10 → keypoints; ignored.
        }

        var detections = new List<(System.Windows.Rect, float)>();
        foreach (var stride in Strides)
        {
            int fH = imgH / stride;
            int fW = imgW / stride;

            // Models vary between 1 and 2 anchors per location. Pick whichever
            // anchor count actually has a matching tensor.
            int anchorsPerStride = 0;
            int totalAnchors     = 0;
            foreach (int aps in new[] { 2, 1 })
            {
                int candidate = fH * fW * aps;
                if (scoresByAnchorCount.ContainsKey(candidate) &&
                    bboxesByAnchorCount.ContainsKey(candidate))
                {
                    anchorsPerStride = aps;
                    totalAnchors     = candidate;
                    break;
                }
            }
            if (anchorsPerStride == 0) continue;

            var scores = scoresByAnchorCount[totalAnchors];
            var boxes  = bboxesByAnchorCount[totalAnchors];

            int anchorIdx = 0;
            for (int row = 0; row < fH; row++)
            for (int col = 0; col < fW; col++)
            for (int a   = 0; a   < anchorsPerStride; a++, anchorIdx++)
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
