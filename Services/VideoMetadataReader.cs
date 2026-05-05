using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Reads video metadata (duration, embedded title, year, resolution) via the
/// Windows Shell IPropertyStore, and parses S01E02 / year patterns from filenames.
/// No extra library dependencies.
/// </summary>
public static class VideoMetadataReader
{
    // PKEY constants: (formatId, propertyId)
    private static readonly Guid DurationFmt    = new("64440490-4C8B-11D1-8B70-080036B11A03"); // PKEY_Media_Duration  pid 3
    private static readonly Guid TitleFmt       = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"); // PKEY_Title           pid 2
    private static readonly Guid YearFmt        = new("56A3372E-CE9C-11D2-9F0E-006097C686F6"); // PKEY_Media_Year      pid 5
    private static readonly Guid VideoFmt       = new("64440491-4C8B-11D1-8B70-080036B11A03"); // PKEY_Video_Frame*    pid 3/4

    private static readonly (Guid Fmt, uint Pid)[] Keys =
    [
        (DurationFmt, 3), // 0 duration
        (TitleFmt,    2), // 1 embedded title
        (YearFmt,     5), // 2 year
        (VideoFmt,    3), // 3 frame width
        (VideoFmt,    4), // 4 frame height
    ];

    // ── Filename pattern regexes ──────────────────────────────────────────────

    // "Show.Name.S01E02.mkv" or "Show Name S01E02"
    private static readonly Regex SePattern = new(
        @"^(.+?)[.\s_\-]+[sS](\d{1,2})[eE](\d{1,2})", RegexOptions.Compiled);

    // "Show.Name.01x02.mkv"
    private static readonly Regex AltSePattern = new(
        @"^(.+?)[.\s_\-]+(\d{1,2})x(\d{2})\b", RegexOptions.Compiled);

    // "Movie.Name.(2023)" or "Movie.Name.2023."
    private static readonly Regex YearPattern = new(
        @"^(.+?)[.\s_\-]+\(?\b(19\d{2}|20\d{2})\b\)?", RegexOptions.Compiled);

    public static VideoInfo? Read(string filePath)
    {
        TimeSpan? duration    = null;
        string?   title       = null;
        uint?     year        = null;
        uint?     frameWidth  = null;
        uint?     frameHeight = null;

        // ── Shell property store ──────────────────────────────────────────────
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreFromParsingName(filePath, IntPtr.Zero, 0, ref iid, out var store);
            if (hr == 0 && store is not null)
            {
                try
                {
                    for (int i = 0; i < Keys.Length; i++)
                    {
                        var key = new PropertyKey { FormatId = Keys[i].Fmt, PropertyId = Keys[i].Pid };
                        store.GetValue(ref key, out var pv);

                        switch (i)
                        {
                            case 0 when pv.VarType == 21 && pv.UInt64Value > 0:
                                duration = TimeSpan.FromTicks((long)pv.UInt64Value);
                                break;
                            case 1 when pv.VarType == 31 && pv.PointerValue != IntPtr.Zero:
                                title = Marshal.PtrToStringUni(pv.PointerValue);
                                break;
                            case 2 when pv.VarType == 19 && pv.UInt32Value > 0:
                                year = pv.UInt32Value;
                                break;
                            case 3 when pv.VarType == 19 && pv.UInt32Value > 0:
                                frameWidth = pv.UInt32Value;
                                break;
                            case 4 when pv.VarType == 19 && pv.UInt32Value > 0:
                                frameHeight = pv.UInt32Value;
                                break;
                        }

                        PropVariantClear(ref pv);
                    }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
        }
        catch { /* shell properties unavailable */ }

        // ── Filename parsing ──────────────────────────────────────────────────
        string?  parsedShow    = null;
        int?     parsedSeason  = null;
        int?     parsedEpisode = null;
        int?     parsedYear    = null;

        var stem = Path.GetFileNameWithoutExtension(filePath);

        var m = SePattern.Match(stem);
        if (m.Success)
        {
            parsedShow    = CleanName(m.Groups[1].Value);
            parsedSeason  = int.Parse(m.Groups[2].Value);
            parsedEpisode = int.Parse(m.Groups[3].Value);
        }
        else
        {
            m = AltSePattern.Match(stem);
            if (m.Success)
            {
                parsedShow    = CleanName(m.Groups[1].Value);
                parsedSeason  = int.Parse(m.Groups[2].Value);
                parsedEpisode = int.Parse(m.Groups[3].Value);
            }
            else
            {
                m = YearPattern.Match(stem);
                if (m.Success)
                {
                    parsedShow = CleanName(m.Groups[1].Value);
                    parsedYear = int.Parse(m.Groups[2].Value);
                }
            }
        }

        return new VideoInfo(
            duration, title, year, frameWidth, frameHeight,
            parsedShow, parsedSeason, parsedEpisode, parsedYear);
    }

    private static string CleanName(string raw) =>
        Regex.Replace(raw.Replace('.', ' '), @"\s{2,}", " ").Trim();

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, uint flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey key);
        void GetValue([In] ref PropertyKey key, [Out] out PropVariant pv);
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public ulong  UInt64Value;
        [FieldOffset(8)] public uint   UInt32Value;
        [FieldOffset(8)] public IntPtr PointerValue;
    }
}
