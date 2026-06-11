using System.IO;

namespace FileRecoveryParser.Services;

/// <summary>
/// Computes OpenSubtitles' "movie hash" — a 64-bit fingerprint built from
/// the file size plus the unsigned 64-bit checksum of the first and last
/// 64 KB of the file. Designed to identify a specific video file without
/// reading its entire contents.
/// </summary>
public static class OsdbHashService
{
    public record HashResult(string Hex, long FileSize);

    private const int ChunkSize = 65536;

    public static HashResult? Compute(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long size = fs.Length;
            if (size < ChunkSize * 2) return null; // too small to compute the canonical hash

            ulong hash = (ulong)size;
            var buf = new byte[ChunkSize];

            // First 64 KB
            ReadExact(fs, buf, ChunkSize);
            for (int i = 0; i < ChunkSize; i += 8)
                hash += BitConverter.ToUInt64(buf, i);

            // Last 64 KB
            fs.Seek(-ChunkSize, SeekOrigin.End);
            ReadExact(fs, buf, ChunkSize);
            for (int i = 0; i < ChunkSize; i += 8)
                hash += BitConverter.ToUInt64(buf, i);

            return new HashResult(hash.ToString("x16"), size);
        }
        catch { return null; }
    }

    private static void ReadExact(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
