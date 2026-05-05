using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Services;

/// <summary>
/// Extracts a thumbnail frame from a video file using the Windows Shell
/// IShellItemImageFactory — the same source Explorer uses, zero extra dependencies.
/// </summary>
public static class VideoThumbnailService
{
    public static BitmapSource? GetThumbnail(string filePath, int size = 420)
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

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE sz, SIIGBF flags, out IntPtr phbm);
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
