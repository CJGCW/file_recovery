using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Converters;

/// <summary>Formats a byte count as a human-readable string (KB / MB / GB).</summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return string.Empty;
        return bytes switch
        {
            < 1_024                => $"{bytes} B",
            < 1_048_576            => $"{bytes / 1024.0:F1} KB",
            < 1_073_741_824        => $"{bytes / 1_048_576.0:F1} MB",
            _                      => $"{bytes / 1_073_741_824.0:F2} GB"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Converts a FileCategory to a colour for the category badge.</summary>
public class CategoryColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileCategory cat) return Brushes.Gray;
        return cat switch
        {
            FileCategory.Image      => new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)),  // teal
            FileCategory.Video      => new SolidColorBrush(Color.FromRgb(0x7C, 0x6F, 0xF7)),  // purple
            FileCategory.Audio      => new SolidColorBrush(Color.FromRgb(0xD8, 0x5A, 0x30)),  // coral
            FileCategory.Document   => new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),  // blue
            FileCategory.Archive    => new SolidColorBrush(Color.FromRgb(0xBA, 0x75, 0x17)),  // amber
            FileCategory.Code       => new SolidColorBrush(Color.FromRgb(0x63, 0x99, 0x22)),  // green
            FileCategory.Font       => new SolidColorBrush(Color.FromRgb(0xD4, 0x53, 0x7E)),  // pink
            FileCategory.Database   => new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)),  // red
            FileCategory.Executable => new SolidColorBrush(Color.FromRgb(0x88, 0x87, 0x80)),  // gray
            _                       => Brushes.DimGray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Returns Visibility.Visible when the bound bool is true.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Inverts a bool. Useful for IsEnabled bindings.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? false : true;
}

/// <summary>Returns Visibility.Visible when the bound bool is false (inverted).</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>
/// Returns a sort-indicator arrow ("↑" / "↓") for a column header.
/// Parameter = column property name; binding = the ViewModel's SortColumn.
/// </summary>
public class SortIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        var currentCol = values[0] as string;
        var direction  = values[1] is System.ComponentModel.ListSortDirection d ? d : System.ComponentModel.ListSortDirection.Ascending;
        var thisCol    = parameter as string;

        if (currentCol != thisCol) return string.Empty;
        return direction == System.ComponentModel.ListSortDirection.Ascending ? " ↑" : " ↓";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Formats a nullable DateTime as a short date string.</summary>
public class DateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTime dt ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Converts a hex colour string (e.g. "#7C6FF7") to a SolidColorBrush.</summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch { }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Formats a nullable TimeSpan as h:mm:ss or m:ss. Returns empty string for null/zero.</summary>
public class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts || ts == TimeSpan.Zero) return string.Empty;
        return ts.Hours > 0
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Picks one of two brushes by a bool. Used to tint the column-filter
/// chevron purple when its filter is active and grey otherwise.
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush  { get; set; } = Brushes.White;
    public Brush FalseBrush { get; set; } = Brushes.Gray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

