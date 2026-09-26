using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfEngine.Exceptions;
using PdfEngine.Vector;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Hybrid composition-root behaviour: the vector path honours the same security ceilings as the
/// PDFium path, records local diagnostics without document content, and agrees with PDFium on
/// geometry that the viewer depends on (page size, rotation, text hit boxes).
/// </summary>
public class HybridVectorServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HybridVectorServiceTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task VectorPath_EnforcesRenderDimensionPolicy()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "dims.pdf"), 1);
        var policy = new PdfSecurityPolicy { MaxRenderDimensionPixels = 200 };
        using var service = new HybridVectorDocumentService(policy, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);
        Assert.True(service.IsVectorDocumentOpen);

        await Assert.ThrowsAnyAsync<PdfSecurityPolicyException>(() => service.RenderPageAsync(1, 300));
    }

    [Fact]
    public async Task Metrics_RecordPagesOnceAndNeverContainDocumentText()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "metrics.pdf"), 2, "SecretKeyword");
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path);

        await service.RenderPageAsync(1, 72);
        await service.RenderPageAsync(1, 144); // zoom: same page, not a new page
        await service.RenderPageAsync(2, 72);

        var metrics = service.EngineMetrics;
        Assert.Equal(2, metrics.TotalPagesParsed);
        Assert.True(metrics.TotalPagesFullyVector == 2, metrics.ToJsonReport());

        string json = metrics.ToJsonReport();
        Assert.Contains("\"pagesParsed\":2", json);
        Assert.DoesNotContain("SecretKeyword", json);
        Assert.DoesNotContain(path, json);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task VectorAndPdfium_AgreeOnRasterSizeAndContent(int rotate)
    {
        string path = TestPdfBuilder.CreateRotatedPdf(Path.Combine(_dir, $"rot{rotate}.pdf"), rotate);

        using var vector = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Vector);
        await vector.OpenDocumentAsync(path);
        var v = await vector.RenderPageAsync(1, 72);

        using var pdfium = new PdfiumDocumentService(PdfSecurityPolicy.DefaultStrict);
        await pdfium.OpenDocumentAsync(path);
        var p = await pdfium.RenderPageAsync(1, 72);

        Assert.NotNull(v);
        Assert.NotNull(p);
        Assert.Equal(p!.PixelWidth, v!.PixelWidth);
        Assert.Equal(p.PixelHeight, v.PixelHeight);
        Assert.True(MeanDifference(v, p) < 6.0, "vector and PDFium renders diverge");
    }

    [Fact]
    public async Task VectorTextSegments_LineUpWithPdfiumWords()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "text.pdf"), 1, "AlignToken");

        using var hybrid = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Vector);
        await hybrid.OpenDocumentAsync(path);
        await hybrid.RenderPageAsync(1, 72);
        var chars = await hybrid.ExtractPageTextSegmentsAsync(1);

        using var pdfium = new PdfiumDocumentService(PdfSecurityPolicy.DefaultStrict);
        await pdfium.OpenDocumentAsync(path);
        var words = await pdfium.ExtractPageTextSegmentsAsync(1);

        Assert.NotEmpty(chars);
        var word = words.First(w => w.Text.StartsWith("AlignToken", StringComparison.Ordinal));
        var first = chars.First(c => c.Text == "A" &&
                                     Math.Abs(c.Y - word.Y) < 0.05 &&
                                     Math.Abs(c.X - word.X) < 0.05);
        Assert.InRange(first.X, word.X - 0.01, word.X + 0.01);
        Assert.True(first.Y + first.Height > word.Y && first.Y < word.Y + word.Height, "character box overlaps the word box vertically");
    }

    [Theory]
    [InlineData(PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind.Aes128_R4)]
    [InlineData(PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind.Aes256_R6)]
    [InlineData(PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind.Rc4_128_R3)]
    public async Task EncryptedDocument_WithPassword_RendersOnTheVectorPath(PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind scheme)
    {
        var b = new PdfEngine.Vector.Tests.Fixtures.VectorPdfBuilder();
        b.AddPage("0 0 1 rg 20 20 160 160 re f BT /F1 20 Tf 30 90 Td (Locked) Tj ET",
            $"<< /Font << /F1 {b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")} 0 R >> >>");
        string path = Path.Combine(_dir, $"encrypted-{scheme}.pdf");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, b.Build(new PdfEngine.Vector.Tests.Fixtures.PdfTestEncryption(scheme, "secret", "owner")));

        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(path, "secret");
        Assert.True(service.IsVectorDocumentOpen);
        var page = await service.RenderPageAsync(1, 72);
        Assert.NotNull(page);
        Assert.Equal(PdfEngineMode.Vector, service.GetPageEngineReport(1)!.Engine);
    }

    [Fact]
    public async Task SwitchingFromPdfiumToAuto_OpensTheVectorDocumentLazily()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "switch.pdf"), 1);
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Pdfium);
        await service.OpenDocumentAsync(path);
        Assert.False(service.IsVectorDocumentOpen);

        service.EngineMode = PdfEngineMode.Auto;
        await service.RenderPageAsync(1, 72);

        Assert.True(service.IsVectorDocumentOpen);
        Assert.Equal(PdfEngineMode.Vector, service.GetPageEngineReport(1)!.Engine);
    }

    private static double MeanDifference(BitmapSource a, BitmapSource b)
    {
        var fa = new FormatConvertedBitmap(a, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var fb = new FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = fa.PixelWidth, h = fa.PixelHeight;
        var pa = new byte[w * h * 4];
        var pb = new byte[w * h * 4];
        fa.CopyPixels(pa, w * 4, 0);
        fb.CopyPixels(pb, w * 4, 0);
        long total = 0;
        for (int i = 0; i < pa.Length; i += 4)
            total += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
        return total / (double)(w * h * 3);
    }
}
