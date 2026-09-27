using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Kerning of new and edited text: pair adjustments from GPOS (formats 1 and 2) or the legacy
/// kern table, applied to text in embedded installed fonts; and glyphs that were side by side in
/// the document keep the spacing they had when their line is laid out again.
/// </summary>
public class KerningTests
{
    private static byte[] U16(params int[] values) => values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray();

    /// <summary>A GPOS table: script list empty, one 'kern' feature with one pair lookup.</summary>
    private static byte[] Gpos(byte[] subtable, int lookupType = 2)
    {
        // Header (10) | FeatureList | LookupList | Lookup | subtable
        var featureList = U16(1).Concat("kern"u8.ToArray()).Concat(U16(8, 0, 1, 0)).ToArray(); // one record; feature at +8: params 0, 1 lookup, index 0
        var lookupList = U16(1, 4); // one lookup at +4
        var lookup = U16(lookupType, 0, 1, 8); // type, flag, one subtable at +8
        int featureAt = 10, lookupListAt = featureAt + featureList.Length;
        return U16(1, 0, 0, featureAt, lookupListAt).Concat(featureList).Concat(lookupList).Concat(lookup).Concat(subtable).ToArray();
    }

    [Fact]
    public void GposPairFormat1_GivesTheFirstGlyphsAdvanceChange()
    {
        // Coverage of glyph 5; its pair set: (7, -80) and (9, 40). Value format 4 (XAdvance).
        int coverage = 12 + 2 + 2 * 4; // after the subtable header (12) and the pair set (count + two records of 4 bytes)
        var pairSet = U16(2, 7, -80 & 0xFFFF, 9, 40);
        var sub = U16(1, coverage, 4, 0, 1, 12).Concat(pairSet).Concat(U16(1, 1, 5)).ToArray();
        var k = OpenTypeKerning.Read(new Dictionary<string, byte[]> { ["GPOS"] = Gpos(sub) })!;
        Assert.Equal(-80, k.Pair(5, 7));
        Assert.Equal(40, k.Pair(5, 9));
        Assert.Equal(0, k.Pair(5, 8));
        Assert.Equal(0, k.Pair(6, 7));
    }

    [Fact]
    public void GposPairFormat2_KernsByClass_AlsoThroughAnExtensionLookup()
    {
        // Class 1 of the first glyph: 10..12; class 1 of the second: 20. Value 1 of (1,1) = -120, with XPlacement before it.
        var cov = U16(2, 1, 10, 12, 0);
        var class1 = U16(2, 1, 10, 12, 1);
        var class2 = U16(1, 20, 1, 1);
        int size = 16 + 2 * 2 * 4; // 2 x 2 classes, value format 5 (XPlacement + XAdvance): 4 bytes each
        var values = U16(0, 0, 0, 0, 0, 0, 7, -120 & 0xFFFF);
        int covAt = size, c1At = covAt + cov.Length, c2At = c1At + class1.Length;
        var sub = U16(2, covAt, 5, 0, c1At, c2At, 2, 2).Concat(values).Concat(cov).Concat(class1).Concat(class2).ToArray();
        var extension = U16(1, 2, 0, 8).Concat(sub).ToArray(); // format 1, type 2, 32-bit offset 8
        var k = OpenTypeKerning.Read(new Dictionary<string, byte[]> { ["GPOS"] = Gpos(extension, lookupType: 9) })!;
        Assert.Equal(-120, k.Pair(11, 20));
        Assert.Equal(0, k.Pair(11, 21));
        Assert.Equal(0, k.Pair(13, 20));
    }

    [Fact]
    public void LegacyKernTable_IsRead_WhenThereIsNoGposKerning()
    {
        var kern = U16(0, 1, 0, 14 + 12, 0x0001, 2, 12, 1, 0, 3, 4, -50 & 0xFFFF, 3, 5, 25);
        var k = OpenTypeKerning.Read(new Dictionary<string, byte[]> { ["kern"] = kern })!;
        Assert.Equal(-50, k.Pair(3, 4));
        Assert.Equal(25, k.Pair(3, 5));
        Assert.Equal(0, k.Pair(4, 3));
        Assert.Null(OpenTypeKerning.Read(new Dictionary<string, byte[]> { ["GPOS"] = new byte[] { 0, 1, 0, 0, 0, 3 } }));
    }

    private static string? Arial()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void Arial_KernsAV()
    {
        if (Arial() is not { } path) return;
        var font = TrueTypeFontFile.TryLoad(File.ReadAllBytes(path))!;
        int a = font.GlyphFor('A'), v = font.GlyphFor('V'), o = font.GlyphFor('o');
        Assert.True(font.Kerning(a, v) < -30, $"AV kerns by {font.Kerning(a, v)}");
        Assert.Equal(0, font.Kerning(o, o));
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
    public async Task AddedText_IsKerned()
    {
        if (Arial() == null) return;
        var b = new VectorPdfBuilder();
        b.AddPage("", "<< >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfAddText(1, new PdfPoint(40, 200), "AVAVoo", new PdfTextFormat("Arial", 100)) });
        var g = await Glyphs(result.Bytes);
        var font = TrueTypeFontFile.TryLoad(File.ReadAllBytes(Arial()!))!;
        double advanceA = font.Advance(font.GlyphFor('A')) / 10, advanceO = font.Advance(font.GlyphFor('o')) / 10;
        Assert.True(g[1].X - g[0].X < advanceA - 3, "V is pulled towards A");
        Assert.Equal(advanceO, g[5].X - g[4].X, 1); // no kerning between o and o
    }

    [Fact]
    public async Task RelaidLines_KeepTheDocumentsOwnKerning()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 12 Tf 40 300 Td [(W) 120 (A) 120 (V) 120 (E) -40 ( waves here)] TJ ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        var before = await Glyphs(pdf);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, text.Text.Replace("here", "there")) });
        var after = await Glyphs(result.Bytes);
        // The kerned word before the change is exactly where it was.
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(before[i].Ch, after[i].Ch);
            Assert.Equal(before[i].X, after[i].X, 3);
        }
    }
}
