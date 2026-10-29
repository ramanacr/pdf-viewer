using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Demo;
using SkiaSharp;
using Xunit;
using PdfEngine.Vector.Fonts;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The web demo's engine layer (src/PdfViewer.Web.Engine), run on the desktop: the built-in tour
/// document, rendering, search highlights, the outline, open failures, the page cache and font
/// sources like the browser's (no installed fonts, or the device fonts the user lends the page).
/// </summary>
public class WebViewerEngineTests
{
    private static byte[] HelveticaPage(string text, string mediaBox = "[0 0 300 200]", string extra = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage($"BT /F1 24 Tf 20 100 Td ({text}) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>", mediaBox: mediaBox, extra: extra);
        return b.Build();
    }

    /// <summary>Pixels darker than light grey (ink) in a BGRA bitmap.</summary>
    private static int Ink(ViewerBitmap bitmap)
    {
        int ink = 0;
        var px = bitmap.Pixels.AsSpan();
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] + px[i + 1] + px[i + 2] < 3 * 160) ink++;
        return ink;
    }

    [Fact]
    public async Task DemoDocument_OpensWithItsOutlineAndRendersWithoutFallback()
    {
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create());
        Assert.Equal(4, doc.PageCount);
        Assert.All(doc.Pages, p => Assert.Equal((612.0, 792.0), (p.Width, p.Height)));
        Assert.Equal("PDF Viewer Native: engine tour", doc.Title);
        var outline = await doc.GetOutlineAsync();
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, outline.Select(o => o.Page));
        Assert.Equal("Paths, strokes and clipping", outline[1].Title);

        for (int page = 1; page <= doc.PageCount; page++)
        {
            var bitmap = await doc.RenderPageAsync(page, 1.0);
            Assert.Equal((612, 792), (bitmap.Width, bitmap.Height));
            Assert.Equal(0, bitmap.UnsupportedRegions);
            Assert.True(Ink(bitmap) > 2000, $"page {page} looks empty");
        }
    }

    [Fact]
    public async Task DemoDocument_TextIsExtractedAndSearchable()
    {
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create());
        string text = (await doc.GetPageTextAsync(1)).Replace("\r\n", " ");
        Assert.Contains("running in your browser as WebAssembly", text);
        foreach (var word in DemoDocument.SearchExamples)
        {
            var hits = new List<ViewerSearchHit>();
            await foreach (var hit in doc.SearchAsync(word)) hits.Add(hit);
            Assert.NotEmpty(hits);
            Assert.All(hits, h =>
            {
                Assert.Equal(word, h.Match, ignoreCase: true);
                var r = Assert.Single(h.Rects);
                Assert.InRange(r.X, 0, 612);
                Assert.InRange(r.Y, 0, 792);
                Assert.InRange(r.Width, 1, 200);
                Assert.InRange(r.Height, 4, 40);
            });
        }
        var cased = new List<ViewerSearchHit>();
        await foreach (var hit in doc.SearchAsync("skia", matchCase: true)) cased.Add(hit);
        Assert.Empty(cased);
    }

    [Fact]
    public async Task SearchHighlight_SitsOnTheGlyphs()
    {
        await using var doc = await ViewerDocument.OpenAsync(HelveticaPage("Find me here"));
        var hit = Assert.Single(await doc.SearchPageAsync(1, "me"));
        var r = Assert.Single(hit.Rects);
        // Baseline at y = 100 of a 200 pt page: the box spans it, a little above and below.
        Assert.InRange(r.Y, 70, 100);
        Assert.InRange(r.Y + r.Height, 100, 110);
        Assert.InRange(r.X, 70, 110);
        Assert.Equal("Find ", hit.Before);
        Assert.Equal(" here", hit.After);

        // The rendered page has ink inside the highlight.
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        int ink = 0;
        for (int y = (int)r.Y; y < (int)(r.Y + r.Height); y++)
            for (int x = (int)r.X; x < (int)(r.X + r.Width); x++)
            {
                int i = (y * bitmap.Width + x) * 4;
                if (bitmap.Pixels[i] < 128) ink++;
            }
        Assert.True(ink > 30, "no glyph under the highlight");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task SearchHighlight_FollowsPageRotation(int rotate)
    {
        await using var doc = await ViewerDocument.OpenAsync(HelveticaPage("Rotated", extra: $"/Rotate {rotate}"));
        var page = doc.Pages[0];
        Assert.Equal(rotate == 180 ? (300.0, 200.0) : (200.0, 300.0), (page.Width, page.Height));
        var r = Assert.Single(Assert.Single(await doc.SearchPageAsync(1, "Rotated")).Rects);
        Assert.InRange(r.X, 0, page.Width);
        Assert.InRange(r.Y, 0, page.Height);
        Assert.True(rotate == 180 ? r.Width > r.Height : r.Height > r.Width, "the highlight does not turn with the page");

        // Ink under the highlight on the rendered (rotated) page.
        var bitmap = await doc.RenderPageAsync(1, 1.0);
        Assert.Equal(((int)page.Width, (int)page.Height), (bitmap.Width, bitmap.Height));
        int ink = 0;
        for (int y = (int)r.Y; y < (int)(r.Y + r.Height) && y < bitmap.Height; y++)
            for (int x = (int)r.X; x < (int)(r.X + r.Width) && x < bitmap.Width; x++)
                if (bitmap.Pixels[(y * bitmap.Width + x) * 4] < 128) ink++;
        Assert.True(ink > 30, $"no glyph under the highlight at /Rotate {rotate}");
    }

    [Fact]
    public async Task NightRender_IsTheInvertedDayRender()
    {
        await using var doc = await ViewerDocument.OpenAsync(HelveticaPage("Night"));
        var day = await doc.RenderPageAsync(1, 1.5);
        var night = await doc.RenderPageAsync(1, 1.5, night: true);
        for (int i = 0; i < day.Pixels.Count; i += 4)
        {
            Assert.Equal(255 - day.Pixels[i], night.Pixels[i]);
            Assert.Equal(day.Pixels[i + 3], night.Pixels[i + 3]);
        }
    }

    [Fact]
    public async Task Render_IsCappedAtTheLargestBitmap()
    {
        await using var doc = await ViewerDocument.OpenAsync(HelveticaPage("Big"), options: new ViewerOptions { MaxBitmapPixels = 300 * 200 * 4 });
        var bitmap = await doc.RenderPageAsync(1, 10.0);
        Assert.True((long)bitmap.Width * bitmap.Height <= 300 * 200 * 4);
        Assert.InRange(bitmap.Scale, 1.9, 2.0);
    }

    [Fact]
    public async Task Open_ReportsWhyItFails()
    {
        var notPdf = await Assert.ThrowsAsync<ViewerOpenException>(() => ViewerDocument.OpenAsync("not a PDF at all"u8.ToArray()));
        Assert.Equal(ViewerOpenFailure.NotReadable, notPdf.Failure);
        Assert.NotEmpty(notPdf.Detail);

        var large = await Assert.ThrowsAsync<ViewerOpenException>(() => ViewerDocument.OpenAsync(new byte[100], options: new ViewerOptions { MaxFileBytes = 10 }));
        Assert.Equal(ViewerOpenFailure.TooLarge, large.Failure);

        var b = new VectorPdfBuilder();
        b.AddPage("BT /F1 12 Tf 20 100 Td (Secret) Tj ET", "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        byte[] encrypted = b.Build(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner"));
        var required = await Assert.ThrowsAsync<ViewerOpenException>(() => ViewerDocument.OpenAsync(encrypted));
        Assert.Equal(ViewerOpenFailure.PasswordRequired, required.Failure);
        var wrong = await Assert.ThrowsAsync<ViewerOpenException>(() => ViewerDocument.OpenAsync(encrypted, "nope"));
        Assert.Equal(ViewerOpenFailure.WrongPassword, wrong.Failure);
        await using var opened = await ViewerDocument.OpenAsync(encrypted, "user");
        Assert.True(opened.IsEncrypted);
        Assert.Equal("Secret", await opened.GetPageTextAsync(1));
        // Messages are fit to show and never quote the document.
        Assert.DoesNotContain("Secret", required.Message + wrong.Message + notPdf.Message);
    }

    [Fact]
    public void PlatformCrypto_OnTheDesktop_HasEverything()
    {
        Assert.All(PlatformCrypto.Primitives, p => Assert.True(p.Available, p.Name));
        Assert.True(PlatformCrypto.CanDecrypt);
    }

    /// <summary>Fonts like a browser's: nothing installed, and no family lookup.</summary>
    private sealed class BrowserLikeFonts(string directory) : SkiaFontSource(SystemFontCatalog.ForDirectory(directory))
    {
        public override SKTypeface? MatchFamily(string family, bool bold, bool italic) => null;
    }

    [Fact]
    public async Task NonEmbeddedText_NeedsFonts_AndDeviceFontsDrawIt()
    {
        string empty = Directory.CreateTempSubdirectory("webviewer-nofonts").FullName;
        string lent = Directory.CreateTempSubdirectory("webviewer-devicefonts").FullName;
        try
        {
            await using var doc = await ViewerDocument.OpenAsync(HelveticaPage("Lend me fonts"), options: new ViewerOptions { Fonts = new BrowserLikeFonts(empty) });
            var without = await doc.RenderPageAsync(1, 1.0);
            Assert.True(without.TextRegionsWithoutFont > 0);
            Assert.Equal(0, Ink(without));

            // What the web viewer does with fonts from the Local Font Access API: files in a
            // directory, read through the catalog (Helvetica is matched to Arial by design).
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"), Path.Combine(lent, "ArialMT.ttf"));
            await doc.UseFontsAsync(new BrowserLikeFonts(lent));
            var with = await doc.RenderPageAsync(1, 1.0);
            Assert.Equal(0, with.TextRegionsWithoutFont);
            Assert.True(Ink(with) > 200, "the lent font drew nothing");
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
            try { Directory.Delete(lent, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the font may still be mapped */ }
        }
    }

    [Fact]
    public void PageBitmapCache_KeepsTheMostRecentWithinItsBudget()
    {
        static ViewerBitmap Bitmap(int page, int bytes) =>
            new(page, 1, bytes / 4, 1, new ArraySegment<byte>(new byte[bytes]), TimeSpan.Zero, TimeSpan.Zero, []);

        var cache = new PageBitmapCache(1000);
        cache.Add(1, 1.0, false, Bitmap(1, 400));
        cache.Add(2, 1.0, false, Bitmap(2, 400));
        Assert.True(cache.TryGet(1, 1.0, false, out _)); // page 1 is now the most recent
        cache.Add(3, 1.0, false, Bitmap(3, 400));
        Assert.Equal(800, cache.Bytes);
        Assert.False(cache.TryGet(2, 1.0, false, out _));
        Assert.True(cache.TryGet(1, 1.0, false, out _));
        Assert.True(cache.TryGet(3, 1.0, false, out _));

        // Scale and night mode are part of the key.
        Assert.False(cache.TryGet(3, 1.5, false, out _));
        Assert.False(cache.TryGet(3, 1.0, true, out _));

        // A bitmap larger than the budget stays, alone.
        cache.Add(4, 1.0, false, Bitmap(4, 4000));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(4, 1.0, false, out _));
        cache.Clear();
        Assert.Equal(0, cache.Bytes);
    }
}
