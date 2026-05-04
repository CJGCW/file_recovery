using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Views;

// ── View models used only by this dialog ─────────────────────────────────────

public class DuplicateGroup
{
    public long                    FileSizeBytes { get; init; }
    public List<DuplicateFileItem> Files         { get; init; } = [];
}

public class DuplicateFileItem : INotifyPropertyChanged
{
    private bool _isMarked;

    public FileRecord Record { get; init; } = null!;

    public bool IsMarked
    {
        get => _isMarked;
        set { _isMarked = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Dialog ────────────────────────────────────────────────────────────────────

public partial class DuplicatesDialog : Window
{
    private List<DuplicateGroup> _groups;

    /// <summary>IDs of FileRecords sent to the Recycle Bin during this session.</summary>
    public HashSet<long> DeletedRecordIds { get; } = [];

    public DuplicatesDialog(List<List<FileRecord>> rawGroups)
    {
        InitializeComponent();

        // Wrap raw groups into observable view-model objects, newest-first within each group
        _groups = rawGroups
            .Select(g => new DuplicateGroup
            {
                FileSizeBytes = g[0].FileSizeBytes,
                Files         = g.OrderByDescending(f => f.LastModified ?? DateTime.MinValue)
                                  .Select(f => new DuplicateFileItem { Record = f })
                                  .ToList()
            })
            .OrderByDescending(g => g.FileSizeBytes)   // biggest groups first
            .ToList();

        // Subscribe to checkbox changes for footer count
        foreach (var item in _groups.SelectMany(g => g.Files))
            item.PropertyChanged += (_, _) => UpdateFooter();

        GroupsPanel.ItemsSource = _groups;
        UpdateSummary();
        UpdateFooter();
    }

    // ── Summary / footer ──────────────────────────────────────────────────────

    private void UpdateSummary()
    {
        int removable = _groups.Sum(g => g.Files.Count - 1);
        SummaryText.Text = _groups.Count == 0
            ? "All duplicates resolved."
            : $"{_groups.Count} group(s) — {removable} file(s) can be removed";
    }

    private void UpdateFooter()
    {
        int count = _groups.SelectMany(g => g.Files).Count(f => f.IsMarked);
        MarkedCountText.Text = count == 0 ? "No files marked" : $"{count} file(s) marked for deletion";
        DeleteBtn.IsEnabled  = count > 0;
    }

    // ── Group-level helpers ───────────────────────────────────────────────────

    private static DuplicateGroup GroupFromSender(object sender) =>
        (DuplicateGroup)((Button)sender).Tag;

    private void KeepNewest_Click(object sender, RoutedEventArgs e)
    {
        var group  = GroupFromSender(sender);
        var keeper = group.Files[0]; // already sorted newest-first
        foreach (var f in group.Files) f.IsMarked = f != keeper;
    }

    private void KeepOldest_Click(object sender, RoutedEventArgs e)
    {
        var group  = GroupFromSender(sender);
        var keeper = group.Files[^1]; // last = oldest
        foreach (var f in group.Files) f.IsMarked = f != keeper;
    }

    // ── Global mark-all ───────────────────────────────────────────────────────

    private void MarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _groups)
        {
            var keeper = group.Files[0]; // keep newest in each group
            foreach (var f in group.Files) f.IsMarked = f != keeper;
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private void DeleteMarked_Click(object sender, RoutedEventArgs e)
    {
        var marked = _groups.SelectMany(g => g.Files).Where(f => f.IsMarked).ToList();
        if (marked.Count == 0) return;

        var answer = MessageBox.Show(
            $"Move {marked.Count} file(s) to the Recycle Bin?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        var errors = new List<string>();

        foreach (var item in marked)
        {
            try
            {
                SendToRecycleBin(item.Record.FullPath);
                DeletedRecordIds.Add(item.Record.Id);
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Record.FileName}: {ex.Message}");
            }
        }

        // Remove deleted items from the in-dialog groups
        foreach (var group in _groups)
            group.Files.RemoveAll(f => DeletedRecordIds.Contains(f.Record.Id));

        _groups.RemoveAll(g => g.Files.Count < 2);

        // Re-subscribe to new items (old subscriptions die with removed items)
        foreach (var item in _groups.SelectMany(g => g.Files))
            item.PropertyChanged -= (_, _) => UpdateFooter(); // noop — re-add below
        foreach (var item in _groups.SelectMany(g => g.Files))
            item.PropertyChanged += (_, _) => UpdateFooter();

        // Refresh the ItemsControl
        GroupsPanel.ItemsSource = null;
        GroupsPanel.ItemsSource = _groups;

        UpdateSummary();
        UpdateFooter();

        if (errors.Count > 0)
            MessageBox.Show(
                $"{errors.Count} file(s) could not be deleted:\n{string.Join("\n", errors.Take(5))}",
                "Partial Failure", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── Recycle Bin via SHFileOperation ──────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    private const uint   FO_DELETE     = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRM = 0x0010;
    private const ushort FOF_SILENT    = 0x0004;

    private static void SendToRecycleBin(string path)
    {
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
}
