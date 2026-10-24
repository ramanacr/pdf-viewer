using System.Buffers;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// What the Skia renderer does beyond replaying commands: night mode, region renders, fallback
/// regions from a provider, classification of what it cannot draw, a host's own font source, and
/// cancellation.
/// </summary>
public class RendererBehaviourTests
{
    private readonly Parity _parity;
    public RendererBehaviourTests(ITestOutputHelper output) => _parity = new Parity(output);

    private const string Backdrop =
        "1 0 0 rg 0 0 50 200 re f 0 1 0 rg 50 0 50 200 re f 0 0 1 rg 100 0 50 200 re f 0.5 0.5 0.5 rg 150 0 50 200 re f ";

    private static byte[] BlendPage()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "/G1 gs 0.9 0.5 0.2 rg 20 20 160 160 re f 0 0 0 RG 3 w 10 10 m 190 190 l S",
            "<< /ExtGState << /G1 << /BM /Multiply /ca 0.8 >> >> >>");
        return b.Build();
    }

    [Fact]
    public Task NightMode_MatchesDirect2DNightMode() =>
        _parity.AssertMatches("night", BlendPage(), 1.0, 0.01, invert: true);

    [Fact]
    public async Task NightMode_IsExactlyTheInvertedDayPage()
    {
        using var doc = await PdfVectorDocument.OpenAsync(BlendPage());
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = 96 };
        using var day = (await Parity.Skia.RenderAsync(list, request, null, null, false, CancellationToken.None)).Page;
        using var night = (await Parity.Skia.RenderAsync(list, request, null, null, true, CancellationToken.None)).Page;
        var d = day.Pixels.Span;
        var n = night.Pixels.Span;
        for (int i = 0; i < d.Length; i += 4)
        {
            Assert.Equal(255 - d[i], n[i]);
            Assert.Equal(255 - d[i + 1], n[i + 1]);
            Assert.Equal(255 - d[i + 2], n[i + 2]);
            Assert.Equal(255, n[i + 3]);
        }
    }

    [Fact]
    public async Task RegionRender_IsTheSameAsThatPartOfTheFullPage()
    {
        using var doc = await PdfVectorDocument.OpenAsync(BlendPage());
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = 144 };
        using var full = (await Parity.Skia.RenderAsync(list, request, null, null, false, CancellationToken.None)).Page;
        var region = new SkiaPixelRegion(37, 51, 150, 120);
        using var part = (await Parity.Skia.RenderAsync(list, request, null, region, false, CancellationToken.None)).Page;
        Assert.Equal(150, part.WidthPixels);
        Assert.Equal(120, part.HeightPixels);
        long total = 0;
        for (int y = 0; y < part.HeightPixels; y++)
            for (int x = 0; x < part.WidthPixels * 4; x++)
                total += Math.Abs(part.Pixels.Span[y * part.Stride + x] - full.Pixels.Span[(y + region.Y) * full.Stride + region.X * 4 + x]);
        // Groups are drawn into bitmaps aligned to the region, so only antialiasing may differ by a level.
        Assert.True(total / (double)(part.WidthPixels * part.HeightPixels * 4) < 0.05, $"region differs by {total} levels");
    }

    /// <summary>A provider that paints every fallback region in one colour.</summary>
    private sealed class SolidProvider : IPdfFallbackProvider
    {
        public int Calls;
        public ValueTask<RenderedPage?> RenderFallbackRegionAsync(PdfFallbackToken token, double dpi, CancellationToken cancellationToken = default)
        {
            Calls++;
            int w = (int)Math.Ceiling(token.Bounds.Width * dpi / 72), h = (int)Math.Ceiling(token.Bounds.Height * dpi / 72);
            var buffer = new byte[w * h * 4];
            for (int i = 0; i < buffer.Length; i += 4) { buffer[i] = 0; buffer[i + 1] = 0; buffer[i + 2] = 200; buffer[i + 3] = 255; } // BGRA red
            return ValueTask.FromResult<RenderedPage?>(new RenderedPage(token.PageNumber, w, h, w * 4, dpi, PageRotation.Rotate0, new Owner(buffer)));
        }

        private sealed class Owner(byte[] array) : IMemoryOwner<byte>
        {
            public Memory<byte> Memory => array;
            public void Dispose() { }
        }
    }

    private static PdfDisplayList ListWithFallback()
    {
        var token = new PdfFallbackToken(1, new PdfRect(50, 50, 100, 100), PdfFallbackReason.UnknownOperator, "test region");
        var commands = new PdfDrawCommand[]
        {
            new FillPath(new PdfPath(new PdfPathSegment[]
            {
                new PdfMoveTo(new PdfPoint(0, 0)), new PdfLineTo(new PdfPoint(200, 0)), new PdfLineTo(new PdfPoint(200, 200)), new PdfLineTo(new PdfPoint(0, 200)), new PdfCloseSubpath(),
            }), new PdfPaint(PdfColor.FromRgb(0, 0, 1)), PdfFillRule.NonZero, new PdfRect(0, 0, 200, 200)),
            new DrawFallbackRegion(token, token.Bounds),
        };
        return new PdfDisplayList(1, new PdfSize(200, 200), new PdfRect(0, 0, 200, 200), new PdfRect(0, 0, 200, 200), 0, commands,
            PdfFeatureSet.Paths | PdfFeatureSet.Fallback, new[] { token });
    }

    [Fact]
    public async Task FallbackRegions_AreDrawnFromTheProviderLast()
    {
        var provider = new SolidProvider();
        var result = await Parity.Skia.RenderAsync(ListWithFallback(), new RenderRequest { PageNumber = 1, Dpi = 72 }, provider, null, false, CancellationToken.None);
        using var page = result.Page;
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, result.FallbackRegionsComposited);
        var span = page.Pixels.Span;
        int inside = 100 * page.Stride + 100 * 4, outside = 20 * page.Stride + 20 * 4;
        Assert.Equal(200, span[inside + 2]); // provider red over the region
        Assert.Equal(0, span[inside]);
        Assert.Equal(255, span[outside]); // the page's blue elsewhere
    }

    [Fact]
    public async Task FallbackRegions_WithoutAProvider_AreOutlinedInStrictMode()
    {
        using var strict = new SkiaVectorRenderer { OutlineUnsupportedRegions = true };
        using var marked = await strict.RenderDisplayListAsync(ListWithFallback(), new RenderRequest { PageNumber = 1, Dpi = 72 });
        using var plain = await Parity.Skia.RenderDisplayListAsync(ListWithFallback(), new RenderRequest { PageNumber = 1, Dpi = 72 });
        int centre = 100 * marked.Stride + 100 * 4, edge = 50 * marked.Stride + 100 * 4; // the region's centre and top edge (page y 150)
        // Strict mode tints the region orange and outlines it; otherwise the page shows as drawn.
        Assert.True(marked.Pixels.Span[centre + 2] > 20, "the missing region is not tinted");
        Assert.True(marked.Pixels.Span[edge + 2] > marked.Pixels.Span[centre + 2], "the missing region has no outline");
        Assert.Equal(0, plain.Pixels.Span[centre + 2]);
    }

    /// <summary>A font source with nothing in it: the host bundles no fonts.</summary>
    private sealed class NoFonts : SkiaFontSource
    {
        public NoFonts(string emptyDirectory) : base(SystemFontCatalog.ForDirectory(emptyDirectory)) { }
        public override SKTypeface? MatchFamily(string family, bool bold, bool italic) => null;
    }

    [Fact]
    public async Task HostFontSource_WithoutASubstitute_ClassifiesTheText()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 24 Tf 10 100 Td (No font) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        string empty = Directory.CreateTempSubdirectory("skia-nofonts").FullName;
        try
        {
            using var renderer = new SkiaVectorRenderer(fonts: new NoFonts(empty));
            var tokens = await renderer.AnalyzeAsync(list);
            var token = Assert.Single(tokens);
            Assert.Equal(PdfFallbackReason.UnsupportedFontType, token.Reason);
            // The text is left to the overlay: the page itself stays blank.
            using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
            Assert.All(page.Pixels.ToArray(), v => Assert.Equal(255, v));
        }
        finally
        {
            Directory.Delete(empty);
        }
    }

    [Fact]
    public async Task CancelledRender_Throws()
    {
        using var doc = await PdfVectorDocument.OpenAsync(BlendPage());
        var list = await doc.GetPageDisplayListAsync(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Parity.Skia.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, cts.Token));
    }

    [Fact]
    public async Task RawRgbImageData_WithoutASource_IsDrawn()
    {
        // Builders and tests hand over RGB24 directly (PdfImageRef.ImageData).
        var image = new PdfImageRef("raw", 2, 1, 8, "DeviceRGB", false, new byte[] { 255, 0, 0, 0, 0, 255 });
        var list = new PdfDisplayList(1, new PdfSize(100, 100), new PdfRect(0, 0, 100, 100), new PdfRect(0, 0, 100, 100), 0,
            new PdfDrawCommand[] { new DrawImage(image, new PdfMatrix(100, 0, 0, 100, 0, 0), new PdfRect(0, 0, 100, 100)) }, PdfFeatureSet.Images);
        using var page = await Parity.Skia.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var span = page.Pixels.Span;
        int left = 50 * page.Stride + 10 * 4, right = 50 * page.Stride + 90 * 4;
        Assert.True(span[left + 2] > 250 && span[left] < 5, "left half is not red");
        Assert.True(span[right] > 250 && span[right + 2] < 5, "right half is not blue");
    }

    [Fact]
    public async Task OutputSize_MatchesDirect2D()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 g 0 0 10 10 re f", mediaBox: "[0 0 612 792]", extra: "/Rotate 270");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        foreach (var request in new[] { new RenderRequest { Dpi = 96 }, new RenderRequest { Dpi = 150, Rotation = PageRotation.Rotate90 }, new RenderRequest { TargetWidthPixels = 300, TargetHeightPixels = 200 } })
            Assert.Equal(Direct2D.Direct2DVectorRenderer.OutputSize(list, request), SkiaVectorRenderer.OutputSize(list, request));
    }
}
