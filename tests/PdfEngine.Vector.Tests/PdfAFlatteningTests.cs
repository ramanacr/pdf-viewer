using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Transparency flattening for PDF/A-1b: what is transparent is drawn by the engine into images
/// over the smallest areas it covers, everything else stays vector (text drawn above the
/// transparency is drawn above the image again), text in a flattened transparent object stays
/// searchable as invisible text, and the result is valid PDF/A-1b that looks as the original did.
/// </summary>
public class PdfAFlatteningTests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    private readonly ITestOutputHelper _output;

    public PdfAFlatteningTests(ITestOutputHelper output) => _output = output;

    /// <summary>PDFium for pages the vector engine cannot draw by itself (as the viewer does).</summary>
    internal static PdfRasterImage? PdfiumPage(byte[] bytes, int page, int width, int height, PdfPixelWindow window) => Task.Run(async () =>
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(bytes, null);
        using var rendered = await engine.Renderer.RenderPageAsync(doc, new RenderRequest
        {
            PageNumber = page, TargetWidthPixels = width, TargetHeightPixels = height, RenderAnnotations = false, RenderForms = false,
        });
        return new PdfRasterImage(window.Width, window.Height, PdfEngine.Vector.Direct2D.Direct2DPageRasterizer.Crop(rendered, window.X, window.Y, window.Width, window.Height), "PDFium");
    }).GetAwaiter().GetResult();

    private static async Task<PdfAConversionResult> Convert(byte[] pdf, PdfFlatteningOptions? flattening = null)
    {
        using var rasterizer = new Direct2DPageRasterizer(PdfiumPage);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavor = PdfAFlavor.A1b, Rasterizer = rasterizer, Flattening = flattening ?? new() });
    }

    /// <summary>The flattening alone (before fonts are embedded), to compare pixels with the original.</summary>
    private static async Task<PdfFlatteningResult> Flatten(byte[] pdf)
    {
        using var rasterizer = new Direct2DPageRasterizer(PdfiumPage);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return TransparencyFlattener.Run(doc, rasterizer, new PdfFlatteningOptions()) ?? throw new InvalidOperationException("Nothing was flattened.");
    }

    private static async Task<RenderedPage> Vector(byte[] pdf, double dpi, int page = 1)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(page);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = page, Dpi = dpi }, null, CancellationToken.None);
        return result.Page;
    }

    private static async Task<RenderedPage> Pdfium(byte[] pdf, double dpi, int page = 1, bool annotations = false)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = page, Dpi = dpi, RenderAnnotations = annotations, RenderForms = annotations });
    }

    private static async Task<string> Text(byte[] pdf, int page = 1)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, page);
    }

    private static async Task<string> PageContent(byte[] pdf, int page = 1)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var decoder = new PdfStreamDecoder(null, doc.Resolver.Resolve);
        return string.Concat(doc.PageTree.Pages[page - 1].Contents.Select(s => Encoding.Latin1.GetString(decoder.DecodeStream(s)) + "\n"));
    }

    /// <summary>The flattened page looks as the original did: drawn by the engine, and by PDFium, before and after.</summary>
    private async Task AssertLooksTheSame(string name, byte[] original, byte[] flattened, double maxMean = 1.5, double maxBad = 0.006, bool pdfium = true)
    {
        using (var before = await Vector(original, 144))
        using (var after = await Vector(flattened, 144))
        {
            var (mean, bad) = DifferentialRenderingTests.Compare(after, before);
            _output.WriteLine($"{name} engine: mean={mean:F3} bad={bad:P3}");
            Assert.True(mean <= maxMean && bad <= maxBad, $"{name}: the engine draws the flattened page differently (mean {mean:F2}, {bad:P2} of pixels)");
        }
        if (!pdfium) return;
        using (var before = await Pdfium(original, 144))
        using (var after = await Pdfium(flattened, 144))
        {
            // PDFium draws the original's text in its own Helvetica, the image has the engine's.
            var (mean, bad) = DifferentialRenderingTests.Compare(after, before);
            _output.WriteLine($"{name} PDFium: mean={mean:F3} bad={bad:P3}");
            Assert.True(mean <= maxMean * 2 && bad <= maxBad * 3, $"{name}: PDFium draws the flattened page differently (mean {mean:F2}, {bad:P2} of pixels)");
        }
    }

    private static async Task AssertValidPdfA1b(PdfAConversionResult result)
    {
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var again = PdfAValidator.Validate(doc, result.Bytes, PdfAFlavor.A1b);
        Assert.Equal(PdfAFlavor.A1b, again.Claimed);
        Assert.True(again.IsCompliant, string.Join("\n", again.Issues));
        string text = Encoding.Latin1.GetString(result.Bytes);
        Assert.DoesNotContain("/SMask", text);
        Assert.DoesNotContain("/Transparency", text);
        Assert.DoesNotContain("/Multiply", text);
        Assert.NotNull(result.Flattening);
    }

    /// <summary>Every image in the file, with whether it has a soft mask.</summary>
    private static async Task<List<(int Width, int Height, bool SoftMask)>> Images(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = new List<(int, int, bool)>();
        foreach (var (number, entry) in doc.XrefTable.Entries)
            if (entry.IsInUse && doc.Resolver.Resolve(number) is PdfStream { Dictionary: var d } && d.GetName("Subtype") == "Image")
                list.Add(((int)(d.GetInteger("Width") ?? 0), (int)(d.GetInteger("Height") ?? 0), d.ContainsKey("SMask")));
        return list;
    }

    private static byte[] SoftMaskedImageOverText()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        var rgb = new byte[16 * 16 * 3];
        var gray = new byte[16 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = y * 16 + x;
                rgb[i * 3] = (byte)(x * 16); rgb[i * 3 + 1] = 40; rgb[i * 3 + 2] = (byte)(255 - y * 16);
                gray[i] = (byte)(x * 255 / 15); // fades in from left to right
            }
        int mask = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8", gray);
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R", rgb);
        b.AddPage("BT /F1 16 Tf 0 0 0 rg 24 104 Td (Under the mask) Tj ET\n" +
                  "BT /F1 12 Tf 0.1 0.2 0.6 rg 20 20 Td (Plain words) Tj ET\n" +
                  "q 160 0 0 60 20 80 cm /Im1 Do Q",
            $"<< /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >>");
        return b.Build();
    }

    [Fact]
    public async Task ASoftMaskedImageOverText_IsFlattenedInItsArea_AndTheTextStaysSearchable()
    {
        byte[] pdf = SoftMaskedImageOverText();
        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);

        var page = Assert.Single(result.Flattening!.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.False(page.WholePage);
        var region = Assert.Single(page.Regions);
        // The image's area (20..180 × 80..140), a point or two around it, and nothing else.
        Assert.InRange(region.Bounds.X, 17, 20);
        Assert.InRange(region.Bounds.Y, 77, 80);
        Assert.InRange(region.Bounds.Right, 180, 183);
        Assert.InRange(region.Bounds.Bottom, 140, 143);
        Assert.True(region.ContainsText);
        Assert.Equal(600, region.Dpi);
        Assert.Contains(result.Changes, c => c.StartsWith("Flattened transparency on page(s) 1", StringComparison.Ordinal));

        string text = await Text(result.Bytes);
        Assert.Contains("Under the mask", text);
        Assert.Contains("Plain words", text);
        // The soft-masked image and its mask are gone; only the flattened image is left.
        var images = await Images(result.Bytes);
        var flat = Assert.Single(images);
        Assert.False(flat.SoftMask);
        Assert.Equal((region.WidthPixels, region.HeightPixels), (flat.Width, flat.Height));

        await AssertLooksTheSame("soft mask", pdf, (await Flatten(pdf)).Bytes);
    }

    [Fact]
    public async Task AHalfTransparentRectangle_OverVectorArt_KeepsWhatIsAboveItAsVectors()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 /CA 0.5 >>");
        b.AddPage("0 0.6 0 rg 30 30 80 80 re f 0 0 1 RG 3 w 20 170 m 180 170 l S\n" +
                  "q /GS1 gs 1 0 0 rg 60 50 80 70 re f Q\n" +
                  "BT /F1 14 Tf 0 0 0 rg 70 90 Td (Above it) Tj ET\n" +
                  "0.5 0 0.5 rg 150 150 30 30 re f",
            $"<< /Font << /F1 {font} 0 R >> /ExtGState << /GS1 {gs} 0 R >> >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        var region = Assert.Single(Assert.Single(result.Flattening!.Pages).Regions);
        Assert.InRange(region.Bounds.X, 57, 60);
        Assert.InRange(region.Bounds.Right, 140, 143);
        // The text is drawn after the rectangle: it is not in the image (no text resolution) ...
        Assert.False(region.ContainsText);
        Assert.Equal(300, region.Dpi);
        Assert.Contains("Above it", await Text(result.Bytes));

        // ... but drawn again, as text, over it.
        var flattened = await Flatten(pdf);
        string content = await PageContent(flattened.Bytes);
        int image = content.IndexOf("/FlIm1 Do", StringComparison.Ordinal);
        int above = content.IndexOf(Shown("Above it"), StringComparison.Ordinal);
        Assert.True(image > 0 && above > image, "the text above the transparency is drawn after the image");
        Assert.Equal(1, CountOccurrences(content, Shown("Above it"))); // once, so it is extracted once
        Assert.DoesNotContain("60 50 80 70 re\nf", content); // the transparent rectangle is only in the image
        await AssertLooksTheSame("alpha", pdf, flattened.Bytes);
    }

    [Fact]
    public async Task ALuminositySoftMask_InAGraphicsState_IsFlattened_AndLooksTheSame()
    {
        var b = new VectorPdfBuilder();
        // A mask that fades from left to right (a shading in the mask group), over a red square
        // drawn on a grey background; the region is away from the page's corner.
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>" +
            " /Resources << /Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceGray /Coords [60 0 160 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Extend [true true] >> >> >>",
            "/Sh1 sh");
        int gs = b.Add($"<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >>");
        b.AddPage("0.6 g 0 0 200 200 re f q /GS1 gs 1 0 0 rg 60 60 100 100 re f Q 0 0 1 rg 10 10 20 20 re f",
            $"<< /ExtGState << /GS1 {gs} 0 R >> >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        var region = Assert.Single(Assert.Single(result.Flattening!.Pages).Regions);
        Assert.InRange(region.Bounds.X, 57, 60);
        Assert.InRange(region.Bounds.Y, 57, 60);
        await AssertLooksTheSame("luminosity mask", pdf, (await Flatten(pdf)).Bytes);
    }

    [Fact]
    public async Task AMultiplyBlend_IsFlattened_AndLooksTheSame()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /BM /Multiply >>");
        b.AddPage("0 0.7 1 rg 20 20 100 100 re f q /GS1 gs 1 1 0 rg 70 70 100 100 re f Q 0 0 0 RG 1 w 10 190 m 190 190 l S",
            $"<< /ExtGState << /GS1 {gs} 0 R >> >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        var region = Assert.Single(Assert.Single(result.Flattening!.Pages).Regions);
        Assert.InRange(region.Bounds.X, 67, 70);
        Assert.InRange(region.Bounds.Y, 67, 70);
        await AssertLooksTheSame("multiply", pdf, (await Flatten(pdf)).Bytes);
    }

    [Fact]
    public async Task ATransparencyGroupDrawnWithAlpha_IsFlattened_AndAnOpaqueGroupOnlyLosesItsGroup()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 >>");
        // Two overlapping squares: as a group at half alpha, the overlap is not darker (it is composited once).
        int group = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /I true /K false >>",
            "1 0 0 rg 0 0 60 60 re f 0 0 1 rg 30 30 60 60 re f");
        int opaque = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 40 40] /Group << /S /Transparency /CS /DeviceRGB >>",
            "0.9 0.5 0 rg 0 0 40 40 re f");
        b.AddPage("0 0.5 0 rg 10 10 180 30 re f q /GS1 gs 1 0 0 1 20 60 cm /Fm1 Do Q q 1 0 0 1 150 5 cm /Fm2 Do Q",
            $"<< /ExtGState << /GS1 {gs} 0 R >> /XObject << /Fm1 {group} 0 R /Fm2 {opaque} 0 R >> >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        var region = Assert.Single(Assert.Single(result.Flattening!.Pages).Regions);
        // The group's box (20..120 × 60..160); the opaque group (150..190 × 5..45) stays vector.
        Assert.InRange(region.Bounds.X, 17, 20);
        Assert.InRange(region.Bounds.Bottom, 160, 163);
        Assert.False(region.Bounds.IntersectsWith(new PdfRect(150, 5, 40, 40)));
        Assert.True(result.Flattening.GroupsRemoved >= 1);
        await AssertLooksTheSame("group", pdf, (await Flatten(pdf)).Bytes);
    }

    [Fact]
    public async Task APageGroup_OverOpaqueContent_IsRemovedWithoutAnyImage()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        b.AddPage("0.2 0.4 0.8 rg 20 20 160 60 re f BT /F1 14 Tf 0 0 0 rg 20 120 Td (Page group) Tj ET",
            $"<< /Font << /F1 {font} 0 R >> >>", extra: "/Group << /S /Transparency /CS /DeviceRGB /I true >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.Empty(result.Flattening!.Pages);
        Assert.Equal(1, result.Flattening.PagesCleanedOnly);
        Assert.Equal(1, result.Flattening.GroupsRemoved);
        Assert.Empty(await Images(result.Bytes));
        Assert.Contains("Page group", await Text(result.Bytes));
        await AssertLooksTheSame("page group", pdf, (await Flatten(pdf)).Bytes, maxMean: 0.2, maxBad: 0.001);
    }

    [Fact]
    public async Task APageGroup_WithTransparentContent_IsFlattened()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /ca 0.4 >>");
        b.AddPage("0 0 1 rg 20 20 100 100 re f /GS1 gs 1 0 0 rg 60 60 100 100 re f",
            $"<< /ExtGState << /GS1 {gs} 0 R >> >>", extra: "/Group << /S /Transparency /CS /DeviceRGB /I true /K false >>");
        byte[] pdf = b.Build();
        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.Single(result.Flattening!.Pages);
        await AssertLooksTheSame("page group with alpha", pdf, (await Flatten(pdf)).Bytes);
    }

    [Fact]
    public async Task TransparentText_StaysSearchable_AsInvisibleTextInPlace()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int gs = b.Add("<< /Type /ExtGState /ca 0.4 >>");
        b.AddPage("BT /F1 18 Tf 0 0 0 rg 20 140 Td (Seen clearly) Tj ET\n" +
                  "q /GS1 gs BT /F1 18 Tf 0.8 0 0 rg 20 60 Td (Faded words) Tj ET Q",
            $"<< /Font << /F1 {font} 0 R >> /ExtGState << /GS1 {gs} 0 R >> >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.True(result.Flattening!.InvisibleTextOperators >= 1);
        var region = Assert.Single(Assert.Single(result.Flattening.Pages).Regions);
        Assert.True(region.ContainsText);
        Assert.True(region.Bounds.Bottom < 140, "the opaque line above stays vector");
        string text = await Text(result.Bytes);
        Assert.Contains("Faded words", text);
        Assert.Contains("Seen clearly", text);

        var flattened = await Flatten(pdf);
        string content = await PageContent(flattened.Bytes);
        Assert.Contains("3 Tr " + Shown("Faded words") + " 0 Tr", content);
        await AssertLooksTheSame("faded text", pdf, flattened.Bytes);

        // The invisible text is where the faded text was.
        using var doc = await PdfVectorDocument.OpenAsync(flattened.Bytes);
        var list = await doc.GetPageDisplayListAsync(1);
        var run = list.Commands.OfType<DrawGlyphRun>().First(g => g.Run.IsInvisible);
        Assert.InRange(run.Bounds!.Value.X, 19, 21);
        Assert.InRange(run.Bounds!.Value.Y, 50, 64);
    }

    [Fact]
    public async Task TransparencyOverMostOfThePage_FlattensTheWholePage_AndPagesWithoutItAreUntouched()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 >>");
        b.AddPage("0 0 1 rg 0 0 200 200 re f /GS1 gs 1 1 0 rg 10 10 180 180 re f", $"<< /ExtGState << /GS1 {gs} 0 R >> >>");
        b.AddPage("0 0.5 0 rg 50 50 100 100 re f");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        var page = Assert.Single(result.Flattening!.Pages);
        Assert.True(page.WholePage);
        Assert.Equal(new PdfRect(0, 0, 200, 200), Assert.Single(page.Regions).Bounds);
        Assert.Equal("0 0.5 0 rg 50 50 100 100 re f", (await PageContent(result.Bytes, 2)).Trim());
    }

    [Fact]
    public async Task ALongLineOfSmallText_KeepsItsAdvance_WithinPdfA1sNumberLimits()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 >>");
        // 4-point text 190 points long: skipped in the copy over the image, its advance is 47 500 thousandths.
        string line = new string('m', 57);
        b.AddPage($"BT /F1 4 Tf 5 100 Td ({line}) Tj ET q /GS1 gs 1 0 0 rg 20 90 50 30 re f Q 0 0 1 rg 150 150 20 20 re f",
            $"<< /Font << /F1 {font} 0 R >> /ExtGState << /GS1 {gs} 0 R >> >>");
        byte[] pdf = b.Build();
        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.Equal("-30000 -17500", TransparencyFlattener.Adjustments(-47500));
        Assert.Contains("[-30000 -", await PageContent((await Flatten(pdf)).Bytes));
        Assert.Contains(line, await Text(result.Bytes));
    }

    [Fact]
    public async Task TranslucentAnnotations_AndAHighlight_GetFlattenedAppearances_AndKeepTheirComments()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        // A translucent square (CA 0.5) and a highlight whose appearance multiplies over the text.
        int squareAp = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 60 40]", "0 0 1 rg 0 0 60 40 re f");
        int square = b.Add($"<< /Type /Annot /Subtype /Square /Rect [120 120 180 160] /CA 0.5 /F 4 /Contents (Check the figures) /AP << /N {squareAp} 0 R >> >>");
        int highlightAp = b.AddStream("/Type /XObject /Subtype /Form /BBox [18 96 112 116] /Resources << /ExtGState << /M << /BM /Multiply >> >> >>",
            "/M gs 1 1 0 rg 18 96 94 20 re f");
        int highlight = b.Add($"<< /Type /Annot /Subtype /Highlight /Rect [18 96 112 116] /F 4 /QuadPoints [18 116 112 116 18 96 112 96] /Contents (Important) /AP << /N {highlightAp} 0 R >> >>");
        // An appearance with only an opaque group: cleaned, not drawn as an image.
        int plainAp = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Group << /S /Transparency >>", "1 0 0 rg 0 0 20 20 re f");
        int plain = b.Add($"<< /Type /Annot /Subtype /Square /Rect [20 160 40 180] /F 4 /AP << /N {plainAp} 0 R >> >>");
        b.AddPage("BT /F1 14 Tf 0 0 0 rg 20 100 Td (Marked words) Tj ET 0.8 0.2 0.2 rg 100 100 90 30 re f",
            $"<< /Font << /F1 {font} 0 R >> >>", extra: $"/Annots [{square} 0 R {highlight} 0 R {plain} 0 R]");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.Empty(result.Flattening!.Pages); // the page itself has no transparency
        Assert.Equal(new[] { "Square", "Highlight" }, result.Flattening.Annotations.Select(a => a.Subtype));
        Assert.True(result.Flattening.Annotations.Single(a => a.Subtype == "Highlight").Region.ContainsText);
        Assert.Equal(1, result.Flattening.AnnotationsCleaned);
        Assert.Contains("Marked words", await Text(result.Bytes));

        // The annotations are still there, with their comments and no opacity of their own.
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var annots = (PdfArray)doc.Resolver.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        var dicts = annots.Items.Select(a => (PdfDictionary)doc.Resolver.Resolve(a)!).ToList();
        Assert.Contains(dicts, d => d.GetString("Contents") == "Check the figures" && !d.ContainsKey("CA"));
        Assert.Contains(dicts, d => d.GetString("Contents") == "Important");

        // As shown with annotations, the page looks as it did. PDFium ignores an annotation's /CA (it
        // draws the square opaque), so the square is checked on its own: it is now half blue, as
        // ISO 32000 12.5.2 and Acrobat draw it.
        using var before = await Pdfium(pdf, 144, annotations: true);
        using var after = await Pdfium(result.Bytes, 144, annotations: true);
        int differing = 0;
        for (int y = 0; y < before.HeightPixels; y++)
            for (int x = 0; x < before.WidthPixels; x++)
            {
                if (x >= 236 && x <= 364 && y >= 76 && y <= 164) continue; // the square (120..180 × 120..160), and a pixel or two
                var (b1, g1, r1) = InterpreterCoverageTests.Pixel(before, x, y);
                var (b2, g2, r2) = InterpreterCoverageTests.Pixel(after, x, y);
                if (Math.Abs(b1 - b2) + Math.Abs(g1 - g2) + Math.Abs(r1 - r2) > 96) differing++;
            }
        _output.WriteLine($"annotations PDFium: {differing} pixels differ outside the square");
        // The highlighted words are in its image, drawn with the engine's Helvetica stand-in rather than PDFium's own.
        Assert.True(differing < before.WidthPixels * before.HeightPixels / 80, $"{differing} pixels differ");
        var (sb, sg, sr) = InterpreterCoverageTests.Pixel(after, 300, 90); // over white
        Assert.True(sb > 230 && sr is > 100 and < 160 && sg is > 100 and < 160, $"the square over white is {sr},{sg},{sb}");
    }

    [Fact]
    public async Task OpaqueGroups_InAFormFieldsAppearances_AndInType3Glyphs_AreRemovedWithoutAnyImage()
    {
        var b = new VectorPdfBuilder();
        // A check box whose two states have opaque groups, and a Type 3 glyph drawing an opaque group form.
        int on = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Group << /S /Transparency >>", "0 0 0 rg 4 4 12 12 re f");
        int off = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Group << /S /Transparency >>", "0 0 0 RG 1 1 18 18 re S");
        int widget = b.Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes /AS /Yes /Rect [20 20 40 40] /F 4 /AP << /N << /Yes {on} 0 R /Off {off} 0 R >> >> >>");
        int groupForm = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 1000 1000] /Group << /S /Transparency >>", "0 0 1000 1000 re f");
        int glyph = b.AddStream(string.Empty, "1000 0 0 0 1000 1000 d1 /G Do");
        int font = b.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /square {glyph} 0 R >>" +
                         $" /Encoding << /Type /Encoding /Differences [65 /square] >> /FirstChar 65 /LastChar 65 /Widths [1000] /Resources << /XObject << /G {groupForm} 0 R >> >> >>");
        b.AddPage("BT /T3 30 Tf 0 0 1 rg 60 100 Td (AA) Tj ET", $"<< /Font << /T3 {font} 0 R >> >>", extra: $"/Annots [{widget} 0 R]");
        b.WithCatalog($"/AcroForm << /Fields [{widget} 0 R] >>");
        byte[] pdf = b.Build();

        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.Empty(result.Flattening!.Pages);
        Assert.Empty(result.Flattening.Annotations);
        Assert.Equal(1, result.Flattening.AnnotationsCleaned);
        Assert.Empty(await Images(result.Bytes));
        await AssertLooksTheSame("opaque groups", pdf, (await Flatten(pdf)).Bytes, maxMean: 0.2, maxBad: 0.001);
    }

    [Fact]
    public void RunLengthEncoding_RoundTrips()
    {
        var data = new byte[5000];
        new Random(3).NextBytes(data.AsSpan(1000, 700));
        data.AsSpan(3000, 300).Fill(7);
        var stream = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("RunLengthDecode") }, TransparencyFlattener.RunLength(data));
        Assert.Equal(data, new PdfStreamDecoder(null, null).DecodeStream(stream));
        Assert.True(TransparencyFlattener.RunLength(new byte[100_000]).Length <= 100_000 / 64 + 16);
    }

    [Fact]
    public async Task Type3GlyphsDrawnWithAlpha_AreFlattened()
    {
        var b = new VectorPdfBuilder();
        int glyph = b.AddStream(string.Empty, "1000 0 0 0 1000 1000 d1 /G gs 0 0 1000 1000 re f");
        int font = b.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /square {glyph} 0 R >>" +
                         " /Encoding << /Type /Encoding /Differences [65 /square] >> /FirstChar 65 /LastChar 65 /Widths [1000]" +
                         " /Resources << /ExtGState << /G << /ca 0.5 >> >> >> >>");
        b.AddPage("0 0 1 rg 30 30 60 60 re f BT /T3 40 Tf 1 0 0 rg 50 50 Td (AA) Tj ET", $"<< /Font << /T3 {font} 0 R >> >>");
        byte[] pdf = b.Build();
        var result = await Convert(pdf);
        await AssertValidPdfA1b(result);
        Assert.Single(Assert.Single(result.Flattening!.Pages).Regions);
        // PDFium ignores graphics states inside Type 3 glyph procedures (it draws the glyphs opaque), so only the engine is compared.
        await AssertLooksTheSame("type 3", pdf, (await Flatten(pdf)).Bytes, pdfium: false);
    }

    [Fact]
    public async Task WithoutARasterizer_TransparencyIsStillRefused()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 >>");
        b.AddPage("/GS1 gs 1 0 0 rg 20 20 100 100 re f", $"<< /ExtGState << /GS1 {gs} 0 R >> >>");
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var refused = Assert.Throws<PdfAConversionRefusedException>(() => PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavor = PdfAFlavor.A1b }));
        Assert.Contains("does not allow transparency", refused.Message);
    }

    [Fact]
    public async Task ThePixelResolution_IsConfigurable()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 >>");
        b.AddPage("q /GS1 gs 1 0 0 rg 20 20 72 72 re f Q", $"<< /ExtGState << /GS1 {gs} 0 R >> >>");
        var result = await Convert(b.Build(), new PdfFlatteningOptions { Dpi = 150 });
        await AssertValidPdfA1b(result);
        var region = Assert.Single(Assert.Single(result.Flattening!.Pages).Regions);
        Assert.Equal(150, region.Dpi);
        Assert.InRange(region.WidthPixels, 150, 160); // 72 points and the margin, at 150 dpi
    }

    [Fact]
    public void PngPredictors_RoundTrip()
    {
        var rng = new Random(7);
        var samples = new byte[13 * 5 * 3];
        rng.NextBytes(samples);
        byte[] predicted = TransparencyFlattener.PngPredict(samples, 13, 5, 3);
        var data = new Dictionary<string, PdfObject>
        {
            ["DecodeParms"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Predictor"] = new PdfInteger(15), ["Colors"] = new PdfInteger(3), ["BitsPerComponent"] = new PdfInteger(8), ["Columns"] = new PdfInteger(13),
            }),
            ["Filter"] = new PdfName("FlateDecode"),
        };
        var stream = PdfObjectWriter.NewStream(data, Redaction.ContentRedactor.Deflate(predicted));
        Assert.Equal(samples, new PdfStreamDecoder(null, null).DecodeStream(stream));
    }

    /// <summary>A Tj of the text as the rewritten content writes it (a hex string).</summary>
    private static string Shown(string text) => "<" + System.Convert.ToHexString(Encoding.Latin1.GetBytes(text)) + "> Tj";

    private static int CountOccurrences(string text, string what)
    {
        int count = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
