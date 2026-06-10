using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileRecoveryParser.Models;

public class FileRecord : INotifyPropertyChanged
{
    private string   _fullPath      = string.Empty;
    private string   _fileName      = string.Empty;
    private DateTime? _lastModified;
    private bool     _isSelected;

    public long   Id               { get; set; }
    public string Extension        { get; set; } = string.Empty;
    public string DetectedMimeType { get; set; } = string.Empty;
    public FileCategory Category   { get; set; }
    public long   FileSizeBytes    { get; set; }
    public DateTime ScannedAt      { get; set; } = DateTime.UtcNow;
    public string?          DocumentTitle   { get; set; }
    public DocumentContent? DocumentContent { get; set; }
    public TimeSpan?        Duration        { get; set; }
    public VideoInfo?       VideoInfo       { get; set; }
    public ImageSubcategory ImageGroup      { get; set; }
    public ObservableCollection<TagDefinition>  Tags           { get; } = [];
    public ObservableCollection<SearchPattern>  MatchedPatterns { get; } = [];

    /// <summary>
    /// Sort key for the Tags column — alphabetised, comma-joined tag names.
    /// Files with no tags sort after files with tags (use 0xFFFF prefix).
    /// Recomputed lazily on the getter; bound by <see cref="SortDescription"/>
    /// in <c>ApplySort</c>, which re-reads it whenever the view re-sorts.
    /// </summary>
    public string TagsSortKey =>
        Tags.Count == 0
            ? "￾"  // sorts last in ascending
            : string.Join(",", Tags.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(); }
    }

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(); }
    }

    public DateTime? LastModified
    {
        get => _lastModified;
        set { _lastModified = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
