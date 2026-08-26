using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Text;
using PdfViewer.Models;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

public class PdfServiceTests : IDisposable
{
    private readonly string _testDir;

    public PdfServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        LicenseService.Initialize();
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

    [Fact]
    public void TestLicenseInitialization()
    {
        LicenseService.Initialize();
        Assert.True(LicenseService.IsLicensed, $"License failed to initialize. Message: {LicenseService.LicenseStatusMessage}");
    }

    [Fact]
    public void TestEmbeddedLicenseResource()
    {
        var assembly = typeof(LicenseService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        Assert.Contains(resourceNames, name => name.EndsWith("Aspose.Total.lic", StringComparison.OrdinalIgnoreCase));

        using var stream = assembly.GetManifestResourceStream(resourceNames.First(n => n.EndsWith("Aspose.Total.lic", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }

    private string CreateSamplePdf(string name = "sample.pdf", int pageCount = 3)
    {
        string filePath = Path.Combine(_testDir, name);
        using var doc = new Document();

        for (int i = 1; i <= pageCount; i++)
        {
            var page = doc.Pages.Add();
            var text = new TextFragment($"This is page number {i} of the test document. Keyword: SearchableToken_{i}");
            text.TextState.FontSize = 16;
            text.TextState.Font = FontRepository.FindFont("Arial");
            page.Paragraphs.Add(text);

            // Add bookmark
            var outline = new OutlineItemCollection(doc.Outlines)
            {
                Title = $"Section {i} Title",
                Italic = false,
                Bold = true,
                Destination = new FitExplicitDestination(page)
            };
            doc.Outlines.Add(outline);
        }

        doc.Save(filePath);
        return filePath;
    }

    [Fact]
    public async Task TestDocumentLoadingAndMetadata()
    {
        string pdfPath = CreateSamplePdf("metadata_test.pdf", 3);

        using var service = new PdfDocumentService();
        var meta = await service.OpenDocumentAsync(pdfPath);

        Assert.NotNull(meta);
        Assert.Equal(3, meta.PageCount);
        Assert.Equal(3, service.PageCount);
        Assert.True(service.IsDocumentLoaded);
        Assert.Equal("metadata_test.pdf", meta.FileName);
        Assert.False(meta.IsEncrypted);
    }

    [Fact]
    public async Task TestBookmarksExtraction()
    {
        string pdfPath = CreateSamplePdf("bookmarks_test.pdf", 4);

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);
        var bookmarks = service.ExtractBookmarks();

        Assert.NotNull(bookmarks);
        Assert.Equal(4, bookmarks.Count);
        Assert.Equal("Section 1 Title", bookmarks[0].Title);
        Assert.Equal(1, bookmarks[0].TargetPageNumber);
        Assert.Equal("Section 4 Title", bookmarks[3].Title);
        Assert.Equal(4, bookmarks[3].TargetPageNumber);
    }

    [Fact]
    public async Task TestTextSearch()
    {
        string pdfPath = CreateSamplePdf("search_test.pdf", 3);

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var matches = await service.SearchTextAsync("SearchableToken_2");
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.PageNumber == 2 && m.Text.Contains("SearchableToken_2"));
    }

    [Fact]
    public async Task TestPageRendering()
    {
        string pdfPath = CreateSamplePdf("render_test.pdf", 2);

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var bitmap = await service.RenderPageAsync(1, dpi: 150, rotationAngle: 0);
        Assert.NotNull(bitmap);
        Assert.True(bitmap.PixelWidth > 0);
        Assert.True(bitmap.PixelHeight > 0);
        Assert.True(bitmap.IsFrozen);
    }

    [Fact]
    public async Task TestLruPageCache()
    {
        var cache = new LruPageCache(capacity: 3);

        // Dummy rendered mock tests
        using var service = new PdfDocumentService();
        string pdfPath = CreateSamplePdf("cache_test.pdf", 4);
        await service.OpenDocumentAsync(pdfPath);

        var img1 = service.RenderPage(1, 150, 0);
        var img2 = service.RenderPage(2, 150, 0);
        var img3 = service.RenderPage(3, 150, 0);
        var img4 = service.RenderPage(4, 150, 0);

        Assert.NotNull(img1);
        Assert.NotNull(img2);
        Assert.NotNull(img3);
        Assert.NotNull(img4);

        cache.Add(1, 150, 0, img1);
        cache.Add(2, 150, 0, img2);
        cache.Add(3, 150, 0, img3);

        Assert.True(cache.TryGet(1, 150, 0, out _));

        // Adding 4th item should evict least recently used (item 2)
        cache.Add(4, 150, 0, img4);
        Assert.False(cache.TryGet(2, 150, 0, out _));
        Assert.True(cache.TryGet(1, 150, 0, out _));
        Assert.True(cache.TryGet(4, 150, 0, out _));
    }

    [Fact]
    public async Task TestExportPagesToImages()
    {
        string pdfPath = CreateSamplePdf("export_test.pdf", 2);
        string exportDir = Path.Combine(_testDir, "exported_images");

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        await service.ExportPagesToImagesAsync(
            outputDirectory: exportDir,
            fileNamePrefix: "doc",
            startPage: 1,
            endPage: 2,
            format: "PNG",
            dpi: 150);

        string page1File = Path.Combine(exportDir, "doc_page_001.png");
        string page2File = Path.Combine(exportDir, "doc_page_002.png");

        Assert.True(File.Exists(page1File), "Page 1 exported image should exist");
        Assert.True(File.Exists(page2File), "Page 2 exported image should exist");
        Assert.True(new FileInfo(page1File).Length > 0);
    }

    [Fact]
    public async Task TestEncryptedPdfHandling()
    {
        string encPath = Path.Combine(_testDir, "encrypted.pdf");
        using (var doc = new Document())
        {
            var page = doc.Pages.Add();
            page.Paragraphs.Add(new TextFragment("Confidential secret text"));
            doc.Encrypt("userpass123", "ownerpass456", Permissions.PrintDocument, CryptoAlgorithm.AESx128);
            doc.Save(encPath);
        }

        using var service = new PdfDocumentService();

        // Should fail without password
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await service.OpenDocumentAsync(encPath);
        });

        // Should succeed with correct password
        var meta = await service.OpenDocumentAsync(encPath, "userpass123");
        Assert.NotNull(meta);
        Assert.True(meta.IsEncrypted);
        Assert.Equal(1, meta.PageCount);
    }

    [Fact]
    public void TestGenerateDemoDocument()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        string rootDir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")))
            {
                rootDir = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        string samplePath = Path.Combine(rootDir, "samples", "SampleDocument.pdf");
        string createdPath = SamplePdfGenerator.GenerateSamplePdf(samplePath);

        Assert.True(File.Exists(createdPath));
        Assert.True(new FileInfo(createdPath).Length > 0);
    }

    [Fact]
    public void TestGenerateApplicationIcons()
    {
        string srcJpg = @"C:\Users\ramanareddy\.gemini\antigravity\brain\9f87780e-135d-4888-9e66-56fa588be862\pdf_viewer_icon_1787752279772.jpg";
        if (!File.Exists(srcJpg)) return;

        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        string rootDir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")))
            {
                rootDir = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        string outIco = Path.Combine(rootDir, "assets", "app_icon.ico");
        string outPng = Path.Combine(rootDir, "assets", "app_icon.png");

        IconBuilder.BuildIcon(new[] { srcJpg, outIco, outPng });

        // Copy to project asset directories
        string[] targetDirs =
        {
            Path.Combine(rootDir, "src", "PdfViewer", "assets"),
            Path.Combine(rootDir, "src", "Installer", "assets")
        };

        foreach (var tDir in targetDirs)
        {
            Directory.CreateDirectory(tDir);
            File.Copy(outIco, Path.Combine(tDir, "app_icon.ico"), overwrite: true);
            File.Copy(outPng, Path.Combine(tDir, "app_icon.png"), overwrite: true);
        }

        Assert.True(File.Exists(outIco));
        Assert.True(File.Exists(outPng));
        Assert.True(new FileInfo(outIco).Length > 1000);
    }
}
