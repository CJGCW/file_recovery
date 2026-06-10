using System.Windows;

namespace FileRecoveryParser.Views;

public partial class TagInputDialog : Window
{
    public string TagName { get; private set; } = "";

    public TagInputDialog(string? prompt = null, string? initial = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(prompt)) PromptLabel.Text = prompt;
        TagNameBox.Text = initial ?? "";
        Loaded += (_, _) => { TagNameBox.Focus(); TagNameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        TagName = TagNameBox.Text.Trim();
        if (string.IsNullOrEmpty(TagName)) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
