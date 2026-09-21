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

    [Fact]
    public async Task VectorRenderer_ShadingAndTransparencyGroup_RendersSmoothGradients()
    {
        byte[] pdfBytes = BuildPdf(
            // 1: Catalog
            "<< /Type /Catalog /Pages 2 0 R >>",
            // 2: Pages
            "<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>",
            // 3: Page
            "<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 200 200 ] /Resources << /Shading << /Sh1 5 0 R >> >> /Contents 4 0 R >>",
            // 4: Contents
            "<< /Length 17 >>\nstream\nq\n/Sh1 sh\nQ\nendstream",
            // 5: Shading
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [ 0 0 200 200 ] /Function << /FunctionType 2 /Domain [ 0 1 ] /C0 [ 1 0 0 ] /C1 [ 0 0 1 ] /N 1 >> >>"
        );

        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        var displayList = await doc.GetPageDisplayListAsync(1);

        Assert.True(displayList.Features.HasFlag(PdfFeatureSet.Shading));
        Assert.Contains(displayList.Commands, cmd => cmd is DrawShading);

        using var renderer = new WindowsVectorRenderer();
        var request = new RenderRequest { PageNumber = 1, Dpi = 96.0 };
        using var rendered = await renderer.RenderDisplayListAsync(displayList, request);
        Assert.NotNull(rendered);
        Assert.True(rendered.WidthPixels > 0);
        Assert.True(rendered.HeightPixels > 0);
    }

    [Fact]
    public async Task VectorDocument_OutlinesAndBookmarks_ResolvesHierarchy()
    {
        byte[] pdfBytes = BuildPdf(
            // 1: Catalog
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            // 2: Pages
            "<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>",
            // 3: Page
            "<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 200 200 ] /Contents 4 0 R >>",
            // 4: Contents
            "<< /Length 10 >>\nstream\n10 10 m S\nendstream",
            // 5: Outlines
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
            // 6: Outline item
            "<< /Title (Chapter 1) /Parent 5 0 R /Dest [ 3 0 R /XYZ 0 200 1.5 ] /First 7 0 R /Last 7 0 R >>",
            // 7: Child outline item
            "<< /Title (Section 1.1) /Parent 6 0 R /Dest [ 3 0 R /XYZ 50 150 1.0 ] >>"
        );

        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        var bookmarks = await doc.GetBookmarksAsync();

        Assert.NotNull(bookmarks);
        Assert.Single(bookmarks);
        var root = bookmarks[0];
        Assert.Equal("Chapter 1", root.Title);
        Assert.Equal(1, root.TargetPageNumber);
        Assert.Equal(0.0, root.TargetX);
        Assert.Equal(200.0, root.TargetY);
        Assert.Equal(1.5, root.TargetZoom);

        Assert.Single(root.Children);
        var child = root.Children[0];
        Assert.Equal("Section 1.1", child.Title);
        Assert.Equal(1, child.TargetPageNumber);
        Assert.Equal(50.0, child.TargetX);
        Assert.Equal(150.0, child.TargetY);
        Assert.Equal(1.0, child.TargetZoom);
    }

    [Fact]
    public async Task VectorRenderer_IndexedColorSpace_ResolvesPaletteColors()
    {
        byte[] pdfBytes = BuildPdf(
            // 1: Catalog
            "<< /Type /Catalog /Pages 2 0 R >>",
            // 2: Pages
            "<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>",
            // 3: Page
            "<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 200 200 ] /Resources << /ColorSpace << /CS0 [ /Indexed /DeviceRGB 1 <FF000000FF00> ] >> >> /Contents 4 0 R >>",
            // 4: Contents
            "<< /Length 50 >>\nstream\n/CS0 cs\n0 sc\n10 10 50 50 re f\n1 sc\n70 10 50 50 re f\nendstream"
        );

        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        var displayList = await doc.GetPageDisplayListAsync(1);

        Assert.NotNull(displayList);
        Assert.True(displayList.Features.HasFlag(PdfFeatureSet.Paths));

        var fills = displayList.Commands.OfType<FillPath>().ToList();
        Assert.Equal(2, fills.Count);
        // Palette index 0 is red (FF0000)
        Assert.Equal(1f, fills[0].Paint.Color.R);
        Assert.Equal(0f, fills[0].Paint.Color.G);
        Assert.Equal(0f, fills[0].Paint.Color.B);

        // Palette index 1 is green (00FF00)
        Assert.Equal(0f, fills[1].Paint.Color.R);
        Assert.Equal(1f, fills[1].Paint.Color.G);
        Assert.Equal(0f, fills[1].Paint.Color.B);
    }

    [Fact]
    public async Task VectorRenderer_Type0CompositeFont_DecodesTwoByteCidText()
    {
        string cMapStream = @"/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
/CMapName /Custom-ToUnicode def
/CMapType 2 def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
1 beginbfchar
<0001> <0041>
endbfchar
1 beginbfrange
<0002> <0002> <0042>
endbfrange
endcmap
CMapName currentdict /CMap defineresource pop
end
end";

        byte[] pdfBytes = BuildPdf(
            // 1: Catalog
            "<< /Type /Catalog /Pages 2 0 R >>",
            // 2: Pages
            "<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>",
            // 3: Page
            "<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 200 200 ] /Resources << /Font << /F0 5 0 R >> >> /Contents 4 0 R >>",
            // 4: Contents
            "<< /Length 30 >>\nstream\nBT\n/F0 12 Tf\n<00010002> Tj\nET\nendstream",
            // 5: Type0 Font
            "<< /Type /Font /Subtype /Type0 /BaseFont /CustomType0 /Encoding /Identity-H /DescendantFonts [ 6 0 R ] /ToUnicode 7 0 R >>",
            // 6: CIDFont descendant
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /CustomType0 /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /W [ 1 [ 500 600 ] ] >>",
            // 7: ToUnicode stream
            $"<< /Length {cMapStream.Length} >>\nstream\n{cMapStream}\nendstream"
        );

        using var doc = await PdfVectorDocument.OpenAsync(pdfBytes);
        var displayList = await doc.GetPageDisplayListAsync(1);

        Assert.NotNull(displayList);
        Assert.True(displayList.Features.HasFlag(PdfFeatureSet.Text));

        var glyphRunCmd = displayList.Commands.OfType<DrawGlyphRun>().Single();
        var run = glyphRunCmd.Run;
        Assert.Equal(2, run.Glyphs.Count);

        // CID 1 mapped to 'A' (0x0041)
        Assert.Equal(1, run.Glyphs[0].GlyphId);
        Assert.Equal("A", run.Glyphs[0].Unicode);
        Assert.Equal(500.0 / 1000.0 * 12.0, run.Glyphs[0].AdvanceX);

        // CID 2 mapped to 'B' (0x0042)
        Assert.Equal(2, run.Glyphs[1].GlyphId);
        Assert.Equal("B", run.Glyphs[1].Unicode);
        Assert.Equal(600.0 / 1000.0 * 12.0, run.Glyphs[1].AdvanceX);
    }

    private static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new List<int>();

        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n");
            sb.Append(objects[i].Trim());
            sb.Append("\nendobj\n");
        }

        int startXref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
