using System;
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
/// (the PDFium-mode path) is the oracle and both give the same results.
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

    private static async Task<(HybridVectorDocumentService Native, PdfiumDocumentService Pdfium)> Open(string path)
    {
        var native = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await native.OpenDocumentAsync(path);
        var pdfium = new PdfiumDocumentService();
        await pdfium.OpenDocumentAsync(path);
        return (native, pdfium);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task SearchSegmentsAndLayout_MatchThePdfiumService(int rotation)
    {
        string path = TestPdfBuilder.CreateSimplePdf(P($"simple{rotation}.pdf"), 2, "Needle", rotation);
        var (native, pdfium) = await Open(path);
        using (native)
        using (pdfium)
        {
            Assert.True(native.IsVectorDocumentOpen);
            foreach (bool matchCase in new[] { false, true })
            {
                var expected = await pdfium.SearchTextAsync("needle", matchCase);
                var actual = await native.SearchTextAsync("needle", matchCase);
                Assert.Equal(expected.Count, actual.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].PageNumber, actual[i].PageNumber);
                    Assert.Equal(expected[i].Text, actual[i].Text);
                    Assert.Equal(expected[i].X, actual[i].X, 3);
                    Assert.Equal(expected[i].Y, actual[i].Y, 3);
                    Assert.Equal(expected[i].Width, actual[i].Width, 3);
                    Assert.Equal(expected[i].Height, actual[i].Height, 3);
                }
            }

            for (int page = 1; page <= 2; page++)
            {
                var words = await pdfium.ExtractPageTextSegmentsAsync(page);
                var nativeWords = await native.ExtractPageTextSegmentsAsync(page); // not rendered yet: word segments
                Assert.Equal(words.Select(w => w.Text), nativeWords.Select(w => w.Text));
                for (int i = 0; i < words.Count; i++)
                {
                    Assert.Equal(words[i].X, nativeWords[i].X, 3);
                    Assert.Equal(words[i].Y, nativeWords[i].Y, 3);
                }

                var layout = await pdfium.ExtractPageTextLayoutAsync(page);
                var nativeLayout = await native.ExtractPageTextLayoutAsync(page);
                Assert.Equal(layout.Glyphs.Select(g => (g.Text, g.IsGenerated, g.IsHyphen)), nativeLayout.Glyphs.Select(g => (g.Text, g.IsGenerated, g.IsHyphen)));
                for (int i = 0; i < layout.Glyphs.Count; i++)
                {
                    var e = layout.Glyphs[i].Box;
                    var a = nativeLayout.Glyphs[i].Box;
                    if (e.IsEmpty || a.IsEmpty) continue;
                    Assert.Equal(e.X, a.X, 2);
                    Assert.Equal(e.Y, a.Y, 2);
                }
            }
        }
    }

    [Fact]
    public async Task TaggedReadingOrder_MatchesThePdfiumService()
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

        var (native, pdfium) = await Open(path);
        using (native)
        using (pdfium)
        {
            var expected = await pdfium.ExtractAccessibleContentAsync(1);
            var actual = await native.ExtractAccessibleContentAsync(1);
            Assert.True(actual.FromTags);
            Assert.Equal(expected.Blocks.Select(x => (x.Role, x.Text, x.AltText)), actual.Blocks.Select(x => (x.Role, x.Text, x.AltText)));
        }
    }
}
