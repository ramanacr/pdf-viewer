using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.PdfA.Xmp;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// PDF/A (ISO 19005): the validator reports each requirement a document breaks under its clause;
/// the converter turns an ordinary document into PDF/A-2b or -3b that passes that validation and
/// looks exactly as before.
/// </summary>
public class PdfATests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>An ordinary document: an unembedded font, RGB colour, a JavaScript open action, a sticky note without an appearance.</summary>
    private static byte[] Ordinary(PdfTestEncryption? encryption = null)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int js = b.Add("<< /S /JavaScript /JS (app.alert\\(1\\)) >>");
        int note = b.Add("<< /Type /Annot /Subtype /Text /Rect [300 300 320 320] /Contents (Check this) /C [1 1 0] >>");
        b.AddPage("0.2 0.4 0.8 rg 40 500 200 100 re f BT /F1 18 Tf 0 0 0 rg 40 700 Td (Archive me) Tj ET",
            $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]", extra: $"/Annots [{note} 0 R]");
        b.WithCatalog($"/OpenAction {js} 0 R");
        return encryption != null ? b.Build(encryption) : b.Build();
    }

    private static async Task<PdfAReport> Validate(byte[] pdf, PdfAFlavour? flavour = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfAValidator.Validate(doc, pdf, flavour);
    }

    private static async Task<PdfAConversionResult> Convert(byte[] pdf, PdfAFlavour? flavour = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavour = flavour ?? PdfAFlavour.A2b });
    }

    private static async Task<RenderedPage> Pdfium(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = 1, Dpi = 72 });
    }

    [Fact]
    public async Task AnOrdinaryDocument_BreaksTheRequirements_ByClause()
    {
        var report = await Validate(Ordinary());
        Assert.False(report.IsCompliant);
        Assert.Null(report.Claimed);
        var rules = report.Issues.Select(i => i.RuleId).ToHashSet();
        Assert.Contains("6.6.2.1-1", rules);     // no XMP metadata
        Assert.Contains("6.2.4.3-2", rules);     // DeviceRGB without an output intent
        Assert.Contains("6.2.11.4.1-1", rules);  // Helvetica not embedded
        Assert.Contains("6.5.1-1", rules);       // a JavaScript action
        Assert.Contains("6.3.3-1", rules);       // an annotation without an appearance
        Assert.Contains("6.3.2-1", rules);       // no flags, so not set to print
    }

    [Fact]
    public async Task Converting_GivesValidPdfA2b_ThatLooksTheSame()
    {
        byte[] pdf = Ordinary();
        var result = await Convert(pdf);
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        Assert.Contains(result.Changes, c => c.Contains("Embedded", StringComparison.Ordinal) && c.Contains("Helvetica", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("output intent", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("JavaScript", StringComparison.Ordinal));
        Assert.Contains(result.Changes, c => c.Contains("Generated appearances", StringComparison.Ordinal));

        // It claims what it is, and a second check agrees.
        var again = await Validate(result.Bytes);
        Assert.Equal(PdfAFlavour.A2b, again.Claimed);
        Assert.True(again.IsCompliant);

        // Same text, same page (the annotation now draws its note icon, as viewers drew it before).
        using var engine = new PdfiumEngine();
        await using (var doc = await engine.OpenDocumentAsync(result.Bytes, null))
            Assert.Contains("Archive me", await engine.TextService.ExtractPageTextAsync(doc, 1));
        using var before = await Pdfium(pdf);
        using var after = await Pdfium(result.Bytes);
        int differing = 0;
        var a = before.Pixels.Span;
        var b = after.Pixels.Span;
        for (int y = 0; y < before.HeightPixels; y++)
            for (int x = 0; x < before.WidthPixels; x++)
            {
                // The note icon's area is where appearances may differ.
                if (x >= 295 && x <= 325 && y >= 792 - 325 && y <= 792 - 295) continue;
                int i = y * before.Stride + x * 4;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 48) differing++;
            }
        Assert.True(differing < before.WidthPixels * before.HeightPixels / 200, $"{differing} pixels differ");
    }

    [Fact]
    public async Task EncryptedDocuments_AreDecrypted_WhenConverted()
    {
        byte[] pdf = Ordinary(new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner"));
        Assert.Contains((await Validate(pdf)).Issues, i => i.RuleId == "6.1.3-2");
        var result = await Convert(pdf);
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(result.Bytes));
    }

    [Fact]
    public async Task PdfA3_KeepsEmbeddedFiles_AsAssociatedFiles()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add(Helv);
        int data = b.AddStream("/Type /EmbeddedFile", "a,b\n1,2\n");
        int spec = b.Add($"<< /Type /Filespec /F (data.csv) /EF << /F {data} 0 R >> >>");
        int names = b.Add($"<< /Names [(data.csv) {spec} 0 R] >>");
        b.AddPage("BT /F1 12 Tf 40 700 Td (Invoice) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        b.WithCatalog($"/Names << /EmbeddedFiles {names} 0 R >>");
        byte[] pdf = b.Build();

        var a2 = await Convert(pdf, PdfAFlavour.A2b);
        Assert.True(a2.Report.IsCompliant, string.Join("\n", a2.Report.Issues));
        Assert.Contains(a2.Changes, c => c.Contains("Removed embedded files", StringComparison.Ordinal));

        var a3 = await Convert(pdf, PdfAFlavour.A3b);
        Assert.True(a3.Report.IsCompliant, string.Join("\n", a3.Report.Issues));
        string text = Encoding.Latin1.GetString(a3.Bytes);
        Assert.Contains("/AFRelationship", text);
        Assert.Contains("/AF [", text);
    }

    [Fact]
    public void TheGeneratedSrgbProfile_IsAValidIccV2DisplayProfile()
    {
        var bytes = SrgbProfile.Bytes;
        var header = PdfAChecker.IccHeader.Read(bytes);
        Assert.NotNull(header);
        Assert.Equal(("mntr", "RGB ", 2, 3), (header!.Class, header.Space, header.Major, header.Channels));
        Assert.Equal(bytes.Length, (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }

    [Theory]
    [InlineData("exif:ColorSpace", "1", true)]
    [InlineData("exif:ColorSpace", "1.3", false)]
    [InlineData("crs:AutoBrightness", "True", true)]
    [InlineData("crs:AutoBrightness", "false", false)]
    [InlineData("tiff:XResolution", "300/1", true)]
    [InlineData("tiff:XResolution", "300", false)]
    [InlineData("xmp:CreateDate", "2016-02-01T13:19:21+01:00", true)]
    [InlineData("xmp:CreateDate", "yesterday", false)]
    public void XmpValues_AreCheckedAgainstTheirSchemaTypes(string property, string value, bool valid)
    {
        var (prefix, name) = (property.Split(':')[0], property.Split(':')[1]);
        string ns = prefix switch { "exif" => XmpSchemas.Exif, "crs" => XmpSchemas.Crs, "tiff" => XmpSchemas.Tiff, _ => XmpSchemas.Xmp };
        string type = XmpSchemas.Schemas[ns][name];
        Assert.Equal(valid, XmpSchemas.Check(new XmpValue { Kind = XmpKind.Simple, Text = value }, type, XmpSchemas.Predefined) == null);
    }

    [Fact]
    public void XmpPackets_RoundTrip_ThroughTheWriter()
    {
        var props = new[]
        {
            new XmpProperty(XmpSchemas.Dc, "dc", "title", XmpWriter.LangAlt("Annual report")),
            new XmpProperty(XmpSchemas.Dc, "dc", "creator", XmpWriter.Array(XmpKind.Seq, new[] { "Jane Roe" })),
            new XmpProperty(XmpSchemas.PdfaId, "pdfaid", "part", XmpWriter.Simple("2")),
        };
        var packet = XmpPacket.Parse(XmpWriter.Write(props));
        Assert.Null(packet.Error);
        Assert.DoesNotContain("bytes=", packet.PacketHeader);
        Assert.Equal("Annual report", packet.Get(XmpSchemas.Dc, "title")!.Value.Items[0].Text);
        Assert.Equal("x-default", packet.Get(XmpSchemas.Dc, "title")!.Value.Items[0].Lang);
        Assert.Equal("2", packet.Simple(XmpSchemas.PdfaId, "part"));
    }
}
