using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Latin text, digits and other characters UAX #50 turns sideways in vertical writing: typed into a
/// column they are drawn turned a quarter clockwise with the font's horizontal advances, ideographs
/// stay upright, and sideways runs already on a page are read (and edited) as part of their column.
/// </summary>
public class VerticalSidewaysTests
{
    // Arial has no ideographs: its Greek capitals stand in for them, read as ideographs through the
    // ToUnicode map, so the column is laid out like Japanese text without a CJK font installed.
    private static readonly (char Glyph, string Text)[] Ideographs = { ('Δ', "日"), ('Θ', "本"), ('Λ', "語"), ('Ξ', "年"), ('Π', "月") };
    private const double Size = 16;

    private sealed record Fixture(byte[] Pdf, TrueTypeFontFile Font)
    {
        public double Width(char c) => Font.Advance(Font.GlyphFor(c)) / 1000.0 * Size;
    }

    private static string? ArialPath()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        return File.Exists(path) ? path : null;
    }

    private static int GlyphOf(TrueTypeFontFile font, string text)
    {
        foreach (var (glyph, ideograph) in Ideographs)
            if (ideograph == text) return font.GlyphFor(glyph);
        return font.GlyphFor(text[0]);
    }

    private static string Hex(TrueTypeFontFile font, string s) =>
        "<" + string.Concat(StringInfoElements(s).Select(e => GlyphOf(font, e).ToString("X4"))) + ">";

    private static IEnumerable<string> StringInfoElements(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) yield return (string)e.Current;
    }

    /// <summary>
    /// A page with Arial embedded once as a CID font (real horizontal widths), shown through an
    /// Identity-V Type0 font (F1) and the same CID font under Identity-H (F2), as producers draw
    /// sideways Latin text in a column.
    /// </summary>
    private static Fixture? Page(Func<TrueTypeFontFile, string> content)
    {
        if (ArialPath() is not { } path) return null;
        byte[] data = File.ReadAllBytes(path);
        var font = TrueTypeFontFile.TryLoad(data)!;
        var map = new List<(int Gid, string Text)>();
        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ") map.Add((font.GlyphFor(c), c.ToString()));
        foreach (var (glyph, text) in Ideographs) map.Add((font.GlyphFor(glyph), text));
        var b = new VectorPdfBuilder();
        int file = b.AddStream($"/Length1 {data.Length}", data, flate: true);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {file} 0 R >>");
        string widths = string.Join(" ", map.OrderBy(m => m.Gid).Select(m => $"{m.Gid} [{Math.Round(font.Advance(m.Gid)).ToString(CultureInfo.InvariantCulture)}]"));
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 1000 /W [{widths}] >>");
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Adobe-Identity-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        cmap.Append(map.Count).Append(" beginbfchar\n");
        foreach (var (gid, text) in map) cmap.Append($"<{gid:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream(string.Empty, cmap.ToString());
        int vertical = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-V /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        int horizontal = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        b.AddPage(content(font), $"<< /Font << /F1 {vertical} 0 R /F2 {horizontal} 0 R >> >>", mediaBox: "[0 0 400 400]");
        return new Fixture(b.Build(), font);
    }

    /// <summary>A column of ideographs at x 200 from y 350 down.</summary>
    private static Fixture? Column(string text) => Page(font => $"BT /F1 {Size} Tf 200 350 Td {Hex(font, text)} Tj ET");

    // A producer's sideways run (baseline 0.3 em left of the column's centre line, a little off the
    // font's own centring) between ideographs: 日本 upright, PDF turned, 語 upright.
    private const double ProducerBaseline = 200 - 0.3 * Size;
    private static Fixture? ColumnWithSidewaysRun() => Page(font =>
        $"BT /F1 {Size} Tf 200 350 Td {Hex(font, "日本")} Tj ET\n" +
        $"BT /F2 {Size} Tf 0 -1 1 0 {ProducerBaseline.ToString(CultureInfo.InvariantCulture)} 318 Tm {Hex(font, "PDF")} Tj ET\n" +
        $"BT /F1 {Size} Tf 200 {(318 - (font.Advance(font.GlyphFor('P')) + font.Advance(font.GlyphFor('D')) + font.Advance(font.GlyphFor('F'))) / 1000.0 * Size).ToString(CultureInfo.InvariantCulture)} Td {Hex(font, "語")} Tj ET");

    private sealed record Drawn(string Ch, double X, double Y, bool Sideways);

    private static async Task<List<Drawn>> Glyphs(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var result = new List<Drawn>();
        foreach (var cmd in list.Commands.OfType<DrawGlyphRun>())
        {
            var m = cmd.Run.TextToPage;
            foreach (var g in cmd.Run.Glyphs)
            {
                var p = m.Transform(g.OffsetX, g.OffsetY);
                // Turned a quarter clockwise: text space x runs down the page.
                result.Add(new Drawn(g.Unicode ?? "?", p.X, p.Y, Math.Abs(m.B) > Math.Abs(m.A) && m.B < 0));
            }
        }
        return result;
    }

    private static async Task<string> PdfiumText(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(pdoc, 1);
    }

    [Fact]
    public void Orientation_FollowsUax50()
    {
        Assert.Equal(VerticalOrientationClass.R, VerticalOrientation.Of('A'));
        Assert.Equal(VerticalOrientationClass.R, VerticalOrientation.Of('7'));
        Assert.Equal(VerticalOrientationClass.R, VerticalOrientation.Of('Ж'));
        Assert.Equal(VerticalOrientationClass.R, VerticalOrientation.Of('λ'));
        Assert.Equal(VerticalOrientationClass.R, VerticalOrientation.Of(' '));
        Assert.Equal(VerticalOrientationClass.U, VerticalOrientation.Of('年'));
        Assert.Equal(VerticalOrientationClass.U, VerticalOrientation.Of('あ'));
        Assert.Equal(VerticalOrientationClass.U, VerticalOrientation.Of('한'));
        Assert.Equal(VerticalOrientationClass.U, VerticalOrientation.Of('×'));
        Assert.Equal(VerticalOrientationClass.U, VerticalOrientation.Of(0x20B9F)); // an ideograph beyond the BMP
        Assert.Equal(VerticalOrientationClass.Tu, VerticalOrientation.Of('。'));
        Assert.Equal(VerticalOrientationClass.Tu, VerticalOrientation.Of('ょ'));
        Assert.Equal(VerticalOrientationClass.Tr, VerticalOrientation.Of('「'));
        Assert.Equal(VerticalOrientationClass.Tr, VerticalOrientation.Of('ー'));
        Assert.Equal(VerticalOrientationClass.Tr, VerticalOrientation.Of('（'));
        Assert.Equal(VerticalOrientationClass.U, VerticalOrientation.Of('Ａ')); // full-width letters stand upright
        Assert.Equal(VerticalOrientationClass.R, VerticalOrientation.Of('ｱ')); // half-width katakana turn
        Assert.True(VerticalOrientation.IsSideways("「", hasVerticalForm: false));
        Assert.False(VerticalOrientation.IsSideways("「", hasVerticalForm: true));
    }

    [Fact]
    public async Task TypingLatinIntoAColumn_TurnsItSideways_AndKeepsIdeographsUpright()
    {
        if (Column("日本語") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        Assert.True(text.Vertical);
        Assert.Equal("日本語", text.Text);

        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "PDF 2026年") });
        Assert.Empty(result.Warnings);
        Assert.Empty(result.EmbeddedFonts); // the column's own font, in its horizontal form

        var after = await Glyphs(result.Bytes);
        foreach (string ch in new[] { "P", "D", "F", "2", "0", "6" })
            Assert.All(after.Where(g => g.Ch == ch), g => Assert.True(g.Sideways, $"{ch} is turned sideways"));
        var year = Assert.Single(after, g => g.Ch == "年");
        Assert.False(year.Sideways);

        // Sideways glyphs follow each other down the column by their horizontal advances, from the
        // column's top, on a baseline that centres them across it (Arial's ascent and descent).
        var p = after.Single(g => g.Ch == "P");
        var d = after.Single(g => g.Ch == "D");
        Assert.Equal(350, p.Y, 2);
        Assert.Equal(fixture.Width('P'), p.Y - d.Y, 0.05);
        Assert.Equal(200 - (0.905 - 0.212) / 2 * Size, p.X, 2);
        var six = after.Single(g => g.Ch == "6");
        Assert.True(year.Y < six.Y - fixture.Width('6') + 0.01, "the ideograph follows the digits");
        Assert.InRange(year.X, 200 - Size, 200); // upright: centred on the column (its origin half its width left)

        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        var read = Assert.Single(PdfContentEditor.Read(again, 1).Texts);
        Assert.True(read.Vertical);
        Assert.Equal("PDF 2026年", read.Text);

        // Plain text extraction reads the column in its logical order.
        string extracted = new string((await PdfiumText(result.Bytes)).Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal("PDF2026年", extracted);
    }

    [Fact]
    public async Task ASidewaysRunInAColumn_IsReadAsPartOfTheColumn()
    {
        if (ColumnWithSidewaysRun() is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        Assert.True(text.Vertical);
        Assert.Equal("日本PDF語", text.Text);
        Assert.InRange(text.Bounds.X, 188, 193);
        Assert.InRange(text.Bounds.Y + text.Bounds.Height, 348, 352);
    }

    [Fact]
    public async Task ATurnedLabelAwayFromAnyColumn_StaysTextOfItsOwn()
    {
        var fixture = Page(font =>
            $"BT /F1 {Size} Tf 200 350 Td {Hex(font, "日本語")} Tj ET\n" +
            $"BT /F2 {Size} Tf 0 -1 1 0 50 300 Tm {Hex(font, "Label")} Tj ET");
        if (fixture == null) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var texts = PdfContentEditor.Read(doc, 1).Texts;
        Assert.Equal(2, texts.Count);
        Assert.True(texts.Single(t => t.Text == "日本語").Vertical);
        var label = texts.Single(t => t.Text == "Label");
        Assert.False(label.Vertical);
        Assert.Equal(-90, label.AngleDegrees, 3);
    }

    [Fact]
    public async Task EditingAColumn_KeepsItsSidewaysRun_AndRoundTrips()
    {
        if (ColumnWithSidewaysRun() is not { } fixture) return;
        var before = await Glyphs(fixture.Pdf);
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);

        // A letter inside the run and an ideograph after it: the letter turns with the run, on its
        // baseline; the ideograph stands upright in the column's font.
        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "日本PDXF月語") });
        Assert.Empty(result.Warnings);
        var after = await Glyphs(result.Bytes);
        Drawn At(List<Drawn> glyphs, string ch) => glyphs.Single(g => g.Ch == ch);

        // The run's glyphs before the change are where they were, the producer's baseline kept.
        foreach (string ch in new[] { "日", "本", "P", "D" })
        {
            Assert.Equal(At(before, ch).X, At(after, ch).X, 2);
            Assert.Equal(At(before, ch).Y, At(after, ch).Y, 2);
            Assert.Equal(At(before, ch).Sideways, At(after, ch).Sideways);
        }
        var x = At(after, "X");
        Assert.True(x.Sideways);
        Assert.Equal(ProducerBaseline, x.X, 2);
        Assert.Equal(At(after, "D").Y - fixture.Width('D'), x.Y, 0.05); // /W holds widths rounded to units
        Assert.Equal(x.Y - fixture.Width('X'), At(after, "F").Y, 0.05);
        var month = At(after, "月");
        Assert.False(month.Sideways);
        Assert.True(month.Y < At(after, "F").Y);
        Assert.True(At(after, "語").Y < month.Y);
        Assert.Equal(200, month.X + fixture.Width('Π') / 2, 0.05); // centred on the column

        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        var read = Assert.Single(PdfContentEditor.Read(again, 1).Texts);
        Assert.True(read.Vertical);
        Assert.Equal("日本PDXF月語", read.Text);

        // Again: a second edit of the edited column reads and writes it the same way.
        var second = PdfContentEditor.Apply(again, result.Bytes, new PdfContentEdit[] { new PdfReplaceText(1, read.Id, "日本PDF語") });
        using var third = await PdfVectorDocument.OpenAsync(second.Bytes);
        Assert.Equal("日本PDF語", Assert.Single(PdfContentEditor.Read(third, 1).Texts).Text);
        var last = await Glyphs(second.Bytes);
        Assert.Equal(At(before, "F").X, At(last, "F").X, 2);
        Assert.Equal(At(before, "F").Y, At(last, "F").Y, 2);
        Assert.Equal(At(before, "語").Y, At(last, "語").Y, 2);
    }

    [Fact]
    public async Task ExtractionReadsAColumnWithASidewaysRun_InLogicalOrder()
    {
        if (ColumnWithSidewaysRun() is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "日本PDF 2026年語") });
        string extracted = new string((await PdfiumText(result.Bytes)).Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal("日本PDF2026年語", extracted);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("日本PDF 2026年語", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }

    [Fact]
    public async Task CharactersTheColumnsFontLacks_ComeFromAnInstalledFont_UprightOrSideways()
    {
        if (Column("日本語") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        // × stands upright (UAX #50 U), € turns (R); neither is in the document's subset map.
        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "日×本€語") });
        Assert.Empty(result.Warnings);
        Assert.NotEmpty(result.EmbeddedFonts);
        var after = await Glyphs(result.Bytes);
        Assert.False(after.Single(g => g.Ch == "×").Sideways);
        Assert.True(after.Single(g => g.Ch == "€").Sideways);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        var read = Assert.Single(PdfContentEditor.Read(again, 1).Texts);
        Assert.True(read.Vertical);
        Assert.Equal("日×本€語", read.Text);
    }
}
