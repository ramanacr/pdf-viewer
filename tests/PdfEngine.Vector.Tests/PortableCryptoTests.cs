using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Security;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The managed MD5 and AES that the Standard security handler falls back to where the runtime has
/// none (browser WebAssembly): published test vectors, agreement with the platform's own
/// implementations on random input, and encrypted documents opened with the fallback forced.
/// </summary>
public class PortableCryptoTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a")]
    public void Md5_MatchesRfc1321Vectors(string message, string digest) =>
        Assert.Equal(Hex(digest), ManagedMd5.Hash(Encoding.ASCII.GetBytes(message)));

    [Fact]
    public void Md5_MatchesThePlatformAtEveryLengthAroundTheBlockBoundaries()
    {
        var random = new Random(1321);
        for (int length = 0; length <= 300; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(MD5.HashData(data), ManagedMd5.Hash(data));
        }
    }

    [Theory]
    [InlineData("000102030405060708090a0b0c0d0e0f", "69c4e0d86a7b0430d8cdb78070b4c55a")]
    [InlineData("000102030405060708090a0b0c0d0e0f1011121314151617", "dda97ca4864cdfe06eaf70a0ec0d7191")]
    [InlineData("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", "8ea2b7ca516745bfeafc49904b496089")]
    public void Aes_MatchesFips197Vectors(string key, string cipher)
    {
        // FIPS 197 appendix C: one block, so CBC with a zero IV is the bare cipher.
        byte[] plain = Hex("00112233445566778899aabbccddeeff");
        Assert.Equal(Hex(cipher), ManagedAes.Cbc(Hex(key), new byte[16], plain, decrypt: false));
        Assert.Equal(plain, ManagedAes.Cbc(Hex(key), new byte[16], Hex(cipher), decrypt: true));
    }

    [Fact]
    public void AesCbc_MatchesThePlatformInBothDirections()
    {
        var random = new Random(197);
        using var aes = Aes.Create();
        foreach (int keyLength in new[] { 16, 24, 32 })
        {
            for (int blocks = 1; blocks <= 24; blocks++)
            {
                var key = new byte[keyLength];
                var iv = new byte[16];
                var data = new byte[blocks * 16];
                random.NextBytes(key);
                random.NextBytes(iv);
                random.NextBytes(data);
                aes.Key = key;
                Assert.Equal(aes.EncryptCbc(data, iv, PaddingMode.None), ManagedAes.Cbc(key, iv, data, decrypt: false));
                Assert.Equal(aes.DecryptCbc(data, iv, PaddingMode.None), ManagedAes.Cbc(key, iv, data, decrypt: true));
            }
        }
    }

    [Fact]
    public void Aes_RejectsBadKeysAndPartialBlocks()
    {
        Assert.Throws<CryptographicException>(() => ManagedAes.Cbc(new byte[15], new byte[16], new byte[16], decrypt: true));
        Assert.Throws<CryptographicException>(() => ManagedAes.Cbc(new byte[16], new byte[16], new byte[17], decrypt: true));
    }

    [Fact]
    public void Desktop_UsesThePlatformImplementations()
    {
        Assert.False(PortableCrypto.UsesManagedMd5);
        Assert.False(PortableCrypto.UsesManagedAes);
        using (PortableCrypto.UseManagedImplementations())
        {
            Assert.True(PortableCrypto.UsesManagedMd5);
            Assert.True(PortableCrypto.UsesManagedAes);
        }
        Assert.False(PortableCrypto.UsesManagedMd5);
    }

    [Theory]
    [InlineData(TestEncryptionKind.Rc4_40_R2, "")]
    [InlineData(TestEncryptionKind.Rc4_128_R3, "")]
    [InlineData(TestEncryptionKind.Rc4_128_R4, "user")]
    [InlineData(TestEncryptionKind.Aes128_R4, "")]
    [InlineData(TestEncryptionKind.Aes128_R4, "user")]
    [InlineData(TestEncryptionKind.Aes256_R5, "user")]
    [InlineData(TestEncryptionKind.Aes256_R6, "")]
    [InlineData(TestEncryptionKind.Aes256_R6, "user")]
    public async Task EncryptedDocuments_OpenWithTheManagedFallback(TestEncryptionKind kind, string userPassword)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BT /F1 12 Tf 20 150 Td (Decrypted in a browser) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        byte[] pdf = b.Build(new PdfTestEncryption(kind, userPassword, "owner"));

        using (PortableCrypto.UseManagedImplementations())
        {
            foreach (var password in new[] { userPassword, "owner" })
            {
                using var doc = await PdfVectorDocument.OpenAsync(pdf, password: password);
                Assert.True(doc.IsEncrypted);
                Assert.Equal("Decrypted in a browser", PdfTextExtractor.LoadPage(doc, 1).GetAllText());
            }
            if (userPassword.Length > 0)
                Assert.Throws<PdfEngine.Vector.Diagnostics.PdfEncryptedDocumentException>(
                    () => PdfVectorDocument.OpenAsync(pdf, password: "wrong").AsTask().GetAwaiter().GetResult());
        }
    }
}
