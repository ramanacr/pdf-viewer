using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PdfViewer.Models;

namespace PdfViewer.Views.Dialogs;

public partial class EditCommentDialog : Window
{
    public AnnotationModel Annotation { get; }
    public bool IsConfirmed { get; private set; }

    private static readonly List<string> DefaultColors = new()
    {
        "#FFFF00", // Yellow
        "#FFD700", // Gold
        "#00E676", // Mint / Light Green
        "#00E5FF", // Cyan
        "#2979FF", // Blue
        "#FF4081", // Pink / Magenta
        "#FF5252", // Coral Red
        "#FF9100", // Orange
        "#AA00FF", // Purple
        "#212121"  // Charcoal Black
    };

    public EditCommentDialog(AnnotationModel annotation, bool isNew = false)
    {
        InitializeComponent();

        Annotation = annotation;
        DialogHeaderTextBlock.Text = isNew ? "Add Comment / Sticky Note" : "Edit Comment / Sticky Note";

        TitleTextBox.Text = string.IsNullOrWhiteSpace(annotation.Title) ? "Note" : annotation.Title;
        CommentTextBox.Text = annotation.Contents;
        AuthorTextBox.Text = string.IsNullOrWhiteSpace(annotation.Author) ? System.Environment.UserName : annotation.Author;

        // A colour the list does not have (one read from the file) is listed too, so it stays
        // selected and choosing any palette colour, yellow included, is a change.
        var colors = DefaultColors.ToList();
        if (!string.IsNullOrEmpty(annotation.ColorHex) && !colors.Contains(annotation.ColorHex)) colors.Add(annotation.ColorHex);
        ColorComboBox.ItemsSource = colors;
        ColorComboBox.SelectedItem = string.IsNullOrEmpty(annotation.ColorHex) ? colors[0] : annotation.ColorHex;
        _initialColor = ColorComboBox.SelectedItem as string;

        if (annotation.IsStyledShape) ShowShapeStyle(annotation);

        Loaded += (s, e) =>
        {
            CommentTextBox.Focus();
            CommentTextBox.SelectAll();
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Annotation.Title = TitleTextBox.Text.Trim();
        Annotation.Contents = CommentTextBox.Text;
        Annotation.Author = AuthorTextBox.Text.Trim();

        if (ColorComboBox.SelectedItem is string colorHex && colorHex != _initialColor)
        {
            Annotation.ColorHex = colorHex;
        }
        if (Annotation.IsStyledShape) ApplyShapeStyle();

        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    // ------------------------------------------------------------------ shape style

    private string? _initialColor;
    private int _initialFill, _initialDash, _initialStart, _initialEnd;

    /// <summary>What the line ending lists call each PDF ending.</summary>
    private static readonly Dictionary<string, string> EndingLabels = new()
    {
        ["None"] = "None",
        ["OpenArrow"] = "Open arrow",
        ["ClosedArrow"] = "Closed arrow",
        ["Circle"] = "Circle",
        ["Square"] = "Square",
        ["Diamond"] = "Diamond",
        ["Butt"] = "Bar",
        ["ROpenArrow"] = "Reversed open arrow",
        ["RClosedArrow"] = "Reversed closed arrow",
        ["Slash"] = "Slash",
    };

    private static readonly Dictionary<string, string> ColorNames = new()
    {
        ["#FFFF00"] = "Yellow", ["#FFD700"] = "Gold", ["#00E676"] = "Mint", ["#00E5FF"] = "Cyan", ["#2979FF"] = "Blue",
        ["#FF4081"] = "Pink", ["#FF5252"] = "Coral red", ["#FF9100"] = "Orange", ["#AA00FF"] = "Purple", ["#212121"] = "Black",
    };

    private static ComboBoxItem Item(string label, object? tag, string? swatch = null)
    {
        object content = label;
        if (swatch != null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 6, 0),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)new Converters.HexToBrushConverter().Convert(swatch, typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture),
            });
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content = panel;
        }
        var item = new ComboBoxItem { Content = content, Tag = tag };
        AutomationProperties.SetName(item, label);
        return item;
    }

    /// <summary>Fills the style lists with the shape's fill, line style and line endings as they are now.</summary>
    private void ShowShapeStyle(AnnotationModel a)
    {
        ShapeStylePanel.Visibility = Visibility.Visible;
        Height += 90;

        FillComboBox.Items.Add(Item("None", null));
        foreach (var hex in DefaultColors) FillComboBox.Items.Add(Item(ColorNames.GetValueOrDefault(hex, hex), hex, hex));
        if (a.InteriorColorHex is { } fill && !DefaultColors.Contains(fill, System.StringComparer.OrdinalIgnoreCase))
            FillComboBox.Items.Add(Item($"Current ({fill})", fill, fill)); // a fill read from the file
        _initialFill = a.InteriorColorHex == null ? 0
            : FillComboBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => string.Equals(i.Tag as string, a.InteriorColorHex, System.StringComparison.OrdinalIgnoreCase));
        FillComboBox.SelectedIndex = _initialFill;

        foreach (var (name, dash) in AnnotationModel.DashStyles) DashComboBox.Items.Add(Item(name, dash));
        _initialDash = a.BorderDash == null ? 0
            : AnnotationModel.DashStyles.ToList().FindIndex(d => d.Dash != null && d.Dash.SequenceEqual(a.BorderDash));
        if (_initialDash < 0)
        {
            DashComboBox.Items.Add(Item("Current dash", a.BorderDash)); // a dash read from the file
            _initialDash = DashComboBox.Items.Count - 1;
        }
        DashComboBox.SelectedIndex = _initialDash;

        bool ends = a.HasLineEndings;
        foreach (var box in new[] { StartEndingComboBox, EndEndingComboBox })
        {
            box.Visibility = ends ? Visibility.Visible : Visibility.Collapsed;
            foreach (var style in AnnotationModel.LineEndingStyles) box.Items.Add(Item(EndingLabels[style], style));
        }
        StartEndingLabel.Visibility = EndEndingLabel.Visibility = ends ? Visibility.Visible : Visibility.Collapsed;
        _initialStart = IndexOfEnding(a.StartEnding);
        _initialEnd = IndexOfEnding(a.EndEnding);
        StartEndingComboBox.SelectedIndex = _initialStart;
        EndEndingComboBox.SelectedIndex = _initialEnd;
    }

    private static int IndexOfEnding(string style) =>
        AnnotationModel.LineEndingStyles.ToList().IndexOf(style) is var i and >= 0 ? i : 0;

    /// <summary>Writes the style the user changed; a list left as it was keeps the shape's own value.</summary>
    private void ApplyShapeStyle()
    {
        if (FillComboBox.SelectedIndex != _initialFill && FillComboBox.SelectedItem is ComboBoxItem fill)
            Annotation.InteriorColorHex = fill.Tag as string;
        if (DashComboBox.SelectedIndex != _initialDash && DashComboBox.SelectedItem is ComboBoxItem dash)
            Annotation.BorderDash = (dash.Tag as double[])?.ToArray();
        if (Annotation.HasLineEndings && (StartEndingComboBox.SelectedIndex != _initialStart || EndEndingComboBox.SelectedIndex != _initialEnd)
            && StartEndingComboBox.SelectedItem is ComboBoxItem { Tag: string start } && EndEndingComboBox.SelectedItem is ComboBoxItem { Tag: string end })
            Annotation.SetLineEndings(start, end);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }
}
