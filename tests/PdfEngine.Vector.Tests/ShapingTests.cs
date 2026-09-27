using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Shaping new text: bidirectional reordering, Arabic joining forms, required ligatures and
/// standard ligatures, so right-to-left and cursive scripts are drawn as they are written.
/// </summary>
public class ShapingTests
{
    private static TrueTypeFontFile? Installed(string file)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
        return File.Exists(path) ? TrueTypeFontFile.TryLoad(File.ReadAllBytes(path)) : null;
    }

    private static string Visual(string logical)
    {
        var levels = Bidi.Levels(logical);
        return new string(Bidi.Reorder(logical.Select((c, i) => (c, Level: levels[i])).ToList(), x => x.Level).Select(x => x.c).ToArray());
    }

    [Fact]
    public void Bidi_ReversesRightToLeftRuns_AndKeepsNumbersLeftToRight()
    {
        Assert.Equal("abc גבא def", Visual("abc אבג def"));
        Assert.Equal("123 גבא", Visual("אבג 123")); // a right-to-left paragraph: the number stays in reading order
        Assert.Equal("fed גבא", Visual("אבג fed"));
        Assert.Equal(1, Bidi.ParagraphLevel("שלום world"));
        Assert.Equal(0, Bidi.ParagraphLevel("hello שלום"));
    }

    [Fact]
    public void ArabicJoining_GivesEachLetterItsForm()
    {
        Assert.Equal(new[] { "init", "medi", "fina" }, ArabicJoining.Forms("بيت"));
        Assert.Equal(new[] { "isol", "isol", "isol" }, ArabicJoining.Forms("دار")); // right-joining letters never join the next one
        var withMark = ArabicJoining.Forms("بَت"); // a vowel mark between does not break the join
        Assert.Equal("init", withMark[0]);
        Assert.Null(withMark[1]);
        Assert.Equal("fina", withMark[2]);
    }

    [Fact]
    public void ArabicText_IsShapedWithJoiningFormsAndLamAlef_InDrawingOrder()
    {
        if (Installed("arial.ttf") is not { } arial) return;
        var shaped = TextShaper.ShapeLine("بيت", _ => arial, f => f);
        Assert.Equal(3, shaped.Count);
        // Drawn left to right: the last letter first.
        Assert.Equal("ت", shaped[0].Glyph.Text);
        Assert.Equal("ب", shaped[2].Glyph.Text);
        Assert.NotEqual(arial.GlyphFor('ب'), shaped[2].Glyph.Gid); // its initial form, not the isolated one
        Assert.True(shaped.All(g => (g.Glyph.Level & 1) == 1));

        var lamAlef = TextShaper.ShapeLine("لا", _ => arial, f => f);
        var glyph = Assert.Single(lamAlef);
        Assert.Equal("لا", glyph.Glyph.Text); // one glyph for both letters, and it still reads as both
    }

    [Fact]
    public void Ligatures_JoinTheirLetters_WhenTheFontHasThem()
    {
        foreach (string file in new[] { "calibri.ttf", "candara.ttf", "constan.ttf", "corbel.ttf", "segoeui.ttf" })
        {
            if (Installed(file) is not { } font) continue;
            var shaped = TextShaper.ShapeLine("office", _ => font, f => f);
            if (shaped.Count == 6) continue; // this font has no standard ligature for it
            Assert.Equal("office", string.Concat(shaped.Select(g => g.Glyph.Text)));
            Assert.Contains(shaped, g => g.Glyph.Text.Length > 1);
            return;
        }
    }

    [Fact]
    public async Task AddedArabicText_IsShaped_AndReadsBack()
    {
        if (Installed("arial.ttf") == null) return;
        var b = new VectorPdfBuilder();
        b.AddPage("", "<< >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfAddText(1, new PdfPoint(40, 200), "مرحبا بالعالم", new PdfTextFormat("Arial", 24)) });
        Assert.Empty(result.Warnings);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(result.Bytes, null);
        string text = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
        Assert.Contains("مرحبا", text);
        Assert.Contains("بالعالم", text);
    }

    [Fact]
    public async Task ArabicWordsTypedIntoAParagraph_AreShaped_AndDrawnRightToLeft()
    {
        if (Installed("arial.ttf") == null) return;
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 12 Tf 40 300 Td (Meeting notes for today) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "Meeting notes مرحبا بالعالم today") });

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(result.Bytes, null);
        string read = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
        Assert.Contains("مرحبا", read);
        Assert.Contains("بالعالم", read);
        Assert.Contains("Meeting notes", read);

        using var after = await PdfVectorDocument.OpenAsync(result.Bytes);
        var list = await after.GetPageDisplayListAsync(1);
        var glyphs = list.Commands.OfType<DrawGlyphRun>().SelectMany(c => c.Run.Glyphs.Select(g => (Text: g.Unicode ?? "", X: c.Run.TextToPage.Transform(g.OffsetX, g.OffsetY).X))).ToList();
        double X(string ch) => glyphs.Single(g => g.Text == ch).X;
        Assert.True(X("ح") > glyphs.Where(g => g.Text == "ع").Max(g => g.X), "the first word read is to the right of the second");
        Assert.True(X("ر") > X("ح"), "a word's letters are drawn right to left");
        Assert.True(X("y") > X("ر"), "the words after it follow on the right");
    }

    [Fact]
    public async Task RightToLeftTextOnAPage_IsReadInReadingOrder_AndEditedThatWay()
    {
        if (Installed("arial.ttf") == null) return;
        var b = new VectorPdfBuilder();
        b.AddPage("", "<< >>", mediaBox: "[0 0 400 400]");
        byte[] blank = b.Build();
        byte[] pdf;
        using (var doc = await PdfVectorDocument.OpenAsync(blank))
            pdf = PdfContentEditor.Apply(doc, blank, new PdfContentEdit[] { new PdfAddText(1, new PdfPoint(40, 200), "مرحبا بالعالم (1)", new PdfTextFormat("Arial", 20)) }).Bytes;

        using var drawn = await PdfVectorDocument.OpenAsync(pdf);
        var text = Assert.Single(PdfContentEditor.Read(drawn, 1).Texts);
        Assert.Equal("مرحبا بالعالم (1)", text.Text);

        var edited = PdfContentEditor.Apply(drawn, pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "مرحبا يا صديقي (1)") });
        using var after = await PdfVectorDocument.OpenAsync(edited.Bytes);
        Assert.Equal("مرحبا يا صديقي (1)", Assert.Single(PdfContentEditor.Read(after, 1).Texts).Text);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(edited.Bytes, null);
        string read = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
        Assert.Contains("صديقي", read);
        Assert.Contains("مرحبا", read);
    }
}
