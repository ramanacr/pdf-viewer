using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Visible signature appearances, Acrobat-style: a handwritten or picture signature (an image
/// XObject whose transparency is a soft mask) beside or instead of the details, the name set
/// large when there is no picture, and the text in an embedded font subset with a ToUnicode map.
/// Checked in the file's structure, in pixels from both renderers (this engine's Direct2D and
/// PDFium), and in the text PDFium extracts.
/// </summary>
public class SignatureAppearanceTests : IDisposable
{
    private readonly TestPki _pki = new("Ada Lovelace");
    private static readonly PdfRect Box = new(20, 20, 260, 80); // page 300 x 400: pixel rows 300..380 at 72 dpi

    public void Dispose() => _pki.Dispose();

    private static byte[] PlainPdf()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 18 Tf 20 350 Td (Contract) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 300 400]");
        return b.Build();
    }

    /// <summary>A dark blue diagonal stroke on a transparent background: what a scanned signature with its paper removed looks like.</summary>
    private static PdfImageContent Stroke(int w = 120, int h = 40)
    {
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                rgb[i * 3] = 10; rgb[i * 3 + 1] = 20; rgb[i * 3 + 2] = 160;
                alpha[i] = Math.Abs(y - x * (double)h / w) < h / 6.0 ? (byte)255 : (byte)0;
            }
        return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, alpha);
    }

    private async Task<byte[]> Sign(PdfSignatureAppearance appearance, string name = "Ada Lovelace", byte[]? pdf = null)
    {
        pdf ??= PlainPdf();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest
        {
            SignerName = name, Reason = "Approved", Location = "London", Rect = Box, Appearance = appearance,
            SigningTime = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero),
        });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), _pki.Signer));
    }

    /// <summary>The widget's normal appearance: its decoded content and its resources.</summary>
    private static async Task<(string Content, PdfDictionary Resources, PdfVectorDocument Doc)> Appearance(byte[] pdf)
    {
        var doc = await PdfVectorDocument.OpenAsync(pdf);
        var r = doc.Resolver;
        var (field, _, _) = PdfSignatureValidator.SignedFields(doc).Single();
        var widget = (PdfDictionary)r.Resolve(field.Widgets[0].ObjectNumber)!;
        var ap = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(widget["AP"])!)["N"])!;
        string content = Encoding.Latin1.GetString(new PdfEngine.Vector.Streams.PdfStreamDecoder(null, r.Resolve).DecodeStream(ap));
        return (content, (PdfDictionary)r.Resolve(ap.Dictionary["Resources"])!, doc);
    }

    private static async Task<RenderedPage> RenderDirect2D(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, CancellationToken.None);
        return result.Page;
    }

    private static async Task<RenderedPage> RenderPdfium(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf);
        return await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = 1, Dpi = 72 });
    }

    private static async Task<string> PdfiumText(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf);
        return await engine.TextService.ExtractPageTextAsync(doc, 1);
    }

    /// <summary>Counts pixels in a rectangle given in PDF user space (y up) that satisfy <paramref name="test"/> (B, G, R).</summary>
    private static int Count(RenderedPage page, double x0, double y0, double x1, double y1, Func<byte, byte, byte, bool> test)
    {
        var span = page.Pixels.Span;
        int n = 0;
        for (int py = (int)(page.HeightPixels - y1); py < (int)(page.HeightPixels - y0); py++)
            for (int px = (int)x0; px < (int)x1; px++)
            {
                int o = py * page.Stride + px * 4;
                if (test(span[o], span[o + 1], span[o + 2])) n++;
            }
        return n;
    }

    private static bool Blue(byte b, byte g, byte r) => b > 110 && r < 100 && g < 100;
    private static bool White(byte b, byte g, byte r) => b > 235 && g > 235 && r > 235;
    private static bool Dark(byte b, byte g, byte r) => b < 110 && g < 110 && r < 110;

    /// <summary>
    /// A copy whose page content draws the signature's appearance, so PDFium's text extraction
    /// (which reads page content, not annotations) sees the text the appearance sets.
    /// </summary>
    private static async Task<byte[]> DrawnIntoContent(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var r = doc.Resolver;
        var (field, _, _) = PdfSignatureValidator.SignedFields(doc).Single();
        var widget = (PdfDictionary)r.Resolve(field.Widgets[0].ObjectNumber)!;
        var apRef = (PdfIndirectRef)((PdfDictionary)r.Resolve(widget["AP"])!)["N"]!;
        var pageRef = (PdfIndirectRef)widget["P"]!;
        var page = (PdfDictionary)r.Resolve(pageRef)!;
        int n = PdfIncrementalWriter.NextObjectNumber(doc);
        var resources = new Dictionary<string, PdfObject>(((PdfDictionary)r.Resolve(page["Resources"])!).Entries)
        {
            ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["SigAp"] = apRef }),
        };
        var contents = new List<PdfObject>();
        if (r.Resolve(page["Contents"]) is PdfArray existing) contents.AddRange(existing.Items); else contents.Add(page["Contents"]!);
        contents.Add(new PdfIndirectRef(n));
        var objects = new Dictionary<int, PdfObject>
        {
            [n] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(),
                Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"q 1 0 0 1 {Box.X} {Box.Y} cm /SigAp Do Q"))),
            [pageRef.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Entries)
            {
                ["Contents"] = new PdfArray(contents), ["Resources"] = new PdfDictionary(resources),
            }),
        };
        return PdfIncrementalWriter.Append(pdf, doc, objects);
    }

    [Fact]
    public async Task PictureAndText_EmbedTheImageWithItsSoftMask_AndAFontSubset()
    {
        byte[] signed = await Sign(new PdfSignatureAppearance
        {
            Layout = PdfSignatureLayout.ImageAndText, Image = Stroke(), Fonts = SystemFontCatalog.Installed,
        });

        // Where the picture is (its left part of the box): blue where the stroke is, the white page
        // where it is transparent. The details are on the right.
        // This engine draws the widget; PDFium (which draws widgets only with a form-fill
        // environment) draws the same appearance stream placed in the page content.
        byte[] drawnIn = await DrawnIntoContent(signed);
        foreach (var page in new[] { await RenderDirect2D(signed), await RenderPdfium(drawnIn) })
            using (page)
            {
                int blue = Count(page, 24, 24, 150, 96, Blue);
                int white = Count(page, 24, 24, 150, 96, White);
                int text = Count(page, 154, 24, 276, 96, Dark);
                Assert.True(blue > 400, $"the stroke is drawn ({blue} blue pixels)");
                Assert.True(white > 2000, $"the picture's transparent parts show the page ({white} white pixels)");
                Assert.True(text > 60, $"the details are drawn beside it ({text} dark pixels)");
                Assert.Equal(0, Count(page, 154, 24, 276, 96, Blue));
            }

        var (content, resources, doc) = await Appearance(signed);
        using (doc)
        {
            var r = doc.Resolver;
            var image = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(resources["XObject"])!)["SigImg"])!;
            Assert.Equal("Image", image.Dictionary.GetName("Subtype"));
            Assert.Equal(120, image.Dictionary.GetInteger("Width"));
            var mask = Assert.IsType<PdfStream>(r.Resolve(image.Dictionary["SMask"]));
            Assert.Equal("DeviceGray", mask.Dictionary.GetName("ColorSpace"));
            Assert.Matches(@"q [\d.]+ 0 0 [\d.]+ [\d.]+ [\d.]+ cm /SigImg Do Q", content);

            var fonts = (PdfDictionary)r.Resolve(resources["Font"])!;
            var font = (PdfDictionary)r.Resolve(Assert.Single(fonts.Entries).Value)!;
            Assert.Equal("Type0", font.GetName("Subtype"));
            Assert.Matches("^[A-Z]{6}\\+", font.GetName("BaseFont"));
            Assert.NotNull(font["ToUnicode"]);
            var cid = (PdfDictionary)r.Resolve(((PdfArray)r.Resolve(font["DescendantFonts"])!)[0])!;
            var descriptor = (PdfDictionary)r.Resolve(cid["FontDescriptor"])!;
            Assert.IsType<PdfStream>(r.Resolve(descriptor["FontFile2"]));
        }


        string extracted = Regex.Replace(await PdfiumText(drawnIn), @"\s+", " ");
        Assert.Contains("Digitally signed by Ada Lovelace", extracted);
        Assert.Contains("Approved", extracted);
        Assert.Contains("London", extracted);

        // Still a valid signature: the appearance is inside the signed revision.
        Assert.True(Assert.Single(await PdfSignatureValidator.ValidateAsync(signed)).IntegrityValid);
    }

    [Fact]
    public async Task PictureOnly_FillsTheBox_KeepingItsProportions_WithNoText()
    {
        byte[] signed = await Sign(new PdfSignatureAppearance { Layout = PdfSignatureLayout.ImageOnly, Image = Stroke(200, 50) });
        var (content, resources, doc) = await Appearance(signed);
        doc.Dispose();
        Assert.DoesNotContain("BT", content);
        Assert.Null(resources["Font"]);
        var m = Regex.Match(content, @"q ([\d.]+) 0 0 ([\d.]+) ([\d.]+) ([\d.]+) cm");
        Assert.True(m.Success, content);
        double w = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), h = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.Equal(4.0, w / h, 3);           // the picture's own proportions
        Assert.Equal(252, w, 1);               // as wide as the box allows (260 less the padding)

        using var page = await RenderDirect2D(signed);
        Assert.True(Count(page, 20, 20, 280, 100, Blue) > 1000);
        Assert.Equal(0, Count(page, 20, 20, 280, 100, Dark));
    }

    [Fact]
    public async Task WithoutAPicture_TheNameIsSetLarge_BesideTheDetails()
    {
        byte[] signed = await Sign(new PdfSignatureAppearance { Layout = PdfSignatureLayout.ImageAndText });
        var (content, _, doc) = await Appearance(signed);
        doc.Dispose();
        var sizes = Regex.Matches(content, @"/Helv ([\d.]+) Tf \(([^)]*)\) Tj").Select(m => (Size: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Text: m.Groups[2].Value)).ToList();
        var name = sizes.Where(s => s.Text.Contains("Ada") || s.Text.Contains("Lovelace")).ToList();
        Assert.NotEmpty(name);
        Assert.All(name, n => Assert.True(n.Size > 12, $"the name is large ({n.Size} pt)"));
        Assert.All(sizes.Where(s => s.Text.StartsWith("Date", StringComparison.Ordinal) || s.Text.StartsWith("Reason", StringComparison.Ordinal)), s => Assert.True(s.Size <= 12));
        Assert.DoesNotContain("Digitally signed by", content); // the name is already there, large

        string extracted = await PdfiumText(await DrawnIntoContent(signed));
        Assert.Contains("Reason: Approved", extracted);
    }

    [Fact]
    public async Task TextOnly_ShowsOnlyTheChosenDetails()
    {
        byte[] signed = await Sign(new PdfSignatureAppearance { ShowDate = false, ShowLocation = false, ShowLabels = false });
        var (content, _, doc) = await Appearance(signed);
        doc.Dispose();
        Assert.Contains("(Ada Lovelace) Tj", content);
        Assert.Contains("(Approved) Tj", content);
        Assert.DoesNotContain("London", content);
        Assert.DoesNotContain("2026", content);
        Assert.DoesNotContain("Reason:", content);
    }

    [Fact]
    public async Task ANameHelveticaCannotShow_IsSetInAnEmbeddedInstalledFont()
    {
        const string name = "Zoë Łukasiewicz";
        byte[] signed = await Sign(new PdfSignatureAppearance(), name);
        var (_, resources, doc) = await Appearance(signed);
        using (doc)
        {
            var fonts = (PdfDictionary)doc.Resolver.Resolve(resources["Font"])!;
            Assert.All(fonts.Entries.Values, f => Assert.Equal("Type0", ((PdfDictionary)doc.Resolver.Resolve(f)!).GetName("Subtype")));
        }
        Assert.Contains(name, await PdfiumText(await DrawnIntoContent(signed)));
    }

    [Fact]
    public async Task PictureSignature_OnARotatedPage_IsDrawnUpright()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 300 400]", extra: "/Rotate 90");
        byte[] signed = await Sign(new PdfSignatureAppearance { Layout = PdfSignatureLayout.ImageOnly, Image = Stroke() }, pdf: b.Build());
        var (content, _, doc) = await Appearance(signed);
        using (doc)
        {
            var (field, _, _) = PdfSignatureValidator.SignedFields(doc).Single();
            var widget = (PdfDictionary)doc.Resolver.Resolve(field.Widgets[0].ObjectNumber)!;
            var ap = (PdfStream)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(widget["AP"])!)["N"])!;
            Assert.NotNull(ap.Dictionary["Matrix"]); // turned against the page's rotation
            var bbox = (PdfArray)ap.Dictionary["BBox"]!;
            Assert.Equal(80, ((PdfInteger)bbox[2]).Value); // the box's height becomes the appearance's width
        }
        Assert.Contains("/SigImg Do", content);
    }
}
