using System.Windows;
using FileRecoveryParser.ViewModels;
using Microsoft.Win32;

namespace FileRecoveryParser.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        // Populate extension filters once scanning finishes
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsScanning) && !Vm.IsScanning)
                Vm.PopulateExtensionFilters();
        };
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog is available in .NET 8+ WPF
        var dialog = new OpenFolderDialog
        {
            Title = "Select the folder containing recovered files"
        };

        if (dialog.ShowDialog(this) == true)
            Vm.FolderPath = dialog.FolderName;
    }
}
