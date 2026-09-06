using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Pdfium;
using PdfViewer.Models;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers Save (Ctrl+S). Saving writes over the file the user opened, which is the one
/// operation in this application that can destroy their work, so the tests are about what
/// survives rather than about the call returning.
/// </summary>
public class SaveInPlaceTests : IDisposable
{
    private readonly string _testDir;

    public SaveInPlaceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfSaveTests_" + Guid.NewGuid().ToString("N"));
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

    private async Task<(MainViewModel Vm, string Path)> LoadedViewModel(int pageCount = 2)
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, $"save_{Guid.NewGuid():N}.pdf"), pageCount, "SaveToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        return (vm, path);
    }

    private static AnnotationModel Highlight(int page = 1) => new()
    {
        PageNumber = page,
        Type = AnnotationType.Highlight,
        X = 0.1,
        Y = 0.1,
        Width = 0.2,
        Height = 0.05,
        ColorHex = "#FFFF00",
        Opacity = 0.4,
        Contents = "saved note"
    };

    [Fact]
    public async Task TestAFreshlyOpenedDocumentHasNothingToSave()
    {
        var (vm, _) = await LoadedViewModel();

        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.CanSave());
        Assert.DoesNotContain("*", vm.WindowTitle);
    }

    [Fact]
    public async Task TestAnnotatingMarksTheDocumentUnsaved()
    {
        var (vm, _) = await LoadedViewModel();

        vm.AddAnnotation(Highlight());

        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CanSave());

        // The title is the only warning a user gets before closing.
        Assert.StartsWith("*", vm.WindowTitle);
    }

    [Fact]
    public async Task TestDeletingAndClearingAlsoCount()
    {
        var (vm, _) = await LoadedViewModel();

        var annotation = Highlight();
        vm.AddAnnotation(annotation);
        await vm.SaveAsync();
        Assert.False(vm.HasUnsavedChanges);

        vm.DeleteAnnotation(annotation);
        Assert.True(vm.HasUnsavedChanges);

        await vm.SaveAsync();
        vm.AddAnnotation(Highlight());
        await vm.SaveAsync();
        Assert.False(vm.HasUnsavedChanges);

        vm.ClearAllAnnotations();
        Assert.True(vm.HasUnsavedChanges);
    }

    /// <summary>
    /// The point of Save: the annotation is in the file afterwards, and the file is still a
    /// document that opens with all its pages.
    /// </summary>
    [Fact]
    public async Task TestSaveWritesTheAnnotationIntoTheOriginalFile()
    {
        var (vm, path) = await LoadedViewModel(3);
        long sizeBefore = new FileInfo(path).Length;

        vm.AddAnnotation(Highlight(2));
        await vm.SaveAsync();

        Assert.False(vm.HasUnsavedChanges);
        Assert.DoesNotContain("*", vm.WindowTitle);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.NotEqual(sizeBefore, new FileInfo(path).Length);

        // Re-open from disk: the document survives and carries the annotation.
        using IPdfEngine engine = new PdfiumEngine();
        await using var reopened = await engine.OpenDocumentAsync(path);

        Assert.Equal(3, reopened.PageCount);

        var annotations = await engine.AnnotationService.LoadAnnotationsAsync(reopened, 2);
        Assert.NotEmpty(annotations);
    }

    /// <summary>
    /// Saving must not leave scratch files behind next to the user's document.
    /// </summary>
    [Fact]
    public async Task TestSaveLeavesNoTemporaryFilesBehind()
    {
        var (vm, path) = await LoadedViewModel();

        vm.AddAnnotation(Highlight());
        await vm.SaveAsync();

        var leftovers = Directory.GetFiles(Path.GetDirectoryName(path)!)
            .Select(Path.GetFileName)
            .Where(f => f!.Contains(".saving", StringComparison.OrdinalIgnoreCase)
                     || f!.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task TestSavingTwiceKeepsBothAnnotations()
    {
        var (vm, path) = await LoadedViewModel();

        vm.AddAnnotation(Highlight(1));
        await vm.SaveAsync();

        vm.AddAnnotation(Highlight(1));
        await vm.SaveAsync();

        Assert.False(vm.HasUnsavedChanges);

        using IPdfEngine engine = new PdfiumEngine();
        await using var reopened = await engine.OpenDocumentAsync(path);

        var annotations = await engine.AnnotationService.LoadAnnotationsAsync(reopened, 1);
        Assert.True(annotations.Count >= 2, $"Expected both annotations, found {annotations.Count}.");
    }

    [Fact]
    public async Task TestSaveDoesNothingWhenThereIsNothingToSave()
    {
        var (vm, path) = await LoadedViewModel();
        byte[] before = File.ReadAllBytes(path);

        await vm.SaveAsync();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// Cancelling the prompt has to actually stop what triggered it, or "Cancel" is a lie and
    /// the work is gone anyway.
    /// </summary>
    [Fact]
    public async Task TestCancellingTheUnsavedPromptStopsTheAction()
    {
        var (vm, _) = await LoadedViewModel();
        vm.AddAnnotation(Highlight());

        vm.ConfirmSaveBeforeClosingFunc = _ => null;   // cancel

        Assert.False(await vm.ConfirmDiscardChangesAsync());
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task TestDiscardingLetsTheActionProceedWithoutWriting()
    {
        var (vm, path) = await LoadedViewModel();
        byte[] before = File.ReadAllBytes(path);

        vm.AddAnnotation(Highlight());
        vm.ConfirmSaveBeforeClosingFunc = _ => false;  // discard

        Assert.True(await vm.ConfirmDiscardChangesAsync());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task TestChoosingSaveAtThePromptWritesTheFile()
    {
        var (vm, path) = await LoadedViewModel();
        long before = new FileInfo(path).Length;

        vm.AddAnnotation(Highlight());
        vm.ConfirmSaveBeforeClosingFunc = _ => true;   // save

        Assert.True(await vm.ConfirmDiscardChangesAsync());
        Assert.False(vm.HasUnsavedChanges);
        Assert.NotEqual(before, new FileInfo(path).Length);
    }

    /// <summary>
    /// Opening another document replaces this one, so it has to go through the same prompt.
    /// </summary>
    [Fact]
    public async Task TestOpeningAnotherDocumentAsksAboutUnsavedWork()
    {
        var (vm, firstPath) = await LoadedViewModel();
        vm.AddAnnotation(Highlight());

        string secondPath = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, "second.pdf"), 1, "Second");

        bool asked = false;
        vm.ConfirmSaveBeforeClosingFunc = _ => { asked = true; return null; };

        await vm.LoadDocumentAsync(secondPath);

        Assert.True(asked);

        // Cancelled, so the first document is still the one open and still unsaved.
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(firstPath, vm.Metadata!.FilePath);
    }

    [Fact]
    public async Task TestClosingTheDocumentClearsTheUnsavedMarker()
    {
        var (vm, _) = await LoadedViewModel();
        vm.AddAnnotation(Highlight());

        vm.CloseDocument();

        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.CanSave());
        Assert.Equal("PDF Viewer", vm.WindowTitle);
    }
}
