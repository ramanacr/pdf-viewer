using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Optimize;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Reducing file size: unused objects go, identical resources are stored once, objects are packed
/// into object streams, images shown above a resolution are downsampled with their masks, and the
/// document reads and renders as before in both engines.
/// </summary>
public class OptimizerTests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static async Task<PdfOptimizeResult> Optimize(byte[] pdf, PdfOptimizeOptions? options = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfOptimizer.Optimize(doc, pdf, options);
    }

    private static async Task<string> Text(byte[] pdf, int page = 1)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, page);
    }

    [Fact]
    public async Task UnusedAndDuplicateObjects_Go_AndObjectsArePacked()
    {
        var b = new VectorPdfBuilder();
        string program = new string('x', 4000);
        int file1 = b.AddStream("", program), file2 = b.AddStream("", program); // the same stream twice
        int font1 = b.Add(Helv), font2 = b.Add(Helv);
        b.Add("<< /Unused true /Big (" + new string('u', 5000) + ") >>"); // nothing refers to it
        b.AddPage("BT /F1 12 Tf 40 700 Td (First page) Tj ET", $"<< /Font << /F1 {font1} 0 R >> /XObject << >> /Extra [{file1} 0 R] >>", mediaBox: "[0 0 612 792]");
        b.AddPage("BT /F2 12 Tf 40 700 Td (Second page) Tj ET", $"<< /Font << /F2 {font2} 0 R >> /Extra [{file2} 0 R] >>", mediaBox: "[0 0 612 792]");
        byte[] pdf = b.Build();

        var result = await Optimize(pdf);
        Assert.True(result.Bytes.Length < pdf.Length / 2, $"{pdf.Length} -> {result.Bytes.Length}");
        Assert.True(result.DuplicatesMerged >= 2, $"merged {result.DuplicatesMerged}"); // the stream and the font
        string raw = Encoding.Latin1.GetString(result.Bytes);
        Assert.Contains("/ObjStm", raw);
        Assert.Contains("/XRef", raw);
        Assert.DoesNotContain("uuuuuuuuuu", raw);

        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("First page", await Text(result.Bytes, 1));
        Assert.Contains("Second page", await Text(result.Bytes, 2));
    }

    [Fact]
    public async Task ImagesShownAboveTheResolution_AreDownsampled_WithTheirMasks()
    {
        var b = new VectorPdfBuilder();
        int n = 800;
        var rgb = new byte[n * n * 3];
        var rnd = new Random(7);
        rnd.NextBytes(rgb); // noise: it does not compress, so size tells
        var alpha = new byte[n * n];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)(i % n < n / 2 ? 255 : 0);
        int mask = b.AddStream($"/Type /XObject /Subtype /Image /Width {n} /Height {n} /ColorSpace /DeviceGray /BitsPerComponent 8", alpha, flate: true);
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width {n} /Height {n} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R", rgb, flate: true);
        // 800 pixels over 2 inches: 400 ppi.
        b.AddPage("q 144 0 0 144 100 100 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>", mediaBox: "[0 0 400 400]");
        byte[] pdf = b.Build();

        var kept = await Optimize(pdf);
        Assert.Equal(0, kept.ImagesDownsampled); // no resolution asked for

        var result = await Optimize(pdf, new PdfOptimizeOptions { ImageResolution = 150 });
        Assert.Equal(1, result.ImagesDownsampled);
        Assert.True(result.Bytes.Length < pdf.Length / 5, $"{pdf.Length} -> {result.Bytes.Length}");
        using var doc = await PdfVectorDocument.OpenAsync(result.Bytes);
        var list = await doc.GetPageDisplayListAsync(1);
        var drawn = Assert.Single(list.Commands.OfType<DrawImage>()).Image;
        Assert.Equal(300, drawn.Width); // 2 inches at 150 ppi
        Assert.True(drawn.HasAlpha);
    }

    [Fact]
    public async Task APdfA1Document_KeepsItsClaim_WithoutObjectStreams()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                     "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"1\" pdfaid:conformance=\"B\"/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        int metadata = b.AddStream("/Type /Metadata /Subtype /XML", xmp);
        b.AddPage("BT /F1 12 Tf 40 100 Td (Archived) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 200 200]");
        byte[] pdf = b.WithCatalog($"/Metadata {metadata} 0 R").Build();

        var result = await Optimize(pdf);
        string raw = Encoding.Latin1.GetString(result.Bytes);
        Assert.StartsWith("%PDF-1.4", raw);
        Assert.DoesNotContain("/ObjStm", raw);
        Assert.Contains("pdfaid:part=\"1\"", raw); // the metadata stays readable
        Assert.Contains("Archived", await Text(result.Bytes));
    }

    [Fact]
    public async Task AnEncryptedDocument_IsRefused()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BT ET");
        byte[] pdf = b.Build(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "secret", "owner"));
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: "secret");
        Assert.Throws<InvalidOperationException>(() => PdfOptimizer.Optimize(doc, pdf));
    }
}
