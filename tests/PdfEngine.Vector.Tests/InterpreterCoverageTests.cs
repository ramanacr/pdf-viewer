using System;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Fail-first coverage for interpreter gaps found in the migration audit: every construct is either
/// rendered or classified — never silently dropped (06_PDFIUM_FALLBACK, 13_RISK "stop conditions").
/// </summary>
public class InterpreterCoverageTests
{
    private static async Task<IPdfDisplayList> BuildAsync(byte[] pdf, int page = 1, PdfSecurityLimits? limits = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, limits: limits);
        return await doc.GetPageDisplayListAsync(page);
    }

    internal static (byte B, byte G, byte R) Pixel(RenderedPage page, int x, int y)
    {
        var span = page.Pixels.Span;
        int i = y * page.Stride + x * 4;
        return (span[i], span[i + 1], span[i + 2]);
    }

    internal static async Task<RenderedPage> RenderAsync(IPdfDisplayList list, double dpi = 72, PageRotation rotation = PageRotation.Rotate0, IPdfFallbackProvider? provider = null)
    {
        using var renderer = new WindowsVectorRenderer();
        return await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = list.PageNumber, Dpi = dpi, Rotation = rotation }, provider);
    }

    [Fact]
    public async Task FormXObject_ContentIsInterpreted_WithMatrixAndPageSpaceBounds()
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Matrix [2 0 0 2 50 60]", "1 0 0 rg 0 0 10 10 re f");
        b.AddPage("/Fm1 Do", $"<< /XObject << /Fm1 {form} 0 R >> >>");

        var list = await BuildAsync(b.Build());

        var fill = Assert.Single(list.Commands.OfType<FillPath>());
        Assert.False(list.HasFallback);
        var bounds = Assert.IsType<PdfRect>(fill.Bounds);
        Assert.Equal(50, bounds.X, 3);
        Assert.Equal(60, bounds.Y, 3);
        Assert.Equal(20, bounds.Width, 3);

        using var page = await RenderAsync(list);
        var (bb, gg, rr) = Pixel(page, 60, 200 - 70);
        Assert.True(rr > 200 && gg < 50 && bb < 50, $"expected red inside the form, got {rr},{gg},{bb}");
    }

    [Fact]
    public async Task FormXObject_ThatPaintsItself_DoesNotRecurseForever()
    {
        var b = new VectorPdfBuilder();
        // Object 3 is this form: it references itself through its own resources.
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /Me 3 0 R >> >>", "0 0 5 5 re f /Me Do");
        Assert.Equal(3, form);
        b.AddPage("/Me Do", $"<< /XObject << /Me {form} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        Assert.Single(list.Commands.OfType<FillPath>());
    }

    [Fact]
    public async Task InvisibleText_Mode3_IsKeptForSelectionButNotPainted()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT 3 Tr /F1 48 Tf 10 80 Td (HIDDEN) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run;
        Assert.True(run.IsInvisible);
        Assert.Equal("HIDDEN", run.FullText);

        using var page = await RenderAsync(list);
        for (int x = 10; x < 190; x += 3)
        {
            var (bb, gg, rr) = Pixel(page, x, 200 - 95);
            Assert.True(rr > 250 && gg > 250 && bb > 250, $"invisible text painted at x={x}");
        }
    }

    [Fact]
    public async Task InlineImage_IsDecodedAndDrawnTopRowFirst()
    {
        // 1×2 RGB inline image: top row red, bottom row blue, scaled to 100×100 at (50,50).
        var b = new VectorPdfBuilder();
        b.AddPage("q 100 0 0 100 50 50 cm BI /W 1 /H 2 /CS /RGB /BPC 8 ID ÿ\u0000\u0000\u0000\u0000ÿ EI Q");

        var list = await BuildAsync(b.Build());
        Assert.Single(list.Commands.OfType<DrawImage>());
        Assert.False(list.HasFallback);

        using var page = await RenderAsync(list);
        var top = Pixel(page, 100, 200 - 140);    // y = 140 in PDF space: upper half
        var bottom = Pixel(page, 100, 200 - 60);  // y = 60: lower half
        Assert.True(top.R > 200 && top.B < 60, $"top should be red, got {top}");
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom should be blue, got {bottom}");
    }

    [Fact]
    public async Task ImageXObject_GrayAndStencilMask_Decode()
    {
        var b = new VectorPdfBuilder();
        int gray = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 0, 255 });
        // Stencil: 1 bpc, sample 0 paints with the fill colour.
        int mask = b.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 1", new byte[] { 0x00 }, flate: true);
        b.AddPage("q 100 0 0 50 0 150 cm /Im1 Do Q 0 1 0 rg q 50 0 0 50 0 0 cm /Mk Do Q",
            $"<< /XObject << /Im1 {gray} 0 R /Mk {mask} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        Assert.Equal(2, list.Commands.OfType<DrawImage>().Count());

        using var page = await RenderAsync(list);
        Assert.True(Pixel(page, 20, 25).R < 30, "left gray sample is black");
        Assert.True(Pixel(page, 80, 25).R > 225, "right gray sample is white");
        var m = Pixel(page, 25, 175);
        Assert.True(m.G > 200 && m.R < 60, $"stencil painted with fill colour, got {m}");
    }

    [Theory]
    [InlineData("/SMask << /Type /Mask /S /Luminosity /G 99 0 R >>", PdfFallbackReason.SoftMask)]
    [InlineData("/BM /Multiply", PdfFallbackReason.BlendMode)]
    public async Task UnsupportedTransparency_IsClassifiedWithObjectBounds(string gsEntries, PdfFallbackReason expected)
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add($"<< /Type /ExtGState {gsEntries} >>");
        b.AddPage("0 0 1 rg 10 10 30 30 re f /G1 gs 1 0 0 rg 100 100 20 20 re f", $"<< /ExtGState << /G1 {gs} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        var token = Assert.Single(list.FallbackTokens);
        Assert.Equal(expected, token.Reason);
        // Region fallback: only the affected object, not the page.
        Assert.InRange(token.Bounds.X, 97, 100);
        Assert.InRange(token.Bounds.Width, 20, 26);
        Assert.Single(list.Commands.OfType<FillPath>()); // the unaffected blue rectangle
        Assert.True(list.ComputeFallbackAreaRatio() < 0.05);
    }

    [Fact]
    public async Task TilingPattern_IsClassified_ShadingPattern_IsRendered()
    {
        var b = new VectorPdfBuilder();
        int tile = b.AddStream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>", "0 0 2 2 re f");
        int fn = b.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        int shading = b.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function {fn} 0 R /Extend [true true] >>");
        int shPattern = b.Add($"<< /Type /Pattern /PatternType 2 /Shading {shading} 0 R >>");
        b.AddPage("/Pattern cs /P1 scn 0 0 50 50 re f /Pattern cs /P2 scn 0 100 200 100 re f",
            $"<< /Pattern << /P1 {tile} 0 R /P2 {shPattern} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        var token = Assert.Single(list.FallbackTokens);
        Assert.Equal(PdfFallbackReason.Pattern, token.Reason);
        var sh = Assert.Single(list.Commands.OfType<DrawShading>());
        var axial = Assert.IsType<PdfAxialShading>(sh.Shading);
        Assert.NotNull(axial.Stops);

        using var page = await RenderAsync(list);
        var left = Pixel(page, 5, 50);
        var right = Pixel(page, 195, 50);
        Assert.True(left.R > 200 && left.B < 60, $"gradient starts red, got {left}");
        Assert.True(right.B > 200 && right.R < 60, $"gradient ends blue, got {right}");
    }

    [Fact]
    public async Task UnknownOperator_OutsideCompatibility_IsClassified_InsideBxEx_IsIgnored()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BX 1 2 frobnicate EX 0 0 10 10 re f");
        b.AddPage("1 2 frobnicate 0 0 10 10 re f");
        var bytes = b.Build();

        Assert.False((await BuildAsync(bytes, 1)).HasFallback);
        var second = await BuildAsync(bytes, 2);
        Assert.Equal(PdfFallbackReason.UnknownOperator, Assert.Single(second.FallbackTokens).Reason);
    }

    [Fact]
    public async Task EncryptedDocument_IsRefusedWithTypedError()
    {
        var b = new VectorPdfBuilder();
        int enc = b.Add("<< /Filter /Standard /V 2 /R 3 /O <00> /U <00> /P -4 >>");
        b.AddPage("0 0 10 10 re f");
        b.WithTrailer($"/Encrypt {enc} 0 R /ID [<01> <01>]");

        await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => await PdfVectorDocument.OpenAsync(b.Build()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DamagedCrossReference_IsReconstructed(bool corruptStartXref, bool omitXref)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 rg 10 10 50 50 re f");
        b.AddPage("0 0 1 rg 10 10 50 50 re f");

        using var doc = await PdfVectorDocument.OpenAsync(b.Build(corruptStartXref, omitXref));
        Assert.True(doc.WasRepaired);
        Assert.Equal(2, doc.PageCount);
        var list = await doc.GetPageDisplayListAsync(2);
        Assert.Single(list.Commands.OfType<FillPath>());
    }

    [Fact]
    public async Task XrefOffsetPointingAtAnotherObject_TriggersRebuildInsteadOfWrongObject()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 rg 10 10 50 50 re f");
        byte[] pdf = b.Build();
        // Swap the xref offsets of objects 2 and 3 (pages node and page content): a writer bug.
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        var lines = text.Split('\n');
        int xref = Array.IndexOf(lines, "xref");
        (lines[xref + 4], lines[xref + 5]) = (lines[xref + 5], lines[xref + 4]);
        pdf = System.Text.Encoding.Latin1.GetBytes(string.Join('\n', lines));

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.Equal(1, doc.PageCount);
        Assert.True(doc.WasRepaired);
        Assert.Single((await doc.GetPageDisplayListAsync(1)).Commands.OfType<FillPath>());
    }

    [Fact]
    public async Task OptionalContent_OffGroup_IsNotPainted()
    {
        var b = new VectorPdfBuilder();
        int ocg = b.Add("<< /Type /OCG /Name (Hidden layer) >>");
        b.WithCatalog($"/OCProperties << /OCGs [{ocg} 0 R] /D << /OFF [{ocg} 0 R] >> >>");
        b.AddPage("/OC /L1 BDC 1 0 0 rg 0 0 100 100 re f EMC 0 0 1 rg 100 100 50 50 re f",
            $"<< /Properties << /L1 {ocg} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        var fill = Assert.Single(list.Commands.OfType<FillPath>());
        Assert.Equal(0, fill.Paint.Color.R);
    }

    [Fact]
    public async Task AnnotationNormalAppearance_IsPainted()
    {
        var b = new VectorPdfBuilder();
        int ap = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", "0 1 0 rg 0 0 1 1 re f");
        int annot = b.Add($"<< /Type /Annot /Subtype /Square /Rect [20 20 60 60] /AP << /N {ap} 0 R >> >>");
        b.AddPage(string.Empty, extra: $"/Annots [{annot} 0 R]");

        var list = await BuildAsync(b.Build());
        var fill = Assert.Single(list.Commands.OfType<FillPath>());
        var bounds = Assert.IsType<PdfRect>(fill.Bounds);
        Assert.Equal(20, bounds.X, 3);
        Assert.Equal(40, bounds.Width, 3);
    }

    [Fact]
    public void PageBoxes_InheritOnlySpecifiedCropBox_AndClipAtTheLeaf()
    {
        // Found in the veraPDF corpus: an ancestor's defaulted crop box overrode the page's own
        // media box, and an inherited crop box was clipped by the ancestor's media box.
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 1 re f", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        string text = System.Text.Encoding.Latin1.GetString(pdf)
            .Replace("<< /Type /Pages /Kids", "<< /Type /Pages /MediaBox [0 0 200 200] /CropBox [0 0 300 300] /Kids");
        using var source = new PdfEngine.Vector.Parsing.MemoryByteSource(System.Text.Encoding.Latin1.GetBytes(text));
        using var doc = PdfVectorDocument.Open(source);
        var page = doc.PageTree.Pages[0];
        Assert.Equal(400, page.MediaBox.Width);
        Assert.Equal(300, page.CropBox.Width); // inherited /CropBox, clipped by the page's own media box

        var b2 = new VectorPdfBuilder();
        b2.AddPage("0 0 1 1 re f", mediaBox: "[0 0 595.3 841.9]");
        string text2 = System.Text.Encoding.Latin1.GetString(b2.Build())
            .Replace("<< /Type /Pages /Kids", "<< /Type /Pages /MediaBox [0 0 595 841] /Kids");
        using var source2 = new PdfEngine.Vector.Parsing.MemoryByteSource(System.Text.Encoding.Latin1.GetBytes(text2));
        using var doc2 = PdfVectorDocument.Open(source2);
        Assert.Equal(841.9, doc2.PageTree.Pages[0].CropBox.Height, 3); // no crop box anywhere: the page's media box
    }

    [Fact]
    public async Task IntrinsicRotate90_IsApplied()
    {
        var b = new VectorPdfBuilder();
        // Red square in the bottom-left of a 200×100 page. Rotated 90° clockwise it lands top-left.
        b.AddPage("1 0 0 rg 0 0 40 40 re f", mediaBox: "[0 0 200 100]", extra: "/Rotate 90");

        var list = await BuildAsync(b.Build());
        using var page = await RenderAsync(list);
        Assert.Equal(100, page.WidthPixels);
        Assert.Equal(200, page.HeightPixels);
        var topLeft = Pixel(page, 10, 10);
        var bottomRight = Pixel(page, 90, 190);
        Assert.True(topLeft.R > 200 && topLeft.G < 60, $"rotated square should be top-left, got {topLeft}");
        Assert.True(bottomRight.G > 200, "rest of page is white");
    }

    [Fact]
    public async Task CommandBounds_AreInPageSpace_UnderScaledCtm()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.01 0 0 0.01 10 10 cm 0 0 1000 2000 re f");
        var list = await BuildAsync(b.Build());
        var bounds = Assert.IsType<PdfRect>(Assert.Single(list.Commands.OfType<FillPath>()).Bounds);
        Assert.Equal(10, bounds.X, 3);
        Assert.Equal(10, bounds.Width, 3);
        Assert.Equal(20, bounds.Height, 3);
    }

    [Fact]
    public async Task HostileNesting_IsBounded()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(string.Concat(Enumerable.Repeat("q ", 20000)) + "0 0 10 10 re f" + string.Concat(Enumerable.Repeat(" Q", 20000)));
        var list = await BuildAsync(b.Build());
        Assert.True(list.Commands.OfType<SaveState>().Count() <= PdfSecurityLimits.Default.MaxGraphicsStateDepth);
        Assert.Equal(list.Commands.OfType<SaveState>().Count(), list.Commands.OfType<RestoreState>().Count());
    }

    [Fact]
    public async Task OperatorBudget_ProducesClassifiedResourceLimitFallback()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(string.Concat(Enumerable.Repeat("0 0 1 1 re f ", 5000)));
        var list = await BuildAsync(b.Build(), limits: new PdfSecurityLimits { MaxOperatorsPerPage = 1000 });
        Assert.Contains(list.FallbackTokens, t => t.Reason == PdfFallbackReason.ResourceLimit);
    }

    [Fact]
    public async Task TextClipMode_IsClassified()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT 7 Tr /F1 40 Tf 10 50 Td (CLIP) Tj ET 1 0 0 rg 0 0 200 200 re f", $"<< /Font << /F1 {font} 0 R >> >>");
        var list = await BuildAsync(b.Build());
        var token = Assert.Single(list.FallbackTokens);
        Assert.Equal(PdfFallbackReason.TextClipping, token.Reason);
        Assert.True(token.Bounds.Width < 150, "clip region is the text box, not the page");
    }

    [Fact]
    public async Task Type3Font_GlyphProceduresAreExecuted()
    {
        var b = new VectorPdfBuilder();
        int proc = b.AddStream(string.Empty, "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f");
        int font = b.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                         $"/CharProcs << /square {proc} 0 R >> /Encoding << /Type /Encoding /Differences [65 /square] >> " +
                         "/FirstChar 65 /LastChar 65 /Widths [1000] >>");
        b.AddPage("0 0 1 rg BT /T3 20 Tf 50 50 Td (AA) Tj ET", $"<< /Font << /T3 {font} 0 R >> >>");

        var list = await BuildAsync(b.Build());
        Assert.False(list.HasFallback);
        var fills = list.Commands.OfType<FillPath>().ToList();
        Assert.Equal(2, fills.Count);
        var second = Assert.IsType<PdfRect>(fills[1].Bounds);
        Assert.Equal(70, second.X, 2);    // advanced by one 20pt em
        Assert.Equal(20, second.Width, 2);
        Assert.Equal("AA", list.Commands.OfType<DrawGlyphRun>().Single().Run.FullText);
    }

    [Fact]
    public async Task RegionFallback_CompositesProviderPixelsOnlyInsideTheRegion()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /BM /Multiply >>");
        b.AddPage("1 0 0 rg 0 0 200 200 re f /G1 gs 0 0 0 rg 100 100 50 50 re f", $"<< /ExtGState << /G1 {gs} 0 R >> >>");
        var list = await BuildAsync(b.Build());

        using var page = await RenderAsync(list, provider: new SolidProvider(0, 0, 255));
        var inside = Pixel(page, 125, 200 - 125);
        var outside = Pixel(page, 20, 20);
        Assert.True(inside.B > 200 && inside.R < 50, $"fallback region shows provider pixels, got {inside}");
        Assert.True(outside.R > 200 && outside.B < 50, $"vector content outside the region, got {outside}");
    }

    [Fact]
    public async Task StrictVectorMode_ShowsPlaceholderInsteadOfSilentOutput()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /BM /Multiply >>");
        b.AddPage("/G1 gs 0 0 0 rg 100 100 50 50 re f", $"<< /ExtGState << /G1 {gs} 0 R >> >>");
        var list = await BuildAsync(b.Build());
        using var page = await RenderAsync(list);
        var inside = Pixel(page, 125, 200 - 125);
        Assert.False(inside.R < 30 && inside.G < 30 && inside.B < 30, "unsupported blend must not be drawn as if Normal");
    }

    [Fact]
    public async Task ConcurrentPageBuilds_AreSafeAndCached()
    {
        var b = new VectorPdfBuilder();
        for (int i = 0; i < 12; i++)
            b.AddPage($"{i / 12.0:0.00} 0 0 rg 0 0 {10 + i} 10 re f", flate: true);
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        var lists = await Task.WhenAll(Enumerable.Range(1, 12).Select(p => doc.GetPageDisplayListAsync(p).AsTask()));
        for (int i = 0; i < 12; i++)
        {
            var bounds = Assert.IsType<PdfRect>(lists[i].Commands.OfType<FillPath>().Single().Bounds);
            Assert.Equal(10 + i, bounds.Width, 3);
        }
        Assert.Same(lists[3], await doc.GetPageDisplayListAsync(4));
    }

    [Fact]
    public async Task DisplayListCache_IsBoundedByCommandBudget()
    {
        var b = new VectorPdfBuilder();
        for (int i = 0; i < 5; i++)
            b.AddPage(string.Concat(Enumerable.Repeat("0 0 1 1 re f ", 100)));
        using var doc = await PdfVectorDocument.OpenAsync(b.Build(), limits: new PdfSecurityLimits { MaxCachedDisplayListCommands = 150 });

        var first = await doc.GetPageDisplayListAsync(1);
        await doc.GetPageDisplayListAsync(2);
        await doc.GetPageDisplayListAsync(3);
        Assert.NotSame(first, await doc.GetPageDisplayListAsync(1)); // evicted and rebuilt
    }

    private sealed class SolidProvider : IPdfFallbackProvider
    {
        private readonly byte _r, _g, _b;
        public SolidProvider(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }

        public ValueTask<RenderedPage?> RenderFallbackRegionAsync(PdfFallbackToken token, double dpi, System.Threading.CancellationToken cancellationToken = default)
        {
            int w = Math.Max(1, (int)Math.Ceiling(token.Bounds.Width * dpi / 72));
            int h = Math.Max(1, (int)Math.Ceiling(token.Bounds.Height * dpi / 72));
            var owner = new ManagedMemoryOwner(w * h * 4);
            for (int i = 0; i < owner.Buffer.Length; i += 4)
            {
                owner.Buffer[i] = _b;
                owner.Buffer[i + 1] = _g;
                owner.Buffer[i + 2] = _r;
                owner.Buffer[i + 3] = 255;
            }
            return ValueTask.FromResult<RenderedPage?>(new RenderedPage(token.PageNumber, w, h, w * 4, dpi, PageRotation.Rotate0, owner));
        }
    }
}
