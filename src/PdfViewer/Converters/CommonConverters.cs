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
/// True when two bindings resolve to the same object.
///
/// Used to mark the active tab. Reference identity rather than equality on purpose: two
/// documents opened from the same path would still be different tabs, and only the one the
/// shell is actually showing should look selected.
/// </summary>
public class SameInstanceConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] != null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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

/// <summary>
/// Turns an annotation's freehand strokes into a drawable geometry.
///
/// Nothing bound to the ink before this existed, so a pen stroke was recorded and then never
/// drawn - the drawing vanished the moment the pen was lifted. One figure per stroke keeps
/// separate strokes separate instead of joining them with a line across the page.
///
/// The points stay in the page's normalized space and the shape is drawn with Stretch="Fill"
/// inside the annotation's own box, which is the bounding box of the strokes.
/// </summary>
public class InkStrokesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var geometry = new System.Windows.Media.StreamGeometry();
        if (value is not System.Collections.Generic.IEnumerable<System.Collections.Generic.List<Point>> strokes)
        {
            return geometry;
        }

        using (var context = geometry.Open())
        {
            foreach (var stroke in strokes)
            {
                if (stroke == null || stroke.Count < 2) continue;

                context.BeginFigure(stroke[0], isFilled: false, isClosed: false);
                context.PolyLineTo(stroke.GetRange(1, stroke.Count - 1), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// A line, arrow, polygon or connected lines drawn over its box: values are the annotation and
/// the box's drawn width and height (further values, such as its points and line width, only make
/// the binding update when they change). The parameter "Body" draws only the lines (which may be
/// dashed), "Ends" only the line endings (always solid); none draws both. The value at
/// <see cref="ScaleIndex"/>, when given, is the page's zoom (pixels per point), which sizes the
/// endings as the engine draws them at that zoom.
/// </summary>
public class ShapeGeometryConverter : IMultiValueConverter
{
    /// <summary>Where the page's display scale is among the values (after the ones that only trigger updates).</summary>
    public const int ScaleIndex = 10;

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 3 && values[0] is PdfViewer.Models.AnnotationModel a && values[1] is double w && values[2] is double h
            ? a.ShapeGeometry(w, h, body: parameter as string != "Ends", ends: parameter as string != "Body",
                scale: values.Length > ScaleIndex && values[ScaleIndex] is double s ? s : 1)
            : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// The turn of free text drawn over its box: values are its /Rotate (degrees counter-clockwise,
/// null when it has none) and the page's own /Rotate, which applies when it has none. The text is
/// turned back by that much, so with the overlay turned with the page it reads upright, as the
/// engine draws it.
/// </summary>
public class TextTurnConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double turn = Angle(values.Length > 0 && values[0] is int own ? own : null, values.Length > 1 && values[1] is int page ? page : 0);
        var transform = new System.Windows.Media.RotateTransform(turn);
        transform.Freeze();
        return transform;
    }

    /// <summary>The clockwise screen angle that turns free text with this /Rotate (or, without one, the page's) upright.</summary>
    public static double Angle(int? textRotation, int pageRotation) =>
        -PdfEngine.Vector.Annotations.PdfAnnotation.QuarterTurn(textRotation ?? pageRotation);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// An annotation's line width on screen: its width in points (first value) times the page's
/// display scale in pixels per point (second value), so the overlay draws it as wide as the page
/// will once it is saved, at any zoom. A dash in line widths (<c>StrokeDashArray</c>) then scales with it.
/// </summary>
public class ScaledStrokeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        Scaled(values.Length > 0 && values[0] is double width ? width : 0, values.Length > 1 && values[1] is double scale ? scale : 1);

    /// <summary>The width <paramref name="points"/> wide drawn at <paramref name="scale"/> pixels per point.</summary>
    public static double Scaled(double points, double scale) =>
        double.IsFinite(points) && points > 0 ? points * (double.IsFinite(scale) && scale > 0 ? scale : 1) : 0;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
