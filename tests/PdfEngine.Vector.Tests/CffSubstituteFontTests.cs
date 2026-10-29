using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// System fonts with CFF outlines (OpenType 'OTTO', as Android's Noto Sans CJK) used for new text:
/// loaded with their metrics and character map, shaped and measured, and embedded as a CID-keyed
/// CFF subset (CIDFontType0, FontFile3 /CIDFontType0C). The font is built in code (<see cref="CffOpenTypeFont"/>).
/// </summary>
public sealed class CffSubstituteFontTests
{
    [Fact]
    public void Load_ReadsMetricsCharacterMapAndOutlines_OfAFontWithCffOutlines()
    {
        byte[] data = CffOpenTypeFont.Font();
        // Callers that need TrueType outlines (PDF/A, renderers' shaping) still get none.
        Assert.Null(TrueTypeFontFile.TryLoad(data));

        var font = TrueTypeFontFile.TryLoad(data, 0, openTypeCff: true);
        Assert.NotNull(font);
        Assert.True(font.IsCff);
        Assert.Equal(5, font.GlyphCount);
        Assert.Equal(CffOpenTypeFont.Family, font.FamilyName);
        Assert.Equal(CffOpenTypeFont.PostScriptName, font.PostScriptName);
        Assert.True(font.EmbeddingAllowed);
        Assert.Equal((1, 2, 3), (font.GlyphFor('A'), font.GlyphFor(0x4E2D), font.GlyphFor(' ')));
        Assert.Equal(0, font.GlyphFor('B'));
        Assert.Equal((600.0, 1000.0, 250.0), (font.Advance(1), font.Advance(2), font.Advance(3)));
        Assert.Equal(900, font.Ascender);
        // Outlines are found through subroutine calls, a hintmask's mask bytes skipped.
        Assert.True(font.HasOutline(1));
        Assert.True(font.HasOutline(2));
        Assert.False(font.HasOutline(3));

        // A CFF face inside a .ttc collection, as NotoSansCJK-Regular.ttc.
        byte[] collection = CffOpenTypeFont.Collection(data);
        Assert.Equal(2, TrueTypeFontFile.FontCount(collection));
        var second = TrueTypeFontFile.TryLoad(collection, 1, openTypeCff: true);
        Assert.NotNull(second);
        Assert.True(second.IsCff);
        Assert.Equal(2, second.GlyphFor(0x4E2D));
    }

    [Fact]
    public void Subset_IsCidKeyed_WithTheUsedGlyphsAndOnlyTheSubroutinesTheyCall()
    {
        var font = TrueTypeFontFile.TryLoad(CffOpenTypeFont.Font(), 0, openTypeCff: true)!;
        byte[] subset = font.Subset(new[] { 2, 1, 3, 1 });

        var cff = CffFont.TryParse(subset);
        Assert.NotNull(cff);
        Assert.True(cff.IsCidKeyed);
        Assert.Equal(4, cff.GlyphCount);
        // CID = the glyph id in the system font, which is what the content stream shows (Identity-H).
        Assert.Equal((1, 2, 3, 0), (cff.GidForCid(1), cff.GidForCid(2), cff.GidForCid(3), cff.GidForCid(4)));
        Assert.True(Contains(subset, CffOpenTypeFont.UsedLocalSubr));
        Assert.True(Contains(subset, CffOpenTypeFont.UsedGlobalSubr));
        Assert.False(Contains(subset, CffOpenTypeFont.UnusedLocalSubr));
        Assert.False(Contains(subset, CffOpenTypeFont.UnusedGlobalSubr));

        // The glyph that calls them keeps them.
        byte[] all = font.Subset(new[] { 1, 2, 3, 4 });
        Assert.True(Contains(all, CffOpenTypeFont.UnusedLocalSubr));
        Assert.True(Contains(all, CffOpenTypeFont.UnusedGlobalSubr));

        // A CID-keyed subset reads back, and subsets again.
        var again = CffSubsetter.TryParse(subset)!.Subset(new[] { 2 }, "Again");
        var reread = CffFont.TryParse(again);
        Assert.NotNull(reread);
        Assert.Equal(2, reread.GlyphCount);
        Assert.Equal(1, reread.GidForCid(2));
        Assert.True(Contains(again, CffOpenTypeFont.UsedGlobalSubr));
        Assert.False(Contains(again, CffOpenTypeFont.UsedLocalSubr));
    }

    [Fact]
    public void CollectionFaces_ShareOneCopyOfTheirCffTable_AndACharacterMapRulesFacesOut()
    {
        // As NotoSansCJK-Regular.ttc: several faces over one CFF table.
        using var folder = new FontFolder("SyntheticCjk.ttc", CffOpenTypeFont.Collection(CffOpenTypeFont.Font()));
        var catalog = SystemFontCatalog.ForAndroid(folder.Path);
        Assert.Equal(2, catalog.Faces.Count);

        // Without loading the face: its character map says what it lacks.
        Assert.True(catalog.MayHave(catalog.Faces[1], 0x4E2D));
        Assert.False(catalog.MayHave(catalog.Faces[1], 0x1F600)); // an emoji no face has

        var first = catalog.Load(catalog.Faces[0]);
        var second = catalog.Load(catalog.Faces[1]);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Same(first.Table("CFF "), second.Table("CFF "));
        Assert.Same(first.Table("cmap"), second.Table("cmap"));
        Assert.Equal(2, second.GlyphFor(0x4E2D));
        Assert.True(catalog.MayHave(catalog.Faces[0], 'A'));
        Assert.False(catalog.MayHave(catalog.Faces[0], 'B'));
    }

    [Fact]
    public void SeacGlyph_IsNotUsed_SoTheNextFontDrawsIt()
    {
        // A name-keyed font whose 'A' is built from two standard-encoding glyphs (endchar adx ady bchar achar):
        // a CID-keyed subset cannot express that.
        var box = new SyntheticCff.CharString().N(50, 0).Op(21).N(400, 0).Op(5).N(0, 700).Op(5).N(-400, 0).Op(5).Op(14).ToArray();
        var seac = new SyntheticCff.CharString().N(0, 0, 66, 67).Op(14).ToArray();
        byte[] cff = SyntheticCff.NameKeyed(null, new[] { ".notdef", "A", "B", "C", "D" }, new[] { new byte[] { 14 }, seac, box, box, box });
        var program = CffSubsetter.TryParse(cff)!;
        Assert.True(program.UsesSeac(1));
        Assert.False(program.UsesSeac(2));
        Assert.False(program.HasOutline(1));

        var font = TrueTypeFontFile.TryLoad(CffOpenTypeFont.Font(cff), 0, openTypeCff: true)!;
        Assert.Equal(0, font.GlyphFor('A'));       // glyph 1 is the seac glyph
        Assert.Equal(2, font.GlyphFor(0x4E2D));    // glyph 2 draws itself
    }

    [Fact]
    public void Subset_OfANameKeyedFont_IsCidKeyed_AndKeepsItsFontMatrix()
    {
        // Most Latin .otf fonts are name-keyed, many with 2048 units per em.
        double[] matrix = { 0.00048828125, 0, 0, 0.00048828125, 0, 0 };
        var subr = new SyntheticCff.CharString().N(1000, 0).Op(5).N(0, 1000).Op(5).Op(11).ToArray();
        var glyph = new SyntheticCff.CharString().N(100, 0).Op(21).N(-107).Op(10).Op(14).ToArray();
        byte[] source = SyntheticCff.NameKeyed(matrix, new[] { ".notdef", "A", "B" }, new[] { new byte[] { 14 }, glyph, SyntheticCff.LShape(2048) }, new[] { subr });

        byte[] subset = CffSubsetter.TryParse(source)!.Subset(new[] { 1 }, "Latin");
        var cff = CffFont.TryParse(subset);
        Assert.NotNull(cff);
        Assert.True(cff.IsCidKeyed);
        Assert.Equal(2, cff.GlyphCount);
        Assert.Equal(1, cff.GidForCid(1));
        Assert.Equal(0.00048828125, cff.OutlineMatrix(1)[0], 15);
        Assert.True(Contains(subset, subr));
    }

    [Fact]
    public void Catalog_LoadsCffFaces_ShapesAndMeasuresWithThem_AndPrefersTrueType()
    {
        using var folder = new FontFolder();
        var catalog = SystemFontCatalog.ForAndroid(folder.Path);
        var face = Assert.Single(catalog.Faces);
        Assert.True(face.OpenTypeCff);
        var font = catalog.Load(face);
        Assert.NotNull(font);
        Assert.True(font.IsCff);

        var shaped = TextShaper.ShapeLine("A中", _ => font, f => f);
        Assert.Equal(new[] { 1, 2 }, shaped.Select(s => s.Glyph.Gid));

        // As on Android: free text in Helvetica, the Chinese character from the CJK fallback.
        var layout = PdfAnnotationAppearances.MeasureFreeText("A中 A中", "Helv", 12, 220, fonts: catalog);
        Assert.NotNull(layout);
        Assert.Equal(0, layout.Value.Missing);
        Assert.Equal(1, layout.Value.Lines);

        // A TrueType face that stands in for the same family comes first; the CFF face stays for what only it has.
        var trueType = new SystemFontFace(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"), 0, "Droid Sans Fallback", "Regular", "DroidSansFallback", 400, false);
        var both = SystemFontCatalog.FromFaces(new[] { face, trueType }, SystemFontCatalog.AndroidFamilyAliases);
        var order = both.Fallbacks(false, false).ToList();
        Assert.True(order.IndexOf(trueType) >= 0 && order.IndexOf(trueType) < order.IndexOf(face), string.Join(", ", order.Select(f => f.Family)));
        // Within one family a TrueType face wins a tie with a CFF face.
        var sameFamily = SystemFontCatalog.FromFaces(new[] { face, trueType with { Family = CffOpenTypeFont.Family } });
        Assert.False(sameFamily.FindFamily(CffOpenTypeFont.Family, false, false)!.OpenTypeCff);
    }

    [Fact]
    public void Embedding_WritesCidFontType0_WithACffSubsetInFontFile3_AndWidths()
    {
        var font = TrueTypeFontFile.TryLoad(CffOpenTypeFont.Font(), 0, openTypeCff: true)!;
        var builder = new EmbeddedFontBuilder(font, "F1");
        builder.Encode(1, "A");
        builder.Encode(2, "中");
        var objects = new Dictionary<int, PdfObject>();
        int next = 1;
        int type0 = builder.Write(() => next++, objects);

        var top = (PdfDictionary)objects[type0];
        Assert.Equal("Type0", top.GetName("Subtype"));
        Assert.Equal("Identity-H", top.GetName("Encoding"));
        var cid = (PdfDictionary)objects[Ref(((PdfArray)top["DescendantFonts"]!)[0])];
        Assert.Equal("CIDFontType0", cid.GetName("Subtype"));
        Assert.False(cid.ContainsKey("CIDToGIDMap"));
        Assert.Equal("[1 [600 1000]]", cid["W"]!.ToString());
        var descriptor = (PdfDictionary)objects[Ref(cid["FontDescriptor"]!)];
        Assert.False(descriptor.ContainsKey("FontFile2"));
        Assert.Equal(900, ((PdfInteger)descriptor["Ascent"]!).Value);
        var file = (PdfStream)objects[Ref(descriptor["FontFile3"]!)];
        Assert.Equal("CIDFontType0C", file.Dictionary.GetName("Subtype"));
        Assert.False(file.Dictionary.ContainsKey("Length1"));

        using var inflate = new ZLibStream(new MemoryStream(file.CachedRawBytes!.Value.ToArray()), CompressionMode.Decompress);
        using var program = new MemoryStream();
        inflate.CopyTo(program);
        var cff = CffFont.TryParse(program.ToArray());
        Assert.NotNull(cff);
        Assert.True(cff.IsCidKeyed);
        Assert.Equal(3, cff.GlyphCount);
        Assert.Equal(2, cff.GidForCid(2));
        // The program's name is the CIDFontName: the /BaseFont with its subset tag.
        Assert.Equal(cid.GetName("BaseFont"), cff.FontName);
        Assert.Matches("^[A-Z]{6}\\+" + CffOpenTypeFont.PostScriptName + "$", cff.FontName);
    }

    [Fact]
    public async Task TextDocument_SetInACffFont_ReadsBackAsItsText()
    {
        using var folder = new FontFolder();
        using var ms = new MemoryStream();
        PdfTextDocument.Write("A中 A", ms, new PdfTextLayoutOptions { FontFamily = CffOpenTypeFont.Family }, catalog: SystemFontCatalog.ForAndroid(folder.Path));
        byte[] pdf = ms.ToArray();
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/FontFile3", raw);
        Assert.DoesNotContain("/FontFile2", raw);
        Assert.DoesNotContain("/CIDToGIDMap", raw);

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        Assert.Contains("A中 A", PdfTextExtractor.LoadPage(doc, 1).Text);
    }

    private static int Ref(PdfObject o) => (int)((PdfIndirectRef)o).ObjectNumber;

    private static bool Contains(byte[] data, byte[] part) => data.AsSpan().IndexOf(part) >= 0;

    /// <summary>A temporary font folder with the font as an .otf file.</summary>
    private sealed class FontFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("cff-fonts").FullName;

        public FontFolder() : this("SyntheticCjk-Regular.otf", CffOpenTypeFont.Font()) { }

        public FontFolder(string name, byte[] data) => File.WriteAllBytes(System.IO.Path.Combine(Path, name), data);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
