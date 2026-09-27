using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Editing a page's text and images: paragraphs are found and reflow when edited, keeping their
/// fonts, width and alignment; untouched lines keep their exact glyphs; characters the fonts
/// lack come from an embedded installed font; images move, resize, turn, get replaced or go.
/// </summary>
public class ContentEditingTests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static byte[] Page(string content, string? xobjects = null, string extra = "", Func<VectorPdfBuilder, string>? more = null)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        string res = $"/Font << /F1 {font} 0 R >>";
        if (more != null) res += more(b);
        return AddPage(b, content, res, extra);
    }

    private static byte[] AddPage(VectorPdfBuilder b, string content, string res, string extra = "")
    {
        b.AddPage(content, $"<< {res} >>", mediaBox: "[0 0 400 400]", extra: extra);
        return b.Build();
    }

    /// <summary>A three-line left-aligned paragraph at 12 pt with 14 pt leading, and a heading above it.</summary>
    private const string Paragraph =
        "BT /F1 18 Tf 40 360 Td (Quarterly report) Tj ET\n" +
        "BT /F1 12 Tf 14 TL 40 320 Td (The committee reviewed the budget) Tj T* (and agreed to fund the new) Tj T* (construction next spring.) Tj ET\n";

    private static async Task<PdfEditablePage> Read(byte[] pdf, int page = 1)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfContentEditor.Read(doc, page);
    }

    private static async Task<PdfContentEditResult> Edit(byte[] pdf, params PdfContentEdit[] edits)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfContentEditor.Apply(doc, pdf, edits);
    }

    private static async Task<string> PdfiumText(byte[] pdf, int page = 1)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, page);
    }

    private static string Squash(string s) => string.Join(' ', s.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Every glyph the vector engine draws: text and baseline origin.</summary>
    private static async Task<List<(string Ch, double X, double Y)>> Glyphs(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var result = new List<(string, double, double)>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
            foreach (var g in cmd.Run.Glyphs)
            {
                var p = cmd.Run.TextToPage.Transform(g.OffsetX, g.OffsetY);
                result.Add((g.Unicode ?? "?", p.X, p.Y));
            }
        return result;
    }

    private static async Task<RenderedPage> Render(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        return (await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, CancellationToken.None)).Page;
    }

    [Fact]
    public async Task Paragraphs_AreFound_WithTheirWidthAlignmentAndLines()
    {
        var page = await Read(Page(Paragraph));
        Assert.Equal(2, page.Texts.Count);
        var heading = page.Texts.Single(t => t.Text == "Quarterly report");
        Assert.Equal(18, heading.FontSize, 3);
        var para = page.Texts.Single(t => t.Text.StartsWith("The committee", StringComparison.Ordinal));
        Assert.Equal("The committee reviewed the budget and agreed to fund the new construction next spring.", para.Text);
        Assert.Equal(3, para.LineBounds.Count);
        Assert.Equal(14, para.LineSpacing, 3);
        Assert.Equal(PdfTextAlignment.Left, para.Alignment);
        Assert.Equal("Helvetica", para.FontName);
        Assert.InRange(para.Bounds.X, 39, 41);
    }

    [Fact]
    public async Task HardLineBreaks_AreKept()
    {
        // An address: each line far shorter than the next word would need.
        var page = await Read(Page("BT /F1 12 Tf 14 TL 40 320 Td (Jane Doe, Director of Operations) Tj T* (12 High St) Tj T* (Springfield) Tj ET"));
        Assert.Equal("Jane Doe, Director of Operations\n12 High St\nSpringfield", Assert.Single(page.Texts).Text);
    }

    [Fact]
    public async Task ReplacingAWord_KeepsTheLinesBeforeAndAfterExactly()
    {
        byte[] pdf = Page(Paragraph);
        var para = (await Read(pdf)).Texts.Single(t => t.Text.StartsWith("The", StringComparison.Ordinal));
        var before = await Glyphs(pdf);
        var result = await Edit(pdf, new PdfReplaceText(1, para.Id, para.Text.Replace("fund", "build")));
        Assert.Empty(result.EmbeddedFonts); // Helvetica has every letter
        string text = Squash(await PdfiumText(result.Bytes));
        Assert.Contains("and agreed to build the new", text);
        Assert.DoesNotContain("fund", text);

        var after = await Glyphs(result.Bytes);
        // The first and third lines are the same glyphs at the same places.
        foreach (double y in new[] { 320.0, 292.0 })
        {
            var a = before.Where(g => Math.Abs(g.Y - y) < 0.5).ToList();
            var b = after.Where(g => Math.Abs(g.Y - y) < 0.5).ToList();
            Assert.Equal(a.Select(g => g.Ch), b.Select(g => g.Ch));
            Assert.All(a.Zip(b), p => Assert.Equal(p.First.X, p.Second.X, 3));
        }
        // The edited line starts where it did, on its baseline.
        var edited = after.Where(g => Math.Abs(g.Y - 306) < 0.5).OrderBy(g => g.X).ToList();
        Assert.Equal(40, edited[0].X, 2);
        Assert.Equal("andagreedtobuildthenew", string.Concat(edited.Select(g => g.Ch)).Replace(" ", ""));
    }

    [Fact]
    public async Task LongerText_Reflows_WithinTheParagraphsWidth_AndMovesTheRestDown()
    {
        byte[] pdf = Page(Paragraph + "BT /F1 12 Tf 40 200 Td (Footer) Tj ET");
        var para = (await Read(pdf)).Texts.Single(t => t.Text.StartsWith("The", StringComparison.Ordinal));
        string longer = para.Text.Replace("the budget", "the budget for the coming year in great detail");
        var result = await Edit(pdf, new PdfReplaceText(1, para.Id, longer));
        Assert.Equal(Squash(longer), Squash(string.Join(' ', (await PdfiumText(result.Bytes)).Split('\n').Where(l => !l.Contains("Quarterly") && !l.Contains("Footer")))));

        var edited = (await Read(result.Bytes)).Texts.Single(t => t.Text.StartsWith("The", StringComparison.Ordinal));
        Assert.True(edited.LineBounds.Count > 3, "the paragraph grew by a line");
        double right = para.Bounds.X + para.Bounds.Width;
        Assert.All(edited.LineBounds, l => Assert.True(l.X + l.Width <= right + 1, $"a line ends at {l.X + l.Width}, past {right}"));
        Assert.All(edited.LineBounds, l => Assert.InRange(l.X, 39, 41));
        // Lines 14 pt apart all the way down.
        var baselines = (await Glyphs(result.Bytes)).Where(g => g.Y < 330 && g.Y > 210).Select(g => Math.Round(g.Y, 1)).Distinct().OrderByDescending(y => y).ToList();
        for (int i = 1; i < baselines.Count; i++) Assert.Equal(14, baselines[i - 1] - baselines[i], 1);
        // Other text stays where it was.
        Assert.Contains(await Glyphs(result.Bytes), g => g.Ch == "F" && Math.Abs(g.X - 40) < 0.01 && Math.Abs(g.Y - 200) < 0.01);
    }

    [Fact]
    public async Task CharactersTheFontLacks_ComeFromAnEmbeddedInstalledFont()
    {
        byte[] pdf = Page(Paragraph);
        var heading = (await Read(pdf)).Texts.Single(t => t.Text == "Quarterly report");
        var result = await Edit(pdf, new PdfReplaceText(1, heading.Id, "Quarterly report Ω → ✓"));
        Assert.NotEmpty(result.EmbeddedFonts);
        string text = await PdfiumText(result.Bytes);
        Assert.Contains("Ω", text);
        Assert.Contains("✓", text);
        Assert.Contains("Quarterly report", text);
        // The subset is small: only the glyphs used.
        Assert.True(result.Bytes.Length < pdf.Length + 120_000, $"the edit added {result.Bytes.Length - pdf.Length} bytes");
    }

    [Fact]
    public async Task AddedText_CanBeEditedAgain_UsingItsOwnGlyphsAndTheInstalledFontForNewOnes()
    {
        byte[] pdf = Page(Paragraph);
        var added = await Edit(pdf, new PdfAddText(1, new PdfPoint(40, 100), "Hello there", new PdfTextFormat("Arial", 14, Red: 0.8)));
        Assert.Contains("Arial", Assert.Single(added.EmbeddedFonts));
        var text = (await Read(added.Bytes)).Texts.Single(t => t.Text == "Hello there");
        Assert.Equal(14, text.FontSize, 2);
        Assert.Equal(0.8, text.Color[0], 3);
        Assert.Equal("Arial", text.FontFamily ?? "Arial");

        var again = await Edit(added.Bytes, new PdfReplaceText(1, text.Id, "Hello there, world"));
        Assert.Contains("Hello there, world", await PdfiumText(again.Bytes));
        var glyphs = await Glyphs(again.Bytes);
        Assert.Equal(40, glyphs.Where(g => Math.Abs(g.Y - 100) < 0.5).Min(g => g.X), 2);
    }

    [Fact]
    public async Task MovingAParagraph_MovesEveryGlyphExactly()
    {
        // Kerned text (TJ adjustments) must keep its spacing.
        byte[] pdf = Page("BT /F1 12 Tf 60 300 Td [(W) 80 (AVE) -250 (to) 40 (day)] TJ ET");
        var text = Assert.Single((await Read(pdf)).Texts);
        var before = await Glyphs(pdf);
        var result = await Edit(pdf, new PdfTransformContent(1, PdfEditTarget.Text, text.Id, PdfMatrix.CreateTranslation(25, -110)));
        var after = await Glyphs(result.Bytes);
        Assert.Equal(before.Count, after.Count);
        foreach (var (a, b) in before.Zip(after))
        {
            Assert.Equal(a.Ch, b.Ch);
            Assert.Equal(a.X + 25, b.X, 3);
            Assert.Equal(a.Y - 110, b.Y, 3);
        }
    }

    [Fact]
    public async Task DeletingAParagraph_RemovesItsText()
    {
        byte[] pdf = Page(Paragraph);
        var heading = (await Read(pdf)).Texts.Single(t => t.Text == "Quarterly report");
        var result = await Edit(pdf, new PdfDeleteContent(1, PdfEditTarget.Text, heading.Id));
        string text = await PdfiumText(result.Bytes);
        Assert.DoesNotContain("Quarterly", text);
        Assert.Contains("committee", text);
    }

    [Fact]
    public async Task CentredText_StaysCentred()
    {
        static double W(string t) => t.Sum(c => PdfEngine.Vector.Fonts.Standard14Fonts.TryGetWidth("Helvetica", c, out double w) ? w : 0) / 1000 * 12;
        static string X(string t) => (200 - W(t) / 2).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        // Two lines centred on x = 200.
        byte[] pdf = Page($"BT /F1 12 Tf 1 0 0 1 {X("A centred title")} 300 Tm (A centred title) Tj ET BT /F1 12 Tf 1 0 0 1 {X("subtitle")} 286 Tm (subtitle) Tj ET");
        var text = Assert.Single((await Read(pdf)).Texts);
        Assert.Equal(PdfTextAlignment.Center, text.Alignment);
        var result = await Edit(pdf, new PdfReplaceText(1, text.Id, text.Text.Replace("subtitle", "the subtitle")));
        var line = (await Glyphs(result.Bytes)).Where(g => Math.Abs(g.Y - 286) < 0.5).ToList();
        Assert.Equal(200 - W("the subtitle") / 2, line.Min(g => g.X), 1);
    }

    [Fact]
    public async Task JustifiedText_FillsItsWidth_WhenReflowed()
    {
        static double W(string t) => t.Sum(c => PdfEngine.Vector.Fonts.Standard14Fonts.TryGetWidth("Helvetica", c, out double w) ? w : 0) / 1000 * 10;
        var lines = new[] { "Lorem ipsum dolor sit amet, consectetur", "adipiscing elit, sed do eiusmod tempor", "incididunt ut labore et dolore magna", "aliqua." };
        const double width = 200;
        // Every line but the last spread to exactly 200 pt by word spacing (Tw), as a justifying typesetter does.
        var content = new System.Text.StringBuilder("BT /F1 10 Tf 12 TL 40 300 Td ");
        for (int i = 0; i < lines.Length; i++)
        {
            double tw = i < lines.Length - 1 ? (width - W(lines[i])) / lines[i].Count(c => c == ' ') : 0;
            if (i > 0) content.Append("T* ");
            content.Append(tw.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(" Tw (").Append(lines[i]).Append(") Tj ");
        }
        content.Append("ET");
        byte[] pdf = Page(content.ToString());
        var text = Assert.Single((await Read(pdf)).Texts);
        Assert.Equal(PdfTextAlignment.Justified, text.Alignment);

        var result = await Edit(pdf, new PdfReplaceText(1, text.Id, text.Text.Replace("dolor sit", "dolor et magna sit")));
        Assert.Contains("dolor et magna sit", Squash(await PdfiumText(result.Bytes)));
        // The lines the edit reflowed still end at the right edge; the paragraph's last line does not stretch.
        var edited = (await Read(result.Bytes)).Texts.Single();
        var bounds = edited.LineBounds.ToList();
        for (int i = 0; i < bounds.Count - 1; i++) Assert.Equal(240, bounds[i].X + bounds[i].Width, 0);
        Assert.True(bounds[^1].X + bounds[^1].Width < 230, "the last line is not stretched");
        Assert.All(bounds, b => Assert.Equal(40, b.X, 0));
    }

    private static byte[] RedSquare(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) rgb[i * 3] = 255;
        return rgb;
    }

    private static byte[] ImagePage()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        var blue = new byte[4 * 4 * 3];
        for (int i = 0; i < 16; i++) blue[i * 3 + 2] = 255;
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8", blue, flate: true);
        return AddPage(b, "q 100 0 0 80 50 250 cm /Im1 Do Q BT /F1 12 Tf 50 100 Td (Caption) Tj ET", $"/Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >>");
    }

    private static (int R, int G, int B) Px(RenderedPage page, int x, int yPdf)
    {
        int i = (400 - yPdf) * page.Stride + x * 4;
        var s = page.Pixels.Span;
        return (s[i + 2], s[i + 1], s[i]);
    }

    [Fact]
    public async Task Images_AreFound_AndMoveAndResizeWhereTheyAreDrawn()
    {
        byte[] pdf = ImagePage();
        var image = Assert.Single((await Read(pdf)).Images);
        Assert.Equal(new PdfRect(50, 250, 100, 80), image.Bounds);
        Assert.Equal((4, 4), (image.PixelWidth, image.PixelHeight));

        // Twice as wide, moved right by 150.
        var t = PdfMatrix.CreateTranslation(-50, -250) * PdfMatrix.CreateScale(2, 1) * PdfMatrix.CreateTranslation(200, 250);
        var result = await Edit(pdf, new PdfTransformContent(1, PdfEditTarget.Image, image.Id, t));
        var moved = Assert.Single((await Read(result.Bytes)).Images);
        Assert.Equal(200, moved.Bounds.X, 3);
        Assert.Equal(200, moved.Bounds.Width, 3);
        using var page = await Render(result.Bytes);
        Assert.Equal((255, 255, 255), Px(page, 100, 290)); // where it was
        Assert.True(Px(page, 380, 290).B > 200 && Px(page, 380, 290).R < 60, "it is drawn where it went");
        Assert.Contains("Caption", await PdfiumText(result.Bytes));
    }

    [Fact]
    public async Task Images_AreReplaced_KeepingTheNewProportions_OrDeleted()
    {
        byte[] pdf = ImagePage();
        var image = Assert.Single((await Read(pdf)).Images);
        // A 2:1 picture in the 100 x 80 box: 100 wide, 50 high, centred.
        var replaced = await Edit(pdf, new PdfReplaceImage(1, image.Id, new PdfImageContent(20, 10, RedSquare(20, 10), PdfImageEncoding.Rgb)));
        var now = Assert.Single((await Read(replaced.Bytes)).Images);
        Assert.Equal(new PdfRect(50, 265, 100, 50), new PdfRect(Math.Round(now.Bounds.X, 3), Math.Round(now.Bounds.Y, 3), Math.Round(now.Bounds.Width, 3), Math.Round(now.Bounds.Height, 3)));
        using (var page = await Render(replaced.Bytes))
        {
            Assert.True(Px(page, 100, 290).R > 200 && Px(page, 100, 290).B < 60, "the new image is drawn");
            Assert.Equal((255, 255, 255), Px(page, 100, 255)); // below it: nothing left of the old one
        }

        var deleted = await Edit(pdf, new PdfDeleteContent(1, PdfEditTarget.Image, image.Id));
        Assert.Empty((await Read(deleted.Bytes)).Images);
        using var blank = await Render(deleted.Bytes);
        Assert.Equal((255, 255, 255), Px(blank, 100, 290));
    }

    [Fact]
    public async Task NewImages_AreAdded_WithTransparency()
    {
        byte[] pdf = ImagePage();
        var alpha = new byte[20 * 10];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)(i % 20 < 10 ? 255 : 0); // left half opaque
        var result = await Edit(pdf, new PdfAddImage(1, new PdfRect(200, 50, 100, 50), new PdfImageContent(20, 10, RedSquare(20, 10), PdfImageEncoding.Rgb, alpha)));
        Assert.Equal(2, (await Read(result.Bytes)).Images.Count);
        using var page = await Render(result.Bytes);
        Assert.True(Px(page, 225, 75).R > 200 && Px(page, 225, 75).G < 60, "opaque half is red");
        Assert.Equal((255, 255, 255), Px(page, 275, 75)); // transparent half shows the page
    }

    [Fact]
    public async Task TextInAnEncryptedDocument_IsEdited_AndStaysEncrypted()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        b.AddPage("BT /F1 12 Tf 40 300 Td (Confidential draft) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner"));
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, pdf, new[] { new PdfReplaceText(1, text.Id, "Final version") });
        Assert.Contains("Final version", await PdfiumText(result.Bytes));
        Assert.DoesNotContain("Final version", System.Text.Encoding.Latin1.GetString(result.Bytes));
    }

    /// <summary>A form XObject (its own font and a blue image) drawn twice: once 200 pt up, once where it is.</summary>
    private static byte[] FormPage()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        var blue = new byte[4 * 4 * 3];
        for (int i = 0; i < 16; i++) blue[i * 3 + 2] = 255;
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8", blue, flate: true);
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 400 200] /Matrix [1 0 0 1 20 0] /Resources << /Font << /F7 {font} 0 R >> /XObject << /Im7 {image} 0 R >> >>",
            "BT /F7 12 Tf 10 150 Td (Inside the form) Tj ET q 50 0 0 40 10 50 cm /Im7 Do Q", flate: true);
        return AddPage(b, "q 1 0 0 1 0 200 cm /Fm1 Do Q /Fm1 Do BT /F1 12 Tf 40 20 Td (On the page) Tj ET", $"/Font << /F1 {font} 0 R >> /XObject << /Fm1 {form} 0 R >>");
    }

    [Fact]
    public async Task TextAndImagesInFormXObjects_AreFound_WhereEachDrawingPutsThem()
    {
        var page = await Read(FormPage());
        var inside = page.Texts.Where(t => t.Text == "Inside the form").OrderByDescending(t => t.Bounds.Y).ToList();
        Assert.Equal(2, inside.Count);
        Assert.Equal(30, inside[0].Bounds.X, 0);
        Assert.InRange(inside[0].Bounds.Y, 340, 352);
        Assert.InRange(inside[1].Bounds.Y, 140, 152);
        Assert.Single(page.Texts, t => t.Text == "On the page");
        var images = page.Images.OrderByDescending(i => i.Bounds.Y).ToList();
        Assert.Equal(new PdfRect(30, 250, 50, 40), images[0].Bounds);
        Assert.Equal(new PdfRect(30, 50, 50, 40), images[1].Bounds);
    }

    [Fact]
    public async Task EditingInsideAFormXObject_ChangesOnlyThatDrawing()
    {
        byte[] pdf = FormPage();
        var page = await Read(pdf);
        var upper = page.Texts.Where(t => t.Text == "Inside the form").OrderByDescending(t => t.Bounds.Y).First();
        var images = page.Images.OrderByDescending(i => i.Bounds.Y).ToList();
        var result = await Edit(pdf,
            new PdfReplaceText(1, upper.Id, "Edited in the form Ω"), // Helvetica has no omega: an installed font draws it
            new PdfDeleteContent(1, PdfEditTarget.Image, images[1].Id),
            new PdfTransformContent(1, PdfEditTarget.Image, images[0].Id, PdfMatrix.CreateTranslation(100, 0)));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("no ", StringComparison.OrdinalIgnoreCase));

        string text = Squash(await PdfiumText(result.Bytes));
        Assert.Contains("Edited in the form Ω", text);
        Assert.Contains("Inside the form", text); // the other drawing is as it was
        Assert.Contains("On the page", text);
        var after = await Read(result.Bytes);
        Assert.Single(after.Texts, t => t.Text == "Inside the form");
        Assert.Single(after.Texts, t => t.Text.StartsWith("Edited in the form", StringComparison.Ordinal));
        var image = Assert.Single(after.Images);
        Assert.Equal(new PdfRect(130, 250, 50, 40), new PdfRect(Math.Round(image.Bounds.X, 3), Math.Round(image.Bounds.Y, 3), Math.Round(image.Bounds.Width, 3), Math.Round(image.Bounds.Height, 3)));

        using var rendered = await Render(result.Bytes);
        Assert.Equal((255, 255, 255), Px(rendered, 55, 70));  // the deleted image
        Assert.Equal((255, 255, 255), Px(rendered, 55, 270)); // where the moved one was
        Assert.True(Px(rendered, 155, 270).B > 200 && Px(rendered, 155, 270).R < 60, "the moved image is drawn where it went");

        // Editing it again reads the copy.
        var edited = after.Texts.Single(t => t.Text.StartsWith("Edited in the form", StringComparison.Ordinal));
        var again = await Edit(result.Bytes, new PdfReplaceText(1, edited.Id, "Second edit"));
        string twice = Squash(await PdfiumText(again.Bytes));
        Assert.Contains("Second edit", twice);
        Assert.DoesNotContain("Edited", twice);
        Assert.Contains("Inside the form", twice);
    }
}
