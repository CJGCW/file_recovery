using System.Runtime.InteropServices;

namespace FileRecoveryParser.Services;

/// <summary>
/// Reads media duration via the Windows Shell property store (IPropertyStore).
/// Uses PKEY_Media_Duration {64440490-4C8B-11D1-8B70-080036B11A03}, pid 3,
/// which is populated by Windows Media Foundation shell handlers for all
/// common video and audio formats without requiring an additional library.
/// </summary>
public static class MediaDurationReader
{
    private static readonly Guid FmtId = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private const uint DurationPid = 3;

    public static TimeSpan? Read(string filePath)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            int hr  = SHGetPropertyStoreFromParsingName(filePath, IntPtr.Zero, 0, ref iid, out var store);
            if (hr != 0 || store is null) return null;

            var key = new PropertyKey { FormatId = FmtId, PropertyId = DurationPid };
            var pv  = new PropVariant();
            store.GetValue(ref key, out pv);
            Marshal.ReleaseComObject(store);

            // VT_UI8 = 21: duration in 100-nanosecond ticks
            if (pv.VarType != 21 || pv.UInt64Value == 0) return null;
            return TimeSpan.FromTicks((long)pv.UInt64Value);
        }
        catch { return null; }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, uint flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey key);
        void GetValue([In] ref PropertyKey key, [Out] out PropVariant pv);
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // Minimal PROPVARIANT — only fields needed for VT_UI8 (duration)
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public ulong  UInt64Value;
    }
}
