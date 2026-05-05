using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileRecoveryParser.Models;

public class TagDefinition : INotifyPropertyChanged
{
    private string _name  = string.Empty;
    private string _color = "#7C6FF7";

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Color
    {
        get => _color;
        set { _color = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
