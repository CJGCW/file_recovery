using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Models;

/// <summary>
/// A single hit produced by a "Scan for tags" run. One result row per
/// (file, tag) pair — captures the best matching frame and its score so
/// the results window can show a thumbnail and let the user review/apply.
/// </summary>
public class ScanResult : INotifyPropertyChanged
{
    public string FilePath  { get; init; } = string.Empty;
    public string FileName  { get; init; } = string.Empty;

    // Tag name is mutable: the user can right-click and rename a hit before
    // applying, in case the scan matched the wrong tag definition.
    private string _tagName = string.Empty;
    public string TagName
    {
        get => _tagName;
        set { _tagName = value ?? string.Empty; OnPropertyChanged(); }
    }

    /// <summary>Numeric score — lower for dHash (Hamming distance), higher for embedding cosine sim.</summary>
    public double MatchStrength { get; init; }

    /// <summary>Human-readable score (e.g. "dHash distance 4" or "cosine 0.81").</summary>
    public string MatchStrengthLabel { get; init; } = string.Empty;

    /// <summary>
    /// Self-contained PNG bytes of the matching frame. Stored as bytes rather
    /// than a live BitmapSource so the thumbnail survives independently of
    /// the original BitmapSource (whose backing memory from the ffmpeg pipe
    /// could be released after the scan ends — which was causing thumbnails
    /// to render blank when the Scan Results window was reopened).
    /// </summary>
    public byte[]? ThumbnailPng { get; init; }

    // Cached decode of ThumbnailPng. Decoded once on first access, then
    // frozen and held for the lifetime of the ScanResult so the DataGrid's
    // virtualization doesn't pay a decode cost on every scroll.
    private BitmapSource? _decodedThumbnail;
    public BitmapSource? Thumbnail
    {
        get
        {
            if (_decodedThumbnail is not null)     return _decodedThumbnail;
            if (ThumbnailPng is not { Length: > 0 }) return null;
            try
            {
                using var ms = new MemoryStream(ThumbnailPng);
                var decoder = new PngBitmapDecoder(ms,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var f = decoder.Frames[0];
                f.Freeze();
                _decodedThumbnail = f;
                return f;
            }
            catch { return null; }
        }
    }

    private bool _isApplied;
    public bool IsApplied
    {
        get => _isApplied;
        set { _isApplied = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// One-word bucket of how good the match is. Bridges the two underlying
    /// scoring schemes (dHash Hamming distance — lower is better; embedding
    /// cosine similarity — higher is better, stored as 1000-sim so it sorts
    /// alongside dHash) into a single human-readable rating shown in the
    /// Scan Results grid. The raw label remains available as a tooltip.
    /// </summary>
    public string MatchStrengthRating
    {
        get
        {
            if (MatchStrengthLabel.StartsWith("cosine", StringComparison.OrdinalIgnoreCase))
            {
                var sim = 1000.0 - MatchStrength;
                return sim >= 0.90 ? "Excellent"
                     : sim >= 0.80 ? "Strong"
                     : sim >= 0.70 ? "Good"
                     : sim >= 0.60 ? "Fair"
                     : "Weak";
            }
            // dHash Hamming distance (whole-frame or crop): lower = better
            var d = MatchStrength;
            return d <= 2 ? "Excellent"
                 : d <= 4 ? "Strong"
                 : d <= 6 ? "Good"
                 : d <= 8 ? "Fair"
                 : "Weak";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
