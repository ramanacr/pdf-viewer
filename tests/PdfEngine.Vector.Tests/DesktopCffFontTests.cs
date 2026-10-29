using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Fonts with CFF outlines (OpenType <c>.otf</c>) installed on Windows: listed by the desktop
/// catalog (the machine's and the user's font folders), tried for new text after every TrueType
/// fallback, loaded by DirectWrite and WPF, and embedded as CIDFontType0C. The font is built in code
/// (<see cref="CffOpenTypeFont"/>) under a family no fallback names, as a font the user installed.
/// </summary>
public sealed class DesktopCffFontTests
{
    private const string Family = "Synthetic Han Sans";
    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    [Fact]
    public void Catalog_ListsOtfFaces_OfEveryFolder_InFolderOrder()
    {
        using var system = new TempFolder();
        using var user = new TempFolder();
        File.Copy(Path.Combine(WindowsFonts, "arial.ttf"), Path.Combine(system.Path, "arial.ttf"));
        user.Add("SyntheticHanSans-Regular.otf", CffOpenTypeFont.Font(family: Family, postScriptName: "SyntheticHanSans-Regular"));

        var single = SystemFontCatalog.ForDirectory(user.Path);
        var otf = Assert.Single(single.Faces);
        Assert.True(otf.OpenTypeCff);
        Assert.Equal(Family, otf.Family);
        Assert.Equal("SyntheticHanSans-Regular", otf.PostScriptName);

        var both = SystemFontCatalog.ForDirectories(system.Path, user.Path);
        Assert.Equal(new[] { ("Arial", false), (Family, true) }, both.Faces.Select(f => (f.Family, f.OpenTypeCff)));
        Assert.Same(both, SystemFontCatalog.ForDirectories(system.Path, user.Path));
        // A document font of that name is the installed face, as a viewer with system fonts draws it.
        Assert.Equal(otf, both.Match("SyntheticHanSans-Regular", false, false, false, false));
    }

    [Fact]
    public void InstalledCatalog_OnWindows_AlsoReadsTheUsersFontFolder()
    {
        string? user = PdfPlatform.UserFontDirectory;
        Assert.NotNull(user);
        Assert.EndsWith(Path.Combine("Microsoft", "Windows", "Fonts"), user, StringComparison.OrdinalIgnoreCase);
        Assert.Same(SystemFontCatalog.ForDirectories(WindowsFonts, user), SystemFontCatalog.Installed);
        var installed = SystemFontCatalog.Installed.Faces.ToHashSet();
        Assert.All(SystemFontCatalog.ForDirectory(user).Faces, f => Assert.Contains(f, installed));
        Assert.Contains(SystemFontCatalog.Installed.Faces, f => f.Family == "Arial");
        using (PdfPlatform.Simulate(canStartThreads: true, hasInstalledFonts: false))
            Assert.Null(PdfPlatform.UserFontDirectory);
    }

    [Fact]
    public async Task NewText_UsesTrueTypeFallbacksFirst_ThenAnInstalledCffFont_EmbeddedAndDrawnByDirect2D()
    {
        using var folder = new TempFolder();
        File.Copy(Path.Combine(WindowsFonts, "arial.ttf"), Path.Combine(folder.Path, "arial.ttf"));
        folder.Add("SyntheticHanSans-Regular.otf", CffOpenTypeFont.Font(family: Family, postScriptName: "SyntheticHanSans-Regular"));

        // No TrueType face has 中: the CFF face, last in the fallbacks, draws it (page text editing
        // sets a run in one face that has all its characters, so the A too).
        var catalog = SystemFontCatalog.ForDirectory(folder.Path);
        Assert.Equal(new[] { "Arial", Family }, catalog.Fallbacks(false, false).Select(f => f.Family));
        var (_, latin) = await AddText(catalog, "A");
        Assert.Equal(new[] { ("A", PdfFontProgramFormat.TrueType) }, latin);
        var (pdf, formats) = await AddText(catalog, "A中");
        Assert.Equal(new[] { ("A", PdfFontProgramFormat.OpenTypeCff), ("中", PdfFontProgramFormat.OpenTypeCff) }, formats);
        string raw = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/CIDFontType0C", raw);

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var d2d = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        var result = await d2d.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, CancellationToken.None);
        Assert.Empty(result.Fallbacks);
        using (var page = result.Page)
        {
            // 中 is a box of 800 units at 100 pt after the A (600 units): x 110 to 190, y 200 to 280 in page space.
            int dark = 0, area = 0;
            var px = page.Pixels.Span;
            for (int y = 130; y < 190; y++)
                for (int x = 130; x < 185; x++, area++)
                {
                    int p = y * page.Stride + x * 4;
                    if (Math.Min(px[p], Math.Min(px[p + 1], px[p + 2])) < 128) dark++;
                }
            Assert.True(dark > area * 9 / 10, $"{dark} of {area} pixels inked");
        }

        // With a TrueType face that has it (Microsoft YaHei), that face draws it, not the CFF one.
        string yahei = Path.Combine(WindowsFonts, "msyh.ttc");
        if (!File.Exists(yahei)) return;
        using var second = new TempFolder();
        File.Copy(yahei, Path.Combine(second.Path, "msyh.ttc"));
        var withYaHei = SystemFontCatalog.ForDirectories(folder.Path, second.Path);
        var order = withYaHei.Fallbacks(false, false).Select(f => f.Family).ToList();
        Assert.True(order.IndexOf("Microsoft YaHei") >= 0 && order.IndexOf("Microsoft YaHei") < order.IndexOf(Family), string.Join(", ", order));
        (_, formats) = await AddText(withYaHei, "A中");
        Assert.Equal(new[] { ("A", PdfFontProgramFormat.TrueType), ("中", PdfFontProgramFormat.TrueType) }, formats);
    }

    [Fact]
    public void InstalledCffFace_LoadsInDirectWriteAndWpf_WithItsShapingTables()
    {
        using var folder = new TempFolder();
        folder.Add("SyntheticHanSans-Regular.otf", CffOpenTypeFont.Font(family: Family, postScriptName: "SyntheticHanSans-Regular"));
        var face = Assert.Single(SystemFontCatalog.ForDirectory(folder.Path).Faces);

        using var resources = new PdfEngine.Vector.Direct2D.D2DResources(64L << 20);
        var dw = resources.GetInstalledFace(face, bold: false, italic: false);
        Assert.NotNull(dw);
        Assert.Equal(CffOpenTypeFont.Advances.Length, dw.GlyphCount);
        var shaping = resources.GetShapingFont(dw);
        Assert.NotNull(shaping);
        Assert.True(shaping.IsCff);
        Assert.Equal(2, shaping.GlyphFor(0x4E2D));

        // WPF (printing, export) loads the file as a GlyphTypeface; its shaping tables are read too.
        using var wpf = new PdfEngine.Vector.Windows.WpfFontCache();
        var typeface = new System.Windows.Media.GlyphTypeface(new Uri(face.Path));
        Assert.Equal(CffOpenTypeFont.Advances.Length, typeface.GlyphCount);
        var wpfShaping = wpf.GetShapingFont(typeface);
        Assert.NotNull(wpfShaping);
        Assert.True(wpfShaping.IsCff);
    }

    [Fact]
    public void OtherCffFamilies_DrawNoPrivateUseCharacters_NoEmptyGlyphs_AndNoIconFonts()
    {
        using var folder = new TempFolder();
        File.Copy(Path.Combine(WindowsFonts, "arial.ttf"), Path.Combine(folder.Path, "arial.ttf"));
        // A text font that also has a private-use code (as converted Symbol text, U+F0B7, would find).
        folder.Add("Han.otf", CffOpenTypeFont.Font(family: Family, postScriptName: "SyntheticHanSans-Regular", map: new[] { (0x41, 1), (0x4E2D, 2), (0xF0B7, 2) }));
        // An icon font, by its name.
        folder.Add("Icons.otf", CffOpenTypeFont.Font(family: "Synthetic Icons", postScriptName: "SyntheticIcons", map: new[] { (0x4E8C, 2) }));
        // A blank font (as Adobe Blank): its glyph for the character has no outline.
        folder.Add("Empty.otf", CffOpenTypeFont.Font(family: "Synthetic Empty Sans", postScriptName: "SyntheticEmptySans", map: new[] { (0x4E09, 3) }));
        // A font with a symbol character map only.
        folder.Add("Legacy.otf", CffOpenTypeFont.Font(family: "Synthetic Legacy Sans", postScriptName: "SyntheticLegacySans", map: new[] { (0x4E5D, 2), (0xF04E, 2) }, symbol: true));
        var catalog = SystemFontCatalog.ForDirectory(folder.Path);
        Assert.Equal(5, catalog.Faces.Count);
        Assert.DoesNotContain("Synthetic Icons", catalog.Fallbacks(false, false).Select(f => f.Family));

        int Missing(string text) => PdfEngine.Vector.Annotations.PdfAnnotationAppearances.MeasureFreeText(text, "Helv", 12, 400, fonts: catalog)!.Value.Missing;
        Assert.Equal(0, Missing("中"));
        Assert.Equal(1, Missing(""));
        Assert.Equal(1, Missing("二"));
        Assert.Equal(1, Missing("三"));
        Assert.Equal(1, Missing("九"));
        Assert.Equal(1, Missing("\U000F0001"));
        Assert.True(SystemFontCatalog.IsPrivateUse(0xE000) && SystemFontCatalog.IsPrivateUse(0x10FFFD) && !SystemFontCatalog.IsPrivateUse(0x4E2D));

        // A text document reports nothing missing for 中 and leaves the private-use character out (no CFF face for it).
        using var ms = new MemoryStream();
        PdfEngine.Vector.Create.PdfTextDocument.Write("", ms, new PdfEngine.Vector.Create.PdfTextLayoutOptions { FontFamily = "Arial" }, catalog: catalog);
        Assert.DoesNotContain("/FontFile3", System.Text.Encoding.Latin1.GetString(ms.ToArray()));
    }

    [Fact]
    public void Cff2OnlyFont_IsNotCatalogued()
    {
        // A variable CFF font cannot be loaded for new text; matching must not choose it.
        byte[] data = CffOpenTypeFont.Font(family: "Synthetic Variable Sans", postScriptName: "SyntheticVariableSans");
        int tables = data[4] << 8 | data[5];
        for (int i = 0; i < tables; i++)
            if (data.AsSpan(12 + i * 16, 4).SequenceEqual("CFF "u8)) "CFF2"u8.CopyTo(data.AsSpan(12 + i * 16));
        using var folder = new TempFolder();
        folder.Add("Variable.otf", data);
        Assert.Empty(SystemFontCatalog.ForDirectory(folder.Path).Faces);
    }

    [Fact]
    public async Task EditedText_LoadsOnlyTheFallbacksWhoseCharacterMapsHaveTheCharacter()
    {
        using var folder = new TempFolder();
        File.Copy(Path.Combine(WindowsFonts, "arial.ttf"), Path.Combine(folder.Path, "arial.ttf"));
        // Before the Han font by name, without 中.
        folder.Add("Alpha.otf", CffOpenTypeFont.Font(family: "Synthetic Alpha Sans", postScriptName: "SyntheticAlphaSans", map: new[] { (0x41, 1) }));
        folder.Add("Han.otf", CffOpenTypeFont.Font(family: Family, postScriptName: "SyntheticHanSans-Regular"));
        var catalog = SystemFontCatalog.ForDirectory(folder.Path);
        var alpha = catalog.Faces.Single(f => f.Family == "Synthetic Alpha Sans");
        var han = catalog.Faces.Single(f => f.Family == Family);
        Assert.Equal(new[] { "Arial", "Synthetic Alpha Sans", Family }, catalog.Fallbacks(false, false).Select(f => f.Family));

        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 24 Tf 40 200 Td (AB) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var text = Assert.Single(PdfContentEditor.Read(doc, 1).Texts);
        var result = PdfContentEditor.Apply(doc, pdf, new PdfContentEdit[] { new PdfReplaceText(1, text.Id, "A中B") }, new PdfContentEditOptions { Fonts = catalog });
        Assert.Empty(result.Warnings);
        Assert.True(catalog.IsLoaded(han));
        Assert.False(catalog.IsLoaded(alpha));
        using var after = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Contains("A中B", PdfTextExtractor.LoadPage(after, 1).Text);
    }

    /// <summary>Adds the text in Arial at 100 pt to an empty page; the saved file and, per glyph, its character and font program format.</summary>
    private static async Task<(byte[] Pdf, (string, PdfFontProgramFormat)[] Formats)> AddText(SystemFontCatalog catalog, string text)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", "<< >>", mediaBox: "[0 0 400 400]");
        byte[] blank = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(blank);
        var result = PdfContentEditor.Apply(doc, blank, new PdfContentEdit[] { new PdfAddText(1, new PdfPoint(40, 200), text, new PdfTextFormat("Arial", 100)) },
            new PdfContentEditOptions { Fonts = catalog });
        Assert.Empty(result.Warnings);
        using var after = await PdfVectorDocument.OpenAsync(result.Bytes);
        var list = await after.GetPageDisplayListAsync(1);
        var formats = list.Commands.OfType<DrawGlyphRun>()
            .SelectMany(c => c.Run.Glyphs.Select(g => (g.Unicode ?? "", c.Run.Face!.Format))).ToArray();
        return (result.Bytes, formats);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("desktop-cff").FullName;

        public void Add(string name, byte[] data) => File.WriteAllBytes(System.IO.Path.Combine(Path, name), data);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
