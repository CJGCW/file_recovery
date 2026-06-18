using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
    private CancellationTokenSource? _thumbCts;
    private double _timelineValue;
    private double _previewImagePixelWidth;
    private double _previewImagePixelHeight;

    // ── Delete mode (bulk delete by extension under a chosen folder) ─────────
    private bool _isDeleteMode;
    private bool _isDeleting;
    private double _deleteProgress;
    private List<string>? _preDeleteModeCheckedExtensions;

    // ── Manual region selection on image preview ─────────────────────────────
    private System.Windows.Rect _selectedRegion = System.Windows.Rect.Empty;

    // ── Scan-for-tags (modal scan of selected files producing reviewable results) ──
    private bool   _isScanningForTags;
    private int    _scanFilesDone;
    private int    _scanFilesTotal;
    private string _scanCurrentFile = string.Empty;
    private CancellationTokenSource? _scanForTagsCts;
    public ObservableCollection<ScanResult> ScanResults { get; } = [];

    // ── Cached selection counts (kept in sync via Record_PropertyChanged) ────
    // CanExecute is called by CommandManager on every UI tick (focus, key,
    // mouse). With 740k files an O(n) iteration per tick froze the app, so
    // selection counts are maintained incrementally instead.
    private int _selectedCount;
    private readonly int[] _selectedByCategory =
        new int[Enum.GetValues<FileCategory>().Length];

    private bool   _isDeepScanning;
    private double _deepScanProgress;
    private bool   _isIdentifying;
    private AppSettings _appSettings = AppSettings.Load();
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
    private readonly FrameTagStore _frameTagStore = new();
    private readonly ScannedVideoStore _scannedVideoStore = new();
    private string _newTagText = string.Empty;
    private IList<string> _suggestedTags = [];

    // ── In-memory scan cache (lifetime: app session) ─────────────────────────
    // Keyed by FileRecord.FullPath. Stored as a snapshot of the ThumbnailStrip
    // at the time the user last left the file. Skips redoing quick + deep
    // scans when the user revisits a previously-scanned video.
    private readonly Dictionary<string, List<VideoFrame>> _scanCache =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Text search ──────────────────────────────────────────────────────────
    private string _newSearchText        = string.Empty;
    private bool   _newSearchCaseSensitive;
    private bool   _newSearchWholeWord;
    private bool   _isSearching;
    private CancellationTokenSource? _searchCts;

    // ── Collections ──────────────────────────────────────────────────────────

    private readonly ObservableCollection<FileRecord> _allFiles = [];
    public  ICollectionView FileView { get; }

    public ObservableCollection<CategoryExtensionGroup> FileTypeGroups    { get; } = [];
    public ObservableCollection<ImageGroupFilter>        ImageGroupFilters { get; } = [];
    public ObservableCollection<VideoFrame>              ThumbnailStrip    { get; } = [];
    public ObservableCollection<DetectedFaceVm>          DetectedFaces     { get; } = [];

    // Per-column filter state for the main file grid. Each ColumnFilter holds
    // a list of distinct observed values with check-states; when at least one
    // is unchecked, ApplyFilter rejects rows whose value isn't allowed. Values
    // are rebuilt on demand from _allFiles when the user opens the popup.
    public ColumnFilter ExtColumnFilter      { get; } = new();
    public ColumnFilter CategoryColumnFilter { get; } = new();
    public ColumnFilter TagsColumnFilter     { get; } = new();

    private ColumnFilter? GetColumnFilter(string? key) => key switch
    {
        "Extension" => ExtColumnFilter,
        "Category"  => CategoryColumnFilter,
        "Tags"      => TagsColumnFilter,
        _           => null,
    };

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainViewModel()
    {
        _suggestionService = new SuggestionService(new PersonStore());

        FileView = CollectionViewSource.GetDefaultView(_allFiles);
        FileView.Filter = ApplyFilter;
        ((ICollectionView)FileView).CollectionChanged += (_, _) => OnPropertyChanged(nameof(FileCount));

        AllTags        = new ObservableCollection<TagDefinition>(_tagStore.Definitions);
        SearchPatterns = [];
        ThumbnailStrip.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowThumbnailStrip));

        ScanCommand            = new RelayCommand(_ => { if (_ is string s) StartScan(s); else StartScan(FolderPath); }, _ => !IsScanning);
        CancelCommand          = new RelayCommand(_ => { _scanCts?.Cancel(); _searchCts?.Cancel(); },
                                                  _ => IsScanning || _isSearching);
        SortCommand            = new RelayCommand(col => ApplySort(col as string ?? string.Empty));
        ClearCommand           = new RelayCommand(_ => ClearResults(), _ => _allFiles.Count > 0);
        MoveCommand            = new RelayCommand(_ => ExecuteMove(),   _ => SelectedCount > 0);
        RenameCommand          = new RelayCommand(_ => ExecuteRename(), _ => SelectedCount > 0);
        DeleteCommand          = new RelayCommand(_ => ExecuteDelete(), _ => SelectedCount > 0);
        OpenLocationCommand    = new RelayCommand(_ => OpenSelectedLocation(),
                                       _ => SelectedFile is not null || SelectedCount == 1);
        DeleteByExtensionsCommand = new RelayCommand(_ => DeleteByExtensions(),
                                       _ => IsDeleteMode && FileTypeGroups.SelectMany(g => g.Extensions).Any(e => e.IsChecked));
        ScanForTagsCommand     = new RelayCommand(_ => ScanForTagsAsync(),
                                       _ => CanScanForTags());
        CancelScanForTagsCommand = new RelayCommand(_ => _scanForTagsCts?.Cancel(),
                                       _ => _isScanningForTags);
        ManageTagsCommand      = new RelayCommand(_ => OpenTagManagement());
        ApplySuggestionCommand         = new RelayCommand(s => ApplySuggestion(s as SuggestionResult));
        TogglePreviewMaximizeCommand   = new RelayCommand(_ => IsPreviewMaximized = !IsPreviewMaximized);
        FindDuplicatesCommand          = new RelayCommand(_ => FindDuplicatesAsync(),
                                            _ => _allFiles.Count > 0 && !IsScanning && !_isDetectingDuplicates);
        AddTagCommand                  = new RelayCommand(p => AddTag(p as string), _ => SelectedFile is not null);
        RemoveTagCommand               = new RelayCommand(p => RemoveTag(p as TagDefinition), _ => SelectedFile is not null);
        CreateAndAddTagCommand         = new RelayCommand(_ => CreateAndAddTag(), _ => SelectedFile is not null && !string.IsNullOrWhiteSpace(NewTagText));
        TagFrameCommand                = new RelayCommand(p => TagFrame(p as VideoFrame),
                                             _ => SelectedFile is not null && ThumbnailStrip.Count > 0);
        TagSelectedFrameCommand        = new RelayCommand(_ => TagFrame(
                                                             ThumbnailStrip.FirstOrDefault(f => f.IsSelected)
                                                             ?? ThumbnailStrip.FirstOrDefault()),
                                             _ => SelectedFile is not null && ThumbnailStrip.Count > 0);
        TagFaceCommand                 = new RelayCommand(p => TagFace(p as DetectedFaceVm),
                                             _ => SelectedFile is not null);
        TagRegionCommand               = new RelayCommand(_ => TagRegion(),
                                             _ => SelectedFile is not null && !_selectedRegion.IsEmpty && PreviewImage is not null);
        DeepScanCommand                = new RelayCommand(_ => DeepScanAsync(),
                                             _ => ShowVideoPreview && !_isDeepScanning && !_isIdentifying);
        SelectThumbnailCommand         = new RelayCommand(p =>
        {
            if (p is not VideoFrame f) return;
            PreviewImage = f.Image;
            foreach (var t in ThumbnailStrip) t.IsSelected = false;
            f.IsSelected = true;
        });
        IdentifyEpisodeCommand         = new RelayCommand(_ => IdentifyEpisodeAsync(),
                                             _ => ShowVideoPreview && !_isIdentifying && !_isDeepScanning);
        IdentifyIconCommand            = new RelayCommand(_ => IdentifyIconAsync(),
                                             _ => ShowIconIdentify && PreviewImage is not null && !_isIdentifying);
        AddSearchPatternCommand        = new RelayCommand(_ => AddSearchPattern(), _ => !string.IsNullOrWhiteSpace(NewSearchText));
        RemoveSearchPatternCommand     = new RelayCommand(p => RemoveSearchPattern(p as SearchPattern));
        RunSearchCommand               = new RelayCommand(_ => RunSearch(),
                                             _ => SearchPatterns.Count > 0 && _allFiles.Count > 0 && !_isScanning && !_isSearching);
        ClearSearchCommand             = new RelayCommand(_ => ClearSearch(),
                                             _ => SearchPatterns.Count > 0 || _allFiles.Any(f => f.MatchedPatterns.Count > 0));

        // Column-filter commands. Refresh = rebuild the value list from current
        // _allFiles (called when the popup opens). Apply = commit check states
        // + refresh the grid view. Clear = check all + refresh.
        RefreshColumnFilterCommand = new RelayCommand(p =>
        {
            switch (p as string)
            {
                case "Extension": ExtColumnFilter.RebuildValues(
                    _allFiles.Select(f => f.Extension ?? string.Empty)); break;
                case "Category":  CategoryColumnFilter.RebuildValues(
                    _allFiles.Select(f => f.Category.ToString())); break;
                case "Tags":      TagsColumnFilter.RebuildValues(
                    _allFiles.SelectMany(f => f.Tags.Count == 0
                        ? new[] { string.Empty }
                        : f.Tags.Select(t => t.Name))); break;
            }
        });
        ApplyColumnFilterCommand = new RelayCommand(p =>
        {
            switch (p as string)
            {
                case "Extension": ExtColumnFilter.Apply();      break;
                case "Category":  CategoryColumnFilter.Apply(); break;
                case "Tags":      TagsColumnFilter.Apply();     break;
            }
            RefreshFilter();
        });
        ClearColumnFilterCommand = new RelayCommand(p =>
        {
            switch (p as string)
            {
                case "Extension": ExtColumnFilter.ClearAndCheckAll();      break;
                case "Category":  CategoryColumnFilter.ClearAndCheckAll(); break;
                case "Tags":      TagsColumnFilter.ClearAndCheckAll();     break;
            }
            RefreshFilter();
        });

        // In-popup Select all / Deselect all. These flip the checkbox state
        // of every visible row but DON'T commit — the user still has to click
        // Apply for the change to take effect on the grid. This matches the
        // Excel-style filter popup convention.
        SelectAllColumnValuesCommand = new RelayCommand(p =>
        {
            var f = GetColumnFilter(p as string);
            if (f is null) return;
            foreach (var v in f.Values) v.IsChecked = true;
        });
        DeselectAllColumnValuesCommand = new RelayCommand(p =>
        {
            var f = GetColumnFilter(p as string);
            if (f is null) return;
            foreach (var v in f.Values) v.IsChecked = false;
        });

        // Click on an autocomplete suggestion: add the existing tag to the
        // current file and clear the input so the user can type the next one.
        AcceptTagSuggestionCommand = new RelayCommand(p =>
        {
            if (p is not TagDefinition def || SelectedFile is null) return;
            NewTagText = string.Empty;   // also clears TagSuggestions via setter
            AddTag(def.Name);
        });

        // Reopen the Scan Results window with the results from the most
        // recent scan-for-tags run. ScanResults persists on the VM for the
        // lifetime of the session (only cleared when the next scan starts),
        // so the user can close the window, work on files, and come back.
        // Reuses an already-open window via _scanResultsWindow so we don't
        // end up with two windows showing the same data.
        OpenScanResultsCommand = new RelayCommand(_ => ShowScanResultsWindow(),
                                                  _ => ScanResults.Count > 0);

        // Clear any active sort on the main file grid — empties
        // FileView.SortDescriptions and blanks SortColumn so the ↑/↓ indicator
        // disappears from the column header. Disabled when nothing is sorted.
        ClearSortCommand = new RelayCommand(_ => ClearSort(),
                                            _ => IsSorted);

        // Master Select-all / Deselect-all for the sidebar FILE TYPES list.
        // If anything is currently unchecked, we check everything; otherwise
        // (all already on) we uncheck everything. One button, two behaviours,
        // matching the most common "fix this filter" intent in either direction.
        ToggleAllFileTypesCommand = new RelayCommand(_ => ToggleAllFileTypes());

        InitialiseFileTypeGroups();
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
        set { _searchText = value; OnPropertyChanged(); RefreshFilter(); }
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

    public int SelectedCount => _selectedCount;

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
            OnPropertyChanged(nameof(TimelineMax));
            LoadPreview(value);
            CurrentSuggestions = value is not null
                ? _suggestionCache.GetValueOrDefault(value.Id, [])
                : [];
            SuggestedTags = value is not null ? ComputeSuggestedTags(value) : [];
            // The "already on file" filter for autocomplete depends on which
            // file is selected, so refresh suggestions when the file changes.
            UpdateTagSuggestions();
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
        set { _showImagePreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowMediaPreview)); OnPropertyChanged(nameof(ShowAnyPreview)); OnPropertyChanged(nameof(ShowIconIdentify)); }
    }

    public bool ShowIconIdentify    => _showImagePreview && _selectedFile?.ImageGroup == ImageSubcategory.Icon;
    public bool ShowThumbnailStrip  => _showVideoPreview && ThumbnailStrip.Count > 0;
    public bool ShowTimeline        => _showVideoPreview;

    public double TimelineMax => SelectedFile?.Duration?.TotalSeconds ?? 600;

    public double TimelineValue
    {
        get => _timelineValue;
        set
        {
            _timelineValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimelineLabel));
            SeekTimeline(value);
        }
    }

    public string TimelineLabel
    {
        get
        {
            var ts = TimeSpan.FromSeconds(_timelineValue);
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }
    }

    private void SeekTimeline(double seconds)
    {
        // Snap to the nearest cached frame in the strip instead of doing a
        // live MF seek — many recovered files don't seek cleanly, and the
        // strip already holds frames at every 1-second boundary that the
        // sequential deep scan was able to decode.
        if (ThumbnailStrip.Count == 0) return;

        var target = TimeSpan.FromSeconds(seconds);
        VideoFrame? closest = null;
        double      minDiff = double.MaxValue;
        foreach (var f in ThumbnailStrip)
        {
            double diff = Math.Abs((f.Position - target).TotalSeconds);
            if (diff < minDiff) { minDiff = diff; closest = f; }
        }

        if (closest is null) return;
        PreviewImage = closest.Image;
        foreach (var f in ThumbnailStrip) f.IsSelected = false;
        closest.IsSelected = true;
    }

    public bool ShowVideoPreview
    {
        get => _showVideoPreview;
        set { _showVideoPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowMediaPreview)); OnPropertyChanged(nameof(ShowAnyPreview)); OnPropertyChanged(nameof(ShowThumbnailStrip)); OnPropertyChanged(nameof(ShowTimeline)); }
    }

    public bool ShowDocumentPreview
    {
        get => _showDocumentPreview;
        set { _showDocumentPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAnyPreview)); }
    }

    // Inline plain-text preview — used for .txt and other text-like extensions
    // where the Shell preview handler is often missing, broken, or 32-bit-only
    // (which silently fails in our 64-bit host). We just read the file and
    // dump it into a TextBox instead.
    private bool   _showTextPreview;
    private string _textPreviewContent = string.Empty;
    public bool ShowTextPreview
    {
        get => _showTextPreview;
        set { _showTextPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAnyPreview)); }
    }
    public string TextPreviewContent
    {
        get => _textPreviewContent;
        set { _textPreviewContent = value; OnPropertyChanged(); }
    }

    public bool IsDeepScanning
    {
        get => _isDeepScanning;
        private set { _isDeepScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowAnyPreview)); CommandManager.InvalidateRequerySuggested(); }
    }

    public double DeepScanProgress
    {
        get => _deepScanProgress;
        private set { _deepScanProgress = value; OnPropertyChanged(); }
    }

    public bool IsIdentifying
    {
        get => _isIdentifying;
        private set { _isIdentifying = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsDeleteMode
    {
        get => _isDeleteMode;
        set
        {
            if (_isDeleteMode == value) return;
            _isDeleteMode = value;
            if (value) EnterDeleteMode();
            else       ExitDeleteMode();
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        private set { _isDeleting = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public double DeleteProgress
    {
        get => _deleteProgress;
        private set { _deleteProgress = value; OnPropertyChanged(); }
    }


    // The user-drawn region rectangle in original-pixel (oriented) coordinates,
    // set by the image preview overlay's mouse handlers.
    public System.Windows.Rect SelectedRegion
    {
        get => _selectedRegion;
        set { _selectedRegion = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    // ── Scan-for-tags ─────────────────────────────────────────────────────────

    public bool AutoApplyScanResults
    {
        get => _appSettings.AutoApplyScanResults;
        set
        {
            if (_appSettings.AutoApplyScanResults == value) return;
            _appSettings.AutoApplyScanResults = value;
            _appSettings.Save();
            OnPropertyChanged();
        }
    }

    public string ExcludedFolderNames
    {
        get => _appSettings.ExcludedFolderNames ?? string.Empty;
        set
        {
            var v = value ?? string.Empty;
            if (_appSettings.ExcludedFolderNames == v) return;
            _appSettings.ExcludedFolderNames = v;
            _appSettings.Save();
            OnPropertyChanged();
        }
    }

    private HashSet<string> ParseExcludedFolderNames() =>
        new HashSet<string>(
            (_appSettings.ExcludedFolderNames ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    public bool IsScanningForTags
    {
        get => _isScanningForTags;
        private set { _isScanningForTags = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    public int ScanFilesDone
    {
        get => _scanFilesDone;
        private set { _scanFilesDone = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanProgressPercent)); }
    }

    public int ScanFilesTotal
    {
        get => _scanFilesTotal;
        private set { _scanFilesTotal = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanProgressPercent)); }
    }

    public double ScanProgressPercent =>
        _scanFilesTotal > 0 ? _scanFilesDone * 100.0 / _scanFilesTotal : 0;

    public string ScanCurrentFile
    {
        get => _scanCurrentFile;
        private set { _scanCurrentFile = value; OnPropertyChanged(); }
    }

    public string ScanForTagsDisabledReason
    {
        get
        {
            if (_selectedCount == 0)
                return "Check at least one row in the grid.";
            int imageCount = _selectedByCategory[(int)FileCategory.Image];
            int videoCount = _selectedByCategory[(int)FileCategory.Video];
            bool allImage = imageCount == _selectedCount && imageCount > 0;
            bool allVideo = videoCount == _selectedCount && videoCount > 0;
            if (!allImage && !allVideo)
                return "All checked rows must be the same file type (all Video, or all Image).";
            if (_frameTagStore.Frames.Count == 0)
                return "Tag at least one frame or region first.";
            return string.Empty;
        }
    }

    private bool CanScanForTags()
    {
        if (_isScanningForTags) return false;
        if (_selectedCount == 0) return false;
        if (_frameTagStore.Frames.Count == 0) return false;
        int imageCount = _selectedByCategory[(int)FileCategory.Image];
        int videoCount = _selectedByCategory[(int)FileCategory.Video];
        return (imageCount == _selectedCount && imageCount > 0) ||
               (videoCount == _selectedCount && videoCount > 0);
    }

    public double PreviewImagePixelWidth
    {
        get => _previewImagePixelWidth;
        private set { _previewImagePixelWidth = value; OnPropertyChanged(); }
    }

    public double PreviewImagePixelHeight
    {
        get => _previewImagePixelHeight;
        private set { _previewImagePixelHeight = value; OnPropertyChanged(); }
    }

    public bool ShowMediaPreview   => _showImagePreview || _showVideoPreview;
    public bool ShowAnyPreview     => _showImagePreview || _showVideoPreview || _showDocumentPreview || _showTextPreview || _isDeepScanning;

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

    /// <summary>
    /// Autocomplete suggestions for the "Add tag" input. Recomputed on every
    /// NewTagText keystroke by word-prefix matching against AllTags, excluding
    /// tags the currently-selected file already has. Capped at 10 entries.
    /// </summary>
    public ObservableCollection<TagDefinition> TagSuggestions { get; } = [];

    public bool IsTagSuggestionsOpen => TagSuggestions.Count > 0;

    public string NewTagText
    {
        get => _newTagText;
        set
        {
            _newTagText = value;
            OnPropertyChanged();
            UpdateTagSuggestions();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // Word-prefix match: "To" matches "Tom & Jerry" (word "Tom") and
    // "Tiny Toons" (word "Toons"). Cheap enough to recompute on every
    // keystroke — AllTags is typically tens of entries, not thousands.
    private void UpdateTagSuggestions()
    {
        TagSuggestions.Clear();
        var q = (_newTagText ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            OnPropertyChanged(nameof(IsTagSuggestionsOpen));
            return;
        }

        var alreadyOnFile = SelectedFile is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(SelectedFile.Tags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var t in AllTags)
        {
            if (alreadyOnFile.Contains(t.Name)) continue;
            if (!TagMatches(t.Name, q))         continue;
            // Skip an exact match — Enter on the textbox already adds it.
            if (string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase)) continue;

            TagSuggestions.Add(t);
            if (TagSuggestions.Count >= 10) break;
        }

        OnPropertyChanged(nameof(IsTagSuggestionsOpen));
    }

    private static readonly char[] TagWordSeparators = [' ', '_', '-', '.', ',', '&', '/', '\\'];

    private static bool TagMatches(string tag, string query)
    {
        if (tag.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var word in tag.Split(TagWordSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (word.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
    public ICommand MoveCommand            { get; }
    public ICommand RenameCommand          { get; }
    public ICommand ApplySuggestionCommand       { get; }
    public ICommand TogglePreviewMaximizeCommand { get; }
    public ICommand DeleteCommand                { get; }
    public ICommand OpenLocationCommand          { get; }
    public ICommand DeleteByExtensionsCommand    { get; }
    public ICommand ScanForTagsCommand           { get; }
    public ICommand CancelScanForTagsCommand     { get; }
    public ICommand ManageTagsCommand            { get; }
    public ICommand FindDuplicatesCommand        { get; }
    public ICommand AddTagCommand                { get; }
    public ICommand TagFrameCommand              { get; }
    public ICommand TagSelectedFrameCommand      { get; }
    public ICommand TagFaceCommand               { get; }
    public ICommand TagRegionCommand             { get; }
    public ICommand RemoveTagCommand             { get; }
    public ICommand CreateAndAddTagCommand       { get; }
    public ICommand AddSearchPatternCommand      { get; }
    public ICommand RemoveSearchPatternCommand   { get; }
    public ICommand RunSearchCommand             { get; }
    public ICommand ClearSearchCommand           { get; }
    public ICommand DeepScanCommand              { get; }
    public ICommand SelectThumbnailCommand       { get; }
    public ICommand IdentifyEpisodeCommand       { get; }
    public ICommand IdentifyIconCommand          { get; }
    public ICommand RefreshColumnFilterCommand   { get; }
    public ICommand ApplyColumnFilterCommand     { get; }
    public ICommand ClearColumnFilterCommand     { get; }
    public ICommand SelectAllColumnValuesCommand   { get; }
    public ICommand DeselectAllColumnValuesCommand { get; }
    public ICommand AcceptTagSuggestionCommand   { get; }
    public ICommand OpenScanResultsCommand       { get; }
    public ICommand ClearSortCommand             { get; }
    public ICommand ToggleAllFileTypesCommand    { get; }

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

        var allowedExtensions = FileTypeGroups
            .SelectMany(g => g.Extensions.Where(e => e.IsChecked).Select(e => e.Extension))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Treat "nothing checked" the same as "everything checked" — the user
        // most likely just cleared the filter and a literal interpretation
        // (filter out every extension) would silently drop the entire scan.
        bool allExtensionsAllowed = allowedExtensions.Count == 0 ||
            allowedExtensions.Count == FileTypeGroups.Sum(g => g.Extensions.Count);

        var excludedFolders = ParseExcludedFolderNames();
        Task.Run(async () =>
        {
            var scanner = new FileScanner(extraSkippedFolderNames: excludedFolders);
            int found = 0, skipped = 0;

            try
            {
                await foreach (var record in scanner.ScanAsync(path, ct))
                {
                    if (ct.IsCancellationRequested) break;

                    if (!allExtensionsAllowed && !allowedExtensions.Contains(record.Extension))
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
        if (e.PropertyName != nameof(FileRecord.IsSelected)) return;
        if (sender is not FileRecord record) return;

        int catIdx = (int)record.Category;
        bool inRange = catIdx >= 0 && catIdx < _selectedByCategory.Length;
        if (record.IsSelected)
        {
            _selectedCount++;
            if (inRange) _selectedByCategory[catIdx]++;
        }
        else
        {
            _selectedCount--;
            if (inRange) _selectedByCategory[catIdx]--;
        }
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ScanForTagsDisabledReason));
        CommandManager.InvalidateRequerySuggested();
    }

    // Removes a record's PropertyChanged subscription AND adjusts the cached
    // selected counts if the record was selected at removal time. Call this
    // instead of doing the unsubscribe directly when deleting from _allFiles.
    private void DetachRecord(FileRecord record)
    {
        if (record.IsSelected)
        {
            _selectedCount = Math.Max(0, _selectedCount - 1);
            int catIdx = (int)record.Category;
            if (catIdx >= 0 && catIdx < _selectedByCategory.Length)
                _selectedByCategory[catIdx] = Math.Max(0, _selectedByCategory[catIdx] - 1);
        }
        record.PropertyChanged -= Record_PropertyChanged;
    }

    private void ClearResults()
    {
        foreach (var r in _allFiles) r.PropertyChanged -= Record_PropertyChanged;
        _selectedCount = 0;
        Array.Clear(_selectedByCategory);
        _ocrCts?.Cancel();
        _ocrCts = null;
        _allFiles.Clear();
        _suggestionCache.Clear();
        CurrentSuggestions  = [];
        SuggestedTags       = [];
        ImageGroupFilters.Clear();
        ThumbnailStrip.Clear();
        PreviewImage        = null;
        ShowImagePreview    = false;
        ShowVideoPreview    = false;
        ShowDocumentPreview = false;
        ShowTextPreview     = false;
        TextPreviewContent  = string.Empty;
        PreviewFilePath     = null;
        SelectedFile        = null;
        OnPropertyChanged(nameof(ShowMetadata));
        OnPropertyChanged(nameof(FileCount));
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
                DetachRecord(record);
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

    // ── Delete mode ───────────────────────────────────────────────────────────

    private void EnterDeleteMode()
    {
        // Remember the previous extension check state so we can restore it on exit.
        _preDeleteModeCheckedExtensions = FileTypeGroups
            .SelectMany(g => g.Extensions)
            .Where(e => e.IsChecked)
            .Select(e => e.Extension)
            .ToList();

        foreach (var group in FileTypeGroups)
        {
            foreach (var e in group.Extensions) e.SetChecked(false);
            group.NotifyAllCheckedRefreshed();
        }
        RefreshFilter();
        StatusText = "Delete mode — check extensions to remove, then click \"Delete from folder…\".";
    }

    private void ExitDeleteMode()
    {
        if (_preDeleteModeCheckedExtensions is not null)
        {
            var set = new HashSet<string>(_preDeleteModeCheckedExtensions, StringComparer.OrdinalIgnoreCase);
            foreach (var group in FileTypeGroups)
            {
                foreach (var e in group.Extensions) e.SetChecked(set.Contains(e.Extension));
                group.NotifyAllCheckedRefreshed();
            }
            RefreshFilter();
        }
        _preDeleteModeCheckedExtensions = null;
        StatusText = string.Empty;
    }


    // ── Scan-for-tags worker (videos for this milestone) ─────────────────────

    private async void ScanForTagsAsync()
    {
        var selectedFiles = _allFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0) return;
        var cats = selectedFiles.Select(f => f.Category).Distinct().ToList();
        if (cats.Count != 1) return;
        var category = cats[0];
        if (category != FileCategory.Video)
        {
            MessageBox.Show("Only video tag-scan is implemented in this build. " +
                            "Image tag-scan is coming next.",
                "Scan for tags", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ScanResults.Clear();
        ScanFilesTotal = selectedFiles.Count;
        ScanFilesDone  = 0;
        ScanCurrentFile = string.Empty;
        IsScanningForTags = true;

        _scanForTagsCts = new CancellationTokenSource();
        var ct = _scanForTagsCts.Token;
        bool autoApply = AutoApplyScanResults;

        try
        {
            foreach (var record in selectedFiles)
            {
                if (ct.IsCancellationRequested) break;
                ScanCurrentFile = record.FileName;

                var hits = await ScanVideoForTagsAsync(record, ct);
                foreach (var result in hits)
                {
                    if (autoApply)
                    {
                        ApplyMatchedTags(result.FilePath, [result.TagName]);
                        result.IsApplied = true;
                    }
                    Application.Current.Dispatcher.Invoke(() => ScanResults.Add(result));
                }

                ScanFilesDone++;
            }
        }
        catch (OperationCanceledException)
        {
            // User clicked Cancel — exit gracefully without a crash dialog.
            // This is async void so an unhandled exception would surface as
            // a UI-thread crash.
            StatusText = "Scan-for-tags cancelled.";
        }
        finally
        {
            IsScanningForTags = false;
            ScanCurrentFile   = string.Empty;
            _scanForTagsCts?.Dispose();
            _scanForTagsCts = null;
        }

        // Open the results window once the modal is dismissed.
        Application.Current.Dispatcher.Invoke(ShowScanResultsWindow);
    }

    // Lifetime-tracked scan results window. Null when closed; we set it on
    // first open and clear it via the window's Closed event so subsequent
    // calls reuse the same instance if it's still up.
    private Views.ScanResultsWindow? _scanResultsWindow;
    private void ShowScanResultsWindow()
    {
        if (_scanResultsWindow is not null)
        {
            if (_scanResultsWindow.WindowState == WindowState.Minimized)
                _scanResultsWindow.WindowState = WindowState.Normal;
            _scanResultsWindow.Activate();
            return;
        }

        _scanResultsWindow = new Views.ScanResultsWindow(this)
        {
            Owner = Application.Current.MainWindow,
        };
        _scanResultsWindow.Closed += (_, _) => _scanResultsWindow = null;
        _scanResultsWindow.Show();
    }

    private async Task<IList<ScanResult>> ScanVideoForTagsAsync(FileRecord record, CancellationToken ct)
    {
        if (_frameTagStore.Frames.Count == 0) return [];

        // Fast path: video has already been deep-scanned this session or in
        // a previous one. Cached whole-frame hashes are good enough to do
        // dHash matching without re-launching ffmpeg. Crop + embedding
        // matching requires a fresh scan (we don't cache those), but for
        // most matches (logos, title cards, distinctive frames) the whole-
        // frame hash hits first anyway.
        var cached = _scannedVideoStore.TryGet(record.FullPath);
        if (cached is not null)
        {
            return ScanFromCache(cached, record);
        }

        bool anyEmbeddings = _frameTagStore.Frames.Any(f => f.Embedding is { Length: > 0 });
        var bestPerTag = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
        var freshFrames = new List<(TimeSpan Position, ulong Hash)>();

        try
        {
            await VideoThumbnailService.ScanDeepAsync(record.FullPath, 420, ct,
                (pos, bmp) =>
                {
                    // Encode-at-most-once-per-frame. Multiple tags can hit the
                    // same frame (whole-frame dHash + several crops + embedding);
                    // without this each would re-encode the same bitmap. Lazy
                    // defers the PNG encode until the first hit needs it AND
                    // skips it entirely when no tag is improved by this frame.
                    var framePng = new Lazy<byte[]?>(() => EncodeThumbnailPng(bmp, maxWidth: 320));

                    var whole = PerceptualHashService.Compute(bmp);
                    if (whole != 0)
                    {
                        // Record for the persistent cache so future tag-scans
                        // of this video can skip ffmpeg entirely.
                        freshFrames.Add((pos, whole));

                        foreach (var (tag, dist, _) in _frameTagStore.BestDistancePerTag(whole))
                        {
                            if (dist <= FrameTagStore.DefaultMaxDistance)
                                ConsiderHit(tag, dist, $"dHash dist {dist}", framePng, bestPerTag, record);
                        }
                    }

                    foreach (var crop in GenerateMatchingCrops(bmp))
                    {
                        var ch = PerceptualHashService.Compute(crop);
                        if (ch != 0)
                        {
                            foreach (var (tag, dist, _) in _frameTagStore.BestDistancePerTag(ch))
                            {
                                if (dist <= FrameTagStore.DefaultCropMaxDistance)
                                    ConsiderHit(tag, dist, $"crop dHash dist {dist}", framePng, bestPerTag, record);
                            }
                        }
                        if (anyEmbeddings)
                        {
                            var emb = _suggestionService.GetEmbeddingFromCrop(crop);
                            if (emb is not null)
                                foreach (var (tag, sim, _) in _frameTagStore.BestSimilarityPerTag(emb))
                                {
                                    if (sim >= FrameTagStore.DefaultCosineThreshold)
                                        ConsiderHit(tag, 1000.0 - sim, $"cosine {sim:F3}", framePng, bestPerTag, record);
                                }
                        }
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the user's intent — propagate so the outer
            // scan loop stops, rather than silently treating cancel as
            // "scan complete, no hits".
            throw;
        }
        catch (Exception ex)
        {
            // One bad file shouldn't kill the whole scan, but the user should
            // see what went wrong rather than be told everything is fine.
            Application.Current.Dispatcher.Invoke(() =>
                StatusText = $"Tag-scan error on {record.FileName}: {ex.Message}");
        }

        // Persist the freshly computed hashes so the next scan of this video
        // can hit the cache. Only save when we have a complete (or near-
        // complete) set; an empty list means the scan failed before yielding
        // any frame and we don't want to mask a future retry with junk data.
        if (freshFrames.Count > 0)
            _scannedVideoStore.Save(record.FullPath, freshFrames);

        return bestPerTag.Values.ToList();
    }

    // Matches a video's cached whole-frame hashes against every known tag
    // without touching ffmpeg. No thumbnail can be produced for these hits —
    // ScanResult.FromCache = true signals the UI to render a "cached" badge
    // in place of the thumbnail.
    private IList<ScanResult> ScanFromCache(ScannedVideo cached, FileRecord record)
    {
        var bestPerTag = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in cached.Frames)
        {
            if (frame.Hash == 0) continue;
            foreach (var (tag, dist, _) in _frameTagStore.BestDistancePerTag(frame.Hash))
            {
                if (dist > FrameTagStore.DefaultMaxDistance) continue;
                if (bestPerTag.TryGetValue(tag, out var existing) && existing.MatchStrength <= dist)
                    continue;
                bestPerTag[tag] = new ScanResult
                {
                    FilePath           = record.FullPath,
                    FileName           = record.FileName,
                    TagName            = tag,
                    MatchStrength      = dist,
                    MatchStrengthLabel = $"dHash dist {dist} (cached)",
                    ThumbnailPng       = null,
                    FromCache          = true,
                };
            }
        }
        return bestPerTag.Values.ToList();
    }

    private static void ConsiderHit(
        string tag, double strength, string label, Lazy<byte[]?> framePng,
        Dictionary<string, ScanResult> bestPerTag, FileRecord record)
    {
        if (bestPerTag.TryGetValue(tag, out var existing) && existing.MatchStrength <= strength)
            return;

        // .Value triggers EncodeThumbnailPng on the first hit in this frame,
        // returns the cached bytes for any subsequent hits. Encoded while
        // the bmp's pixel buffer is still alive on the scan thread (the
        // ffmpeg pipe tears it down at end of scan, so we can't defer).
        bestPerTag[tag] = new ScanResult
        {
            FilePath           = record.FullPath,
            FileName           = record.FileName,
            TagName            = tag,
            MatchStrength      = strength,
            MatchStrengthLabel = label,
            ThumbnailPng       = framePng.Value,
        };
    }

    /// <summary>
    /// Encodes a BitmapSource to PNG bytes for persistent thumbnail storage,
    /// capped at MaxWidth so the JSON file doesn't bloat. Returns null on failure.
    /// </summary>
    private static byte[]? EncodeThumbnailPng(BitmapSource? src, int maxWidth = 200)
    {
        if (src is null) return null;
        try
        {
            BitmapSource toEncode = src;
            if (src.PixelWidth > maxWidth)
            {
                double scale = maxWidth / (double)src.PixelWidth;
                var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                scaled.Freeze();
                toEncode = scaled;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(toEncode));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // Generates crops at four relative sizes (75%, 50%, 33%, 20% of the image)
    // each placed at multiple positions across a grid. Covers small faces,
    // medium objects, and large regions without exploding the search space.
    private static IEnumerable<BitmapSource> GenerateMatchingCrops(BitmapSource src)
    {
        int W = src.PixelWidth;
        int H = src.PixelHeight;
        if (W < 32 || H < 32) yield break;

        // (fraction, grid steps per axis) — denser sampling at smaller sizes
        // since that's where faces and logos typically live.
        var passes = new (double Frac, int Steps)[]
        {
            (0.75, 2),
            (0.50, 3),
            (0.33, 4),
            (0.20, 4),
        };

        foreach (var (frac, steps) in passes)
        {
            int cw = (int)(W * frac);
            int ch = (int)(H * frac);
            if (cw < 24 || ch < 24) continue;

            for (int gy = 0; gy < steps; gy++)
            for (int gx = 0; gx < steps; gx++)
            {
                int cx = steps > 1 ? gx * (W - cw) / (steps - 1) : (W - cw) / 2;
                int cy = steps > 1 ? gy * (H - ch) / (steps - 1) : (H - ch) / 2;
                cx = Math.Clamp(cx, 0, W - cw);
                cy = Math.Clamp(cy, 0, H - ch);
                yield return new CroppedBitmap(src, new System.Windows.Int32Rect(cx, cy, cw, ch));
            }
        }
    }


    private void OpenSelectedLocation()
    {
        // Prefer the selection (single row), otherwise fall back to the
        // currently-previewed file.
        var target = _allFiles.FirstOrDefault(f => f.IsSelected) ?? SelectedFile;
        if (target is null) return;
        OpenInExplorer(target.FullPath);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            // Quotes around the path so spaces and commas don't get split.
            var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }

    private void OpenTagManagement()
    {
        var win = new Views.TagManagementWindow(_frameTagStore)
        {
            Owner = Application.Current.MainWindow
        };
        win.Show();
    }

    public void ApplyScanResult(ScanResult result)
    {
        if (result.IsApplied) return;
        ApplyMatchedTags(result.FilePath, [result.TagName]);
        result.IsApplied = true;
    }

    // Persist matches + update the live grid if the file happens to be loaded.
    private void ApplyMatchedTags(string path, IList<string> tagNames)
    {
        foreach (var name in tagNames)
        {
            var def = _tagStore.GetOrCreate(name);
            _tagStore.AssignTag(path, def.Name);

            var record = _allFiles.FirstOrDefault(f =>
                string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (record is not null && !record.Tags.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                record.Tags.Add(def);

            if (!AllTags.Contains(def)) AllTags.Add(def);

            // Surface any brand-new tag name in the Tags column-filter popup
            // without a full _allFiles rebuild. ObservableCollection raises
            // CollectionChanged, so an open popup updates live.
            TagsColumnFilter.MaybeAddValue(def.Name);
        }
    }

    private async void DeleteByExtensions()
    {
        var checkedExts = FileTypeGroups.SelectMany(g => g.Extensions)
                                        .Where(e => e.IsChecked)
                                        .Select(e => e.Extension.ToLowerInvariant())
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (checkedExts.Count == 0)
        {
            MessageBox.Show("Check at least one file extension to delete.",
                "Delete mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Use whatever folder is already in the sidebar — no picker prompt.
        var folder = FolderPath?.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(
                "Set a folder in the sidebar first (type a path or use the “…” browse button).",
                "Delete mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText = $"Enumerating {string.Join(", ", checkedExts.OrderBy(e => e))} under {folder}…";
        var toDelete   = new List<string>();
        long totalBytes = 0;

        await Task.Run(() =>
        {
            foreach (var path in SafeEnumerateFiles(folder))
            {
                // GetEffectiveExtension recognises PhotoRec-style names
                // (e.g. "f0002227_memdiag_exe" → ".exe").
                if (!checkedExts.Contains(FileTypeDetector.GetEffectiveExtension(path))) continue;
                toDelete.Add(path);
                try { totalBytes += new FileInfo(path).Length; } catch { }
            }
        });

        if (toDelete.Count == 0)
        {
            MessageBox.Show($"No matching files found under:\n{folder}",
                "Delete mode", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText = string.Empty;
            return;
        }

        var examples = toDelete.Take(3).Select(Path.GetFileName).ToList();
        var msg =
            $"Move {toDelete.Count:N0} file(s) to the Recycle Bin?\n\n" +
            $"Folder:      {folder}\n" +
            $"Extensions:  {string.Join(", ", checkedExts.OrderBy(e => e))}\n" +
            $"Total size:  {FormatBytes(totalBytes)}\n" +
            $"Examples:    {string.Join(", ", examples)}\n\n" +
            "Files can be restored from the Recycle Bin.";

        var answer = MessageBox.Show(msg, "Confirm delete by type",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        // Batched recycle-bin delete + sync the in-memory list (in case the
        // user is deleting from the same folder they previously scanned).
        IsDeleting     = true;
        DeleteProgress = 0;

        const int chunkSize = 500;
        int   deleted = 0;
        var   errors  = new List<string>();
        var   pathSet = toDelete.ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < toDelete.Count; i += chunkSize)
                {
                    var chunk = toDelete.Skip(i).Take(chunkSize).ToList();
                    try
                    {
                        SendManyToRecycleBin(chunk);
                        deleted += chunk.Count;
                    }
                    catch (Exception ex) { errors.Add($"Chunk @{i}: {ex.Message}"); }

                    var progress = deleted / (double)toDelete.Count * 100.0;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        DeleteProgress = progress;
                        StatusText     = $"Deleting… {deleted:N0} of {toDelete.Count:N0}";
                    });
                }
            });
        }
        finally
        {
            IsDeleting     = false;
            DeleteProgress = 0;
        }

        // Drop any in-memory FileRecords whose paths we just deleted.
        var goneRecords = _allFiles.Where(f => pathSet.Contains(f.FullPath)).ToList();
        foreach (var record in goneRecords)
        {
            if (SelectedFile == record) SelectedFile = null;
            _suggestionCache.TryRemove(record.Id, out _);
            DetachRecord(record);
            _allFiles.Remove(record);
        }

        StatusText = errors.Count == 0
            ? $"Moved {deleted:N0} file(s) to the Recycle Bin."
            : $"Deleted {deleted:N0}; {errors.Count} error(s). First: {errors[0]}";

        OnPropertyChanged(nameof(FileCount));
        RaiseSelectionChanged();
    }

    // Manual recursive walk that:
    //   • catches every exception per directory (permissions, IO, path-too-long, etc.)
    //   • skips well-known untouchable system folders by name at every level
    // so a single bad subtree can't kill the entire enumeration.
    private static readonly HashSet<string> SkippedFolderNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "System Volume Information",
            "$RECYCLE.BIN",
            "$Recycle.Bin",
            "Config.Msi",
            "Recovery",
        };

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Materialise via ToList so any lazy-throw lands inside the try.
            List<string>? subdirs = null;
            try { subdirs = Directory.EnumerateDirectories(dir).ToList(); } catch { }

            if (subdirs is not null)
            {
                foreach (var sd in subdirs)
                {
                    var name = Path.GetFileName(sd);
                    if (SkippedFolderNames.Contains(name)) continue;
                    stack.Push(sd);
                }
            }

            List<string>? files = null;
            try { files = Directory.EnumerateFiles(dir).ToList(); } catch { }

            if (files is not null)
                foreach (var f in files)
                    yield return f;
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024;
        if (bytes < KB)       return $"{bytes} B";
        if (bytes < KB*KB)    return $"{bytes / KB:F1} KB";
        if (bytes < KB*KB*KB) return $"{bytes / (KB*KB):F1} MB";
        return $"{bytes / (KB*KB*KB):F2} GB";
    }

    // Batched SHFileOperation — pFrom is a buffer of null-terminated paths
    // terminated by an extra null, so the OS deletes them all in one call.
    private static void SendManyToRecycleBin(IList<string> paths)
    {
        if (paths.Count == 0) return;
        var pFrom = string.Join("\0", paths) + "\0\0";
        var op = new ShFileOpStruct
        {
            hwnd   = IntPtr.Zero,
            wFunc  = FO_DELETE,
            pFrom  = pFrom,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRM | FOF_SILENT,
        };
        int result = SHFileOperation(ref op);
        if (result != 0)
            throw new IOException($"SHFileOperation failed (0x{result:X8})");
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
            DetachRecord(record);
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
        TagsColumnFilter.MaybeAddValue(tag.Name);

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

    private async void TagRegion()
    {
        if (SelectedFile is null || PreviewImage is null || _selectedRegion.IsEmpty) return;

        var dialog = new Views.TagInputDialog(prompt: "Tag this region as:")
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.TagName;
        if (string.IsNullOrWhiteSpace(name)) return;

        var record = SelectedFile;

        // SelectedRegion is in *oriented original* image coords (the grid is
        // sized to those). PreviewImage may be a downscaled version of the
        // original. Map the region into PreviewImage coords for cropping.
        double sx = PreviewImage.PixelWidth  / Math.Max(1.0, PreviewImagePixelWidth);
        double sy = PreviewImage.PixelHeight / Math.Max(1.0, PreviewImagePixelHeight);
        int cx = (int)Math.Max(0, _selectedRegion.X      * sx);
        int cy = (int)Math.Max(0, _selectedRegion.Y      * sy);
        int cw = (int)Math.Min(_selectedRegion.Width  * sx, PreviewImage.PixelWidth  - cx);
        int ch = (int)Math.Min(_selectedRegion.Height * sy, PreviewImage.PixelHeight - cy);
        if (cw < 4 || ch < 4) return;

        try
        {
            var crop = new CroppedBitmap(PreviewImage, new System.Windows.Int32Rect(cx, cy, cw, ch));
            crop.Freeze();
            var hash = PerceptualHashService.Compute(crop);

            // Also compute a MobileFaceNet embedding so scans can identity-
            // match the same person across photos via cosine similarity.
            StatusText = "Computing embedding…";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var embedding = await _suggestionService.GetEmbeddingFromCropAsync(
                crop, cts.Token,
                status: msg => Application.Current.Dispatcher.Invoke(() => StatusText = msg));

            if (SelectedFile?.Id != record.Id) return;

            var thumb = EncodeThumbnailPng(crop);
            _frameTagStore.Add(hash, name, record.FullPath, TimeSpan.Zero, embedding, thumb);
            AddTag(name);
            StatusText = embedding is null
                ? $"Tagged region as \"{name}\" (visual hash only — embedding unavailable)."
                : $"Tagged region as \"{name}\" with embedding.";
        }
        catch (Exception ex)
        {
            StatusText = $"Tag region failed: {ex.Message}";
        }
    }

    private void TagFace(DetectedFaceVm? face)
    {
        if (face is null || SelectedFile is null) return;

        var dialog = new Views.TagInputDialog(
            prompt:  "Name this face:",
            initial: face.DisplayName ?? "")
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.TagName;
        if (string.IsNullOrWhiteSpace(name)) return;

        // Update PersonStore so the name propagates to other images of the
        // same face via cosine-similarity matching.
        var person = _suggestionService.NameFace(face.Embedding, name);
        face.Person      = person;
        face.DisplayName = name;

        // Also assign the name as a regular file tag so it shows up on the
        // file alongside the visual frame-match / OCR tags.
        AddTag(name);

        StatusText = $"Tagged face as \"{name}\".";
    }

    private void TagFrame(VideoFrame? frame)
    {
        if (frame is null || SelectedFile is null) return;

        var dialog = new Views.TagInputDialog(
            prompt:  $"Tag this frame as (saves visual fingerprint at {frame.Label}):",
            initial: "")
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var tagName = dialog.TagName;
        if (string.IsNullOrWhiteSpace(tagName)) return;

        // 1. Persist the frame's perceptual hash + tag.
        var hash = PerceptualHashService.Compute(frame.Image);
        if (hash != 0)
        {
            var thumb = EncodeThumbnailPng(frame.Image);
            _frameTagStore.Add(hash, tagName, SelectedFile.FullPath, frame.Position, thumbnailPng: thumb);
        }

        // 2. Also assign the tag to the current file via the existing tag system,
        //    so the visual tag immediately shows up on the file too.
        AddTag(tagName);

        StatusText = $"Tagged frame at {frame.Label} as \"{tagName}\".";
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

    // Matches "<base> (N)" so we can strip an explicit numeric suffix the
    // user may have typed and treat all "name", "name (1)", "name (2)" as
    // one family.
    private static readonly System.Text.RegularExpressions.Regex NumberedSuffixPattern =
        new(@"^(.+?)\s+\((\d+)\)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Pick a free path by continuing the "(N)" numbering from the highest N
    /// already present in the directory — not by filling the lowest gap.
    /// Example: with "X.mkv", "X (1).mkv", "X (5).mkv" present, renaming a
    /// new file to "X.mkv" produces "X (6).mkv" (not "X (2).mkv"). Avoids
    /// recycling indices the user has previously used.
    /// </summary>
    private static string MakeUniquePath(string path)
    {
        var dir  = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);

        // Strip a trailing "(N)" so the user typing "X (1).mkv" still
        // continues the X family — they don't end up with "X (1) (2).mkv".
        var stripped = NumberedSuffixPattern.Match(name);
        if (stripped.Success) name = stripped.Groups[1].Value;

        int highest = 0;
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s+\((\d+)\)" +
            System.Text.RegularExpressions.Regex.Escape(ext) + @"$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        try
        {
            foreach (var existing in Directory.EnumerateFiles(dir))
            {
                var match = pattern.Match(Path.GetFileName(existing));
                if (match.Success && int.TryParse(match.Groups[1].Value, out int n) && n > highest)
                    highest = n;
            }
        }
        catch { /* permission / IO error — fall through, next index is highest+1=1 */ }

        // Continue from highest + 1. The While loop is a last-resort guard
        // against a concurrent write that lands between our scan and the
        // File.Move caller — never expected to iterate in practice.
        int next = highest + 1;
        string candidate;
        do { candidate = Path.Combine(dir, $"{name} ({next++}){ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }

    // ── Filtering ─────────────────────────────────────────────────────────────

    // Cached filter sets, rebuilt only when the user changes a checkbox or
    // RefreshFilter() is called. Before: ApplyFilter rebuilt these HashSets
    // per file × 740k files × every refresh = the slow filter many of our
    // status messages have blamed for "scan got slower again".
    // null  → cache empty, will be populated on the next call.
    // count == 0 → no filter is active (everything passes).
    private HashSet<string>? _filterAllowedExtensions;
    private HashSet<ImageSubcategory>? _filterAllowedImageGroups;

    private void InvalidateFilterCache()
    {
        _filterAllowedExtensions  = null;
        _filterAllowedImageGroups = null;
    }

    private void EnsureFilterCache()
    {
        if (_filterAllowedExtensions is null)
        {
            int checkedExts = 0, totalExts = 0;
            foreach (var g in FileTypeGroups)
                foreach (var e in g.Extensions)
                {
                    totalExts++;
                    if (e.IsChecked) checkedExts++;
                }
            // Empty set means "no filter active" — see Zero-checked = no filter
            // convention used by StartScan. Sentinel-empty avoids a separate
            // _isExtensionFilterActive flag.
            if (checkedExts == 0 || checkedExts == totalExts)
                _filterAllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            else
                _filterAllowedExtensions = FileTypeGroups
                    .SelectMany(g => g.Extensions.Where(e => e.IsChecked).Select(e => e.Extension))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (_filterAllowedImageGroups is null)
        {
            if (ImageGroupFilters.Count == 0 || ImageGroupFilters.All(f => f.IsChecked))
                _filterAllowedImageGroups = [];
            else
                _filterAllowedImageGroups = ImageGroupFilters
                    .Where(f => f.IsChecked).Select(f => f.Group).ToHashSet();
        }
    }

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

        EnsureFilterCache();

        // Extension filter — empty cache = no filter active (zero-checked or
        // all-checked, see the cache-build comment above).
        if (_filterAllowedExtensions!.Count > 0 && !_filterAllowedExtensions.Contains(r.Extension))
            return false;

        if (r.Category == FileCategory.Image && _filterAllowedImageGroups!.Count > 0
            && !_filterAllowedImageGroups.Contains(r.ImageGroup))
            return false;

        // Per-column filters from the grid header dropdowns. Each is a no-op
        // (returns true immediately) when no values have been unchecked.
        if (!ExtColumnFilter.IsAllowed(r.Extension))           return false;
        if (!CategoryColumnFilter.IsAllowed(r.Category.ToString())) return false;
        if (!TagsColumnFilter.IsAnyAllowed(r.Tags.Select(t => t.Name))) return false;

        return true;
    }

    /// <summary>
    /// Re-evaluate the FileView filter. Before refreshing, drops the selection
    /// for any record the new filter hides — otherwise hidden-but-selected rows
    /// would still be picked up by Move / Rename / Delete / Apply-tag operations
    /// (and silently rename or tag the wrong files, which is exactly what the
    /// "I named everything wrong" bug looked like).
    /// </summary>
    public void RefreshFilter()
    {
        // Filter state may have changed (sidebar checkbox, column dropdown,
        // search text). Drop the cache so EnsureFilterCache rebuilds it once
        // for this whole refresh pass instead of per-file.
        InvalidateFilterCache();
        foreach (var r in _allFiles)
        {
            if (r.IsSelected && !ApplyFilter(r))
                r.IsSelected = false;
        }
        FileView.Refresh();
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    /// <summary>True when an active SortDescription is on the FileView. Drives
    /// the visibility / enabled state of the "Clear sort" button. Uses the
    /// view's actual SortDescriptions count rather than the SortColumn field
    /// so the button doesn't appear-and-do-nothing on a fresh session before
    /// the user has clicked any header.</summary>
    public bool IsSorted => FileView.SortDescriptions.Count > 0;

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
        OnPropertyChanged(nameof(IsSorted));
    }

    private void ClearSort()
    {
        FileView.SortDescriptions.Clear();
        SortColumn = string.Empty;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(IsSorted));
    }

    /// <summary>True when at least one extension across all FileTypeGroups is
    /// unchecked. Drives the "Select all" / "Deselect all" label on the toggle.</summary>
    public bool HasUncheckedFileType =>
        FileTypeGroups.Any(g => g.Extensions.Any(e => !e.IsChecked));

    private void ToggleAllFileTypes()
    {
        bool target = HasUncheckedFileType;   // if anything is off → turn all on
        foreach (var group in FileTypeGroups)
        {
            foreach (var e in group.Extensions) e.SetChecked(target);
            group.NotifyAllCheckedRefreshed();
        }
        OnPropertyChanged(nameof(HasUncheckedFileType));
        RefreshFilter();
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private async void LoadPreview(FileRecord? record)
    {
        // Cancel any in-flight thumbnail loading or OCR from the previous selection
        _thumbCts?.Cancel();
        _thumbCts = null;
        _ocrCts?.Cancel();
        _ocrCts   = null;

        PreviewImage        = null;
        ShowImagePreview    = false;
        ShowVideoPreview    = false;
        ShowDocumentPreview = false;
        ShowTextPreview     = false;
        TextPreviewContent  = string.Empty;
        PreviewFilePath     = null;
        IsDeepScanning      = false;
        ThumbnailStrip.Clear();
        DetectedFaces.Clear();
        _timelineValue = 0;
        OnPropertyChanged(nameof(TimelineValue));
        OnPropertyChanged(nameof(TimelineLabel));

        if (record is null || !File.Exists(record.FullPath))
        {
            IsPreviewMaximized = false;
            return;
        }

        if (record.Category == FileCategory.Video)
        {
            var cts = new CancellationTokenSource();
            _thumbCts = cts;
            var ct = cts.Token;

            BitmapSource? firstFrame = null;

            // ── Cache hit: restore the previously-scanned strip without rescanning ──
            if (_scanCache.TryGetValue(record.FullPath, out var cached) && cached.Count > 0)
            {
                bool first = true;
                foreach (var f in cached)
                {
                    f.IsSelected = first;
                    ThumbnailStrip.Add(f);
                    if (first)
                    {
                        firstFrame       = f.Image;
                        PreviewImage     = f.Image;
                        ShowVideoPreview = true;
                        first            = false;
                    }
                }
            }
            else
            {
                // ── Quick scan: populate thumbnail strip ──────────────────────
                await VideoThumbnailService.GetQuickFramesAsync(
                    record.FullPath, 420, ct,
                    (pos, bmp) => Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested || SelectedFile?.Id != record.Id) return;
                        var isFirst = ThumbnailStrip.Count == 0;
                        var frame   = new VideoFrame(bmp, pos) { IsSelected = isFirst };
                        ThumbnailStrip.Add(frame);
                        if (isFirst)
                        {
                            firstFrame       = bmp;
                            PreviewImage     = bmp;
                            ShowVideoPreview = true;
                        }
                    }));

                if (ct.IsCancellationRequested) return;

                // Cache the quick-scan result.
                if (ThumbnailStrip.Count > 0)
                    _scanCache[record.FullPath] = ThumbnailStrip.ToList();
            }

            // OCR first captured frame for title-card text
            if (firstFrame is not null)
            {
                _ocrCts = new CancellationTokenSource();
                var ocrCt  = _ocrCts.Token;
                var ocrBmp = firstFrame;
                _ = Task.Run(async () =>
                {
                    var title = await VideoTitleOcrService.ScanForTitleAsync(ocrBmp, ocrCt);
                    if (title is not null && !ocrCt.IsCancellationRequested)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (SelectedFile?.Id != record.Id) return;
                            var existing = CurrentSuggestions.ToList();
                            existing.Insert(0, new SuggestionResult(
                                SuggestedName: title, SuggestedFolder: null,
                                Confidence: 0.70f, Source: "video-ocr"));
                            CurrentSuggestions = existing;
                        });
                }, ocrCt);
            }

            // Filename-parsed title suggestion
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

            // Deep scan is on-demand — user presses the "Deep Scan" button.
        }
        else if (record.Category == FileCategory.Image)
        {
            var faceCts = new CancellationTokenSource();
            _thumbCts   = faceCts;   // reused as the image-preview cancel slot
            var faceCt  = faceCts.Token;

            _ = Task.Run(() =>
            {
                try
                {
                    // Load with EXIF orientation applied so the preview matches
                    // what face detection sees (and so phone-portrait photos
                    // aren't displayed sideways).
                    var oriented = FaceRecognitionService.LoadOrientedBitmap(record.FullPath);
                    int origW = oriented.PixelWidth;
                    int origH = oriented.PixelHeight;

                    // Downscale large photos for the preview to keep RAM bounded.
                    double scale = origW > 1280 ? 1280.0 / origW : 1.0;
                    BitmapSource preview = scale < 1.0
                        ? new TransformedBitmap(oriented, new System.Windows.Media.ScaleTransform(scale, scale))
                        : oriented;
                    if (!preview.IsFrozen) preview.Freeze();

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (SelectedFile?.Id != record.Id) return;
                        PreviewImage            = preview;
                        PreviewImagePixelWidth  = origW;
                        PreviewImagePixelHeight = origH;
                        ShowImagePreview        = true;
                    });
                }
                catch { }
            });

            // Detect & match faces on a worker, then post DetectedFaceVm's to the UI.
            // AnalyzeFacesAsync now lazily downloads + initialises the ONNX models
            // on first use, so we don't need a prior scan to have completed.
            _ = Task.Run(async () =>
            {
                if (faceCt.IsCancellationRequested) return;
                try
                {
                    Application.Current.Dispatcher.Invoke(() => StatusText = "Detecting faces…");
                    var faces = await _suggestionService.AnalyzeFacesAsync(
                        record.FullPath, faceCt,
                        status: msg => Application.Current.Dispatcher.Invoke(() => StatusText = msg));
                    if (faceCt.IsCancellationRequested) return;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (SelectedFile?.Id != record.Id) return;
                        DetectedFaces.Clear();
                        foreach (var f in faces)
                        {
                            DetectedFaces.Add(new DetectedFaceVm
                            {
                                X           = f.Box.X,
                                Y           = f.Box.Y,
                                Width       = f.Box.Width,
                                Height      = f.Box.Height,
                                Embedding   = f.Embedding,
                                Person      = f.MatchedPerson,
                                DisplayName = f.MatchedPerson?.Name,
                            });
                        }
                        StatusText = faces.Count > 0
                            ? $"Detected {faces.Count} face(s) — right-click a box to tag."
                            : "No faces detected in this image.";
                    });
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusText = $"Face detection error: {ex.Message}");
                }
            }, faceCt);
        }
        else if (record.Category == FileCategory.Document || record.Category == FileCategory.Code)
        {
            if (IsTextLikeExtension(record.Extension))
            {
                // Read inline — bypass the Shell preview handler entirely.
                // Off-thread so a 256 KB read from a slow recovery drive
                // doesn't stall the UI. Re-check selection on the way back —
                // user might have clicked away while we were reading.
                ShowTextPreview    = true;
                TextPreviewContent = "Loading…";
                var path = record.FullPath;
                var content = await Task.Run(() => ReadTextPreview(path));
                if (SelectedFile is not null && SelectedFile.FullPath == path)
                    TextPreviewContent = content;
            }
            else
            {
                PreviewFilePath     = record.FullPath;
                ShowDocumentPreview = true;
            }
        }
    }

    private static readonly HashSet<string> TextLikeExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".md", ".markdown", ".csv", ".tsv",
            ".json", ".xml", ".html", ".htm", ".css", ".js", ".ts",
            ".yaml", ".yml", ".ini", ".conf", ".cfg", ".toml",
            ".py", ".rb", ".pl", ".sh", ".bat", ".ps1",
            ".c", ".cpp", ".h", ".hpp", ".cs", ".java", ".kt",
            ".go", ".rs", ".swift", ".sql", ".r",
        };

    private static bool IsTextLikeExtension(string ext) =>
        !string.IsNullOrEmpty(ext) && TextLikeExtensions.Contains(ext);

    // Hard cap so a stray 2 GB log file doesn't OOM the preview panel. Anything
    // past the limit gets truncated with a marker line so the user knows.
    private const int TextPreviewByteLimit = 256 * 1024;

    // BOM-aware text read. StreamReader with detectEncodingFromByteOrderMarks
    // sniffs UTF-8 / UTF-16-LE / UTF-16-BE / UTF-32 BOMs and decodes the file
    // accordingly. Files with no BOM fall back to the constructor's UTF-8 —
    // good enough for previewing recovery output, where most text files are
    // UTF-8 or have a BOM if they aren't.
    private static string ReadTextPreview(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long total = fs.Length;
            int  toRead = (int)Math.Min(total, TextPreviewByteLimit);
            var buf = new byte[toRead];
            int read = fs.Read(buf, 0, toRead);

            using var ms = new MemoryStream(buf, 0, read);
            using var sr = new StreamReader(ms, System.Text.Encoding.UTF8,
                                            detectEncodingFromByteOrderMarks: true);
            string content = sr.ReadToEnd();

            if (total > toRead)
                content += $"\n\n── truncated — showing first {toRead / 1024} KB of {total / 1024} KB ──";
            return content;
        }
        catch (Exception ex)
        {
            return $"(Could not read file: {ex.Message})";
        }
    }

    // ── Episode identification ────────────────────────────────────────────────

    private async void DeepScanAsync()
    {
        if (SelectedFile is null || _thumbCts is null) return;

        var record = SelectedFile;
        var ct     = _thumbCts.Token;

        IsDeepScanning   = true;
        DeepScanProgress = 0;
        StatusText       = "Deep scanning...";
        var progress = new Progress<double>(p => DeepScanProgress = p);
        int added = 0;
        // Accumulated on the scan thread (not UI thread) so we can persist
        // a single batch at the end. ConcurrentBag would be overkill — the
        // VideoThumbnailService callback is invoked serially from one
        // worker thread.
        var freshFrames = new List<(TimeSpan Position, ulong Hash)>();
        try
        {
            await VideoThumbnailService.ScanDeepAsync(
                record.FullPath, 420, ct,
                (pos, bmp) =>
                {
                    // Side-channel: compute the hash off the UI thread and
                    // tuck it away for the persistent cache before we hop
                    // the dispatcher for the strip update.
                    var hash = PerceptualHashService.Compute(bmp);
                    if (hash != 0) freshFrames.Add((pos, hash));

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested || SelectedFile?.Id != record.Id) return;

                        // Insert chronologically; skip if a frame at this exact position already exists.
                        int idx = 0;
                        while (idx < ThumbnailStrip.Count && ThumbnailStrip[idx].Position < pos) idx++;
                        if (idx < ThumbnailStrip.Count && ThumbnailStrip[idx].Position == pos) return;
                        ThumbnailStrip.Insert(idx, new VideoFrame(bmp, pos));
                        added++;
                    });
                },
                progress);
            if (ct.IsCancellationRequested || SelectedFile?.Id != record.Id) return;

            StatusText = added > 0
                ? $"Deep scan complete — {added} frame{(added == 1 ? "" : "s")} added."
                : "Deep scan: no additional frames found.";

            // Update the in-memory cache so revisiting this file restores the
            // post-deep-scan strip without rescanning.
            if (ThumbnailStrip.Count > 0)
                _scanCache[record.FullPath] = ThumbnailStrip.ToList();

            // Persist the whole-frame hashes so a later scan-for-tags of this
            // video can skip ffmpeg entirely.
            if (freshFrames.Count > 0)
                _scannedVideoStore.Save(record.FullPath, freshFrames);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Deep scan failed: {ex.Message}"; }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsDeepScanning   = false;
                DeepScanProgress = 0;
            }
        }
    }

    private async void IdentifyEpisodeAsync()
    {
        if (SelectedFile is null) return;

        IsIdentifying = true;

        var record    = SelectedFile;
        var stripSnap = ThumbnailStrip.ToList();
        StatusText    = "Identifying...";

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        try
        {
            // ── Embedded container metadata + subtitles (free, local, fast) ──
            // For ripped media these are by far the highest-signal sources:
            // MakeMKV/Handbrake tags carry the title, and embedded subs
            // reveal show identifiers without any frame decoding.
            var metaTask    = FfmpegMetadataService.ReadMetadataAsync(record.FullPath, ct);
            var subsTask    = FfmpegMetadataService.ExtractSubtitleCuesAsync(record.FullPath, 20, ct);

            // ── OCR every cached frame in parallel ──────────────────────────
            // Aggregating across the whole strip catches title cards, crawl
            // text, captions, and disambiguating info wherever they appear.
            var ocrTasks = stripSnap
                .Select(f => Task.Run(() => VideoTitleOcrService.ScanAllTextAsync(f.Image, ct), ct))
                .ToArray();
            var perFrameLines = ocrTasks.Length > 0 ? await Task.WhenAll(ocrTasks) : [];

            var meta = await metaTask;
            var subs = await subsTask;

            // Aggregate unique lines preserving first-seen order. OCR within a
            // frame is already sorted largest-font-first, which surfaces title
            // text ahead of crawl paragraphs.
            var aggLines = new List<string>();
            var seen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lines in perFrameLines)
                foreach (var line in lines)
                    if (seen.Add(line)) aggLines.Add(line);

            var results = new List<SuggestionResult>();

            // ── Container metadata title (highest signal when present) ───────
            // ffmpeg labels chapter timestamps with key "title" too; require
            // it to contain a letter so we don't pick up "00:00:09.259".
            if (meta.TryGetValue("title", out var metaTitle)
                && !string.IsNullOrWhiteSpace(metaTitle)
                && metaTitle.Any(char.IsLetter))
            {
                results.Add(new SuggestionResult(
                    SuggestedName:   metaTitle.Trim(),
                    SuggestedFolder: null,
                    Confidence:      0.95f,
                    Source:          "metadata"));
            }

            // ── Embedded subtitle cues ───────────────────────────────────────
            if (subs.Count > 0)
            {
                results.Add(new SuggestionResult(
                    SuggestedName:   string.Join("  •  ", subs.Take(3)),
                    SuggestedFolder: null,
                    Confidence:      0.55f,
                    Source:          "subtitles"));
            }

            // ── OpenSubtitles hash lookup (highest-confidence signal) ────────
            // OSDB hash + XML-RPC lookup. No API key needed; rate-limited via
            // the "TemporaryUserAgent" anonymous identity.
            try
            {
                StatusText = "Looking up file hash on OpenSubtitles...";
                var hash = await Task.Run(() => OsdbHashService.Compute(record.FullPath), ct);
                if (hash is not null)
                {
                    var match = await OpenSubtitlesService.LookupByHashAsync(hash, ct);
                    if (match is not null)
                    {
                        string name = (match.Season is not null && match.Episode is not null)
                            ? $"{match.MovieName} S{match.Season:D2}E{match.Episode:D2}"
                            : match.Year is not null
                                ? $"{match.MovieName} ({match.Year})"
                                : match.MovieName!;
                        string folder = match.Kind == "episode" && match.Season is not null
                            ? $"{match.MovieName}\\Season {match.Season:D2}\\"
                            : "Movies\\";
                        results.Add(new SuggestionResult(
                            SuggestedName:   name,
                            SuggestedFolder: folder,
                            Confidence:      0.98f,
                            Source:          "opensubtitles"));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* OpenSubtitles XML-RPC is best-effort */ }

            if (aggLines.Count > 0)
            {
                results.Add(new SuggestionResult(
                    SuggestedName:   string.Join("  •  ", aggLines.Take(5)),
                    SuggestedFolder: null,
                    Confidence:      0.45f,
                    Source:          "ocr"));
            }

            // ── Frame-match tags (visual fingerprint propagation) ────────────
            // Compute the dHash of every cached strip frame, look each up
            // against the user's tagged-frame library, and surface every tag
            // that has a visually-similar saved frame.
            if (stripSnap.Count > 0 && _frameTagStore.Frames.Count > 0)
            {
                var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await Task.Run(() =>
                {
                    foreach (var f in stripSnap)
                    {
                        if (ct.IsCancellationRequested) break;
                        var hash = PerceptualHashService.Compute(f.Image);
                        if (hash == 0) continue;
                        foreach (var tag in _frameTagStore.Match(hash))
                            matched.Add(tag);
                    }
                }, ct);

                foreach (var tag in matched)
                {
                    results.Add(new SuggestionResult(
                        SuggestedName:   tag,
                        SuggestedFolder: null,
                        Confidence:      0.80f,
                        Source:          "frame-match"));
                }
            }

            if (stripSnap.Count > 0)
            {
                // Best Vision candidate: the frame with the most extracted text
                // (likely a title card or info-rich frame). Falls back to frame 0.
                int bestIdx = 0, bestLen = 0;
                for (int i = 0; i < perFrameLines.Length; i++)
                {
                    int len = perFrameLines[i].Sum(l => l.Length);
                    if (len > bestLen) { bestLen = len; bestIdx = i; }
                }
                var visionFrame = stripSnap[bestIdx].Image;

                // ── Tesseract OCR on sampled frames ─────────────────────────
                // Tesseract handles stylized fonts (yellow-on-black title
                // cards, perspective text, etc.) better than Windows.Media.Ocr.
                // It's CPU-heavy so we sample evenly across the strip.
                if (TesseractOcrService.IsAvailable)
                {
                    StatusText = "Running Tesseract OCR on sampled frames...";
                    const int MaxTessFrames = 20;
                    int step = Math.Max(1, stripSnap.Count / MaxTessFrames);
                    var sampled = new List<VideoFrame>();
                    for (int i = 0; i < stripSnap.Count; i += step)
                        sampled.Add(stripSnap[i]);

                    var tessTasks = sampled
                        .Select(f => TesseractOcrService.ScanLinesAsync(f.Image, ct))
                        .ToArray();
                    var tessResults = await Task.WhenAll(tessTasks);
                    var tessLines = new List<string>();
                    foreach (var lines in tessResults)
                        foreach (var line in lines)
                            if (seen.Add(line)) { tessLines.Add(line); aggLines.Add(line); }

                    if (tessLines.Count > 0)
                    {
                        results.Add(new SuggestionResult(
                            SuggestedName:   string.Join("  •  ", tessLines.Take(5)),
                            SuggestedFolder: null,
                            Confidence:      0.50f,
                            Source:          "tesseract"));
                    }
                }

                StatusText = "Looking up identifying info...";

                // ── Reverse image search — no key needed ─────────────────────
                var imgQuery = await Task.Run(
                    () => ReverseImageSearchService.SearchAsync(visionFrame, ct), ct);
                if (imgQuery is not null)
                {
                    results.Add(new SuggestionResult(
                        SuggestedName:   imgQuery,
                        SuggestedFolder: null,
                        Confidence:      0.65f,
                        Source:          "image-search"));
                }

                // ── Vision + TMDB — optional, only when keys are configured ──
                if (!string.IsNullOrWhiteSpace(_appSettings.GoogleVisionKey))
                {
                    try
                    {
                        var visionResults = await Task.Run(
                            () => EpisodeIdentifier.IdentifyAsync(visionFrame, aggLines, _appSettings, ct), ct);
                        results.AddRange(visionResults);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Vision is optional enrichment */ }
                }
            }

            if (SelectedFile?.Id != record.Id) return;

            if (results.Count > 0)
            {
                var replaced = new HashSet<string>
                    { "metadata", "subtitles", "opensubtitles", "ocr", "tesseract",
                      "frame-match", "image-search", "vision", "vision+tmdb" };
                CurrentSuggestions = results
                    .Concat(CurrentSuggestions.Where(s => !replaced.Contains(s.Source)))
                    .ToList();
                StatusText = $"Found {results.Count} result(s).";
            }
            else
            {
                StatusText = "No identifying info found across the scanned frames.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Identification failed: {ex.Message}";
        }
        finally
        {
            IsIdentifying = false;
        }
    }

    private async void IdentifyIconAsync()
    {
        if (PreviewImage is null || SelectedFile is null) return;

        if (string.IsNullOrWhiteSpace(_appSettings.GoogleVisionKey))
        {
            ConfigureApiKeys();
            if (string.IsNullOrWhiteSpace(_appSettings.GoogleVisionKey)) return;
        }

        IsIdentifying = true;
        StatusText    = "Identifying icon...";

        var record = SelectedFile;
        var frame  = PreviewImage;

        try
        {
            var logos = await Task.Run(() =>
                GoogleVisionService.DetectLogoAsync(frame, _appSettings.GoogleVisionKey));

            if (SelectedFile?.Id != record.Id) return;

            if (logos.Count > 0)
            {
                var results = logos.Take(3)
                    .Select(l => new SuggestionResult(
                        SuggestedName:   $"{l.Name}{record.Extension}",
                        SuggestedFolder: $"Icons\\{l.Name}\\",
                        Confidence:      l.Score,
                        Source:          "vision+logo"))
                    .ToList<SuggestionResult>();

                var merged = results.Concat(CurrentSuggestions
                    .Where(s => s.Source != "vision+logo"))
                    .ToList();
                CurrentSuggestions = merged;
                StatusText = $"Identified as: {logos[0].Name}";
            }
            else
            {
                StatusText = "No logo match found.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Icon identification failed: {ex.Message}";
        }
        finally
        {
            IsIdentifying = false;
        }
    }

    private void ConfigureApiKeys()
    {
        var dialog = new Views.ApiKeyDialog(_appSettings)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        _appSettings.GoogleVisionKey = dialog.VisionKey;
        _appSettings.TmdbKey         = dialog.TmdbKey;
        _appSettings.Save();
    }

    // ── Category filter setup ────────────────────────────────────────────────

    private void InitialiseFileTypeGroups()
    {
        var categoryOrder = new[]
        {
            FileCategory.Image, FileCategory.Video, FileCategory.Audio,
            FileCategory.Document, FileCategory.Archive, FileCategory.Code,
            FileCategory.Font, FileCategory.Database, FileCategory.Executable,
        };

        var byCategory = Data.ExtensionMap.Entries
            .GroupBy(kvp => kvp.Value.Category)
            .ToDictionary(g => g.Key, g => g.Select(kvp => kvp.Key).OrderBy(e => e).ToList());

        foreach (var cat in categoryOrder)
        {
            if (!byCategory.TryGetValue(cat, out var exts)) continue;

            var group = new CategoryExtensionGroup(cat, CategoryLabel(cat),
                onGroupChanged: () =>
                {
                    // Notify HasUncheckedFileType so the sidebar
                    // "Select all" / "Deselect all" toggle's label flips
                    // immediately when an individual extension is checked
                    // or unchecked — otherwise the label stays stale and
                    // the next click looks like it's doing the wrong thing.
                    OnPropertyChanged(nameof(HasUncheckedFileType));
                    RefreshFilter();
                });

            foreach (var ext in exts)
                group.AddExtension(ext);

            FileTypeGroups.Add(group);
        }
    }

    private static string CategoryLabel(FileCategory cat) => cat switch
    {
        FileCategory.Image      => "Images",
        FileCategory.Video      => "Videos",
        FileCategory.Audio      => "Audio",
        FileCategory.Document   => "Documents",
        FileCategory.Archive    => "Archives",
        FileCategory.Code       => "Code",
        FileCategory.Font       => "Fonts",
        FileCategory.Database   => "Databases",
        FileCategory.Executable => "Executables",
        _                       => cat.ToString(),
    };

    public void PopulateImageGroupFilters()
    {
        var prevState = ImageGroupFilters.ToDictionary(f => f.Group, f => f.IsChecked);

        ImageGroupFilters.Clear();

        foreach (var g in _allFiles
            .Where(f => f.Category == FileCategory.Image)
            .GroupBy(f => f.ImageGroup)
            .OrderBy(g => (int)g.Key))
        {
            var filter = new ImageGroupFilter(g.Key, g.Count(), onChanged: () => RefreshFilter());
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

public class CategoryExtensionGroup : INotifyPropertyChanged
{
    private bool _isExpanded;
    private readonly Action _onGroupChanged;

    public FileCategory Category { get; }
    public string       Label    { get; }
    public ObservableCollection<ExtensionFilter> Extensions { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; RaisePropertyChanged(); }
    }

    // Tri-state: true=all, false=none, null=mixed
    public bool? IsAllChecked
    {
        get
        {
            int n = Extensions.Count(e => e.IsChecked);
            if (n == Extensions.Count) return true;
            if (n == 0) return false;
            return null;
        }
        set
        {
            bool check = value ?? false; // clicking from all-checked (→ indeterminate) → uncheck all
            foreach (var ext in Extensions)
                ext.SetChecked(check);
            RaisePropertyChanged();
            _onGroupChanged();
        }
    }

    internal void AddExtension(string ext)
    {
        Extensions.Add(new ExtensionFilter(ext, onChanged: () =>
        {
            RaisePropertyChanged(nameof(IsAllChecked));
            _onGroupChanged();
        }));
    }

    /// <summary>
    /// Forces the tri-state master checkbox to re-evaluate its IsChecked
    /// binding after a bulk SetChecked() pass on individual extensions.
    /// </summary>
    public void NotifyAllCheckedRefreshed() =>
        RaisePropertyChanged(nameof(IsAllChecked));

    public CategoryExtensionGroup(FileCategory category, string label, Action onGroupChanged)
    { Category = category; Label = label; _onGroupChanged = onGroupChanged; }

    private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            _onChanged();
        }
    }

    // Bulk-update path: fires PropertyChanged but not _onChanged (caller handles refresh)
    internal void SetChecked(bool value)
    {
        _isChecked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
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
