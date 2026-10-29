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
