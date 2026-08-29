using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void TestPdfiumEngineInitialization()
    {
        PdfiumNativeBridge.EnsureInitialized();
        using var service = new PdfiumDocumentService();
        Assert.NotNull(service);
        Assert.False(service.IsDocumentLoaded);
    }

    private string CreateSamplePdf(string name = "sample.pdf", int pageCount = 3)
    {
        string filePath = Path.Combine(_testDir, name);
        return TestPdfBuilder.CreateSimplePdf(filePath, pageCount, "SearchableToken");
    }

    [Fact]
    public async Task TestDocumentLoadingAndMetadata()
    {
        string pdfPath = CreateSamplePdf("metadata_test.pdf", 3);

        using var service = new PdfiumDocumentService();
        var meta = await service.OpenDocumentAsync(pdfPath);

        Assert.NotNull(meta);
        Assert.Equal(3, meta.PageCount);
        Assert.Equal(3, service.PageCount);
        Assert.True(service.IsDocumentLoaded);
        Assert.Equal("metadata_test.pdf", meta.FileName);
        Assert.False(meta.IsEncrypted);
        Assert.Equal("Test Document Title", meta.Title);
        Assert.Equal("Test Author", meta.Author);
    }

    [Fact]
    public async Task TestBookmarksExtraction()
    {
        string pdfPath = CreateSamplePdf("bookmarks_test.pdf", 4);

        using var service = new PdfiumDocumentService();
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

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var matches = await service.SearchTextAsync("SearchableToken_2");
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.PageNumber == 2 && m.Text.Contains("SearchableToken_2"));
    }

    [Fact]
    public async Task TestTextSearchCaseSensitivity()
    {
        string pdfPath = CreateSamplePdf("search_case_test.pdf", 2);

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        // Case insensitive should find
        var matchesInsensitive = await service.SearchTextAsync("searchabletoken_1", matchCase: false);
        Assert.NotEmpty(matchesInsensitive);

        // Case sensitive should NOT find lowercase query
        var matchesSensitive = await service.SearchTextAsync("searchabletoken_1", matchCase: true);
        Assert.Empty(matchesSensitive);
    }

    [Fact]
    public async Task TestPageRendering()
    {
        string pdfPath = CreateSamplePdf("render_test.pdf", 2);

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var bitmap = await service.RenderPageAsync(1, dpi: 150, rotationAngle: 0);
        Assert.NotNull(bitmap);
        Assert.True(bitmap.PixelWidth > 0);
        Assert.True(bitmap.PixelHeight > 0);
        Assert.True(bitmap.IsFrozen);
    }

    [Fact]
    public async Task TestPageRenderingAllRotations()
    {
        string pdfPath = CreateSamplePdf("render_rot_test.pdf", 1);

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        foreach (int angle in new[] { 0, 90, 180, 270 })
        {
            var bitmap = await service.RenderPageAsync(1, dpi: 150, rotationAngle: angle);
            Assert.NotNull(bitmap);
            Assert.True(bitmap.PixelWidth > 0);
            Assert.True(bitmap.PixelHeight > 0);
        }
    }

    [Fact]
    public async Task TestMultiPageRenderingBeyondFourPages()
    {
        string pdfPath = CreateSamplePdf("multipage_test.pdf", 10);

        using var service = new PdfiumDocumentService();
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

        using var service = new PdfiumDocumentService();
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

        using var service = new PdfiumDocumentService();
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
    public async Task TestCorruptedPdfHandlingSafely()
    {
        string corruptPath = Path.Combine(_testDir, "corrupt.pdf");
        TestPdfBuilder.CreateCorruptPdf(corruptPath);

        using var service = new PdfiumDocumentService();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await service.OpenDocumentAsync(corruptPath);
        });
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
        if (!File.Exists(samplePath))
        {
            TestPdfBuilder.CreateSimplePdf(samplePath, 5, "SampleFeature");
        }

        Assert.True(File.Exists(samplePath));
        Assert.True(new FileInfo(samplePath).Length > 0);
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
        var asm = typeof(PdfiumDocumentService).Assembly;
        var version = asm.GetName().Version;

        Assert.NotNull(version);
        Assert.True(version.Major >= 1);
        Assert.True(version.Build >= 0);
    }

    [Fact]
    public void TestAssemblyVersionMatchesLatestGitTagForSelfUpdate()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "describe --tags --abbrev=0")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        string tag = process!.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(tag))
        {
            return; // No tags reachable
        }

        var expected = UpdateService.ParseVersion(tag);
        var actual = typeof(PdfiumDocumentService).Assembly.GetName().Version;

        Assert.NotNull(actual);
        Assert.Equal(expected.Major, actual!.Major);
        Assert.Equal(expected.Minor, actual.Minor);
        Assert.Equal(expected.Build, actual.Build);
    }

    [Fact]
    public void TestVersionComparison()
    {
        Assert.Equal(new Version(1, 0, 12), UpdateService.ParseVersion("v1.0.12"));
        Assert.Equal(new Version(1, 0, 0), UpdateService.ParseVersion("v1.0.0"));
        Assert.Equal(new Version(2, 5, 0), UpdateService.ParseVersion("v2.5"));
        Assert.Equal(new Version(3, 0, 0), UpdateService.ParseVersion("3.0.0"));
        Assert.Equal(new Version(1, 0, 12), UpdateService.ParseVersion("v1.0.12+abc123"));
        Assert.Equal(new Version(0, 0, 0), UpdateService.ParseVersion(""));

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

        var infoSame = UpdateService.ParseReleaseJson(sampleJson, new Version(2, 0, 0));
        Assert.False(infoSame.IsUpdateAvailable);

        var infoNewer = UpdateService.ParseReleaseJson(sampleJson, new Version(3, 0, 0));
        Assert.False(infoNewer.IsUpdateAvailable);
    }

    [Fact]
    public async Task TestSearchHighlightCoordinatesAndNormalization()
    {
        string samplePdf = CreateSamplePdf("Highlight_Coordinates_Test.pdf");
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var matches = await service.SearchTextAsync("SearchableToken");
        Assert.NotEmpty(matches);

        foreach (var match in matches)
        {
            Assert.InRange(match.X, 0.0, 1.0);
            Assert.InRange(match.Y, 0.0, 1.0);
            Assert.InRange(match.Width, 0.001, 1.0);
            Assert.InRange(match.Height, 0.001, 1.0);
            Assert.Contains("SearchableToken", match.Text);
        }
    }

    [Fact]
    public async Task TestCompareSearchAndTextSegmentCoordinates()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        string samplePdf = "";
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "SampleDocument.pdf");
            if (File.Exists(candidate)) { samplePdf = candidate; break; }
            dir = dir.Parent;
        }
        if (string.IsNullOrEmpty(samplePdf)) samplePdf = CreateSamplePdf("Coord_Compare_Test.pdf", 1);

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var segments = await service.ExtractPageTextSegmentsAsync(1);
        var matches = await service.SearchTextAsync("Viewer");

        Assert.NotEmpty(matches);
        var match = matches[0];

        var matchingSegments = segments.Where(s => s.Text.Contains("Viewer")).ToList();
        Assert.NotEmpty(matchingSegments);
        var seg = matchingSegments[0];

        // Search Match and Segment should be closely aligned with proper optical padding
        double xDiff = Math.Abs(match.X - seg.X);
        double yDiff = Math.Abs(match.Y - seg.Y);
        Assert.True(xDiff < 0.005, $"X difference ({xDiff:F4}) exceeded tolerance");
        Assert.True(yDiff < 0.005, $"Y difference ({yDiff:F4}) exceeded tolerance");
        Assert.True(match.Height >= seg.Height * 0.9, "Match height should adequately cover text segment");
    }

    [Fact]
    public async Task TestSearchSelectionAndScrollNavigation()
    {
        string samplePdf = CreateSamplePdf("Search_Nav_Test.pdf", 3);
        var vm = new MainViewModel();

        int scrolledPage = 0;
        double scrolledNormX = -1;
        double scrolledNormY = -1;

        vm.ScrollToMatchAction = (page, x, y) =>
        {
            scrolledPage = page;
            scrolledNormX = x;
            scrolledNormY = y;
        };

        await vm.LoadDocumentAsync(samplePdf);
        vm.SearchQuery = "SearchableToken";
        await vm.ExecuteSearchCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.SearchMatches.Count);

        // When search finishes, it automatically navigates to match 0 (Page 1)
        Assert.Equal(1, scrolledPage);

        // Select match 1 (Page 2) via SelectedSearchMatch
        scrolledPage = 0;
        vm.SelectedSearchMatch = vm.SearchMatches[1];

        Assert.Equal(2, scrolledPage);
        Assert.Equal(vm.SearchMatches[1].X, scrolledNormX);
        Assert.Equal(vm.SearchMatches[1].Y, scrolledNormY);
        Assert.True(vm.SearchMatches[1].IsCurrentMatch);
        Assert.False(vm.SearchMatches[0].IsCurrentMatch);

        // Select match 2 (Page 3) via SelectSearchMatch command
        scrolledPage = 0;
        vm.SelectSearchMatch(vm.SearchMatches[2]);

        Assert.Equal(3, scrolledPage);
        Assert.Equal(vm.SearchMatches[2].X, scrolledNormX);
        Assert.Equal(vm.SearchMatches[2].Y, scrolledNormY);
        Assert.True(vm.SearchMatches[2].IsCurrentMatch);
    }

    [Fact]
    public async Task TestSaveAnnotatedEmbedded()
    {
        string samplePdf = CreateSamplePdf("Annotation_Embedding_Test.pdf");
        using var service = new PdfiumDocumentService();
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
        Assert.True(new FileInfo(targetPdf).Length > 100);

        // Load the saved PDF and verify annotations are present
        using var verifyService = new PdfiumDocumentService();
        await verifyService.OpenDocumentAsync(targetPdf);
        var loadedAnnots = verifyService.LoadExistingAnnotations();

        Assert.NotEmpty(loadedAnnots);
        Assert.Contains(loadedAnnots, a => a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Note);
    }

    [Fact]
    public async Task TestSaveAnnotatedFlattened()
    {
        string samplePdf = CreateSamplePdf("Annotation_Flattening_Test.pdf");
        using var service = new PdfiumDocumentService();
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
        using var verifyService = new PdfiumDocumentService();
        await verifyService.OpenDocumentAsync(targetPdf);
        var loadedAnnots = verifyService.LoadExistingAnnotations();

        Assert.Empty(loadedAnnots);
    }

    [Fact]
    public async Task TestExportAnnotationsToXfdf()
    {
        string samplePdf = CreateSamplePdf("XFDF_Export_Test.pdf");
        using var service = new PdfiumDocumentService();
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
        string samplePdf = CreateSamplePdf("Overwrite_Prevention_Test.pdf");
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var annotations = new List<AnnotationModel>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.SaveAnnotatedDocumentAsync(samplePdf, AnnotationSaveMode.Embedded, annotations, samplePdf);
        });
    }

    [Fact]
    public async Task TestExtractPageTextSegmentsAsync()
    {
        string samplePdf = CreateSamplePdf("TextSegmentsTest.pdf", 1);

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var segments = await service.ExtractPageTextSegmentsAsync(1);
        Assert.NotEmpty(segments);

        foreach (var seg in segments)
        {
            Assert.Equal(1, seg.PageNumber);
            Assert.False(string.IsNullOrWhiteSpace(seg.Text));
            Assert.InRange(seg.X, 0.0, 1.0);
            Assert.InRange(seg.Y, 0.0, 1.0);
            Assert.InRange(seg.Width, 0.0, 1.0);
            Assert.InRange(seg.Height, 0.0, 1.0);
        }

        string combinedText = string.Join(" ", segments.Select(s => s.Text));
        Assert.Contains("document", combinedText);
        Assert.Contains("Keyword", combinedText);
    }

    [Fact]
    public async Task TestPageViewModelSelectionAndExtraction()
    {
        string samplePdf = CreateSamplePdf("PageViewModelSelectionTest.pdf", 1);

        using var service = new PdfiumDocumentService();
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
        Assert.Contains("document", selectedAllText);

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
        string samplePdf = CreateSamplePdf("MainVmSelectionTest.pdf", 1);

        var mainVm = new MainViewModel();
        await mainVm.LoadDocumentAsync(samplePdf);

        var firstPage = mainVm.Pages[0];
        await firstPage.LoadTextSegmentsAsync(mainVm.DocumentService);

        firstPage.SelectAllText();
        mainVm.UpdateSelectionFromPages();

        Assert.True(mainVm.HasTextSelection);
        Assert.Contains("document", mainVm.SelectedText);
        string expectedText = mainVm.SelectedText;

        Assert.True(mainVm.HighlightSelectedTextCommand.CanExecute(null));
        mainVm.HighlightSelectedTextCommand.Execute(null);

        Assert.NotEmpty(mainVm.AllAnnotations);
        var annot = mainVm.AllAnnotations.Last();
        Assert.Equal(AnnotationType.Highlight, annot.Type);
        Assert.Equal(1, annot.PageNumber);
        Assert.Equal(expectedText, annot.Contents);

        Assert.False(mainVm.HasTextSelection);
        Assert.Equal(string.Empty, mainVm.SelectedText);
    }

    [Fact]
    public async Task TestPageViewModelReRendersOnDpiChange()
    {
        string pdfPath = CreateSamplePdf("dpi_change_test.pdf", 1);

        using var service = new PdfiumDocumentService();
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

        Assert.True(highDpiWidth > lowDpiWidth, "Higher DPI must re-render rather than reuse low-DPI bitmap.");

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
        var match = new SearchMatch { PageNumber = 1, Text = "foo" };

        var raisedProperties = new List<string>();
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
        string samplePdf = CreateSamplePdf("ThumbnailRotationTest.pdf", 1);

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
        string pdfPath = CreateSamplePdf("print_rotation_test.pdf", 1);

        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        var unrotated = new PdfiumPdfPaginator(service, 1, 1, 850, 1100, rotationAngle: 0);
        var rotated = new PdfiumPdfPaginator(service, 1, 1, 850, 1100, rotationAngle: 90);

        var rotationField = typeof(PdfiumPdfPaginator).GetField("_rotationAngle", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rotationField);
        Assert.Equal(0, (int)rotationField!.GetValue(unrotated)!);
        Assert.Equal(90, (int)rotationField.GetValue(rotated)!);

        var unrotatedPage = unrotated.GetPage(0);
        var rotatedPage = rotated.GetPage(0);

        Assert.NotNull(unrotatedPage);
        Assert.NotNull(rotatedPage);
    }

    [Fact]
    public async Task TestRapidDocumentOpeningAndClosing()
    {
        string pdfPath1 = CreateSamplePdf("rapid_1.pdf", 3);
        string pdfPath2 = CreateSamplePdf("rapid_2.pdf", 3);

        using var service = new PdfiumDocumentService();

        for (int i = 0; i < 10; i++)
        {
            await service.OpenDocumentAsync(pdfPath1);
            Assert.True(service.IsDocumentLoaded);
            var bmp = service.RenderPage(1, 100, 0);
            Assert.NotNull(bmp);

            await service.OpenDocumentAsync(pdfPath2);
            Assert.True(service.IsDocumentLoaded);
            var bmp2 = service.RenderPage(2, 100, 0);
            Assert.NotNull(bmp2);

            service.CloseDocument();
            Assert.False(service.IsDocumentLoaded);
        }
    }

    [Fact]
    public async Task TestRecentFilesServiceConcurrentAddsDoNotLoseEntries()
    {
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

    [Fact]
    public async Task TestPdfiumEncryptedDocumentRequiresPasswordAndOpens()
    {
        string encPath = FixtureGenerator.GetEncryptedPdfPath();
        Assert.True(File.Exists(encPath), $"Encrypted fixture not found at {encPath}");

        using var service = new PdfiumDocumentService();

        // 1. Opening without password must fail with UnauthorizedAccessException
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.OpenDocumentAsync(encPath);
        });

        // 2. Opening with wrong password must fail with UnauthorizedAccessException
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.OpenDocumentAsync(encPath, "wrongpassword");
        });

        // 3. Opening with correct password must succeed
        var meta = await service.OpenDocumentAsync(encPath, "userpass123");
        Assert.NotNull(meta);
        Assert.True(meta.IsEncrypted);
        Assert.Equal(1, meta.PageCount);
        Assert.True(service.IsDocumentLoaded);

        var matches = await service.SearchTextAsync("TopSecretData");
        Assert.NotEmpty(matches);
    }

    [Fact]
    public async Task TestPdfiumMultiThreadedRenderingSafety()
    {
        string pdfPath = CreateSamplePdf("multithread_test.pdf", 10);
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        // Render multiple pages simultaneously across threads
        var tasks = Enumerable.Range(1, 10).Select(p => Task.Run(() =>
        {
            var bmp = service.RenderPage(p, dpi: 150, rotationAngle: 0);
            Assert.NotNull(bmp);
            Assert.True(bmp.PixelWidth > 0);
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task TestPdfiumLarge500PageDocumentPerformance()
    {
        string pdfPath = Path.Combine(_testDir, "large_500_pages.pdf");
        TestPdfBuilder.CreateSimplePdf(pdfPath, 500, "LargeDocKeyword");

        using var service = new PdfiumDocumentService();
        var meta = await service.OpenDocumentAsync(pdfPath);

        Assert.Equal(500, meta.PageCount);
        Assert.Equal(500, service.PageCount);

        // Render page 1, 250, 500
        var bmp1 = await service.RenderPageAsync(1, dpi: 96);
        var bmp250 = await service.RenderPageAsync(250, dpi: 96);
        var bmp500 = await service.RenderPageAsync(500, dpi: 96);

        Assert.NotNull(bmp1);
        Assert.NotNull(bmp250);
        Assert.NotNull(bmp500);

        // Search for page 500 keyword
        var matches = await service.SearchTextAsync("LargeDocKeyword_500");
        Assert.Single(matches);
        Assert.Equal(500, matches[0].PageNumber);
    }

    [Fact]
    public async Task TestPdfiumArbitraryDpiScaling()
    {
        string pdfPath = CreateSamplePdf("dpi_scaling_test.pdf", 1);
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(pdfPath);

        int[] dpis = { 72, 96, 150, 300 };
        int prevWidth = 0;

        foreach (int dpi in dpis)
        {
            var bmp = await service.RenderPageAsync(1, dpi: dpi);
            Assert.NotNull(bmp);
            Assert.True(bmp.PixelWidth > prevWidth, $"DPI {dpi} width ({bmp.PixelWidth}) should be greater than previous ({prevWidth})");
            prevWidth = bmp.PixelWidth;
        }
    }

    [Fact]
    public async Task TestPdfiumInkAnnotationRoundtrip()
    {
        string samplePdf = CreateSamplePdf("Ink_Annotation_Test.pdf", 1);
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var annotations = new List<AnnotationModel>
        {
            new AnnotationModel
            {
                PageNumber = 1,
                Type = AnnotationType.Ink,
                X = 0.1,
                Y = 0.1,
                Width = 0.5,
                Height = 0.5,
                ColorHex = "#FF0000FF",
                InkPoints = new List<System.Windows.Point>
                {
                    new System.Windows.Point(0.1, 0.1),
                    new System.Windows.Point(0.2, 0.3),
                    new System.Windows.Point(0.4, 0.2),
                    new System.Windows.Point(0.5, 0.5)
                }
            }
        };

        string savedPdf = Path.Combine(_testDir, "SavedInk.pdf");
        await service.SaveAnnotatedDocumentAsync(savedPdf, AnnotationSaveMode.Embedded, annotations, samplePdf);

        using var verifyService = new PdfiumDocumentService();
        await verifyService.OpenDocumentAsync(savedPdf);
        var loadedAnnots = verifyService.LoadExistingAnnotations();

        Assert.NotEmpty(loadedAnnots);
        var loadedInk = loadedAnnots.FirstOrDefault(a => a.Type == AnnotationType.Ink);
        Assert.NotNull(loadedInk);
        Assert.NotNull(loadedInk.InkPoints);
        Assert.True(loadedInk.InkPoints.Count >= 4);
    }

    [Fact]
    public async Task TestPrintPreviewViewModelOperations()
    {
        string samplePdf = CreateSamplePdf("print_test.pdf", 5);
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(samplePdf);

        var vm = new PrintPreviewViewModel(service, 2);

        Assert.Equal(2, vm.PreviewPageNumber);
        Assert.Equal(5, vm.PreviewPageCount);
        Assert.Equal(PrintRangeMode.AllPages, vm.RangeMode);
        Assert.Equal(PrintOrientationMode.Auto, vm.Orientation);
        Assert.Equal(PrintColorMode.Color, vm.ColorMode);

        vm.NextPreviewPage();
        Assert.Equal(3, vm.PreviewPageNumber);

        vm.PrevPreviewPage();
        Assert.Equal(2, vm.PreviewPageNumber);

        vm.LastPreviewPage();
        Assert.Equal(5, vm.PreviewPageNumber);

        vm.FirstPreviewPage();
        Assert.Equal(1, vm.PreviewPageNumber);

        vm.Orientation = PrintOrientationMode.Landscape;
        Assert.Equal(PrintOrientationMode.Landscape, vm.Orientation);

        vm.ColorMode = PrintColorMode.Grayscale;
        Assert.Equal(PrintColorMode.Grayscale, vm.ColorMode);
    }
}

