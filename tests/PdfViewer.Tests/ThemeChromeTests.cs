using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The dark theme covers the whole window chrome (menus, drop-downs, input boxes, tooltips), keeps
/// WCAG AA contrast, and the user's choice is remembered.
/// </summary>
public class ThemeChromeTests
{
    [Fact]
    public void LightAndDarkThemesDefineTheSameKeys()
    {
        RunOnSta(() =>
        {
            var light = LoadDictionary("Themes/LightTheme.xaml").Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToArray();
            var dark = LoadDictionary("Themes/DarkTheme.xaml").Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToArray();
            Assert.Equal(light, dark);
        });
    }

    [Fact]
    public void DarkControlsRestyleTheChromeWindowsDrawsLight()
    {
        RunOnSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadDictionary("Themes/DarkTheme.xaml"));
            resources.MergedDictionaries.Add(LoadDictionary("Themes/DarkControls.xaml"));

            foreach (var type in new[]
            {
                typeof(MenuItem), typeof(ContextMenu), typeof(ToolTip), typeof(ComboBox), typeof(ComboBoxItem),
                typeof(TextBox), typeof(PasswordBox), typeof(Slider), typeof(Button), typeof(CheckBox),
                typeof(RadioButton), typeof(Label), typeof(ListBox), typeof(TreeView), typeof(System.Windows.Controls.Primitives.StatusBar),
            })
            {
                Assert.True(resources.Contains(type), $"no dark style for {type.Name}");
            }
            Assert.True(resources.Contains(MenuItem.SeparatorStyleKey));

            // A combo box built from the dark style has the parts ComboBox needs, editable or not.
            var combo = new ComboBox { Style = (Style)resources[typeof(ComboBox)], IsEditable = true };
            combo.Resources.MergedDictionaries.Add(resources);
            combo.ApplyTemplate();
            Assert.NotNull(combo.Template.FindName("PART_EditableTextBox", combo));
            Assert.NotNull(combo.Template.FindName("PART_Popup", combo));
        });
    }

    [Theory]
    [InlineData("Themes/LightTheme.xaml")]
    [InlineData("Themes/DarkTheme.xaml")]
    public void ThemeColoursMeetWcagAa(string theme)
    {
        RunOnSta(() =>
        {
            var d = LoadDictionary(theme);
            Color C(string key) => ((SolidColorBrush)d[key]).Color;
            string[] surfaces = { "AppBgBrush", "SurfaceBgBrush", "SurfaceAltBgBrush", "ToolbarBgBrush", "SidebarBgBrush" };

            foreach (var surface in surfaces)
            {
                AssertContrast(C("TextPrimaryBrush"), C(surface), 4.5, $"text on {surface}");
                AssertContrast(C("TextSecondaryBrush"), C(surface), 4.5, $"secondary text on {surface}");
                AssertContrast(C("IconBrush"), C(surface), 3.0, $"icons on {surface}");
            }
            AssertContrast(C("TextPrimaryBrush"), C("AccentLightBrush"), 4.5, "text on a highlighted item");
            AssertContrast(C("OnAccentTextBrush"), C("AccentBrush"), 4.5, "text on an accent button");
            AssertContrast(C("AccentBrush"), C("SidebarBgBrush"), 4.5, "selected sidebar tab");

            if (theme.Contains("Dark"))
            {
                foreach (var surface in surfaces)
                {
                    AssertContrast(C("ControlBorderBrush"), C(surface), 3.0, $"control borders on {surface}");
                    AssertContrast(C("ScrollBarThumbBrush"), C(surface), 3.0, $"scroll thumbs on {surface}");
                    AssertContrast(C("SliderTrackBrush"), C(surface), 3.0, $"slider track on {surface}");
                }
            }
        });
    }

    [Fact]
    public void TheChosenThemeIsRememberedAndBadFilesAreIgnored()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PdfViewerThemeTest_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "appearance.json");
        try
        {
            Assert.Null(AppearanceSettings.ReadFrom(file));

            AppearanceSettings.WriteTo(file, AppTheme.Dark);
            Assert.Equal(AppTheme.Dark, AppearanceSettings.ReadFrom(file));

            AppearanceSettings.WriteTo(file, AppTheme.Light);
            Assert.Equal(AppTheme.Light, AppearanceSettings.ReadFrom(file));

            File.WriteAllText(file, "{ not json");
            Assert.Null(AppearanceSettings.ReadFrom(file));

            File.WriteAllText(file, "{\"Theme\":\"7\"}");
            Assert.Null(AppearanceSettings.ReadFrom(file));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void AssertContrast(Color a, Color b, double minimum, string what)
    {
        double ratio = Contrast(a, b);
        Assert.True(ratio >= minimum, $"{what}: {ratio:0.00}:1 is below {minimum}:1");
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static ResourceDictionary LoadDictionary(string path)
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", path));
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PdfViewer")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
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
