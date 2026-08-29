using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;
using AnnotationSaveMode = PdfViewer.Models.AnnotationSaveMode;
using AnnotationType = PdfViewer.Models.AnnotationType;
using DocumentMetadata = PdfViewer.Models.DocumentMetadata;

namespace PdfViewer.Tests;

public class ViewModelUserEventTests : IDisposable
{
    private readonly string _testDir;

    public ViewModelUserEventTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerVmTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task TestDocumentOpenAndCloseUserEvents()
    {
        string pdfPath = CreateSamplePdf("open_close.pdf", 4);
        var vm = new MainViewModel();

        Assert.False(vm.IsDocumentLoaded);
        Assert.Equal(0, vm.PageCount);
        Assert.Equal(1, vm.CurrentPageNumber);

        // User opens document
        await vm.LoadDocumentAsync(pdfPath);

        Assert.True(vm.IsDocumentLoaded);
        Assert.Equal(4, vm.PageCount);
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.NotNull(vm.Metadata);
        Assert.Equal(pdfPath, vm.Metadata.FilePath);
        Assert.Equal(4, vm.Pages.Count);
        Assert.Equal(4, vm.Thumbnails.Count);

        // User closes document
        vm.CloseDocument();

        Assert.False(vm.IsDocumentLoaded);
        Assert.Equal(0, vm.PageCount);
        Assert.Empty(vm.Pages);
        Assert.Empty(vm.Thumbnails);
        Assert.Null(vm.Metadata);
    }

    [Fact]
    public async Task TestPageNavigationUserEvents()
    {
        string pdfPath = CreateSamplePdf("navigation.pdf", 5);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        Assert.Equal(1, vm.CurrentPageNumber);

        // Next page
        vm.NextPage();
        Assert.Equal(2, vm.CurrentPageNumber);

        // Next page again
        vm.NextPage();
        Assert.Equal(3, vm.CurrentPageNumber);

        // Previous page
        vm.PreviousPage();
        Assert.Equal(2, vm.CurrentPageNumber);

        // Last page
        vm.LastPage();
        Assert.Equal(5, vm.CurrentPageNumber);

        // Next page on last page should clamp to 5
        vm.NextPage();
        Assert.Equal(5, vm.CurrentPageNumber);

        // First page
        vm.FirstPage();
        Assert.Equal(1, vm.CurrentPageNumber);

        // Previous page on first page should clamp to 1
        vm.PreviousPage();
        Assert.Equal(1, vm.CurrentPageNumber);

        // Direct navigation
        vm.NavigateToPage(4);
        Assert.Equal(4, vm.CurrentPageNumber);

        // Out of range navigation should be ignored
        vm.NavigateToPage(-10);
        Assert.Equal(4, vm.CurrentPageNumber);

        vm.NavigateToPage(100);
        Assert.Equal(4, vm.CurrentPageNumber);
    }

    [Fact]
    public async Task TestZoomAndFitUserEvents()
    {
        string pdfPath = CreateSamplePdf("zoom.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        double initialZoom = vm.ZoomLevel;
        Assert.Equal(1.0, initialZoom);
        Assert.Equal("100%", vm.ZoomPercentageText);

        // User zooms in
        vm.ZoomIn();
        Assert.True(vm.ZoomLevel > initialZoom);

        // User zooms out
        vm.ZoomOut();
        Assert.Equal(1.0, Math.Round(vm.ZoomLevel, 2));

        // User changes zoom via slider/preset
        vm.SetZoom(2.0);
        Assert.Equal(2.0, vm.ZoomLevel);
        Assert.Equal("200%", vm.ZoomPercentageText);

        // Fit Width mode
        vm.GetViewportSizeFunc = () => (800, 600);
        vm.FitWidth();
        Assert.Equal(PageFitMode.FitWidth, vm.FitMode);

        // Fit Page mode
        vm.FitPage();
        Assert.Equal(PageFitMode.FitPage, vm.FitMode);

        // Set Zoom (100%)
        vm.SetZoom(1.0);
        Assert.Equal(PageFitMode.Custom, vm.FitMode);
        Assert.Equal(1.0, vm.ZoomLevel);

        // Zoom Clamping (0.25 to 5.0)
        vm.SetZoom(0.05);
        Assert.Equal(0.25, vm.ZoomLevel);

        vm.SetZoom(10.0);
        Assert.Equal(5.0, vm.ZoomLevel);
    }

    [Fact]
    public async Task TestPageRotationUserEvents()
    {
        string pdfPath = CreateSamplePdf("rotate.pdf", 3);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        Assert.Equal(0, vm.RotationAngle);

        // User rotates clockwise 90 degrees
        vm.RotateClockwise();
        Assert.Equal(90, vm.RotationAngle);

        vm.RotateClockwise();
        Assert.Equal(180, vm.RotationAngle);

        vm.RotateClockwise();
        Assert.Equal(270, vm.RotationAngle);

        vm.RotateClockwise();
        Assert.Equal(0, vm.RotationAngle); // Wraps to 0

        // User rotates counter-clockwise
        vm.RotateCounterClockwise();
        Assert.Equal(270, vm.RotationAngle);

        vm.RotateCounterClockwise();
        Assert.Equal(180, vm.RotationAngle);
    }

    [Fact]
    public async Task TestViewModeToggleUserEvents()
    {
        string pdfPath = CreateSamplePdf("viewmode.pdf", 3);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        Assert.Equal(ViewLayoutMode.Continuous, vm.ViewMode);

        // User switches to Single Page mode
        vm.ToggleViewMode();
        Assert.Equal(ViewLayoutMode.SinglePage, vm.ViewMode);

        // User switches back to Continuous mode
        vm.ToggleViewMode();
        Assert.Equal(ViewLayoutMode.Continuous, vm.ViewMode);
    }

    [Fact]
    public async Task TestSidebarToggleUserEvents()
    {
        var vm = new MainViewModel();

        Assert.True(vm.IsSidebarOpen);

        // User toggles sidebar off
        vm.ToggleSidebar();
        Assert.False(vm.IsSidebarOpen);

        // User toggles sidebar on
        vm.ToggleSidebar();
        Assert.True(vm.IsSidebarOpen);
    }

    [Fact]
    public async Task TestPanToolUserEvents()
    {
        var vm = new MainViewModel();

        Assert.False(vm.IsPanningEnabled);

        // User activates pan tool
        vm.TogglePanTool();
        Assert.True(vm.IsPanningEnabled);
        Assert.Null(vm.ActiveAnnotationTool);

        // User deactivates pan tool
        vm.TogglePanTool();
        Assert.False(vm.IsPanningEnabled);
    }

    [Fact]
    public void TestThemeSwitchingUserEvents()
    {
        var vm = new MainViewModel();

        ThemeManager.SetTheme(AppTheme.Light);
        Assert.Equal(AppTheme.Light, ThemeManager.CurrentTheme);

        // User toggles theme
        vm.ToggleTheme();
        Assert.Equal(AppTheme.Dark, ThemeManager.CurrentTheme);

        // User toggles theme back
        vm.ToggleTheme();
        Assert.Equal(AppTheme.Light, ThemeManager.CurrentTheme);
    }

    [Fact]
    public async Task TestDocumentPropertiesUserEvents()
    {
        string pdfPath = CreateSamplePdf("props.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        bool propertiesDialogShown = false;
        DocumentMetadata? capturedMetadata = null;

        vm.ShowPropertiesAction = meta =>
        {
            propertiesDialogShown = true;
            capturedMetadata = meta;
        };

        // User clicks File -> Properties
        vm.ShowProperties();

        Assert.True(propertiesDialogShown);
        Assert.NotNull(capturedMetadata);
        Assert.Equal(2, capturedMetadata.PageCount);
        Assert.Equal(pdfPath, capturedMetadata.FilePath);
    }

    [Fact]
    public async Task TestExportImagesUserEvents()
    {
        string pdfPath = CreateSamplePdf("export.pdf", 3);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        string exportOutDir = Path.Combine(_testDir, "exported_imgs");
        Directory.CreateDirectory(exportOutDir);

        bool exportCalled = false;
        vm.ShowExportDialogFunc = meta =>
        {
            exportCalled = true;
            return (true, exportOutDir, "PageExport", 1, 2, "PNG", 150);
        };

        // User triggers export images
        await vm.ExportImagesAsync();

        Assert.True(exportCalled);
        var exportedFiles = Directory.GetFiles(exportOutDir, "*.png");
        Assert.True(exportedFiles.Length >= 2);
    }

    [Fact]
    public async Task TestSaveAnnotatedUserEvents()
    {
        string pdfPath = CreateSamplePdf("save_annot.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        string targetSavedPdf = Path.Combine(_testDir, "annotated_out.pdf");
        bool saveDialogCalled = false;

        vm.ShowSaveAnnotatedDialogFunc = meta =>
        {
            saveDialogCalled = true;
            return (true, targetSavedPdf, AnnotationSaveMode.Embedded);
        };

        // User adds an annotation
        vm.ToggleAnnotationTool("Highlight");
        var page = vm.Pages[0];
        vm.AddAnnotation(new AnnotationModel
        {
            PageNumber = 1,
            Type = AnnotationType.Highlight,
            X = 0.1,
            Y = 0.1,
            Width = 0.4,
            Height = 0.1,
            ColorHex = "#FFFFE0"
        });

        // User clicks Save As
        await vm.SaveAnnotatedAsAsync();

        Assert.True(saveDialogCalled);
        Assert.True(File.Exists(targetSavedPdf));
        Assert.True(new FileInfo(targetSavedPdf).Length > 0);
    }

    [Fact]
    public async Task TestPrintCommandUserEvents()
    {
        string pdfPath = CreateSamplePdf("print_cmd.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        bool printDialogInvoked = false;
        vm.ShowPrintDialogFunc = (docService, currPage) =>
        {
            printDialogInvoked = true;
            Assert.NotNull(docService);
            Assert.Equal(1, currPage);
            return true;
        };

        // User clicks Print
        vm.Print();

        Assert.True(printDialogInvoked);
        Assert.Equal("Print job sent to printer.", vm.StatusText);
    }
}
