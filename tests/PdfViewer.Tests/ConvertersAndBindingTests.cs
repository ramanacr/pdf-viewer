using System;
using System.Globalization;
using System.Windows;
using PdfEngine.Annotations;
using PdfViewer.Converters;
using PdfViewer.Models;
using PdfViewer.ViewModels;
using Xunit;
using AnnotationType = PdfViewer.Models.AnnotationType;

namespace PdfViewer.Tests;

public class ConvertersAndBindingTests
{
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    [Fact]
    public void TestBoolToVisibilityConverter()
    {
        var converter = new BoolToVisibilityConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(true, typeof(Visibility), null!, _culture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(false, typeof(Visibility), null!, _culture));
        Assert.Equal(Visibility.Visible, converter.Convert(5, typeof(Visibility), null!, _culture)); // int > 0
        Assert.Equal(Visibility.Collapsed, converter.Convert(0, typeof(Visibility), null!, _culture)); // int == 0

        // Inverted
        var inverted = new BoolToVisibilityConverter { Invert = true };
        Assert.Equal(Visibility.Collapsed, inverted.Convert(true, typeof(Visibility), null!, _culture));
        Assert.Equal(Visibility.Visible, inverted.Convert(false, typeof(Visibility), null!, _culture));
    }

    [Fact]
    public void TestNullToVisibilityConverter()
    {
        var converter = new NullToVisibilityConverter();

        Assert.Equal(Visibility.Collapsed, converter.Convert(null!, typeof(Visibility), null!, _culture));
        Assert.Equal(Visibility.Visible, converter.Convert("hello", typeof(Visibility), null!, _culture));

        // Inverted
        var inverted = new NullToVisibilityConverter { Invert = true };
        Assert.Equal(Visibility.Visible, inverted.Convert(null!, typeof(Visibility), null!, _culture));
        Assert.Equal(Visibility.Collapsed, inverted.Convert("hello", typeof(Visibility), null!, _culture));
    }

    [Fact]
    public void TestEnumToBoolConverter()
    {
        var converter = new EnumToBoolConverter();

        Assert.True((bool)converter.Convert(AnnotationType.Highlight, typeof(bool), "Highlight", _culture));
        Assert.True((bool)converter.Convert(AnnotationType.Highlight, typeof(bool), "highlight", _culture)); // Case insensitive
        Assert.False((bool)converter.Convert(AnnotationType.Highlight, typeof(bool), "Underline", _culture));
        Assert.False((bool)converter.Convert(null!, typeof(bool), "Highlight", _culture));
        Assert.False((bool)converter.Convert(AnnotationType.Highlight, typeof(bool), null!, _culture));

        // ConvertBack
        object parsed = converter.ConvertBack(true, typeof(AnnotationType), "Underline", _culture);
        Assert.Equal(AnnotationType.Underline, parsed);
    }

    [Fact]
    public void TestEnumToVisibilityConverter()
    {
        var converter = new EnumToVisibilityConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(ViewLayoutMode.Continuous, typeof(Visibility), "Continuous", _culture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(ViewLayoutMode.Continuous, typeof(Visibility), "SinglePage", _culture));

        var inverted = new EnumToVisibilityConverter { Invert = true };
        Assert.Equal(Visibility.Collapsed, inverted.Convert(ViewLayoutMode.Continuous, typeof(Visibility), "Continuous", _culture));
        Assert.Equal(Visibility.Visible, inverted.Convert(ViewLayoutMode.Continuous, typeof(Visibility), "SinglePage", _culture));
    }

    [Fact]
    public void TestNormalizedCoordinateConverter()
    {
        var converter = new NormalizedCoordinateConverter();

        object[] values = new object[] { 0.25, 800.0 };
        object result = converter.Convert(values, typeof(double), null!, _culture);

        Assert.Equal(200.0, (double)result);

        object[] zeroValues = new object[] { 0.0, 1000.0 };
        Assert.Equal(0.0, (double)converter.Convert(zeroValues, typeof(double), null!, _culture));

        object[] invalidValues = new object[] { "invalid", 100.0 };
        Assert.Equal(0.0, (double)converter.Convert(invalidValues, typeof(double), null!, _culture));
    }

    [Fact]
    public void TestSearchHighlightConverters()
    {
        var bgConverter = new SearchHighlightBackgroundConverter();
        var borderConverter = new SearchHighlightBorderConverter();

        var activeBg = bgConverter.Convert(true, typeof(System.Windows.Media.Brush), null!, _culture);
        var normalBg = bgConverter.Convert(false, typeof(System.Windows.Media.Brush), null!, _culture);

        Assert.NotNull(activeBg);
        Assert.NotNull(normalBg);
        Assert.NotEqual(activeBg, normalBg);

        var activeBorder = borderConverter.Convert(true, typeof(System.Windows.Media.Brush), null!, _culture);
        var normalBorder = borderConverter.Convert(false, typeof(System.Windows.Media.Brush), null!, _culture);

        Assert.NotNull(activeBorder);
        Assert.NotNull(normalBorder);
        Assert.NotEqual(activeBorder, normalBorder);
    }

    [Fact]
    public void TestHexToBrushConverter()
    {
        var converter = new HexToBrushConverter();

        var redBrush = (System.Windows.Media.SolidColorBrush)converter.Convert("#FFFF0000", typeof(System.Windows.Media.Brush), null!, _culture);
        Assert.Equal(System.Windows.Media.Colors.Red, redBrush.Color);

        var fallbackBrush = converter.Convert("invalid-color-hex", typeof(System.Windows.Media.Brush), null!, _culture);
        Assert.NotNull(fallbackBrush);
    }
}
