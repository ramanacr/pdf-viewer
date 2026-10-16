using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;
using Xunit.Abstractions;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Printing pages whose groups read back the page under them: only those areas become images
/// in the print recording, the rest stays vectors, and the recording rasterized looks like the
/// screen render. The recording is drawn to a bitmap here; PrintingTests sends such pages to
/// the PDF and XPS writers.
/// </summary>
public class PrintBackdropTests
{
    private readonly ITestOutputHelper _output;
    public PrintBackdropTests(ITestOutputHelper output) => _output = output;

    private const double Dpi = 144;

    /// <summary>
    /// A 400 × 400 pt page: text, a half-transparent strip that crosses the blend area, an image
    /// under a Multiply-blended square, then text over the square and text far from it.
    /// </summary>
    internal static byte[] MultiplyPage()
    {
        var b = new VectorPdfBuilder();
        // 2 × 2 RGB image: red, green / blue, yellow.
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
            new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 });
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage(
            "BT /F1 20 Tf 30 360 Td (Before the blend) Tj ET " +
            "/T gs 0 0.6 0 rg 20 180 360 30 re f " +
            "q 120 0 0 120 140 140 cm /Im Do Q " +
            "/M gs 0.9 0.5 0.2 rg 160 160 100 100 re f " +
            "/N gs 0 0 0 rg BT /F1 18 Tf 150 200 Td (After the blend) Tj ET " +
            "BT /F1 16 Tf 30 30 Td (Far away text) Tj ET",
            $"<< /Font << /F1 {helv} 0 R >> /XObject << /Im {image} 0 R >> " +
            "/ExtGState << /T << /ca 0.5 >> /M << /BM /Multiply >> /N << /BM /Normal /ca 1 >> >> >>",
            mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    /// <summary>A 400 × 400 pt page with a non-isolated knockout group (one element multiplies) over coloured strips, then text.</summary>
    internal static byte[] KnockoutPage()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true >> " +
                               "/Resources << /ExtGState << /H << /ca 0.5 >> /B << /ca 0.5 /BM /Multiply >> >> >>",
            "/H gs 0.9 0.4 0.1 rg 60 40 80 80 re f /B gs 0.1 0.4 0.9 rg 100 80 80 80 re f");
        b.AddPage(
            "BT /F1 20 Tf 30 360 Td (Knockout page) Tj ET " +
            "q 1 0 0 1 100 100 cm " +
            "1 0 0 rg 0 0 50 200 re f 0 1 0 rg 50 0 50 200 re f 0 0 1 rg 100 0 50 200 re f 0.5 0.5 0.5 rg 150 0 50 200 re f " +
            "/K Do Q " +
            "0 0 0 rg BT /F1 18 Tf 120 190 Td (Drawn after) Tj ET " +
            "BT /F1 16 Tf 30 30 Td (Far away text) Tj ET",
            $"<< /Font << /F1 {helv} 0 R >> /XObject << /K {form} 0 R >> >>",
            mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    [Theory]
    [InlineData("multiply")]
    [InlineData("knockout")]
    public async Task Recording_MatchesTheScreenAndKeepsTheRestVector(string name)
    {
        byte[] pdf = name == "multiply" ? MultiplyPage() : KnockoutPage();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var renderer = new Direct2DVectorRenderer();
        var request = new RenderRequest { PageNumber = 1, Dpi = Dpi };
        var (width, height) = Direct2DVectorRenderer.OutputSize(list, request);

        // The sheet is the recording's own pixel size, so the comparison is pixel for pixel.
        var recorded = await renderer.RecordPrintPageWithRegionsAsync(list, request, null, new PrintRect(0, 0, width, height), CancellationToken.None);
        Assert.NotNull(recorded);
        var (sheet, regions) = recorded.Value;
        using (sheet)
        {
            var region = Assert.Single(regions);
            _output.WriteLine($"{name}: page {width}×{height}, image region ({region.Left},{region.Top})–({region.Right},{region.Bottom})");
            // Only the group's area is an image, not the page.
            double share = (double)(region.Right - region.Left) * (region.Bottom - region.Top) / (width * height);
            Assert.InRange(share, 0.01, 0.35);

            byte[] printed = Rasterize(renderer, sheet, width, height);
            using var screen = (await renderer.RenderAsync(list, request, null, CancellationToken.None)).Page;
            var (mean, bad) = Compare(printed, screen, width, height);
            _output.WriteLine($"{name}: mean={mean:F3} bad={bad:P3}");
            Assert.True(mean <= 0.5, $"mean difference {mean:F3}");
            Assert.True(bad <= 0.002, $"{bad:P3} pixels off");

            // Inside the region the image is the screen's composited result.
            var (insideMean, _) = Compare(printed, screen, width, height, region);
            Assert.True(insideMean <= 0.5, $"inside the region: mean {insideMean:F3}");
        }
    }

    [Fact]
    public async Task BlendCoveringMostOfThePage_IsNotSplit()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.2 0.6 0.9 rg 0 0 200 200 re f /G1 gs 0.9 0.5 0.2 rg 10 10 180 180 re f", "<< /ExtGState << /G1 << /BM /Multiply >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var recorded = await renderer.RecordPrintPageWithRegionsAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null,
            new PrintRect(0, 0, 200, 200), CancellationToken.None);
        Assert.Null(recorded);
    }

    [Fact]
    public async Task PageWithoutBackdropGroups_HasNoImageRegions()
    {
        var b = new VectorPdfBuilder();
        // An isolated, translucent group whose content multiplies composites on its own bitmap: no read-back of the page.
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> " +
                               "/Resources << /ExtGState << /M << /BM /Multiply >> >> >>",
            "1 1 0 rg 20 20 100 100 re f /M gs 0 1 1 rg 60 60 100 100 re f");
        b.AddPage("0 0 1 rg 0 0 50 50 re f /A gs /F Do", $"<< /XObject << /F {form} 0 R >> /ExtGState << /A << /ca 0.8 >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var request = new RenderRequest { PageNumber = 1, Dpi = Dpi };
        var (width, height) = Direct2DVectorRenderer.OutputSize(list, request);
        var recorded = await renderer.RecordPrintPageWithRegionsAsync(list, request, null, new PrintRect(0, 0, width, height), CancellationToken.None);
        Assert.NotNull(recorded);
        using var sheet = recorded.Value.Sheet;
        Assert.Empty(recorded.Value.Regions);
        byte[] printed = Rasterize(renderer, sheet, width, height);
        using var screen = (await renderer.RenderAsync(list, request, null, CancellationToken.None)).Page;
        var (mean, bad) = Compare(printed, screen, width, height);
        Assert.True(mean <= 0.5 && bad <= 0.002, $"mean {mean:F3}, {bad:P3} off");
    }

    /// <summary>The command list drawn over white paper, BGRA.</summary>
    private static byte[] Rasterize(Direct2DVectorRenderer renderer, ID2D1CommandList sheet, int width, int height)
    {
        using var ctx = renderer.Device.CreateDeviceContext(DeviceContextOptions.None);
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
        using var target = ctx.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target));
        using var staging = ctx.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
            new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(1, 1, 1, 1));
        ctx.DrawImage(sheet, Vector2.Zero, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
        ctx.EndDraw().CheckError();
        ctx.Target = null;
        staging.CopyFromBitmap(target);
        var map = staging.Map(MapOptions.Read);
        try
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.Bits + (int)(y * map.Pitch), pixels, y * width * 4, width * 4);
            return pixels;
        }
        finally
        {
            staging.Unmap();
        }
    }

    /// <summary>Mean channel difference and the share of pixels off by more than 64, over the page or a region of it.</summary>
    private static (double Mean, double Bad) Compare(byte[] printed, RenderedPage screen, int width, int height, Vortice.RawRect? region = null)
    {
        Assert.Equal(width, screen.WidthPixels);
        Assert.Equal(height, screen.HeightPixels);
        var r = region ?? new Vortice.RawRect(0, 0, width, height);
        var s = screen.Pixels.Span;
        long total = 0, bad = 0, count = 0;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            for (int x = r.Left; x < r.Right; x++)
            {
                int max = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(printed[(y * width + x) * 4 + c] - s[y * screen.Stride + x * 4 + c]);
                    total += d;
                    max = Math.Max(max, d);
                }
                if (max > 64) bad++;
                count++;
            }
        }
        return (total / (double)(count * 3), bad / (double)count);
    }
}
