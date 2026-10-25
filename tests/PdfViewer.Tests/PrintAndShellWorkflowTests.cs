using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

public class PrintAndShellWorkflowTests : IDisposable
{
    private readonly string _testDir;

    public PrintAndShellWorkflowTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerPrintShellTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
#if PDFIUM_RENDERER
        PdfiumNativeBridge.EnsureInitialized();
#endif
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

    private string CreateSamplePdf(string name = "sample.pdf", int pageCount = 4)
    {
        string filePath = Path.Combine(_testDir, name);
        TestPdfBuilder.CreateSimplePdf(filePath, pageCount);
        return filePath;
    }

    [Fact]
    public async Task TestPrintPreviewViewModelCustomRangeParsing()
    {
        string pdfPath = CreateSamplePdf("print_range.pdf", 10);
#if PDFIUM_RENDERER
        using var service = new PdfiumDocumentService();
#else
        using var service = new HybridVectorDocumentService(PdfViewer.Core.Security.PdfSecurityPolicy.DefaultStrict);
#endif
        await service.OpenDocumentAsync(pdfPath);

        var vm = new PrintPreviewViewModel(service, 1);

        // Test RangeMode switching
        vm.RangeMode = PrintRangeMode.CurrentPage;
        Assert.Equal(1, vm.PreviewPageCount);
        Assert.Equal(1, vm.PreviewPageNumber);

        vm.RangeMode = PrintRangeMode.AllPages;
        Assert.Equal(10, vm.PreviewPageCount);

        vm.RangeMode = PrintRangeMode.CustomRange;
        vm.CustomPageRange = "3-7";

        // Test custom scale percent
        vm.Scaling = PrintScalingMode.CustomScale;
        vm.CustomScalePercent = 150;
        Assert.Equal(150, vm.CustomScalePercent);

        // Test copies and collate
        vm.Copies = 3;
        vm.Collate = false;
        Assert.Equal(3, vm.Copies);
        Assert.False(vm.Collate);

        // Test cancel
        bool closed = false;
        vm.CloseAction = () => closed = true;
        vm.Cancel();

        Assert.True(closed);
        Assert.False(vm.DialogResult);
    }

    [Fact]
    public void TestShellIntegrationRegistrationLifecycle()
    {
        // Written under the run's own HKCU key (TestProfileIsolation), never the real Software\Classes.
        string classes = ShellIntegrationService.ClassesRoot;
        Assert.NotEqual(@"Software\Classes", classes);
        string fakeExe = Path.Combine(_testDir, "PdfViewer.exe");
        File.WriteAllText(fakeExe, "fake binary");
        string fileIcon = Path.Combine(_testDir, "assets", "pdf_file.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(fileIcon)!);
        File.WriteAllBytes(fileIcon, new byte[] { 0, 0, 1, 0 });

        // Register shell association
        ShellIntegrationService.RegisterShellAssociation(fakeExe);

        // PDFs show the file icon whether Windows takes the association from the ProgId or, after
        // "Open with", from the Applications entry.
        foreach (string key in new[] { @"PdfViewer.Document\DefaultIcon", @"Applications\PdfViewer.exe\DefaultIcon" })
        {
            using var iconKey = Registry.CurrentUser.OpenSubKey($@"{classes}\{key}");
            Assert.NotNull(iconKey);
            Assert.Equal($"{fileIcon},0", iconKey.GetValue(""));
        }

        using (var progKey = Registry.CurrentUser.OpenSubKey($@"{classes}\PdfViewer.Document"))
        {
            Assert.NotNull(progKey);
            Assert.Equal("PDF Document", progKey.GetValue(""));
            Assert.Equal("document", progKey.GetValue("PerceivedType"));

            using var cmdKey = progKey.OpenSubKey(@"shell\open\command");
            Assert.NotNull(cmdKey);
            string? cmdVal = cmdKey.GetValue("") as string;
            Assert.Contains(fakeExe, cmdVal);
        }

        // Unregister shell association
        ShellIntegrationService.UnregisterShellAssociation();

        using (var checkKey = Registry.CurrentUser.OpenSubKey($@"{classes}\PdfViewer.Document"))
        {
            Assert.Null(checkKey);
        }
        using (var appKey = Registry.CurrentUser.OpenSubKey($@"{classes}\Applications\PdfViewer.exe"))
        {
            Assert.Null(appKey);
        }
    }
}
