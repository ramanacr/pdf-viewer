using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfEngine.Vector.Annotations;
using PdfViewer.Services;

namespace PdfViewer.Views.Dialogs;

/// <summary>Small dialogs for measuring and stamps, built in code: a scale, a line of text, a page number.</summary>
internal static class MeasureStampDialogs
{
    private static Window Frame(Window? owner, string title, double width)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
        };
        w.SetResourceReference(Control.BackgroundProperty, "SurfaceBgBrush");
        w.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        return w;
    }

    private static TextBlock Text(string text, bool secondary = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
        return t;
    }

    private static StackPanel Buttons(Window w, string ok, Func<bool> accept)
    {
        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 84, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(10, 3, 10, 3) };
        okButton.Click += (_, _) => { if (accept()) w.DialogResult = true; };
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { okButton, cancel } };
    }

    private static bool TryNumber(string s, out double v) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && double.IsFinite(v);

    /// <summary>"[1] [in] = [10] [ft]", with decimal places: the scale a document is measured at.</summary>
    public static MeasureScale? AskScale(Window? owner, MeasureScale current)
    {
        var w = Frame(owner, "Scale and Units", 440);
        var paper = new TextBox { Text = current.PaperValue.ToString("0.######", CultureInfo.InvariantCulture), Width = 70, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
        var paperUnit = new ComboBox { ItemsSource = MeasureScale.PaperUnits, SelectedItem = current.PaperUnit, Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        var real = new TextBox { Text = current.RealValue.ToString("0.######", CultureInfo.InvariantCulture), Width = 70, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
        var realUnit = new ComboBox { ItemsSource = PdfMeasure.Units, SelectedItem = current.RealUnit, Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        var decimals = new ComboBox { ItemsSource = Enumerable.Range(0, 5).ToList(), SelectedItem = current.Decimals, Width = 50, Margin = new Thickness(8, 0, 0, 0) };
        AutomationName(paper, "Length on the page"); AutomationName(paperUnit, "Unit on the page");
        AutomationName(real, "Real length"); AutomationName(realUnit, "Real unit"); AutomationName(decimals, "Decimal places");
        var equals = new TextBlock { Text = "=", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { paper, paperUnit, equals, real, realUnit } };
        var places = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { Text("Decimal places:"), decimals } };
        places.Children[0].SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        var error = Text(string.Empty);
        error.Foreground = System.Windows.Media.Brushes.Firebrick;
        MeasureScale? result = null;
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                Text("What a length on the page stands for, e.g. 1 in = 10 ft. Pages that give their own scale are measured with theirs.", secondary: true),
                row, places, error,
            },
        };
        panel.Children.Add(Buttons(w, "OK", () =>
        {
            if (!TryNumber(paper.Text, out double p) || !TryNumber(real.Text, out double r))
            {
                error.Text = "Enter two positive numbers.";
                return false;
            }
            result = new MeasureScale(p, (string)paperUnit.SelectedItem, r, (string)realUnit.SelectedItem, (int)decimals.SelectedItem);
            return result.IsValid;
        }));
        w.Content = panel;
        w.Loaded += (_, _) => { paper.Focus(); paper.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>A line of text (a name), or null when cancelled.</summary>
    public static string? AskText(Window? owner, string title, string prompt, string initial, string ok = "OK")
    {
        var w = Frame(owner, title, 420);
        var box = new TextBox { Text = initial, Padding = new Thickness(6, 4, 6, 4), MaxLength = 120 };
        AutomationName(box, prompt);
        string? result = null;
        var panel = new StackPanel { Margin = new Thickness(20), Children = { Text(prompt), box } };
        panel.Children.Add(Buttons(w, ok, () =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return false;
            result = box.Text.Trim();
            return true;
        }));
        w.Content = panel;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>A page number from 1 to <paramref name="pages"/>, or null.</summary>
    public static int? AskPage(Window? owner, int pages)
    {
        if (pages <= 1) return 1;
        string? text = AskText(owner, "Custom Stamp", $"Which page becomes the stamp (1 to {pages})?", "1");
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= pages ? n : null;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
