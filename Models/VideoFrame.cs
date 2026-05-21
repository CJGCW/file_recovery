using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace FileRecoveryParser.Models;

public class VideoFrame : INotifyPropertyChanged
{
    private bool _isSelected;

    public BitmapSource Image    { get; }
    public TimeSpan     Position { get; }

    public string Label => Position.TotalHours >= 1
        ? Position.ToString(@"h\:mm\:ss")
        : Position.ToString(@"m\:ss");

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public VideoFrame(BitmapSource image, TimeSpan position)
    {
        Image    = image;
        Position = position;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
