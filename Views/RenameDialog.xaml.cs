using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileRecoveryParser.Models;
using FileRecoveryParser.ViewModels;
using Microsoft.Win32;

namespace FileRecoveryParser.Views;

public partial class RenameDialog : Window
{
    private readonly IList<FileRecord> _items;
    private readonly bool _isBulk;

    public string  ResultPattern     { get; private set; } = string.Empty;
    public string? DestinationFolder { get; private set; }
    public bool    Confirmed         { get; private set; }

    private static readonly string[] Tokens = ["{name}", "{ext}", "{index}", "{index:3}", "{date}"];

    public RenameDialog(IList<FileRecord> items)
    {
        InitializeComponent();
        _items  = items;
        _isBulk = items.Count > 1;

        if (_isBulk)
        {
            LabelText.Text          = $"Rename pattern for {items.Count} files:";
            InputBox.Text           = "{name}_{index}";
            TokenPanel.Visibility   = Visibility.Visible;
            PreviewPanel.Visibility = Visibility.Visible;
            Height = 460;
            BuildTokenChips();
            UpdatePreview();
        }
        else
        {
            LabelText.Text = "New filename:";
            InputBox.Text  = Path.GetFileNameWithoutExtension(items[0].FileName);
        }

        InputBox.SelectAll();
        InputBox.Focus();
    }

    private void BuildTokenChips()
    {
        foreach (var token in Tokens)
        {
            var border = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x40)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5C)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(6, 2, 6, 2),
                Margin          = new Thickness(0, 0, 6, 4),
                Cursor          = Cursors.Hand,
                Tag             = token,
                Child           = new TextBlock
                {
                    Text       = token,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x6F, 0xF7))
                }
            };
            border.MouseLeftButtonUp += Token_Click;
            TokenChips.Children.Add(border);
        }
    }

    private void Token_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string token })
        {
            var pos = InputBox.SelectionStart;
            InputBox.Text = InputBox.Text.Insert(pos, token);
            InputBox.SelectionStart = pos + token.Length;
            InputBox.Focus();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isBulk) UpdatePreview();
        OkBtn.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    private void UpdatePreview()
    {
        PreviewList.Items.Clear();
        var pattern = InputBox.Text;
        var preview = _items.Take(5).Select((r, i) =>
            $"{r.FileName}  →  {MainViewModel.ExpandPattern(pattern, r, i + 1)}");
        foreach (var line in preview)
            PreviewList.Items.Add(line);
    }

    private void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose destination folder" };
        if (dialog.ShowDialog() != true) return;
        DestinationFolder  = dialog.FolderName;
        DestBox.Text       = DestinationFolder;
        DestBox.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    }

    private void ClearDest_Click(object sender, RoutedEventArgs e)
    {
        DestinationFolder  = null;
        DestBox.Text       = "(same folder — no move)";
        DestBox.Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xB0));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultPattern = InputBox.Text;
        Confirmed     = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
