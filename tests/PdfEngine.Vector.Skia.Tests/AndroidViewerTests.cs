using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using SkiaSharp;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The platform-neutral parts of the Android viewer (src/PdfViewer.Android), run on the desktop:
/// the continuous page layout, the page image cache, and font substitution through a catalog with
/// family aliases, as Android's /system/fonts catalog has.
/// </summary>
public class AndroidViewerTests
{
    private static readonly ViewerPage[] Letter3 = { new(1, 612, 792), new(2, 612, 792), new(3, 612, 792) };

    [Fact]
    public void Layout_FitsWidth_StacksPagesAndFindsTheCurrentPage()
    {
        var layout = new ContinuousPageLayout(Letter3, gap: 20, margin: 10);
        layout.Resize(632, 800);
        Assert.Equal(1.0, layout.PixelsPerPoint, 6);
        Assert.Equal(new LayoutRect(10, 10, 612, 792), layout.PageRect(1));
        Assert.Equal(10 + 792 + 20, layout.PageRect(2).Y, 6);
        Assert.Equal(10 + 3 * 792 + 2 * 20 + 10, layout.ContentHeight, 6);
        Assert.Equal(new[] { 1 }, layout.VisiblePages()); // page 2 starts at 822, below the 800-pixel view
        Assert.Equal(new[] { 1, 2 }, layout.VisiblePages(extra: 30));
        Assert.Equal(1, layout.CurrentPage);

        layout.GoTo(3);
        Assert.Equal(3, layout.CurrentPage);
        Assert.Equal(layout.PageRect(3).Y - 10, layout.ScrollY, 6); // half the gap above the page
        Assert.Equal(new[] { 3 }, layout.VisiblePages());
        Assert.Equal(new[] { 1, 2, 3 }, layout.VisiblePages(extra: 2000));

        layout.ScrollTo(-50, 1e9);
        Assert.Equal((0.0, layout.MaxScrollY), (layout.ScrollX, layout.ScrollY));
    }

    [Fact]
    public void Layout_ZoomKeepsThePointUnderTheFingers()
    {
        var layout = new ContinuousPageLayout(Letter3, gap: 20, margin: 10);
        layout.Resize(632, 800);
        layout.ScrollTo(0, 900);
        var before = layout.AnchorAt(300, 400);
        layout.ZoomAt(2.5, 300, 400);
        Assert.Equal(2.5, layout.Zoom);
        Assert.Equal(2.5, layout.PixelsPerPoint, 6);
        var after = layout.AnchorAt(300, 400);
        Assert.Equal(before.Page, after.Page);
        Assert.Equal(before.U, after.U, 3);
        Assert.Equal(before.V, after.V, 3);
        Assert.True(layout.MaxScrollX > 0);

        layout.ZoomAt(100, 0, 0);
        Assert.Equal(ContinuousPageLayout.MaxZoom, layout.Zoom);
        layout.ZoomAt(0.01, 0, 0);
        Assert.Equal(ContinuousPageLayout.MinZoom, layout.Zoom);
        Assert.Equal(0, layout.MaxScrollX); // narrower than the view: centred, no sideways scroll
    }

    [Fact]
    public void Layout_RotationKeepsThePageInViewAndMapsSearchRects()
    {
        var pages = new[] { new ViewerPage(1, 612, 792), new ViewerPage(2, 792, 612), new ViewerPage(3, 612, 792) };
        var layout = new ContinuousPageLayout(pages, gap: 0, margin: 0);
        layout.Resize(792, 1000);
        // The widest page decides the fit: the landscape page fills the width, the others are centred.
        Assert.Equal(1.0, layout.PixelsPerPoint, 6);
        Assert.Equal(90, layout.PageRect(1).X, 6);
        layout.GoTo(2);
        Assert.Equal(2, layout.AnchorAt(0, 0).Page);
        layout.Resize(1000, 400); // a phone turned sideways
        Assert.Equal(2, layout.AnchorAt(0, 0).Page);

        var rect = layout.ToView(2, new ViewerRect(10, 20, 30, 40));
        var page = layout.PageRectInView(2);
        double s = layout.PixelsPerPoint;
        Assert.Equal(page.X + 10 * s, rect.X, 6);
        Assert.Equal(page.Y + 20 * s, rect.Y, 6);
        Assert.Equal((30 * s, 40 * s), (rect.Width, rect.Height));
    }

    [Fact]
    public void Layout_CurrentPage_InTheGapIsTheNearerPage()
    {
        var layout = new ContinuousPageLayout(Letter3, gap: 100, margin: 0);
        layout.Resize(612, 200);
        // Page 1 ends at 792, page 2 starts at 892; the view's middle is ScrollY + 100.
        layout.ScrollTo(0, 802 - 100);
        Assert.Equal(1, layout.CurrentPage);
        layout.ScrollTo(0, 882 - 100);
        Assert.Equal(2, layout.CurrentPage);
    }

    private sealed class Image(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void ImageCache_KeepsTheNewestImagePerPage_AndDisposesWhatItDrops()
    {
        var cache = new PageImageCache<Image>(maxBytes: 300);
        Image a = new(1), b = new(2), c = new(3), d = new(4), big = new(5);
        cache.Add(1, false, 1.0, a, 100);
        cache.Add(2, false, 1.0, b, 100);
        Assert.True(cache.Has(1, false, 1.0004));
        Assert.False(cache.Has(1, false, 2.0));
        Assert.False(cache.Has(1, true, 1.0));

        // A new zoom replaces the page's old image: it was only a placeholder.
        cache.Add(1, false, 2.0, c, 100);
        Assert.True(a.Disposed);
        Assert.True(cache.TryGet(1, false, out var got, out double scale));
        Assert.Same(c, got);
        Assert.Equal(2.0, scale);
        Assert.Equal(200, cache.Bytes);

        // Over budget: the least recently used goes (page 2; page 1 was just read).
        cache.Add(3, false, 1.0, d, 150);
        Assert.True(b.Disposed);
        Assert.False(cache.TryGet(2, false, out _, out _));
        Assert.Equal(250, cache.Bytes);

        // One image larger than the budget is kept, alone.
        cache.Add(4, false, 1.0, big, 1000);
        Assert.Equal(1, cache.Count);
        Assert.False(big.Disposed);
        Assert.True(c.Disposed && d.Disposed);

        // Adding the same image again does not dispose it.
        cache.Add(4, false, 1.5, big, 1000);
        Assert.False(big.Disposed);

        cache.Clear();
        Assert.True(big.Disposed);
        Assert.Equal((0, 0L), (cache.Count, cache.Bytes));
    }

    [Fact]
    public async Task NonEmbeddedText_DrawsWithAnAliasedStandIn_AsOnAndroid()
    {
        // As on Android: the catalog lacks Arial and Helvetica, and an alias names the family that stands in.
        string dir = Directory.CreateTempSubdirectory("android-fonts").FullName;
        try
        {
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "georgia.ttf"), Path.Combine(dir, "georgia.ttf"));
            var catalog = SystemFontCatalog.ForDirectory(dir).WithFamilyAliases(new Dictionary<string, string[]> { ["Arial"] = new[] { "Georgia" } });
            var b = new VectorPdfBuilder();
            b.AddPage("BT /F1 24 Tf 20 100 Td (Roboto stands in) Tj ET",
                "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>", mediaBox: "[0 0 300 200]");
            await using var doc = await ViewerDocument.OpenAsync(b.Build(), options: new ViewerOptions { Fonts = new AliasOnlyFonts(catalog) });
            var bitmap = await doc.RenderPageAsync(1, 1.0);
            Assert.Equal(0, bitmap.TextRegionsWithoutFont);
            int ink = 0;
            var px = bitmap.Pixels.AsSpan();
            for (int i = 0; i < px.Length; i += 4)
                if (px[i] + px[i + 1] + px[i + 2] < 3 * 160) ink++;
            Assert.True(ink > 200, $"ink {ink}");
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public void FontSource_MatchFamily_FallsBackToTheCatalogsAliases()
    {
        string dir = Directory.CreateTempSubdirectory("android-fonts").FullName;
        try
        {
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "georgia.ttf"), Path.Combine(dir, "georgia.ttf"));
            var catalog = SystemFontCatalog.ForDirectory(dir).WithFamilyAliases(new Dictionary<string, string[]> { ["No Such Family 7"] = new[] { "Georgia" } });
            var source = new SkiaFontSource(catalog);
            using var standIn = source.MatchFamily("No Such Family 7", bold: false, italic: false);
            Assert.Equal("Georgia", standIn?.FamilyName);
            // Without an alias, a family Skia's font manager does not have stays missing.
            Assert.Null(new SkiaFontSource(SystemFontCatalog.ForDirectory(dir)).MatchFamily("No Such Family 7", false, false));
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public void VariableFont_BoldIsTheFontManagersInstance_NotAnEmboldenedRegular()
    {
        // Windows' Bahnschrift is one variable font, as Roboto is on Android 12 and later. The
        // DirectWrite font manager names its weights as Android's reads them from fonts.xml.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "bahnschrift.ttf");
        if (!File.Exists(path)) return;
        var face = new SystemFontFace(path, 0, "Bahnschrift", "Regular", "Bahnschrift", 400, false);
        using var bold = SkiaFontSource.FindInstance(SKFontManager.Default, face, 700, italic: false);
        Assert.NotNull(bold);
        Assert.InRange(bold!.FontWeight, 650, 750);
        using var regular = SKTypeface.FromFile(path);
        using var boldFont = new SKFont(bold, 100);
        using var regularFont = new SKFont(regular, 100);
        // The bold design has heavier stems than the regular one.
        int boldInk = Ink(boldFont), regularInk = Ink(regularFont);
        Assert.True(boldInk > regularInk * 1.15, $"ink {boldInk} vs {regularInk}"); // 10203 against 7916 when written
        // Asked again: the same instance, from the remembered style.
        using var again = SkiaFontSource.FindInstance(SKFontManager.Default, face, 700, italic: false);
        Assert.Equal(bold.FontWeight, again!.FontWeight);

        // A static font has no instances to find.
        var georgia = new SystemFontFace(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "georgia.ttf"), 0, "Georgia", "Regular", "Georgia", 400, false);
        Assert.Null(SkiaFontSource.FindInstance(SKFontManager.Default, georgia, 700, italic: false));
    }

    /// <summary>Dark pixels of "Weight" drawn with <paramref name="font"/>.</summary>
    private static int Ink(SKFont font)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(500, 150, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("Weight", 10, 110, font, paint);
        }
        int ink = 0;
        foreach (byte b in bitmap.Bytes) if (b < 128) ink++;
        return ink;
    }

    /// <summary>Skia may still map a font file until its typeface is collected; the temporary folder is then left to the OS.</summary>
    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The default source without Skia's font manager (the Windows one would find Arial): only the catalog and its aliases.</summary>
    private sealed class AliasOnlyFonts(SystemFontCatalog catalog) : SkiaFontSource(catalog)
    {
        public override SKTypeface? MatchFamily(string family, bool bold, bool italic) =>
            Catalog.FindAliasedFamily(family, bold ? 700 : 400, italic) is { } face ? Load(face) : null;
    }
}
