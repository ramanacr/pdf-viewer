using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>Per-pixel difference of two renders of the same page (colour channels, 0..255).</summary>
/// <param name="Mean">Mean absolute channel difference.</param>
/// <param name="Max">Largest channel difference of any pixel.</param>
/// <param name="Visible">Share of pixels with a channel off by more than 16.</param>
/// <param name="Bad">Share of pixels with a channel off by more than 64 (the differential tests' "bad" pixel).</param>
public readonly record struct PixelDiff(double Mean, int Max, double Visible, double Bad)
{
    public override string ToString() => $"mean={Mean:F2} max={Max} >16={Visible:P2} >64={Bad:P2}";

    public static PixelDiff Compare(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(b.WidthPixels, a.WidthPixels);
        Assert.Equal(b.HeightPixels, a.HeightPixels);
        var pa = a.Pixels.Span;
        var pb = b.Pixels.Span;
        long total = 0, visible = 0, bad = 0;
        int worst = 0;
        long pixels = (long)a.WidthPixels * a.HeightPixels;
        for (int y = 0; y < a.HeightPixels; y++)
        {
            for (int x = 0; x < a.WidthPixels; x++)
            {
                int ia = y * a.Stride + x * 4, ib = y * b.Stride + x * 4;
                int max = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(pa[ia + c] - pb[ib + c]);
                    total += d;
                    if (d > max) max = d;
                }
                if (max > 16) visible++;
                if (max > 64) bad++;
                if (max > worst) worst = max;
            }
        }
        return new PixelDiff(total / (double)(pixels * 3), worst, visible / (double)pixels, bad / (double)pixels);
    }
}

/// <summary>
/// Renders fixtures with the Skia backend and with the Direct2D backend (the reference: same
/// display list, same semantics) and compares the pixels. Pixel equality is not expected
/// (rasterizers antialias differently); each test states its budget for the mean channel
/// difference and the share of pixels off by more than 64.
/// </summary>
public sealed class Parity
{
    private readonly ITestOutputHelper _output;

    public Parity(ITestOutputHelper output) => _output = output;

    // One renderer of each kind for the whole run: both serialize their renders.
    private static readonly Lazy<SkiaVectorRenderer> SharedSkia = new(() => new SkiaVectorRenderer());
    private static readonly Lazy<Direct2DVectorRenderer> SharedD2D = new(() => new Direct2DVectorRenderer());

    public static SkiaVectorRenderer Skia => SharedSkia.Value;
    public static Direct2DVectorRenderer D2D => SharedD2D.Value;

    public sealed record Pair(RenderedPage Skia, RenderedPage D2D, IPdfDisplayList List, PixelDiff Diff, IReadOnlyList<PdfFallbackToken> SkiaBackendFallbacks) : IDisposable
    {
        public void Dispose()
        {
            Skia.Dispose();
            D2D.Dispose();
        }
    }

    /// <summary>Renders page 1 of <paramref name="pdf"/> with both backends (Skia: <paramref name="skiaRenderer"/>, else the shared renderer).</summary>
    public static async Task<Pair> RenderBoth(byte[] pdf, double dpi, PageRotation rotation = PageRotation.Rotate0, bool invert = false, SkiaVectorRenderer? skiaRenderer = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var request = new RenderRequest { PageNumber = 1, Dpi = dpi, Rotation = rotation };
        var skia = await (skiaRenderer ?? Skia).RenderAsync(list, request, null, null, invert, CancellationToken.None);
        var d2d = (await D2D.RenderAsync(list, request, null, null, invert, CancellationToken.None)).Page;
        var backend = skia.Fallbacks.Skip(list.FallbackTokens.Count).ToList();
        return new Pair(skia.Page, d2d, list, PixelDiff.Compare(skia.Page, d2d), backend);
    }

    /// <summary>
    /// Asserts the fixture is fully vector, that the Skia backend classifies nothing, and that its
    /// render stays within the budget of the Direct2D render at each resolution.
    /// </summary>
    public async Task<PixelDiff> AssertMatches(string name, byte[] pdf, double maxMean, double maxBad, double[]? dpis = null,
        PageRotation rotation = PageRotation.Rotate0, bool invert = false, SkiaVectorRenderer? skiaRenderer = null)
    {
        PixelDiff worst = default;
        foreach (double dpi in dpis ?? new[] { 72.0, 144.0 })
        {
            using var pair = await RenderBoth(pdf, dpi, rotation, invert, skiaRenderer);
            Assert.False(pair.List.HasFallback, $"{name}: the interpreter fell back: {string.Join(", ", pair.List.FallbackTokens.Select(t => t.Reason))}");
            var backend = pair.SkiaBackendFallbacks;
            Assert.True(backend.Count == 0, $"{name}: the Skia backend classified {string.Join(", ", backend.Select(t => t.Reason))}");
            _output.WriteLine($"skia vs d2d {name} @{dpi}: {pair.Diff}");
            Dump(name, dpi, pair.Skia, pair.D2D);
            Assert.True(pair.Diff.Mean <= maxMean, $"{name} @{dpi}: mean {pair.Diff.Mean:F2} > {maxMean}");
            Assert.True(pair.Diff.Bad <= maxBad, $"{name} @{dpi}: {pair.Diff.Bad:P2} pixels off by > 64 (budget {maxBad:P2})");
            if (pair.Diff.Mean >= worst.Mean) worst = pair.Diff;
        }
        return worst;
    }

    /// <summary>
    /// With SKIA_PARITY_DUMP set to a local folder, writes both renders as PNG files there, to look
    /// at a difference. Corpus pages dumped this way show document content: keep the folder local.
    /// </summary>
    internal static void Dump(string name, double dpi, RenderedPage skia, RenderedPage d2d)
    {
        string? dir = Environment.GetEnvironmentVariable("SKIA_PARITY_DUMP");
        if (string.IsNullOrEmpty(dir))
            return;
        Directory.CreateDirectory(dir);
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        foreach (var (page, backend) in new[] { (skia, "skia"), (d2d, "d2d") })
        {
            var info = new SkiaSharp.SKImageInfo(page.WidthPixels, page.HeightPixels, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            using var image = SkiaSharp.SKImage.FromPixelCopy(info, page.Pixels.Span, page.Stride);
            using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"{name}@{dpi}.{backend}.png"), png.ToArray());
        }
    }
}
