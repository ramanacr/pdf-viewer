using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// M9, native rendering: the engine draws every page by itself. Damaged or unsupported content is
/// skipped or drawn by best effort the way PDFium draws it, a fallback region without a provider
/// shows what the engine draws of it (never a placeholder, never a blank), night mode inverts
/// the finished tile on the GPU, and form widgets come from their appearance streams, generated
/// where a reader must generate them.
/// </summary>
public class NativeRenderingTests
{
    private static async Task<(PdfVectorDocument Doc, IPdfDisplayList List)> OpenAsync(byte[] pdf, int page = 1)
    {
        var doc = await PdfVectorDocument.OpenAsync(pdf);
        return (doc, await doc.GetPageDisplayListAsync(page));
    }

    private static (byte B, byte G, byte R) Pixel(RenderedPage page, int x, int y)
    {
        var span = page.Pixels.Span;
        int i = y * page.Stride + x * 4;
        return (span[i], span[i + 1], span[i + 2]);
    }

    private static byte[] Flate(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.Latin1.GetBytes(text));
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ damaged content

    [Fact]
    public async Task StreamKeyword_FollowedBySpaces_StillReadsTheStream()
    {
        // "stream" must be followed by an end of line; PDFium also accepts spaces before it.
        var b = new VectorPdfBuilder();
        byte[] data = Flate("1 0 0 rg 10 10 50 50 re f");
        using var body = new MemoryStream();
        body.Write(Encoding.Latin1.GetBytes($"<< /Filter /FlateDecode /Length {data.Length} >>\nstream \n"));
        body.Write(data);
        body.Write(Encoding.Latin1.GetBytes("\nendstream"));
        int content = b.Add(body.ToArray());
        b.AddPageObject($"<< /Type /Page /Parent {{0}} 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents {content} 0 R >>");

        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            Assert.False(list.HasFallback);
            Assert.Single(list.Commands.OfType<FillPath>());
        }
    }

    [Fact]
    public async Task ContentStreamWithAnUnknownFilter_IsSkipped_AndThePageGoesOn()
    {
        var b = new VectorPdfBuilder();
        int bad = b.AddStream("/Filter /Flatedecode.", "0 0 1 rg 0 0 200 200 re f");
        int good = b.AddStream(string.Empty, "1 0 0 rg 10 10 50 50 re f");
        b.AddPageObject($"<< /Type /Page /Parent {{0}} 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents [{bad} 0 R {good} 0 R] >>");

        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            Assert.False(list.HasFallback);
            Assert.True(list.Features.HasFlag(PdfFeatureSet.Recovered));
            var fill = Assert.Single(list.Commands.OfType<FillPath>());
            Assert.True(fill.Paint.Color.R > 0.9 && fill.Paint.Color.B < 0.1);
        }
    }

    [Fact]
    public async Task ImageWithAnUnknownFilter_IsSkipped()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("q 50 0 0 50 10 10 cm BI /W 1 /H 1 /CS /G /BPC 8 /F /Lzw ID x EI Q 1 0 0 rg 100 100 20 20 re f");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            Assert.False(list.HasFallback);
            Assert.True(list.Features.HasFlag(PdfFeatureSet.Recovered));
            Assert.Empty(list.Commands.OfType<DrawImage>());
            Assert.Single(list.Commands.OfType<FillPath>());
        }
    }

    [Fact]
    public async Task UnknownColourSpaceName_KeepsTheCurrentSpace()
    {
        // /CS0 is not in the resources: PDFium keeps DeviceGray, so "0.5 sc" paints mid gray.
        var b = new VectorPdfBuilder();
        b.AddPage("/CS0 cs 0.5 sc 10 10 50 50 re f");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            Assert.False(list.HasFallback);
            var fill = Assert.Single(list.Commands.OfType<FillPath>());
            Assert.InRange(fill.Paint.Color.R, 0.45, 0.55);
            Assert.InRange(fill.Paint.Color.G, 0.45, 0.55);
        }
    }

    [Fact]
    public async Task UnknownCMap_IsReadAsIdentity_AndTheTextIsDrawn()
    {
        var b = new VectorPdfBuilder();
        int cid = b.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 >>");
        int font = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /No-Such-CMap /DescendantFonts [{cid} 0 R] >>");
        b.AddPage("BT /F1 30 Tf 10 100 Td <00410042> Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            Assert.False(list.HasFallback);
            Assert.True(list.Features.HasFlag(PdfFeatureSet.Recovered));
            var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>());
            Assert.False(run.Run.IsInvisible);
            Assert.Equal(2, run.Run.Glyphs.Count);
        }
    }

    [Fact]
    public async Task UnloadableEmbeddedFont_IsDrawnWithASubstitute()
    {
        // A FontFile2 that is not a font program: the backend substitutes by name, as PDFium does.
        var b = new VectorPdfBuilder();
        int program = b.AddStream(string.Empty, "this is not a TrueType program");
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {program} 0 R >>");
        int font = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /Arial /FirstChar 72 /LastChar 79 /Widths [722 556 278 278 278 278 278 611] /FontDescriptor {descriptor} 0 R >>");
        b.AddPage("BT /F1 48 Tf 10 80 Td (HOLL) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        using (var renderer = new Direct2DVectorRenderer())
        {
            Assert.Empty(await renderer.AnalyzeAsync(list));
            using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
            Assert.True(CountDark(page) > 100, "the text is drawn");
        }
    }

    [Fact]
    public async Task JpegWhoseComponentsDoNotMatchItsColourSpace_IsShownInItsOwnColours()
    {
        // An RGB JPEG declared /Indexed: PDFium shows the JPEG's colours.
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 0 <000000>] /Filter /DCTDecode",
            RedJpeg(8, 8));
        b.AddPage("q 100 0 0 100 50 50 cm /Im0 Do Q", $"<< /XObject << /Im0 {image} 0 R >> >>");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        using (var renderer = new Direct2DVectorRenderer())
        {
            Assert.Empty(await renderer.AnalyzeAsync(list));
            using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
            var (bb, gg, rr) = Pixel(page, 100, 100);
            Assert.True(rr > 180 && gg < 90 && bb < 90, $"expected the JPEG's red, got {rr},{gg},{bb}");
        }
    }

    [Fact]
    public async Task FormNesting_DeeperThanPdfiumDraws_IsCutThere_NotAtTheWholePage()
    {
        var b = new VectorPdfBuilder();
        int inner = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "1 0 0 rg 10 10 30 30 re f");
        int current = inner;
        for (int i = 0; i < 30; i++)
            current = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /X {current} 0 R >> >>", "/X Do");
        b.AddPage("0 0 1 rg 100 100 20 20 re f /X Do", $"<< /XObject << /X {current} 0 R >> >>");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            Assert.False(list.HasFallback);
            // 31 levels are within the 40 PDFium draws: both fills are there.
            Assert.Equal(2, list.Commands.OfType<FillPath>().Count());
        }
    }

    // ------------------------------------------------------------------ fallback regions without a provider

    [Fact]
    public async Task FallbackRegionWithoutProvider_IsDrawnByTheEngine_NotLeftBlankOrOutlined()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 rg 20 20 100 100 re f");
        var (doc, plain) = await OpenAsync(b.Build());
        using (doc)
        using (var renderer = new Direct2DVectorRenderer())
        {
            // A region the engine still classifies, over content it draws.
            var token = new PdfFallbackToken(1, new PdfRect(10, 10, 150, 150), PdfFallbackReason.InternalCompatibilityGuard, "test region");
            var list = new PdfDisplayList(plain.PageNumber, plain.PageSize, plain.MediaBox, plain.CropBox, plain.RotationDegrees,
                plain.Commands.Append(new DrawFallbackRegion(token, token.Bounds)).ToList(), plain.Features | PdfFeatureSet.Fallback, new[] { token });
            var request = new RenderRequest { PageNumber = 1, Dpi = 72 };

            using (var page = await renderer.RenderDisplayListAsync(list, request))
            {
                var (bb, gg, rr) = Pixel(page, 70, 200 - 70);
                Assert.True(rr > 240 && gg < 15 && bb < 15, $"the engine's drawing shows, no placeholder over it: {rr},{gg},{bb}");
                var (wb, wg, wr) = Pixel(page, 140, 200 - 140);
                Assert.True(wr > 250 && wg > 250 && wb > 250, $"no tint where the engine drew nothing: {wr},{wg},{wb}");
            }

            // Strict vector mode outlines it instead, so it is visible during development.
            renderer.OutlineUnsupportedRegions = true;
            using (var outlined = await renderer.RenderDisplayListAsync(list, request))
            {
                var (wb, wg, wr) = Pixel(outlined, 140, 200 - 140);
                Assert.True(wb < 240 && wr > 240, $"strict mode tints the region orange: {wr},{wg},{wb}");
            }
        }
    }

    // ------------------------------------------------------------------ night mode

    [Fact]
    public async Task NightTile_IsTheInvertedDayTile_BlendModesIncluded()
    {
        var b = new VectorPdfBuilder();
        int gs = b.Add("<< /Type /ExtGState /BM /Multiply /ca 0.7 >>");
        b.AddPage("0 0.6 1 rg 10 10 120 120 re f /G1 gs 1 0.8 0 rg 60 60 120 120 re f 0 g BT 0 Tr ET",
            $"<< /ExtGState << /G1 {gs} 0 R >> >>");
        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        using (var renderer = new Direct2DVectorRenderer())
        {
            var request = new RenderRequest { PageNumber = 1, Dpi = 96 };
            var region = new PixelRegion(0, 0, 200, 200);
            using var day = (await renderer.RenderAsync(list, request, null, region, invertColors: false, CancellationToken.None)).Page;
            using var night = (await renderer.RenderAsync(list, request, null, region, invertColors: true, CancellationToken.None)).Page;
            var d = day.Pixels.Span;
            var n = night.Pixels.Span;
            int worst = 0;
            for (int y = 0; y < day.HeightPixels; y++)
                for (int x = 0; x < day.WidthPixels; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int i = y * day.Stride + x * 4 + c;
                        worst = Math.Max(worst, Math.Abs(255 - d[i] - n[i]));
                    }
            Assert.True(worst <= 1, $"night = 255 - day, worst channel difference {worst}");
        }
    }

    // ------------------------------------------------------------------ form widgets

    private static byte[] FormDocument(string fieldEntries, string widgetExtra, bool needAppearances, string flags = "/F 4")
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        // Widget 3: the field and its widget in one dictionary (merged).
        int widget = b.Add($"<< /Type /Annot /Subtype /Widget /Rect [20 80 180 110] {flags} {fieldEntries} {widgetExtra} >>");
        b.AddPage("", "<< >>", extra: $"/Annots [{widget} 0 R]");
        b.WithCatalog($"/AcroForm << /Fields [{widget} 0 R] /DR << /Font << /Helv {helv} 0 R >> >> /DA (/Helv 0 Tf 0 g) {(needAppearances ? "/NeedAppearances true" : "")} >>");
        return b.Build();
    }

    [Fact]
    public async Task TextFieldWithoutAppearance_ShowsItsValue()
    {
        var pdf = FormDocument("/FT /Tx /T (name) /V (Ada Lovelace) /DA (/Helv 12 Tf 0 g)", string.Empty, needAppearances: false);
        var (doc, list) = await OpenAsync(pdf);
        using (doc)
        {
            var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>());
            Assert.Equal("Ada Lovelace", run.Run.FullText);
        }
    }

    [Fact]
    public async Task NeedAppearances_RegeneratesTheStaleAppearanceFromTheValue()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int stale = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 160 30] /Resources << /Font << /Helv {helv} 0 R >> >>",
            "BT /Helv 12 Tf 2 10 Td (Old value) Tj ET");
        int widget = b.Add($"<< /Type /Annot /Subtype /Widget /Rect [20 80 180 110] /F 4 /FT /Tx /T (name) /V (New value) /DA (/Helv 12 Tf 0 g) /AP << /N {stale} 0 R >> >>");
        b.AddPage("", "<< >>", extra: $"/Annots [{widget} 0 R]");
        b.WithCatalog($"/AcroForm << /Fields [{widget} 0 R] /DR << /Font << /Helv {helv} 0 R >> >> /NeedAppearances true >>");
        var pdf = b.Build();

        var (doc, list) = await OpenAsync(pdf);
        using (doc)
        {
            var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>());
            Assert.Equal("New value", run.Run.FullText);
        }
    }

    [Fact]
    public async Task CheckBoxWithoutAS_ShowsTheStateItsValueNames()
    {
        var b = new VectorPdfBuilder();
        int on = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 rg 0 0 20 20 re f");
        int off = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 0 1 rg 0 0 20 20 re f");
        int widget = b.Add($"<< /Type /Annot /Subtype /Widget /Rect [20 20 40 40] /F 4 /FT /Btn /T (agree) /V /Yes /AP << /N << /Yes {on} 0 R /Off {off} 0 R >> >> >>");
        b.AddPage("", "<< >>", extra: $"/Annots [{widget} 0 R]");
        b.WithCatalog($"/AcroForm << /Fields [{widget} 0 R] >>");

        var (doc, list) = await OpenAsync(b.Build());
        using (doc)
        {
            var fill = Assert.Single(list.Commands.OfType<FillPath>());
            Assert.True(fill.Paint.Color.R > 0.9 && fill.Paint.Color.B < 0.1, "the /Yes appearance");
        }
    }

    [Fact]
    public async Task CheckBoxWithoutAppearance_IsDrawnWithACheckWhenOn()
    {
        var pdf = FormDocument("/FT /Btn /T (agree) /V /Yes /AS /Yes", "/MK << /BC [0] /BG [1] >>", needAppearances: false);
        var (doc, list) = await OpenAsync(pdf);
        using (doc)
        {
            var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>());
            Assert.Equal("ZapfDingbats", run.Run.FontFamilyName);
        }
    }

    [Fact]
    public async Task PrintedPage_HasPrintAnnotationsOnly_ScreenPage_HasNoViewOnesLeftOut()
    {
        var b = new VectorPdfBuilder();
        int screenOnly = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 rg 0 0 20 20 re f");
        int printOnly = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 0 1 rg 0 0 20 20 re f");
        // No Print flag (shown, not printed); Print + NoView (printed, not shown).
        int a1 = b.Add($"<< /Type /Annot /Subtype /Square /Rect [10 10 30 30] /F 0 /AP << /N {screenOnly} 0 R >> >>");
        int a2 = b.Add($"<< /Type /Annot /Subtype /Square /Rect [50 50 70 70] /F 36 /AP << /N {printOnly} 0 R >> >>");
        b.AddPage("", "<< >>", extra: $"/Annots [{a1} 0 R {a2} 0 R]");

        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var screen = await doc.GetPageDisplayListAsync(1);
        var print = await doc.GetPagePrintDisplayListAsync(1);
        var screenFill = Assert.Single(screen.Commands.OfType<FillPath>());
        Assert.True(screenFill.Paint.Color.R > 0.9);
        var printFill = Assert.Single(print.Commands.OfType<FillPath>());
        Assert.True(printFill.Paint.Color.B > 0.9);
    }

    // ------------------------------------------------------------------ helpers

    private static int CountDark(RenderedPage page)
    {
        var span = page.Pixels.Span;
        int dark = 0;
        for (int y = 0; y < page.HeightPixels; y++)
            for (int x = 0; x < page.WidthPixels; x++)
            {
                int i = y * page.Stride + x * 4;
                if (span[i] < 128 && span[i + 1] < 128 && span[i + 2] < 128) dark++;
            }
        return dark;
    }

    private static byte[] RedJpeg(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 230; pixels[i + 1] = 20; pixels[i + 2] = 20; }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
