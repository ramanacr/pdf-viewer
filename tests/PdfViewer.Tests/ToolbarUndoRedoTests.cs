using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfViewer.Models;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Undo and Redo buttons in the toolbar's annotation group, next to the drawing tools. They run
/// the same commands as the Edit menu and Ctrl+Z / Ctrl+Y, and their tooltip and automation
/// name say which step they take back ("Undo Add Highlight").
/// </summary>
public class ToolbarUndoRedoTests : IDisposable
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ToolbarUndo_" + Guid.NewGuid().ToString("N"));

    public ToolbarUndoRedoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static XDocument MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "PdfViewer", "Views", "MainWindow.xaml"));
    }

    [Theory]
    [InlineData("ToolbarUndoButton", "UndoCommand", "UndoToolTip", "UndoName", "IconGlyphs.Undo")]
    [InlineData("ToolbarRedoButton", "RedoCommand", "RedoToolTip", "RedoName", "IconGlyphs.Redo")]
    public void TestTheAnnotationToolbarHasTheButton(string name, string command, string toolTip, string automationName, string glyph)
    {
        var button = MainWindowXaml().Descendants(Wpf + "Button").SingleOrDefault(b => (string?)b.Attribute(Xaml + "Name") == name);
        Assert.NotNull(button);

        Assert.Equal($"{{Binding {command}}}", (string?)button!.Attribute("Command"));
        Assert.Equal($"{{Binding {toolTip}}}", (string?)button.Attribute("ToolTip"));
        Assert.Equal($"{{Binding {automationName}}}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Contains("ToolbarIconButton", (string?)button.Attribute("Style"));
        Assert.Contains(glyph, (string?)button.Element(Wpf + "TextBlock")?.Attribute("Text"));

        // In the annotation group: after the drawing tools, before the group's closing separator.
        var siblings = button.Parent!.Elements().ToList();
        int at = siblings.IndexOf(button);
        int lastTool = siblings.FindLastIndex(e => (string?)e.Attribute("Command") == "{Binding ToggleAnnotationToolCommand}");
        int nextSeparator = siblings.FindIndex(lastTool, e => e.Name == Wpf + "Separator");
        Assert.True(lastTool >= 0 && at > lastTool && at < nextSeparator, $"{name} is not in the annotation group");
    }

    [Fact]
    public async Task TestTheButtonsNameTheStepAndFollowTheHistory()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "undo.pdf"), 1);
        using var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.Equal("Undo", vm.UndoName);
        Assert.Equal("Undo (Ctrl+Z)", vm.UndoToolTip);
        Assert.Equal("Redo (Ctrl+Y)", vm.RedoToolTip);

        vm.AddAnnotation(new AnnotationModel
        {
            PageNumber = 1, Type = AnnotationType.Highlight, X = 0.1, Y = 0.1, Width = 0.3, Height = 0.05,
            ColorHex = "#FFFFFF00", Opacity = 0.4,
        });
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Undo Add Highlight", vm.UndoName);
        Assert.Equal("Undo Add Highlight (Ctrl+Z)", vm.UndoToolTip);

        await vm.UndoAsync();
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));
        Assert.Equal("Redo Add Highlight", vm.RedoName);
        Assert.Equal("Redo Add Highlight (Ctrl+Y)", vm.RedoToolTip);

        await vm.RedoAsync();
        Assert.Equal("Undo Add Highlight", vm.UndoName);
        Assert.Equal("Redo", vm.RedoName);
    }
}
