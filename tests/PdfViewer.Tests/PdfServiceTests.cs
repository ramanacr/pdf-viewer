using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Text;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;
using AnnotationType = PdfViewer.Models.AnnotationType;

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
        Assert.StartsWith("1.2.", meta.ApplicationVersion);

        var vm = new MainViewModel();
        Assert.NotNull(vm.ApplicationVersion);
        Assert.StartsWith("1.2.", vm.ApplicationVersion);
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

    [Fact]
    public async Task TestSearchHighlightCoordinatesAndNormalization()
    {
        string samplePdf = CreateSamplePdf("Highlight Coordinates Test Doc");
        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var matches = await service.SearchTextAsync("Keyword");
        Assert.NotEmpty(matches);

        foreach (var match in matches)
        {
            Assert.InRange(match.X, 0.0, 1.0);
            Assert.InRange(match.Y, 0.0, 1.0);
            Assert.InRange(match.Width, 0.001, 1.0);
            Assert.InRange(match.Height, 0.001, 1.0);
            Assert.Equal("Keyword", match.Text);
        }
    }

    [Fact]
    public async Task TestSaveAnnotatedEmbedded()
    {
        string samplePdf = CreateSamplePdf("Annotation Embedding Test Doc");
        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var annotations = new List<AnnotationModel>
        {
            new AnnotationModel
            {
                PageNumber = 1,
                Type = AnnotationType.Highlight,
                X = 0.1,
                Y = 0.2,
                Width = 0.3,
                Height = 0.05,
                ColorHex = "#FF32CD32",
                Contents = "Embedded highlight comment"
            },
            new AnnotationModel
            {
                PageNumber = 1,
                Type = AnnotationType.Note,
                X = 0.5,
                Y = 0.5,
                Width = 0.05,
                Height = 0.05,
                ColorHex = "#FFFFD700",
                Contents = "Embedded sticky note test"
            }
        };

        string targetPdf = Path.Combine(_testDir, "AnnotatedEmbedded.pdf");
        await service.SaveAnnotatedDocumentAsync(targetPdf, AnnotationSaveMode.Embedded, annotations, samplePdf);

        Assert.True(File.Exists(targetPdf));
        Assert.True(new FileInfo(targetPdf).Length > 1000);

        // Load the saved PDF and verify annotations are present
        using var verifyService = new PdfDocumentService();
        await verifyService.OpenDocumentAsync(targetPdf);
        var loadedAnnots = verifyService.LoadExistingAnnotations();

        Assert.NotEmpty(loadedAnnots);
        Assert.Contains(loadedAnnots, a => a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Note);
    }

    [Fact]
    public async Task TestSaveAnnotatedFlattened()
    {
        string samplePdf = CreateSamplePdf("Annotation Flattening Test Doc");
        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var annotations = new List<AnnotationModel>
        {
            new AnnotationModel
            {
                PageNumber = 1,
                Type = AnnotationType.Highlight,
                X = 0.1,
                Y = 0.2,
                Width = 0.3,
                Height = 0.05,
                ColorHex = "#FF32CD32",
                Contents = "Flattened highlight"
            }
        };

        string targetPdf = Path.Combine(_testDir, "AnnotatedFlattened.pdf");
        await service.SaveAnnotatedDocumentAsync(targetPdf, AnnotationSaveMode.Flattened, annotations, samplePdf);

        Assert.True(File.Exists(targetPdf));

        // Load the flattened PDF — annotations should now be baked graphics (0 comment objects)
        using var verifyService = new PdfDocumentService();
        await verifyService.OpenDocumentAsync(targetPdf);
        var loadedAnnots = verifyService.LoadExistingAnnotations();

        Assert.Empty(loadedAnnots); // Flattened into graphics, no comment objects remaining
    }

    [Fact]
    public async Task TestExportAnnotationsToXfdf()
    {
        string samplePdf = CreateSamplePdf("XFDF Export Test Doc");
        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var annotations = new List<AnnotationModel>
        {
            new AnnotationModel
            {
                PageNumber = 1,
                Type = AnnotationType.Highlight,
                X = 0.15,
                Y = 0.25,
                Width = 0.4,
                Height = 0.05,
                ColorHex = "#FF32CD32",
                Contents = "XFDF test comment",
                Author = "TestReviewer"
            }
        };

        string targetXfdf = Path.Combine(_testDir, "AnnotationsExport.xfdf");
        await service.SaveAnnotatedDocumentAsync(targetXfdf, AnnotationSaveMode.ExportXfdf, annotations, samplePdf);

        Assert.True(File.Exists(targetXfdf));
        string xmlContent = await File.ReadAllTextAsync(targetXfdf);

        Assert.Contains("<xfdf", xmlContent);
        Assert.Contains("<annots>", xmlContent);
        Assert.Contains("highlight", xmlContent);
        Assert.Contains("XFDF test comment", xmlContent);
        Assert.Contains("TestReviewer", xmlContent);
    }

    [Fact]
    public async Task TestPreventOverwriteOriginalDocument()
    {
        string samplePdf = CreateSamplePdf("Overwrite Prevention Test Doc");
        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var annotations = new List<AnnotationModel>();

        // Attempting to save over the original source file must throw InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.SaveAnnotatedDocumentAsync(samplePdf, AnnotationSaveMode.Embedded, annotations, samplePdf);
        });
    }

    [Fact]
    public async Task TestDoNotTreatLinksOrWidgetsAsHighlights()
    {
        string samplePdf = Path.Combine(_testDir, "DocWithLinks.pdf");
        using (var doc = new Document())
        {
            var page = doc.Pages.Add();
            var text = new TextFragment("Click this link here");
            page.Paragraphs.Add(text);

            // Add a LinkAnnotation
            var link = new LinkAnnotation(page, new Aspose.Pdf.Rectangle(100, 100, 300, 120))
            {
                Action = new Aspose.Pdf.Annotations.GoToURIAction("https://example.com")
            };
            page.Annotations.Add(link);
            doc.Save(samplePdf);
        }

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var loaded = service.LoadExistingAnnotations();
        // The LinkAnnotation must NOT be converted to a highlight annotation
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task TestExtractPageTextSegmentsAsync()
    {
        string samplePdf = Path.Combine(_testDir, "TextSegmentsTest.pdf");
        using (var doc = new Document())
        {
            var page = doc.Pages.Add();
            page.Paragraphs.Add(new TextFragment("Hello World from Text Selection"));
            page.Paragraphs.Add(new TextFragment("Second line with additional details and content"));
            doc.Save(samplePdf);
        }

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var segments = await service.ExtractPageTextSegmentsAsync(1);
        Assert.NotEmpty(segments);

        // Verify normalized coordinates are within [0.0, 1.0]
        foreach (var seg in segments)
        {
            Assert.Equal(1, seg.PageNumber);
            Assert.False(string.IsNullOrWhiteSpace(seg.Text));
            Assert.InRange(seg.X, 0.0, 1.0);
            Assert.InRange(seg.Y, 0.0, 1.0);
            Assert.InRange(seg.Width, 0.0, 1.0);
            Assert.InRange(seg.Height, 0.0, 1.0);
        }

        // Verify text content was captured
        string combinedText = string.Join(" ", segments.Select(s => s.Text));
        Assert.Contains("Hello", combinedText);
        Assert.Contains("World", combinedText);
        Assert.Contains("Selection", combinedText);
    }

    [Fact]
    public async Task TestPageViewModelSelectionAndExtraction()
    {
        string samplePdf = Path.Combine(_testDir, "PageViewModelSelectionTest.pdf");
        using (var doc = new Document())
        {
            var page = doc.Pages.Add();
            page.Paragraphs.Add(new TextFragment("Alpha Beta Gamma Delta"));
            doc.Save(samplePdf);
        }

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var pageVm = new PageViewModel(1, 612, 792);
        await pageVm.LoadTextSegmentsAsync(service);

        Assert.True(pageVm.IsTextExtracted);
        Assert.NotEmpty(pageVm.TextSegments);

        // Test Select All
        pageVm.SelectAllText();
        Assert.NotEmpty(pageVm.SelectedSegments);
        Assert.Equal(pageVm.TextSegments.Count, pageVm.SelectedSegments.Count);
        string selectedAllText = pageVm.GetSelectedText();
        Assert.Contains("Alpha", selectedAllText);
        Assert.Contains("Gamma", selectedAllText);

        // Test Clear Selection
        pageVm.ClearTextSelection();
        Assert.Empty(pageVm.SelectedSegments);
        Assert.Equal(string.Empty, pageVm.GetSelectedText());

        // Test Select Word At
        var firstSeg = pageVm.TextSegments[0];
        pageVm.SelectWordAt(new System.Windows.Point(firstSeg.X + firstSeg.Width / 2, firstSeg.Y + firstSeg.Height / 2));
        Assert.Single(pageVm.SelectedSegments);
        Assert.Equal(firstSeg.Text, pageVm.GetSelectedText());

        // Test Select Range
        pageVm.SelectRange(new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        Assert.NotEmpty(pageVm.SelectedSegments);
    }

    [Fact]
    public async Task TestMainViewModelSelectionAndHighlightCommand()
    {
        string samplePdf = Path.Combine(_testDir, "MainVmSelectionTest.pdf");
        using (var doc = new Document())
        {
            var page = doc.Pages.Add();
            page.Paragraphs.Add(new TextFragment("Important Highlightable Statement"));
            doc.Save(samplePdf);
        }

        var mainVm = new MainViewModel();
        await mainVm.LoadDocumentAsync(samplePdf);

        var firstPage = mainVm.Pages[0];
        await firstPage.LoadTextSegmentsAsync(mainVm.DocumentService);

        // Select all text
        firstPage.SelectAllText();
        mainVm.UpdateSelectionFromPages();

        Assert.True(mainVm.HasTextSelection);
        Assert.Contains("Important", mainVm.SelectedText);
        string expectedText = mainVm.SelectedText;

        // Trigger HighlightSelectedTextCommand
        Assert.True(mainVm.HighlightSelectedTextCommand.CanExecute(null));
        mainVm.HighlightSelectedTextCommand.Execute(null);

        // Verify highlight annotation was created
        Assert.NotEmpty(mainVm.AllAnnotations);
        var annot = mainVm.AllAnnotations.Last();
        Assert.Equal(AnnotationType.Highlight, annot.Type);
        Assert.Equal(1, annot.PageNumber);
        Assert.Equal(expectedText, annot.Contents);

        // Selection should be cleared after highlighting
        Assert.False(mainVm.HasTextSelection);
        Assert.Equal(string.Empty, mainVm.SelectedText);
    }

    [Fact]
    public async Task TestPageViewModelReRendersOnDpiChange()
    {
        // Regression test: LoadImageAsync used to skip re-rendering whenever an image
        // was already loaded and the rotation hadn't changed, even if a higher DPI was
        // requested (e.g. after zooming in). This left pages permanently blurry.
        string pdfPath = CreateSamplePdf("dpi_change_test.pdf", 1);

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var cache = new LruPageCache(10);
        var renderer = new AsyncPageRenderer(service, cache);
        var pageVm = new PageViewModel(1, 612, 792);

        await pageVm.LoadImageAsync(renderer, dpi: 50, rotation: 0);
        Assert.NotNull(pageVm.RenderedImage);
        int lowDpiWidth = pageVm.RenderedImage!.PixelWidth;

        await pageVm.LoadImageAsync(renderer, dpi: 150, rotation: 0);
        Assert.NotNull(pageVm.RenderedImage);
        int highDpiWidth = pageVm.RenderedImage!.PixelWidth;

        Assert.True(highDpiWidth > lowDpiWidth, "Requesting a higher DPI must re-render the page instead of reusing the stale low-DPI bitmap.");

        // Re-requesting the same DPI/rotation should be a no-op (no exception, same reference).
        var sameImage = pageVm.RenderedImage;
        await pageVm.LoadImageAsync(renderer, dpi: 150, rotation: 0);
        Assert.Same(sameImage, pageVm.RenderedImage);
    }

    [Fact]
    public void TestPageViewModelUnloadImageResetsDpiTracking()
    {
        var pageVm = new PageViewModel(1, 612, 792);
        var field = typeof(PageViewModel).GetField("_renderedDpi", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        field!.SetValue(pageVm, 300);
        pageVm.UnloadImage();

        Assert.Null(pageVm.RenderedImage);
        Assert.Equal(0, (int)field.GetValue(pageVm)!);
    }

    [Fact]
    public void TestSearchMatchRaisesPropertyChangedForIsCurrentMatch()
    {
        // Regression test: SearchMatch previously wasn't an ObservableObject, so toggling
        // IsCurrentMatch while navigating search results never repainted the active highlight.
        var match = new SearchMatch { PageNumber = 1, Text = "foo" };

        var raisedProperties = new System.Collections.Generic.List<string>();
        ((INotifyPropertyChanged)match).PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) raisedProperties.Add(e.PropertyName);
        };

        match.IsCurrentMatch = true;

        Assert.Contains(nameof(SearchMatch.IsCurrentMatch), raisedProperties);
    }

    [Fact]
    public async Task TestThumbnailsAreUnloadedImmediatelyOnRotation()
    {
        // Regression test: OnRotationChanged only unloaded PageViewModel images, never
        // ThumbnailViewModel images, so sidebar thumbnails kept the pre-rotation bitmap forever.
        string samplePdf = Path.Combine(_testDir, "ThumbnailRotationTest.pdf");
        using (var doc = new Document())
        {
            doc.Pages.Add().Paragraphs.Add(new TextFragment("Rotation thumbnail test"));
            doc.Save(samplePdf);
        }

        var mainVm = new MainViewModel();
        await mainVm.LoadDocumentAsync(samplePdf);

        var thumb = mainVm.Thumbnails[0];
        thumb.GetType().GetProperty(nameof(ThumbnailViewModel.ThumbnailImage))!
            .SetValue(thumb, System.Windows.Media.Imaging.BitmapSource.Create(
                1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0 }, 4));

        Assert.NotNull(thumb.ThumbnailImage);

        mainVm.RotateClockwise();

        Assert.Null(thumb.ThumbnailImage);
    }

    [Fact]
    public async Task TestPrintPaginatorForwardsRotationToRenderPage()
    {
        // Regression test: PrintDocument previously always rendered at rotation 0, ignoring
        // the document's current on-screen rotation, so rotated pages printed unrotated.
        string pdfPath = CreateSamplePdf("print_rotation_test.pdf", 1);

        using var service = new PdfDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var unrotated = new AsposePdfPaginator(service, 1, 1, 850, 1100, rotationAngle: 0);
        var rotated = new AsposePdfPaginator(service, 1, 1, 850, 1100, rotationAngle: 90);

        var rotationField = typeof(AsposePdfPaginator).GetField("_rotationAngle", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rotationField);
        Assert.Equal(0, (int)rotationField!.GetValue(unrotated)!);
        Assert.Equal(90, (int)rotationField.GetValue(rotated)!);

        // Both must still successfully produce a printable page using the forwarded rotation.
        var unrotatedPage = unrotated.GetPage(0);
        var rotatedPage = rotated.GetPage(0);

        Assert.NotNull(unrotatedPage);
        Assert.NotNull(rotatedPage);
    }

    [Fact]
    public async Task TestRecentFilesServiceConcurrentAddsDoNotLoseEntries()
    {
        // Regression test: LoadRecentFiles/AddRecentFile did an unsynchronized
        // read-modify-write of the settings file, so concurrent writers could
        // clobber each other's updates.
        string originalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PdfViewerNative");
        string tempDir = Path.Combine(_testDir, "recent_files_concurrency");

        try
        {
            RecentFilesService.SetSettingsDirectoryForTests(tempDir);

            var filePaths = new string[8];
            for (int i = 0; i < filePaths.Length; i++)
            {
                filePaths[i] = CreateSamplePdf($"recent_{i}.pdf", 1);
            }

            var tasks = new Task[filePaths.Length];
            for (int i = 0; i < filePaths.Length; i++)
            {
                string path = filePaths[i];
                tasks[i] = Task.Run(() => RecentFilesService.AddRecentFile(path));
            }
            await Task.WhenAll(tasks);

            var finalList = RecentFilesService.LoadRecentFiles();
            foreach (var path in filePaths)
            {
                Assert.Contains(finalList, p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            RecentFilesService.SetSettingsDirectoryForTests(originalDir);
        }
    }
}


