using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Printing;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Vector printing through Direct2D's print control: the page layout math on its own, and whole
/// jobs sent to the PDF and XPS writers with their output redirected to a file (never a
/// physical printer, never a file dialog).
/// </summary>
public class PrintingTests
{
    private readonly ITestOutputHelper _output;
    public PrintingTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ layout

    [Theory]
    [InlineData(0, 612, 792)]
    [InlineData(90, 792, 612)]
    [InlineData(180, 612, 792)]
    [InlineData(270, 792, 612)]
    [InlineData(-90, 792, 612)]
    [InlineData(450, 792, 612)]
    public void RotatedSize_SwapsSidesForQuarterTurns(int rotation, double width, double height)
    {
        Assert.Equal((width, height), PrintLayout.RotatedSize(612, 792, rotation));
    }

    [Fact]
    public void Fit_ScalesToTheLimitingSideAndCentres()
    {
        // A Letter page (8.5 × 11 in) fitted to a 4 × 4 in area: height limits, centred across.
        var area = new PrintRect(48, 48, 384, 384);
        var r = PrintLayout.Place(612, 792, area, PrintScaling.Fit);
        double scale = 384 / (792 * PrintLayout.DipsPerPoint);
        Assert.Equal(384, r.Height, 9);
        Assert.Equal(612 * PrintLayout.DipsPerPoint * scale, r.Width, 9);
        Assert.Equal(48, r.Y, 9);
        Assert.Equal(48 + (384 - r.Width) / 2, r.X, 9);
        Assert.Equal(area.X + area.Width / 2, r.X + r.Width / 2, 9);
    }

    [Fact]
    public void Fit_EnlargesSmallPages()
    {
        var area = new PrintRect(0, 0, 816, 1056); // Letter in DIPs
        var r = PrintLayout.Place(306, 396, area, PrintScaling.Fit); // half Letter
        Assert.Equal(new PrintRect(0, 0, 816, 1056), Round(r));
    }

    [Fact]
    public void ActualSize_KeepsPointsAsSeventySecondsOfAnInch()
    {
        var area = new PrintRect(24, 24, 768, 1008);
        var r = PrintLayout.Place(144, 72, area, PrintScaling.ActualSize); // 2 × 1 in
        Assert.Equal(192, r.Width, 9);
        Assert.Equal(96, r.Height, 9);
        Assert.Equal(24 + (768 - 192) / 2.0, r.X, 9);
        Assert.Equal(24 + (1008 - 96) / 2.0, r.Y, 9);
    }

    [Fact]
    public void ActualSize_OversizedPageIsCentredSoBothSidesClipEvenly()
    {
        var area = new PrintRect(0, 0, 816, 1056);
        var r = PrintLayout.Place(1224, 792, area, PrintScaling.ActualSize); // tabloid landscape
        Assert.Equal(1632, r.Width, 9);
        Assert.Equal(-408, r.X, 9);
        Assert.Equal(816 - r.Right, r.X, 9);
    }

    [Theory]
    [InlineData(50, 0.5)]
    [InlineData(200, 2.0)]
    [InlineData(0, 1.0)]   // an unusable percentage prints at actual size
    [InlineData(-10, 1.0)]
    public void Custom_ScalesByPercentage(double percent, double factor)
    {
        var area = new PrintRect(0, 0, 816, 1056);
        var r = PrintLayout.Place(612, 792, area, PrintScaling.Custom, percent);
        Assert.Equal(816 * factor, r.Width, 9);
        Assert.Equal(1056 * factor, r.Height, 9);
        Assert.Equal(408, r.X + r.Width / 2, 9);
        Assert.Equal(528, r.Y + r.Height / 2, 9);
    }

    [Fact]
    public void RotatedLandscapePage_FitsAPortraitArea()
    {
        // The viewer's landscape print turns the page a quarter: fit uses the turned size.
        var (w, h) = PrintLayout.RotatedSize(612, 792, 90);
        var r = PrintLayout.Place(w, h, new PrintRect(0, 0, 816, 1056), PrintScaling.Fit);
        Assert.Equal(816, r.Width, 9);
        Assert.Equal(816 * 612.0 / 792.0, r.Height, 9);
        Assert.Equal(528, r.Y + r.Height / 2, 9);
    }

    [Fact]
    public void Place_DegenerateInputsGiveAnEmptyRectangle()
    {
        var area = new PrintRect(10, 20, 100, 100);
        Assert.Equal(new PrintRect(10, 20, 0, 0), PrintLayout.Place(0, 100, area, PrintScaling.Fit));
        Assert.Equal(new PrintRect(10, 20, 0, 0), PrintLayout.Place(100, 100, area with { Width = 0 }, PrintScaling.Fit));
    }

    [Fact]
    public void PrintableArea_TurnsWithALandscapeSheet()
    {
        var portraitArea = new PrintRect(12, 18, 792, 1020); // margins 12 left/right, 18 top/bottom
        Assert.Equal(portraitArea, PrintLayout.OrientPrintableArea(816, 1056, portraitArea));
        var turned = PrintLayout.OrientPrintableArea(1056, 816, portraitArea);
        Assert.Equal(new PrintRect(18, 12, 1020, 792), turned);
        Assert.Equal(turned, PrintLayout.OrientPrintableArea(1056, 816, turned)); // already oriented
    }

    private static PrintRect Round(PrintRect r) =>
        new(Math.Round(r.X, 6), Math.Round(r.Y, 6), Math.Round(r.Width, 6), Math.Round(r.Height, 6));

    // ------------------------------------------------------------------ print control

    private const string PdfPrinter = "Microsoft Print to PDF";
    private const string XpsPrinter = "Microsoft XPS Document Writer";

    private static bool PrinterInstalled(string name)
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues().Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] DefaultTicket(string printer)
    {
        using var server = new LocalPrintServer();
        using var queue = server.GetPrintQueue(printer);
        using var stream = queue.UserPrintTicket?.GetXmlStream() ?? queue.DefaultPrintTicket.GetXmlStream();
        return stream.ToArray();
    }

    /// <summary>Three Letter pages, each with text in a substituted and an embedded font, filled and stroked paths and a transparent group.</summary>
    private static byte[] SourceDocument()
    {
        var b = new VectorPdfBuilder();
        byte[] times = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "times.ttf"));
        int stream = b.AddStream($"/Length1 {times.Length}", times, flate: true);
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /TimesNewRomanPSMT /Flags 34 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {stream} 0 R >>");
        int embedded = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /TimesNewRomanPSMT /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 /CA 0.5 >>");
        string resources = $"<< /Font << /F1 {helv} 0 R /F2 {embedded} 0 R >> /ExtGState << /G {gs} 0 R >> >>";
        for (int page = 1; page <= 3; page++)
        {
            b.AddPage(
                "0.2 0.4 0.8 rg 72 520 300 120 re f " +
                "1 0 0 RG 6 w 72 480 m 540 400 l S " +
                "/G gs 0 0.6 0 rg 300 560 200 150 re f " +
                $"BT /F1 28 Tf 72 700 Td (Vector print page {page}) Tj ET " +
                "BT /F2 18 Tf 72 300 Td (Embedded Times survives as text) Tj ET",
                resources, mediaBox: "[0 0 612 792]");
        }
        return b.Build();
    }

    /// <summary>Waits until the spooler has finished writing <paramref name="path"/> (the file can be opened exclusively and is complete).</summary>
    private static byte[] WaitForOutput(string path, Func<byte[], bool> complete, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (file.Length > 0)
                {
                    var bytes = new byte[file.Length];
                    file.ReadExactly(bytes);
                    if (complete(bytes)) return bytes;
                }
            }
            catch (IOException)
            {
                // still being written by the spooler
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException($"The print job did not finish writing {path} within {timeout.TotalSeconds:F0} s.");
    }

    /// <summary>Prints <paramref name="pages"/> on a thread of its own, as the viewer does; the kind of each page added.</summary>
    private static async Task<PrintedPageKind[]> PrintAsync(string printer, byte[] pdf, int[] pages, string outputPath)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var lists = new List<IPdfDisplayList>();
        foreach (int number in pages) lists.Add(await doc.GetPageDisplayListAsync(number));
        byte[] ticket = DefaultTicket(printer);
        var kinds = new List<PrintedPageKind>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var job = Direct2DPrintJob.Start(printer, "Vector print test", ticket, outputPath);
                foreach (var list in lists)
                {
                    var crop = list.CropBox;
                    // The page fills a sheet of its own size, so the output page matches the source page.
                    double w = crop.Width * PrintLayout.DipsPerPoint, h = crop.Height * PrintLayout.DipsPerPoint;
                    kinds.Add(job.AddPageAsync(list, PageRotation.Rotate0, new PrintRect(0, 0, w, h), w, h, provider: null).GetAwaiter().GetResult());
                }
                Assert.Equal(lists.Count, job.PageCount);
                job.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        return kinds.ToArray();
    }

    [Fact]
    public async Task PrintToPdf_WritesTheRequestedPagesAsVectorsWithText()
    {
        if (!PrinterInstalled(PdfPrinter))
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        byte[] source = SourceDocument();
        string path = Path.Combine(Path.GetTempPath(), $"vector-print-{Guid.NewGuid():N}.pdf");
        try
        {
            Assert.All(await PrintAsync(PdfPrinter, source, new[] { 1, 3 }, path), k => Assert.Equal(PrintedPageKind.Vector, k));
            byte[] printed = WaitForOutput(path, bytes => bytes.AsSpan(Math.Max(0, bytes.Length - 32)).IndexOf("%%EOF"u8) >= 0, TimeSpan.FromSeconds(90));
            _output.WriteLine($"printed PDF: {printed.Length} bytes");

            using var engine = new PdfiumEngine();
            await using var output = await engine.OpenDocumentAsync(printed);
            await using var original = await engine.OpenDocumentAsync(source);
            Assert.Equal(2, output.PageCount);

            foreach (var (outPage, srcPage) in new[] { (1, 1), (2, 3) })
            {
                string text = await engine.TextService.ExtractPageTextAsync(output, outPage);
                _output.WriteLine($"page {outPage} text: {text.Replace('\n', ' ').Replace('\r', ' ')}");
                Assert.Contains($"Vector print page {srcPage}", text);
                Assert.Contains("Embedded Times survives as text", text);

                using var printedPixels = await engine.Renderer.RenderPageAsync(output, new RenderRequest { PageNumber = outPage, Dpi = 72 });
                using var sourcePixels = await engine.Renderer.RenderPageAsync(original, new RenderRequest { PageNumber = srcPage, Dpi = 72 });
                var (mean, bad) = DifferentialRenderingTests.Compare(printedPixels, sourcePixels);
                _output.WriteLine($"page {outPage}: mean={mean:F2} bad={bad:P2}");
                Assert.True(mean <= 4.0, $"page {outPage}: mean {mean:F2}");
                Assert.True(bad <= 0.03, $"page {outPage}: {bad:P2} pixels off");
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PrintToXps_WritesOneFixedPagePerRequestedPage()
    {
        if (!PrinterInstalled(XpsPrinter))
        {
            _output.WriteLine($"{XpsPrinter} is not installed: skipped.");
            return;
        }
        byte[] source = SourceDocument();
        string path = Path.Combine(Path.GetTempPath(), $"vector-print-{Guid.NewGuid():N}.xps");
        try
        {
            Assert.All(await PrintAsync(XpsPrinter, source, new[] { 2, 3, 1 }, path), k => Assert.Equal(PrintedPageKind.Vector, k));
            Assert.Equal(3, CountXpsPages(path));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task BlendModePages_PrintAsImageAreasOrWholeImagesBesideVectorPages()
    {
        if (!PrinterInstalled(XpsPrinter))
        {
            _output.WriteLine($"{XpsPrinter} is not installed: skipped.");
            return;
        }
        var b = new VectorPdfBuilder();
        // A blend over a quarter of the page: only that area is an image.
        b.AddPage("0.2 0.6 0.9 rg 20 20 160 160 re f /G1 gs 0.9 0.5 0.2 rg 60 60 100 100 re f", "<< /ExtGState << /G1 << /BM /Multiply >> >> >>");
        b.AddPage("0 0 1 rg 20 20 100 100 re f");
        // A blend over almost all of it: the page is one image.
        b.AddPage("0.2 0.6 0.9 rg 0 0 200 200 re f /G1 gs 0.9 0.5 0.2 rg 5 5 190 190 re f", "<< /ExtGState << /G1 << /BM /Multiply >> >> >>");
        string path = Path.Combine(Path.GetTempPath(), $"vector-print-{Guid.NewGuid():N}.xps");
        try
        {
            var kinds = await PrintAsync(XpsPrinter, b.Build(), new[] { 1, 2, 3 }, path);
            Assert.Equal(new[] { PrintedPageKind.Mixed, PrintedPageKind.Vector, PrintedPageKind.Raster }, kinds);
            Assert.Equal(3, CountXpsPages(path));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // A blend and a non-isolated knockout group each print as an image of their area only: the
    // text before, over and away from them stays glyphs, and the output looks like the source.
    [Fact]
    public async Task BackdropGroupPages_KeepTheirTextAsGlyphsInXps()
    {
        if (!PrinterInstalled(XpsPrinter))
        {
            _output.WriteLine($"{XpsPrinter} is not installed: skipped.");
            return;
        }
        string path = Path.Combine(Path.GetTempPath(), $"vector-print-{Guid.NewGuid():N}.xps");
        try
        {
            var kinds = await PrintAsync(XpsPrinter, PrintBackdropTests.MultiplyPage(), new[] { 1 }, path);
            Assert.Equal(new[] { PrintedPageKind.Mixed }, kinds);
            var pages = ReadXpsPages(path);
            string page = Assert.Single(pages);
            // The writer keeps glyph indices without the text: one Glyphs element per run, a glyph per letter.
            var runs = System.Text.RegularExpressions.Regex.Matches(page, "<Glyphs [^>]*Indices=\"([^\"]*)\"")
                .Select(m => m.Groups[1].Value.Split(';').Length).ToArray();
            _output.WriteLine($"glyph runs: {string.Join(", ", runs)}");
            foreach (var text in new[] { "Before the blend", "After the blend", "Far away text" })
                Assert.Contains(text.Replace(" ", "").Length, runs); // spaces are advances, not glyphs
            Assert.Contains("ImageBrush", page); // the blended area
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task BackdropGroupPages_PrintToPdfWithTextAndTheSourceLook()
    {
        if (!PrinterInstalled(PdfPrinter))
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        var cases = new (byte[] Pdf, string[] Texts)[]
        {
            (PrintBackdropTests.MultiplyPage(), new[] { "Before the blend", "After the blend", "Far away text" }),
            (PrintBackdropTests.KnockoutPage(), new[] { "Knockout page", "Drawn after", "Far away text" }),
        };
        foreach (var (source, texts) in cases)
        {
            string path = Path.Combine(Path.GetTempPath(), $"vector-print-{Guid.NewGuid():N}.pdf");
            try
            {
                Assert.Equal(new[] { PrintedPageKind.Mixed }, await PrintAsync(PdfPrinter, source, new[] { 1 }, path));
                byte[] printed = WaitForOutput(path, bytes => bytes.AsSpan(Math.Max(0, bytes.Length - 32)).IndexOf("%%EOF"u8) >= 0, TimeSpan.FromSeconds(90));
                using var engine = new PdfiumEngine();
                await using var output = await engine.OpenDocumentAsync(printed);
                await using var original = await engine.OpenDocumentAsync(source);
                string text = await engine.TextService.ExtractPageTextAsync(output, 1);
                _output.WriteLine($"{texts[0]}: {text.Replace('\n', ' ').Replace('\r', ' ')}");
                foreach (var t in texts)
                    Assert.Equal(1, CountOf(text, t)); // once: nothing under the image is drawn again

                using var printedPixels = await engine.Renderer.RenderPageAsync(output, new RenderRequest { PageNumber = 1, Dpi = 72 });
                using var sourcePixels = await engine.Renderer.RenderPageAsync(original, new RenderRequest { PageNumber = 1, Dpi = 72 });
                var (mean, bad) = DifferentialRenderingTests.Compare(printedPixels, sourcePixels);
                _output.WriteLine($"{texts[0]}: mean={mean:F2} bad={bad:P2}");
                Assert.True(mean <= 4.0, $"{texts[0]}: mean {mean:F2}");
                Assert.True(bad <= 0.03, $"{texts[0]}: {bad:P2} pixels off");
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The markup of each fixed page in the XPS package the writer produced, once it is complete.</summary>
    private static List<string> ReadXpsPages(string path)
    {
        var pages = new List<string>();
        WaitForOutput(path, bytes =>
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                pages.Clear();
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase)))
                {
                    using var reader = new StreamReader(entry.Open());
                    pages.Add(reader.ReadToEnd());
                }
                return true;
            }
            catch (InvalidDataException)
            {
                return false; // central directory not written yet
            }
        }, TimeSpan.FromSeconds(90));
        return pages;
    }

    /// <summary>Fixed pages in the XPS package the writer produced, once it is complete.</summary>
    private static int CountXpsPages(string path)
    {
        int pages = 0;
        WaitForOutput(path, bytes =>
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                pages = zip.Entries.Count(e => e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase));
                return true;
            }
            catch (InvalidDataException)
            {
                return false; // central directory not written yet
            }
        }, TimeSpan.FromSeconds(90));
        return pages;
    }

    [Fact]
    public void Start_UnknownPrinterThrowsSoCallersCanFallBack()
    {
        Assert.ThrowsAny<Exception>(() => Direct2DPrintJob.Start("No Such Printer " + Guid.NewGuid().ToString("N"), "x", ReadOnlySpan<byte>.Empty));
    }
}
