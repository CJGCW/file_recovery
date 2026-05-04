using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileRecoveryParser.Models;
using FileRecoveryParser.ViewModels;
using Microsoft.Win32;

namespace FileRecoveryParser.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsScanning) && !Vm.IsScanning)
                Vm.PopulateExtensionFilters();
        };
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the folder containing recovered files"
        };

        if (dialog.ShowDialog(this) == true)
            Vm.FolderPath = dialog.FolderName;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Let text inputs handle their own keys
        if (e.OriginalSource is TextBox) return;

        switch (e.Key)
        {
            case Key.Up:
                MoveFileSelection(-1);
                e.Handled = true;
                break;

            case Key.Down:
                MoveFileSelection(1);
                e.Handled = true;
                break;

            case Key.Delete:
                if (Vm.DeleteCommand.CanExecute(null))
                {
                    Vm.DeleteCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }

    private void MoveFileSelection(int delta)
    {
        var items = Vm.FileView.Cast<FileRecord>().ToList();
        if (items.Count == 0) return;

        int current = Vm.SelectedFile is null ? -1 : items.IndexOf(Vm.SelectedFile);
        int next    = Math.Clamp(current + delta, 0, items.Count - 1);
        if (next == current && Vm.SelectedFile is not null) return;

        Vm.SelectedFile = items[next];
        FileGrid.ScrollIntoView(Vm.SelectedFile);
    }
}
