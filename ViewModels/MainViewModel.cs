using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FileRecoveryParser.Data;
using FileRecoveryParser.Models;
using FileRecoveryParser.Services;

namespace FileRecoveryParser.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    // ── Backing fields ───────────────────────────────────────────────────────

    private string _folderPath = string.Empty;
    private string _searchText = string.Empty;
    private bool   _isScanning;
    private string _statusText = "Select a folder to begin.";
    private FileRecord? _selectedFile;
    private BitmapImage? _previewImage;
    private bool _showPreview;
    private string _sortColumn = nameof(FileRecord.FileName);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private CancellationTokenSource? _scanCts;

    // ── Collections ──────────────────────────────────────────────────────────

    private readonly ObservableCollection<FileRecord> _allFiles = [];
    public  ICollectionView FileView { get; }

    public ObservableCollection<CategoryFilter> CategoryFilters { get; } = [];
    public ObservableCollection<ExtensionFilter> ExtensionFilters { get; } = [];

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainViewModel()
    {
        FileView = CollectionViewSource.GetDefaultView(_allFiles);
        FileView.Filter = ApplyFilter;
        ((ICollectionView)FileView).CollectionChanged += (_, _) => OnPropertyChanged(nameof(FileCount));

        ScanCommand   = new RelayCommand(_ => _ is string s ? StartScan(s) : StartScan(FolderPath), _ => !IsScanning);
        CancelCommand = new RelayCommand(_ => _scanCts?.Cancel(),                                    _ => IsScanning);
        SortCommand   = new RelayCommand(col => ApplySort(col as string ?? string.Empty));
        ClearCommand  = new RelayCommand(_ => ClearResults(), _ => _allFiles.Count > 0);

        InitialiseCategoryFilters();
    }

    // ── Properties ───────────────────────────────────────────────────────────

    public string FolderPath
    {
        get => _folderPath;
        set { _folderPath = value; OnPropertyChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); FileView.Refresh(); }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotScanning)); }
    }

    public bool IsNotScanning => !_isScanning;

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public int FileCount => FileView.Cast<object>().Count();

    public FileRecord? SelectedFile
    {
        get => _selectedFile;
        set
        {
            _selectedFile = value;
            OnPropertyChanged();
            LoadPreview(value);
        }
    }

    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        set { _previewImage = value; OnPropertyChanged(); }
    }

    public bool ShowPreview
    {
        get => _showPreview;
        set { _showPreview = value; OnPropertyChanged(); }
    }

    public string SortColumn
    {
        get => _sortColumn;
        set { _sortColumn = value; OnPropertyChanged(); }
    }

    public ListSortDirection SortDirection
    {
        get => _sortDirection;
        set { _sortDirection = value; OnPropertyChanged(); }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand ScanCommand   { get; }
    public ICommand CancelCommand { get; }
    public ICommand SortCommand   { get; }
    public ICommand ClearCommand  { get; }

    // ── Scanning ─────────────────────────────────────────────────────────────

    private void StartScan(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText = "Folder not found. Please choose a valid path.";
            return;
        }

        ClearResults();
        FolderPath = path;
        IsScanning = true;
        StatusText = "Scanning…";

        _scanCts = new CancellationTokenSource();
        var ct   = _scanCts.Token;

        // Collect active extension filters (empty set = all)
        var allowedExtensions = ExtensionFilters
            .Where(f => f.IsChecked)
            .Select(f => f.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Collect active category filters
        var allowedCategories = CategoryFilters
            .Where(f => f.IsChecked)
            .Select(f => f.Category)
            .ToHashSet();

        Task.Run(async () =>
        {
            var scanner = new FileScanner();
            int found = 0, skipped = 0;

            try
            {
                await foreach (var record in scanner.ScanAsync(path, ct))
                {
                    if (ct.IsCancellationRequested) break;

                    // Apply pre-scan category/extension filter so we don't load unwanted files
                    if (allowedCategories.Count > 0 && !allowedCategories.Contains(record.Category))
                    { skipped++; continue; }

                    if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(record.Extension))
                    { skipped++; continue; }

                    found++;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _allFiles.Add(record);
                        if (found % 500 == 0)
                            StatusText = $"Found {found:N0} files…";
                    });
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = ct.IsCancellationRequested
                        ? $"Cancelled. {found:N0} files loaded."
                        : $"Scan complete — {found:N0} files identified, {skipped:N0} skipped.";
                    IsScanning = false;
                    OnPropertyChanged(nameof(FileCount));
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = $"Error: {ex.Message}";
                    IsScanning = false;
                });
            }
        }, ct);
    }

    private void ClearResults()
    {
        _allFiles.Clear();
        PreviewImage = null;
        ShowPreview  = false;
        SelectedFile = null;
        OnPropertyChanged(nameof(FileCount));
    }

    // ── Filtering ─────────────────────────────────────────────────────────────

    private bool ApplyFilter(object obj)
    {
        if (obj is not FileRecord r) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (!r.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !r.Extension.Contains(q, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public void RefreshFilter() => FileView.Refresh();

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void ApplySort(string column)
    {
        if (string.IsNullOrEmpty(column)) return;

        // Toggle direction if same column clicked
        if (SortColumn == column)
            SortDirection = SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        else
        {
            SortColumn    = column;
            SortDirection = ListSortDirection.Ascending;
        }

        FileView.SortDescriptions.Clear();
        FileView.SortDescriptions.Add(new SortDescription(SortColumn, SortDirection));
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private void LoadPreview(FileRecord? record)
    {
        PreviewImage = null;
        ShowPreview  = false;

        if (record is null || record.Category != FileCategory.Image) return;
        if (!File.Exists(record.FullPath)) return;

        Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource          = new Uri(record.FullPath);
                bmp.DecodePixelWidth   = 420;   // thumbnail — don't load full res
                bmp.CacheOption        = BitmapCacheOption.OnLoad;
                bmp.CreateOptions      = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();   // make cross-thread safe

                Application.Current.Dispatcher.Invoke(() =>
                {
                    PreviewImage = bmp;
                    ShowPreview  = true;
                });
            }
            catch
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ShowPreview = false;
                });
            }
        });
    }

    // ── Category filter setup ────────────────────────────────────────────────

    private void InitialiseCategoryFilters()
    {
        foreach (FileCategory cat in Enum.GetValues<FileCategory>())
        {
            if (cat == FileCategory.Unknown) continue;
            var filter = new CategoryFilter(cat, onChanged: () => FileView.Refresh());
            CategoryFilters.Add(filter);
        }
    }

    public void PopulateExtensionFilters()
    {
        var seen = _allFiles
            .Select(f => f.Extension)
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e)
            .ToList();

        ExtensionFilters.Clear();
        foreach (var ext in seen)
            ExtensionFilters.Add(new ExtensionFilter(ext, onChanged: () => FileView.Refresh()));
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Helper filter models ─────────────────────────────────────────────────────

public class CategoryFilter : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private readonly Action _onChanged;

    public FileCategory Category  { get; }
    public string       Label     => Category.ToString();

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); _onChanged(); }
    }

    public CategoryFilter(FileCategory category, Action onChanged)
    { Category = category; _onChanged = onChanged; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ExtensionFilter : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private readonly Action _onChanged;

    public string Extension { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); _onChanged(); }
    }

    public ExtensionFilter(string extension, Action onChanged)
    { Extension = extension; _onChanged = onChanged; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// ── RelayCommand ─────────────────────────────────────────────────────────────

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }

    public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
    public void Execute(object?    p) => _execute(p);
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
