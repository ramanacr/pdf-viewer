using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Security;

/// <summary>How one class of data (strings, streams) is encrypted (ISO 32000-2 7.6.5, Table 25 /CFM).</summary>
public enum PdfCryptMethod
{
    None,
    Rc4,
    AesV2,
    AesV3,
}

/// <summary>
/// The Standard security handler (ISO 32000-2 7.6.4): revisions 2–4 (RC4 40–128 bit and AES-128 via
/// crypt filters) and 5–6 (AES-256). Authenticates the user or owner password, derives the file
/// key, and decrypts strings and streams per object (Algorithm 1), honouring /StmF, /StrF, named
/// stream crypt filters, /EncryptMetadata and the exemption of cross-reference streams.
/// </summary>
public sealed class PdfStandardSecurityHandler
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private readonly int _v, _r, _keyLength;
    private readonly byte[] _o, _u, _oe, _ue, _id0;
    private readonly int _p;
    private readonly bool _encryptMetadata;
    private readonly Dictionary<string, PdfCryptMethod> _cryptFilters = new(StringComparer.Ordinal);
    private readonly PdfCryptMethod _streamMethod, _stringMethod;
    private byte[]? _fileKey;

    /// <summary>The /P permission flags (Table 22). Bit 3 print, 4 modify, 5 copy, 12 high-quality print.</summary>
    public int Permissions => _p;

    /// <summary>True after <see cref="Authenticate"/> succeeded with the owner password.</summary>
    public bool IsOwner { get; private set; }

    public int Revision => _r;

    private PdfStandardSecurityHandler(PdfDictionary encrypt, byte[] id0, Func<PdfObject?, PdfObject?> resolve)
    {
        if (encrypt.GetName("Filter") != "Standard")
            throw new PdfEncryptedDocumentException($"Security handler /{encrypt.GetName("Filter")} is not supported.");
        _v = (int)(encrypt.GetInteger("V") ?? 0);
        _r = (int)(encrypt.GetInteger("R") ?? 0);
        _p = (int)(encrypt.GetInteger("P") ?? 0);
        _o = Bytes(resolve(encrypt["O"]));
        _u = Bytes(resolve(encrypt["U"]));
        _oe = Bytes(resolve(encrypt["OE"]));
        _ue = Bytes(resolve(encrypt["UE"]));
        _id0 = id0;
        _encryptMetadata = resolve(encrypt["EncryptMetadata"]) is not PdfBoolean { Value: false };
        // File key length (7.6.4.3): 40 bits for V1; /Length (default 40) for V2; 128 bits for V4
        // unless stated; 256 bits for V5.
        int lengthBits = (int)(encrypt.GetInteger("Length") ?? (_v == 4 ? 128 : 40));
        _keyLength = _v switch { 1 => 5, 5 => 32, _ => Math.Clamp(lengthBits / 8, 5, 16) };

        if (_r is < 2 or > 6 || _v is < 1 or > 5 || _v == 3)
            throw new PdfEncryptedDocumentException($"Standard security handler V{_v} R{_r} is not supported.");
        if (_o.Length < 32 || _u.Length < 32 || (_r >= 5 && (_o.Length < 48 || _u.Length < 48 || _oe.Length < 32 || _ue.Length < 32)))
            throw new PdfEncryptedDocumentException("Encryption dictionary has malformed /O or /U entries.");

        if (_v >= 4)
        {
            if (resolve(encrypt["CF"]) is PdfDictionary cf)
            {
                foreach (var (name, value) in cf.Entries)
                {
                    if (resolve(value) is not PdfDictionary f) continue;
                    var method = f.GetName("CFM") switch
                    {
                        "V2" => PdfCryptMethod.Rc4,
                        "AESV2" => PdfCryptMethod.AesV2,
                        "AESV3" => PdfCryptMethod.AesV3,
                        "None" or null => PdfCryptMethod.None,
                        var other => throw new PdfEncryptedDocumentException($"Crypt filter method /{other} is not supported."),
                    };
                    _cryptFilters[name] = method; // keys always derive from the file key (Algorithm 1)
                }
            }
            _streamMethod = Method(encrypt.GetName("StmF") ?? "Identity");
            _stringMethod = Method(encrypt.GetName("StrF") ?? "Identity");
        }
        else
        {
            _streamMethod = _stringMethod = PdfCryptMethod.Rc4;
        }
    }

    private PdfCryptMethod Method(string filterName) =>
        filterName == "Identity" ? PdfCryptMethod.None
        : _cryptFilters.TryGetValue(filterName, out var f) ? f
        : throw new PdfEncryptedDocumentException($"Crypt filter /{filterName} is not defined.");

    private static byte[] Bytes(PdfObject? o) => o is PdfString s ? s.RawBytes.ToArray() : Array.Empty<byte>();

    /// <summary>Creates the handler for a document's /Encrypt dictionary (the first /ID string feeds key derivation).</summary>
    public static PdfStandardSecurityHandler Create(PdfDictionary encrypt, PdfArray? id, Func<PdfObject?, PdfObject?> resolve)
    {
        byte[] id0 = id != null && id.Count > 0 ? Bytes(resolve(id[0])) : Array.Empty<byte>();
        return new PdfStandardSecurityHandler(encrypt, id0, resolve);
    }

    // ------------------------------------------------------------------ authentication

    /// <summary>Tries <paramref name="password"/> as the user, then the owner password. True when either opens the document.</summary>
    public bool Authenticate(string? password)
    {
        password ??= string.Empty;
        if (_r >= 5)
        {
            byte[] pw = Utf8Password(password);
            if (TryUserR6(pw) is { } userKey) { _fileKey = userKey; IsOwner = false; return true; }
            if (TryOwnerR6(pw) is { } ownerKey) { _fileKey = ownerKey; IsOwner = true; return true; }
            return false;
        }
        byte[] latin = Latin1Password(password);
        if (TryUser(latin) is { } key) { _fileKey = key; IsOwner = false; return true; }
        if (TryOwner(latin) is { } okey) { _fileKey = okey; IsOwner = true; return true; }
        return false;
    }

    private static byte[] Latin1Password(string password)
    {
        var bytes = new List<byte>(32);
        foreach (char c in password)
        {
            if (bytes.Count == 32) break;
            bytes.Add(c <= 0xFF ? (byte)c : (byte)'?');
        }
        return bytes.ToArray();
    }

    private static byte[] Utf8Password(string password)
    {
        // SASLprep (RFC 4013) normalisation: NFKC covers the mapping that matters in practice.
        byte[] utf8 = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return utf8.Length > 127 ? utf8[..127] : utf8;
    }

    private static byte[] Pad(byte[] password)
    {
        var padded = new byte[32];
        int n = Math.Min(32, password.Length);
        Array.Copy(password, padded, n);
        Array.Copy(Padding, 0, padded, n, 32 - n);
        return padded;
    }

    /// <summary>Algorithm 2: the file key from a (user) password.</summary>
    private byte[] ComputeKey(byte[] password)
    {
        using var md5 = MD5.Create();
        var input = new List<byte>(128);
        input.AddRange(Pad(password));
        input.AddRange(_o.AsSpan(0, 32).ToArray());
        input.AddRange(BitConverter.GetBytes(_p)); // little-endian low-order byte first
        input.AddRange(_id0);
        if (_r >= 4 && !_encryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] hash = md5.ComputeHash(input.ToArray());
        int n = _r == 2 ? 5 : _keyLength;
        if (_r >= 3)
            for (int i = 0; i < 50; i++)
                hash = md5.ComputeHash(hash, 0, n);
        return hash[..n];
    }

    /// <summary>Algorithms 4/5 and 6: the key when <paramref name="password"/> is the user password.</summary>
    private byte[]? TryUser(byte[] password)
    {
        byte[] key = ComputeKey(password);
        if (_r == 2)
            return Rc4(key, Padding).AsSpan().SequenceEqual(_u.AsSpan(0, 32)) ? key : null;
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Padding.Concat(_id0).ToArray());
        byte[] x = Rc4(key, hash);
        for (int i = 1; i <= 19; i++)
            x = Rc4(XorKey(key, i), x);
        return x.AsSpan(0, 16).SequenceEqual(_u.AsSpan(0, 16)) ? key : null;
    }

    /// <summary>Algorithm 7: recover the user password from /O with the owner password, then authenticate as user.</summary>
    private byte[]? TryOwner(byte[] password)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Pad(password));
        int n = _r == 2 ? 5 : _keyLength;
        if (_r >= 3)
            for (int i = 0; i < 50; i++)
                hash = md5.ComputeHash(hash);
        byte[] key = hash[..n];
        byte[] user = _o.AsSpan(0, 32).ToArray();
        if (_r == 2)
        {
            user = Rc4(key, user);
        }
        else
        {
            for (int i = 19; i >= 0; i--)
                user = Rc4(XorKey(key, i), user);
        }
        return TryUser(user);
    }

    private static byte[] XorKey(byte[] key, int i)
    {
        var k = new byte[key.Length];
        for (int j = 0; j < key.Length; j++) k[j] = (byte)(key[j] ^ i);
        return k;
    }

    /// <summary>Algorithm 2.A (user) for R5/R6.</summary>
    private byte[]? TryUserR6(byte[] pw)
    {
        byte[] validationSalt = _u[32..40], keySalt = _u[40..48];
        if (!Hash(pw, validationSalt, Array.Empty<byte>()).AsSpan().SequenceEqual(_u.AsSpan(0, 32)))
            return null;
        return AesCbcNoPadding(Hash(pw, keySalt, Array.Empty<byte>()), new byte[16], _ue[..32], decrypt: true);
    }

    /// <summary>Algorithm 2.A (owner) for R5/R6: the owner hash also covers the 48-byte /U.</summary>
    private byte[]? TryOwnerR6(byte[] pw)
    {
        byte[] validationSalt = _o[32..40], keySalt = _o[40..48], u48 = _u[..48];
        if (!Hash(pw, validationSalt, u48).AsSpan().SequenceEqual(_o.AsSpan(0, 32)))
            return null;
        return AesCbcNoPadding(Hash(pw, keySalt, u48), new byte[16], _oe[..32], decrypt: true);
    }

    /// <summary>R5: SHA-256; R6: Algorithm 2.B (iterated SHA-256/384/512 with AES-128-CBC).</summary>
    private byte[] Hash(byte[] pw, byte[] salt, byte[] udata)
    {
        byte[] k = SHA256.HashData(pw.Concat(salt).Concat(udata).ToArray());
        if (_r == 5)
            return k;
        int round = 0;
        while (true)
        {
            var k1Unit = pw.Concat(k).Concat(udata).ToArray();
            var k1 = new byte[k1Unit.Length * 64];
            for (int i = 0; i < 64; i++) Buffer.BlockCopy(k1Unit, 0, k1, i * k1Unit.Length, k1Unit.Length);
            byte[] e = AesCbcNoPadding(k[..16], k[16..32], k1, decrypt: false);
            int mod = 0;
            for (int i = 0; i < 16; i++) mod += e[i];
            k = (mod % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            round++;
            if (round >= 64 && e[^1] <= round - 32)
                break;
        }
        return k[..32];
    }

    // ------------------------------------------------------------------ decryption

    /// <summary>
    /// Decrypts every string of an indirect object, and a stream's data, with the object's key.
    /// Cross-reference streams, and the metadata stream when /EncryptMetadata is false, are left as is.
    /// </summary>
    public PdfObject DecryptObject(PdfObject obj, int objectNumber, int generation)
    {
        if (_fileKey == null)
            throw new InvalidOperationException("The security handler is not authenticated.");
        switch (obj)
        {
            case PdfStream stream:
            {
                var dict = (PdfDictionary)DecryptStrings(stream.Dictionary, objectNumber, generation);
                string? type = dict.GetName("Type");
                if (type == "XRef" || (type == "Metadata" && !_encryptMetadata))
                    return stream with { Dictionary = dict };
                var method = StreamMethod(dict);
                if (method == PdfCryptMethod.None)
                    return stream with { Dictionary = dict, Decrypted = true };
                byte[] plain = Decrypt(stream.GetRawBytes().ToArray(), method, objectNumber, generation);
                return stream with { Dictionary = dict, CachedRawBytes = plain, Decrypted = true };
            }
            default:
                return DecryptStrings(obj, objectNumber, generation);
        }
    }

    /// <summary>The stream's own /Crypt filter (first in /Filter) overrides /StmF (7.4.10).</summary>
    private PdfCryptMethod StreamMethod(PdfDictionary dict)
    {
        var filter = dict["Filter"];
        string? first = filter is PdfName n ? n.Value : filter is PdfArray a && a.Count > 0 && a[0] is PdfName n0 ? n0.Value : null;
        if (first == "Crypt")
        {
            var parms = dict["DecodeParms"] is PdfArray pa && pa.Count > 0 ? pa[0] as PdfDictionary : dict["DecodeParms"] as PdfDictionary;
            string name = parms?.GetName("Name") ?? "Identity";
            if (name == "Identity") return PdfCryptMethod.None;
            return _cryptFilters.TryGetValue(name, out var f) ? f : throw new PdfEncryptedDocumentException($"Crypt filter /{name} is not defined.");
        }
        return _streamMethod;
    }

    private PdfObject DecryptStrings(PdfObject obj, int num, int gen)
    {
        switch (obj)
        {
            case PdfString s when _stringMethod != PdfCryptMethod.None:
                return s with { RawBytes = Decrypt(s.RawBytes.ToArray(), _stringMethod, num, gen) };
            case PdfArray a:
            {
                var items = new PdfObject[a.Count];
                bool changed = false;
                for (int i = 0; i < a.Count; i++) { items[i] = DecryptStrings(a[i], num, gen); changed |= !ReferenceEquals(items[i], a[i]); }
                return changed ? new PdfArray(items) : a;
            }
            case PdfDictionary d:
            {
                Dictionary<string, PdfObject>? copy = null;
                foreach (var (k, v) in d.Entries)
                {
                    var dv = DecryptStrings(v, num, gen);
                    if (!ReferenceEquals(dv, v))
                    {
                        copy ??= new Dictionary<string, PdfObject>(d.Entries);
                        copy[k] = dv;
                    }
                }
                return copy != null ? new PdfDictionary(copy) : d;
            }
            default:
                return obj;
        }
    }

    private byte[] Decrypt(byte[] data, PdfCryptMethod method, int num, int gen)
    {
        if (method == PdfCryptMethod.None || data.Length == 0)
            return data;
        byte[] key = ObjectKey(method, num, gen);
        if (method == PdfCryptMethod.Rc4)
            return Rc4(key, data);
        // AES: 16-byte IV prefix, CBC, PKCS#5 padding (7.6.3.2). Lenient with bad padding and
        // data that is not a whole number of blocks, as other readers are.
        if (data.Length < 16)
            return Array.Empty<byte>();
        byte[] iv = data[..16];
        int body = (data.Length - 16) / 16 * 16;
        if (body == 0)
            return Array.Empty<byte>();
        byte[] plain = AesCbcNoPadding(key, iv, data.AsSpan(16, body).ToArray(), decrypt: true);
        int pad = plain[^1];
        if (pad is >= 1 and <= 16 && pad <= plain.Length)
        {
            bool valid = true;
            for (int i = plain.Length - pad; i < plain.Length && valid; i++) valid = plain[i] == pad;
            if (valid) return plain[..^pad];
        }
        return plain;
    }

    /// <summary>Algorithm 1 (R ≤ 4) or the file key itself (AES-256).</summary>
    private byte[] ObjectKey(PdfCryptMethod method, int num, int gen)
    {
        if (method == PdfCryptMethod.AesV3)
            return _fileKey!;
        var input = new byte[_fileKey!.Length + 5 + (method == PdfCryptMethod.AesV2 ? 4 : 0)];
        Array.Copy(_fileKey, input, _fileKey.Length);
        int p = _fileKey.Length;
        input[p++] = (byte)num; input[p++] = (byte)(num >> 8); input[p++] = (byte)(num >> 16);
        input[p++] = (byte)gen; input[p++] = (byte)(gen >> 8);
        if (method == PdfCryptMethod.AesV2) { input[p++] = 0x73; input[p++] = 0x41; input[p++] = 0x6C; input[p] = 0x54; } // "sAlT"
        byte[] hash = MD5.HashData(input);
        return hash[..Math.Min(_fileKey.Length + 5, 16)];
    }

    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }

    internal static byte[] AesCbcNoPadding(byte[] key, byte[] iv, byte[] data, bool decrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return decrypt
            ? aes.DecryptCbc(data, iv, PaddingMode.None)
            : aes.EncryptCbc(data, iv, PaddingMode.None);
    }
}
