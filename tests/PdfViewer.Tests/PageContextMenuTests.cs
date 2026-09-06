using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PdfViewer.Models;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers the page's right-click menu.
///
/// Its commands were bound with a RelativeSource walk to an ancestor Window. A ContextMenu is
/// hosted in its own popup with its own visual root, so that walk never arrives: every binding
/// resolved to nothing and the menu opened with items that did nothing at all. Nothing failed
/// loudly - WPF reports an unresolved binding to the debug trace and carries on - so it went
/// unnoticed until someone tried to use it.
/// </summary>
public class PageContextMenuTests : IDisposable
{
    private readonly string _testDir;

    public PageContextMenuTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfCtxTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
        catch { }
    }

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

    private static string MainWindowXaml() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", "Views", "MainWindow.xaml"));

    /// <summary>
    /// The structural rule: nothing inside a ContextMenu may bind by walking up to an ancestor
    /// Window, because from a popup there is no such ancestor to find.
    /// </summary>
    [Fact]
    public void TestNoContextMenuBindingReachesForAnAncestorWindow()
    {
        string xaml = MainWindowXaml();
        var offenders = new System.Collections.Generic.List<string>();

        foreach (Match menu in Regex.Matches(xaml, @"<ContextMenu\b.*?</ContextMenu>", RegexOptions.Singleline))
        {
            foreach (Match binding in Regex.Matches(menu.Value, @"\{Binding[^}]*AncestorType=Window[^}]*\}"))
            {
                offenders.Add(binding.Value.Trim());
            }
        }

        Assert.True(offenders.Count == 0,
            "These bindings sit inside a ContextMenu and walk to an ancestor Window, which a popup " +
            "does not have, so they resolve to nothing:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// And the positive half: the page menu's commands are actually bound to something that
    /// exists, through the page's owning document.
    /// </summary>
    [Fact]
    public void TestThePageMenuBindsItsCommandsThroughThePagesOwner()
    {
        string xaml = MainWindowXaml();

        var expected = new[]
        {
            "Owner.CopySelectedTextCommand",
            "Owner.HighlightSelectedTextCommand",
            "Owner.SelectAllTextCommand",
            "Owner.ClearSelectionCommand",
            "Owner.HasTextSelection"
        };

        foreach (string path in expected)
        {
            Assert.Contains(path, xaml);
        }

        // Every one of those has to name a real member, or the binding is decoration again.
        var owner = typeof(PageViewModel).GetProperty(nameof(PageViewModel.Owner));
        Assert.NotNull(owner);
        Assert.Equal(typeof(MainViewModel), owner!.PropertyType);

        foreach (string path in expected)
        {
            string member = path["Owner.".Length..];
            Assert.True(typeof(MainViewModel).GetProperty(member) != null,
                $"MainViewModel has no '{member}', so the page menu binding to it cannot work.");
        }
    }

    /// <summary>
    /// The binding only connects if the page actually knows which document it belongs to.
    /// </summary>
    [Fact]
    public async Task TestEveryPageKnowsItsDocument()
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, "ctx.pdf"), 3, "CtxToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        Assert.NotEmpty(vm.Pages);
        Assert.All(vm.Pages, page => Assert.Same(vm, page.Owner));
    }

    /// <summary>
    /// What the menu items actually do, exercised through the same commands the menu invokes.
    /// </summary>
    [Fact]
    public async Task TestThePageMenuCommandsDoTheirWork()
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, "work.pdf"), 2, "CtxToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        var page = vm.Pages[0];
        page.TextSegments.Clear();
        page.TextSegments.Add(new PageTextSegment
        {
            Text = "Selectable", X = 0.1, Y = 0.1, Width = 0.3, Height = 0.05, SegmentIndex = 0
        });

        // Stand in for extraction having finished. Without this the real extraction is still
        // pending, Select All takes its asynchronous branch, and the assertions below race it.
        page.IsTextExtracted = true;

        // Select All, then Copy, then Deselect - the menu's three working verbs.
        vm.SelectAllTextCommand.Execute(null);
        Assert.True(vm.HasTextSelection);
        Assert.Contains("Selectable", vm.SelectedText);

        vm.HighlightSelectedTextCommand.Execute(null);
        Assert.NotEmpty(vm.AllAnnotations);

        vm.SelectAllTextCommand.Execute(null);
        Assert.True(vm.HasTextSelection);

        vm.ClearSelectionCommand.Execute(null);
        Assert.False(vm.HasTextSelection);
        Assert.Empty(vm.SelectedText);
    }
}
