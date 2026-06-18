using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Services;

/// <summary>
/// Extracts a video thumbnail using Windows Media Foundation.
/// Tries seek positions at 10 s, 30 s, 1 m, 2 m, 5 m, and 10 m and returns
/// the first frame that decodes successfully. Falls back to the Windows Shell
/// thumbnail cache if MF fails entirely (e.g. unsupported codec).
/// </summary>
public static class VideoThumbnailService
{
    // Seek candidates in 100-nanosecond ticks
    private static readonly long[] SeekTicks =
    [
         10 * TimeSpan.TicksPerSecond,
         30 * TimeSpan.TicksPerSecond,
         60 * TimeSpan.TicksPerSecond,
        120 * TimeSpan.TicksPerSecond,
        300 * TimeSpan.TicksPerSecond,
        600 * TimeSpan.TicksPerSecond,
    ];

    // MF attribute GUIDs
    private static Guid _mtMajorType    = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static Guid _mtSubtype      = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static Guid _mtFrameSize    = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static Guid _mtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static Guid _mtFrameRate    = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static Guid _mediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static Guid _videoFmtRgb32  = new("00000016-0000-0010-8000-00AA00389B71");

    private const uint AllStreams       = 0xFFFFFFFE;
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint FlagError        = 0x1;
    private const uint FlagEos          = 0x4;

    public static BitmapSource? GetThumbnail(string filePath, int size = 420)
    {
        try { return ExtractMfFrame(filePath, size); }
        catch { }
        return GetShellThumbnail(filePath, size);
    }

    /// <summary>
    /// Extracts frames at each of the 6 quick-scan positions and invokes
    /// <paramref name="onFrame"/> for every usable one.  Runs on a thread-pool
    /// thread; the callback is invoked from that same thread so the caller must
    /// dispatch to the UI thread if required.
    /// </summary>
    public static Task GetQuickFramesAsync(
        string filePath, int size, CancellationToken ct,
        Action<TimeSpan, BitmapSource> onFrame) =>
        Task.Run(() => ExtractQuickFrames(filePath, size, ct, onFrame), ct);

    /// <summary>
    /// Scans every second from 1 s to 10 min and invokes <paramref name="onFrame"/>
    /// for every successfully-decoded frame.  Reports 0–100 progress via
    /// <paramref name="progress"/> as each second is attempted.  Runs on a
    /// thread-pool thread; the callback is invoked from that thread so the
    /// caller must dispatch to the UI thread if required.
    /// </summary>
    public static Task ScanDeepAsync(
        string filePath, int size, CancellationToken ct,
        Action<TimeSpan, BitmapSource> onFrame,
        IProgress<double>? progress = null) =>
        Task.Run(() => ScanDeep(filePath, size, ct, onFrame, progress), ct);

    /// <summary>
    /// Runs `ffmpeg -i filePath` and returns the stderr it prints. ffmpeg
    /// writes the container/codec summary AND any decode errors to stderr,
    /// so this is the canonical "why can't I open this video" report —
    /// useful for diagnosing PhotoRec recoveries where headers are partial
    /// or moov atoms are missing.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DurationLineRegex =
        new(@"Duration:\s+(\d+):(\d+):(\d+(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Extracts the playback duration via ffmpeg. Used as a fallback when
    /// the Windows Shell property store returns nothing for the file —
    /// MKV containers in particular often have unread duration metadata
    /// on PhotoRec recoveries even though ffmpeg can parse them just fine.
    /// </summary>
    public static async Task<TimeSpan?> GetDurationAsync(string filePath, CancellationToken ct = default)
    {
        string stderr;
        try { stderr = await ProbeAsync(filePath, ct); }
        catch { return null; }

        var m = DurationLineRegex.Match(stderr);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out int h)) return null;
        if (!int.TryParse(m.Groups[2].Value, out int mi)) return null;
        if (!double.TryParse(m.Groups[3].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double s)) return null;
        return TimeSpan.FromSeconds(h * 3600 + mi * 60 + s);
    }

    public static Task<string> ProbeAsync(string filePath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath is null) return "(ffmpeg.exe not bundled — cannot probe)";

            var psi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            // -hide_banner cuts the 20-line build info; -nostdin so ffmpeg
            // doesn't hang waiting for a key on damaged files; -i with no
            // output target makes ffmpeg parse + report what it found and
            // exit non-zero (which is fine, we only want stderr).
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(filePath);

            using var proc = Process.Start(psi);
            if (proc is null) return "(failed to start ffmpeg)";

            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            });

            string stderr = proc.StandardError.ReadToEnd();
            try { proc.WaitForExit(5000); } catch { }
            return string.IsNullOrWhiteSpace(stderr)
                ? "(ffmpeg returned no output — file may be 0 bytes or unreachable)"
                : stderr.Trim();
        }, ct);

    private static void ExtractQuickFrames(
        string filePath, int size, CancellationToken ct,
        Action<TimeSpan, BitmapSource> onFrame)
    {
        if (MFStartup(0x00020070, 1) != 0)
        {
            FallbackShell(filePath, size, onFrame);
            return;
        }
        try
        {
            if (MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out var reader) != 0 || reader is null)
            {
                FallbackShell(filePath, size, onFrame);
                return;
            }
            try
            {
                if (!SetupVideoReader(reader, out int width, out int height,
                        out int absStride, out bool bottomUp, out _))
                {
                    FallbackShell(filePath, size, onFrame);
                    return;
                }

                bool anyFound = false;

                // Always try to read the first frame sequentially first —
                // covers short videos and files where seeking isn't supported.
                var firstBmp = ReadSequentialFrame(reader, width, height, absStride, bottomUp, size);
                if (firstBmp is not null)
                {
                    onFrame(TimeSpan.Zero, firstBmp);
                    anyFound = true;
                }

                // Then try the 6 timed positions.
                foreach (var ticks in SeekTicks)
                {
                    if (ct.IsCancellationRequested) break;
                    var bmp = ReadFrameAt(reader, ticks, width, height, absStride, bottomUp, size);
                    if (bmp is not null)
                    {
                        onFrame(TimeSpan.FromTicks(ticks), bmp);
                        anyFound = true;
                    }
                }

                if (!anyFound && !ct.IsCancellationRequested)
                    FallbackShell(filePath, size, onFrame);
            }
            finally { Marshal.ReleaseComObject(reader); }
        }
        finally { MFShutdown(); }
    }

    private static void FallbackShell(string filePath, int size,
        Action<TimeSpan, BitmapSource> onFrame)
    {
        var bmp = GetShellThumbnail(filePath, size);
        if (bmp is not null) onFrame(TimeSpan.Zero, bmp);
    }

    private static void ScanDeep(
        string filePath, int size, CancellationToken ct,
        Action<TimeSpan, BitmapSource> onFrame,
        IProgress<double>? progress = null)
    {
        // Pipes BMP frames out of bundled ffmpeg at 1 fps, up to MaxSecond
        // frames. ffmpeg handles arbitrary containers (MKV, AVI, etc.) and
        // codecs that Media Foundation can't decode reliably.
        const int MaxSecond = 600;

        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null) return;

        var psi = new ProcessStartInfo
        {
            FileName               = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-v");           psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");           psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-vf");          psi.ArgumentList.Add($"fps=1,scale={size}:-2");
        psi.ArgumentList.Add("-frames:v");    psi.ArgumentList.Add(MaxSecond.ToString());
        psi.ArgumentList.Add("-c:v");         psi.ArgumentList.Add("bmp");
        psi.ArgumentList.Add("-f");           psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("pipe:1");

        var proc = Process.Start(psi);
        if (proc is null) return;

        // Drain stderr in the background so ffmpeg doesn't block on a full pipe.
        _ = Task.Run(() => { try { proc.StandardError.ReadToEnd(); } catch { } });

        using var ctReg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        try
        {
            int second = 1;
            var stream = proc.StandardOutput.BaseStream;
            while (second <= MaxSecond && !ct.IsCancellationRequested)
            {
                var bmp = ReadOneBmp(stream, ct);
                if (bmp is null) break;
                onFrame(TimeSpan.FromSeconds(second), bmp);
                progress?.Report(second / (double)MaxSecond * 100.0);
                second++;
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(2000); } catch { }
            proc.Dispose();
        }
    }

    private static BitmapSource? ReadOneBmp(Stream stream, CancellationToken ct)
    {
        // BMP file header (14 bytes): "BM", little-endian uint32 file size at [2..6], reserved, data offset.
        var header = new byte[14];
        if (!ReadExact(stream, header, 0, 14, ct)) return null;
        if (header[0] != (byte)'B' || header[1] != (byte)'M') return null;

        int fileSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2, 4));
        if (fileSize < 14 || fileSize > 50_000_000) return null;

        var data = new byte[fileSize];
        Array.Copy(header, data, 14);
        if (!ReadExact(stream, data, 14, fileSize - 14, ct)) return null;

        try
        {
            using var ms = new MemoryStream(data);
            var decoder = new BmpBitmapDecoder(ms,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            if (ct.IsCancellationRequested) return false;
            int got;
            try { got = stream.Read(buffer, offset + total, count - total); }
            catch { return false; }
            if (got <= 0) return false;
            total += got;
        }
        return true;
    }

    private static string? ResolveFfmpegPath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "tools", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    // Shared MF reader setup: sets stream selection, output type, and reads frame geometry.
    private static bool SetupVideoReader(IMFSourceReader reader,
        out int width, out int height, out int absStride, out bool bottomUp,
        out double framerate)
    {
        width = height = absStride = 0;
        bottomUp = false;
        framerate = 0;

        reader.SetStreamSelection(AllStreams, false);
        reader.SetStreamSelection(FirstVideoStream, true);

        if (MFCreateMediaType(out var outType) == 0 && outType is not null)
        {
            try
            {
                outType.SetGUID(ref _mtMajorType, ref _mediaTypeVideo);
                outType.SetGUID(ref _mtSubtype, ref _videoFmtRgb32);
                reader.SetCurrentMediaType(FirstVideoStream, 0, outType);
            }
            finally { Marshal.ReleaseComObject(outType); }
        }

        reader.GetCurrentMediaType(FirstVideoStream, out var curType);
        curType.GetUINT64(ref _mtFrameSize, out ulong frameSize);
        int strideHr   = curType.GetUINT32(ref _mtDefaultStride, out uint strideRaw);
        int frHr       = curType.GetUINT64(ref _mtFrameRate, out ulong frameRatePacked);
        Marshal.ReleaseComObject(curType);

        width    = (int)(frameSize >> 32);
        height   = (int)(frameSize & 0xFFFFFFFF);
        int stride = strideHr == 0 && strideRaw != 0 ? (int)strideRaw : width * 4;
        bottomUp = stride < 0;
        absStride = Math.Abs(stride);

        if (frHr == 0)
        {
            uint num = (uint)(frameRatePacked >> 32);
            uint den = (uint)(frameRatePacked & 0xFFFFFFFF);
            if (num > 0 && den > 0) framerate = num / (double)den;
        }

        return width > 0 && height > 0;
    }

    // ── MF source-reader path ─────────────────────────────────────────────────

    private static BitmapSource? ExtractMfFrame(string filePath, int size)
    {
        if (MFStartup(0x00020070, 1) != 0) return null;
        try
        {
            if (MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out var reader) != 0 || reader is null)
                return null;

            try
            {
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstVideoStream, true);

                if (MFCreateMediaType(out var outType) != 0 || outType is null) return null;
                try
                {
                    outType.SetGUID(ref _mtMajorType, ref _mediaTypeVideo);
                    outType.SetGUID(ref _mtSubtype, ref _videoFmtRgb32);
                    reader.SetCurrentMediaType(FirstVideoStream, 0, outType);
                }
                finally { Marshal.ReleaseComObject(outType); }

                reader.GetCurrentMediaType(FirstVideoStream, out var curType);
                curType.GetUINT64(ref _mtFrameSize, out ulong frameSize);
                int strideHr = curType.GetUINT32(ref _mtDefaultStride, out uint strideRaw);
                Marshal.ReleaseComObject(curType);

                int width     = (int)(frameSize >> 32);
                int height    = (int)(frameSize & 0xFFFFFFFF);
                int stride    = (strideHr == 0 && strideRaw != 0) ? (int)strideRaw : width * 4;
                bool bottomUp = stride < 0;
                int absStride = Math.Abs(stride);

                if (width <= 0 || height <= 0) return null;

                foreach (long ticks in SeekTicks)
                {
                    var bmp = ReadFrameAt(reader, ticks, width, height, absStride, bottomUp, size);
                    if (bmp != null) return bmp;
                }
                return null;
            }
            finally { Marshal.ReleaseComObject(reader); }
        }
        finally { MFShutdown(); }
    }

    private static BitmapSource? ReadFrameAt(
        IMFSourceReader reader, long ticks,
        int width, int height, int absStride, bool bottomUp, int size)
    {
        var guidNull = Guid.Empty;
        var pv = new PropVariantI8 { VarType = 20 /* VT_I8 */, Value = ticks };
        if (reader.SetCurrentPosition(ref guidNull, ref pv) != 0) return null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (reader.ReadSample(FirstVideoStream, 0,
                    out _, out uint streamFlags, out _, out var sample) != 0)
                return null;

            if ((streamFlags & (FlagError | FlagEos)) != 0) return null;
            if (sample is null) continue;

            try
            {
                if (sample.ConvertToContiguousBuffer(out var buf) != 0) continue;
                try
                {
                    if (buf.Lock(out IntPtr data, out _, out uint dataLen) != 0) continue;
                    try
                    {
                        if ((int)dataLen < absStride * height) continue;
                        var bmp = CreateBitmap(data, width, height, absStride, bottomUp);
                        if (bmp is null) continue;

                        if (width > size || height > size)
                        {
                            double scale  = Math.Min((double)size / width, (double)size / height);
                            var scaled    = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                            scaled.Freeze();
                            return scaled;
                        }
                        bmp.Freeze();
                        return bmp;
                    }
                    finally { buf.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buf); }
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        return null;
    }

    private static BitmapSource? ReadSequentialFrame(
        IMFSourceReader reader, int width, int height, int absStride, bool bottomUp, int size)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (reader.ReadSample(FirstVideoStream, 0,
                    out _, out uint streamFlags, out _, out var sample) != 0)
                return null;

            if ((streamFlags & FlagEos)   != 0) return null;
            if ((streamFlags & FlagError) != 0) return null;
            if (sample is null) continue;

            try
            {
                if (sample.ConvertToContiguousBuffer(out var buf) != 0) continue;
                try
                {
                    if (buf.Lock(out IntPtr data, out _, out uint dataLen) != 0) continue;
                    try
                    {
                        if ((int)dataLen < absStride * height) continue;
                        var bmp = CreateBitmap(data, width, height, absStride, bottomUp);
                        if (bmp is null) continue;

                        if (width > size || height > size)
                        {
                            double scale = Math.Min((double)size / width, (double)size / height);
                            var scaled   = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                            scaled.Freeze();
                            return scaled;
                        }
                        bmp.Freeze();
                        return bmp;
                    }
                    finally { buf.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buf); }
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        return null;
    }

    private static BitmapSource? CreateBitmap(IntPtr data, int width, int height, int absStride, bool bottomUp)
    {
        if (!bottomUp)
            return BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgr32, null, data, absStride * height, absStride);

        // Bottom-up: copy rows in reverse into a managed buffer
        var pixels = new byte[absStride * height];
        for (int y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(data, (height - 1 - y) * absStride),
                pixels, y * absStride, absStride);
        return BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgr32, null, pixels, absStride);
    }

    // ── Shell thumbnail fallback ──────────────────────────────────────────────

    private static BitmapSource? GetShellThumbnail(string filePath, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out var ppv);
            if (ppv is not IShellItemImageFactory factory) return null;

            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.ThumbnailOnly, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;
            try
            {
                var bmpSrc = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmpSrc.Freeze();
                return bmpSrc;
            }
            finally { DeleteObject(hBitmap); }
        }
        catch { return null; }
    }

    // ── COM interfaces ────────────────────────────────────────────────────────

    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        // vtable slots 3-9 (IUnknown slots 0-2 are implicit)
        [PreserveSig] int GetStreamSelection(uint idx, [MarshalAs(UnmanagedType.Bool)] out bool pSelected);
        [PreserveSig] int SetStreamSelection(uint idx, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint idx, uint typeIdx, out IntPtr ppType);
        [PreserveSig] int GetCurrentMediaType(uint idx, [MarshalAs(UnmanagedType.Interface)] out IMFMediaType_ ppType);
        [PreserveSig] int SetCurrentMediaType(uint idx, uint reserved, [MarshalAs(UnmanagedType.Interface)] IMFMediaType_ pType);
        [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, ref PropVariantI8 varPosition);
        [PreserveSig] int ReadSample(uint idx, uint flags, out uint pActualIdx, out uint pStreamFlags, out long pTimestamp, [MarshalAs(UnmanagedType.Interface)] out IMFSample_ ppSample);
    }

    // Partial IMFMediaType vtable — stubs through slot 21 (SetGUID), plus GetUINT32/GetUINT64
    // IMFMediaType IID: 44AE0FA8-EA31-4109-8D2E-4CAE4997C555
    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType_
    {
        // IMFAttributes slots 0-3 (GetItem, GetItemType, CompareItem, Compare)
        [PreserveSig] int _m0(); [PreserveSig] int _m1();
        [PreserveSig] int _m2(); [PreserveSig] int _m3();
        // slot 4 — GetUINT32 (used to read MF_MT_DEFAULT_STRIDE)
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint value);
        // slot 5 — GetUINT64 (used to read MF_MT_FRAME_SIZE)
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong value);
        // slots 6-20 (GetDouble … SetDouble) — stubs
        [PreserveSig] int _m6();  [PreserveSig] int _m7();  [PreserveSig] int _m8();
        [PreserveSig] int _m9();  [PreserveSig] int _m10(); [PreserveSig] int _m11();
        [PreserveSig] int _m12(); [PreserveSig] int _m13(); [PreserveSig] int _m14();
        [PreserveSig] int _m15(); [PreserveSig] int _m16(); [PreserveSig] int _m17();
        [PreserveSig] int _m18(); [PreserveSig] int _m19(); [PreserveSig] int _m20();
        // slot 21 — SetGUID (used to set major type and subtype)
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
    }

    // Partial IMFSample vtable — stubs through slot 38 (ConvertToContiguousBuffer)
    // IMFSample IID: C40A00F2-B93A-4D80-AE8C-5A1C634F58E4
    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample_
    {
        // IMFAttributes slots 0-29 — stubs
        [PreserveSig] int _a0();  [PreserveSig] int _a1();  [PreserveSig] int _a2();
        [PreserveSig] int _a3();  [PreserveSig] int _a4();  [PreserveSig] int _a5();
        [PreserveSig] int _a6();  [PreserveSig] int _a7();  [PreserveSig] int _a8();
        [PreserveSig] int _a9();  [PreserveSig] int _a10(); [PreserveSig] int _a11();
        [PreserveSig] int _a12(); [PreserveSig] int _a13(); [PreserveSig] int _a14();
        [PreserveSig] int _a15(); [PreserveSig] int _a16(); [PreserveSig] int _a17();
        [PreserveSig] int _a18(); [PreserveSig] int _a19(); [PreserveSig] int _a20();
        [PreserveSig] int _a21(); [PreserveSig] int _a22(); [PreserveSig] int _a23();
        [PreserveSig] int _a24(); [PreserveSig] int _a25(); [PreserveSig] int _a26();
        [PreserveSig] int _a27(); [PreserveSig] int _a28(); [PreserveSig] int _a29();
        // IMFSample slots 30-37 (GetSampleFlags … GetBufferByIndex) — stubs
        [PreserveSig] int _s30(); [PreserveSig] int _s31(); [PreserveSig] int _s32();
        [PreserveSig] int _s33(); [PreserveSig] int _s34(); [PreserveSig] int _s35();
        [PreserveSig] int _s36(); [PreserveSig] int _s37();
        // slot 38 — ConvertToContiguousBuffer
        [PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer_ ppBuffer);
    }

    // IMFMediaBuffer IID: 045FA593-8799-42B8-BC8D-8968C6453507
    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer_
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariantI8
    {
        [FieldOffset(0)] public ushort VarType; // VT_I8 = 20
        [FieldOffset(8)] public long   Value;
    }

    // ── Shell COM interfaces ──────────────────────────────────────────────────

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE sz, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit   = 0x00,
        BiggerSizeOk  = 0x01,
        MemoryOnly    = 0x02,
        IconOnly      = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly   = 0x10,
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(
        string pwszURL, IntPtr pAttributes,
        [MarshalAs(UnmanagedType.Interface)] out IMFSourceReader ppSourceReader);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(
        [MarshalAs(UnmanagedType.Interface)] out IMFMediaType_ ppMFType);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
