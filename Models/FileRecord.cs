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
