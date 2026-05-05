using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileRecoveryParser.Models;

public class SearchPattern : INotifyPropertyChanged
{
    private string _text            = string.Empty;
    private bool   _isCaseSensitive;
    private bool   _isWholeWord;

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public bool IsCaseSensitive
    {
        get => _isCaseSensitive;
        set { _isCaseSensitive = value; OnPropertyChanged(); }
    }

    public bool IsWholeWord
    {
        get => _isWholeWord;
        set { _isWholeWord = value; OnPropertyChanged(); }
    }

    // Visual identity — assigned once at creation, never changed
    public string Symbol { get; init; } = "●";
    public string Color  { get; init; } = "#7C6FF7";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
