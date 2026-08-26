using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Text;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
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
    public async Task TestMultiPageRenderingBeyondFourPages()
    {
        string pdfPath = CreateSamplePdf("multipage_test.pdf", 10);

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        Assert.Equal(10, service.PageCount);
        for (int i = 1; i <= 10; i++)
        {
            var bitmap = await service.RenderPageAsync(i, dpi: 100, rotationAngle: 0);
            Assert.NotNull(bitmap);
            Assert.True(bitmap.PixelWidth > 0, $"Page {i} bitmap width is 0");
            Assert.True(bitmap.PixelHeight > 0, $"Page {i} bitmap height is 0");
        }
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
        IconBuilder.BuildIcon();

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

        string appIco = Path.Combine(rootDir, "assets", "app_icon.ico");
        string appPng = Path.Combine(rootDir, "assets", "app_icon.png");
        string pdfIco = Path.Combine(rootDir, "assets", "pdf_file.ico");
        string pdfPng = Path.Combine(rootDir, "assets", "pdf_file.png");

        Assert.True(File.Exists(appIco));
        Assert.True(File.Exists(appPng));
        Assert.True(File.Exists(pdfIco));
        Assert.True(File.Exists(pdfPng));

        Assert.True(new FileInfo(appIco).Length > 1000);
        Assert.True(new FileInfo(pdfIco).Length > 1000);
    }

    [Fact]
    public void TestAutoIncrementedVersion()
    {
        var asm = typeof(LicenseService).Assembly;
        var version = asm.GetName().Version;

        Assert.NotNull(version);
        Assert.True(version.Major >= 1);
        Assert.True(version.Build >= 0);
    }

    [Fact]
    public void TestApplicationVersionProperties()
    {
        var meta = new DocumentMetadata();
        Assert.NotNull(meta.ApplicationVersion);
        Assert.StartsWith("1.0.", meta.ApplicationVersion);

        var vm = new MainViewModel();
        Assert.NotNull(vm.ApplicationVersion);
        Assert.StartsWith("1.0.", vm.ApplicationVersion);
    }

    [Fact]
    public void TestVersionComparison()
    {
        // ParseVersion
        Assert.Equal(new Version(1, 0, 12), UpdateService.ParseVersion("v1.0.12"));
        Assert.Equal(new Version(1, 0, 0), UpdateService.ParseVersion("v1.0.0"));
        Assert.Equal(new Version(2, 5, 0), UpdateService.ParseVersion("v2.5"));
        Assert.Equal(new Version(3, 0, 0), UpdateService.ParseVersion("3.0.0"));
        Assert.Equal(new Version(1, 0, 12), UpdateService.ParseVersion("v1.0.12+abc123"));
        Assert.Equal(new Version(0, 0, 0), UpdateService.ParseVersion(""));

        // CompareVersions
        Assert.True(UpdateService.CompareVersions(new Version(1, 0, 12), new Version(1, 0, 0)) > 0);
        Assert.True(UpdateService.CompareVersions(new Version(1, 0, 0), new Version(1, 0, 12)) < 0);
        Assert.Equal(0, UpdateService.CompareVersions(new Version(1, 0, 12), new Version(1, 0, 12)));
        Assert.True(UpdateService.CompareVersions(new Version(2, 0, 0), new Version(1, 9, 99)) > 0);
        Assert.True(UpdateService.CompareVersions(new Version(1, 1, 0), new Version(1, 0, 99)) > 0);
    }

    [Fact]
    public void TestUpdateInfoParsing()
    {
        string sampleJson = @"{
            ""tag_name"": ""v2.0.0"",
            ""name"": ""v2.0.0 - Major Update"",
            ""body"": ""This is a big release with many features."",
            ""html_url"": ""https://github.com/ramanacr/pdf-viewer/releases/tag/v2.0.0"",
            ""published_at"": ""2026-08-26T15:04:31Z"",
            ""assets"": [
                {
                    ""name"": ""PdfViewerSetup.exe"",
                    ""size"": 42519751,
                    ""browser_download_url"": ""https://github.com/ramanacr/pdf-viewer/releases/download/v2.0.0/PdfViewerSetup.exe""
                },
                {
                    ""name"": ""PdfViewer.exe"",
                    ""size"": 59753193,
                    ""browser_download_url"": ""https://github.com/ramanacr/pdf-viewer/releases/download/v2.0.0/PdfViewer.exe""
                }
            ]
        }";

        var info = UpdateService.ParseReleaseJson(sampleJson, new Version(1, 0, 12));

        Assert.True(info.IsUpdateAvailable);
        Assert.Equal("1.0.12", info.CurrentVersion);
        Assert.Equal("2.0.0", info.LatestVersion);
        Assert.Equal("v2.0.0 - Major Update", info.ReleaseTitle);
        Assert.Contains("big release", info.ReleaseNotes);
        Assert.NotNull(info.InstallerDownloadUrl);
        Assert.Contains("PdfViewerSetup.exe", info.InstallerDownloadUrl);
        Assert.Equal(42519751, info.InstallerSize);
        Assert.NotNull(info.PublishedAt);
        Assert.Equal("40.5 MB", info.FormattedInstallerSize);

        // Test with same version — no update
        var infoSame = UpdateService.ParseReleaseJson(sampleJson, new Version(2, 0, 0));
        Assert.False(infoSame.IsUpdateAvailable);

        // Test with newer local version — no update
        var infoNewer = UpdateService.ParseReleaseJson(sampleJson, new Version(3, 0, 0));
        Assert.False(infoNewer.IsUpdateAvailable);
    }
}
