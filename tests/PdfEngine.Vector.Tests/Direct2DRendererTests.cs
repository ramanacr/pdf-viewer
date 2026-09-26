using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The Direct2D / DirectWrite backend (ADR-005) against the PDFium oracle, on the same fixtures
/// and budgets as the WPF backend, plus the properties only it provides: device-loss recovery,
/// tiled output beyond a single GPU texture, true device-pixel hairlines, in-memory fonts.
/// </summary>
public class Direct2DRendererTests
{
    private readonly ITestOutputHelper _output;
    public Direct2DRendererTests(ITestOutputHelper output) => _output = output;

    private static async Task<(RenderedPage Vector, RenderedPage Reference)> Render(byte[] pdf, double dpi, Direct2DVectorRenderer renderer, PageRotation rotation = PageRotation.Rotate0)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var vector = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi, Rotation = rotation });
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi, Rotation = rotation });
        return (vector, reference);
    }

    [Theory]
    [MemberData(nameof(DifferentialRenderingTests.Fixtures), MemberType = typeof(DifferentialRenderingTests))]
    public async Task Direct2D_MatchesPdfiumWithinPerceptualBudget(string name, string content, string resources, double maxMean, double maxBadShare)
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, resources);
        using var renderer = new Direct2DVectorRenderer();
        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            var (v, p) = await Render(b.Build(), dpi, renderer);
            using (v) using (p)
            {
                var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
                _output.WriteLine($"d2d {name} @{dpi}: mean={mean:F2} bad={bad:P2} software={renderer.IsSoftware}");
                Assert.True(mean <= maxMean, $"{name} @{dpi}: mean {mean:F2} > {maxMean}");
                Assert.True(bad <= maxBadShare, $"{name} @{dpi}: {bad:P2} pixels off");
            }
        }
    }

    [Fact]
    public async Task Direct2D_TextInEmbeddedAndSubstitutedFonts_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        byte[] times = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "times.ttf"));
        int stream = b.AddStream($"/Length1 {times.Length}", times, flate: true);
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /TimesNewRomanPSMT /Flags 34 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {stream} 0 R >>");
        int embedded = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /TimesNewRomanPSMT /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 24 Tf 10 150 Td (Embedded Times text) Tj /F2 20 Tf 0 -50 Td (Substituted Helvetica) Tj ET",
            $"<< /Font << /F1 {embedded} 0 R /F2 {helv} 0 R >> >>");
        using var renderer = new Direct2DVectorRenderer();
        var (v, p) = await Render(b.Build(), 144, renderer);
        using (v) using (p)
        {
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"d2d text: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 2.0, $"text mean {mean:F2}");
            Assert.True(bad <= 0.02, $"text bad {bad:P2}");
        }
    }

    [Theory]
    [InlineData(PageRotation.Rotate90)]
    [InlineData(PageRotation.Rotate180)]
    [InlineData(PageRotation.Rotate270)]
    public async Task Direct2D_Rotation_MatchesPdfium(PageRotation rotation)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 rg 0 0 40 40 re f 0 0 1 rg 150 50 30 90 re f", mediaBox: "[0 0 200 150]");
        using var renderer = new Direct2DVectorRenderer();
        var (v, p) = await Render(b.Build(), 72, renderer, rotation);
        using (v) using (p)
        {
            var (mean, _) = DifferentialRenderingTests.Compare(v, p);
            Assert.True(mean <= 1.5, $"{rotation}: mean {mean:F2}");
        }
    }

    [Fact]
    public async Task Direct2D_SurvivesDeviceLoss_WithoutReparsing()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 10 10 180 180 re f 1 0 0 RG 3 w 20 20 m 180 180 l S");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        using var before = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 96 });
        renderer.SimulateDeviceLoss();
        using var after = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 96 });
        Assert.Same(list, await doc.GetPageDisplayListAsync(1)); // nothing reparsed
        var (mean, _) = DifferentialRenderingTests.Compare(before, after);
        Assert.True(mean < 0.01, $"render after device loss differs by {mean:F3}");
    }

    [Fact]
    public async Task Direct2D_TilesOutputsLargerThanOneTexture()
    {
        // 200pt page at 1600 % = 3200 px: several 2048 px tiles must stitch without seams.
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 200 200 re f 1 1 1 rg 99 0 2 200 re f");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer(tileSize: 2048);
        using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 * 16 });
        Assert.Equal(3200, page.WidthPixels);
        var span = page.Pixels.Span;
        foreach (int x in new[] { 10, 2047, 2048, 2049, 3100 })
        {
            if (x >= 1584 && x < 1616) continue; // the white bar
            int i = 1600 * page.Stride + x * 4;
            Assert.True(span[i] > 200 && span[i + 2] < 50, $"seam or gap at x={x}");
        }
        // The white bar straddling no tile edge is present.
        int bar = 1600 * page.Stride + 1600 * 4;
        Assert.True(span[bar] > 240 && span[bar + 1] > 240 && span[bar + 2] > 240);
    }

    [Fact]
    public async Task Direct2D_ZeroWidthStroke_IsOneDevicePixel()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 G 0 w 0 100.5 m 200 100.5 l S");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        foreach (double dpi in new[] { 72.0, 288.0 })
        {
            using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi });
            int dark = 0;
            int x = page.WidthPixels / 2;
            for (int y = 0; y < page.HeightPixels; y++)
                if (page.Pixels.Span[y * page.Stride + x * 4] < 200) dark++;
            Assert.InRange(dark, 1, 2); // hairline thickness is device pixels at every zoom
        }
    }
    /// <summary>
    /// Rectangular clips (<c>re W n</c>) are axis-aligned clips instead of offscreen layers; the
    /// pixels must equal the same clip drawn as a general path (an extra collinear point keeps it
    /// on the layer path), nested, under page rotation, fractional edges, and inside a blended
    /// group whose content re-applies the enclosing clips.
    /// </summary>
    [Theory]
    [InlineData(PageRotation.Rotate0, 72.0)]
    [InlineData(PageRotation.Rotate90, 144.0)]
    [InlineData(PageRotation.Rotate270, 100.0)]
    public async Task Direct2D_RectangleClips_MatchGeneralPathClips(PageRotation rotation, double dpi)
    {
        const string body = "1 0 0 rg 0 0 200 200 re f 0 0 1 rg 30 30 150 120 re f " +
                            "/GS1 gs 0 1 0 rg 40.25 40.75 60 60 re f";
        string Rect(double x, double y, double w, double h) => $"{x} {y} {w} {h} re";
        string Poly(double x, double y, double w, double h) =>
            $"{x} {y} m {x + w / 2} {y} l {x + w} {y} l {x + w} {y + h} l {x} {y + h} l h";
        string Page(Func<double, double, double, double, string> clip) =>
            $"q {clip(10.3, 12.7, 170.2, 150.1)} W n q {clip(25.5, 20, 120, 170)} W n {body} Q Q " +
            $"q 1 0 0 1 5 5 cm {clip(0, 0, 90.5, 90)} W* n 0 0 0 rg 0 0 300 300 re f Q";
        const string res = "<< /ExtGState << /GS1 << /BM /Multiply /ca 0.7 >> >> >>";

        async Task<RenderedPage> Draw(string content, Direct2DVectorRenderer r)
        {
            var b = new VectorPdfBuilder();
            b.AddPage(content, res);
            using var doc = await PdfVectorDocument.OpenAsync(b.Build());
            var list = await doc.GetPageDisplayListAsync(1);
            return await r.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi, Rotation = rotation });
        }

        using var renderer = new Direct2DVectorRenderer();
        using var fast = await Draw(Page(Rect), renderer);
        using var general = await Draw(Page(Poly), renderer);
        var (mean, bad) = DifferentialRenderingTests.Compare(fast, general);
        _output.WriteLine($"rect vs path clip {rotation} @{dpi}: mean={mean:F3} bad={bad:P3}");
        Assert.True(mean <= 0.25, $"mean {mean:F3}");
        Assert.True(bad <= 0.001, $"bad {bad:P3}");
    }
    /// <summary>
    /// The zero-copy path: a region rendered into a shared GPU texture holds exactly the pixels of
    /// the readback path (read here through a second Direct3D 11 device), and Direct3D 9Ex — what
    /// WPF's D3DImage composes — can open it.
    /// </summary>
    [Fact]
    public async Task Direct2D_SharedTexture_HoldsTheSamePixelsAsReadback_AndOpensInDirect3D9Ex()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 rg 20 20 100 60 re f 0 0 1 RG 3 w 10 10 m 190 190 l S 0 g BT /F1 18 Tf 20 150 Td (Shared) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        if (renderer.IsSoftware) return; // WARP cannot share with another device
        var request = new RenderRequest { PageNumber = 1, Dpi = 216 };
        var region = new PixelRegion(40, 60, 400, 300);

        using var shared = await renderer.RenderToSharedTextureAsync(list, request, null, region, invertColors: false, System.Threading.CancellationToken.None);
        Assert.NotNull(shared);
        Assert.Equal(400, shared!.Width);
        Assert.NotEqual(IntPtr.Zero, shared.SharedHandle);

        var reference = await renderer.RenderAsync(list, request, null, region, invertColors: false, System.Threading.CancellationToken.None);
        using var expected = reference.Page;

        // Read the shared texture back through an independent device.
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,
            null!, out Vortice.Direct3D11.ID3D11Device? device).CheckError();
        using (device)
        {
            using var opened = device!.OpenSharedResource<Vortice.Direct3D11.ID3D11Texture2D>(shared.SharedHandle);
            var desc = opened.Description;
            desc.Usage = Vortice.Direct3D11.ResourceUsage.Staging;
            desc.BindFlags = Vortice.Direct3D11.BindFlags.None;
            desc.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read;
            desc.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(desc);
            var ctx = device.ImmediateContext;
            ctx.CopyResource(staging, opened);
            var map = ctx.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var actual = new byte[expected.Stride * expected.HeightPixels];
            for (int row = 0; row < expected.HeightPixels; row++)
                System.Runtime.InteropServices.Marshal.Copy(map.DataPointer + (int)(row * map.RowPitch), actual, row * expected.Stride, expected.Stride);
            ctx.Unmap(staging, 0);
            var span = expected.Pixels.Span;
            int differing = 0;
            for (int i = 0; i < actual.Length; i++)
                if (Math.Abs(actual[i] - span[i]) > 1) differing++;
            Assert.True(differing == 0, $"{differing} bytes differ between the shared texture and the readback");
        }

        // Direct3D 9Ex opens it (what D3DImage.SetBackBuffer is given).
        using var d3d9 = D3D9ExInterop.TryCreate(GetDesktopWindow());
        Assert.NotNull(d3d9);
        using var surface = d3d9!.OpenShared(shared.SharedHandle, shared.Width, shared.Height);
        Assert.NotNull(surface);
        Assert.NotEqual(IntPtr.Zero, surface!.Surface);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
