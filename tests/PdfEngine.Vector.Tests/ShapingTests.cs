using System;
using System.Collections.Generic;
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

    private static byte[] U16(params int[] values) => values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray();

    /// <summary>
    /// A GSUB whose 'calt' feature runs one chained contextual lookup (format 3): glyph 5 followed by
    /// glyph 7 becomes glyph 9, through a nested single substitution.
    /// </summary>
    private static byte[] ChainedGsub()
    {
        var scriptList = U16(1).Concat("DFLT"u8.ToArray()).Concat(U16(8)).Concat(U16(4, 0)).Concat(U16(0, 0xFFFF, 1, 0)).ToArray();
        var featureList = U16(1).Concat("calt"u8.ToArray()).Concat(U16(8)).Concat(U16(0, 1, 0)).ToArray();
        var chain = U16(3, 0, 1, 18, 1, 24, 1, 0, 1).Concat(U16(1, 1, 5)).Concat(U16(1, 1, 7)).ToArray();
        var lookup0 = U16(6, 0, 1, 8).Concat(chain).ToArray();
        var single = U16(2, 8, 1, 9).Concat(U16(1, 1, 5)).ToArray();
        var lookup1 = U16(1, 0, 1, 8).Concat(single).ToArray();
        var lookupList = U16(2, 6, 6 + lookup0.Length).Concat(lookup0).Concat(lookup1).ToArray();
        int features = 10 + scriptList.Length, lookups = features + featureList.Length;
        return U16(1, 0, 10, features, lookups).Concat(scriptList).Concat(featureList).Concat(lookupList).ToArray();
    }

    [Fact]
    public void ChainedContextualSubstitution_AppliesItsLookup_WhereTheSequenceMatches()
    {
        var gsub = OpenTypeSubstitution.Read(new Dictionary<string, byte[]> { ["GSUB"] = ChainedGsub() })!;
        var run = new List<ShapingGlyph> { new() { Gid = 5, Text = "a" }, new() { Gid = 7, Text = "b" }, new() { Gid = 5, Text = "a" }, new() { Gid = 8, Text = "c" } };
        gsub.Apply(run, "latn", new[] { "calt" });
        Assert.Equal(new[] { 9, 7, 5, 8 }, run.Select(g => g.Gid)); // only the 5 that a 7 follows
    }

    [Fact]
    public void ArabicMarks_SitOnTheirLetters_AndDoNotAdvance()
    {
        if (Installed("arial.ttf") is not { } arial || arial.Positioning == null) return;
        var shaped = TextShaper.ShapeLine("بَت", _ => arial, f => f); // beh, fatha, teh
        var fatha = shaped.Single(g => g.Glyph.Text == "َ").Glyph;
        Assert.Equal(0, fatha.Advance);
        // Moved onto the beh's anchor (the short initial beh brings it down a little from where it is designed).
        Assert.True(fatha.Dx != 0 || fatha.Dy != 0, "the fatha is placed on its anchor");
        Assert.InRange(fatha.Dy, -400, 400);
        // Drawing order: teh, beh, then the fatha over the beh.
        Assert.Equal("ت", shaped[0].Glyph.Text);
        Assert.Equal("ب", shaped[1].Glyph.Text);
    }
}
