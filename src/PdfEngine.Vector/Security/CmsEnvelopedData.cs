using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfEngine.Vector.Security;

/// <summary>
/// Decrypts CMS EnvelopedData (RFC 5652 §6) with a recipient's certificate, using only in-box
/// .NET: key-transport recipients (RSA PKCS#1 v1.5 or OAEP) identified by issuer and serial
/// number or subject key identifier, and AES-128/192/256-CBC, 3DES-CBC, RC2-CBC or RC4 content
/// encryption — what PDF public-key encryption (ISO 32000-2 7.6.5) writes.
/// </summary>
internal static class CmsEnvelopedData
{
    private const string EnvelopedDataOid = "1.2.840.113549.1.7.3";
    private const string RsaOid = "1.2.840.113549.1.1.1";
    private const string RsaOaepOid = "1.2.840.113549.1.1.7";

    private sealed record Recipient(byte[]? Issuer, byte[]? Serial, byte[]? SubjectKeyId, string KeyAlgorithm, byte[]? KeyAlgorithmParams, byte[] EncryptedKey);

    /// <summary>The decrypted content, or null when none of <paramref name="certificates"/> is a recipient.</summary>
    public static byte[]? TryDecrypt(byte[] der, IEnumerable<X509Certificate2> certificates)
    {
        var (recipients, contentAlgorithm, contentParams, encryptedContent) = Parse(der);
        foreach (var cert in certificates)
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa == null)
                continue;
            foreach (var r in recipients.Where(r => Matches(r, cert)))
            {
                byte[] key;
                try
                {
                    key = rsa.Decrypt(r.EncryptedKey, KeyPadding(r));
                }
                catch (CryptographicException)
                {
                    continue;
                }
                return DecryptContent(contentAlgorithm, contentParams, key, encryptedContent);
            }
        }
        return null;
    }

    private static (List<Recipient>, string, byte[]?, byte[]) Parse(byte[] der)
    {
        var outer = new AsnReader(der, AsnEncodingRules.BER);
        var contentInfo = outer.ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != EnvelopedDataOid)
            throw new CryptographicException("Not CMS EnvelopedData.");
        var explicit0 = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        var env = explicit0.ReadSequence();
        env.ReadInteger(); // version
        if (env.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            env.ReadEncodedValue(); // originatorInfo
        var recipients = new List<Recipient>();
        var set = env.ReadSetOf();
        while (set.HasData)
        {
            var tag = set.PeekTag();
            if (tag.TagClass != TagClass.Universal || tag.TagValue != (int)UniversalTagNumber.Sequence)
            {
                set.ReadEncodedValue(); // key agreement / KEK / password recipients: not used by PDF writers
                continue;
            }
            var ktri = set.ReadSequence();
            ktri.ReadInteger();
            byte[]? issuer = null, serial = null, ski = null;
            if (ktri.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                ski = ktri.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
            }
            else
            {
                var ias = ktri.ReadSequence();
                issuer = ias.ReadEncodedValue().ToArray();
                serial = ias.ReadIntegerBytes().ToArray();
            }
            var alg = ktri.ReadSequence();
            string algOid = alg.ReadObjectIdentifier();
            byte[]? algParams = alg.HasData ? alg.ReadEncodedValue().ToArray() : null;
            byte[] encryptedKey = ktri.ReadOctetString();
            recipients.Add(new Recipient(issuer, serial, ski, algOid, algParams, encryptedKey));
        }
        var eci = env.ReadSequence();
        eci.ReadObjectIdentifier(); // content type
        var cea = eci.ReadSequence();
        string ceaOid = cea.ReadObjectIdentifier();
        byte[]? ceaParams = cea.HasData ? cea.ReadEncodedValue().ToArray() : null;
        byte[] content = eci.HasData ? eci.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0)) : Array.Empty<byte>();
        return (recipients, ceaOid, ceaParams, content);
    }

    private static byte[] StripLeadingZeros(byte[] b)
    {
        int i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..];
    }

    private static bool Matches(Recipient r, X509Certificate2 cert)
    {
        if (r.SubjectKeyId != null)
        {
            var ext = cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
            return ext?.SubjectKeyIdentifier is string id && Convert.FromHexString(id).AsSpan().SequenceEqual(r.SubjectKeyId);
        }
        return r.Issuer != null && r.Serial != null &&
               cert.IssuerName.RawData.AsSpan().SequenceEqual(r.Issuer) &&
               StripLeadingZeros(cert.SerialNumberBytes.ToArray()).AsSpan().SequenceEqual(StripLeadingZeros(r.Serial));
    }

    private static RSAEncryptionPadding KeyPadding(Recipient r)
    {
        if (r.KeyAlgorithm == RsaOid)
            return RSAEncryptionPadding.Pkcs1;
        if (r.KeyAlgorithm != RsaOaepOid)
            throw new CryptographicException($"Key transport algorithm {r.KeyAlgorithm} is not supported.");
        // RSAES-OAEP-params: hashAlgorithm [0] (default SHA-1); the MGF hash follows it in practice.
        var hash = HashAlgorithmName.SHA1;
        if (r.KeyAlgorithmParams is { Length: > 2 } p)
        {
            var seq = new AsnReader(p, AsnEncodingRules.DER).ReadSequence();
            if (seq.HasData && seq.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                var h = seq.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
                hash = h.ReadObjectIdentifier() switch
                {
                    "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
                    "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                    "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                    _ => HashAlgorithmName.SHA1,
                };
            }
        }
        return RSAEncryptionPadding.CreateOaep(hash);
    }

    private static byte[] OctetParam(byte[]? param) =>
        param != null ? new AsnReader(param, AsnEncodingRules.BER).ReadOctetString() : throw new CryptographicException("Missing IV.");

    private static byte[] DecryptContent(string oid, byte[]? param, byte[] key, byte[] data)
    {
        switch (oid)
        {
            case "2.16.840.1.101.3.4.1.2": // aes128-CBC
            case "2.16.840.1.101.3.4.1.22": // aes192-CBC
            case "2.16.840.1.101.3.4.1.42": // aes256-CBC
            {
                using var aes = Aes.Create();
                aes.Key = key;
                return aes.DecryptCbc(data, OctetParam(param), PaddingMode.PKCS7);
            }
            case "1.2.840.113549.3.7": // des-ede3-cbc
            {
                using var des = TripleDES.Create();
                des.Key = key;
                return des.DecryptCbc(data, OctetParam(param), PaddingMode.PKCS7);
            }
            case "1.2.840.113549.3.2": // rc2-cbc: RC2CBCParameter ::= SEQUENCE { version INTEGER OPTIONAL, iv OCTET STRING } (or the IV alone)
            {
                byte[] iv;
                int effectiveBits = 32;
                var r = new AsnReader(param ?? throw new CryptographicException("Missing RC2 parameters."), AsnEncodingRules.BER);
                if (r.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                {
                    var seq = r.ReadSequence();
                    if (seq.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
                    {
                        int version = (int)seq.ReadInteger();
                        effectiveBits = version switch { 160 => 40, 120 => 64, 58 => 128, >= 256 => version, _ => 32 };
                    }
                    iv = seq.ReadOctetString();
                }
                else
                {
                    iv = r.ReadOctetString();
                }
#pragma warning disable SYSLIB0022 // RC2 is required to read legacy envelopes
                using var rc2 = RC2.Create();
                rc2.Key = key;
                if (effectiveBits != key.Length * 8)
                    rc2.EffectiveKeySize = effectiveBits;
                return rc2.DecryptCbc(data, iv, PaddingMode.PKCS7);
#pragma warning restore SYSLIB0022
            }
            case "1.2.840.113549.3.4": // rc4 (early Acrobat envelopes)
                return PdfSecurityHandler.Rc4(key, data);
            default:
                throw new CryptographicException($"Content encryption algorithm {oid} is not supported.");
        }
    }
}
