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
/// Tate-chu-yoko (horizontal in vertical): short runs of half-width digits in a column set upright,
/// side by side in one em of it, as Japanese typesetting does (JIS X 4051, W3C JLREQ). Typed runs
/// are set that way under the editing option, units already on a page are read as part of their
/// column and kept as they are, and extraction reads them in the column's order.
/// </summary>
public class TateChuYokoTests
{
    // Arial has no ideographs: its Greek capitals stand in for them, read as ideographs through the
    // ToUnicode map, so the column is laid out like Japanese text without a CJK font installed.
    private static readonly (char Glyph, string Text)[] Ideographs =
    {
        ('Δ', "日"), ('Θ', "本"), ('Ξ', "年"), ('Σ', "平"), ('Φ', "成"), ('Ψ', "令"), ('Ω', "和"), ('Γ', "度"),
    };
    private const double Size = 16;
    // Arial's ascent and descent: a horizontal glyph's middle above its baseline, in em.
    private const double Centre = (0.905 - 0.212) / 2;

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
        "<" + string.Concat(s.Select(c => GlyphOf(font, c.ToString()).ToString("X4"))) + ">";

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A page with Arial embedded once as a CID font, shown through an Identity-V Type0 font (F1)
    /// and the same CID font under Identity-H (F2), as producers draw tate-chu-yoko in a column.
    /// </summary>
    private static Fixture? Page(Func<TrueTypeFontFile, string> content)
    {
        if (ArialPath() is not { } path) return null;
        byte[] data = File.ReadAllBytes(path);
        var font = TrueTypeFontFile.TryLoad(data)!;
        var map = new List<(int Gid, string Text)>();
        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 !?") map.Add((font.GlyphFor(c), c.ToString()));
        foreach (var (glyph, text) in Ideographs) map.Add((font.GlyphFor(glyph), text));
        var b = new VectorPdfBuilder();
        int file = b.AddStream($"/Length1 {data.Length}", data, flate: true);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {file} 0 R >>");
        string widths = string.Join(" ", map.OrderBy(m => m.Gid).Select(m => $"{m.Gid} [{F(Math.Round(font.Advance(m.Gid)))}]"));
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
    private static Fixture? Column(string text) => Page(font => $"BT /F1 {F(Size)} Tf 200 350 Td {Hex(font, text)} Tj ET");

    // A producer's unit: 令和 upright, "06" side by side in the third slot (318 down to 302),
    // compressed to 90 % with Tz, centred on the column, then 年 upright.
    private const double UnitTz = 90;
    private static Fixture? ColumnWithUnit() => Page(font =>
    {
        double width = 2 * font.Advance(font.GlyphFor('0')) / 1000.0 * Size * UnitTz / 100;
        return $"BT /F1 {F(Size)} Tf 200 350 Td {Hex(font, "令和")} Tj ET\n" +
            $"BT /F2 {F(Size)} Tf {F(UnitTz)} Tz 1 0 0 1 {F(200 - width / 2)} {F(310 - Centre * Size)} Tm {Hex(font, "06")} Tj ET\n" +
            $"BT /F1 {F(Size)} Tf 200 302 Td {Hex(font, "年")} Tj ET";
    });

    private sealed record Drawn(string Ch, double X, double Y, bool Sideways, bool Upright, double ScaleX);

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
                result.Add(new Drawn(g.Unicode ?? "?", p.X, p.Y, Math.Abs(m.B) > Math.Abs(m.A) && m.B < 0, m.A > 0 && Math.Abs(m.B) < 1e-9, m.A * cmd.Run.HorizontalScaling / 100));
            }
        }
        return result;
    }

    private static Drawn At(List<Drawn> glyphs, string ch) => glyphs.Single(g => g.Ch == ch);

    private static async Task<string> PdfiumText(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf, null);
        string text = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
        return new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private static PdfContentEditResult Replace(PdfVectorDocument doc, byte[] pdf, string text, PdfTateChuYoko rule = PdfTateChuYoko.TwoDigits)
    {
        var block = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        return PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfReplaceText(1, block.Id, text) }, new PdfContentEditOptions { TateChuYoko = rule });
    }

    [Fact]
    public void TheDefaultRule_IsTwoDigits() => Assert.Equal(PdfTateChuYoko.TwoDigits, new PdfContentEditOptions().TateChuYoko);

    [Fact]
    public async Task TypingTwoDigitsIntoAColumn_SetsThemUprightInOneEm()
    {
        if (Column("平成年") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, Assert.Single(PdfContentEditor.Read(doc, 1).Texts).Id, "平成12年") });
        Assert.Empty(result.Warnings);
        Assert.Empty(result.EmbeddedFonts); // the column's own font, in its horizontal form

        var after = await Glyphs(result.Bytes);
        var one = At(after, "1");
        var two = At(after, "2");
        Assert.True(one.Upright && two.Upright, "the digits stand upright");
        // Side by side on one baseline that centres them in the third slot (318 down to 302).
        Assert.Equal(one.Y, two.Y, 3);
        Assert.Equal(310 - Centre * Size, one.Y, 2);
        // Wider than an em together, so compressed to exactly one, centred on the column.
        double k = Size / (fixture.Width('1') + fixture.Width('2'));
        Assert.Equal(k, one.ScaleX, 3);
        Assert.Equal(200 - Size / 2, one.X, 2);
        Assert.Equal(fixture.Width('1') * k, two.X - one.X, 2);
        // The column goes on one em further down.
        var year = At(after, "年");
        Assert.False(year.Sideways);
        Assert.True(year.Y < one.Y);
        Assert.Equal(At(after, "成").Y - 2 * Size, year.Y, 2);

        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        var read = Assert.Single(PdfContentEditor.Read(again, 1).Texts);
        Assert.True(read.Vertical);
        Assert.Equal("平成12年", read.Text);
        Assert.Equal("平成12年", await PdfiumText(result.Bytes));
    }

    [Fact]
    public async Task ASingleDigit_StandsUprightCentred()
    {
        if (Column("平成年") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var result = Replace(doc, fixture.Pdf, "平成1年");
        Assert.Empty(result.Warnings);
        var after = await Glyphs(result.Bytes);
        var one = At(after, "1");
        Assert.True(one.Upright);
        Assert.Equal(1, one.ScaleX, 3); // narrower than an em: not compressed
        Assert.Equal(200 - fixture.Width('1') / 2, one.X, 2);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("平成1年", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }

    [Fact]
    public async Task LongerDigitRuns_StaySideways_UnlessTheRuleAllowsThree()
    {
        if (Column("平成年") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);

        var four = await Glyphs(Replace(doc, fixture.Pdf, "平成2026年").Bytes);
        foreach (string ch in new[] { "2", "0", "6" })
            Assert.All(four.Where(g => g.Ch == ch), g => Assert.True(g.Sideways, $"{ch} is turned sideways"));

        var three = await Glyphs(Replace(doc, fixture.Pdf, "平成123年").Bytes);
        Assert.All(three.Where(g => g.Ch is "1" or "2" or "3"), g => Assert.True(g.Sideways));

        var result = Replace(doc, fixture.Pdf, "平成123年", PdfTateChuYoko.UpToThree);
        var upright = await Glyphs(result.Bytes);
        Assert.All(upright.Where(g => g.Ch is "1" or "2" or "3"), g => Assert.True(g.Upright));
        double k = Size / (fixture.Width('1') + fixture.Width('2') + fixture.Width('3'));
        Assert.Equal(k, At(upright, "1").ScaleX, 2); // Tz is written rounded
        Assert.Equal(200 + Size / 2, At(upright, "3").X + fixture.Width('3') * k, 2);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("平成123年", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }

    [Fact]
    public async Task ExclamationAndQuestionMarks_FormAUnitToo()
    {
        if (Column("日本") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var result = Replace(doc, fixture.Pdf, "日本!?");
        var after = await Glyphs(result.Bytes);
        Assert.True(At(after, "!").Upright && At(after, "?").Upright);
        Assert.Equal(At(after, "!").Y, At(after, "?").Y, 3);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("日本!?", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }

    [Fact]
    public async Task DigitsNextToLatinText_StaySideways()
    {
        if (Column("平成年") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var after = await Glyphs(Replace(doc, fixture.Pdf, "平成A12年").Bytes);
        Assert.All(after.Where(g => g.Ch is "A" or "1" or "2"), g => Assert.True(g.Sideways));
    }

    [Fact]
    public async Task TheOptionOff_TurnsDigitsSideways()
    {
        if (Column("平成年") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var result = Replace(doc, fixture.Pdf, "平成12年", PdfTateChuYoko.Off);
        var after = await Glyphs(result.Bytes);
        Assert.True(At(after, "1").Sideways);
        Assert.True(At(after, "2").Sideways);
        Assert.Equal(At(after, "1").Y - fixture.Width('1'), At(after, "2").Y, 0.05);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("平成12年", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }

    [Fact]
    public async Task AUnitOnThePage_IsReadAsPartOfItsColumn()
    {
        if (ColumnWithUnit() is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        Assert.True(text.Vertical);
        Assert.Equal("令和06年", text.Text);
        Assert.InRange(text.Bounds.Y + text.Bounds.Height, 348, 352);
        Assert.InRange(text.Bounds.Y, 280, 290);
        Assert.Equal("令和06年", await PdfiumText(fixture.Pdf));
    }

    [Fact]
    public async Task HorizontalDigitsAwayFromAnyColumn_StayTextOfTheirOwn()
    {
        var fixture = Page(font =>
            $"BT /F1 {F(Size)} Tf 200 350 Td {Hex(font, "令和年")} Tj ET\n" +
            $"BT /F2 {F(Size)} Tf 1 0 0 1 50 100 Tm {Hex(font, "06")} Tj ET");
        if (fixture == null) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var texts = PdfContentEditor.Read(doc, 1).Texts;
        Assert.Equal(2, texts.Count);
        Assert.True(texts.Single(t => t.Text == "令和年").Vertical);
        Assert.False(texts.Single(t => t.Text == "06").Vertical);
    }

    [Fact]
    public async Task EditingAColumn_KeepsItsUnit_AndRoundTrips()
    {
        if (ColumnWithUnit() is not { } fixture) return;
        var before = await Glyphs(fixture.Pdf);
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);

        // An ideograph added after the unit: the unit is drawn again exactly as it was.
        var result = Replace(doc, fixture.Pdf, "令和06年度");
        Assert.Empty(result.Warnings);
        var after = await Glyphs(result.Bytes);
        foreach (string ch in new[] { "令", "和", "0", "6", "年" })
        {
            Assert.Equal(At(before, ch).X, At(after, ch).X, 2);
            Assert.Equal(At(before, ch).Y, At(after, ch).Y, 2);
            Assert.Equal(At(before, ch).ScaleX, At(after, ch).ScaleX, 3);
        }
        Assert.Equal(At(after, "年").Y - Size, At(after, "度").Y, 2);
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        var read = Assert.Single(PdfContentEditor.Read(again, 1).Texts);
        Assert.True(read.Vertical);
        Assert.Equal("令和06年度", read.Text);
        Assert.Equal("令和06年度", await PdfiumText(result.Bytes));

        // An ideograph before it removed: the unit moves up a slot, unchanged.
        var moved = await Glyphs(Replace(again, result.Bytes, "令06年度").Bytes);
        Assert.Equal(At(before, "0").X, At(moved, "0").X, 2);
        Assert.Equal(At(before, "0").Y + Size, At(moved, "0").Y, 2);
        Assert.Equal(At(before, "6").X, At(moved, "6").X, 2);

        // A digit of the unit changed: the run is set again, in the unit's own font, as one em.
        var second = Replace(again, result.Bytes, "令和07年度");
        Assert.Empty(second.Warnings);
        var set = await Glyphs(second.Bytes);
        var zero = At(set, "0");
        var seven = At(set, "7");
        Assert.True(zero.Upright && seven.Upright);
        Assert.Equal(zero.Y, seven.Y, 3);
        Assert.Equal(At(before, "0").Y, zero.Y, 2);
        double k = Size / (fixture.Width('0') + fixture.Width('7'));
        Assert.Equal(200 - Size / 2, zero.X, 2);
        Assert.Equal(k, zero.ScaleX, 3);
        Assert.Equal(At(before, "年").Y, At(set, "年").Y, 2);
        using var third = await PdfVectorDocument.OpenAsync(second.Bytes);
        Assert.Equal("令和07年度", Assert.Single(PdfContentEditor.Read(third, 1).Texts).Text);
        Assert.Equal("令和07年度", await PdfiumText(second.Bytes));
    }

    [Fact]
    public async Task ExtractionReadsTypedUnits_InTheColumnsOrder()
    {
        if (Column("令和年") is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var result = Replace(doc, fixture.Pdf, "令和06年");
        Assert.Equal("令和06年", await PdfiumText(result.Bytes));
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("令和06年", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }
}
