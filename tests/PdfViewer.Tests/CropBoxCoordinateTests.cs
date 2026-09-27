using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Pages whose crop box does not start at 0,0 (common in scans and publisher PDFs): PDFium
/// reports text and annotation boxes in absolute user space, the viewer's coordinates start at
/// the crop box. Selections, search hits and annotations must land on the text, not shifted by
/// the crop box's offset, and annotations must save and load back where they were drawn.
/// </summary>
public class CropBoxCoordinateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "cropbox_" + Guid.NewGuid().ToString("N") + ".pdf");
    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    /// <summary>Media 600 x 800, crop [100 200 400 600]: "Hello" at user space (150, 500), 20 pt.</summary>
    private PdfiumDocumentService Open()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 20 Tf 150 500 Td (Hello) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 600 800]", extra: "/CropBox [100 200 400 600]");
        File.WriteAllBytes(_path, b.Build());
        var s = new PdfiumDocumentService();
        s.OpenDocumentAsync(_path).GetAwaiter().GetResult();
        return s;
    }

    // "Hello" relative to the crop box: x (150-100)/300, top of the glyphs ≈ (500+14.5-200)/400 from the bottom.
    private const double ExpectedX = 50.0 / 300, ExpectedTop = 1 - 314.5 / 400;

    [Fact]
    public void TextSegments_LandOnTheText()
    {
        using var s = Open();
        var hello = s.ExtractPageTextSegments(1).Single(t => t.Text == "Hello");
        Assert.InRange(hello.X, ExpectedX - 0.01, ExpectedX + 0.01);
        Assert.InRange(hello.Y, ExpectedTop - 0.02, ExpectedTop + 0.02);
    }

    [Fact]
    public async Task SearchHits_LandOnTheText()
    {
        using var s = Open();
        var hit = Assert.Single(await s.SearchTextAsync("Hello"));
        Assert.InRange(hit.X, ExpectedX - 0.01, ExpectedX + 0.01);
        Assert.InRange(hit.Y, ExpectedTop - 0.02, ExpectedTop + 0.02);
    }

    [Fact]
    public async Task Annotations_SaveAndLoadWhereTheyWereDrawn()
    {
        using (var s = Open())
        {
            var annot = new AnnotationModel { PageNumber = 1, Type = AnnotationType.Rectangle, X = 0.25, Y = 0.5, Width = 0.2, Height = 0.1, ColorHex = "#FFFF0000", StrokeThickness = 1 };
            string saved = _path + ".annot.pdf";
            await s.SaveAnnotatedDocumentAsync(saved, AnnotationSaveMode.Embedded, new[] { annot }, _path);
            File.Copy(saved, _path, overwrite: true);
            File.Delete(saved);
        }
        // In the file: where the page shows it (x 100 + 0.25 * 300, top 200 + (1 - 0.5) * 400).
        using (var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(File.ReadAllBytes(_path)))
        {
            var r = doc.Resolver;
            var annots = (PdfEngine.Vector.Objects.PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
            var rect = (PdfEngine.Vector.Objects.PdfArray)((PdfEngine.Vector.Objects.PdfDictionary)r.Resolve(annots[0])!)["Rect"]!;
            rect[0].TryGetNumber(out double x0); rect[3].TryGetNumber(out double top);
            Assert.Equal(175, x0, 0);
            Assert.Equal(400, top, 0);
        }
        using var reopened = new PdfiumDocumentService();
        await reopened.OpenDocumentAsync(_path);
        var back = Assert.Single(reopened.LoadExistingAnnotations());
        Assert.Equal(0.25, back.X, 2);
        Assert.Equal(0.5, back.Y, 2);
        Assert.Equal(0.2, back.Width, 2);
        Assert.Equal(0.1, back.Height, 2);
    }
}
