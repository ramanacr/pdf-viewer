using System;
using System.IO;
using System.Threading.Tasks;
using PdfEngine.Vector;
using PdfViewer.Core.Security;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

public class EngineModeAndDiagnosticsTests
{
    /// <summary>samples/EngineShowcase.pdf at the repository root, from whichever output folder the tests run (bin\ or bin\nopdfium\).</summary>
    private static string ShowcasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
        return Path.Combine(root, "samples", "EngineShowcase.pdf");
    }
#if PDFIUM_RENDERER
    [Fact]
    public void PdfiumDocumentService_ReportsPdfiumEngine()
    {
        using var service = new PdfiumDocumentService(PdfSecurityPolicy.DefaultStrict);
        Assert.Equal(PdfEngineMode.Pdfium, service.EngineMode);

        var report = service.GetPageEngineReport(1);
        Assert.NotNull(report);
        Assert.Equal(PdfEngineMode.Pdfium, report.Engine);
        Assert.False(report.IsFallback);
        Assert.Contains("PDFium", report.BadgeText);
    }
#endif

    [Fact]
    public void HybridVectorDocumentService_EngineModeCanBeUpdated()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        Assert.Equal(PdfEngineMode.Auto, service.EngineMode);

        service.EngineMode = PdfEngineMode.Vector;
        Assert.Equal(PdfEngineMode.Vector, service.EngineMode);

        service.EngineMode = PdfEngineMode.Pdfium;
#if PDFIUM_RENDERER
        Assert.Equal(PdfEngineMode.Pdfium, service.EngineMode);
#else
        Assert.Equal(PdfEngineMode.Auto, service.EngineMode); // no PDFium in this build: PDFium mode is Auto
#endif
    }

    [Fact]
    public void HybridVectorDocumentService_PendingReportMatchesEngineMode()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        var reportAuto = service.GetPageEngineReport(1);
        Assert.NotNull(reportAuto);
        Assert.Contains("Vector", reportAuto.BadgeText);

        service.EngineMode = PdfEngineMode.Pdfium;
        var reportPdfium = service.GetPageEngineReport(1);
        Assert.NotNull(reportPdfium);
#if PDFIUM_RENDERER
        Assert.Contains("PDFium", reportPdfium.BadgeText);
#else
        Assert.DoesNotContain("PDFium", reportPdfium.BadgeText);
        Assert.Contains("Vector", reportPdfium.BadgeText);
#endif
    }

    [Fact]
    public void MainViewModel_EngineModePropertiesAndCommands_ToggleCorrectly()
    {
        var vm = new MainViewModel();

        Assert.Equal(PdfEngineMode.Auto, vm.CurrentEngineMode);
        Assert.True(vm.IsAutoEngineMode);
        Assert.False(vm.IsVectorEngineMode);
        Assert.False(vm.IsPdfiumEngineMode);

        vm.SetEngineModeVectorCommand.Execute(null);
        Assert.Equal(PdfEngineMode.Vector, vm.CurrentEngineMode);
        Assert.False(vm.IsAutoEngineMode);
        Assert.True(vm.IsVectorEngineMode);
        Assert.False(vm.IsPdfiumEngineMode);

        vm.SetEngineModePdfiumCommand.Execute(null);
#if PDFIUM_RENDERER
        Assert.Equal(System.Windows.Visibility.Visible, vm.PdfiumEngineModeVisibility);
        Assert.Equal(PdfEngineMode.Pdfium, vm.CurrentEngineMode);
        Assert.False(vm.IsAutoEngineMode);
        Assert.False(vm.IsVectorEngineMode);
        Assert.True(vm.IsPdfiumEngineMode);
#else
        // The published app: no PDFium menu item, and the mode cannot be selected.
        Assert.Equal(System.Windows.Visibility.Collapsed, vm.PdfiumEngineModeVisibility);
        Assert.Equal(PdfEngineMode.Auto, vm.CurrentEngineMode);
        Assert.False(vm.IsPdfiumEngineMode);
#endif

        vm.SetEngineModeAutoCommand.Execute(null);
        Assert.Equal(PdfEngineMode.Auto, vm.CurrentEngineMode);
        Assert.True(vm.IsAutoEngineMode);
    }

    [Fact]
    public async Task EngineShowcasePdf_ExhibitsVectorAndFallbackAcrossPages()
    {
        string samplePath = ShowcasePath();

        TestPdfBuilder.CreateEngineShowcasePdf(samplePath);

        Assert.True(File.Exists(samplePath));
        Assert.True(new FileInfo(samplePath).Length > 500);

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        var meta = await service.OpenDocumentAsync(samplePath);
        Assert.Equal(3, meta.PageCount);

        // Page 1: Native Vector
        var p1 = await service.RenderPageAsync(1, 150);
        Assert.NotNull(p1);
        var r1 = service.GetPageEngineReport(1);
        Assert.NotNull(r1);
        Assert.True(r1.Engine == PdfEngineMode.Vector);
        Assert.False(r1.IsFallback);
        Assert.Contains("Vector", r1.BadgeText);

        // Page 2: a damaged JPEG the engine cannot decode is left out, as PDFium leaves out what it
        // cannot decode; everything else (knockout group included) is drawn by the engine, and no
        // other renderer is asked. ('ri' is a valid operator and must not be reported.)
        var p2 = await service.RenderPageAsync(2, 150);
        Assert.NotNull(p2);
        var r2 = service.GetPageEngineReport(2);
        Assert.NotNull(r2);
        Assert.Equal(PdfEngineMode.Vector, r2.Engine);
        Assert.False(r2.IsFallback);
        Assert.Contains("Vector", r2.BadgeText);
        Assert.Contains(r2.FallbackReasons, r => r.Contains("ImageDecode"));
        Assert.DoesNotContain(r2.FallbackReasons, r => r.Contains("UnknownOperator"));
        // The region without fallback pixels is not covered by a placeholder (strict mode's orange).
        Assert.Equal(0, CountOutlinePixels(p2));

        // Page 3: Native Vector
        var p3 = await service.RenderPageAsync(3, 150);
        Assert.NotNull(p3);
        var r3 = service.GetPageEngineReport(3);
        Assert.NotNull(r3);
        Assert.Equal(PdfEngineMode.Vector, r3.Engine);
        Assert.False(r3.IsFallback);
        Assert.Contains("Vector", r3.BadgeText);
    }

    /// <summary>Pixels of the orange outline strict vector mode draws around unsupported regions.</summary>
    private static int CountOutlinePixels(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > 240 && pixels[i + 1] is > 120 and < 160 && pixels[i] < 30) count++;
        return count;
    }

    [Fact]
    public async Task StrictVectorMode_OutlinesWhatTheEngineCannotReproduce()
    {
        string samplePath = Path.Combine(Path.GetTempPath(), $"showcase-strict-{Guid.NewGuid():N}.pdf");
        TestPdfBuilder.CreateEngineShowcasePdf(samplePath);
        try
        {
            using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Vector);
            await service.OpenDocumentAsync(samplePath);
            var p2 = await service.RenderPageAsync(2, 150);
            Assert.NotNull(p2);
            Assert.True(CountOutlinePixels(p2!) > 0, "strict mode outlines the undecodable image");
            Assert.Contains("unsupported", service.GetPageEngineReport(2)!.BadgeText);
        }
        finally
        {
            File.Delete(samplePath);
        }
    }

    [Fact]
    [Trait("Category", PdfiumGoldens.Trait)]
    public async Task HybridVectorDocumentService_ExtractPageTextSegments_ReturnsPerCharacterSegmentsInVectorMode()
    {
        string samplePath = ShowcasePath();

        TestPdfBuilder.CreateEngineShowcasePdf(samplePath);

        // --- Vector mode: should produce per-character segments ---
        using var vectorService = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Vector);
        await vectorService.OpenDocumentAsync(samplePath);
        var vectorSegments = await vectorService.ExtractPageTextSegmentsAsync(1);

        Assert.NotEmpty(vectorSegments);

        // In vector mode every segment should be exactly ONE character
        foreach (var seg in vectorSegments)
        {
            Assert.Equal(1, seg.Text.Length);
            Assert.InRange(seg.X, 0.0, 1.0);
            Assert.InRange(seg.Y, 0.0, 1.0);
            Assert.InRange(seg.Width, 0.0, 1.0);
            Assert.InRange(seg.Height, 0.0, 1.0);
        }

        // Reconstructed text should contain words from the page
        string reconstructed = string.Concat(vectorSegments.Select(s => s.Text));
        Assert.Contains("Vector", reconstructed);

#if PDFIUM_RENDERER
        // --- PDFium mode: word-grouped segments (each segment may contain multiple characters) ---
        using var pdfiumService = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Pdfium);
        await pdfiumService.OpenDocumentAsync(samplePath);
        var pdfiumSegments = await pdfiumService.ExtractPageTextSegmentsAsync(1);

        Assert.NotEmpty(pdfiumSegments);
        PdfiumGoldens.Verify("EngineMode_ShowcaseWordSegments", pdfiumSegments.Count);

        // Vector char segments should be >= PDFium word segments (finer granularity)
        Assert.True(vectorSegments.Count >= pdfiumSegments.Count,
            $"Expected vector char segments ({vectorSegments.Count}) >= PDFium word segments ({pdfiumSegments.Count})");
#endif

        // Vector char segments should be >= PDFium's word segments (finer granularity): PDFium's count
        // as committed (PdfiumGoldens), in every build; the developer build also checks it live above.
        int pdfiumWordCount = PdfiumGoldens.Load<int>("EngineMode_ShowcaseWordSegments");
        Assert.True(vectorSegments.Count >= pdfiumWordCount,
            $"Expected vector char segments ({vectorSegments.Count}) >= PDFium word segments ({pdfiumWordCount})");
    }
}
