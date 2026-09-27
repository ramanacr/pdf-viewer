using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// A soft mask compressed as JPEG (/SMask with /DCTDecode, as govdocs1 001989.pdf has it) is applied
/// by the backend, so the image is drawn by the vector path instead of going to PDFium.
/// </summary>
public class JpegSoftMaskTests
{
    /// <summary>A gray JPEG: white (opaque) on the left half, black (transparent) on the right.</summary>
    private static byte[] HalfMask(int size)
    {
        var gray = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size / 2; x++) gray[y * size + x] = 255;
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, gray, size)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task JpegSoftMask_IsAppliedByTheBackend()
    {
        var b = new VectorPdfBuilder();
        byte[] mask = HalfMask(32);
        int smask = b.AddStream("/Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", mask);
        var blue = new byte[32 * 32 * 3];
        for (int i = 0; i < 32 * 32; i++) blue[i * 3 + 2] = 255;
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {smask} 0 R", blue, flate: true);
        b.AddPage("q 100 0 0 100 50 50 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        byte[] pdf = b.Build();

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        Assert.Empty(await renderer.AnalyzeAsync(list));
        using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        (int R, int G, int B) Px(int x, int yPdf)
        {
            var s = page.Pixels.Span;
            int i = (200 - yPdf) * page.Stride + x * 4;
            return (s[i + 2], s[i + 1], s[i]);
        }
        var left = Px(70, 100);
        var right = Px(130, 100);
        Assert.True(left.B > 200 && left.R < 60, $"the opaque half is blue ({left})");
        Assert.True(right.R > 200 && right.G > 200 && right.B > 200, $"the masked half shows the page ({right})");
    }
}
