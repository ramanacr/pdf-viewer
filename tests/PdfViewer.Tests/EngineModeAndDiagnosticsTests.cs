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
}
