using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Ocr;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Searchable scans: recognized words added as an invisible text layer are found by text
/// extraction where they are on the page, the page looks exactly as before, any Unicode survives,
/// and rotated pages read in the right direction.
/// </summary>
public class TextLayerTests
{
    /// <summary>A "scan": one grey image covering a 612 x 792 page, no text.</summary>
    private static byte[] Scan(int rotate = 0)
    {
        var b = new VectorPdfBuilder();
        var pixels = new byte[64 * 64];
        new Random(3).NextBytes(pixels);
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceGray /BitsPerComponent 8", pixels, flate: true);
        b.AddPage("q 612 0 0 792 0 0 cm /Im0 Do Q", $"<< /XObject << /Im0 {image} 0 R >> >>", mediaBox: "[0 0 612 792]",
            extra: rotate != 0 ? $"/Rotate {rotate}" : string.Empty);
        return b.Build();
    }

    private static async Task<byte[]> AddLayer(byte[] pdf, params PdfRecognizedWord[] words)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfTextLayerWriter.Apply(doc, pdf, new Dictionary<int, IReadOnlyList<PdfRecognizedWord>> { [1] = words });
    }

    private static async Task<IReadOnlyList<PdfEngine.Text.TextSegment>> Words(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf);
        return await engine.TextService.ExtractTextSegmentsAsync(doc, 1);
    }

    [Fact]
    public async Task RecognizedWords_AreFoundWhereTheyAre()
    {
        byte[] pdf = Scan();
        byte[] searchable = await AddLayer(pdf,
            new PdfRecognizedWord("Invoice", 0.10, 0.10, 0.20, 0.03),
            new PdfRecognizedWord("Total:", 0.50, 0.80, 0.10, 0.02),
            new PdfRecognizedWord("$1,250.00", 0.62, 0.80, 0.16, 0.02));
        Assert.True(searchable.AsSpan(0, pdf.Length).SequenceEqual(pdf), "an incremental update");

        var words = await Words(searchable);
        void Near(string text, double x, double y, double w)
        {
            var seg = Assert.Single(words, s => s.Text == text);
            Assert.InRange(seg.X, x - 0.01, x + 0.01);
            Assert.InRange(seg.Y, y - 0.015, y + 0.015);
            Assert.InRange(seg.Width, w * 0.85, w * 1.15);
        }
        Near("Invoice", 0.10, 0.10, 0.20);
        Near("Total:", 0.50, 0.80, 0.10);
        Near("$1,250.00", 0.62, 0.80, 0.16);
    }

    [Fact]
    public async Task TheLayer_IsInvisible()
    {
        byte[] pdf = Scan();
        byte[] searchable = await AddLayer(pdf, new PdfRecognizedWord("Hidden", 0.1, 0.1, 0.5, 0.2));
        using var a = await Render(pdf);
        using var b = await Render(searchable);
        Assert.True(a.Pixels.Span.SequenceEqual(b.Pixels.Span), "the page renders exactly as before");
    }

    [Fact]
    public async Task AnyUnicode_Survives()
    {
        byte[] searchable = await AddLayer(Scan(),
            new PdfRecognizedWord("naïve café", 0.1, 0.1, 0.3, 0.03),
            new PdfRecognizedWord("日本語", 0.1, 0.2, 0.2, 0.03),
            new PdfRecognizedWord("😀ok", 0.1, 0.3, 0.2, 0.03));
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(searchable);
        string text = await engine.TextService.ExtractPageTextAsync(doc, 1);
        Assert.Contains("naïve café", text);
        Assert.Contains("日本語", text);
        Assert.Contains("😀ok", text);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task RotatedPages_ReadInTheirDisplayDirection(int rotate)
    {
        byte[] searchable = await AddLayer(Scan(rotate), new PdfRecognizedWord("Rotated", 0.2, 0.3, 0.3, 0.04));
        using var doc = await PdfVectorDocument.OpenAsync(searchable);
        var list = await doc.GetPageDisplayListAsync(1);
        var run = list.Commands.OfType<DrawGlyphRun>().Single();
        Assert.Equal("Rotated", string.Concat(run.Run.Glyphs.Select(g => g.Unicode)));
        // The run starts at the word's display-bottom-left corner, mapped back through /Rotate.
        var node = doc.PageTree.Pages[0];
        var expected = PdfTextLayerWriter.ToUser(0.2, 0.34, node.CropBox, rotate);
        var start = run.Run.TextToPage.Transform(0, 0);
        Assert.InRange(Math.Abs(start.X - expected.X) + Math.Abs(start.Y - expected.Y), 0, 8);
        // And it runs along the display's x axis: its direction in user space matches the page's rotation.
        var end = run.Run.TextToPage.Transform(run.Run.Glyphs.Sum(g => g.AdvanceX), 0);
        var expectedEnd = PdfTextLayerWriter.ToUser(0.5, 0.34, node.CropBox, rotate);
        Assert.InRange(Math.Abs(end.X - expectedEnd.X) + Math.Abs(end.Y - expectedEnd.Y), 0, 12);
    }

    [Fact]
    public async Task NoWords_LeavesTheFileAsItWas()
    {
        byte[] pdf = Scan();
        Assert.Same(pdf, await AddLayer(pdf));
    }

    private static async Task<RenderedPage> Render(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 36 }, null, CancellationToken.None);
        return result.Page;
    }
}
