using System;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Content;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Windows;
using PdfEngine.Vector.Xref;
using Xunit;

namespace PdfEngine.Vector.Tests;

public class RenderingTests
{
    [Fact]
    public async Task VectorRenderer_RendersVectorGraphicsSuccessfully()
    {
        string pdfContent =
@"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 200 200 ] /Contents 4 0 R >>
endobj
4 0 obj
<< /Length 85 >>
stream
0 0 1 rg
10 10 180 180 re f
1 0 0 RG
2 w
20 20 m 180 180 l S
0 1 0 RG
20 180 m 180 20 l S
endstream
endobj
xref
0 5
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000116 00000 n 
0000000205 00000 n 
trailer
<< /Size 5 /Root 1 0 R >>
startxref
338
%%EOF";

        byte[] pdfBytes = Encoding.ASCII.GetBytes(pdfContent.Replace("\r\n", "\n"));
        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        Assert.Equal(1, doc.PageCount);

        var displayList = await doc.GetPageDisplayListAsync(1);
        Assert.NotNull(displayList);
        Assert.True(displayList.Features.HasFlag(PdfFeatureSet.Paths));
        Assert.False(displayList.HasFallback);

        using var renderer = new WindowsVectorRenderer();
        var request = new RenderRequest
        {
            PageNumber = 1,
            Dpi = 96.0,
            Rotation = PageRotation.Rotate0
        };

        using var renderedPage = await renderer.RenderDisplayListAsync(displayList, request);
        Assert.NotNull(renderedPage);
        Assert.True(renderedPage.WidthPixels > 0);
        Assert.True(renderedPage.HeightPixels > 0);
        Assert.Equal(renderedPage.WidthPixels * 4, renderedPage.Stride);
        Assert.False(renderedPage.Pixels.IsEmpty);
    }

    [Fact]
    public async Task VectorRenderer_ZoomInvariance_ReusesDisplayListFrom25To1600Percent()
    {
        string pdfContent =
@"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 100 100 ] /Contents 4 0 R >>
endobj
4 0 obj
<< /Length 38 >>
stream
1 0 0 rg
0 0 100 100 re f
0 0 1 RG
10 10 m 90 90 l S
endstream
endobj
xref
0 5
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000116 00000 n 
0000000205 00000 n 
trailer
<< /Size 5 /Root 1 0 R >>
startxref
291
%%EOF";

        byte[] pdfBytes = Encoding.ASCII.GetBytes(pdfContent.Replace("\r\n", "\n"));
        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        var displayList = await doc.GetPageDisplayListAsync(1);

        using var renderer = new WindowsVectorRenderer();

        // Test zoom levels: 25%, 50%, 100%, 200%, 400%, 800%, 1600%
        // In PDF points (72 DPI = 100%), 96 DPI is standard 100% monitor scale.
        double[] zoomScales = [0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0];

        foreach (double zoom in zoomScales)
        {
            double testDpi = 96.0 * zoom;
            var request = new RenderRequest
            {
                PageNumber = 1,
                Dpi = testDpi
            };

            using var rendered = await renderer.RenderDisplayListAsync(displayList, request);
            Assert.NotNull(rendered);
            int expectedWidth = (int)Math.Round(100.0 * (testDpi / 72.0));
            int expectedHeight = (int)Math.Round(100.0 * (testDpi / 72.0));

            Assert.Equal(expectedWidth, rendered.WidthPixels);
            Assert.Equal(expectedHeight, rendered.HeightPixels);
            Assert.Equal(expectedWidth * 4, rendered.Stride);
            Assert.Equal(rendered.ByteLength, rendered.Pixels.Length);
        }
    }

    [Fact]
    public async Task VectorRenderer_TextWithTjAndKerningTJ_RendersProperly()
    {
        string pdfContent =
@"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 300 200 ] /Contents 4 0 R >>
endobj
4 0 obj
<< /Length 120 >>
stream
BT
/F1 16 Tf
50 150 Td
(Hello Vector World!) Tj
0 -30 Td
[ (A) 120 (W) -80 (A) 50 (Y) ] TJ
ET
endstream
endobj
xref
0 5
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000116 00000 n 
0000000205 00000 n 
trailer
<< /Size 5 /Root 1 0 R >>
startxref
373
%%EOF";

        byte[] pdfBytes = Encoding.ASCII.GetBytes(pdfContent.Replace("\r\n", "\n"));
        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        var displayList = await doc.GetPageDisplayListAsync(1);

        Assert.True(displayList.Features.HasFlag(PdfFeatureSet.Text));

        using var renderer = new WindowsVectorRenderer();
        var request = new RenderRequest { PageNumber = 1, Dpi = 96.0 };
        using var rendered = await renderer.RenderDisplayListAsync(displayList, request);

        Assert.NotNull(rendered);
        Assert.True(rendered.WidthPixels > 0);
        Assert.True(rendered.HeightPixels > 0);
    }
}
