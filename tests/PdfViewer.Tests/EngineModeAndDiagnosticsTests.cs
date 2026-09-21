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

    [Fact]
    public void HybridVectorDocumentService_EngineModeCanBeUpdated()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        Assert.Equal(PdfEngineMode.Auto, service.EngineMode);

        service.EngineMode = PdfEngineMode.Vector;
        Assert.Equal(PdfEngineMode.Vector, service.EngineMode);

        service.EngineMode = PdfEngineMode.Pdfium;
        Assert.Equal(PdfEngineMode.Pdfium, service.EngineMode);
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
        Assert.Contains("PDFium", reportPdfium.BadgeText);
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
        Assert.Equal(PdfEngineMode.Pdfium, vm.CurrentEngineMode);
        Assert.False(vm.IsAutoEngineMode);
        Assert.False(vm.IsVectorEngineMode);
        Assert.True(vm.IsPdfiumEngineMode);

        vm.SetEngineModeAutoCommand.Execute(null);
        Assert.Equal(PdfEngineMode.Auto, vm.CurrentEngineMode);
        Assert.True(vm.IsAutoEngineMode);
    }

    [Fact]
    public async Task EngineShowcasePdf_ExhibitsVectorAndFallbackAcrossPages()
    {
        string samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/EngineShowcase.pdf"));
        if (!Directory.Exists(Path.GetDirectoryName(samplePath)))
        {
            samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../samples/EngineShowcase.pdf"));
        }
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

        // Page 2: Triggers fallback due to 'ri'
        var p2 = await service.RenderPageAsync(2, 150);
        Assert.NotNull(p2);
        var r2 = service.GetPageEngineReport(2);
        Assert.NotNull(r2);
        Assert.Equal(PdfEngineMode.Pdfium, r2.Engine);
        Assert.True(r2.IsFallback);
        Assert.Contains("PDFium (Fallback)", r2.BadgeText);
        Assert.Contains(r2.FallbackReasons, r => r.Contains("ri"));

        // Page 3: Native Vector
        var p3 = await service.RenderPageAsync(3, 150);
        Assert.NotNull(p3);
        var r3 = service.GetPageEngineReport(3);
        Assert.NotNull(r3);
        Assert.Equal(PdfEngineMode.Vector, r3.Engine);
        Assert.False(r3.IsFallback);
        Assert.Contains("Vector", r3.BadgeText);
    }
}
