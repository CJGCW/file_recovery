using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FileRecoveryParser.Models;
using FileRecoveryParser.Services;
using FileRecoveryParser.Views;
using Microsoft.Win32;

namespace FileRecoveryParser.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    // ── Backing fields ───────────────────────────────────────────────────────

    private string _folderPath = string.Empty;
    private string _searchText = string.Empty;
    private bool   _isScanning;
    private string _statusText = "Select a folder to begin.";
    private FileRecord? _selectedFile;
    private BitmapSource? _previewImage;
    private bool    _showImagePreview;
    private bool    _showVideoPreview;
    private bool    _showDocumentPreview;
    private string? _previewFilePath;
    private bool    _isPreviewMaximized;
    private bool    _isDetectingDuplicates;
    private CancellationTokenSource? _ocrCts;
    private string _sortColumn = nameof(FileRecord.FileName);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private CancellationTokenSource? _scanCts;
    private IList<SuggestionResult> _currentSuggestions = [];

    // ── Suggestion pipeline ──────────────────────────────────────────────────
    private readonly SuggestionService _suggestionService;
    private readonly ConcurrentDictionary<long, IList<SuggestionResult>> _suggestionCache = new();
    private Channel<FileRecord>? _recognitionQueue;

    // ── Tagging ──────────────────────────────────────────────────────────────
    private readonly TagStore _tagStore = new();
    private string _newTagText = string.Empty;
    private IList<string> _suggestedTags = [];

    // ── Text search ──────────────────────────────────────────────────────────
    private string _newSearchText        = string.Empty;
    private bool   _newSearchCaseSensitive;
    private bool   _newSearchWholeWord;
    private bool   _isSearching;
    private CancellationTokenSource? _searchCts;

    // ── Collections ──────────────────────────────────────────────────────────

    private readonly ObservableCollection<FileRecord> _allFiles = [];
    public  ICollectionView FileView { get; }

    public ObservableCollection<CategoryFilter>  CategoryFilters   { get; } = [];
    public ObservableCollection<ExtensionFilter> ExtensionFilters  { get; } = [];
    public ObservableCollection<ImageGroupFilter> ImageGroupFilters { get; } = [];

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _suggestionService = new SuggestionService(new PersonStore());

        FileView = CollectionViewSource.GetDefaultView(_allFiles);
        FileView.Filter = ApplyFilter;
        ((ICollectionView)FileView).CollectionChanged += (_, _) => OnPropertyChanged(nameof(FileCount));

        AllTags        = new ObservableCollection<TagDefinition>(_tagStore.Definitions);
        SearchPatterns = [];

        ScanCommand            = new RelayCommand(_ => { if (_ is string s) StartScan(s); else StartScan(FolderPath); }, _ => !IsScanning);
        CancelCommand          = new RelayCommand(_ => { _scanCts?.Cancel(); _searchCts?.Cancel(); },
                                                  _ => IsScanning || _isSearching);
        SortCommand            = new RelayCommand(col => ApplySort(col as string ?? string.Empty));
        ClearCommand           = new RelayCommand(_ => ClearResults(), _ => _allFiles.Count > 0);
        SelectAllCommand       = new RelayCommand(_ => ToggleSelectAll());
        MoveCommand            = new RelayCommand(_ => ExecuteMove(),   _ => SelectedCount > 0);
        RenameCommand          = new RelayCommand(_ => ExecuteRename(), _ => SelectedCount > 0);
        DeleteCommand          = new RelayCommand(_ => ExecuteDelete(), _ => SelectedCount > 0);
        ApplySuggestionCommand         = new RelayCommand(s => ApplySuggestion(s as SuggestionResult));
        TogglePreviewMaximizeCommand   = new RelayCommand(_ => IsPreviewMaximized = !IsPreviewMaximized);
        FindDuplicatesCommand          = new RelayCommand(_ => FindDuplicatesAsync(),
                                            _ => _allFiles.Count > 0 && !IsScanning && !_isDetectingDuplicates);
        AddTagCommand                  = new RelayCommand(p => AddTag(p as string), _ => SelectedFile is not null);
        RemoveTagCommand               = new RelayCommand(p => RemoveTag(p as TagDefinition), _ => SelectedFile is not null);
        CreateAndAddTagCommand         = new RelayCommand(_ => CreateAndAddTag(), _ => SelectedFile is not null && !string.IsNullOrWhiteSpace(NewTagText));
        AddSearchPatternCommand        = new RelayCommand(_ => AddSearchPattern(), _ => !string.IsNullOrWhiteSpace(NewSearchText));
        RemoveSearchPatternCommand     = new RelayCommand(p => RemoveSearchPattern(p as SearchPattern));
        RunSearchCommand               = new RelayCommand(_ => RunSearch(),
                                             _ => SearchPatterns.Count > 0 && _allFiles.Count > 0 && !_isScanning && !_isSearching);
        ClearSearchCommand             = new RelayCommand(_ => ClearSearch(),
                                             _ => SearchPatterns.Count > 0 || _allFiles.Any(f => f.MatchedPatterns.Count > 0));

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
        set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotScanning)); OnPropertyChanged(nameof(IsAnyOperationRunning)); }
    }

    public bool IsNotScanning => !_isScanning;

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public int FileCount => FileView.Cast<object>().Count();

    public int SelectedCount => _allFiles.Count(f => f.IsSelected);

    public bool? AllSelected
    {
        get
        {
            var visible = FileView.Cast<FileRecord>().ToList();
            if (visible.Count == 0) return false;
            var checkedCount = visible.Count(f => f.IsSelected);
            if (checkedCount == 0) return false;
            if (checkedCount == visible.Count) return true;
            return null;
        }
        set
        {
            var check = value ?? false;
            foreach (var f in FileView.Cast<FileRecord>())
                f.IsSelected = check;
            RaiseSelectionChanged();
        }
    }

    public IList<SuggestionResult> CurrentSuggestions
    {
        get => _currentSuggestions;
        private set { _currentSuggestions = value; OnPropertyChanged(); }
    }

    public FileRecord? SelectedFile
    {
        get => _selectedFile;
        set
        {
            _selectedFile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMetadata));
            LoadPreview(value);
            CurrentSuggestions = value is not null
                ? _suggestionCache.GetValueOrDefault(value.Id, [])
                : [];
            SuggestedTags = value is not null ? ComputeSuggestedTags(value) : [];
        }
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        set { _previewImage = value; OnPropertyChanged(); }
    }

    public bool ShowImagePreview
    {
        get => _showImagePreview;
        set { _showImagePreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowMediaPreview)); OnPropertyChanged(nameof(ShowAnyPreview)); }
    }

    public bool ShowVideoPreview
    {
        get => _showVideoPreview;
        set { _showVideoPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowMediaPreview)); OnPropertyChanged(nameof(ShowAnyPreview)); }
    }

    public bool ShowDocumentPreview
    {
        get => _showDocumentPreview;
        set { _showDocumentPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAnyPreview)); }
    }

    public bool ShowMediaPreview   => _showImagePreview || _showVideoPreview;
    public bool ShowAnyPreview     => _showImagePreview || _showVideoPreview || _showDocumentPreview;

    public bool IsPreviewMaximized
    {
        get => _isPreviewMaximized;
        set { _isPreviewMaximized = value; OnPropertyChanged(); }
    }

    public string? PreviewFilePath
    {
        get => _previewFilePath;
        set { _previewFilePath = value; OnPropertyChanged(); }
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

    public ObservableCollection<TagDefinition> AllTags { get; private set; } = [];

    public string NewTagText
    {
        get => _newTagText;
        set { _newTagText = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public IList<string> SuggestedTags
    {
        get => _suggestedTags;
        private set { _suggestedTags = value; OnPropertyChanged(); }
    }

    public bool ShowMetadata => _selectedFile is not null;

    public ObservableCollection<SearchPattern> SearchPatterns { get; private set; }

    public string NewSearchText
    {
        get => _newSearchText;
        set { _newSearchText = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool NewSearchCaseSensitive
    {
        get => _newSearchCaseSensitive;
        set { _newSearchCaseSensitive = value; OnPropertyChanged(); }
    }

    public bool NewSearchWholeWord
    {
        get => _newSearchWholeWord;
        set { _newSearchWholeWord = value; OnPropertyChanged(); }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            _isSearching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyOperationRunning));
        }
    }

    public bool IsAnyOperationRunning => _isScanning || _isSearching;

    public int SearchMatchCount => _allFiles.Count(f => f.MatchedPatterns.Count > 0);

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand ScanCommand            { get; }
    public ICommand CancelCommand          { get; }
    public ICommand SortCommand            { get; }
    public ICommand ClearCommand           { get; }
    public ICommand SelectAllCommand       { get; }
    public ICommand MoveCommand            { get; }
    public ICommand RenameCommand          { get; }
    public ICommand ApplySuggestionCommand       { get; }
    public ICommand TogglePreviewMaximizeCommand { get; }
    public ICommand DeleteCommand                { get; }
    public ICommand FindDuplicatesCommand        { get; }
    public ICommand AddTagCommand                { get; }
    public ICommand RemoveTagCommand             { get; }
    public ICommand CreateAndAddTagCommand       { get; }
    public ICommand AddSearchPatternCommand      { get; }
    public ICommand RemoveSearchPatternCommand   { get; }
    public ICommand RunSearchCommand             { get; }
    public ICommand ClearSearchCommand           { get; }

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

        // Fresh recognition queue for this scan
        _recognitionQueue?.Writer.TryComplete();
        _recognitionQueue = Channel.CreateBounded<FileRecord>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite });
        var queue = _recognitionQueue;

        // Start background recognition worker
        _ = Task.Run(() => RunRecognitionWorkerAsync(queue, ct), CancellationToken.None);

        AllTags.Clear();
        foreach (var t in _tagStore.Definitions) AllTags.Add(t);

        var allowedExtensions = ExtensionFilters
            .Where(f => f.IsChecked)
            .Select(f => f.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

                    if (allowedCategories.Count > 0 && !allowedCategories.Contains(record.Category))
                    { skipped++; continue; }

                    if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(record.Extension))
                    { skipped++; continue; }

                    record.PropertyChanged += Record_PropertyChanged;
                    queue.Writer.TryWrite(record);

                    found++;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var tag in _tagStore.GetTagsForPath(record.FullPath))
                            record.Tags.Add(tag);
                        _allFiles.Add(record);
                        if (found % 500 == 0)
                            StatusText = $"Found {found:N0} files…";
                    });
                }

                queue.Writer.TryComplete();

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
                queue.Writer.TryComplete();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = $"Error: {ex.Message}";
                    IsScanning = false;
                });
            }
        }, ct);
    }

    private async Task RunRecognitionWorkerAsync(Channel<FileRecord> queue, CancellationToken ct)
    {
        bool anyFaceFound = false;
        try
        {
            await _suggestionService.EnsureReadyAsync(
                msg => Application.Current.Dispatcher.Invoke(() => StatusText = msg), ct);

            await foreach (var record in queue.Reader.ReadAllAsync(ct))
            {
                var suggestions = await _suggestionService.GetSuggestionsAsync(record, ct);
                _suggestionCache[record.Id] = suggestions;

                if (suggestions.Any(s => s.Source == "face") && record.Category == FileCategory.Image)
                {
                    record.ImageGroup = ImageSubcategory.PersonPhoto;
                    anyFaceFound = true;
                }

                if (suggestions.Count > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (SelectedFile?.Id == record.Id)
                            CurrentSuggestions = suggestions;
                    });
            }

            if (anyFaceFound)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PopulateImageGroupFilters();
                    FileView.Refresh();
                });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Record_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileRecord.IsSelected))
            RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(AllSelected));
    }

    private void ClearResults()
    {
        foreach (var r in _allFiles) r.PropertyChanged -= Record_PropertyChanged;
        _ocrCts?.Cancel();
        _ocrCts = null;
        _allFiles.Clear();
        _suggestionCache.Clear();
        CurrentSuggestions  = [];
        SuggestedTags       = [];
        ImageGroupFilters.Clear();
        PreviewImage        = null;
        ShowImagePreview    = false;
        ShowVideoPreview    = false;
        ShowDocumentPreview = false;
        PreviewFilePath     = null;
        SelectedFile        = null;
        OnPropertyChanged(nameof(ShowMetadata));
        OnPropertyChanged(nameof(FileCount));
        RaiseSelectionChanged();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void ToggleSelectAll()
    {
        var all = AllSelected == true;
        foreach (var f in FileView.Cast<FileRecord>())
            f.IsSelected = !all;
        RaiseSelectionChanged();
    }

    // ── Move ─────────────────────────────────────────────────────────────────

    private void ExecuteMove()
    {
        var selected = _allFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        var dialog = new OpenFolderDialog { Title = "Choose destination folder" };
        if (dialog.ShowDialog() != true) return;

        var dest    = dialog.FolderName;
        int moved   = 0;
        var errors  = new List<string>();

        foreach (var record in selected)
        {
            try
            {
                var newPath = Path.Combine(dest, record.FileName);
                if (File.Exists(newPath))
                    newPath = MakeUniquePath(newPath);

                var oldPath = record.FullPath;
                File.Move(oldPath, newPath);

                _suggestionService.NotifyFileMoved(
                    record, dest, null, _suggestionCache.GetValueOrDefault(record.Id, []));
                _tagStore.UpdatePath(oldPath, newPath);

                var info            = new FileInfo(newPath);
                record.FullPath     = newPath;
                record.LastModified = info.LastWriteTimeUtc;
                record.IsSelected   = false;
                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.FileName}: {ex.Message}");
            }
        }

        StatusText = errors.Count == 0
            ? $"Moved {moved} file(s) to {dest}."
            : $"Moved {moved} file(s); {errors.Count} error(s). First: {errors[0]}";
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    private void ExecuteRename()
    {
        var selected = _allFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        var dialog = new RenameDialog(selected) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
        if (!dialog.Confirmed) return;

        var pattern = dialog.ResultPattern;
        int renamed = 0;
        var errors  = new List<string>();

        var destFolder = dialog.DestinationFolder;

        for (int i = 0; i < selected.Count; i++)
        {
            var record = selected[i];
            try
            {
                var newName = selected.Count == 1
                    ? pattern + record.Extension
                    : ExpandPattern(pattern, record, i + 1);

                var targetDir = destFolder ?? Path.GetDirectoryName(record.FullPath)!;
                var newPath   = Path.Combine(targetDir, newName);
                if (File.Exists(newPath) && !newPath.Equals(record.FullPath, StringComparison.OrdinalIgnoreCase))
                    newPath = MakeUniquePath(newPath);

                var oldPath = record.FullPath;
                File.Move(oldPath, newPath);

                _suggestionService.NotifyFileMoved(
                    record, targetDir, newName, _suggestionCache.GetValueOrDefault(record.Id, []));
                _tagStore.UpdatePath(oldPath, newPath);

                record.FullPath   = newPath;
                record.FileName   = Path.GetFileName(newPath);
                record.IsSelected = false;
                renamed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.FileName}: {ex.Message}");
            }
        }

        StatusText = errors.Count == 0
            ? $"Renamed {renamed} file(s)."
            : $"Renamed {renamed} file(s); {errors.Count} error(s). First: {errors[0]}";
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private void ExecuteDelete()
    {
        var selected = _allFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        var preview = selected.Count == 1
            ? $"\"{selected[0].FileName}\""
            : $"{selected.Count} files";

        var answer = MessageBox.Show(
            $"Move {preview} to the Recycle Bin?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        int deleted = 0;
        var errors  = new List<string>();

        foreach (var record in selected)
        {
            try
            {
                SendToRecycleBin(record.FullPath);
                if (SelectedFile == record)
                {
                    SelectedFile = null;
                }
                _suggestionCache.TryRemove(record.Id, out _);
                record.PropertyChanged -= Record_PropertyChanged;
                Application.Current.Dispatcher.Invoke(() => _allFiles.Remove(record));
                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.FileName}: {ex.Message}");
            }
        }

        StatusText = errors.Count == 0
            ? $"Moved {deleted} file(s) to the Recycle Bin."
            : $"Deleted {deleted} file(s); {errors.Count} error(s). First: {errors[0]}";

        OnPropertyChanged(nameof(FileCount));
        RaiseSelectionChanged();
    }

    // SHFileOperation — sends a file to the Recycle Bin without showing the
    // system confirmation dialog (we already confirmed via MessageBox above).
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint   wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool   fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint   FO_DELETE        = 0x0003;
    private const ushort FOF_ALLOWUNDO    = 0x0040;
    private const ushort FOF_NOCONFIRM    = 0x0010;
    private const ushort FOF_SILENT       = 0x0004;

    private static void SendToRecycleBin(string path)
    {
        // pFrom must be double-null-terminated
        var op = new ShFileOpStruct
        {
            hwnd   = IntPtr.Zero,
            wFunc  = FO_DELETE,
            pFrom  = path + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRM | FOF_SILENT,
        };
        int result = SHFileOperation(ref op);
        if (result != 0)
            throw new IOException($"SHFileOperation failed (0x{result:X8})");
    }

    // ── Pattern expander (public so RenameDialog can call it for preview) ─────

    private static readonly Regex IndexPadded = new(@"\{index:(\d+)\}", RegexOptions.Compiled);

    public static string ExpandPattern(string pattern, FileRecord record, int index)
    {
        var name = Path.GetFileNameWithoutExtension(record.FileName);
        var ext  = record.Extension.TrimStart('.');
        var date = (record.LastModified ?? DateTime.UtcNow).ToString("yyyyMMdd");

        var result = pattern
            .Replace("{name}",  name)
            .Replace("{ext}",   ext)
            .Replace("{index}", index.ToString())
            .Replace("{date}",  date);

        result = IndexPadded.Replace(result, m =>
        {
            var width = int.Parse(m.Groups[1].Value);
            return index.ToString().PadLeft(width, '0');
        });

        // Ensure the extension is present
        if (!result.EndsWith(record.Extension, StringComparison.OrdinalIgnoreCase))
            result += record.Extension;

        return result;
    }

    // ── Find duplicates ───────────────────────────────────────────────────────

    private async void FindDuplicatesAsync()
    {
        _isDetectingDuplicates = true;
        CommandManager.InvalidateRequerySuggested();
        StatusText = "Scanning for duplicates…";

        var snapshot = _allFiles.ToList();
        List<List<FileRecord>> rawGroups;

        try
        {
            rawGroups = await DuplicateDetector.FindAsync(
                snapshot,
                msg => Application.Current.Dispatcher.Invoke(() => StatusText = msg),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate scan error: {ex.Message}";
            return;
        }
        finally
        {
            _isDetectingDuplicates = false;
            CommandManager.InvalidateRequerySuggested();
        }

        if (rawGroups.Count == 0)
        {
            StatusText = "No duplicates found.";
            return;
        }

        StatusText = $"Found {rawGroups.Count} duplicate group(s).";

        var dialog = new Views.DuplicatesDialog(rawGroups) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();

        if (dialog.DeletedRecordIds.Count == 0) return;

        foreach (var id in dialog.DeletedRecordIds)
        {
            var record = _allFiles.FirstOrDefault(f => f.Id == id);
            if (record is null) continue;
            record.PropertyChanged -= Record_PropertyChanged;
            _allFiles.Remove(record);
            _suggestionCache.TryRemove(id, out _);
        }

        if (SelectedFile is not null && dialog.DeletedRecordIds.Contains(SelectedFile.Id))
            SelectedFile = null;

        OnPropertyChanged(nameof(FileCount));
        RaiseSelectionChanged();
        StatusText = $"Removed {dialog.DeletedRecordIds.Count} duplicate(s). {_allFiles.Count:N0} files remaining.";
    }

    // ── Apply suggestion ──────────────────────────────────────────────────────

    private void ApplySuggestion(SuggestionResult? suggestion)
    {
        if (suggestion is null || SelectedFile is null) return;
        var record = SelectedFile;

        var newName  = suggestion.SuggestedName is not null
            ? suggestion.SuggestedName + record.Extension
            : null;
        var destDir  = suggestion.SuggestedFolder ?? Path.GetDirectoryName(record.FullPath)!;
        var fileName = newName ?? record.FileName;
        var newPath  = Path.Combine(destDir, fileName);

        if (File.Exists(newPath) && !newPath.Equals(record.FullPath, StringComparison.OrdinalIgnoreCase))
            newPath = MakeUniquePath(newPath);

        try
        {
            Directory.CreateDirectory(destDir);
            File.Move(record.FullPath, newPath);

            _suggestionService.NotifyFileMoved(
                record, destDir, newName, _suggestionCache.GetValueOrDefault(record.Id, []));

            var info            = new FileInfo(newPath);
            record.FullPath     = newPath;
            record.FileName     = Path.GetFileName(newPath);
            record.LastModified = info.LastWriteTimeUtc;
            CurrentSuggestions  = [];
            StatusText = $"Applied suggestion → {newPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error applying suggestion: {ex.Message}";
        }
    }

    // ── Text search ───────────────────────────────────────────────────────────

    private void AddSearchPattern()
    {
        var text = NewSearchText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var (symbol, color) = DocumentSearchService.GetPatternStyle(SearchPatterns.Count);
        SearchPatterns.Add(new SearchPattern
        {
            Text            = text,
            IsCaseSensitive = NewSearchCaseSensitive,
            IsWholeWord     = NewSearchWholeWord,
            Symbol          = symbol,
            Color           = color,
        });
        NewSearchText = string.Empty;
    }

    private void RemoveSearchPattern(SearchPattern? p)
    {
        if (p is null) return;
        SearchPatterns.Remove(p);
        foreach (var file in _allFiles)
            file.MatchedPatterns.Remove(p);
        OnPropertyChanged(nameof(SearchMatchCount));
    }

    private async void RunSearch()
    {
        _isSearching = true;
        IsSearching  = true;
        _searchCts   = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();

        try
        {
            await DocumentSearchService.RunSearchAsync(
                _allFiles.ToList(),
                SearchPatterns.ToList(),
                msg => Application.Current.Dispatcher.Invoke(() => StatusText = msg),
                _searchCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled.";
        }
        finally
        {
            IsSearching = false;
            _searchCts  = null;
            OnPropertyChanged(nameof(SearchMatchCount));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ClearSearch()
    {
        SearchPatterns.Clear();
        foreach (var file in _allFiles)
            file.MatchedPatterns.Clear();
        OnPropertyChanged(nameof(SearchMatchCount));
    }

    // ── Tagging ───────────────────────────────────────────────────────────────

    private void AddTag(string? name)
    {
        if (SelectedFile is null || string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (SelectedFile.Tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        var tag = _tagStore.GetOrCreate(name);
        _tagStore.AssignTag(SelectedFile.FullPath, tag.Name);
        SelectedFile.Tags.Add(tag);

        if (!AllTags.Contains(tag)) AllTags.Add(tag);

        // Remove from suggestions now that it's applied
        SuggestedTags = SuggestedTags
            .Where(s => !s.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void RemoveTag(TagDefinition? tag)
    {
        if (SelectedFile is null || tag is null) return;
        _tagStore.UnassignTag(SelectedFile.FullPath, tag.Name);
        SelectedFile.Tags.Remove(tag);

        // Re-add to suggestions if it would naturally be suggested
        SuggestedTags = ComputeSuggestedTags(SelectedFile);
    }

    private void CreateAndAddTag()
    {
        AddTag(NewTagText.Trim());
        NewTagText = string.Empty;
    }

    private IList<string> ComputeSuggestedTags(FileRecord r)
    {
        var suggestions = new List<string>();

        switch (r.Category)
        {
            case FileCategory.Document:
            {
                var text = string.Join(" ",
                    r.DocumentTitle,
                    r.DocumentContent?.Title,
                    r.DocumentContent?.HeaderText,
                    r.DocumentContent?.Author).ToLowerInvariant();

                if (text.Contains("chem") || text.Contains("lab"))              suggestions.Add("Chemistry");
                if (text.Contains("homework") || text.Contains("assignment"))    suggestions.Add("Homework");
                if (text.Contains("invoice") || text.Contains("receipt"))        suggestions.Add("Invoice");
                if (text.Contains("contract") || text.Contains("agreement"))     suggestions.Add("Contract");
                if (text.Contains("resume") || text.Contains("curriculum") ||
                    text.Contains(" cv ") || text.Contains("curriculum vitae"))  suggestions.Add("Resume");
                if (text.Contains("essay") || text.Contains("thesis"))           suggestions.Add("Essay");
                if (text.Contains("report"))                                      suggestions.Add("Report");
                if (text.Contains("physics"))                                     suggestions.Add("Physics");
                if (text.Contains("math") || text.Contains("calculus") ||
                    text.Contains("algebra"))                                     suggestions.Add("Math");
                if (text.Contains("biology") || text.Contains("biolog"))         suggestions.Add("Biology");

                if (!string.IsNullOrWhiteSpace(r.DocumentContent?.Author))
                    suggestions.Add(TitleCase(r.DocumentContent.Author.Trim()));

                foreach (var kw in r.DocumentContent?.Keywords ?? [])
                    if (kw.Length > 3) suggestions.Add(TitleCase(kw));
                break;
            }

            case FileCategory.Video:
                if (r.VideoInfo?.EpisodeLabel is not null)  suggestions.Add("TV Show");
                else if (r.VideoInfo?.Year is not null)     suggestions.Add("Movie");

                if (r.VideoInfo?.BestTitle is string title && !string.IsNullOrWhiteSpace(title))
                    suggestions.Add(title);
                break;

            case FileCategory.Image:
                var label = r.ImageGroup switch
                {
                    ImageSubcategory.Icon        => "Icon",
                    ImageSubcategory.Screenshot  => "Screenshot",
                    ImageSubcategory.Wallpaper   => "Wallpaper",
                    ImageSubcategory.GameAsset   => "Game Asset",
                    ImageSubcategory.PersonPhoto => "People",
                    _                            => null
                };
                if (label is not null) suggestions.Add(label);
                break;

            case FileCategory.Audio:
                suggestions.Add("Music");
                break;
        }

        return suggestions
            .Where(s => !r.Tags.Any(t => t.Name.Equals(s, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static string TitleCase(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MakeUniquePath(string path)
    {
        var dir  = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        int n    = 1;
        string candidate;
        do { candidate = Path.Combine(dir, $"{name} ({n++}){ext}"); }
        while (File.Exists(candidate));
        return candidate;
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

        if (CategoryFilters.Any(f => !f.IsChecked))
        {
            var allowed = CategoryFilters.Where(f => f.IsChecked).Select(f => f.Category).ToHashSet();
            if (!allowed.Contains(r.Category)) return false;
        }

        if (ExtensionFilters.Count > 0 && ExtensionFilters.Any(f => !f.IsChecked))
        {
            var allowed = ExtensionFilters.Where(f => f.IsChecked)
                                          .Select(f => f.Extension)
                                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!allowed.Contains(r.Extension)) return false;
        }

        if (r.Category == FileCategory.Image && ImageGroupFilters.Count > 0 && ImageGroupFilters.Any(f => !f.IsChecked))
        {
            var allowed = ImageGroupFilters.Where(f => f.IsChecked).Select(f => f.Group).ToHashSet();
            if (!allowed.Contains(r.ImageGroup)) return false;
        }

        return true;
    }

    public void RefreshFilter() => FileView.Refresh();

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void ApplySort(string column)
    {
        if (string.IsNullOrEmpty(column)) return;

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
        // Cancel any in-flight video OCR from the previously selected file
        _ocrCts?.Cancel();
        _ocrCts = null;

        PreviewImage        = null;
        ShowImagePreview    = false;
        ShowVideoPreview    = false;
        ShowDocumentPreview = false;
        PreviewFilePath     = null;

        if (record is null || !File.Exists(record.FullPath))
        {
            IsPreviewMaximized = false;
            return;
        }

        if (record.Category == FileCategory.Video)
        {
            var thumbnail = VideoThumbnailService.GetThumbnail(record.FullPath);
            if (thumbnail is not null)
            {
                PreviewImage     = thumbnail;
                ShowVideoPreview = true;
            }

            // OCR the thumbnail frame for title-card text
            if (thumbnail is not null)
            {
                _ocrCts = new CancellationTokenSource();
                var ct  = _ocrCts.Token;
                _ = Task.Run(async () =>
                {
                    var title = await VideoTitleOcrService.ScanForTitleAsync(thumbnail, ct);
                    if (title is not null && !ct.IsCancellationRequested)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (SelectedFile?.Id != record.Id) return;
                            var existing = CurrentSuggestions.ToList();
                            existing.Insert(0, new SuggestionResult(
                                SuggestedName: title, SuggestedFolder: null,
                                Confidence: 0.70f, Source: "video-ocr"));
                            CurrentSuggestions = existing;
                        });
                }, ct);
            }

            // Add filename-parsed title as a suggestion too (fast, no OCR needed)
            if (record.VideoInfo?.BestTitle is string parsedTitle
                && string.IsNullOrWhiteSpace(record.VideoInfo.EmbeddedTitle))
            {
                var label = record.VideoInfo.EpisodeLabel is not null
                    ? $"{parsedTitle} {record.VideoInfo.EpisodeLabel}"
                    : parsedTitle;
                CurrentSuggestions = [new SuggestionResult(
                    SuggestedName: label, SuggestedFolder: null,
                    Confidence: 0.55f, Source: "filename")];
            }
        }
        else if (record.Category == FileCategory.Image)
        {
            Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource        = new Uri(record.FullPath);
                    bmp.DecodePixelWidth = 420;
                    bmp.CacheOption      = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions    = BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    bmp.Freeze();

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        PreviewImage     = bmp;
                        ShowImagePreview = true;
                    });
                }
                catch { }
            });
        }
        else if (record.Category == FileCategory.Document)
        {
            PreviewFilePath     = record.FullPath;
            ShowDocumentPreview = true;
        }
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

    public void PopulateImageGroupFilters()
    {
        var prevState = ImageGroupFilters.ToDictionary(f => f.Group, f => f.IsChecked);

        ImageGroupFilters.Clear();

        foreach (var g in _allFiles
            .Where(f => f.Category == FileCategory.Image)
            .GroupBy(f => f.ImageGroup)
            .OrderBy(g => (int)g.Key))
        {
            var filter = new ImageGroupFilter(g.Key, g.Count(), onChanged: () => FileView.Refresh());
            if (prevState.TryGetValue(g.Key, out var wasChecked) && !wasChecked)
                filter.SetCheckedSilent(false);
            ImageGroupFilters.Add(filter);
        }
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

public class ImageGroupFilter : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private int  _count;
    private readonly Action _onChanged;

    public ImageSubcategory Group { get; }

    public string Label => Group switch
    {
        ImageSubcategory.Icon        => "Icons",
        ImageSubcategory.Screenshot  => "Screenshots",
        ImageSubcategory.Wallpaper   => "Wallpapers",
        ImageSubcategory.GameAsset   => "Game Assets",
        ImageSubcategory.PersonPhoto => "People",
        _                            => "Other Images",
    };

    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        }
    }

    public string DisplayLabel => $"{Label} ({Count:N0})";

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); _onChanged(); }
    }

    // Set checked state without firing the filter-refresh callback (used during repopulation)
    internal void SetCheckedSilent(bool value) => _isChecked = value;

    public ImageGroupFilter(ImageSubcategory group, int count, Action onChanged)
    { Group = group; _count = count; _onChanged = onChanged; }

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
