using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Security;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;
using Kind = PdfEngine.Vector.Tests.Fixtures.TestEncryptionKind;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Writing encryption (Protect with Password) and removing it: AES-256 (R6) and AES-128 (R4)
/// full rewrites read back by our Standard security handler and by PDFium, with the same pixels
/// and text as the original; permissions, unencrypted metadata, object and cross-reference
/// streams in the source, Unicode (SASLprep'd) passwords, and the owner-password rule.
/// </summary>
public class EncryptionWriteTests
{
    private readonly ITestOutputHelper _output;
    public EncryptionWriteTests(ITestOutputHelper output) => _output = output;

    private static readonly PdfPermissionSet Restricted = PdfPermissionSet.All with
    {
        Printing = PdfPrintPermission.LowResolution, CopyContent = false, ChangeDocument = false,
    };

    private static byte[] Document(PdfTestEncryption? encryption = null, bool withMetadata = false, string extraObject = "")
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int cs = b.Add("[/Indexed /DeviceRGB 2 <FF0000 00A040 2040FF>]"); // a string that changes rendering
        var pixels = Enumerable.Range(0, 16 * 16 * 3).Select(i => (byte)(i * 7)).ToArray();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels, flate: true);
        int info = b.Add("<< /Title (Confidential \\(draft\\)) /Author <4A616E65> >>");
        var catalog = new StringBuilder();
        if (withMetadata)
        {
            int meta = b.AddStream("/Type /Metadata /Subtype /XML", "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'/><?xpacket end='w'?>");
            catalog.Append($"/Metadata {meta} 0 R ");
        }
        if (extraObject.Length > 0)
            catalog.Append($"/PieceInfo << /Extra {b.Add(extraObject)} 0 R >> ");
        b.AddPage("/CS1 cs 0 sc 10 10 60 60 re f 1 sc 70 10 60 60 re f 2 sc 130 10 60 60 re f " +
                  "BT /F1 18 Tf 10 150 Td (Secret 123) Tj ET q 80 0 0 60 110 90 cm /Im1 Do Q",
            $"<< /Font << /F1 {font} 0 R >> /ColorSpace << /CS1 {cs} 0 R >> /XObject << /Im1 {image} 0 R >> >>", flate: true);
        b.WithTrailer($"/Info {info} 0 R");
        if (catalog.Length > 0) b.WithCatalog(catalog.ToString());
        return b.Build(encryption);
    }

    private static async Task<byte[]> Protect(byte[] source, PdfEncryptionOptions options, string? openWith = null)
    {
        using var doc = await PdfVectorDocument.OpenAsync(source, password: openWith);
        return PdfEncryptor.Encrypt(doc, options).Bytes;
    }

    private static async Task<RenderedPage> RenderOurs(byte[] pdf, string? password)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, string.Join(", ", list.FallbackTokens.Select(t => t.Reason)));
        using var renderer = new Direct2DVectorRenderer();
        return await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
    }

    private static async Task<(RenderedPage Page, string Text)> RenderPdfium(byte[] pdf, string? password)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, password);
        var page = await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        return (page, await engine.TextService.ExtractPageTextAsync(doc, 1));
    }

    /// <summary>Our reader and PDFium both open the protected copy and show what the original shows.</summary>
    private async Task AssertSameAsOriginal(string name, byte[] original, string? originalPassword, byte[] protectedCopy, string? password)
    {
        using var before = await RenderOurs(original, originalPassword);
        using var after = await RenderOurs(protectedCopy, password);
        var (exact, _) = DifferentialRenderingTests.Compare(before, after);
        Assert.True(exact < 0.01, $"{name}: our rendering differs from the original ({exact:F3})");

        var (pdfiumOriginal, originalText) = await RenderPdfium(original, originalPassword);
        var (pdfiumCopy, copyText) = await RenderPdfium(protectedCopy, password);
        using (pdfiumOriginal)
        using (pdfiumCopy)
        {
            var (same, _) = DifferentialRenderingTests.Compare(pdfiumOriginal, pdfiumCopy);
            Assert.True(same < 0.01, $"{name}: PDFium renders the copy differently ({same:F3})");
            var (mean, bad) = DifferentialRenderingTests.Compare(after, pdfiumCopy);
            _output.WriteLine($"{name}: ours vs PDFium mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 2.0 && bad <= 0.02, $"{name}: mean {mean:F2} bad {bad:P2}");
        }
        Assert.Contains("Secret 123", copyText);
        Assert.Equal(originalText, copyText);
    }

    // ------------------------------------------------------------------ AES-256 and AES-128

    [Theory]
    [InlineData(PdfEncryptionStrength.Aes256, 6, "AES-256")]
    [InlineData(PdfEncryptionStrength.Aes128, 4, "AES-128")]
    public async Task UserAndOwnerPasswords_RoundTrip(PdfEncryptionStrength strength, int revision, string algorithm)
    {
        byte[] original = Document();
        byte[] pdf = await Protect(original, new PdfEncryptionOptions
        {
            UserPassword = "user", OwnerPassword = "owner", Permissions = Restricted, Strength = strength,
        });
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Encrypt", text);
        Assert.DoesNotContain("Confidential", text); // strings are encrypted
        Assert.DoesNotContain("/CFM /V2", text);     // never RC4

        using (var user = await PdfVectorDocument.OpenAsync(pdf, password: "user"))
        {
            var security = Assert.IsType<PdfStandardSecurityHandler>(user.Security);
            Assert.False(security.IsOwner);
            Assert.Equal(revision, security.Revision);
            Assert.Equal(algorithm, security.Algorithm);
            Assert.Equal(Restricted.ToFlags(), security.Permissions);
            Assert.Equal(Restricted, PdfPermissionSet.FromFlags(security.Permissions));
            if (revision == 6) Assert.True(security.PermsMatch());
            Assert.Equal("Confidential (draft)", user.Metadata.Title);
            Assert.Equal("Jane", user.Metadata.Author);
        }
        using (var owner = await PdfVectorDocument.OpenAsync(pdf, password: "owner"))
            Assert.True(owner.Security!.IsOwner);
        var missing = await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => await PdfVectorDocument.OpenAsync(pdf));
        Assert.True(missing.PasswordRequired);
        await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => await PdfVectorDocument.OpenAsync(pdf, password: "guess"));

        await AssertSameAsOriginal($"{strength} user", original, null, pdf, "user");
        await AssertSameAsOriginal($"{strength} owner", original, null, pdf, "owner");
        using var engine = new PdfiumEngine();
        await Assert.ThrowsAsync<PdfEngine.Exceptions.PdfPasswordRequiredException>(async () => await engine.OpenDocumentAsync(pdf));
    }

    [Theory]
    [InlineData(PdfEncryptionStrength.Aes256)]
    [InlineData(PdfEncryptionStrength.Aes128)]
    public async Task EmptyUserPassword_OpensWithoutPrompt_OwnerStillUnlocks(PdfEncryptionStrength strength)
    {
        byte[] original = Document();
        byte[] pdf = await Protect(original, new PdfEncryptionOptions { OwnerPassword = "owner", Permissions = Restricted, Strength = strength });
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            Assert.True(doc.IsEncrypted);
            Assert.False(doc.Security!.IsOwner);
            Assert.Equal(Restricted.ToFlags(), doc.Security.Permissions);
            Assert.False(PdfEncryptor.IsOwnerKnown(doc, null));
            Assert.False(PdfEncryptor.IsOwnerKnown(doc, "guess"));
            Assert.True(PdfEncryptor.IsOwnerKnown(doc, "owner"));
            Assert.False(doc.Security.IsOwner); // probing does not change how the document is open
        }
        using (var owner = await PdfVectorDocument.OpenAsync(pdf, password: "owner"))
            Assert.True(owner.Security!.IsOwner);
        await AssertSameAsOriginal($"{strength} empty user", original, null, pdf, null);
    }

    [Fact]
    public async Task OpenPasswordOnly_DoublesAsTheOwnerPassword()
    {
        byte[] pdf = await Protect(Document(), new PdfEncryptionOptions { UserPassword = "only" });
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: "only");
        Assert.Equal(PdfPermissionSet.All.ToFlags(), doc.Security!.Permissions);
        Assert.True(PdfEncryptor.IsOwnerKnown(doc, "only"));
    }

    // ------------------------------------------------------------------ permissions

    public static TheoryData<string, PdfPermissionSet> PermissionCases => new()
    {
        { "no printing", PdfPermissionSet.All with { Printing = PdfPrintPermission.None } },
        { "low-resolution printing", PdfPermissionSet.All with { Printing = PdfPrintPermission.LowResolution } },
        { "no changes", PdfPermissionSet.All with { ChangeDocument = false } },
        { "no copying", PdfPermissionSet.All with { CopyContent = false } },
        { "no accessibility copying", PdfPermissionSet.All with { CopyForAccessibility = false } },
        { "no commenting", PdfPermissionSet.All with { Comment = false } },
        { "no form filling", PdfPermissionSet.All with { Comment = false, FillForms = false } },
        { "no page assembly", PdfPermissionSet.All with { AssemblePages = false } },
        { "nothing", PdfPermissionSet.None },
    };

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public async Task EachPermission_IsReportedBack(string name, PdfPermissionSet permissions)
    {
        foreach (var strength in new[] { PdfEncryptionStrength.Aes256, PdfEncryptionStrength.Aes128 })
        {
            byte[] pdf = await Protect(Document(), new PdfEncryptionOptions { OwnerPassword = "owner", Permissions = permissions, Strength = strength });
            using var doc = await PdfVectorDocument.OpenAsync(pdf);
            var security = doc.Security!;
            Assert.Equal(permissions, PdfPermissionSet.FromFlags(security.Permissions));
            Assert.Equal(0, security.Permissions & 3);          // bits 1–2 reserved, clear
            Assert.Equal(0xC0, security.Permissions & 0xC0);    // bits 7–8 set

            var reader = PdfDocumentPermissions.FromFlags(security.Permissions, security.Revision, security.IsOwner);
            Assert.Equal(permissions.Printing != PdfPrintPermission.None, reader.CanPrint);
            Assert.Equal(permissions.Printing == PdfPrintPermission.HighResolution, reader.CanPrintHighQuality);
            Assert.Equal(permissions.ChangeDocument, reader.CanModify);
            Assert.Equal(permissions.CopyContent, reader.CanCopy);
            Assert.Equal(permissions.Comment, reader.CanAnnotate);
            Assert.Equal(permissions.FillForms || permissions.Comment, reader.CanFillForms);
            Assert.Equal(permissions.CopyForAccessibility || permissions.CopyContent, reader.CanExtractForAccessibility);
            Assert.Equal(permissions.AssemblePages || permissions.ChangeDocument, reader.CanAssemble);
            _output.WriteLine($"{name} {strength}: P={security.Permissions:X8}");
        }
    }

    // ------------------------------------------------------------------ metadata

    [Theory]
    [InlineData(PdfEncryptionStrength.Aes256)]
    [InlineData(PdfEncryptionStrength.Aes128)]
    public async Task Metadata_CanBeLeftInClear(PdfEncryptionStrength strength)
    {
        byte[] original = Document(withMetadata: true);
        byte[] clear = await Protect(original, new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner", Strength = strength, EncryptMetadata = false });
        byte[] hidden = await Protect(original, new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner", Strength = strength });
        Assert.Contains("<?xpacket begin=", Encoding.Latin1.GetString(clear));    // indexers can read it without the password
        Assert.DoesNotContain("<?xpacket", Encoding.Latin1.GetString(hidden));
        Assert.Contains("/EncryptMetadata false", Encoding.Latin1.GetString(clear));

        using (var doc = await PdfVectorDocument.OpenAsync(clear, password: "user"))
        {
            Assert.False(doc.Security!.IsMetadataEncrypted);
            if (strength == PdfEncryptionStrength.Aes256) Assert.True(((PdfStandardSecurityHandler)doc.Security).PermsMatch());
            var catalog = (Objects.PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
            var xmp = (Objects.PdfStream)doc.Resolver.Resolve(catalog["Metadata"])!;
            Assert.StartsWith("<?xpacket", Encoding.ASCII.GetString(xmp.GetRawBytes().Span));
        }
        await AssertSameAsOriginal($"{strength} clear metadata", original, null, clear, "user");
    }

    // ------------------------------------------------------------------ object and cross-reference streams

    /// <summary>
    /// A PDF 1.5 file whose objects (catalog, pages, page, font, the Indexed colour space's lookup
    /// string, document info) live in a compressed object stream, indexed by a cross-reference stream.
    /// </summary>
    private static byte[] CompressedDocument(bool withSignature = false)
    {
        string content = "/CS1 cs 0 sc 10 10 80 80 re f 1 sc 100 10 80 80 re f BT /F1 18 Tf 10 150 Td (Packed 456) Tj ET";
        var members = new List<(int Number, string Body)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [6 0 R] /Count 1 >>"),
            (3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            (4, "<< /Title (Packed title) /Producer <5061636B6572> >>"),
            (6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R " +
                "/Resources << /Font << /F1 3 0 R >> /ColorSpace << /CS1 9 0 R >> >> >>"),
            (9, "[/Indexed /DeviceRGB 1 <E02020 2080E0>]"),
        };
        if (withSignature)
            members.Add((10, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <3082> >>"));

        var header = new StringBuilder();
        var bodies = new StringBuilder();
        foreach (var (number, body) in members)
        {
            header.Append(CultureInfo.InvariantCulture, $"{number} {bodies.Length} ");
            bodies.Append(body).Append('\n');
        }
        byte[] objStm = Deflate(Encoding.Latin1.GetBytes(header.ToString() + bodies));

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.5\n%âãÏÓ\n");
        long contentOffset = ms.Position;
        byte[] contentBytes = Deflate(Encoding.Latin1.GetBytes(content));
        W($"5 0 obj\n<< /Length {contentBytes.Length} /Filter /FlateDecode >>\nstream\n");
        ms.Write(contentBytes);
        W("\nendstream\nendobj\n");
        long objStmOffset = ms.Position;
        W($"7 0 obj\n<< /Type /ObjStm /N {members.Count} /First {header.Length} /Length {objStm.Length} /Filter /FlateDecode >>\nstream\n");
        ms.Write(objStm);
        W("\nendstream\nendobj\n");

        int size = withSignature ? 11 : 10;
        long xrefOffset = ms.Position;
        var rows = new MemoryStream();
        void Row(int type, long field2, int field3)
        {
            rows.WriteByte((byte)type);
            for (int shift = 24; shift >= 0; shift -= 8) rows.WriteByte((byte)(field2 >> shift));
            rows.WriteByte((byte)(field3 >> 8)); rows.WriteByte((byte)field3);
        }
        for (int n = 0; n < size; n++)
        {
            int index = members.FindIndex(m => m.Number == n);
            if (n == 0) Row(0, 0, 65535);
            else if (n == 5) Row(1, contentOffset, 0);
            else if (n == 7) Row(1, objStmOffset, 0);
            else if (n == 8) Row(1, xrefOffset, 0);
            else Row(2, 7, index);
        }
        byte[] xrefData = rows.ToArray();
        W($"8 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root 1 0 R /Info 4 0 R " +
          $"/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] /Length {xrefData.Length} >>\nstream\n");
        ms.Write(xrefData);
        W($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(PdfEncryptionStrength.Aes256)]
    [InlineData(PdfEncryptionStrength.Aes128)]
    public async Task ObjectStreamsAndXrefStreams_AreRewrittenAndEncrypted(PdfEncryptionStrength strength)
    {
        byte[] original = CompressedDocument();
        using (var check = await PdfVectorDocument.OpenAsync(original))
            Assert.Equal("Packed title", check.Metadata.Title); // the fixture itself parses

        byte[] pdf = await Protect(original, new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner", Strength = strength });
        string text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("/ObjStm", text);
        Assert.DoesNotContain("/XRef", text);
        Assert.DoesNotContain("Packed title", text);
        Assert.DoesNotContain("E02020", text); // the lookup string moved out of the object stream and is encrypted

        using (var doc = await PdfVectorDocument.OpenAsync(pdf, password: "user"))
        {
            Assert.Equal("Packed title", doc.Metadata.Title);
            Assert.Equal("Packer", doc.Metadata.Producer);
        }
        using var before = await RenderOurs(original, null);
        using var after = await RenderOurs(pdf, "user");
        Assert.True(DifferentialRenderingTests.Compare(before, after).Mean < 0.01);
        var (pOriginal, originalText) = await RenderPdfium(original, null);
        var (pCopy, copyText) = await RenderPdfium(pdf, "user");
        using (pOriginal)
        using (pCopy)
            Assert.True(DifferentialRenderingTests.Compare(pOriginal, pCopy).Mean < 0.01);
        Assert.Contains("Packed 456", copyText);
        Assert.Equal(originalText, copyText);
    }

    [Fact]
    public async Task EncryptedSource_IsReencryptedOnlyWithTheOwnerPassword()
    {
        byte[] original = Document(new PdfTestEncryption(Kind.Rc4_128_R3, "olduser", "oldowner"));
        var options = new PdfEncryptionOptions { UserPassword = "new", OwnerPassword = "newowner" };

        using (var asUser = await PdfVectorDocument.OpenAsync(original, password: "olduser"))
        {
            var refused = Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.Encrypt(asUser, options));
            Assert.True(refused.PasswordRequired);
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.Encrypt(asUser, new PdfEncryptionOptions
            {
                UserPassword = "new", OwnerPassword = "newowner", CurrentOwnerPassword = "olduser",
            }));
            // The permissions password proves the right to change the security.
            byte[] proven = PdfEncryptor.Encrypt(asUser, new PdfEncryptionOptions
            {
                UserPassword = "new", OwnerPassword = "newowner", CurrentOwnerPassword = "oldowner",
            }).Bytes;
            using var reopened = await PdfVectorDocument.OpenAsync(proven, password: "new");
            Assert.Equal("AES-256", reopened.Security!.Algorithm);
        }

        byte[] pdf = await Protect(original, options, openWith: "oldowner");
        await Assert.ThrowsAsync<PdfEncryptedDocumentException>(async () => await PdfVectorDocument.OpenAsync(pdf, password: "olduser"));
        await AssertSameAsOriginal("re-encrypted", original, "olduser", pdf, "new");
    }

    // ------------------------------------------------------------------ removing security

    [Theory]
    [InlineData(Kind.Aes256_R6)]
    [InlineData(Kind.Aes128_R4)]
    [InlineData(Kind.Rc4_128_R4)]
    public async Task RemoveSecurity_WithTheOwnerPassword_WritesAPlainCopy(Kind scheme)
    {
        byte[] original = Document(new PdfTestEncryption(scheme, "user", "owner", permissions: Restricted.ToFlags()), withMetadata: true);
        byte[] plain;
        using (var owner = await PdfVectorDocument.OpenAsync(original, password: "owner"))
            plain = PdfEncryptor.RemoveSecurity(owner).Bytes;
        Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(plain));
        using (var doc = await PdfVectorDocument.OpenAsync(plain))
        {
            Assert.False(doc.IsEncrypted);
            Assert.Equal("Confidential (draft)", doc.Metadata.Title);
        }
        await AssertSameAsOriginal($"{scheme} removed", original, "user", plain, null);
    }

    [Fact]
    public async Task RemoveSecurity_IsRefusedWithoutTheOwnerPassword()
    {
        byte[] withUser = Document(new PdfTestEncryption(Kind.Aes256_R6, "user", "owner", permissions: Restricted.ToFlags()));
        using (var doc = await PdfVectorDocument.OpenAsync(withUser, password: "user"))
        {
            Assert.True(Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.RemoveSecurity(doc)).PasswordRequired);
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.RemoveSecurity(doc, "user"));
            Assert.Contains("incorrect", Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.RemoveSecurity(doc, "wrong")).Message);
            Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(PdfEncryptor.RemoveSecurity(doc, "owner").Bytes));
        }

        // No open password: the document opens freely, but its permissions stay the author's.
        byte[] noUser = Document(new PdfTestEncryption(Kind.Aes128_R4, "", "owner", permissions: Restricted.ToFlags()));
        using (var doc = await PdfVectorDocument.OpenAsync(noUser))
        {
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.RemoveSecurity(doc));
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfEncryptor.RemoveSecurity(doc, ""));
            byte[] plain = PdfEncryptor.RemoveSecurity(doc, "owner").Bytes;
            using var reopened = await PdfVectorDocument.OpenAsync(plain);
            Assert.False(reopened.IsEncrypted);
        }

        using (var unencrypted = await PdfVectorDocument.OpenAsync(Document()))
            Assert.Throws<InvalidOperationException>(() => PdfEncryptor.RemoveSecurity(unencrypted));
    }

    [Fact]
    public async Task ProtectThenRemove_RoundTripsToTheOriginal()
    {
        byte[] original = CompressedDocument();
        byte[] pdf = await Protect(original, new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner", Permissions = Restricted });
        byte[] plain;
        using (var owner = await PdfVectorDocument.OpenAsync(pdf, password: "owner"))
            plain = PdfEncryptor.RemoveSecurity(owner).Bytes;
        await AssertCompressedSame(original, plain, null);
    }

    private static async Task AssertCompressedSame(byte[] original, byte[] copy, string? password)
    {
        using var before = await RenderOurs(original, null);
        using var after = await RenderOurs(copy, password);
        Assert.True(DifferentialRenderingTests.Compare(before, after).Mean < 0.01);
        var (_, originalText) = await RenderPdfium(original, null);
        var (page, copyText) = await RenderPdfium(copy, password);
        page.Dispose();
        Assert.Equal(originalText, copyText);
    }

    // ------------------------------------------------------------------ signatures

    [Fact]
    public async Task SignedDocument_IsOnlyRewrittenOnceTheCallerAccepts()
    {
        byte[] signed = CompressedDocument(withSignature: true);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        Assert.Equal(1, PdfEncryptor.CountSignatures(doc));
        var ex = Assert.Throws<InvalidOperationException>(() => PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions { UserPassword = "user" }));
        Assert.Contains("signature", ex.Message);
        var result = PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions { UserPassword = "user", AllowInvalidatingSignatures = true });
        Assert.Equal(1, result.SignaturesInvalidated);
        Assert.Contains("<3082>", Encoding.Latin1.GetString(result.Bytes)); // a signature's /Contents is never encrypted (7.6.2)
        await AssertCompressedSame(signed, result.Bytes, "user");
    }

    // ------------------------------------------------------------------ passwords

    [Fact]
    public async Task UnicodePasswords_AreSaslPrepped_AndOpenInPdfium()
    {
        const string user = "Pässwörd-日本語-Ωμέγα";
        const string owner = "Владелец-🔒";
        byte[] original = Document();
        byte[] pdf = await Protect(original, new PdfEncryptionOptions { UserPassword = user, OwnerPassword = owner, Permissions = Restricted });

        using (var doc = await PdfVectorDocument.OpenAsync(pdf, password: user))
            Assert.False(doc.Security!.IsOwner);
        using (var doc = await PdfVectorDocument.OpenAsync(pdf, password: owner))
            Assert.True(doc.Security!.IsOwner);
        // Canonically equivalent spellings open it too: NFKC composes "a" + U+0308 into "ä".
        string decomposed = user.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(user, decomposed);
        using (var doc = await PdfVectorDocument.OpenAsync(pdf, password: decomposed))
            Assert.False(doc.Security!.IsOwner);
        // A soft hyphen is mapped to nothing, a no-break space to a space.
        byte[] spaced = await Protect(original, new PdfEncryptionOptions { UserPassword = "two words", OwnerPassword = "owner" });
        using (var doc = await PdfVectorDocument.OpenAsync(spaced, password: "two wo­rds"))
            Assert.True(doc.IsEncrypted);

        await AssertSameAsOriginal("unicode user", original, null, pdf, user);
        await AssertSameAsOriginal("unicode owner", original, null, pdf, owner);
    }

    [Fact]
    public void SaslPrep_FollowsRfc4013()
    {
        Assert.Equal("IX", PdfPasswordPreparation.SaslPrep("I­X", allowUnassigned: false));          // mapped to nothing
        Assert.Equal("user", PdfPasswordPreparation.SaslPrep("user", allowUnassigned: false));
        Assert.Equal("a b", PdfPasswordPreparation.SaslPrep("a b", allowUnassigned: false));         // non-ASCII space
        Assert.Equal("IX", PdfPasswordPreparation.SaslPrep("Ⅸ", allowUnassigned: false));             // NFKC: Roman numeral nine
        Assert.Equal("fi", PdfPasswordPreparation.SaslPrep("ﬁ", allowUnassigned: false));
        Assert.Throws<ArgumentException>(() => PdfPasswordPreparation.SaslPrep("a\u0007b", allowUnassigned: false));  // control
        Assert.Throws<ArgumentException>(() => PdfPasswordPreparation.SaslPrep("pw", allowUnassigned: false));  // private use
        Assert.Throws<ArgumentException>(() => PdfPasswordPreparation.SaslPrep("pw\uD800", allowUnassigned: false));  // lone surrogate
        Assert.Throws<ArgumentException>(() => PdfPasswordPreparation.SaslPrep("אabc", allowUnassigned: false)); // RTL mixed with LTR
        Assert.Throws<ArgumentException>(() => PdfPasswordPreparation.SaslPrep("א1", allowUnassigned: false));   // RTL must end RTL
        Assert.Equal("א1ב", PdfPasswordPreparation.SaslPrep("א1ב", allowUnassigned: false));
        Assert.Throws<ArgumentException>(() => PdfPasswordPreparation.SaslPrep("pw͸", allowUnassigned: false));  // unassigned
        Assert.Equal("pw͸", PdfPasswordPreparation.SaslPrep("pw͸", allowUnassigned: true));                 // allowed in a query

        byte[] longPassword = PdfPasswordPreparation.PrepareForEncryption(new string('é', 100)); // 200 bytes of UTF-8
        Assert.Equal(PdfPasswordPreparation.MaxBytes, longPassword.Length);
    }

    [Fact]
    public async Task LongPasswords_AreTruncatedTo127Bytes()
    {
        string password = new string('p', 127);
        byte[] pdf = await Protect(Document(), new PdfEncryptionOptions { UserPassword = password, OwnerPassword = "owner" });
        using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password + "ignored tail");
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public async Task UnusablePasswords_AreRefused()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Document());
        Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions()));
        Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions { UserPassword = "same", OwnerPassword = "same" }));
        Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions { UserPassword = "user", Permissions = Restricted }));
        Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions { UserPassword = "bad\u0001" }));
        Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(doc, new PdfEncryptionOptions
        {
            UserPassword = "日本語", OwnerPassword = "owner", Strength = PdfEncryptionStrength.Aes128,
        }));
    }

    [Fact]
    public async Task EachWrite_UsesFreshKeysAndIdentifiers()
    {
        byte[] original = Document();
        var options = new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" };
        byte[] a = await Protect(original, options), b = await Protect(original, options);
        Assert.NotEqual(a, b);
        using var da = await PdfVectorDocument.OpenAsync(a, password: "user");
        using var db = await PdfVectorDocument.OpenAsync(b, password: "user");
        Assert.NotEqual(da.XrefTable.Trailer!["ID"]!.ToString(), db.XrefTable.Trailer!["ID"]!.ToString());
        Assert.Contains("/ExtensionLevel 8", Encoding.Latin1.GetString(a)); // AES-256 in a 1.7 file (Adobe extension level 8)
        Assert.StartsWith("%PDF-1.7", Encoding.Latin1.GetString(a));
    }
}
