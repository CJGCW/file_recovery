using System.IO;

namespace FileRecoveryParser.Services;

/// <summary>
/// Per-scan diagnostic log. Captures every match attempt so the user can see
/// exactly what each file matched, what came close, and what missed entirely.
/// </summary>
public sealed class ScanLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private bool _disposed;
    public string Path { get; }

    public ScanLog()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileRecoveryParser");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, $"scan_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        try { _writer = new StreamWriter(Path) { AutoFlush = false }; } catch { _writer = null; }
    }

    public void Write(string line)
    {
        try { _writer?.WriteLine(line); } catch { }
    }

    public void Flush()
    {
        try { _writer?.Flush(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer?.Dispose(); } catch { }
    }
}
