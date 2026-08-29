using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;
using AnnotationType = PdfViewer.Models.AnnotationType;

namespace PdfViewer.Tests;

public class AnnotationWorkflowEventTests : IDisposable
{
    private readonly string _testDir;

    public AnnotationWorkflowEventTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerAnnotEventTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        PdfiumNativeBridge.EnsureInitialized();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private string CreateSamplePdf(string name = "sample.pdf", int pageCount = 3)
    {
        string filePath = Path.Combine(_testDir, name);
        TestPdfBuilder.CreateSimplePdf(filePath, pageCount);
        return filePath;
    }

    [Fact]
    public async Task TestAnnotationToolToggleUserEvents()
    {
        string pdfPath = CreateSamplePdf("annot_toggle.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        Assert.Null(vm.ActiveAnnotationTool);

        // User clicks Highlight Tool
        vm.ToggleAnnotationTool("Highlight");
        Assert.Equal(AnnotationType.Highlight, vm.ActiveAnnotationTool);

        // User clicks Highlight Tool again (toggles off)
        vm.ToggleAnnotationTool("Highlight");
        Assert.Null(vm.ActiveAnnotationTool);

        // User clicks Underline Tool
        vm.ToggleAnnotationTool("Underline");
        Assert.Equal(AnnotationType.Underline, vm.ActiveAnnotationTool);

        // User clicks StrikeOut Tool
        vm.ToggleAnnotationTool("StrikeOut");
        Assert.Equal(AnnotationType.StrikeOut, vm.ActiveAnnotationTool);

        // User clicks Ink Tool
        vm.ToggleAnnotationTool("Ink");
        Assert.Equal(AnnotationType.Ink, vm.ActiveAnnotationTool);

        // User clicks Note Tool
        vm.ToggleAnnotationTool("Note");
        Assert.Equal(AnnotationType.Note, vm.ActiveAnnotationTool);

        // User clicks Rectangle Tool
        vm.ToggleAnnotationTool("Rectangle");
        Assert.Equal(AnnotationType.Rectangle, vm.ActiveAnnotationTool);
    }

    [Fact]
    public async Task TestAddAnnotationUserEvents()
    {
        string pdfPath = CreateSamplePdf("add_annot.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        var annot = new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Highlight,
            X = 0.1,
            Y = 0.2,
            Width = 0.3,
            Height = 0.05,
            ColorHex = "#FFFF00",
            Contents = "Highlighted text"
        };

        // User adds annotation
        vm.AddAnnotation(annot);

        Assert.Contains(annot, vm.AllAnnotations);
        Assert.Contains(annot, vm.Pages[0].AnnotationsOnPage);
        Assert.Contains("Added Highlight annotation", vm.StatusText);
    }

    [Fact]
    public async Task TestDeleteAnnotationUserEvents()
    {
        string pdfPath = CreateSamplePdf("delete_annot.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        var annot = new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Underline,
            X = 0.2,
            Y = 0.4,
            Width = 0.4,
            Height = 0.02,
            ColorHex = "#0000FF"
        };

        vm.AddAnnotation(annot);
        Assert.Single(vm.AllAnnotations);

        // User deletes annotation
        vm.DeleteAnnotation(annot);

        Assert.Empty(vm.AllAnnotations);
        Assert.Empty(vm.Pages[0].AnnotationsOnPage);
        Assert.Contains("Removed annotation", vm.StatusText);
    }

    [Fact]
    public async Task TestClearAllAnnotationsUserEvents()
    {
        string pdfPath = CreateSamplePdf("clear_annots.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        vm.AddAnnotation(new AnnotationModel { PageNumber = 1, Type = AnnotationType.Highlight });
        vm.AddAnnotation(new AnnotationModel { PageNumber = 2, Type = AnnotationType.Note });

        Assert.Equal(2, vm.AllAnnotations.Count);

        // User clicks Clear All Annotations
        vm.ClearAllAnnotations();

        Assert.Empty(vm.AllAnnotations);
        Assert.Empty(vm.Pages[0].AnnotationsOnPage);
        Assert.Empty(vm.Pages[1].AnnotationsOnPage);
        Assert.Equal("Cleared all annotations.", vm.StatusText);
    }

    [Fact]
    public async Task TestInkAnnotationPointsUserEvents()
    {
        string pdfPath = CreateSamplePdf("ink_annot.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        var inkAnnot = new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Ink,
            ColorHex = "#FF0000",
            InkPoints = new System.Collections.Generic.List<Point>
            {
                new Point(0.1, 0.1),
                new Point(0.15, 0.18),
                new Point(0.2, 0.25)
            }
        };

        vm.AddAnnotation(inkAnnot);

        Assert.Single(vm.AllAnnotations);
        var added = vm.AllAnnotations.First();
        Assert.Equal(AnnotationType.Ink, added.Type);
        Assert.Equal(3, added.InkPoints?.Count);
    }
}
