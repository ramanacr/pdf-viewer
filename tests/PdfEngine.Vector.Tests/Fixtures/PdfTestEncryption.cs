using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// Encrypts <see cref="VectorPdfBuilder"/> output with the Standard security handler (ISO 32000-2
/// 7.6.4, writer side: Algorithms 1, 2, 3, 5, 8–10). Written independently of the decoder; PDFium
/// opening the result with the same passwords is the oracle.
/// </summary>
public enum TestEncryptionKind { Rc4_40_R2, Rc4_128_R3, Rc4_128_R4, Aes128_R4, Aes256_R5, Aes256_R6, PubSecS4Rc4, PubSecS5Aes128, PubSecS5Aes256 }

internal sealed class PdfTestEncryption
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    public TestEncryptionKind Scheme { get; }
    public bool EncryptMetadata { get; }
    public byte[] Id { get; } = Enumerable.Range(0, 16).Select(i => (byte)(i * 13 + 7)).ToArray();
    public int P { get; } = unchecked((int)0xFFFFF0C4); // print + copy + modify bits clear/set as a typical writer

    private bool IsPublicKey => Scheme is TestEncryptionKind.PubSecS4Rc4 or TestEncryptionKind.PubSecS5Aes128 or TestEncryptionKind.PubSecS5Aes256;

    private readonly byte[] _o = Array.Empty<byte>(), _u = Array.Empty<byte>(), _oe = Array.Empty<byte>(), _ue = Array.Empty<byte>(), _perms = Array.Empty<byte>();
    private readonly byte[] _key;
    private readonly int _r, _n;
    private readonly List<byte[]> _recipients = new();

    /// <summary>
    /// Public-key encryption (7.6.5): one CMS EnvelopedData per certificate, each carrying the same
    /// 20-byte seed and <paramref name="permissions"/>; the file key is SHA-1 (SHA-256 for AES-256)
    /// of the seed, all envelopes and — without metadata encryption — four 0xFF bytes.
    /// </summary>
    public PdfTestEncryption(TestEncryptionKind scheme, IReadOnlyList<X509Certificate2> recipients, int permissions,
        bool subjectKeyId = false, bool oaep = false, bool encryptMetadata = true)
    {
        Scheme = scheme;
        EncryptMetadata = encryptMetadata;
        P = permissions;
        (_r, _n) = scheme switch { TestEncryptionKind.PubSecS4Rc4 => (4, 16), TestEncryptionKind.PubSecS5Aes128 => (4, 16), _ => (5, 32) };
        byte[] seed = Enumerable.Range(0, 20).Select(i => (byte)(200 - i * 7)).ToArray();
        byte[] content = seed.Concat(new[] { (byte)(permissions >> 24), (byte)(permissions >> 16), (byte)(permissions >> 8), (byte)permissions }).ToArray();
        foreach (var cert in recipients)
            _recipients.Add(Envelope(cert, content, subjectKeyId, oaep));
        var input = seed.Concat(_recipients.SelectMany(r => r)).ToList();
        if (!encryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] hash = scheme == TestEncryptionKind.PubSecS5Aes256 ? SHA256.HashData(input.ToArray()) : SHA1.HashData(input.ToArray());
        _key = hash[.._n];
    }

    /// <summary>CMS EnvelopedData (RFC 5652): RSA key transport, AES-256-CBC content.</summary>
    private static byte[] Envelope(X509Certificate2 cert, byte[] content, bool subjectKeyId, bool oaep)
    {
        byte[] cek = Enumerable.Range(0, 32).Select(i => (byte)(i * 11 + 3)).ToArray();
        byte[] iv = Enumerable.Range(0, 16).Select(i => (byte)(i * 5 + 1)).ToArray();
        using var rsa = cert.GetRSAPublicKey()!;
        byte[] encryptedKey = rsa.Encrypt(cek, oaep ? RSAEncryptionPadding.OaepSHA256 : RSAEncryptionPadding.Pkcs1);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = cek;
        byte[] encryptedContent = aes.EncryptCbc(content, iv, PaddingMode.PKCS7);

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.7.3");
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (w.PushSequence())
            {
                w.WriteInteger(subjectKeyId ? 2 : 0);
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteInteger(subjectKeyId ? 2 : 0);
                    if (subjectKeyId)
                    {
                        var ski = cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier!;
                        w.WriteOctetString(Convert.FromHexString(ski), new Asn1Tag(TagClass.ContextSpecific, 0));
                    }
                    else
                    {
                        using (w.PushSequence())
                        {
                            w.WriteEncodedValue(cert.IssuerName.RawData);
                            w.WriteIntegerUnsigned(cert.SerialNumberBytes.Span);
                        }
                    }
                    using (w.PushSequence())
                    {
                        if (oaep)
                        {
                            w.WriteObjectIdentifier("1.2.840.113549.1.1.7");
                            using (w.PushSequence())
                            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                            using (w.PushSequence())
                                w.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                        }
                        else
                        {
                            w.WriteObjectIdentifier("1.2.840.113549.1.1.1");
                            w.WriteNull();
                        }
                    }
                    w.WriteOctetString(encryptedKey);
                }
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier("2.16.840.1.101.3.4.1.42");
                        w.WriteOctetString(iv);
                    }
                    w.WriteOctetString(encryptedContent, new Asn1Tag(TagClass.ContextSpecific, 0));
                }
            }
        }
        return w.Encode();
    }

    /// <summary>A self-signed RSA-2048 certificate with its private key and a subject key identifier.</summary>
    public static X509Certificate2 CreateCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public PdfTestEncryption(TestEncryptionKind scheme, string user, string owner, bool encryptMetadata = true, int? permissions = null)
    {
        if (permissions is int requested) P = requested;
        Scheme = scheme;
        EncryptMetadata = encryptMetadata;
        (_r, _n) = scheme switch
        {
            TestEncryptionKind.Rc4_40_R2 => (2, 5),
            TestEncryptionKind.Rc4_128_R3 => (3, 16),
            TestEncryptionKind.Rc4_128_R4 or TestEncryptionKind.Aes128_R4 => (4, 16),
            TestEncryptionKind.Aes256_R5 => (5, 32),
            _ => (6, 32),
        };
        if (_r >= 5)
        {
            byte[] up = Encoding.UTF8.GetBytes(user), op = Encoding.UTF8.GetBytes(owner);
            _key = Enumerable.Range(0, 32).Select(i => (byte)(255 - i * 3)).ToArray();
            byte[] uvs = { 1, 2, 3, 4, 5, 6, 7, 8 }, uks = { 9, 10, 11, 12, 13, 14, 15, 16 };
            byte[] ovs = { 17, 18, 19, 20, 21, 22, 23, 24 }, oks = { 25, 26, 27, 28, 29, 30, 31, 32 };
            _u = HashR(up, uvs, Array.Empty<byte>()).Concat(uvs).Concat(uks).ToArray();
            _ue = Aes(HashR(up, uks, Array.Empty<byte>()), new byte[16], _key, encrypt: true, CipherMode.CBC);
            _o = HashR(op, ovs, _u).Concat(ovs).Concat(oks).ToArray();
            _oe = Aes(HashR(op, oks, _u), new byte[16], _key, encrypt: true, CipherMode.CBC);
            var perms = new byte[16];
            BitConverter.GetBytes(P).CopyTo(perms, 0);
            perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
            perms[8] = (byte)(encryptMetadata ? 'T' : 'F');
            perms[9] = (byte)'a'; perms[10] = (byte)'d'; perms[11] = (byte)'b';
            _perms = Aes(_key, null, perms, encrypt: true, CipherMode.ECB);
            return;
        }
        // Algorithm 3: /O.
        byte[] ownerKey = MD5.HashData(Pad(Encoding.Latin1.GetBytes(owner.Length > 0 ? owner : user)));
        if (_r >= 3) for (int i = 0; i < 50; i++) ownerKey = MD5.HashData(ownerKey);
        ownerKey = ownerKey[.._n];
        _o = Rc4(ownerKey, Pad(Encoding.Latin1.GetBytes(user)));
        if (_r >= 3) for (int i = 1; i <= 19; i++) _o = Rc4(ownerKey.Select(b => (byte)(b ^ i)).ToArray(), _o);
        // Algorithm 2: the file key.
        var input = Pad(Encoding.Latin1.GetBytes(user)).Concat(_o).Concat(BitConverter.GetBytes(P)).Concat(Id).ToList();
        if (_r >= 4 && !encryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] k = MD5.HashData(input.ToArray());
        if (_r >= 3) for (int i = 0; i < 50; i++) k = MD5.HashData(k[.._n]);
        _key = k[.._n];
        // Algorithms 4 / 5: /U.
        if (_r == 2)
        {
            _u = Rc4(_key, Padding);
        }
        else
        {
            byte[] x = Rc4(_key, MD5.HashData(Padding.Concat(Id).ToArray()));
            for (int i = 1; i <= 19; i++) x = Rc4(_key.Select(b => (byte)(b ^ i)).ToArray(), x);
            _u = x.Concat(new byte[16]).ToArray();
        }
    }

    private static byte[] Pad(byte[] pw) => pw.Take(32).Concat(Padding).Take(32).ToArray();

    /// <summary>R5 (Acrobat 9 extension level 3): a single SHA-256; R6: Algorithm 2.B.</summary>
    private byte[] HashR(byte[] pw, byte[] salt, byte[] udata) =>
        _r == 5 ? SHA256.HashData(pw.Concat(salt).Concat(udata).ToArray()) : Hash6(pw, salt, udata);

    private static byte[] Hash6(byte[] pw, byte[] salt, byte[] udata)
    {
        byte[] k = SHA256.HashData(pw.Concat(salt).Concat(udata).ToArray());
        for (int round = 0; ; )
        {
            byte[] unit = pw.Concat(k).Concat(udata).ToArray();
            byte[] k1 = Enumerable.Repeat(unit, 64).SelectMany(x => x).ToArray();
            byte[] e = Aes(k[..16], k[16..32], k1, encrypt: true, CipherMode.CBC);
            int sum = e.Take(16).Sum(b => b);
            k = (sum % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            round++;
            if (round >= 64 && e[^1] <= round - 32) break;
        }
        return k[..32];
    }

    private static byte[] Aes(byte[] key, byte[]? iv, byte[] data, bool encrypt, CipherMode mode)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        if (mode == CipherMode.ECB)
            return encrypt ? aes.EncryptEcb(data, PaddingMode.None) : aes.DecryptEcb(data, PaddingMode.None);
        return encrypt ? aes.EncryptCbc(data, iv!, PaddingMode.None) : aes.DecryptCbc(data, iv!, PaddingMode.None);
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++) { j = (j + s[i] + key[i % key.Length]) & 255; (s[i], s[j]) = (s[j], s[i]); }
        var o = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255; j = (j + s[i]) & 255; (s[i], s[j]) = (s[j], s[i]);
            o[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
        }
        return o;
    }

    private bool Aes128 => Scheme is TestEncryptionKind.Aes128_R4 or TestEncryptionKind.PubSecS5Aes128;

    /// <summary>Algorithm 1 (or the file key for AES-256), then RC4 or AES-CBC with a deterministic IV and PKCS#7 padding.</summary>
    public byte[] EncryptData(byte[] plain, int num, int gen)
    {
        byte[] key;
        if (_r >= 5 && !(IsPublicKey && Scheme != TestEncryptionKind.PubSecS5Aes256))
        {
            key = _key;
        }
        else
        {
            var input = _key.Concat(new[] { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)gen, (byte)(gen >> 8) }).ToList();
            if (Aes128) input.AddRange("sAlT"u8.ToArray());
            key = MD5.HashData(input.ToArray())[..Math.Min(_n + 5, 16)];
        }
        if (!Aes128 && _r < 5)
            return Rc4(key, plain);
        byte[] iv = Enumerable.Range(0, 16).Select(i => (byte)(num * 31 + i)).ToArray();
        int pad = 16 - plain.Length % 16;
        byte[] padded = plain.Concat(Enumerable.Repeat((byte)pad, pad)).ToArray();
        return iv.Concat(Aes(key, iv, padded, encrypt: true, CipherMode.CBC)).ToArray();
    }

    public string EncryptDictionary()
    {
        static string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";
        string meta = EncryptMetadata ? "" : " /EncryptMetadata false";
        string recipients = "[" + string.Join(" ", _recipients.Select(Hex)) + "]";
        return Scheme switch
        {
            TestEncryptionKind.PubSecS4Rc4 => $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s4 /V 2 /Length 128 /Recipients {recipients}{meta} >>",
            TestEncryptionKind.PubSecS5Aes128 => $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /Length 128 /CF << /DefaultCryptFilter << /CFM /AESV2 /Length 16 /Recipients {recipients} >> >> /StmF /DefaultCryptFilter /StrF /DefaultCryptFilter{meta} >>",
            TestEncryptionKind.PubSecS5Aes256 => $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 5 /Length 256 /CF << /DefaultCryptFilter << /CFM /AESV3 /Length 32 /Recipients {recipients} >> >> /StmF /DefaultCryptFilter /StrF /DefaultCryptFilter{meta} >>",
            TestEncryptionKind.Rc4_40_R2 => $"<< /Filter /Standard /V 1 /R 2 /O {Hex(_o)} /U {Hex(_u)} /P {P} >>",
            TestEncryptionKind.Rc4_128_R3 => $"<< /Filter /Standard /V 2 /R 3 /Length 128 /O {Hex(_o)} /U {Hex(_u)} /P {P} >>",
            TestEncryptionKind.Rc4_128_R4 => $"<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /V2 /Length 16 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF /O {Hex(_o)} /U {Hex(_u)} /P {P}{meta} >>",
            TestEncryptionKind.Aes128_R4 => $"<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /AESV2 /Length 16 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF /O {Hex(_o)} /U {Hex(_u)} /P {P}{meta} >>",
            TestEncryptionKind.Aes256_R5 => $"<< /Filter /Standard /V 5 /R 5 /Length 256 /CF << /StdCF << /CFM /AESV3 /Length 32 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF /O {Hex(_o)} /U {Hex(_u)} /OE {Hex(_oe)} /UE {Hex(_ue)} /Perms {Hex(_perms)} /P {P}{meta} >>",
            _ => $"<< /Filter /Standard /V 5 /R 6 /Length 256 /CF << /StdCF << /CFM /AESV3 /Length 32 /AuthEvent /DocOpen >> >> /StmF /StdCF /StrF /StdCF /O {Hex(_o)} /U {Hex(_u)} /OE {Hex(_oe)} /UE {Hex(_ue)} /Perms {Hex(_perms)} /P {P}{meta} >>",
        };
    }

    /// <summary>Encrypts the strings of an object body and its stream data (Length rewritten).</summary>
    public byte[] EncryptObject(byte[] body, int num)
    {
        int streamAt = IndexOf(body, "\nstream\n"u8);
        byte[] dictPart = streamAt >= 0 ? body[..streamAt] : body;
        string text = Encoding.Latin1.GetString(dictPart);
        bool metadata = text.Contains("/Type /Metadata");
        string encrypted = EncryptStrings(text, num);
        if (streamAt < 0)
            return Encoding.Latin1.GetBytes(encrypted);
        int dataStart = streamAt + "\nstream\n".Length;
        int dataEnd = LastIndexOf(body, "\nendstream"u8);
        byte[] data = body[dataStart..dataEnd];
        if (!(metadata && !EncryptMetadata))
            data = EncryptData(data, num, 0);
        encrypted = Regex.Replace(encrypted, @"/Length \d+", $"/Length {data.Length}");
        return Encoding.Latin1.GetBytes(encrypted + "\nstream\n").Concat(data).Concat(Encoding.Latin1.GetBytes("\nendstream")).ToArray();
    }

    private string EncryptStrings(string text, int num)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                var bytes = new List<byte>();
                int depth = 1;
                for (i++; i < text.Length && depth > 0; i++)
                {
                    char d = text[i];
                    if (d == '\\' && i + 1 < text.Length)
                    {
                        char e = text[++i];
                        bytes.Add(e switch { 'n' => (byte)'\n', 'r' => (byte)'\r', 't' => (byte)'\t', 'b' => 8, 'f' => 12, _ => (byte)e });
                        continue;
                    }
                    if (d == '(') depth++;
                    if (d == ')' && --depth == 0) break;
                    bytes.Add((byte)d);
                }
                sb.Append('<').Append(Convert.ToHexString(EncryptData(bytes.ToArray(), num, 0))).Append('>');
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] != '<' && (i == 0 || text[i - 1] != '<'))
            {
                int end = text.IndexOf('>', i);
                string hex = new string(text[(i + 1)..end].Where(Uri.IsHexDigit).ToArray());
                if (hex.Length % 2 == 1) hex += "0";
                sb.Append('<').Append(Convert.ToHexString(EncryptData(Convert.FromHexString(hex), num, 0))).Append('>');
                i = end;
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] == '<')
            {
                sb.Append("<<");
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
    private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().LastIndexOf(pattern);
}
