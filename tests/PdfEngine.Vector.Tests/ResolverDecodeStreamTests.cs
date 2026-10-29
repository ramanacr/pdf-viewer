using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// <see cref="Parsing.PdfObjectResolver.DecodeStream"/>: the public way to read a stream's decoded data
/// from the COS object model (the decoder classes themselves are internal).
/// </summary>
public class ResolverDecodeStreamTests
{
    private const string Content = "BT /F1 12 Tf 10 150 Td (Secret 123) Tj ET";

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    /// <summary>A stream object written by hand, so that any /Filter can be given.</summary>
    private static byte[] StreamObject(string dictEntries, byte[] data)
    {
        using var body = new MemoryStream();
        body.Write(Encoding.Latin1.GetBytes($"<< {dictEntries} /Length {data.Length} >>\nstream\n"));
        body.Write(data);
        body.Write("\nendstream"u8);
        return body.ToArray();
    }

    private static PdfStream PageContents(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        var pages = Assert.IsType<PdfDictionary>(r.Resolve(doc.Catalog["Pages"]));
        var kids = Assert.IsType<PdfArray>(r.Resolve(pages["Kids"]));
        var page = Assert.IsType<PdfDictionary>(r.Resolve(kids[0]));
        return Assert.IsType<PdfStream>(r.Resolve(page["Contents"]));
    }

    [Fact]
    public async Task FlateStream_IsDecoded()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Content, flate: true);
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        var stream = PageContents(doc);
        Assert.Equal("FlateDecode", stream.Dictionary.GetName("Filter"));
        Assert.Equal(Content, Encoding.Latin1.GetString(doc.Resolver.DecodeStream(stream)));
    }

    [Fact]
    public async Task FilterChain_AndIndirectFilterEntries_AreApplied()
    {
        byte[] payload = Encoding.Latin1.GetBytes(Content);
        // ASCIIHex outside, Flate inside: the filters apply in array order.
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(Zlib(payload)) + ">");
        var b = new VectorPdfBuilder();
        int filters = b.Add("[/ASCIIHexDecode /FlateDecode]");
        int number = b.Add(StreamObject($"/Filter {filters} 0 R", hex));
        b.AddPage("");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        var stream = Assert.IsType<PdfStream>(doc.Resolver.Resolve(number));
        Assert.Equal(payload, doc.Resolver.DecodeStream(stream));
        Assert.Equal(hex, stream.GetRawBytes().ToArray()); // the stream itself is not changed
    }

    [Fact]
    public async Task UnfilteredStream_ReturnsItsBytes()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Content);
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        Assert.Equal(Content, Encoding.Latin1.GetString(doc.Resolver.DecodeStream(PageContents(doc))));
    }

    [Theory]
    [InlineData(TestEncryptionKind.Rc4_128_R3)]
    [InlineData(TestEncryptionKind.Aes128_R4)]
    [InlineData(TestEncryptionKind.Aes256_R6)]
    public async Task EncryptedDocument_GivesThePlainData(TestEncryptionKind scheme)
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Content, flate: true);
        byte[] pdf = b.Build(new PdfTestEncryption(scheme, "user", "owner"));
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: "user");

        var stream = PageContents(doc);
        Assert.True(stream.Decrypted);
        Assert.Equal(Content, Encoding.Latin1.GetString(doc.Resolver.DecodeStream(stream)));
    }

    [Fact]
    public async Task ImageCodecFilter_ThrowsUnsupportedFeature()
    {
        var b = new VectorPdfBuilder();
        int number = b.Add(StreamObject("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode",
            new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
        b.AddPage("");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        var stream = Assert.IsType<PdfStream>(doc.Resolver.Resolve(number));
        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() => doc.Resolver.DecodeStream(stream));
        Assert.IsAssignableFrom<PdfVectorException>(ex);
    }

    [Fact]
    public async Task UnknownFilter_ThrowsUnsupportedFeature()
    {
        var b = new VectorPdfBuilder();
        int number = b.Add(StreamObject("/Filter /NoSuchDecode", "abc"u8.ToArray()));
        b.AddPage("");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        var stream = Assert.IsType<PdfStream>(doc.Resolver.Resolve(number));
        Assert.Throws<PdfUnsupportedFeatureException>(() => doc.Resolver.DecodeStream(stream));
    }

    [Fact]
    public async Task CorruptFlateData_ThrowsSyntax()
    {
        var b = new VectorPdfBuilder();
        int number = b.Add(StreamObject("/Filter /FlateDecode", Enumerable.Repeat((byte)0xFF, 64).ToArray()));
        b.AddPage("");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        var stream = Assert.IsType<PdfStream>(doc.Resolver.Resolve(number));
        Assert.Throws<PdfSyntaxException>(() => doc.Resolver.DecodeStream(stream));
    }

    [Fact]
    public async Task DocumentLimits_Apply()
    {
        byte[] zeros = new byte[64 * 1024];
        var b = new VectorPdfBuilder();
        int number = b.Add(StreamObject("/Filter /FlateDecode", Zlib(zeros)));
        b.AddPage("");
        var limits = new PdfSecurityLimits { MaxDecodedStreamBytes = 1024 };
        using var doc = await PdfVectorDocument.OpenAsync(b.Build(), limits: limits);

        var stream = Assert.IsType<PdfStream>(doc.Resolver.Resolve(number));
        Assert.Throws<PdfResourceLimitException>(() => doc.Resolver.DecodeStream(stream));
    }

    [Fact]
    public async Task NullStream_Throws()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());

        Assert.Throws<ArgumentNullException>(() => doc.Resolver.DecodeStream(null!));
    }
}
