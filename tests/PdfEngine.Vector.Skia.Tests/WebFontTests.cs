using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Demo;
using Xunit;
using PdfEngine.Vector.Fonts;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The web demo's bundled fonts (src/PdfViewer.Web/wwwroot/fonts, Liberation 2.1.5): the faces the
/// catalog describes, the families they stand in for, fetching on demand (a fake fetch counts the
/// requests) and pages drawn with the files read from disk through the same font source.
/// </summary>
public class WebFontTests
{
    private static readonly string FontDirectory = Path.Combine(RepositoryRoot(), "src", "PdfViewer.Web", "wwwroot", "fonts");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("PdfViewer.slnx not found above the test output.");
    }

    /// <summary>A fetch that reads the bundled files from disk and records what was asked for.</summary>
    private sealed class DiskFetch
    {
        public List<string> Requests { get; } = new();
        public bool Fail { get; init; }

        public Task<byte[]?> FetchAsync(string file, CancellationToken cancellationToken)
        {
            Requests.Add(file);
            return Task.FromResult(Fail ? null : File.ReadAllBytes(Path.Combine(FontDirectory, file)));
        }
    }

    private static SystemFontCatalog Catalog => SystemFontCatalog.FromFaces(OnDemandFontSource.LiberationFaces, SystemFontCatalog.LiberationFamilyAliases);

    /// <summary>A page with one line per font resource, each 24 pt, from the top.</summary>
    private static byte[] TextPage(params (string BaseFont, string Text)[] lines)
    {
        var fonts = string.Join(" ", lines.Select((l, i) => $"/F{i} << /Type /Font /Subtype /Type1 /BaseFont /{l.BaseFont} >>"));
        var content = string.Join(" ", lines.Select((l, i) => $"BT /F{i} 24 Tf 20 {360 - 40 * i} Td ({l.Text}) Tj ET"));
        var b = new VectorPdfBuilder();
        b.AddPage(content, $"<< /Font << {fonts} >> >>", mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    /// <summary>Ink pixels (darker than light grey) and their horizontal extent in rows [top, bottom).</summary>
    private static (int Ink, int Left, int Right) Band(ViewerBitmap bitmap, int top, int bottom)
    {
        int ink = 0, left = int.MaxValue, right = -1;
        var px = bitmap.Pixels.AsSpan();
        for (int y = Math.Max(0, top); y < Math.Min(bitmap.Height, bottom); y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                int i = (y * bitmap.Width + x) * 4;
                if (px[i] + px[i + 1] + px[i + 2] >= 3 * 160) continue;
                ink++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
        return (ink, left, right);
    }

    private static int Ink(ViewerBitmap bitmap) => Band(bitmap, 0, bitmap.Height).Ink;

    [Fact]
    public void BundledFiles_AreTheFacesTheCatalogDescribes()
    {
        // Read the name and OS/2 tables of the committed files, as the catalog of a font folder does.
        var scanned = SystemFontCatalog.ForDirectory(FontDirectory).Faces
            .Select(f => (Path.GetFileName(f.Path), f.Index, f.Family, f.Subfamily, f.PostScriptName, f.Weight, f.Italic))
            .OrderBy(f => f.Item1, StringComparer.Ordinal).ToArray();
        var described = OnDemandFontSource.LiberationFaces
            .Select(f => (f.Path, f.Index, f.Family, f.Subfamily, f.PostScriptName, f.Weight, f.Italic))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(12, described.Length);
        Assert.Equal(described, scanned);
        Assert.True(File.Exists(Path.Combine(FontDirectory, "OFL.txt")), "the SIL Open Font License ships with the fonts");
    }

    [Theory]
    // The standard 14 names and their common spellings, by design and style.
    [InlineData("Helvetica", false, false, "Liberation Sans", 400, false)]
    [InlineData("Helvetica-Bold", true, false, "Liberation Sans", 700, false)]
    [InlineData("Helvetica-Oblique", false, true, "Liberation Sans", 400, true)]
    [InlineData("Helvetica-BoldOblique", true, true, "Liberation Sans", 700, true)]
    [InlineData("Arial", false, false, "Liberation Sans", 400, false)]
    [InlineData("ArialMT", false, false, "Liberation Sans", 400, false)]
    [InlineData("Arial,Bold", false, false, "Liberation Sans", 700, false)]
    [InlineData("Arial-BoldItalicMT", false, false, "Liberation Sans", 700, true)]
    [InlineData("Times-Roman", false, false, "Liberation Serif", 400, false)]
    [InlineData("Times-Bold", true, false, "Liberation Serif", 700, false)]
    [InlineData("Times-Italic", false, true, "Liberation Serif", 400, true)]
    [InlineData("Times-BoldItalic", true, true, "Liberation Serif", 700, true)]
    [InlineData("TimesNewRomanPSMT", false, false, "Liberation Serif", 400, false)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", false, false, "Liberation Serif", 700, true)]
    [InlineData("Courier", false, false, "Liberation Mono", 400, false)]
    [InlineData("Courier-Bold", true, false, "Liberation Mono", 700, false)]
    [InlineData("Courier-Oblique", false, true, "Liberation Mono", 400, true)]
    [InlineData("Courier-BoldOblique", true, true, "Liberation Mono", 700, true)]
    [InlineData("CourierNewPSMT", false, false, "Liberation Mono", 400, false)]
    [InlineData("CourierNewPS-BoldMT", false, false, "Liberation Mono", 700, false)]
    // The bundled family by its own name.
    [InlineData("LiberationSerif-Italic", false, false, "Liberation Serif", 400, true)]
    public void StandardAndCommonNames_MapToTheLiberationFace(string baseFont, bool bold, bool italic, string family, int weight, bool italicFace)
    {
        var face = Catalog.Match(baseFont, bold, italic, serif: false, fixedPitch: false);
        Assert.NotNull(face);
        Assert.Equal((family, weight, italicFace), (face!.Family, face.Weight, face.Italic));
    }

    [Theory]
    // Unknown names go by their class (descriptor flags), as on Windows: sans, serif, monospaced.
    [InlineData(false, false, false, false, "Liberation Sans", 400, false)]
    [InlineData(true, false, false, false, "Liberation Serif", 400, false)]
    [InlineData(true, false, true, true, "Liberation Serif", 700, true)]
    [InlineData(false, true, false, false, "Liberation Mono", 400, false)]
    [InlineData(false, false, true, false, "Liberation Sans", 700, false)]
    public void UnknownFonts_MapByClassAndStyle(bool serif, bool fixedPitch, bool bold, bool italic, string family, int weight, bool italicFace)
    {
        var face = Catalog.Match("SomeCorporateFont", bold, italic, serif, fixedPitch);
        Assert.NotNull(face);
        Assert.Equal((family, weight, italicFace), (face!.Family, face.Weight, face.Italic));
    }

    [Fact]
    public void ClassFamilies_StandInByAlias_AndSymbolFamiliesAreNotMapped()
    {
        var catalog = Catalog;
        Assert.Equal("Liberation Sans", catalog.FindFamily("Arial", false, false)?.Family);
        Assert.Equal("Liberation Serif", catalog.FindFamily("Times New Roman", true, true)?.Family);
        Assert.Equal("Liberation Mono", catalog.FindFamily("Courier New", false, true)?.Family);
        // No Liberation equivalent: Symbol and the Dingbats stand-in keep the existing substitute chain.
        Assert.Null(catalog.FindFamily("Symbol", false, false));
        Assert.Null(catalog.FindFamily("Segoe UI Symbol", false, false));
        Assert.Null(catalog.FindFamily("Wingdings", false, false));
    }

    [Fact]
    public async Task EmbeddedOrType3Fonts_FetchNothing()
    {
        var fetch = new DiskFetch();
        var fonts = OnDemandFontSource.Liberation(fetch.FetchAsync);
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create(), options: new ViewerOptions { Fonts = fonts });
        for (int page = 1; page <= doc.PageCount; page++)
            Assert.Equal(0, (await doc.RenderPageAsync(page, 0.5)).UnsupportedRegions);
        Assert.Empty(fetch.Requests);
        Assert.False(fonts.HasPendingRequests);
    }

    [Fact]
    public async Task NonEmbeddedFont_FetchesOnlyTheFaceItIsDrawnWith_Once()
    {
        var fetch = new DiskFetch();
        var fonts = OnDemandFontSource.Liberation(fetch.FetchAsync);
        await using (var doc = await ViewerDocument.OpenAsync(TextPage(("Times-Bold", "Fetched on demand")), options: new ViewerOptions { Fonts = fonts }))
        {
            var bitmap = await doc.RenderPageAsync(1, 1.0);
            Assert.Equal(0, bitmap.TextRegionsWithoutFont);
            Assert.True(Ink(bitmap) > 300, "the fetched face drew nothing");
            // Not the Unicode fallbacks (Liberation Sans for Arial) further down the chain.
            Assert.Equal(new[] { "LiberationSerif-Bold.ttf" }, fetch.Requests);

            await doc.RenderPageAsync(1, 2.0);
            await doc.RenderPageAsync(1, 1.0, night: true);
        }
        // Kept for the session: another document with the same font fetches nothing more.
        await using (var other = await ViewerDocument.OpenAsync(TextPage(("Times-Bold", "Again")), options: new ViewerOptions { Fonts = fonts }))
            Assert.Equal(0, (await other.RenderPageAsync(1, 1.0)).TextRegionsWithoutFont);
        Assert.Single(fetch.Requests);
        Assert.Equal(new[] { "LiberationSerif-Bold.ttf" }, fonts.LoadedFiles);
    }

    [Fact]
    public async Task FailedFetch_IsReportedAndNotRetried()
    {
        var fetch = new DiskFetch { Fail = true };
        var fonts = OnDemandFontSource.Liberation(fetch.FetchAsync);
        await using var doc = await ViewerDocument.OpenAsync(TextPage(("Helvetica", "Offline")), options: new ViewerOptions { Fonts = fonts });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.True(bitmap.TextRegionsWithoutFont > 0, "text without a font must still be reported (the banner)");
        Assert.Equal(0, Ink(bitmap));
        await doc.RenderPageAsync(1, 1.5);
        Assert.Equal(new[] { "LiberationSans-Regular.ttf" }, fetch.Requests);
        Assert.False(fonts.HasPendingRequests);
    }

    [Fact]
    public async Task ZapfDingbats_HasNoLiberationEquivalent_AndStaysReported()
    {
        var fetch = new DiskFetch();
        var fonts = OnDemandFontSource.Liberation(fetch.FetchAsync);
        // ZapfDingbats codes 0x33 and 0x34: check marks (U+2713, U+2714), which Liberation Sans lacks.
        await using var doc = await ViewerDocument.OpenAsync(TextPage(("ZapfDingbats", "34")), options: new ViewerOptions { Fonts = fonts });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.True(bitmap.TextRegionsWithoutFont > 0, "Dingbats text that no bundled face draws must keep the banner");
    }

    [Fact]
    public async Task Symbol_IsNotMapped_ButItsGreekDrawsThroughTheUnicodeFallback()
    {
        var fetch = new DiskFetch();
        var fonts = OnDemandFontSource.Liberation(fetch.FetchAsync);
        // Symbol codes a, b, g: alpha, beta, gamma. The chain asks for Symbol, then for faces that have
        // the characters: Arial among them, which Liberation Sans stands in for, as on Windows without Symbol.
        await using var doc = await ViewerDocument.OpenAsync(TextPage(("Symbol", "abg")), options: new ViewerOptions { Fonts = fonts });
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(0, bitmap.TextRegionsWithoutFont);
        Assert.True(Ink(bitmap) > 50, "the Greek letters drew nothing");
        Assert.Equal(new[] { "LiberationSans-Regular.ttf" }, fetch.Requests);
    }

    [Fact]
    public async Task Helvetica_Times_Courier_DrawWithLiberation_WhereWindowsFontsDraw()
    {
        var lines = new[]
        {
            ("Helvetica", "Helvetica as Liberation Sans"), ("Helvetica-BoldOblique", "Helvetica bold oblique"),
            ("Times-Roman", "Times as Liberation Serif"), ("Times-Italic", "Times italic text"),
            ("Courier", "Courier as Liberation Mono"), ("Courier-Bold", "Courier bold text"),
        };
        byte[] pdf = TextPage(lines);
        var fetch = new DiskFetch();
        await using var web = await ViewerDocument.OpenAsync(pdf, options: new ViewerOptions { Fonts = OnDemandFontSource.Liberation(fetch.FetchAsync) });
        // The reference: the same page with the installed Windows fonts (Arial, Times New Roman, Courier New).
        await using var windows = await ViewerDocument.OpenAsync(pdf);
        var liberation = await web.RenderPageAsync(1, 1.0);
        var installed = await windows.RenderPageAsync(1, 1.0);
        Assert.Equal(0, liberation.TextRegionsWithoutFont);
        Assert.Equal(0, installed.TextRegionsWithoutFont);
        Assert.Equal(
            new[] { "LiberationSans-Regular.ttf", "LiberationSans-BoldItalic.ttf", "LiberationSerif-Regular.ttf", "LiberationSerif-Italic.ttf", "LiberationMono-Regular.ttf", "LiberationMono-Bold.ttf" }.Order(),
            fetch.Requests.Order());

        for (int i = 0; i < lines.Length; i++)
        {
            // Line i's baseline is 360 - 40 i points from the bottom of a 400-point page.
            int baseline = 400 - (360 - 40 * i);
            var a = Band(liberation, baseline - 24, baseline + 8);
            var b = Band(installed, baseline - 24, baseline + 8);
            string what = $"{lines[i].Item1}: Liberation {a}, Windows {b}";
            Assert.True(a.Ink > 150, $"{what}: no text drawn");
            // Metric-compatible: the line starts and ends where the Windows font's does, within 2 pixels.
            Assert.True(Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Right - b.Right) <= 2, what);
            // Similar weight (Liberation Mono is darker than the thin Courier New, hence the wide range).
            Assert.True(a.Ink >= b.Ink * 0.6 && a.Ink <= b.Ink * 2.0, what);
        }
    }
}
