using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// GPOS cursive attachment (lookup type 3) for new text: each glyph's entry anchor is put on the
/// exit anchor of the glyph before it, along the line and across it, for left-to-right and
/// right-to-left runs, with the RightToLeft flag deciding which glyph of a joined pair stays put.
/// </summary>
public class CursiveAttachmentTests
{
    private static byte[] U16(params int[] values) => values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray();

    private sealed record Joint(int Gid, (int X, int Y)? Entry, (int X, int Y)? Exit);

    /// <summary>A cursive subtable (format 1): one entry/exit record per glyph, anchors in format 1, then the coverage.</summary>
    private static byte[] CursiveSubtable(params Joint[] glyphs)
    {
        var sorted = glyphs.OrderBy(g => g.Gid).ToArray();
        int anchorsAt = 6 + sorted.Length * 4;
        var anchors = new List<byte>();
        var records = new List<byte>();
        foreach (var g in sorted)
            foreach (var anchor in new[] { g.Entry, g.Exit })
            {
                if (anchor is not { } a) { records.AddRange(U16(0)); continue; }
                records.AddRange(U16(anchorsAt + anchors.Count));
                anchors.AddRange(U16(1, a.X & 0xFFFF, a.Y & 0xFFFF));
            }
        int coverageAt = anchorsAt + anchors.Count;
        return U16(1, coverageAt, sorted.Length).Concat(records).Concat(anchors)
            .Concat(U16(1, sorted.Length)).Concat(U16(sorted.Select(g => g.Gid).ToArray())).ToArray();
    }

    /// <summary>A GPOS table whose DFLT script turns on one 'curs' feature with one lookup.</summary>
    private static byte[] Gpos(byte[] subtable, int flag, bool extension = false)
    {
        var scriptList = U16(1).Concat("DFLT"u8.ToArray()).Concat(U16(8)).Concat(U16(4, 0)).Concat(U16(0, 0xFFFF, 1, 0)).ToArray();
        var featureList = U16(1).Concat("curs"u8.ToArray()).Concat(U16(8)).Concat(U16(0, 1, 0)).ToArray();
        var body = extension ? U16(1, 3, 0, 8).Concat(subtable).ToArray() : subtable; // format 1, type 3, 32-bit offset 8
        var lookup = U16(extension ? 9 : 3, flag, 1, 8).Concat(body).ToArray();
        var lookupList = U16(1, 4).Concat(lookup).ToArray();
        int features = 10 + scriptList.Length, lookups = features + featureList.Length;
        return U16(1, 0, 10, features, lookups).Concat(scriptList).Concat(featureList).Concat(lookupList).ToArray();
    }

    /// <summary>A GDEF table that says glyph <paramref name="mark"/> is a mark.</summary>
    private static byte[] Gdef(int mark) => U16(1, 0, 12, 0, 0, 0).Concat(U16(2, 1, mark, mark, 3)).ToArray();

    private static (double[] Advances, double[] Dx, double[] Dy) Position(byte[] gpos, int[] gids, double[] advances, bool rtl, byte[]? gdef = null)
    {
        var tables = new Dictionary<string, byte[]> { ["GPOS"] = gpos };
        if (gdef != null) tables["GDEF"] = gdef;
        var positioning = OpenTypePositioning.Read(tables)!;
        var adv = (double[])advances.Clone();
        var dx = new double[gids.Length];
        var dy = new double[gids.Length];
        positioning.Apply(gids, "arab", adv, dx, dy, rtl);
        return (adv, dx, dy);
    }

    /// <summary>Where each glyph (logical index) is drawn: in drawing order from pen 0, reversed for right-to-left.</summary>
    private static (double X, double Y)[] Origins(double[] advances, double[] dx, double[] dy, bool rtl)
    {
        int n = advances.Length;
        var order = Enumerable.Range(0, n).ToArray();
        if (rtl) Array.Reverse(order);
        var origins = new (double, double)[n];
        double pen = 0;
        foreach (int k in order)
        {
            origins[k] = (pen + dx[k], dy[k]);
            pen += advances[k];
        }
        return origins;
    }

    private static void AssertJoined((double X, double Y)[] origins, Joint[] glyphs, int i, int j)
    {
        var exit = glyphs[i].Exit!.Value;
        var entry = glyphs[j].Entry!.Value;
        Assert.Equal(origins[i].X + exit.X, origins[j].X + entry.X, 6);
        Assert.Equal(origins[i].Y + exit.Y, origins[j].Y + entry.Y, 6);
    }

    private static readonly Joint[] LeftToRight =
    {
        new(10, null, (450, 100)),
        new(11, (50, 0), (550, 200)),
        new(12, (30, -50), null),
    };

    [Fact]
    public void LeftToRight_EachEntryMeetsTheExitBeforeIt_AndTheFirstGlyphStaysOnTheBaseline()
    {
        var (adv, dx, dy) = Position(Gpos(CursiveSubtable(LeftToRight), flag: 0), new[] { 10, 11, 12 }, new double[] { 500, 600, 400 }, rtl: false);
        Assert.Equal(new double[] { 450, 500, 370 }, adv);
        Assert.Equal(new double[] { 0, -50, -30 }, dx);
        Assert.Equal(new double[] { 0, 100, 350 }, dy); // the chain climbs from the first glyph
        var origins = Origins(adv, dx, dy, rtl: false);
        AssertJoined(origins, LeftToRight, 0, 1);
        AssertJoined(origins, LeftToRight, 1, 2);
    }

    private static readonly Joint[] RightToLeft =
    {
        new(10, null, (20, 100)),
        new(11, (580, 0), (40, 200)),
        new(12, (390, -50), null),
    };

    [Fact]
    public void RightToLeft_WithTheRightToLeftFlag_HangsTheChainOffTheLastGlyph()
    {
        var (adv, dx, dy) = Position(Gpos(CursiveSubtable(RightToLeft), flag: 0x1), new[] { 10, 11, 12 }, new double[] { 500, 600, 400 }, rtl: true);
        Assert.Equal(new double[] { 480, 540, 390 }, adv);
        Assert.Equal(new double[] { -20, -40, 0 }, dx);
        Assert.Equal(new double[] { -350, -250, 0 }, dy); // the last letter sits on the baseline, the earlier ones hang off it
        var origins = Origins(adv, dx, dy, rtl: true);
        AssertJoined(origins, RightToLeft, 0, 1);
        AssertJoined(origins, RightToLeft, 1, 2);
        Assert.True(origins[0].X > origins[1].X && origins[1].X > origins[2].X, "drawn right to left");
    }

    [Fact]
    public void RightToLeft_WithoutTheFlag_KeepsTheFirstGlyphOnTheBaseline()
    {
        var (adv, dx, dy) = Position(Gpos(CursiveSubtable(RightToLeft), flag: 0), new[] { 10, 11, 12 }, new double[] { 500, 600, 400 }, rtl: true);
        Assert.Equal(new double[] { 0, 100, 350 }, dy);
        var origins = Origins(adv, dx, dy, rtl: true);
        AssertJoined(origins, RightToLeft, 0, 1);
        AssertJoined(origins, RightToLeft, 1, 2);
    }

    [Fact]
    public void ExtensionLookup_JoinsTheSameWay()
    {
        var direct = Position(Gpos(CursiveSubtable(RightToLeft), flag: 0x1), new[] { 10, 11, 12 }, new double[] { 500, 600, 400 }, rtl: true);
        var extended = Position(Gpos(CursiveSubtable(RightToLeft), flag: 0x1, extension: true), new[] { 10, 11, 12 }, new double[] { 500, 600, 400 }, rtl: true);
        Assert.Equal(direct.Advances, extended.Advances);
        Assert.Equal(direct.Dx, extended.Dx);
        Assert.Equal(direct.Dy, extended.Dy);
    }

    [Fact]
    public void IgnoreMarks_JoinsAcrossAMark_WhichOtherwiseBreaksTheChain()
    {
        var glyphs = new[] { LeftToRight[0], LeftToRight[1] };
        int[] run = { 10, 20, 11 }; // glyph 20 is a mark, with no anchors
        var (adv, dx, dy) = Position(Gpos(CursiveSubtable(glyphs), flag: 0x8), run, new double[] { 500, 0, 600 }, rtl: false, gdef: Gdef(20));
        Assert.Equal(new double[] { 450, 0, 550 }, adv);
        Assert.Equal(-50, dx[2]);
        Assert.Equal(100, dy[2]);

        var (adv2, dx2, dy2) = Position(Gpos(CursiveSubtable(glyphs), flag: 0), run, new double[] { 500, 0, 600 }, rtl: false, gdef: Gdef(20));
        Assert.Equal(new double[] { 500, 0, 600 }, adv2);
        Assert.All(dx2.Concat(dy2), v => Assert.Equal(0, v));
    }

    [Fact]
    public void AllOfPositioning_JoinsCursiveGlyphs_WhenTheFeatureIsAsked()
    {
        var positioning = OpenTypePositioning.Read(new Dictionary<string, byte[]> { ["GPOS"] = Gpos(CursiveSubtable(LeftToRight), flag: 0) })!;
        var adv = new double[] { 500, 600, 400 };
        var dx = new double[3];
        var dy = new double[3];
        positioning.Apply(new[] { 10, 11, 12 }, "arab", new[] { "curs" }, adv, dx, dy);
        Assert.Equal(new double[] { 450, 500, 370 }, adv);
        Assert.Equal(new double[] { 0, 100, 350 }, dy);
    }

    [Fact]
    public void DamagedSubtable_LeavesTheRunAsItWas()
    {
        var gpos = Gpos(CursiveSubtable(LeftToRight), flag: 0);
        var cut = gpos.Take(gpos.Length - 4).ToArray(); // the coverage runs off the end
        var (adv, _, dy) = Position(cut, new[] { 10, 11, 12 }, new double[] { 500, 600, 400 }, rtl: false);
        Assert.Equal(3, adv.Length);
        Assert.All(dy, v => Assert.True(double.IsFinite(v)));
    }

    // ------------------------------------------------------------------ end to end, with an installed font

    private static string? Arial()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public async Task AddedArabicText_IsJoinedOnItsCursiveAnchors_AndRaisedAlongTheChain()
    {
        if (Arial() is not { } path) return;
        byte[] original = File.ReadAllBytes(path);
        var arial = TrueTypeFontFile.TryLoad(original)!;
        // The word's glyphs (beh, yeh and teh in their joining forms), in reading order.
        const string word = "بيت";
        var gids = TextShaper.ShapeLine(word, _ => arial, f => f).Select(g => g.Glyph.Gid).Reverse().ToArray();
        Assert.Equal(3, gids.Distinct().Count());
        int upm = arial.UnitsPerEm;
        // Entry on the right, exit on the left (overlapping by 60 units); each join lifts the earlier letter by 300.
        var joints = new[]
        {
            new Joint(gids[0], null, (60, -200)),
            new Joint(gids[1], (arial.AdvanceUnits(gids[1]), 100), (60, -200)),
            new Joint(gids[2], (arial.AdvanceUnits(gids[2]), 100), null),
        };
        var tables = new Dictionary<string, byte[]>(TrueTypeFontFile.ReadTables(original, 0)!, StringComparer.Ordinal)
        {
            ["GPOS"] = Gpos(CursiveSubtable(joints), flag: 0x9), // RightToLeft, IgnoreMarks
        };
        string dir = Path.Combine(Path.GetTempPath(), "pdfengine-cursive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "arial-cursive.ttf"), Sfnt.Write(Sfnt.TrueTypeVersion, tables));
            var b = new VectorPdfBuilder();
            b.AddPage("", "<< >>", mediaBox: "[0 0 400 400]");
            byte[] pdf = b.Build();
            using var doc = await PdfVectorDocument.OpenAsync(pdf);
            var options = new PdfContentEditOptions { Fonts = SystemFontCatalog.ForDirectory(dir) };
            var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfAddText(1, new PdfPoint(40, 200), word, new PdfTextFormat("Arial", 100)) }, options);
            Assert.Empty(result.Warnings);

            using var after = await PdfVectorDocument.OpenAsync(result.Bytes);
            var list = await after.GetPageDisplayListAsync(1);
            var drawn = list.Commands.OfType<DrawGlyphRun>()
                .SelectMany(c => c.Run.Glyphs.Select(g => (Text: g.Unicode ?? "", At: c.Run.TextToPage.Transform(g.OffsetX, g.OffsetY)))).ToList();
            Assert.Equal(3, drawn.Count);
            PdfPoint At(int logical) => drawn.Single(g => g.Text == word[logical].ToString()).At;
            double scale = 100.0 / upm;
            // The last letter sits on the baseline; each one before it is lifted by one more join.
            Assert.Equal(200, At(2).Y, 2);
            Assert.Equal(200 + 300 * scale, At(1).Y, 2);
            Assert.Equal(200 + 600 * scale, At(0).Y, 2);
            // Each letter's exit anchor is on the entry anchor of the letter after it.
            for (int i = 0; i < 2; i++)
            {
                var exit = joints[i].Exit!.Value;
                var entry = joints[i + 1].Entry!.Value;
                Assert.Equal(At(i + 1).X + entry.X * scale, At(i).X + exit.X * scale, 2);
                Assert.Equal(At(i + 1).Y + entry.Y * scale, At(i).Y + exit.Y * scale, 2);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
