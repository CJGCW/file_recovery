using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileRecoveryParser.Models;

/// <summary>
/// One checkbox row in a column-filter popup. Bound to a distinct value
/// observed in the data.
/// </summary>
public class ColumnFilterValue : INotifyPropertyChanged
{
    public string Value   { get; }
    public string Display { get; }

    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
    }

    public ColumnFilterValue(string value, string? display = null)
    {
        Value   = value;
        Display = display ?? (string.IsNullOrEmpty(value) ? "(blank)" : value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Per-column filter state — a list of distinct values, each with a check
/// state. Filter is "active" when at least one value is unchecked. The
/// hosting ViewModel calls RebuildValues() to refresh the candidate list
/// from current data, then Apply() to publish the new excluded-set so
/// ApplyFilter on the FileView can use it.
/// </summary>
public class ColumnFilter : INotifyPropertyChanged
{
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ColumnFilterValue> Values { get; } = [];

    public bool IsActive => _excluded.Count > 0;

    public bool IsAllowed(string? value)
    {
        if (!IsActive) return true;
        return !_excluded.Contains(value ?? string.Empty);
    }

    /// <summary>Returns true if at least one of the supplied values is allowed.</summary>
    public bool IsAnyAllowed(IEnumerable<string> values)
    {
        if (!IsActive) return true;
        bool any = false;
        foreach (var v in values)
        {
            any = true;
            if (!_excluded.Contains(v)) return true;
        }
        // No values supplied: treat as a single "(blank)" entry.
        if (!any) return !_excluded.Contains(string.Empty);
        return false;
    }

    /// <summary>Replaces the candidate value list, preserving previously-excluded entries.</summary>
    public void RebuildValues(IEnumerable<string> uniqueValues)
    {
        var sorted = uniqueValues
            .Where(v => v is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Values.Clear();
        foreach (var v in sorted)
            Values.Add(new ColumnFilterValue(v) { IsChecked = !_excluded.Contains(v) });
    }

    /// <summary>
    /// Append a single distinct value to the list if it isn't already
    /// present. Used to incrementally extend the candidate list when new
    /// values appear after the initial RebuildValues — e.g. a fresh tag
    /// name applied via Scan-for-tags should show up in the popup the
    /// next time the user opens it without a full rebuild.
    /// </summary>
    public void MaybeAddValue(string value)
    {
        value ??= string.Empty;
        if (Values.Any(v => string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase)))
            return;
        Values.Add(new ColumnFilterValue(value) { IsChecked = !_excluded.Contains(value) });
    }

    /// <summary>Commit the current checkbox states into the excluded-set used by IsAllowed.</summary>
    public void Apply()
    {
        _excluded = new HashSet<string>(
            Values.Where(v => !v.IsChecked).Select(v => v.Value),
            StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(IsActive));
    }

    public void ClearAndCheckAll()
    {
        foreach (var v in Values) v.IsChecked = true;
        _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(IsActive));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
