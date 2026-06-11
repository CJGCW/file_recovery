using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FileRecoveryParser.Models;
using FileRecoveryParser.ViewModels;

namespace FileRecoveryParser.Views;

public partial class ScanResultsWindow : Window
{
    private readonly MainViewModel _vm;

    public ScanResultsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        ResultsGrid.ItemsSource = _vm.ScanResults;

        var view = CollectionViewSource.GetDefaultView(_vm.ScanResults);
        view.Filter = FilterResults;
        view.SortDescriptions.Add(new SortDescription(nameof(ScanResult.MatchStrength), ListSortDirection.Ascending));

        RefreshStatus();
        _vm.ScanResults.CollectionChanged += (_, _) => RefreshStatus();
    }

    private void RefreshStatus()
    {
        int total   = _vm.ScanResults.Count;
        int applied = _vm.ScanResults.Count(r => r.IsApplied);
        StatusLine.Text = $"{total} result(s) • {applied} applied";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private string _filter = string.Empty;
    private bool FilterResults(object obj)
    {
        if (obj is not ScanResult r) return false;
        if (string.IsNullOrEmpty(_filter)) return true;
        return r.FileName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || r.TagName.Contains(_filter,  StringComparison.OrdinalIgnoreCase)
            || r.FilePath.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text?.Trim() ?? string.Empty;
        CollectionViewSource.GetDefaultView(_vm.ScanResults).Refresh();
    }

    private void ApplyOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ScanResult result)
        {
            _vm.ApplyScanResult(result);
        }
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in CollectionViewSource.GetDefaultView(_vm.ScanResults).Cast<ScanResult>().ToList())
        {
            if (!item.IsApplied) _vm.ApplyScanResult(item);
        }
    }

    // RoutedCommand for the right-click "Edit tag…" menu item. Wired through
    // a CommandBinding on the window because WPF cannot resolve direct
    // Click="…" handlers on instances declared inside Style.Setter.Value.
    public static readonly System.Windows.Input.RoutedCommand EditTagCommand = new();

    // Puts the clicked row's Tag cell into inline edit mode so the user can
    // rename a wrong match before Apply. The two-way binding on TagName takes
    // care of persisting the new value; ApplyScanResult later reads
    // ScanResult.TagName at apply time and routes the rename through
    // ApplyMatchedTags → tag store → the file's Tags collection.
    private void EditTag_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not ScanResult result) return;
        ResultsGrid.CurrentCell = new DataGridCellInfo(result, TagColumn);
        ResultsGrid.Focus();
        ResultsGrid.BeginEdit();
    }
}
