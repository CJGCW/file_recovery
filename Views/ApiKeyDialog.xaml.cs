using System.Windows;
using FileRecoveryParser.Services;

namespace FileRecoveryParser.Views;

public partial class ApiKeyDialog : Window
{
    public string VisionKey { get; private set; } = "";
    public string TmdbKey   { get; private set; } = "";

    public ApiKeyDialog(AppSettings existing)
    {
        InitializeComponent();
        VisionKeyBox.Text = existing.GoogleVisionKey ?? "";
        TmdbKeyBox.Text   = existing.TmdbKey ?? "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        VisionKey   = VisionKeyBox.Text.Trim();
        TmdbKey     = TmdbKeyBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
