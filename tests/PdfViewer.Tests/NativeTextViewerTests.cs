using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// M9 native text in the viewer: in Auto mode search, word segments, selection layout and the
/// tagged reading order come from the vector engine's text page; the PDFium document service
/// (the PDFium-mode path) is the oracle and both give the same results. The developer build
/// compares with PDFium live; every build compares with what PDFium gave, committed as goldens
/// (<see cref="PdfiumGoldens"/>), which the developer build checks against PDFium.
/// </summary>
public class NativeTextViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PdfNativeText_" + Guid.NewGuid().ToString("N"));

    public NativeTextViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    // ------------------------------------------------------------------ what is compared

    public sealed record Hit(int Page, string Text, double X, double Y, double Width, double Height);
    public sealed record Word(string Text, double X, double Y);
    public sealed record Glyph(string Text, bool IsGenerated, bool IsHyphen, bool IsEmpty, double X, double Y);
    public sealed record PageText(List<Word> Words, List<Glyph> Glyphs);
    public sealed record TextResults(List<Hit> Search, List<Hit> SearchMatchCase, List<PageText> Pages);
    public sealed record Block(string Role, string Text, string? AltText);

    /// <summary>Search (either case rule), then each page's word segments (before any render) and selection layout.</summary>
    private static async Task<TextResults> Read(IPdfDocumentService service, int pages)
    {
        async Task<List<Hit>> Search(bool matchCase) =>
            (await service.SearchTextAsync("needle", matchCase)).Select(m => new Hit(m.PageNumber, m.Text, m.X, m.Y, m.Width, m.Height)).ToList();
        var search = await Search(false);
        var matchCase = await Search(true);
        var texts = new List<PageText>();
        for (int page = 1; page <= pages; page++)
        {
            var words = (await service.ExtractPageTextSegmentsAsync(page)).Select(w => new Word(w.Text, w.X, w.Y)).ToList();
            var glyphs = (await service.ExtractPageTextLayoutAsync(page)).Glyphs
                .Select(g => new Glyph(g.Text, g.IsGenerated, g.IsHyphen, g.Box.IsEmpty, g.Box.IsEmpty ? 0 : g.Box.X, g.Box.IsEmpty ? 0 : g.Box.Y)).ToList();
            texts.Add(new PageText(words, glyphs));
        }
        return new TextResults(search, matchCase, texts);
    }

    private static void Same(TextResults expected, TextResults actual)
    {
        foreach (var (e, a) in new[] { (expected.Search, actual.Search), (expected.SearchMatchCase, actual.SearchMatchCase) })
        {
            Assert.Equal(e.Count, a.Count);
            for (int i = 0; i < e.Count; i++)
            {
                Assert.Equal(e[i].Page, a[i].Page);
                Assert.Equal(e[i].Text, a[i].Text);
                Assert.Equal(e[i].X, a[i].X, 3);
                Assert.Equal(e[i].Y, a[i].Y, 3);
                Assert.Equal(e[i].Width, a[i].Width, 3);
                Assert.Equal(e[i].Height, a[i].Height, 3);
            }
        }
        Assert.Equal(expected.Pages.Count, actual.Pages.Count);
        for (int page = 0; page < expected.Pages.Count; page++)
        {
            var (words, nativeWords) = (expected.Pages[page].Words, actual.Pages[page].Words);
            Assert.Equal(words.Select(w => w.Text), nativeWords.Select(w => w.Text));
            for (int i = 0; i < words.Count; i++)
            {
                Assert.Equal(words[i].X, nativeWords[i].X, 3);
                Assert.Equal(words[i].Y, nativeWords[i].Y, 3);
            }
            var (layout, nativeLayout) = (expected.Pages[page].Glyphs, actual.Pages[page].Glyphs);
            Assert.Equal(layout.Select(g => (g.Text, g.IsGenerated, g.IsHyphen)), nativeLayout.Select(g => (g.Text, g.IsGenerated, g.IsHyphen)));
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout[i].IsEmpty || nativeLayout[i].IsEmpty) continue;
                Assert.Equal(layout[i].X, nativeLayout[i].X, 2);
                Assert.Equal(layout[i].Y, nativeLayout[i].Y, 2);
            }
        }
    }

    private static async Task<IPdfDocumentService> Native(string path)
    {
        var native = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await native.OpenDocumentAsync(path);
        Assert.True(native.IsVectorDocumentOpen);
        return native;
    }

    // ------------------------------------------------------------------ search, words and layout

    // Text is reported in the unrotated page's space, so PDFium gives the same for each /Rotate: one golden.
    private string SimplePdf(int rotation) => TestPdfBuilder.CreateSimplePdf(P($"simple{rotation}.pdf"), 2, "Needle", rotation);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task SearchSegmentsAndLayout_MatchWhatPdfiumGave(int rotation)
    {
        using var native = await Native(SimplePdf(rotation));
        Same(PdfiumGoldens.Load<TextResults>("NativeText_Simple"), await Read(native, 2));
    }

#if PDFIUM_RENDERER
    [Theory]
    [Trait("Category", PdfiumGoldens.Trait)]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task SearchSegmentsAndLayout_MatchThePdfiumService(int rotation)
    {
        string path = SimplePdf(rotation);
        TextResults expected;
        using (var pdfium = new PdfiumDocumentService())
        {
            await pdfium.OpenDocumentAsync(path);
            expected = await Read(pdfium, 2);
        }
        PdfiumGoldens.Verify("NativeText_Simple", expected);
        using var native = await Native(path);
        Same(expected, await Read(native, 2));
    }
#endif

    // ------------------------------------------------------------------ tagged reading order

    private string TaggedPdf()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string content =
            "/Artifact BMC BT /F1 8 Tf 20 190 Td (Running header) Tj ET EMC " +
            "/H1 <</MCID 0>> BDC BT /F1 22 Tf 20 165 Td (Main Title) Tj ET EMC " +
            "/P <</MCID 1>> BDC BT /F1 11 Tf 20 140 Td (First paragraph text.) Tj ET EMC " +
            "/Figure <</MCID 2>> BDC 0 0 1 rg 20 70 60 40 re f EMC " +
            "/P <</MCID 3>> BDC BT /F1 11 Tf 20 30 Td (Original words) Tj ET EMC";
        int page = b.AddPage(content, $"<< /Font << /F1 {font} 0 R >> >>", extra: "/StructParents 0");
        int root = page + 1, doc = page + 2, h1 = page + 3, p = page + 4, fig = page + 5, p2 = page + 6;
        b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R /ParentTree << /Nums [0 [{h1} 0 R {p} 0 R {fig} 0 R {p2} 0 R]] >> >>");
        b.Add($"<< /Type /StructElem /S /Document /P {root} 0 R /K [{h1} 0 R {p} 0 R {fig} 0 R {p2} 0 R] >>");
        b.Add($"<< /Type /StructElem /S /H1 /P {doc} 0 R /Pg {page} 0 R /K 0 >>");
        b.Add($"<< /Type /StructElem /S /P /P {doc} 0 R /Pg {page} 0 R /K 1 >>");
        b.Add($"<< /Type /StructElem /S /Figure /P {doc} 0 R /Pg {page} 0 R /Alt (A blue box) /K 2 >>");
        b.Add($"<< /Type /StructElem /S /P /P {doc} 0 R /Pg {page} 0 R /ActualText (Replacement words) /K 3 >>");
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R");
        string path = P("tagged.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<List<Block>> Blocks(IPdfDocumentService service) =>
        (await service.ExtractAccessibleContentAsync(1)).Blocks.Select(x => new Block(x.Role.ToString(), x.Text, x.AltText)).ToList();

    [Fact]
    public async Task TaggedReadingOrder_MatchesWhatPdfiumGave()
    {
        using var native = await Native(TaggedPdf());
        Assert.True((await native.ExtractAccessibleContentAsync(1)).FromTags);
        Assert.Equal(PdfiumGoldens.Load<List<Block>>("NativeText_Tagged"), await Blocks(native));
    }

#if PDFIUM_RENDERER
    [Fact]
    [Trait("Category", PdfiumGoldens.Trait)]
    public async Task TaggedReadingOrder_MatchesThePdfiumService()
    {
        string path = TaggedPdf();
        List<Block> expected;
        using (var pdfium = new PdfiumDocumentService())
        {
            await pdfium.OpenDocumentAsync(path);
            expected = await Blocks(pdfium);
        }
        PdfiumGoldens.Verify("NativeText_Tagged", expected);
        using var native = await Native(path);
        Assert.True((await native.ExtractAccessibleContentAsync(1)).FromTags);
        Assert.Equal(expected, await Blocks(native));
    }
#endif
}
