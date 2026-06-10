using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using FileRecoveryParser.Models;
using FileRecoveryParser.Services;

namespace FileRecoveryParser.Views;

public partial class TagManagementWindow : Window
{
    private readonly FrameTagStore _store;
    private readonly System.Collections.ObjectModel.ObservableCollection<TagRow> _rows = [];
    private string _filter = string.Empty;

    public TagManagementWindow(FrameTagStore store)
    {
        InitializeComponent();
        _store = store;

        foreach (var f in _store.Frames)
            _rows.Add(new TagRow(f));

        TagsGrid.ItemsSource = _rows;
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = FilterRow;
        view.SortDescriptions.Add(new SortDescription(nameof(TagRow.AddedAt), ListSortDirection.Descending));

        RefreshStatus();

        // Backfill thumbnails for old TaggedFrames that pre-date the
        // ThumbnailPng feature: kick off a background pass that pulls a frame
        // from each source file and stores it. Fires asynchronously so the
        // window appears immediately; rows light up as their thumbnails land.
        Loaded += async (_, _) => await BackfillMissingThumbnailsAsync();
    }

    private async Task BackfillMissingThumbnailsAsync()
    {
        var pending = _rows
            .Where(r => !r.HasThumbnail && !r.SourceMissing && !string.IsNullOrEmpty(r.SourceFile))
            .ToList();
        if (pending.Count == 0) return;

        int backfilled = 0;
        foreach (var row in pending)
        {
            var sourcePath = row.SourceFile;
            // Off-thread: file I/O + Media Foundation decode.
            var png = await Task.Run(() => ExtractThumbnailPng(sourcePath));
            if (png is null) continue;

            row.Frame.ThumbnailPng = png;
            row.NotifyThumbnailChanged();
            backfilled++;
        }

        if (backfilled > 0)
        {
            _store.Flush();
            StatusLine.Text = $"{_rows.Count} tag(s) stored • {_rows.Count(r => r.SourceMissing)} have missing source files • backfilled {backfilled} thumbnail(s)";
        }
    }

    // Re-extracts a thumbnail from the recorded source file. For images we
    // load directly via BitmapImage (with DecodePixelWidth so memory stays
    // bounded); for everything else we go through VideoThumbnailService which
    // tries Media Foundation seek points and falls back to the Shell cache.
    private static byte[]? ExtractThumbnailPng(string sourcePath)
    {
        try
        {
            BitmapSource? bmp = null;
            var ext = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();

            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp"
                    or ".tif" or ".tiff" or ".webp")
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption       = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth  = 320;
                img.UriSource         = new Uri(sourcePath);
                img.EndInit();
                img.Freeze();
                bmp = img;
            }
            else
            {
                bmp = VideoThumbnailService.GetThumbnail(sourcePath, 320);
                if (bmp is not null && !bmp.IsFrozen) bmp.Freeze();
            }

            if (bmp is null) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private bool FilterRow(object obj)
    {
        if (obj is not TagRow r) return false;
        if (OrphansOnly.IsChecked == true && !r.SourceMissing) return false;
        if (string.IsNullOrEmpty(_filter)) return true;
        return r.TagName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || r.SourceFile.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text?.Trim() ?? string.Empty;
        CollectionViewSource.GetDefaultView(_rows).Refresh();
    }

    // Orphans-only checkbox click handler.
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_rows).Refresh();
    }

    // Group-by dropdown: maps SelectedIndex → TagRow property to group on.
    // Setting GroupDescriptions on the view makes the DataGrid render the
    // GroupStyle header template defined in XAML for each distinct value.
    private void GroupBy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rows is null) return;
        var view = CollectionViewSource.GetDefaultView(_rows);
        if (view is null) return;
        view.GroupDescriptions.Clear();

        string? path = GroupByBox.SelectedIndex switch
        {
            1 => nameof(TagRow.TagName),
            2 => nameof(TagRow.SourceFile),
            3 => nameof(TagRow.AddedDateGroup),
            4 => nameof(TagRow.ThumbnailGroup),
            _ => null,
        };
        if (path is not null)
            view.GroupDescriptions.Add(new PropertyGroupDescription(path));
    }

    private void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TagRow row)
            DeleteRows([row]);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = TagsGrid.SelectedItems.Cast<TagRow>().ToList();
        if (rows.Count == 0) return;
        var confirm = MessageBox.Show(
            $"Delete {rows.Count} tag(s)? This removes them from frame_tags.json.",
            "Confirm delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        DeleteRows(rows);
    }

    private void DeleteRows(IList<TagRow> rows)
    {
        _store.RemoveMany(rows.Select(r => r.Frame));
        foreach (var r in rows) _rows.Remove(r);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        int total  = _rows.Count;
        int orphan = _rows.Count(r => r.SourceMissing);
        StatusLine.Text = $"{total} tag(s) stored • {orphan} have missing source files";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Commit any pending edits (e.g. user typed a new name and clicked Close).
        foreach (var r in _rows)
            _store.Rename(r.Frame, r.TagName);
        base.OnClosing(e);
    }
}

/// <summary>One DataGrid row backed by a TaggedFrame. Edits to TagName flow back to the underlying frame on close.</summary>
public class TagRow : INotifyPropertyChanged
{
    public TaggedFrame Frame { get; }

    private string _tagName;
    public string TagName
    {
        get => _tagName;
        set { if (_tagName != value) { _tagName = value; OnPropertyChanged(); } }
    }

    public string  SourceFile     => Frame.SourceFile;
    public bool    SourceMissing  => !string.IsNullOrEmpty(Frame.SourceFile) && !File.Exists(Frame.SourceFile);
    public DateTime AddedAt       => Frame.AddedAt;
    public string  AddedAtLabel   => Frame.AddedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public double  PositionSeconds => Frame.SourcePositionSeconds;
    public string  PositionLabel  => Frame.SourcePositionSeconds > 0
                                      ? TimeSpan.FromSeconds(Frame.SourcePositionSeconds).ToString(@"m\:ss")
                                      : "—";
    public bool    HasThumbnail   => Frame.ThumbnailPng is { Length: > 0 };

    // Group-by keys. AddedDateGroup buckets to local day so adjacent tags
    // collapse together; ThumbnailGroup is a user-facing label rather than
    // a raw bool so the group header reads "With thumbnail" / "(no thumbnail)".
    public string  AddedDateGroup => Frame.AddedAt.ToLocalTime().ToString("yyyy-MM-dd");
    public string  ThumbnailGroup => HasThumbnail ? "With thumbnail" : "(no thumbnail)";

    public BitmapSource? ThumbnailImage
    {
        get
        {
            if (Frame.ThumbnailPng is null || Frame.ThumbnailPng.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(Frame.ThumbnailPng);
                var decoder = new PngBitmapDecoder(ms,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var f = decoder.Frames[0];
                f.Freeze();
                return f;
            }
            catch { return null; }
        }
    }

    public TagRow(TaggedFrame frame)
    {
        Frame    = frame;
        _tagName = frame.TagName;
    }

    /// <summary>Re-fire PropertyChanged for the thumbnail-dependent properties
    /// after the backing TaggedFrame.ThumbnailPng has been mutated by backfill.</summary>
    public void NotifyThumbnailChanged()
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ThumbnailImage));
        OnPropertyChanged(nameof(ThumbnailGroup));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
