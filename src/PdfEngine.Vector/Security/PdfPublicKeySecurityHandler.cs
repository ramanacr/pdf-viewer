using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Security;

/// <summary>
/// The public-key security handler (ISO 32000-2 7.6.5, /Filter /Adobe.PubSec, sub-filters
/// adbe.pkcs7.s3, s4 and s5): each recipient's CMS envelope holds a 20-byte seed and the
/// recipient's permissions; the file key is SHA-1 (SHA-256 for AES-256) of the seed, every
/// /Recipients entry and — with /EncryptMetadata false — four 0xFF bytes.
/// </summary>
public sealed class PdfPublicKeySecurityHandler : PdfSecurityHandler
{
    private readonly List<byte[]> _recipients = new();
    private readonly bool _sha256;

    /// <summary>
    /// Where certificates come from when a document is opened without an explicit list: by default
    /// the personal stores (<see cref="StoreCertificates"/>). Hosts and tests may replace it.
    /// </summary>
    public static Func<IEnumerable<X509Certificate2>> CertificateSource { get; set; } = StoreCertificates;

    /// <summary>Certificates with private keys from the current user's and the machine's personal stores.</summary>
    public static IEnumerable<X509Certificate2> StoreCertificates()
    {
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            X509Certificate2Collection certs;
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                certs = store.Certificates;
            }
            catch (CryptographicException)
            {
                continue;
            }
            foreach (var c in certs)
                if (c.HasPrivateKey)
                    yield return c;
        }
    }

    internal PdfPublicKeySecurityHandler(PdfDictionary encrypt, Func<PdfObject?, PdfObject?> resolve)
        : base(encrypt, resolve)
    {
        string? sub = encrypt.GetName("SubFilter");
        if (sub is not ("adbe.pkcs7.s3" or "adbe.pkcs7.s4" or "adbe.pkcs7.s5"))
            throw new PdfEncryptedDocumentException($"Public-key sub-filter /{sub} is not supported.");
        Revision = sub[^1] - '0';
        PdfArray? recipients = resolve(encrypt["Recipients"]) as PdfArray;
        bool aes256 = V == 5;
        if (V >= 4)
        {
            // s5: recipients live in the crypt filter used for streams (7.6.5.2, Table 27).
            string stmF = encrypt.GetName("StmF") ?? "DefaultCryptFilter";
            if (resolve(encrypt["CF"]) is PdfDictionary cf && resolve(cf[stmF]) is PdfDictionary filter)
            {
                recipients = resolve(filter["Recipients"]) as PdfArray ?? recipients;
                aes256 |= filter.GetName("CFM") == "AESV3";
            }
        }
        if (recipients == null || recipients.Count == 0)
            throw new PdfEncryptedDocumentException("Public-key encryption without /Recipients.");
        foreach (var r in recipients)
            if (resolve(r) is PdfString s)
                _recipients.Add(s.RawBytes.ToArray());
        _sha256 = aes256;
    }

    /// <summary>Opens the document with the first certificate (holding its private key) that is a recipient.</summary>
    public bool Authenticate(IEnumerable<X509Certificate2> certificates)
    {
        var certs = certificates as IList<X509Certificate2> ?? certificates.ToList();
        byte[]? content = null;
        foreach (var envelope in _recipients)
        {
            try
            {
                content = CmsEnvelopedData.TryDecrypt(envelope, certs);
            }
            catch (Exception ex) when (ex is CryptographicException or System.Formats.Asn1.AsnContentException)
            {
                content = null;
            }
            if (content != null)
                break;
        }
        if (content == null || content.Length < 20)
            return false;

        var input = new List<byte>(20 + _recipients.Sum(r => r.Length) + 4);
        input.AddRange(content.AsSpan(0, 20).ToArray());
        foreach (var r in _recipients) input.AddRange(r);
        if (!EncryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] hash = _sha256 ? SHA256.HashData(input.ToArray()) : SHA1.HashData(input.ToArray());
        SetFileKey(hash[..Math.Min(KeyLength, hash.Length)]);

        // Bytes 21–24: the recipient's permissions, most significant byte first; absent = all.
        Permissions = content.Length >= 24 ? content[20] << 24 | content[21] << 16 | content[22] << 8 | content[23] : -1;
        IsOwner = Permissions == -1;
        return true;
    }
}
