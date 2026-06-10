using System.Diagnostics;
using System.IO;

namespace FileRecoveryParser.Services;

/// <summary>
/// Reads container metadata and embedded subtitle text via the bundled
/// ffmpeg.exe. Free, local, no network — designed for use during Identify
/// where the Windows Shell property store can't reach into MKV/MP4 tags.
/// </summary>
public static class FfmpegMetadataService
{
    public static async Task<Dictionary<string, string>> ReadMetadataAsync(
        string filePath, CancellationToken ct = default)
    {
        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg is null) return new(StringComparer.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName               = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-v");        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("ffmetadata");
        psi.ArgumentList.Add("-");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return new(StringComparer.OrdinalIgnoreCase);
            _ = Task.Run(() => { try { proc.StandardError.ReadToEnd(); } catch { } });

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return ParseFfmetadata(stdout);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public static async Task<IList<string>> ExtractSubtitleCuesAsync(
        string filePath, int maxCues = 30, CancellationToken ct = default)
    {
        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg is null) return [];

        var psi = new ProcessStartInfo
        {
            FileName               = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-v");        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-map");      psi.ArgumentList.Add("0:s:0?");   // first sub stream, ok if none
        psi.ArgumentList.Add("-c:s");      psi.ArgumentList.Add("subrip");
        psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("srt");
        psi.ArgumentList.Add("-");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return [];
            _ = Task.Run(() => { try { proc.StandardError.ReadToEnd(); } catch { } });

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return ParseSrtCues(stdout, maxCues);
        }
        catch { return []; }
    }

    private static Dictionary<string, string> ParseFfmetadata(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length > 0) dict[key] = val;
        }
        return dict;
    }

    private static List<string> ParseSrtCues(string srt, int maxCues)
    {
        var cues = new List<string>();
        // Cue blocks are separated by a blank line. Normalize \r\n first.
        var normalized = srt.Replace("\r\n", "\n");
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            if (cues.Count >= maxCues) break;
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // Cue layout: index, "HH:MM:SS,mmm --> HH:MM:SS,mmm", text lines...
            if (lines.Length < 3) continue;
            var text = string.Join(" ", lines[2..])
                             .Replace(' ', ' ')
                             .Trim();
            if (text.Length >= 2) cues.Add(text);
        }
        return cues;
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
}
