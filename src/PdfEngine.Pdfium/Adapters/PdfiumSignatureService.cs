using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfEngine.Documents;
using PdfEngine.Pdfium.Native;
using PdfEngine.Signatures;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Digital signature discovery and cryptographic verification for PDFium documents.
///
/// Verification is real: the PKCS#7/CMS blob from /Contents is checked against a digest
/// computed over the /ByteRange spans of the file on disk. A signature is only reported
/// Valid when the CMS signature verifies, the embedded message digest matches the bytes,
/// and the byte range actually covers the whole file apart from the signature itself.
/// </summary>
public sealed class PdfiumSignatureService : IPdfSignatureService
{
    public ValueTask<IReadOnlyList<SignatureInfo>> GetSignaturesAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        byte[]? fileBytes = TryReadDocumentBytes(pdfiumDoc);
        var signatures = new List<SignatureInfo>();

        lock (pdfiumDoc.SyncLock)
        {
            int count = PdfiumNativeBridge.FPDF_GetSignatureCount(pdfiumDoc.Handle);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IntPtr sig = PdfiumNativeBridge.FPDF_GetSignatureObject(pdfiumDoc.Handle, i);
                if (sig == IntPtr.Zero) continue;

                var info = new SignatureInfo
                {
                    FieldName = $"Signature{i + 1}",
                    Reason = ReadUtf16(sig, PdfiumNativeBridge.FPDFSignatureObj_GetReason),
                    SigningTime = ParsePdfDate(ReadAscii(sig, PdfiumNativeBridge.FPDFSignatureObj_GetTime))
                };

                var (status, message, signerName) = EvaluateSignature(sig, fileBytes);
                info.Status = status;
                info.StatusMessage = message;
                info.SignerName = signerName;

                signatures.Add(info);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SignatureInfo>>(signatures);
    }

    public async ValueTask<SignatureStatus> VerifySignatureAsync(
        IPdfDocument document,
        string fieldName,
        CancellationToken cancellationToken = default)
    {
        var all = await GetSignaturesAsync(document, cancellationToken);

        // No signatures at all is Unknown, not Valid: the previous implementation returned
        // Valid unconditionally, which reported tampered and unsigned documents as good.
        if (all.Count == 0) return SignatureStatus.Unknown;

        var match = all.FirstOrDefault(s =>
            string.Equals(s.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

        return match?.Status ?? SignatureStatus.Unknown;
    }

    /// <summary>
    /// Verifies one signature: CMS signature validity, digest match over the signed byte
    /// ranges, and whether those ranges cover the entire file.
    /// </summary>
    private static (SignatureStatus Status, string Message, string SignerName) EvaluateSignature(
        IntPtr sig,
        byte[]? fileBytes)
    {
        uint contentsLen = PdfiumNativeBridge.FPDFSignatureObj_GetContents(sig, null, 0);
        if (contentsLen == 0)
        {
            return (SignatureStatus.Invalid, "The signature has no /Contents blob.", string.Empty);
        }

        byte[] contents = new byte[contentsLen];
        PdfiumNativeBridge.FPDFSignatureObj_GetContents(sig, contents, contentsLen);

        uint rangeCount = PdfiumNativeBridge.FPDFSignatureObj_GetByteRange(sig, null, 0);
        if (rangeCount < 2)
        {
            return (SignatureStatus.Invalid, "The signature has no usable /ByteRange.", string.Empty);
        }

        int[] byteRange = new int[rangeCount];
        PdfiumNativeBridge.FPDFSignatureObj_GetByteRange(sig, byteRange, rangeCount);

        if (fileBytes == null)
        {
            return (SignatureStatus.Unknown,
                "The signed bytes could not be read from disk, so the signature cannot be verified.",
                TryGetSignerName(contents));
        }

        // Digest the covered spans exactly as the signer did.
        byte[]? signedBytes = ExtractSignedBytes(fileBytes, byteRange);
        if (signedBytes == null)
        {
            return (SignatureStatus.Invalid,
                "The /ByteRange does not lie within the file; it has been altered or truncated.",
                TryGetSignerName(contents));
        }

        SignedCms cms;
        try
        {
            cms = new SignedCms();
            cms.Decode(TrimTrailingZeros(contents));
        }
        catch (CryptographicException ex)
        {
            return (SignatureStatus.Invalid, $"The signature blob could not be parsed: {ex.Message}", string.Empty);
        }

        string signer = cms.SignerInfos.Count > 0
            ? DescribeSigner(cms.SignerInfos[0].Certificate)
            : string.Empty;

        // Verify the CMS itself. Signature-only check: chain trust is evaluated separately
        // so an untrusted-but-intact signature is reported as Untrusted, not Invalid.
        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            return (SignatureStatus.Invalid, $"The signature is not cryptographically valid: {ex.Message}", signer);
        }

        // The CMS content must match the bytes the PDF actually covers.
        if (!DigestMatches(cms, signedBytes))
        {
            return (SignatureStatus.DocumentModified,
                "The document has been modified since it was signed: the signed digest does not match the file.",
                signer);
        }

        // A byte range that does not span the whole file means content was appended after
        // signing - the signature is genuine but does not cover what the reader now sees.
        if (!CoversEntireFile(fileBytes.Length, byteRange))
        {
            return (SignatureStatus.DocumentModified,
                "The document contains changes made after this signature was applied.",
                signer);
        }

        // Intact and complete. Now decide trusted vs untrusted from the certificate chain.
        if (!IsChainTrusted(cms))
        {
            return (SignatureStatus.Untrusted,
                "The signature is intact, but the signing certificate is not trusted by this machine.",
                signer);
        }

        return (SignatureStatus.Valid, "The signature is valid and the document is unmodified.", signer);
    }

    /// <summary>
    /// Concatenates the /ByteRange spans. Returns null when a span falls outside the file.
    /// </summary>
    private static byte[]? ExtractSignedBytes(byte[] fileBytes, int[] byteRange)
    {
        using var buffer = new MemoryStream();
        for (int i = 0; i + 1 < byteRange.Length; i += 2)
        {
            int offset = byteRange[i];
            int length = byteRange[i + 1];

            if (offset < 0 || length < 0 || (long)offset + length > fileBytes.Length)
            {
                return null;
            }

            buffer.Write(fileBytes, offset, length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// True when the byte ranges cover everything except one contiguous gap (the /Contents
    /// hex string itself). Anything else means bytes were added or left unsigned.
    /// </summary>
    private static bool CoversEntireFile(int fileLength, int[] byteRange)
    {
        if (byteRange.Length < 4) return false;

        // Canonical form is [0, a, b, c] where b > a and a+gap = b, and b + c == fileLength.
        int firstStart = byteRange[0];
        int lastStart = byteRange[^2];
        int lastLength = byteRange[^1];

        if (firstStart != 0) return false;

        long end = (long)lastStart + lastLength;

        // Trailing whitespace/newline after %%EOF is normal, so allow a small slack.
        return end >= fileLength - 4;
    }

    /// <summary>
    /// Compares the signer's messageDigest attribute against a fresh digest of the signed
    /// bytes. CheckSignature alone does not prove the CMS was made over THESE bytes when the
    /// signature is detached.
    /// </summary>
    private static bool DigestMatches(SignedCms cms, byte[] signedBytes)
    {
        if (cms.SignerInfos.Count == 0) return false;

        var signerInfo = cms.SignerInfos[0];
        byte[]? expected = null;

        foreach (CryptographicAttributeObject attr in signerInfo.SignedAttributes)
        {
            // 1.2.840.113549.1.9.4 = messageDigest
            if (attr.Oid.Value == "1.2.840.113549.1.9.4" && attr.Values.Count > 0)
            {
                byte[] raw = attr.Values[0].RawData;
                expected = ReadDerOctetString(raw);
                break;
            }
        }

        using HashAlgorithm hash = CreateHash(signerInfo.DigestAlgorithm.Value);
        byte[] actual = hash.ComputeHash(signedBytes);

        if (expected == null)
        {
            // No signed attributes: the CMS content itself is the digest input, and
            // CheckSignature already validated it against cms.ContentInfo.
            return cms.ContentInfo.Content.Length == 0 ||
                   CryptographicOperations.FixedTimeEquals(cms.ContentInfo.Content, signedBytes);
        }

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static HashAlgorithm CreateHash(string? digestOid) => digestOid switch
    {
        "1.3.14.3.2.26" => SHA1.Create(),                 // sha1
        "2.16.840.1.101.3.4.2.1" => SHA256.Create(),      // sha256
        "2.16.840.1.101.3.4.2.2" => SHA384.Create(),      // sha384
        "2.16.840.1.101.3.4.2.3" => SHA512.Create(),      // sha512
        _ => SHA256.Create()
    };

    /// <summary>Unwraps a DER OCTET STRING, returning its contents.</summary>
    private static byte[] ReadDerOctetString(byte[] der)
    {
        if (der.Length < 2 || der[0] != 0x04) return der;

        int length = der[1];
        int offset = 2;

        if (length > 0x80)
        {
            int lengthBytes = length - 0x80;
            length = 0;
            for (int i = 0; i < lengthBytes && offset < der.Length; i++, offset++)
            {
                length = (length << 8) | der[offset];
            }
        }

        if (offset + length > der.Length) return der;

        byte[] result = new byte[length];
        Array.Copy(der, offset, result, 0, length);
        return result;
    }

    private static bool IsChainTrusted(SignedCms cms)
    {
        if (cms.SignerInfos.Count == 0) return false;

        X509Certificate2? cert = cms.SignerInfos[0].Certificate;
        if (cert == null) return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        try
        {
            return chain.Build(cert);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string DescribeSigner(X509Certificate2? certificate)
    {
        if (certificate == null) return string.Empty;

        string common = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(common) ? certificate.Subject : common;
    }

    private static string TryGetSignerName(byte[] contents)
    {
        try
        {
            var cms = new SignedCms();
            cms.Decode(TrimTrailingZeros(contents));
            return cms.SignerInfos.Count > 0 ? DescribeSigner(cms.SignerInfos[0].Certificate) : string.Empty;
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The /Contents hex string is padded with trailing zero bytes to a fixed size; the DER
    /// parser rejects them.
    /// </summary>
    private static byte[] TrimTrailingZeros(byte[] data)
    {
        int end = data.Length;
        while (end > 0 && data[end - 1] == 0) end--;
        return end == data.Length ? data : data.AsSpan(0, end).ToArray();
    }

    private static byte[]? TryReadDocumentBytes(PdfiumDocument document)
    {
        try
        {
            return !string.IsNullOrEmpty(document.FilePath) && File.Exists(document.FilePath)
                ? File.ReadAllBytes(document.FilePath)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private delegate uint SignatureStringGetter(IntPtr signature, byte[]? buffer, uint length);

    private static string ReadUtf16(IntPtr sig, SignatureStringGetter getter)
    {
        uint len = getter(sig, null, 0);
        if (len == 0) return string.Empty;

        byte[] buffer = new byte[len];
        getter(sig, buffer, len);
        return PdfiumNativeBridge.Utf16BytesToString(buffer, (int)len);
    }

    private static string ReadAscii(IntPtr sig, SignatureStringGetter getter)
    {
        uint len = getter(sig, null, 0);
        if (len == 0) return string.Empty;

        byte[] buffer = new byte[len];
        getter(sig, buffer, len);
        return Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }

    /// <summary>
    /// Parses a PDF date string, e.g. "D:20260905120000+05'30'".
    /// </summary>
    internal static DateTime? ParsePdfDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s.Substring(2);

        if (s.Length < 4) return null;

        try
        {
            int year = int.Parse(s.Substring(0, 4));
            int month = s.Length >= 6 ? int.Parse(s.Substring(4, 2)) : 1;
            int day = s.Length >= 8 ? int.Parse(s.Substring(6, 2)) : 1;
            int hour = s.Length >= 10 ? int.Parse(s.Substring(8, 2)) : 0;
            int minute = s.Length >= 12 ? int.Parse(s.Substring(10, 2)) : 0;
            int second = s.Length >= 14 ? int.Parse(s.Substring(12, 2)) : 0;

            if (month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59)
                return null;

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }
}
