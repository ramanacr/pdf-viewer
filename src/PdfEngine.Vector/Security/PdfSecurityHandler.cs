using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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
/// What every security handler shares once it has a file key (ISO 32000-2 7.6): the crypt filters
/// (/CF, /StmF, /StrF, per-stream /Crypt), /EncryptMetadata, per-object keys (Algorithm 1) and
/// RC4 / AES-CBC decryption of strings and streams. Subclasses authenticate and derive the key.
/// </summary>
public abstract class PdfSecurityHandler
{
    private readonly Dictionary<string, PdfCryptMethod> _cryptFilters = new(StringComparer.Ordinal);
    private readonly PdfCryptMethod _streamMethod, _stringMethod;

    /// <summary>/V of the encryption dictionary.</summary>
    protected int V { get; }

    /// <summary>File key length in bytes: 5 for V1, /Length for V2 (default 40 bits), 16 for V4 unless stated, 32 for V5.</summary>
    protected int KeyLength { get; }

    protected bool EncryptMetadata { get; }

    /// <summary>The file key; set by <see cref="SetFileKey"/> after authentication.</summary>
    protected byte[]? FileKey { get; private set; }

    /// <summary>The permission flags in effect (Table 22): /P, or the recipient's permissions for public-key encryption.</summary>
    public int Permissions { get; protected set; }

    /// <summary>
    /// True when access is unrestricted: the Standard owner password, or a public-key recipient
    /// granted every permission. Readers then ignore /P.
    /// </summary>
    public bool IsOwner { get; protected set; }

    /// <summary>Security handler revision (/R), or the public-key sub-filter's equivalent.</summary>
    public int Revision { get; protected set; }

    protected PdfSecurityHandler(PdfDictionary encrypt, Func<PdfObject?, PdfObject?> resolve)
    {
        V = (int)(encrypt.GetInteger("V") ?? 0);
        EncryptMetadata = resolve(encrypt["EncryptMetadata"]) is not PdfBoolean { Value: false };
        int lengthBits = (int)(encrypt.GetInteger("Length") ?? (V == 4 ? 128 : 40));
        KeyLength = V switch { 1 => 5, 5 => 32, _ => Math.Clamp(lengthBits / 8, 5, 16) };
        if (V is < 1 or > 5 || V == 3)
            throw new PdfEncryptedDocumentException($"Encryption algorithm V{V} is not supported.");

        if (V >= 4)
        {
            if (resolve(encrypt["CF"]) is PdfDictionary cf)
            {
                foreach (var (name, value) in cf.Entries)
                {
                    if (resolve(value) is not PdfDictionary f) continue;
                    _cryptFilters[name] = f.GetName("CFM") switch
                    {
                        "V2" => PdfCryptMethod.Rc4,
                        "AESV2" => PdfCryptMethod.AesV2,
                        "AESV3" => PdfCryptMethod.AesV3,
                        "None" or null => PdfCryptMethod.None,
                        var other => throw new PdfEncryptedDocumentException($"Crypt filter method /{other} is not supported."),
                    }; // keys always derive from the file key (Algorithm 1)
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

    protected static byte[] Bytes(PdfObject? o) => o is PdfString s ? s.RawBytes.ToArray() : Array.Empty<byte>();

    protected void SetFileKey(byte[] key) => FileKey = key;

    /// <summary>Creates the handler named by /Filter (Standard or Adobe.PubSec).</summary>
    public static PdfSecurityHandler Create(PdfDictionary encrypt, PdfArray? id, Func<PdfObject?, PdfObject?> resolve)
    {
        return encrypt.GetName("Filter") switch
        {
            "Standard" => PdfStandardSecurityHandler.Create(encrypt, id, resolve),
            "Adobe.PubSec" => new PdfPublicKeySecurityHandler(encrypt, resolve),
            var other => throw new PdfEncryptedDocumentException($"Security handler /{other} is not supported."),
        };
    }

    // ------------------------------------------------------------------ decryption

    /// <summary>
    /// Decrypts every string of an indirect object, and a stream's data, with the object's key.
    /// Cross-reference streams, and the metadata stream when /EncryptMetadata is false, are left as is.
    /// </summary>
    public PdfObject DecryptObject(PdfObject obj, int objectNumber, int generation)
    {
        if (FileKey == null)
            throw new InvalidOperationException("The security handler is not authenticated.");
        switch (obj)
        {
            case PdfStream stream:
            {
                var dict = (PdfDictionary)DecryptStrings(stream.Dictionary, objectNumber, generation);
                string? type = dict.GetName("Type");
                if (type == "XRef" || (type == "Metadata" && !EncryptMetadata))
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

    /// <summary>Algorithm 1 (RC4, AES-128) or the file key itself (AES-256).</summary>
    private byte[] ObjectKey(PdfCryptMethod method, int num, int gen)
    {
        var fileKey = FileKey!;
        if (method == PdfCryptMethod.AesV3)
            return fileKey;
        var input = new byte[fileKey.Length + 5 + (method == PdfCryptMethod.AesV2 ? 4 : 0)];
        Array.Copy(fileKey, input, fileKey.Length);
        int p = fileKey.Length;
        input[p++] = (byte)num; input[p++] = (byte)(num >> 8); input[p++] = (byte)(num >> 16);
        input[p++] = (byte)gen; input[p++] = (byte)(gen >> 8);
        if (method == PdfCryptMethod.AesV2) { input[p++] = 0x73; input[p++] = 0x41; input[p++] = 0x6C; input[p] = 0x54; } // "sAlT"
        byte[] hash = MD5.HashData(input);
        return hash[..Math.Min(fileKey.Length + 5, 16)];
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
