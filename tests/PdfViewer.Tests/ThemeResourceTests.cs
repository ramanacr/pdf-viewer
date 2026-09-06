using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Guards the theme token contract.
///
/// A misspelled resource key fails silently in WPF: the DynamicResource simply resolves to
/// nothing and the control falls back to a system default. That is how the Privacy dialog
/// shipped with dark-on-dark text - it asked for "SurfaceBrush" where the themes define
/// "SurfaceBgBrush", so the window took the OS default background while its text brushes
/// still resolved from the light theme. The build was clean and every unit test passed.
/// </summary>
public class ThemeResourceTests
{
    /// <summary>Theme tokens follow a naming convention; this is what the check applies to.</summary>
    private static readonly Regex ResourceReference =
        new(@"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_]+(?:Brush|Color))\s*\}", RegexOptions.Compiled);

    private static readonly Regex KeyDefinition =
        new(@"x:Key\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void TestEveryThemeBrushReferencedByTheUiExistsInBothThemes()
    {
        string root = FindRepositoryRoot();
        string themeDir = Path.Combine(root, "src", "PdfViewer", "Themes");

        var light = ReadDefinedKeys(Path.Combine(themeDir, "LightTheme.xaml"));
        var dark = ReadDefinedKeys(Path.Combine(themeDir, "DarkTheme.xaml"));

        Assert.NotEmpty(light);
        Assert.NotEmpty(dark);

        var appKeys = ReadDefinedKeys(Path.Combine(root, "src", "PdfViewer", "App.xaml"));

        var problems = new List<string>();

        foreach (string xamlFile in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "PdfViewer", "Views"), "*.xaml", SearchOption.AllDirectories))
        {
            string xaml = File.ReadAllText(xamlFile);
            var localKeys = ReadDefinedKeysFromText(xaml);
            string name = Path.GetFileName(xamlFile);

            foreach (string key in ResourceReference.Matches(xaml).Select(m => m.Groups[1].Value).Distinct())
            {
                if (localKeys.Contains(key) || appKeys.Contains(key)) continue;

                bool inLight = light.Contains(key);
                bool inDark = dark.Contains(key);

                if (!inLight && !inDark)
                {
                    problems.Add($"{name}: '{key}' is defined in neither theme - it will silently " +
                                 "fall back to a system default.");
                }
                else if (!inLight)
                {
                    problems.Add($"{name}: '{key}' is missing from LightTheme.xaml.");
                }
                else if (!inDark)
                {
                    problems.Add($"{name}: '{key}' is missing from DarkTheme.xaml.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// A dialog that does not paint its own background inherits whatever the OS decides,
    /// which is how a light-themed dialog ends up dark. Both must be stated explicitly.
    /// </summary>
    [Fact]
    public void TestEveryDialogPaintsItsOwnBackgroundAndForeground()
    {
        string dialogDir = Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", "Views", "Dialogs");
        var problems = new List<string>();

        foreach (string xamlFile in Directory.EnumerateFiles(dialogDir, "*.xaml"))
        {
            string header = File.ReadAllText(xamlFile);
            int bodyStart = header.IndexOf('>');
            if (bodyStart > 0) header = header[..bodyStart];

            string name = Path.GetFileName(xamlFile);
            if (!header.Contains("Background=")) problems.Add($"{name}: no Background on the Window.");
            if (!header.Contains("Foreground=")) problems.Add($"{name}: no Foreground on the Window.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static HashSet<string> ReadDefinedKeys(string path) =>
        File.Exists(path) ? ReadDefinedKeysFromText(File.ReadAllText(path)) : new HashSet<string>();

    private static HashSet<string> ReadDefinedKeysFromText(string xaml) =>
        KeyDefinition.Matches(xaml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

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
}
