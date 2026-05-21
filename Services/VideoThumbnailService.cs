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
    /// Scans every second from 1 s to 10 min, returning the first frame whose
    /// centre strip has average brightness above the usability threshold,
    /// together with its timestamp.  Reports 0–100 progress via
    /// <paramref name="progress"/> as each second is attempted.
    /// </summary>
    public static Task<(BitmapSource? Frame, TimeSpan Position)> ScanDeepAsync(
        string filePath, int size, CancellationToken ct,
        IProgress<double>? progress = null) =>
        Task.Run(() => ScanDeep(filePath, size, ct, progress), ct);

    /// <summary>
    /// Extracts a single frame at the given <paramref name="position"/>.
    /// Returns null if the position is past the end of the file or decoding fails.
    /// </summary>
    public static Task<BitmapSource?> GetFrameAtPositionAsync(
        string filePath, TimeSpan position, int size, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (MFStartup(0x00020070, 1) != 0) return null;
            try
            {
                if (MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out var reader) != 0 || reader is null)
                    return null;
                try
                {
                    if (!SetupVideoReader(reader, out int width, out int height,
                            out int absStride, out bool bottomUp))
                        return null;

                    if (position == TimeSpan.Zero)
                        return ReadSequentialFrame(reader, width, height, absStride, bottomUp, size);

                    return ReadFrameAt(reader, position.Ticks, width, height, absStride, bottomUp, size);
                }
                finally { Marshal.ReleaseComObject(reader); }
            }
            finally { MFShutdown(); }
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
                        out int absStride, out bool bottomUp))
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

    private static (BitmapSource? Frame, TimeSpan Position) ScanDeep(
        string filePath, int size, CancellationToken ct, IProgress<double>? progress = null)
    {
        if (MFStartup(0x00020070, 1) != 0) return (null, TimeSpan.Zero);
        try
        {
            if (MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out var reader) != 0 || reader is null)
                return (null, TimeSpan.Zero);
            try
            {
                if (!SetupVideoReader(reader, out int width, out int height,
                        out int absStride, out bool bottomUp))
                    return (null, TimeSpan.Zero);

                for (int sec = 1; sec <= 600 && !ct.IsCancellationRequested; sec++)
                {
                    progress?.Report(sec / 600.0 * 100.0);

                    var guidNull = Guid.Empty;
                    var pv = new PropVariantI8 { VarType = 20, Value = (long)sec * TimeSpan.TicksPerSecond };
                    if (reader.SetCurrentPosition(ref guidNull, ref pv) != 0) break;

                    var (found, eos, bmp) = TryGetBrightFrame(
                        reader, width, height, absStride, bottomUp, size);
                    if (found) return (bmp, TimeSpan.FromSeconds(sec));
                    if (eos)   break;
                }
                return (null, TimeSpan.Zero);
            }
            finally { Marshal.ReleaseComObject(reader); }
        }
        finally { MFShutdown(); }
    }

    // Shared MF reader setup: sets stream selection, output type, and reads frame geometry.
    private static bool SetupVideoReader(IMFSourceReader reader,
        out int width, out int height, out int absStride, out bool bottomUp)
    {
        width = height = absStride = 0;
        bottomUp = false;

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
        int strideHr = curType.GetUINT32(ref _mtDefaultStride, out uint strideRaw);
        Marshal.ReleaseComObject(curType);

        width    = (int)(frameSize >> 32);
        height   = (int)(frameSize & 0xFFFFFFFF);
        int stride = strideHr == 0 && strideRaw != 0 ? (int)strideRaw : width * 4;
        bottomUp = stride < 0;
        absStride = Math.Abs(stride);

        return width > 0 && height > 0;
    }

    private static (bool found, bool eos, BitmapSource? bmp) TryGetBrightFrame(
        IMFSourceReader reader, int width, int height, int absStride, bool bottomUp, int size)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (reader.ReadSample(FirstVideoStream, 0, out _, out uint sf, out _, out var sample) != 0)
                return (false, false, null);
            if ((sf & FlagEos)   != 0) return (false, true,  null);
            if ((sf & FlagError) != 0) return (false, false, null);
            if (sample is null) continue;

            try
            {
                if (sample.ConvertToContiguousBuffer(out var buf) != 0) continue;
                try
                {
                    if (buf.Lock(out IntPtr data, out _, out uint len) != 0) continue;
                    try
                    {
                        if ((int)len < absStride * height) continue;
                        if (!IsUsable(data, absStride, width, height)) return (false, false, null);

                        var bmp = CreateBitmap(data, width, height, absStride, bottomUp);
                        if (bmp is null) continue;

                        if (width > size || height > size)
                        {
                            double scale = Math.Min((double)size / width, (double)size / height);
                            var scaled   = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                            scaled.Freeze();
                            return (true, false, scaled);
                        }
                        bmp.Freeze();
                        return (true, false, bmp);
                    }
                    finally { buf.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buf); }
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        return (false, false, null);
    }

    // Samples the horizontal centre strip; returns false when the frame is mostly black.
    private static bool IsUsable(IntPtr data, int absStride, int width, int height)
    {
        int y           = height / 2;
        int samplePx    = Math.Min(width, 128);
        int xOffset     = (width / 2 - samplePx / 2) * 4;
        var sample      = new byte[samplePx * 4];
        Marshal.Copy(IntPtr.Add(data, y * absStride + xOffset), sample, 0, sample.Length);

        int total = 0, count = 0;
        for (int i = 0; i < sample.Length; i += 4)
        {
            total += sample[i] + sample[i + 1] + sample[i + 2]; // B+G+R, skip padding byte
            count += 3;
        }
        return count > 0 && (double)total / count > 20.0;
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
