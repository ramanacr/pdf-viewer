using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using PdfViewer.Services;
using PdfViewer.Themes;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The icon set and the icon-only sidebar tabs.
///
/// Icons are characters of the Windows icon font (IconGlyphs). The checks here keep the set
/// whole: every menu command has one, every glyph exists in both the Windows 11 font and its
/// Windows 10 fallback, and a sidebar tab that hides its label still has a name.
/// </summary>
public class IconAndSidebarTabTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static XDocument MainWindowXaml() =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", "Views", "MainWindow.xaml"));

    private static IEnumerable<(string Name, string Glyph)> AllGlyphs() =>
        typeof(IconGlyphs).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(IconGlyphs.FontFamilyName))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!));

    [Fact]
    public void TestEveryMenuItemWithACommandHasAnIcon()
    {
        var missing = MainWindowXaml().Descendants(Wpf + "MenuItem")
            .Where(m => m.Attribute("Command") != null || m.Attribute("Click") != null)
            .Where(m => m.Element(Wpf + "MenuItem.Icon") == null)
            .Select(m => (string?)m.Attribute("Header") ?? "(no header)")
            .ToList();

        Assert.True(missing.Count == 0, "Menu items with a command but no icon:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void TestEveryMenuIconIsAnIconGlyph()
    {
        var offenders = MainWindowXaml().Descendants(Wpf + "MenuItem.Icon")
            .Where(icon => icon.Elements().Count() != 1
                           || icon.Elements().Single().Name != Wpf + "TextBlock"
                           || !((string?)icon.Elements().Single().Attribute("Text") ?? "").Contains("IconGlyphs."))
            .Select(icon => (string?)icon.Parent?.Attribute("Header") ?? "(no header)")
            .ToList();

        Assert.True(offenders.Count == 0, "Menu icons not drawn from IconGlyphs:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TestEverySidebarTabHasAnAccessibleNameAndAnIcon()
    {
        var tabs = MainWindowXaml().Descendants(Wpf + "TabItem")
            .Where(t => ((string?)t.Attribute("Style") ?? "").Contains("SidebarTabItemStyle"))
            .ToList();

        Assert.True(tabs.Count >= 5, $"expected the sidebar's tabs, found {tabs.Count}");
        foreach (var tab in tabs)
        {
            string header = (string?)tab.Attribute("Header") ?? "";
            Assert.False(string.IsNullOrWhiteSpace(header));
            Assert.Equal(header, (string?)tab.Attribute("AutomationProperties.Name"));
            var glyph = tab.Attributes().FirstOrDefault(a => a.Name.LocalName == "IconGlyphs.Glyph");
            Assert.True(glyph != null && glyph.Value.Contains("IconGlyphs."), $"tab '{header}' has no icon");
        }
    }

    /// <summary>The toolbar styles name each icon button from its tooltip, so every one needs one.</summary>
    [Fact]
    public void TestEveryToolbarIconButtonHasATooltip()
    {
        var unnamed = MainWindowXaml().Descendants()
            .Where(e => (e.Name == Wpf + "Button" || e.Name == Wpf + "ToggleButton")
                        && ((string?)e.Attribute("Style") ?? "").Contains("Toolbar"))
            .Where(e => string.IsNullOrWhiteSpace((string?)e.Attribute("ToolTip")))
            .Select(e => e.ToString().Split('\n')[0])
            .ToList();

        Assert.True(unnamed.Count == 0, "Icon buttons with no tooltip (and so no name):\n  " + string.Join("\n  ", unnamed));
    }

    /// <summary>
    /// A code point missing from a font draws as an empty box. Windows 11 uses Segoe Fluent
    /// Icons and Windows 10 falls back to Segoe MDL2 Assets, so each glyph must be in both.
    /// </summary>
    [Fact]
    public void TestEveryGlyphExistsInTheIconFontAndItsFallback()
    {
        int fontsChecked = 0;
        var missing = new List<string>();
        foreach (string family in IconGlyphs.FontFamilyName.Split(',').Select(f => f.Trim()))
        {
            if (!new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                    .TryGetGlyphTypeface(out var glyphs)
                || !glyphs.FamilyNames.Values.Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // not installed on this machine
            }

            fontsChecked++;
            foreach (var (name, glyph) in AllGlyphs())
            {
                Assert.Equal(1, glyph.Length);
                if (!glyphs.CharacterToGlyphMap.ContainsKey(glyph[0]))
                {
                    missing.Add($"{family}: {name} (U+{(int)glyph[0]:X4})");
                }
            }
        }

        Assert.True(fontsChecked > 0, "neither icon font is installed");
        Assert.True(missing.Count == 0, "Glyphs the font does not have:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void TestHighContrastSwapsTheIconBrushesForSystemColours()
    {
        var resources = new ResourceDictionary();

        ThemeManager.ApplyHighContrastIcons(resources, highContrast: true);
        Assert.Equal(SystemColors.ControlTextColor, ((SolidColorBrush)resources["IconBrush"]).Color);
        Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)resources["IconAccentBrush"]).Color);

        ThemeManager.ApplyHighContrastIcons(resources, highContrast: false);
        Assert.False(resources.Contains("IconBrush"));
        Assert.False(resources.Contains("IconAccentBrush"));
    }

    /// <summary>
    /// Built from the real style: every tab shows its icon, only the selected tab shows its
    /// label, and the label stays available as the tooltip and the automation name.
    /// </summary>
    [Fact]
    public void TestSidebarTabShowsItsLabelOnlyWhenSelected()
    {
        RunOnSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadDictionary("Themes/LightTheme.xaml"));
            resources.MergedDictionaries.Add(LoadDictionary("Themes/Controls.xaml"));
            var style = (Style)resources["SidebarTabItemStyle"];

            var names = new[] { ("Pages", IconGlyphs.Pages), ("Bookmarks", IconGlyphs.Bookmarks), ("Search Results", IconGlyphs.SearchResults) };
            var tabs = new TabControl();
            foreach (var (header, glyph) in names)
            {
                var item = new TabItem { Header = header, Style = style, Content = new TextBlock { Text = header } };
                IconGlyphs.SetGlyph(item, glyph);
                AutomationProperties.SetName(item, header);
                tabs.Items.Add(item);
            }

            var window = new Window
            {
                Resources = resources,
                Content = tabs,
                Width = 400, Height = 300,
                Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                foreach (int selected in new[] { 0, 2, 1 })
                {
                    tabs.SelectedIndex = selected;
                    window.UpdateLayout();

                    for (int i = 0; i < tabs.Items.Count; i++)
                    {
                        var item = (TabItem)tabs.Items[i];
                        var label = (ContentPresenter)item.Template.FindName("TabLabel", item);
                        var glyph = (TextBlock)item.Template.FindName("TabGlyph", item);

                        Assert.Equal(names[i].Item2, glyph.Text);
                        Assert.Equal(Visibility.Visible, glyph.Visibility);
                        Assert.Equal(i == selected ? Visibility.Visible : Visibility.Collapsed, label.Visibility);
                        Assert.Equal(names[i].Item1, item.ToolTip);
                        Assert.Equal(names[i].Item1, AutomationProperties.GetName(item));
                    }
                }

                // The label eases open when Windows allows animation, and is not held narrow
                // afterwards; with animation off it is simply full width.
                var selectedLabel = (ContentPresenter)((TabItem)tabs.Items[1]).Template.FindName("TabLabel", (TabItem)tabs.Items[1]);
                var until = DateTime.UtcNow.AddMilliseconds(600);
                while (DateTime.UtcNow < until)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    Thread.Sleep(15);
                }
                double expected = SystemParameters.ClientAreaAnimation ? 200 : double.PositiveInfinity;
                Assert.Equal(expected, selectedLabel.MaxWidth);
                Assert.Equal(1.0, selectedLabel.Opacity);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Parses the source XAML rather than the compiled resource: a pack:// URI needs a running
    /// Application, which a test host does not have. Loose XAML has to name the assembly.
    /// </summary>
    private static ResourceDictionary LoadDictionary(string path)
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", path))
            .Replace("clr-namespace:PdfViewer.Themes\"", "clr-namespace:PdfViewer.Themes;assembly=PdfViewer\"");
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "test timed out");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
