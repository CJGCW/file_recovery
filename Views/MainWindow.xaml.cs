using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FileRecoveryParser.Models;
using FileRecoveryParser.ViewModels;
using Microsoft.Win32;

namespace FileRecoveryParser.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    // ── Manual region selection ──────────────────────────────────────────────
    private bool _regionDrawing;
    private Point _regionStart;
    private Rectangle? _liveRegionBox;

    public MainWindow()
    {
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsScanning) && !Vm.IsScanning)
            {
                Vm.PopulateImageGroupFilters();
                // Rebuild column-header filter value lists from the freshly
                // scanned data so the Ext/Type/Tags popups have something to show.
                Vm.RefreshColumnFilterCommand.Execute("Extension");
                Vm.RefreshColumnFilterCommand.Execute("Category");
                Vm.RefreshColumnFilterCommand.Execute("Tags");
            }
            // Scan-for-tags can apply new tag names to existing files; refresh
            // the Tags-column dropdown so newly-applied tags become filterable.
            else if (e.PropertyName == nameof(MainViewModel.IsScanningForTags) && !Vm.IsScanningForTags)
            {
                Vm.RefreshColumnFilterCommand.Execute("Tags");
            }
            // Slider scrubbing updates TimelineValue, which selects the nearest
            // frame via SeekTimeline. Pan the strip's ScrollViewer so that
            // frame is visible — otherwise the time changes but the user is
            // looking at thumbnails for a different part of the video.
            else if (e.PropertyName == nameof(MainViewModel.TimelineValue))
            {
                ScrollSelectedThumbnailIntoView();
            }
        };
    }

    // Locates the StackPanel child for whichever ThumbnailStrip item has
    // IsSelected=true and asks WPF to scroll it into the parent ScrollViewer's
    // viewport. Deferred to Loaded priority so any pending layout (e.g. a just-
    // added thumbnail mid-scan) is realized first.
    private void ScrollSelectedThumbnailIntoView()
    {
        var selected = Vm.ThumbnailStrip.FirstOrDefault(f => f.IsSelected);
        if (selected is null) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ThumbnailStripItems.ItemContainerGenerator.ContainerFromItem(selected)
                is FrameworkElement container)
            {
                container.BringIntoView();
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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

    // ── Manual region selection (drag-to-draw rectangle for tagging) ─────────

    private void ManualRegion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        _regionStart   = e.GetPosition(canvas);
        _regionDrawing = true;

        // Clear any previous selection rectangle so only one is ever visible.
        canvas.Children.Clear();

        var accent = (Brush)FindResource("Accent");
        _liveRegionBox = new Rectangle
        {
            Stroke          = accent,
            StrokeThickness = 4,
            StrokeDashArray = [4, 2],
            Fill            = new SolidColorBrush(Color.FromArgb(40, 0x7C, 0x6F, 0xF7)),
            Cursor          = Cursors.Hand,
        };
        Canvas.SetLeft(_liveRegionBox, _regionStart.X);
        Canvas.SetTop(_liveRegionBox, _regionStart.Y);
        canvas.Children.Add(_liveRegionBox);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void ManualRegion_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_regionDrawing || _liveRegionBox is null || sender is not Canvas canvas) return;
        var p = e.GetPosition(canvas);
        double x = Math.Min(_regionStart.X, p.X);
        double y = Math.Min(_regionStart.Y, p.Y);
        double w = Math.Abs(p.X - _regionStart.X);
        double h = Math.Abs(p.Y - _regionStart.Y);
        Canvas.SetLeft(_liveRegionBox, x);
        Canvas.SetTop(_liveRegionBox, y);
        _liveRegionBox.Width  = w;
        _liveRegionBox.Height = h;
    }

    private void ManualRegion_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        canvas.ReleaseMouseCapture();
        _regionDrawing = false;

        if (_liveRegionBox is null) return;

        // Discard tiny drags so misclicks don't leave a stray dot.
        if (_liveRegionBox.Width < 8 || _liveRegionBox.Height < 8)
        {
            canvas.Children.Remove(_liveRegionBox);
            _liveRegionBox = null;
            Vm.SelectedRegion = Rect.Empty;
            return;
        }

        double x = Canvas.GetLeft(_liveRegionBox);
        double y = Canvas.GetTop(_liveRegionBox);
        Vm.SelectedRegion = new Rect(x, y, _liveRegionBox.Width, _liveRegionBox.Height);

        // Right-click on the rectangle to tag.
        _liveRegionBox.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                new MenuItem
                {
                    Header  = "Tag this region…",
                    Command = Vm.TagRegionCommand,
                },
            }
        };
    }
}
