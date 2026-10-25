using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The platform-neutral parts of the Android reader (src/PdfViewer.Android), run on the desktop:
/// tiles for deep zoom, the tile cache, text selection and the copy permission, the outline list,
/// the thumbnail grid, the recent-documents file and the saved view.
/// </summary>
public class AndroidAppTests
{
    private static readonly ViewerPage Letter = new(1, 612, 792);

    // ------------------------------------------------------------------ tiles

    [Fact]
    public void Tiles_CoverTheViewOnly_AndAreCutAtThePageEdges()
    {
        // 8× zoom on a phone: about 14 pixels per point, a page of 8568 × 11088 pixels.
        double scale = 14;
        var (w, h) = PageTiles.PixelSize(Letter, scale);
        Assert.Equal((8568, 11088), (w, h));
        // The view (1080 × 1920) shows the page from pixel (3000, 5000).
        var pageInView = new LayoutRect(-3000, -5000, w, h);
        var tiles = PageTiles.Visible(Letter, scale, pageInView, 1080, 1920);
        Assert.Equal(new[] { 5, 6, 7 }, tiles.Select(t => t.Column).Distinct()); // 2560 … 4096 px
        Assert.Equal(new[] { 9, 10, 11, 12, 13 }, tiles.Select(t => t.Row).Distinct());
        Assert.All(tiles, t => Assert.Equal((512, 512), (t.Width, t.Height)));
        // Every view pixel is under a tile.
        Assert.True(tiles.Min(t => t.X) <= 3000 && tiles.Max(t => t.X + t.Width) >= 3000 + 1080);
        Assert.True(tiles.Min(t => t.Y) <= 5000 && tiles.Max(t => t.Y + t.Height) >= 5000 + 1920);

        // At the bottom-right corner the last tiles are cut to the page.
        var corner = PageTiles.Visible(Letter, scale, new LayoutRect(1080 - w, 1920 - h, w, h), 1080, 1920);
        var last = corner.MaxBy(t => (t.Row, t.Column));
        Assert.Equal((w - 16 * 512, h - 21 * 512), (last.Width, last.Height));
        Assert.Equal(new LayoutRect(1080 - w + 16 * 512, 1920 - h + 21 * 512, last.Width, last.Height), PageTiles.InView(last, Letter, scale, new LayoutRect(1080 - w, 1920 - h, w, h)));

        // While a pinch runs, the page is drawn at another size: the tiles of the old scale still cover the view.
        var half = new LayoutRect(-1500, -2500, w / 2.0, h / 2.0);
        var stretched = PageTiles.Visible(Letter, scale, half, 1080, 1920);
        Assert.True(stretched.Min(t => t.X) <= 3000 && stretched.Max(t => t.X + t.Width) >= 3000 + 2160);
        Assert.All(stretched, t => Assert.True(PageTiles.InView(t, Letter, scale, half).Right > 0));

        // A page beside the view has no visible tiles.
        Assert.Empty(PageTiles.Visible(Letter, scale, new LayoutRect(0, 2000, w, h), 1080, 1920));
        Assert.NotEmpty(PageTiles.Visible(Letter, scale, new LayoutRect(0, 2000, w, h), 1080, 1920, margin: 100));
    }

    [Fact]
    public void BaseScale_IsCappedByPixels_AndTilesAreNeededAboveIt()
    {
        Assert.Equal(1.7, PageTiles.BaseScale(Letter, 1.7, 2_500_000));
        double capped = PageTiles.BaseScale(Letter, 14, 2_500_000);
        Assert.True(612 * capped * 792 * capped <= 2_500_000);
        Assert.True(PageTiles.NeedsTiles(capped, 14));
        Assert.False(PageTiles.NeedsTiles(1.7, 1.7));
    }

    [Fact]
    public async Task Tiles_StitchToTheWholePage()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.2 0.4 0.8 rg 10 10 180 40 re f 1 0 0 RG 3 w 0 0 m 200 200 l S BT /F1 18 Tf 20 120 Td (Tiles stitch) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        await using var doc = await ViewerDocument.OpenAsync(b.Build());
        double scale = 3;
        var whole = await doc.RenderPageAsync(1, scale);
        var (w, h) = PageTiles.PixelSize(doc.Pages[0], scale);
        Assert.Equal((w, h), (whole.Width, whole.Height));
        var stitched = new byte[w * h * 4];
        var tiles = PageTiles.Visible(doc.Pages[0], scale, new LayoutRect(0, 0, w, h), w, h, tileSize: 128);
        Assert.Equal(25, tiles.Count); // 600 / 128 → 5 × 5
        foreach (var tile in tiles)
        {
            var part = await doc.RenderRegionAsync(1, scale, tile);
            Assert.Equal((tile.Width, tile.Height), (part.Width, part.Height));
            for (int y = 0; y < tile.Height; y++)
                part.Pixels.AsSpan(y * tile.Width * 4, tile.Width * 4).CopyTo(stitched.AsSpan(((tile.Y + y) * w + tile.X) * 4));
        }
        long diff = 0;
        var px = whole.Pixels.AsSpan();
        for (int i = 0; i < stitched.Length; i++) diff += Math.Abs(stitched[i] - px[i]);
        Assert.True(diff / (double)stitched.Length < 0.05, $"mean {diff / (double)stitched.Length:F3}");
    }

    private sealed class Image(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TileCache_KeepsTheBudget_AndDropsOtherScales()
    {
        var cache = new TileCache<Image>(maxBytes: 3);
        Image a = new(1), b = new(2), c = new(3), d = new(4), e = new(5);
        cache.Add(1, false, 2.0, 0, 0, a, 1);
        cache.Add(1, false, 2.0, 1, 0, b, 1);
        cache.Add(1, false, 2.0, 0, 1, c, 1);
        Assert.True(cache.Has(1, false, 2.0004, 1, 0));
        Assert.False(cache.Has(1, true, 2.0, 1, 0));
        Assert.True(cache.TryGet(1, false, 2.0, 0, 0, out var got));
        Assert.Same(a, got);
        cache.Add(2, false, 2.0, 0, 0, d, 1); // over budget: b, the least recently used, goes
        Assert.True(b.Disposed);
        Assert.False(a.Disposed);
        Assert.Equal(3, cache.Bytes);

        cache.Add(1, false, 3.0, 0, 0, e, 1); // a new zoom
        cache.RetainScale(3.0);
        Assert.True(a.Disposed && c.Disposed && d.Disposed);
        Assert.Equal((1, 1L), (cache.Count, cache.Bytes));
        cache.Clear();
        Assert.True(e.Disposed);
    }

    // ------------------------------------------------------------------ selection and copy

    private static byte[] TextPdf(PdfTestEncryption? encryption = null)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BT /F1 20 Tf 20 150 Td (Hello brave world) Tj 0 -30 Td (second line here) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>", mediaBox: "[0 0 300 200]");
        return encryption == null ? b.Build() : b.Build(encryption);
    }

    private static ViewerPoint CentreOf(ViewerRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Fact]
    public async Task Selection_LongPressSelectsAWord_AndHandlesExtendIt()
    {
        await using var doc = await ViewerDocument.OpenAsync(TextPdf());
        var layout = await doc.GetTextLayoutAsync(1);
        Assert.Equal(2, layout.Lines.Count);
        var brave = (await doc.SearchPageAsync(1, "brave"))[0].Rects[0];
        var here = (await doc.SearchPageAsync(1, "here"))[0].Rects[0];

        var selection = new ViewerTextSelection(1, layout);
        Assert.False(selection.SelectWordAt(new ViewerPoint(150, 5), slack: 10)); // far above the text
        Assert.True(selection.SelectWordAt(CentreOf(brave), slack: 10));
        Assert.Equal("brave", selection.Text);
        var band = Assert.Single(selection.Bands);
        Assert.True(Math.Abs(band.X - brave.X) < 1 && Math.Abs(band.Width - brave.Width) < 1, $"{band} vs {brave}");

        // The end handle dragged to the end of "here" on the next line.
        Assert.False(selection.MoveHandle(startHandle: false, new ViewerPoint(here.X + here.Width + 2, here.Y + here.Height / 2)));
        Assert.Equal("brave world\nsecond line here", selection.Text);
        Assert.Equal(2, selection.Bands.Count);

        // The start handle dragged past the end: the handles swap.
        Assert.True(selection.MoveHandle(startHandle: true, new ViewerPoint(here.X + here.Width + 40, here.Y + here.Height / 2)));
        Assert.True(selection.Start < selection.End);

        selection.SelectAll();
        Assert.Equal("Hello brave world\nsecond line here", selection.Text);
        var handles = selection.HandlePositions();
        Assert.NotNull(handles);
        Assert.True(handles!.Value.End.Y > handles.Value.Start.Y);
    }

    [Fact]
    public async Task CopyPermission_FollowsTheDocument()
    {
        await using (var plain = await ViewerDocument.OpenAsync(TextPdf()))
            Assert.True(plain.CanCopy);
        // The test writer's /P allows printing but not copying; an empty user password opens it.
        await using (var restricted = await ViewerDocument.OpenAsync(TextPdf(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner"))))
            Assert.False(restricted.CanCopy);
        await using (var owner = await ViewerDocument.OpenAsync(TextPdf(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner")), password: "owner"))
            Assert.True(owner.CanCopy);
    }

    // ------------------------------------------------------------------ outline and thumbnails

    [Fact]
    public async Task Outline_IsAnIndentedListWithPages()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 100 100 re f");       // objects 3 (content), 4 (page)
        b.AddPage("1 0 0 rg 0 0 100 100 re f");       // 5, 6
        b.Add("<< /Type /Outlines /First 8 0 R /Last 9 0 R /Count 3 >>");                                   // 7
        b.Add("<< /Title (Chapter\\n one) /Parent 7 0 R /Next 9 0 R /Dest [4 0 R /Fit] /First 10 0 R /Last 10 0 R /Count 1 >>"); // 8
        b.Add("<< /Title (Chapter two) /Parent 7 0 R /Prev 8 0 R /Dest [6 0 R /XYZ 0 200 0] >>");          // 9
        b.Add("<< /Title (Section) /Parent 8 0 R /Dest [6 0 R /Fit] >>");                                   // 10
        b.WithCatalog("/Outlines 7 0 R");
        await using var doc = await ViewerDocument.OpenAsync(b.Build());
        var lines = ViewerOutline.Flatten(await doc.GetOutlineAsync());
        Assert.Equal(new[] { new ViewerOutlineLine("Chapter one", 0, 1), new ViewerOutlineLine("Section", 1, 2), new ViewerOutlineLine("Chapter two", 0, 2) }, lines);
        Assert.Empty(ViewerOutline.Flatten(Array.Empty<ViewerOutlineItem>()));
        Assert.Single(ViewerOutline.Flatten(new[] { new ViewerOutlineItem("A", null, new[] { new ViewerOutlineItem("B", 1, Array.Empty<ViewerOutlineItem>()) }) }, maxDepth: 0));
    }

    [Fact]
    public void ThumbnailGrid_FitsColumns_FindsPages_AndReveals()
    {
        var pages = Enumerable.Range(1, 30).Select(i => new ViewerPage(i, 612, 792)).ToArray();
        var grid = new ThumbnailGridLayout(pages, minCellWidth: 150, gap: 10, labelHeight: 20);
        grid.Resize(1080, 1800);
        Assert.Equal(6, grid.Columns); // (1080 − 10) / 160
        Assert.Equal(5, grid.Rows);
        var cell = grid.Cell(8); // row 1, column 1
        Assert.Equal(10 + grid.CellWidth + 10, cell.X, 6);
        Assert.Equal(10 + grid.CellHeight + 10, cell.Y, 6);
        Assert.Equal(8, grid.PageAt(cell.X + 5, cell.Y + 5));
        Assert.Equal(0, grid.PageAt(cell.X - 5, cell.Y + 5)); // in the gap
        var picture = grid.Picture(8);
        Assert.True(picture.Width <= cell.Width + 1e-9 && picture.Bottom <= cell.Bottom - 20 + 1e-9);
        Assert.Equal(picture.Width / 612, grid.Scale(8), 9);

        grid.Resize(400, 300); // a narrow view: two columns, 15 rows
        Assert.Equal(2, grid.Columns);
        Assert.Contains(1, grid.VisiblePages());
        Assert.DoesNotContain(30, grid.VisiblePages());
        grid.Reveal(30);
        Assert.Contains(30, grid.VisiblePages());
        Assert.Equal(grid.MaxScrollY, grid.ScrollY, 6);
    }

    // ------------------------------------------------------------------ recent documents, saved view

    [Fact]
    public void RecentDocuments_KeepTheNewestFirst_SurviveARestart_AndIgnoreDamage()
    {
        string dir = Directory.CreateTempSubdirectory("recent").FullName;
        try
        {
            string path = Path.Combine(dir, "recent.json");
            var t0 = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
            var recent = new RecentDocuments(path, capacity: 3);
            recent.Opened("content://a", "a.pdf", t0);
            recent.Opened("content://b", "b.pdf", t0.AddMinutes(1));
            recent.Moved("content://a", 7, 2.5);
            recent.Opened("content://a", "a.pdf", t0.AddMinutes(2)); // reopened: to the top, position kept
            recent.Opened("content://c", "  ", t0.AddMinutes(3));
            recent.Opened("content://d", "d.pdf", t0.AddMinutes(4)); // over capacity: b goes

            var again = new RecentDocuments(path, capacity: 3);
            Assert.Equal(new[] { "content://d", "content://c", "content://a" }, again.Items.Select(d => d.Location));
            Assert.Equal(new RecentDocument("content://a", "a.pdf", 7, 2.5, t0.AddMinutes(2)), again.Find("content://a"));
            Assert.Equal("Document", again.Find("content://c")!.Name);
            Assert.True(again.Remove("content://c"));
            Assert.False(again.Remove("content://c"));
            Assert.Equal(2, new RecentDocuments(path).Items.Count);

            // Only locations, names and positions are written.
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(path, "{ \"documents\": [ { \"location\": 5 }, { broken");
            Assert.Empty(new RecentDocuments(path).Items);
            File.WriteAllText(path, "{ \"documents\": [ { \"location\": \"content://x\", \"page\": -4, \"zoom\": \"big\" } ] }");
            Assert.Equal(new RecentDocument("content://x", "Document", 1, 1, DateTimeOffset.MinValue), Assert.Single(new RecentDocuments(path).Items));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Layout_RestoresASavedViewInANewSize()
    {
        var pages = Enumerable.Range(1, 5).Select(i => new ViewerPage(i, 612, 792)).ToArray();
        var before = new ContinuousPageLayout(pages, gap: 12, margin: 8);
        before.Resize(1080, 1920);
        before.ZoomAt(3, 540, 960);
        before.ScrollTo(before.ScrollX + 200, before.PageRect(4).Y + 300);
        var saved = before.AnchorAt(0, 0);
        double zoom = before.Zoom;

        // The process is gone; a new layout of another size (rotated) shows the same place.
        var after = new ContinuousPageLayout(pages, gap: 12, margin: 8);
        after.Resize(1920, 1080);
        after.Restore(saved, zoom);
        Assert.Equal(3, after.Zoom);
        var restored = after.AnchorAt(0, 0);
        Assert.Equal(saved.Page, restored.Page);
        Assert.Equal(saved.U, restored.U, 3);
        Assert.Equal(saved.V, restored.V, 3);
        Assert.Equal(4, after.CurrentPage);

        after.Restore(new PageAnchor(99, double.NaN, 2), double.PositiveInfinity); // a damaged saved state
        Assert.Equal(1, after.Zoom);
        Assert.Equal(5, after.AnchorAt(0, 0).Page);
    }
}
