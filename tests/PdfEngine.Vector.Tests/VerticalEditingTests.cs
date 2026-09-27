using System;
using System.Collections.Generic;
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
/// Editing vertical writing: columns written top to bottom (Identity-V) are found as paragraphs,
/// read top to bottom and right to left, and edited in place down the column.
/// </summary>
public class VerticalEditingTests
{
    private static string? ArialPath()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Two columns of Arial capitals in a vertical Type0 font: "ABCDEF" at x 200, "GHIJ" left of it.</summary>
    private static (byte[] Pdf, TrueTypeFontFile Font)? Columns()
    {
        if (ArialPath() is not { } path) return null;
        byte[] data = File.ReadAllBytes(path);
        var font = TrueTypeFontFile.TryLoad(data)!;
        var b = new VectorPdfBuilder();
        int file = b.AddStream($"/Length1 {data.Length}", data, flate: true);
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 {file} 0 R >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 1000 >>");
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def /CMapName /Adobe-Identity-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n26 beginbfchar\n");
        for (char c = 'A'; c <= 'Z'; c++) cmap.Append($"<{font.GlyphFor(c):X4}> <{(int)c:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream(string.Empty, cmap.ToString());
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-V /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        string Hex(string s) => "<" + string.Concat(s.Select(c => font.GlyphFor(c).ToString("X4"))) + ">";
        b.AddPage($"BT /F1 16 Tf 200 350 Td {Hex("ABCDEF")} Tj ET BT /F1 16 Tf 180 350 Td {Hex("GHIJ")} Tj ET", $"<< /Font << /F1 {type0} 0 R >> >>", mediaBox: "[0 0 400 400]");
        return (b.Build(), font);
    }

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

    [Fact]
    public async Task Columns_AreOneParagraph_ReadTopToBottomAndRightToLeft()
    {
        if (Columns() is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        Assert.True(text.Vertical);
        Assert.Equal("ABCDEF GHIJ", text.Text);
        Assert.Equal(0, text.AngleDegrees, 3); // upright: the editor is not turned
        Assert.Equal(16, text.FontSize, 3);
        Assert.InRange(text.Bounds.X, 170, 182);
        Assert.InRange(text.Bounds.Y + text.Bounds.Height, 348, 352);
    }

    [Fact]
    public async Task ReplacingLettersInAColumn_DrawsThemWhereTheOldOnesWere()
    {
        if (Columns() is not { } fixture) return;
        var before = await Glyphs(fixture.Pdf);
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "ABXYEF GHIJ") });
        Assert.Empty(result.Warnings);
        var after = await Glyphs(result.Bytes);
        Assert.Equal(before.Count, after.Count);
        (double X, double Y) At(List<(string Ch, double X, double Y)> glyphs, string ch) { var g = glyphs.Single(x => x.Ch == ch); return (g.X, g.Y); }
        foreach (var (was, now) in new[] { ("C", "X"), ("D", "Y"), ("A", "A"), ("F", "F"), ("G", "G") })
        {
            Assert.Equal(At(before, was).X, At(after, now).X, 2);
            Assert.Equal(At(before, was).Y, At(after, now).Y, 2);
        }

        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(result.Bytes, null);
        string read = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
        Assert.Contains("X", read);
        Assert.DoesNotContain("C", read);

        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("ABXYEF GHIJ", Assert.Single(PdfContentEditor.Read(again, 1).Texts).Text);
    }

    [Fact]
    public async Task ALongerColumn_Reflows_IntoTheNextColumnToTheLeft()
    {
        if (Columns() is not { } fixture) return;
        using var doc = await PdfVectorDocument.OpenAsync(fixture.Pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, fixture.Pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "ABC DEF GHIJ KL") });
        var after = await Glyphs(result.Bytes);
        // Every glyph is in one of the columns, 20 apart (x 200, 180, 160, 140), none beyond the paragraph's top.
        Assert.All(after, g => Assert.Contains(new[] { 200.0, 180.0, 160.0, 140.0 }, x => Math.Abs(x - g.X) < 0.5 || Math.Abs(x - (g.X + 8)) < 0.5 || Math.Abs(x - (g.X - 8)) < 0.5));
        Assert.True(after.Min(g => g.X) < after.Single(g => g.Ch == "A").X - 10, "the text continues in a column to the left");
        using var again = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal("ABC DEF GHIJ KL", string.Join(" ", PdfContentEditor.Read(again, 1).Texts.Select(t => t.Text)).Replace("\n", " "));
    }
}
