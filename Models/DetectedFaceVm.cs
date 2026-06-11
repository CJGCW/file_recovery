using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileRecoveryParser.Models;

/// <summary>
/// One detected face on the currently-previewed image, with enough state for
/// the overlay UI: box in original-pixel coordinates, the 512-D embedding,
/// the matched PersonRecord (if any), and the user-visible display name.
/// </summary>
public class DetectedFaceVm : INotifyPropertyChanged
{
    public double X      { get; init; }
    public double Y      { get; init; }
    public double Width  { get; init; }
    public double Height { get; init; }

    public float[]       Embedding { get; init; } = [];
    public PersonRecord? Person    { get; set; }

    private string? _displayName;
    public string? DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasName)); }
    }

    public bool HasName => !string.IsNullOrWhiteSpace(DisplayName);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
