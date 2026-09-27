using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Redaction removes, it does not cover: the text under a mark is gone from every extractor and
/// every stream in the file, the text beside it has not moved, image pixels and vector art under
/// the mark are gone while the rest renders exactly as before, annotations and form fields under
/// it leave, alternate text about it is dropped, and no earlier revision survives.
/// </summary>
public class RedactionTests
{
    private readonly ITestOutputHelper _output;
    public RedactionTests(ITestOutputHelper output) => _output = output;

    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>Where a string starts, in Helvetica at a size, after a prefix: the x of each word.</summary>
    private static double WidthOf(string s, double size) =>
        s.Sum(c => PdfEngine.Vector.Fonts.Standard14Fonts.TryGetWidth("Helvetica", c, out double w) ? w : 0) / 1000.0 * size;

    private static PdfRect Around(double x0, string before, string word, double baseline, double size) =>
        new(x0 + WidthOf(before, size) + 0.5, baseline - 0.25 * size, WidthOf(word, size) - 1, size * 1.2);

    private static async Task<byte[]> Redact(byte[] pdf, IReadOnlyList<PdfRedactionArea> areas, PdfRedactionOptions? options = null, string? password = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        return PdfRedactor.Apply(doc, areas, options).Bytes;
    }

    private static async Task<string> PdfiumText(byte[] pdf, int page = 1, string? password = null)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, password);
        return await engine.TextService.ExtractPageTextAsync(doc, page);
    }

    /// <summary>Every stream in the file, decompressed where it is Flate: what someone digging in the file would find.</summary>
    private static string AllDecodedStreams(byte[] pdf)
    {
        var sb = new StringBuilder(Encoding.Latin1.GetString(pdf));
        string text = Encoding.Latin1.GetString(pdf);
        foreach (Match m in Regex.Matches(text, @"stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var data = pdf.AsSpan(start, end - start).ToArray();
            try
            {
                using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                sb.Append(Encoding.Latin1.GetString(o.ToArray()));
            }
            catch (InvalidDataException) { }
        }
        return sb.ToString();
    }

    /// <summary>The word appears nowhere in the file: not literally, not as a hex string, not in any decompressed stream.</summary>
    private static void AssertAbsent(byte[] pdf, string word)
    {
        string all = AllDecodedStreams(pdf);
        Assert.DoesNotContain(word, all);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.Latin1.GetBytes(word)), all.ToUpperInvariant());
    }

    private static bool Present(byte[] pdf, string word)
    {
        string all = AllDecodedStreams(pdf);
        return all.Contains(word) || all.ToUpperInvariant().Contains(Convert.ToHexString(Encoding.Latin1.GetBytes(word)));
    }

    /// <summary>Page-space origins of every glyph, by character, in drawing order.</summary>
    private static async Task<List<(string Ch, double X, double Y)>> Glyphs(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var result = new List<(string, double, double)>();
        foreach (var cmd in list.Commands.OfType<PdfEngine.Vector.DrawGlyphRun>())
            foreach (var g in cmd.Run.Glyphs)
            {
                var p = cmd.Run.TextToPage.Transform(g.OffsetX, g.OffsetY + cmd.Run.TextRise);
                result.Add((g.Unicode ?? "?", p.X, p.Y));
            }
        return result;
    }

    private static byte[] TextPage(string content, string? extraObjects = null)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        b.AddPage(content, $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 300]", flate: true);
        return b.Build();
    }

    [Fact]
    public async Task Text_UnderTheMark_IsGone_AndTheRestHasNotMoved()
    {
        byte[] pdf = TextPage("BT /F1 12 Tf 40 200 Td (Hello SECRET world) Tj ET");
        var rect = Around(40, "Hello ", "SECRET", 200, 12);
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) });

        string text = await PdfiumText(red);
        Assert.Contains("Hello", text);
        Assert.Contains("world", text);
        Assert.DoesNotContain("SECRET", text);
        AssertAbsent(red, "SECRET");

        var before = await Glyphs(pdf);
        var after = await Glyphs(red);
        Assert.Equal("Hello world", string.Concat(after.Select(g => g.Ch)).Replace("  ", " ").Trim());
        foreach (var g in after)
        {
            var original = before.First(b => b.Ch == g.Ch && Math.Abs(b.X - g.X) < 0.01 && Math.Abs(b.Y - g.Y) < 0.01);
            Assert.NotEqual(default, original);
        }
        // The last glyph is where it was, so nothing after the removed word shifted.
        Assert.Equal(before[^1].X, after[^1].X, 3);
    }

    [Fact]
    public async Task Kerning_WordSpacing_AndNextLineOperators_KeepTheirPositions()
    {
        byte[] pdf = TextPage(
            "BT /F1 10 Tf 14 TL 3 Tw 40 250 Td [(Top ) -120 (SE) 40 (CRET) -250 ( tail)] TJ " +
            "(next line) ' 2 1 (third SECRET line) \" ET");
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var before = await Glyphs(pdf);
        // Mark every glyph of both SECRETs by their measured positions.
        var secrets = before.Where((g, i) => "SECRET".Contains(g.Ch) && g.Ch != " ").ToList();
        var areas = before.Select((g, i) => (g, i)).Where(t => IsSecret(before, t.i)).Select(t => new PdfRedactionArea(1, new PdfRect(t.g.X + 0.5, t.g.Y - 2, 4, 9))).ToList();
        byte[] red = await Redact(pdf, areas, new PdfRedactionOptions { FillColor = null });
        var after = await Glyphs(red);
        AssertAbsent(red, "SECRET");
        string text = await PdfiumText(red);
        Assert.Contains("tail", text);
        Assert.Contains("next line", text);
        Assert.Contains("line", text);
        foreach (var g in after)
            Assert.Contains(before, b => b.Ch == g.Ch && Math.Abs(b.X - g.X) < 0.01 && Math.Abs(b.Y - g.Y) < 0.01);
        Assert.Equal(before.Count - 12, after.Count);
    }

    /// <summary>Glyph i starts a "SECRET" (or is inside one).</summary>
    private static bool IsSecret(List<(string Ch, double X, double Y)> g, int i)
    {
        string s = string.Concat(g.Select(x => x.Ch));
        foreach (Match m in Regex.Matches(s, "SECRET"))
            if (i >= m.Index && i < m.Index + m.Length) return true;
        return false;
    }

    [Fact]
    public async Task InvisibleText_LikeAScansOcrLayer_IsRemovedToo()
    {
        byte[] pdf = TextPage("BT 3 Tr /F1 12 Tf 40 200 Td (visible HIDDEN) Tj ET");
        var rect = Around(40, "visible ", "HIDDEN", 200, 12);
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) });
        Assert.DoesNotContain("HIDDEN", await PdfiumText(red));
        AssertAbsent(red, "HIDDEN");
        Assert.Contains("visible", await PdfiumText(red));
    }

    [Fact]
    public async Task ActualText_AboutRemovedText_IsDropped()
    {
        byte[] pdf = TextPage("/Span << /ActualText (SECRET) >> BDC BT /F1 12 Tf 40 200 Td (XXXXXX) Tj ET EMC " +
                              "/Span << /ActualText (kept note) >> BDC BT /F1 12 Tf 40 100 Td (other) Tj ET EMC");
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, new PdfRect(38, 195, 100, 20)) });
        AssertAbsent(red, "SECRET");
        Assert.True(Present(red, "kept note"), "alternate text of untouched content stays");
    }

    [Fact]
    public async Task ImagePixels_UnderTheMark_AreBlanked_AndTheOriginalImageIsGone()
    {
        var b = new VectorPdfBuilder();
        var pixels = new byte[100 * 100 * 3];
        for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 200; pixels[i + 1] = 30; pixels[i + 2] = 60; }
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 100 /Height 100 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels, flate: true);
        b.AddPage("q 200 0 0 200 100 50 cm /Im0 Do Q", $"<< /XObject << /Im0 {image} 0 R >> >>", mediaBox: "[0 0 400 300]");
        byte[] pdf = b.Build();

        var rect = new PdfRect(100, 50, 100, 200); // the left half of the image
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) }, new PdfRedactionOptions { FillColor = null });

        using var page = await Render(red);
        (int R, int G, int B) Px(int x, int yPdf)
        {
            int y = 300 - yPdf, i = y * page.Stride + x * 4;
            var s = page.Pixels.Span;
            return (s[i + 2], s[i + 1], s[i]);
        }
        var inside = Px(150, 150);
        Assert.False(inside.R > 150 && inside.G < 80, $"under the mark the image is gone: {inside}");
        // And the image data itself is blanked there (not just clipped from view).
        using (var doc = await PdfVectorDocument.OpenAsync(red))
        {
            var xo = (PdfDictionary)doc.Resolver.Resolve(doc.PageTree.Pages[0].Resources["XObject"])!;
            var img = (PdfStream)doc.Resolver.Resolve(xo.Entries.Values.Single())!;
            var data = new PdfEngine.Vector.Streams.PdfStreamDecoder(null, doc.Resolver.Resolve).DecodeStream(img);
            Assert.Equal(0, data[(50 * 100 + 10) * 3]);      // row 50, column 10: under the mark
            Assert.Equal(200, data[(50 * 100 + 90) * 3]);    // column 90: outside it
        }
        var right = Px(250, 150);
        Assert.True(right.R > 150 && right.G < 80, $"outside the mark the image is unchanged: {right}");
        // The original pixels are nowhere in the file.
        Assert.Equal(1, Regex.Matches(Encoding.Latin1.GetString(red), "/Subtype\\s*/Image").Count);
        Assert.DoesNotContain(Convert.ToHexString(Deflate(pixels)[..32]), Convert.ToHexString(red));
    }

    [Fact]
    public async Task VectorArt_UnderTheMark_IsCut_AndTheRestRendersAsBefore()
    {
        byte[] pdf = TextPage(
            "0.1 0.5 0.9 rg 50 50 300 100 re f " +                       // a wide filled bar
            "0 0 0 RG 4 w 40 250 m 360 250 l S " +                        // a stroke across the page
            "0.9 0.2 0.2 rg 200 180 m 260 180 260 240 200 240 c 140 240 140 180 200 180 c f"); // a filled curve
        var rect = new PdfRect(150, 30, 100, 240);
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) }, new PdfRedactionOptions { FillColor = null });

        using var a = await Render(pdf);
        using var c = await Render(red);
        int differentOutside = 0, inkInside = 0;
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
            {
                int i = y * a.Stride + x * 4;
                int yPdf = 300 - y;
                bool inside = x >= rect.X + 1 && x < rect.X + rect.Width - 1 && yPdf > rect.Y + 1 && yPdf < rect.Y + rect.Height - 1;
                bool nearEdge = x >= rect.X - 3 && x < rect.X + rect.Width + 3 && yPdf > rect.Y - 3 && yPdf < rect.Y + rect.Height + 3;
                int diff = Math.Abs(a.Pixels.Span[i] - c.Pixels.Span[i]) + Math.Abs(a.Pixels.Span[i + 1] - c.Pixels.Span[i + 1]) + Math.Abs(a.Pixels.Span[i + 2] - c.Pixels.Span[i + 2]);
                if (inside && c.Pixels.Span[i + 1] < 240) inkInside++;
                if (!nearEdge && diff > 24) differentOutside++;
            }
        _output.WriteLine($"ink inside {inkInside}, different outside {differentOutside}");
        Assert.Equal(0, inkInside);
        Assert.True(differentOutside < 10, $"{differentOutside} pixels changed outside the mark");
    }

    [Fact]
    public async Task FormXObjects_AreRedactedWhereTheyAreUnderTheMark_Only()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 40] /Resources << /Font << /F1 {font} 0 R >> >>",
            "BT /F1 12 Tf 5 15 Td (Account 12345) Tj ET");
        b.AddPage("q 1 0 0 1 40 200 cm /Fm0 Do Q q 1 0 0 1 40 50 cm /Fm0 Do Q", $"<< /XObject << /Fm0 {form} 0 R >> >>", mediaBox: "[0 0 400 300]");
        byte[] pdf = b.Build();
        var rect = Around(45, "Account ", "12345", 215, 12);
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) });
        string text = await PdfiumText(red);
        Assert.Contains("Account 12345", text);   // the copy at the bottom was not under the mark
        Assert.Equal(1, Regex.Matches(text, "12345").Count);
        Assert.Equal(2, Regex.Matches(text, "Account").Count);
    }

    [Fact]
    public async Task AnnotationsAndFormFields_UnderTheMark_Leave()
    {
        byte[] pdf = FormPdfFixture.Build();
        var rect = new PdfRect(15, 355, 270, 30); // the person.name field
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) });
        using var doc = await PdfVectorDocument.OpenAsync(red);
        var form = PdfAcroForm.Read(doc)!;
        Assert.Null(form["person.name"]);
        Assert.NotNull(form["notes"]);
        Assert.Equal(6, form.Fields.Count); // 7 fields, less the one under the mark
    }

    [Fact]
    public async Task InlineImages_AreRedactedToo()
    {
        var px = string.Concat(Enumerable.Repeat("FF0000", 16 * 16));
        byte[] pdf = TextPage($"q 160 0 0 160 100 60 cm BI /W 16 /H 16 /CS /RGB /BPC 8 /F /AHx ID {px}> EI Q");
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, new PdfRect(100, 60, 80, 160)) }, new PdfRedactionOptions { FillColor = null });
        using var page = await Render(red);
        int Px(int x, int yPdf) => page.Pixels.Span[(300 - yPdf) * page.Stride + x * 4 + 2];
        Assert.NotEqual(255, Px(140, 140) - page.Pixels.Span[(300 - 140) * page.Stride + 140 * 4 + 1]); // left half: not red any more
        Assert.Equal(255, Px(220, 140));  // right half still red
    }

    [Fact]
    public async Task StencilMasks_KeepPaintingOutsideTheMark_Only()
    {
        // A 16 x 16 stencil, every sample painting (0 with the default /Decode), drawn in red.
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ImageMask true /BitsPerComponent 1", new byte[32], flate: true);
        b.AddPage("1 0 0 rg q 160 0 0 160 100 60 cm /M0 Do Q", $"<< /XObject << /M0 {mask} 0 R >> >>", mediaBox: "[0 0 400 300]");
        byte[] red = await Redact(b.Build(), new[] { new PdfRedactionArea(1, new PdfRect(100, 60, 80, 160)) }, new PdfRedactionOptions { FillColor = null });
        using var page = await Render(red);
        (int R, int G) Px(int x, int yPdf) { int i = (300 - yPdf) * page.Stride + x * 4; return (page.Pixels.Span[i + 2], page.Pixels.Span[i + 1]); }
        Assert.Equal((255, 255), Px(140, 140));  // under the mark: nothing painted
        Assert.Equal((255, 0), Px(220, 140));    // outside: still painted in the fill colour
    }

    [Fact]
    public async Task PartlyCoveredAnnotations_KeepGoing_WithTheirAppearanceRedacted()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int ap = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 300 30] /Resources << /Font << /F1 {font} 0 R >> >>",
            "BT /F1 12 Tf 5 10 Td (Note: SECRET plan here) Tj ET");
        int annot = b.Add($"<< /Type /Annot /Subtype /FreeText /Rect [40 200 340 230] /Contents (a comment) /DA (/F1 12 Tf) /AP << /N {ap} 0 R >> >>");
        b.AddPage("", mediaBox: "[0 0 400 300]", extra: $"/Annots [{annot} 0 R]");
        var rect = Around(45, "Note: ", "SECRET", 210, 12);
        byte[] red = await Redact(b.Build(), new[] { new PdfRedactionArea(1, rect) });
        AssertAbsent(red, "SECRET");
        Assert.True(Present(red, "plan here"), "the rest of the annotation's text stays");
        using var doc = await PdfVectorDocument.OpenAsync(red);
        Assert.Single((PdfArray)doc.Resolver.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!);
    }

    [Fact]
    public async Task Output_HasNoEarlierRevisions_AndTheOverlayIsDrawn()
    {
        byte[] original = TextPage("BT /F1 12 Tf 40 200 Td (Hello SECRET world) Tj ET");
        byte[] filled;
        using (var doc = await PdfVectorDocument.OpenAsync(original))
            filled = PdfIncrementalWriter.Append(original, doc, new Dictionary<int, PdfObject>()); // a second revision
        var rect = Around(40, "Hello ", "SECRET", 200, 12);
        byte[] red = await Redact(filled, new[] { new PdfRedactionArea(1, rect) }, new PdfRedactionOptions { OverlayText = "Redacted" });
        Assert.Equal(1, Regex.Matches(Encoding.Latin1.GetString(red), "%%EOF").Count);
        Assert.DoesNotContain("/Prev", Encoding.Latin1.GetString(red));
        Assert.Contains("(Redacted) Tj", AllDecodedStreams(red));
        using var page = await Render(red);
        // A corner of the box, clear of the white label in its middle.
        int cx = (int)Math.Ceiling(rect.X + 1), cy = 300 - (int)Math.Floor(rect.Y + rect.Height - 1);
        Assert.True(page.Pixels.Span[cy * page.Stride + cx * 4 + 1] < 60, "the box is filled black");
        string text = await PdfiumText(red);
        Assert.Contains("Redacted", text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public async Task EncryptedDocuments_StayEncrypted()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        b.AddPage("BT /F1 12 Tf 40 200 Td (Hello SECRET world) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 300]");
        byte[] pdf = b.Build(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "user", "owner"));
        var rect = Around(40, "Hello ", "SECRET", 200, 12);
        byte[] red = await Redact(pdf, new[] { new PdfRedactionArea(1, rect) }, password: "user");
        Assert.Contains("/Encrypt", Encoding.Latin1.GetString(red));
        string text = await PdfiumText(red, password: "user");
        Assert.Contains("world", text);
        Assert.DoesNotContain("SECRET", text);
        await Assert.ThrowsAnyAsync<Exception>(() => PdfiumText(red, password: "wrong"));
    }

    private static async Task<PdfEngine.Rendering.RenderedPage> Render(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, CancellationToken.None);
        return result.Page;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
