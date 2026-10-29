using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Ocr;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using PdfEngine.Vector.Windows.Create;
using PdfViewer.Services;
using PdfViewer.Services.Scanning;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// File > Create PDF in the viewer: the menu and its commands, pictures to a new document opened
/// in a tab, a scanner (a fake one, with a feeder) to pages, text files without Office, files
/// combined with a bookmark each, recognition and PDF/A when asked, and a stop that leaves no file.
/// </summary>
public class CreatePdfViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CreatePdf_" + Guid.NewGuid().ToString("N"));
    public CreatePdfViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] Png(int width, int height, int seed = 1)
    {
        var pixels = new byte[width * height * 3];
        new Random(seed).NextBytes(pixels);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgr24, null, pixels, width * 3)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Jpeg(int width, int height)
    {
        var pixels = Enumerable.Range(0, width * height * 3).Select(i => (byte)(i * 7)).ToArray();
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private string File(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    private string TextFile(string name, string text) => File(name, System.Text.Encoding.UTF8.GetBytes(text));

    private string Pdf(string name, string text)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage($"BT /F1 14 Tf 40 700 Td ({text}) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        return File(name, b.Build());
    }

    private static MainViewModel NewViewModel() => new() { ShowMessageBoxAction = (_, _, _, _) => { }, OfficeConverterOverride = new NoOffice() };

    private static PdfVectorDocument Open(string path) => PdfVectorDocument.OpenAsync(path).AsTask().GetAwaiter().GetResult();

    /// <summary>Office as if it were not installed: text files use our own layout.</summary>
    private sealed class NoOffice : IOfficeConverter
    {
        private readonly OfficeConverter _real = new();
        public OfficeApp? AppFor(string extension) => _real.AppFor(extension);
        public bool IsInstalled(OfficeApp app) => false;
        public Task ConvertAsync(string source, string targetPdf, CancellationToken cancellationToken = default) =>
            throw new OfficeConversionException("not installed");
    }

    /// <summary>A recognizer that reads a fixed word on every page it is given, and can stop the run.</summary>
    private sealed class FakeOcr : IOcrEngine
    {
        public List<int> Pages { get; } = new();
        public Action<int>? Before { get; init; }
        public string EngineName => "fake";
        public IReadOnlyList<string> SupportedLanguages => new[] { "en" };
        public ValueTask<OcrPageResult> RecognizePageAsync(IPdfDocument document, int pageNumber, string language = "en", CancellationToken ct = default)
        {
            Pages.Add(pageNumber);
            Before?.Invoke(pageNumber);
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new OcrPageResult
            {
                PageNumber = pageNumber,
                UsedOpticalRecognition = true,
                Words = new[] { new OcrWord { Text = $"Recognized{pageNumber}", Bounds = new PdfRect(0.1, 0.1, 0.5, 0.1) } },
            });
        }
    }

    /// <summary>A scanner with a flatbed and a duplex feeder holding <see cref="SheetsInFeeder"/> sheets.</summary>
    private sealed class FakeScanner : IScannerService
    {
        public List<ScanSettings> Scans { get; } = new();
        public int SheetsInFeeder { get; set; } = 3;
        public IReadOnlyList<ScannerDevice> Devices { get; init; } = new[] { new ScannerDevice("fake-1", "Fake Scanner", true, true, true) };
        public Exception? Fail { get; init; }

        public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Fail != null ? Task.FromException<IReadOnlyList<ScannerDevice>>(Fail) : Task.FromResult(Devices);

        public Task<ScannerDevice> GetCapabilitiesAsync(ScannerDevice device, CancellationToken cancellationToken = default) =>
            Task.FromResult(device.CapabilitiesKnown ? device : device with { HasDuplex = false, CapabilitiesKnown = true });

        public Task<int> ScanAsync(ScanSettings settings, Action<ScannedPage> onPage, CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            Scans.Add(settings);
            int pages = settings.Source == ScanSource.Feeder ? SheetsInFeeder * (settings.Duplex ? 2 : 1) : 1;
            if (pages == 0) throw new ScannerUnavailableException("The document feeder is empty. Put the pages in the feeder and try again.");
            for (int i = 0; i < pages; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // An 8.5 x 11 inch page at a tenth of the resolution asked for, as BMP (what many drivers deliver).
                int w = settings.Dpi * 85 / 100, h = settings.Dpi * 110 / 100;
                var pixels = new byte[w * h * 3];
                Array.Fill(pixels, (byte)(200 + i));
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, settings.Dpi / 10.0, settings.Dpi / 10.0, PixelFormats.Bgr24, null, pixels, w * 3)));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                onPage(new ScannedPage(ms.ToArray()));
            }
            SheetsInFeeder = 0;
            return pages;
        }, cancellationToken);
    }

    // ------------------------------------------------------------------ the menu

    private static XDocument MainWindowXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "PdfViewer", "Views", "MainWindow.xaml"));
    }

    [Fact]
    public void TheFileMenu_HasCreatePdf_WithItsFourCommands_BoundToTheViewModel()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var file = MainWindowXaml().Descendants(wpf + "MenuItem").First(m => (string?)m.Attribute("Header") == "_File");
        var create = file.Elements(wpf + "MenuItem").Single(m => (string?)m.Attribute("Header") == "Create P_DF");
        var commands = create.Elements(wpf + "MenuItem").Select(m => (string?)m.Attribute("Command")).ToList();
        Assert.Equal(new[]
        {
            "{Binding CreatePdfFromImagesCommand}", "{Binding CreatePdfFromScannerCommand}", "{Binding CreatePdfFromFileCommand}", "{Binding CombineFilesCommand}",
        }, commands);
        foreach (var name in new[] { "CreatePdfFromImagesCommand", "CreatePdfFromScannerCommand", "CreatePdfFromFileCommand", "CombineFilesCommand" })
            Assert.NotNull(typeof(MainViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void TheCommands_OpenTheirWindows_WithNoDocumentOpen()
    {
        using var vm = NewViewModel();
        vm.ScannerServiceOverride = new FakeScanner();
        CreatePdfViewModel? shown = null;
        ScanViewModel? scan = null;
        vm.ShowCreatePdfAction = v => shown = v;
        vm.ShowScanAction = v => scan = v;

        Assert.False(vm.IsDocumentLoaded);
        vm.CreatePdfFromImagesCommand.Execute(null);
        Assert.Equal(CreatePdfMode.Images, shown!.Mode);
        vm.CombineFilesCommand.Execute(null);
        Assert.Equal(CreatePdfMode.Combine, shown!.Mode);
        vm.CreatePdfFromScannerCommand.Execute(null);
        Assert.NotNull(scan);
        scan!.Dispose();
    }

    [Fact]
    public async Task TheWindows_LoadTheirXaml_AndBindToTheirViewModels()
    {
        string png = File("w.png", Png(8, 8));
        await StaTask(() =>
        {
            using var vm = NewViewModel();
            vm.ScannerServiceOverride = new FakeScanner();
            var create = vm.CreateCreatePdfViewModel(CreatePdfMode.Combine);
            create.AddFiles(new[] { png });
            var window = new PdfViewer.Views.Dialogs.CreatePdfWindow(create) { Left = -10000, ShowActivated = false, ShowInTaskbar = false };
            Assert.Equal("Combine Files", window.Title);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            var list = (System.Windows.Controls.ListBox)window.FindName("FileList");
            Assert.Single(list.Items);
            window.Close();
            using var scanVm = vm.CreateScanViewModel();
            var scan = new PdfViewer.Views.Dialogs.ScanWindow(scanVm) { Left = -10000, ShowActivated = false, ShowInTaskbar = false };
            Assert.Same(scanVm, scan.DataContext);
            scan.Close();
            return true;
        });
    }

    // ------------------------------------------------------------------ from images

    [Fact]
    public async Task Pictures_BecomeANewDocument_OpenedInANewTab()
    {
        string jpg = File("photo.jpg", Jpeg(48, 32)), png = File("chart.png", Png(30, 20));
        string target = Path.Combine(_dir, "album.pdf");
        using var shell = new ShellViewModel();
        var first = shell.ActiveDocument!;
        await shell.OpenDocumentAsync(Pdf("existing.pdf", "Already open"));
        Assert.Single(shell.Documents);

        first.ShowMessageBoxAction = (_, _, _, _) => { };
        first.ChooseCreateTargetFunc = suggested =>
        {
            Assert.Equal(Path.Combine(_dir, "photo.pdf"), suggested);
            return target;
        };
        CreatePdfViewModel? create = null;
        first.ShowCreatePdfAction = v => create = v;
        first.CreatePdfFromImagesCommand.Execute(null);
        Assert.Empty(create!.AddFiles(new[] { jpg, png }));
        Assert.Equal(new[] { "notes.txt" }, create.AddFiles(new[] { Path.Combine(_dir, "notes.txt"), jpg }));
        create.Remove(create.Items[^1]); // the second photo.jpg
        create.Move(1, 0);               // chart first
        Assert.Equal(new[] { "chart.png", "photo.jpg" }, create.Items.Select(i => i.Name));
        create.Move(1, 0);

        Assert.True(await create.CreateAndOpenAsync(), create.Status);
        Assert.Contains("2 page(s)", create.Status);
        Assert.Contains("1 JPEG(s) kept", create.Status);
        Assert.Equal(2, shell.Documents.Count);
        Assert.Equal(target, shell.ActiveDocument!.Metadata!.FilePath);
        Assert.Equal(2, shell.ActiveDocument.PageCount);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        using var doc = Open(target);
        Assert.Equal("album", PdfDocumentInfo.ReadProperties(doc).Title);
        Assert.StartsWith("PDF Viewer Native", PdfDocumentInfo.ReadProperties(doc).Producer);
    }

    [Fact]
    public async Task MakeSearchable_AddsRecognizedText_AndPdfA_ConvertsTheResult()
    {
        string png = File("scan.png", Png(60, 80));
        var ocr = new FakeOcr();
        using var vm = NewViewModel();
        vm.OcrEngineOverride = ocr;
        var create = vm.CreateCreatePdfViewModel(CreatePdfMode.Images);
        Assert.True(create.Options.IsOcrAvailable);
        create.AddFiles(new[] { png, png });
        create.Options.MakeSearchable = true;
        create.Options.ConvertToPdfA = true;
        string target = Path.Combine(_dir, "searchable.pdf");
        Assert.True(await create.CreateAsync(target), create.Status);
        Assert.Equal(new[] { 1, 2 }, ocr.Pages);
        Assert.Contains("text recognized on 2 page(s)", create.Status);

        using var doc = Open(target);
        Assert.Contains("Recognized2", PdfTextExtractor.LoadPage(doc, 2).Text);
        Assert.Equal(PdfAFlavor.A2b, PdfAValidator.ClaimedFlavor(doc));
        Assert.True(PdfAValidator.Validate(doc, System.IO.File.ReadAllBytes(target), PdfAFlavor.A2b).IsCompliant);
    }

    [Fact]
    public async Task Stopping_LeavesNoFile()
    {
        string png = File("a.png", Png(20, 20));
        using var vm = NewViewModel();
        CreatePdfViewModel create = null!;
        vm.OcrEngineOverride = new FakeOcr { Before = page => { if (page == 2) create.Cancel(); } };
        create = vm.CreateCreatePdfViewModel(CreatePdfMode.Images);
        create.AddFiles(new[] { png, png, png });
        create.Options.MakeSearchable = true;
        string target = Path.Combine(_dir, "stopped.pdf");
        Assert.False(await create.CreateAsync(target));
        Assert.Contains("Stopped", create.Status);
        Assert.False(System.IO.File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(create.IsBusy);
    }

    [Fact]
    public async Task ASourceInTheList_IsNeverOverwritten()
    {
        string png = File("keep.png", Png(10, 10));
        using var owner = NewViewModel();
        var create = owner.CreateCreatePdfViewModel(CreatePdfMode.Combine);
        string pdf = Pdf("keep.pdf", "Original");
        create.AddFiles(new[] { pdf, png });
        byte[] before = System.IO.File.ReadAllBytes(pdf);
        Assert.False(await create.CreateAsync(pdf));
        Assert.Equal(before, System.IO.File.ReadAllBytes(pdf));
    }

    [Fact]
    public async Task WindowsOcr_MakesARenderedPhrase_Searchable()
    {
        if (!WindowsOcrEngine.IsAvailable) return; // no OCR language installed on this machine: nothing to check
        byte[] png = await StaTask(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1700, 600));
                var text = new FormattedText("INVOICE NUMBER 4821", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Arial"), 96, Brushes.Black, 1.0);
                dc.DrawText(text, new Point(80, 220));
            }
            var bitmap = new RenderTargetBitmap(1700, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
        var service = new CreatePdfService { Ocr = new WindowsOcrEngine(VectorPdfRenderer.Shared), Office = new NoOffice() };
        string target = Path.Combine(_dir, "ocr.pdf");
        var report = await service.FromImagesAsync(new[] { ImageInput.FromBytes("invoice.png", png) }, target, new CreatePdfSettings { MakeSearchable = true });
        Assert.Equal(1, report.RecognizedPages);
        using var doc = Open(target);
        string text = PdfTextExtractor.LoadPage(doc, 1).Text;
        Assert.Contains("INVOICE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4821", text);
    }

    private static Task<T> StaTask<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); } catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // ------------------------------------------------------------------ from a scanner

    [Fact]
    public async Task AFeederScan_GivesEveryPage_BothSides_AndANewDocument()
    {
        var scanner = new FakeScanner { SheetsInFeeder = 3 };
        using var vm = NewViewModel();
        vm.ScannerServiceOverride = scanner;
        string target = Path.Combine(_dir, "scanned.pdf");
        vm.ChooseCreateTargetFunc = _ => target;
        using var scan = vm.CreateScanViewModel();
        await scan.LoadDevicesAsync();
        Assert.Equal("Fake Scanner", scan.SelectedDevice!.Name);
        Assert.True(scan.CanUseFeeder);
        scan.UseFeeder = true;
        Assert.True(scan.CanDuplex);
        scan.Duplex = true;
        scan.Dpi = 200;
        scan.ColorMode = ScanColorMode.Grayscale;

        Assert.Equal(6, await scan.ScanAsync());
        Assert.Equal(new ScanSettings("fake-1", ScanColorMode.Grayscale, 200, ScanSource.Feeder, true), scanner.Scans.Single());
        // The feeder is now empty: the scanner says so, and the pages already scanned stay.
        Assert.Equal(0, await scan.ScanAsync());
        Assert.Contains("feeder is empty", scan.Status);
        // One more from the glass.
        scan.UseFeeder = false;
        Assert.Equal(1, await scan.ScanAsync());
        Assert.Equal(7, scan.PageCount);

        Assert.True(await scan.CreateAndOpenAsync(), scan.Status);
        Assert.True(vm.IsDocumentLoaded);
        Assert.Equal(7, vm.PageCount);
        using var doc = Open(target);
        // 170 x 220 pixels at 20 dpi: 8.5 x 11 inches.
        Assert.Equal(612, doc.PageTree.Pages[0].MediaBox.Width, 0); // BMP keeps whole pixels per metre
        Assert.Equal(792, doc.PageTree.Pages[0].MediaBox.Height, 0);
        Assert.Equal(0, scan.PageCount); // the scanned pages are deleted once in the PDF
    }

    [Fact]
    public async Task NoScanner_OrNoScanningService_IsSaidPlainly()
    {
        using var vm = NewViewModel();
        vm.ScannerServiceOverride = new FakeScanner { Devices = Array.Empty<ScannerDevice>() };
        using (var none = vm.CreateScanViewModel())
        {
            await none.LoadDevicesAsync();
            Assert.Contains("No scanner was found", none.Status);
            Assert.False(none.CanScan);
        }
        vm.ScannerServiceOverride = new FakeScanner { Fail = new ScannerUnavailableException("The Windows Image Acquisition service is not running.") };
        using var stopped = vm.CreateScanViewModel();
        await stopped.LoadDevicesAsync();
        Assert.Contains("not running", stopped.Status);
    }

    [Fact]
    public async Task WhatAScannerCanDo_IsReadFromIt_AfterListing()
    {
        using var vm = NewViewModel();
        vm.ScannerServiceOverride = new FakeScanner { Devices = new[] { new ScannerDevice("net-1", "Network Scanner", true, true, true, CapabilitiesKnown: false) } };
        using var scan = vm.CreateScanViewModel();
        await scan.LoadDevicesAsync();
        // The fake answers at once: a flatbed with a feeder, one-sided only.
        Assert.True(scan.SelectedDevice!.CapabilitiesKnown);
        scan.UseFeeder = true;
        Assert.True(scan.CanUseFeeder);
        Assert.False(scan.CanDuplex);
    }

    [Fact]
    public async Task TheWiaService_ListsDevicesWithoutFailing()
    {
        // Whatever this machine has (often no scanner at all): a list, or a plain message.
        if (!WiaScannerService.IsAvailable) return;
        try
        {
            var devices = await new WiaScannerService().GetDevicesAsync();
            Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id)));
        }
        catch (ScannerUnavailableException ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
    }

    // ------------------------------------------------------------------ from a file

    [Fact]
    public async Task ATextFile_WithoutOffice_IsLaidOutAndExtractable()
    {
        string txt = TextFile("minutes.txt", "Minutes of the board meeting\n\nAttendees: Ana, Bo\tand Chen\n");
        string target = Path.Combine(_dir, "minutes.pdf");
        using var vm = NewViewModel();
        vm.PickCreateSourceFunc = () => txt;
        vm.ChooseCreateTargetFunc = suggested =>
        {
            Assert.Equal(target, suggested);
            return suggested;
        };
        await vm.CreatePdfFromFileAsync();
        Assert.True(vm.IsDocumentLoaded, vm.StatusText);
        using var doc = Open(target);
        string text = PdfTextExtractor.LoadPage(doc, 1).Text;
        Assert.Contains("Minutes of the board meeting", text);
        Assert.Contains("Attendees: Ana, Bo", text);
        Assert.Equal("minutes", PdfDocumentInfo.ReadProperties(doc).Title);
    }

    [Fact]
    public async Task AWordFile_WithoutOffice_SaysOfficeIsNeeded()
    {
        string docx = File("letter.docx", new byte[] { 0x50, 0x4B, 3, 4 });
        string? alert = null;
        using var vm = NewViewModel();
        vm.ShowMessageBoxAction = (message, _, _, _) => alert = message;
        vm.PickCreateSourceFunc = () => docx;
        vm.ChooseCreateTargetFunc = s => s;
        await vm.CreatePdfFromFileAsync();
        Assert.Contains("needs Microsoft Word", alert);
        Assert.False(System.IO.File.Exists(Path.Combine(_dir, "letter.pdf")));
        Assert.False(vm.IsDocumentLoaded);
    }

    // ------------------------------------------------------------------ combine

    [Fact]
    public async Task CombineFiles_PutsPdfsPicturesAndTextTogether_WithABookmarkEach()
    {
        string report = Pdf("Report.pdf", "Quarterly report");
        string photo = File("Site photo.jpg", Jpeg(40, 30));
        string notes = TextFile("Notes.txt", "Follow-up actions");
        using var vm = NewViewModel();
        var create = vm.CreateCreatePdfViewModel(CreatePdfMode.Combine);
        Assert.Empty(create.AddFiles(new[] { report, photo, notes }));
        Assert.Equal(new[] { "PDF", "Picture", "Text" }, create.Items.Select(i => i.Kind));
        Assert.Equal(Path.Combine(_dir, "Combined.pdf"), create.SuggestedPath);
        string target = Path.Combine(_dir, "Binder.pdf");
        Assert.True(await create.CreateAsync(target), create.Status);

        using var doc = Open(target);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(new[] { "Report", "Site photo", "Notes" }, PdfDocumentInfo.ReadOutline(doc).Select(o => o.Title));
        Assert.Contains("Quarterly report", PdfTextExtractor.LoadPage(doc, 1).Text);
        Assert.Contains("Follow-up actions", PdfTextExtractor.LoadPage(doc, 3).Text);
        Assert.Equal("Binder", PdfDocumentInfo.ReadProperties(doc).Title);
    }

    [Fact]
    public async Task CombineFiles_AsksForTheirPassword_WhenASourceIsProtected()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 14 Tf 40 700 Td (Locked text) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
        string locked = File("Locked.pdf", b.Build(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "open-sesame", "owner")));
        string? asked = null;
        using var vm = NewViewModel();
        vm.RequestPasswordFunc = name => { asked = name; return Task.FromResult<string?>("open-sesame"); };
        var create = vm.CreateCreatePdfViewModel(CreatePdfMode.Combine);
        create.AddFiles(new[] { locked, File("p.png", Png(10, 10)) });
        string target = Path.Combine(_dir, "unlocked.pdf");
        Assert.True(await create.CreateAsync(target), create.Status);
        Assert.Equal("Locked.pdf", asked);
        using var doc = Open(target);
        Assert.Equal(2, doc.PageCount);
        Assert.False(doc.IsEncrypted);
    }

    // ------------------------------------------------------------------ Office, when installed (opt-in: it starts Office)

    public static IEnumerable<object[]> OfficeFiles() => new[] { new object[] { ".docx" }, new object[] { ".xlsx" }, new object[] { ".pptx" } };

    [Theory]
    [MemberData(nameof(OfficeFiles))]
    public async Task OfficeFiles_ConvertThroughOffice_WhenInstalled(string extension)
    {
        if (Environment.GetEnvironmentVariable("PDFVIEWER_OFFICE_SMOKE") != "1") return;
        var office = new OfficeConverter();
        var app = office.AppFor(extension)!.Value;
        if (!office.IsInstalled(app)) return;

        // The Office file is made by our own exporter from a synthetic PDF.
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 18 Tf 72 700 Td (Smoke test heading) Tj 0 -30 Td (Second line of text) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        using var source = await PdfVectorDocument.OpenAsync(b.Build());
        string input = Path.Combine(_dir, "smoke" + extension);
        await using (var stream = System.IO.File.Create(input))
        {
            if (extension == ".docx") await DocxExporter.ExportAsync(source, stream);
            else if (extension == ".xlsx") await XlsxExporter.ExportAsync(source, stream);
            else await PptxExporter.ExportAsync(source, stream);
        }
        string target = Path.Combine(_dir, "smoke" + extension + ".pdf");
        var service = new CreatePdfService { Office = office };
        var report = await service.FromFileAsync(input, target, new CreatePdfSettings());
        Assert.True(report.Pages >= 1);
        using var doc = Open(target);
        string text = string.Concat(Enumerable.Range(1, doc.PageCount).Select(p => PdfTextExtractor.LoadPage(doc, p).Text));
        Assert.Contains("Smoke", text);
    }
}
