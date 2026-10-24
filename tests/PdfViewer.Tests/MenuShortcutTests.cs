using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// A shortcut shown next to a menu item is only text (InputGestureText); WPF does not bind it.
/// Every Ctrl shortcut a menu shows must therefore have a KeyBinding on the window, or the menu
/// promises a key that does nothing.
/// </summary>
public class MenuShortcutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // Keys the window handles in code (MainWindow.xaml.cs, Window_PreviewKeyDown): undo and redo,
    // which must leave Ctrl+Z to a focused text box rather than undo the document under it.
    private static readonly string[] HandledInCode = ["Ctrl+Z", "Ctrl+Y"];

    private static XDocument MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "PdfViewer", "Views", "MainWindow.xaml"));
    }

    /// <summary>The key a KeyBinding names for the key a menu shows ("Ctrl++" is OemPlus, "Ctrl+0" is D0).</summary>
    private static string KeyName(string shown) => shown switch
    {
        "+" => "OemPlus",
        "-" => "OemMinus",
        _ when shown.Length == 1 && char.IsDigit(shown[0]) => "D" + shown,
        _ => shown,
    };

    [Fact]
    public void TestEveryCtrlShortcutShownInAMenuIsBound()
    {
        var xaml = MainWindowXaml();
        var bound = xaml.Descendants(Wpf + "KeyBinding")
            .Select(k => ((string?)k.Attribute("Modifiers") ?? "") + "+" + (string?)k.Attribute("Key"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unbound = xaml.Descendants(Wpf + "MenuItem")
            .Select(m => (string?)m.Attribute("InputGestureText"))
            .Where(g => g != null && g.StartsWith("Ctrl+", StringComparison.Ordinal) && !HandledInCode.Contains(g))
            .Distinct()
            .Where(g =>
            {
                int last = g!.LastIndexOf('+', g.Length - 2);
                string modifiers = g[..last];
                return !bound.Contains(modifiers + "+" + KeyName(g[(last + 1)..]));
            })
            .ToList();

        Assert.True(unbound.Count == 0, "Menu shortcuts with no KeyBinding:\n  " + string.Join("\n  ", unbound));
    }
}
