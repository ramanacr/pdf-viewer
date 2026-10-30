using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// A text highlight drawn from its appearance stream, whose graphics state sets /BM /Multiply
/// and an alpha (as Acrobat and the engine's own writer make them): the text under it stays
/// dark and readable, by day and in night mode, in the Skia backend (web viewer, Android) and in
/// the Direct2D backend (desktop viewer). The page is built here, never taken from a file.
/// </summary>
public class HighlightMultiplyTests
{
    private const double Dpi = 144; // 2 pixels per point

    /// <summary>
    /// A 200 x 100 page with a black bar ("ink" of a text line) from x 20 to 180, y 40 to 50, and
    /// a pink highlight over x 10 to 190, y 30 to 60, drawn by its /AP with Multiply and alpha 0.6.
    /// </summary>
    private static byte[] HighlightedPage()
    {
        var b = new VectorPdfBuilder();
        int ap = b.AddStream(
            "/Type /XObject /Subtype /Form /BBox [0 0 180 30] " +
            "/Resources << /ExtGState << /GS0 << /BM /Multiply /ca 0.6 /CA 0.6 >> >> >>",
            "/GS0 gs 0.9569 0.7647 0.7961 rg 0 0 180 30 re f");
        int annot = b.Add(
            "<< /Type /Annot /Subtype /Highlight /Rect [10 30 190 60] /F 4 /C [0.9569 0.7647 0.7961] " +
            "/QuadPoints [10 60 190 60 10 30 190 30] " +
            $"/AP << /N {ap} 0 R >> >>");
        b.AddPage("0 g 20 40 160 10 re f", mediaBox: "[0 0 200 100]", extra: $"/Annots [{annot} 0 R]");
        return b.Build();
    }

    private static (byte B, byte G, byte R) Pixel(RenderedPage page, double xPoints, double yPoints)
    {
        // Page y is up; pixel rows go down.
        int x = (int)(xPoints * Dpi / 72), y = (int)((100 - yPoints) * Dpi / 72);
        var px = page.Pixels.Span;
        int i = y * page.Stride + x * 4;
        return (px[i], px[i + 1], px[i + 2]);
    }

    private static void AssertDayPage(RenderedPage page, string backend)
    {
        var ink = Pixel(page, 100, 45);
        Assert.True(ink.R < 40 && ink.G < 40 && ink.B < 40, $"{backend}: text under the highlight is hidden: {ink}");

        // Multiply over white is the highlight colour at 60%: 1 - 0.6 * (1 - c).
        var tint = Pixel(page, 100, 55);
        Assert.InRange(tint.R, 240, 255);
        Assert.InRange(tint.G, 200, 222);
        Assert.InRange(tint.B, 208, 230);

        var paper = Pixel(page, 100, 80);
        Assert.Equal((255, 255, 255), (paper.R, paper.G, paper.B));
    }

    private static void AssertNightPage(RenderedPage page, string backend)
    {
        // The inverted day page: the ink is light and the highlight a dark tint.
        var ink = Pixel(page, 100, 45);
        Assert.True(ink.R > 215 && ink.G > 215 && ink.B > 215, $"{backend}: text under the highlight is hidden at night: {ink}");
        var tint = Pixel(page, 100, 55);
        Assert.True(tint.R < 16 && tint.G > 24 && tint.B > 20, $"{backend}: the highlight is not shown at night: {tint}");
        var paper = Pixel(page, 100, 80);
        Assert.Equal((0, 0, 0), (paper.R, paper.G, paper.B));
    }

    [Fact]
    public async Task Skia_HighlightKeepsTextReadable_ByDayAndAtNight()
    {
        using var doc = await PdfVectorDocument.OpenAsync(HighlightedPage());
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = Dpi };
        using var day = (await Parity.Skia.RenderAsync(list, request, null, null, false, CancellationToken.None)).Page;
        AssertDayPage(day, "Skia");
        using var night = (await Parity.Skia.RenderAsync(list, request, null, null, true, CancellationToken.None)).Page;
        AssertNightPage(night, "Skia");
    }

    [Fact]
    public async Task Direct2D_HighlightKeepsTextReadable_ByDayAndAtNight()
    {
        using var doc = await PdfVectorDocument.OpenAsync(HighlightedPage());
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = Dpi };
        using var day = (await Parity.D2D.RenderAsync(list, request, null, null, false, CancellationToken.None)).Page;
        AssertDayPage(day, "Direct2D");
        using var night = (await Parity.D2D.RenderAsync(list, request, null, null, true, CancellationToken.None)).Page;
        AssertNightPage(night, "Direct2D");
    }
}
