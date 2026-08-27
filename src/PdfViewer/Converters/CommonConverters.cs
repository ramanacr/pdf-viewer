using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PdfViewer.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool flag => flag,
            int count => count > 0,
            _ => value != null
        };

        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNull = value == null;
        if (Invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}

public class EnumToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return Visibility.Collapsed;
        bool match = value.ToString()!.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        if (Invert) match = !match;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// Multiplies a normalized coordinate (0.0 to 1.0) with the container's display width/height.
/// values[0] = double normalized coordinate
/// values[1] = double display dimension (DisplayWidth or DisplayHeight)
/// </summary>
public class NormalizedCoordinateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double norm &&
            values[1] is double dim)
        {
            return Math.Max(0, norm * dim);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts SearchMatch.IsCurrentMatch to Lime-Green (#6632CD32) or Gold (#99FFD700) background brush.
/// </summary>
public class SearchHighlightBackgroundConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush LimeGreenBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0x32, 0xCD, 0x32)); // 40% Lime-Green
    private static readonly System.Windows.Media.Brush ActiveGoldBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB2, 0xFF, 0xD7, 0x00)); // 70% Gold

    static SearchHighlightBackgroundConverter()
    {
        LimeGreenBg.Freeze();
        ActiveGoldBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isCurrent && isCurrent)
            return ActiveGoldBg;
        return LimeGreenBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts SearchMatch.IsCurrentMatch to Lime-Green or Orange border brush.
/// </summary>
public class SearchHighlightBorderConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush LimeGreenBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x22, 0x8B, 0x22)); // Forest/Lime border
    private static readonly System.Windows.Media.Brush ActiveGoldBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00)); // Dark Orange border

    static SearchHighlightBorderConverter()
    {
        LimeGreenBorder.Freeze();
        ActiveGoldBorder.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isCurrent && isCurrent)
            return ActiveGoldBorder;
        return LimeGreenBorder;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// Converts a hex color string into a WPF SolidColorBrush.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;
                return brush;
            }
            catch { }
        }
        return System.Windows.Media.Brushes.LimeGreen;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
