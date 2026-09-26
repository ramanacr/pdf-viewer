using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Security;

/// <summary>
/// The Standard security handler (ISO 32000-2 7.6.4): revisions 2–4 (RC4 40–128 bit and AES-128 via
/// crypt filters) and 5–6 (AES-256). Authenticates the user or owner password, derives the file
/// key, and decrypts strings and streams per object (Algorithm 1), honouring /StmF, /StrF, named
/// stream crypt filters, /EncryptMetadata and the exemption of cross-reference streams.
/// </summary>
public sealed class PdfStandardSecurityHandler : PdfSecurityHandler
{
    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private readonly int _r, _keyLength, _p;
    private readonly byte[] _o, _u, _oe, _ue, _id0;
    private readonly bool _encryptMetadata;

    private PdfStandardSecurityHandler(PdfDictionary encrypt, byte[] id0, Func<PdfObject?, PdfObject?> resolve)
        : base(encrypt, resolve)
    {
        _r = (int)(encrypt.GetInteger("R") ?? 0);
        _p = (int)(encrypt.GetInteger("P") ?? 0);
        Revision = _r;
        Permissions = _p;
        _o = Bytes(resolve(encrypt["O"]));
        _u = Bytes(resolve(encrypt["U"]));
        _oe = Bytes(resolve(encrypt["OE"]));
        _ue = Bytes(resolve(encrypt["UE"]));
        _id0 = id0;
        _encryptMetadata = EncryptMetadata;
        _keyLength = KeyLength;
        if (_r is < 2 or > 6)
            throw new PdfEncryptedDocumentException($"Standard security handler R{_r} is not supported.");
        if (_o.Length < 32 || _u.Length < 32 || (_r >= 5 && (_o.Length < 48 || _u.Length < 48 || _oe.Length < 32 || _ue.Length < 32)))
            throw new PdfEncryptedDocumentException("Encryption dictionary has malformed /O or /U entries.");

    }

    /// <summary>Creates the handler for a document's /Encrypt dictionary (the first /ID string feeds key derivation).</summary>
    public static new PdfStandardSecurityHandler Create(PdfDictionary encrypt, PdfArray? id, Func<PdfObject?, PdfObject?> resolve)
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
            if (TryUserR6(pw) is { } userKey) { SetFileKey(userKey); IsOwner = false; return true; }
            if (TryOwnerR6(pw) is { } ownerKey) { SetFileKey(ownerKey); IsOwner = true; return true; }
            return false;
        }
        byte[] latin = Latin1Password(password);
        if (TryUser(latin) is { } key) { SetFileKey(key); IsOwner = false; return true; }
        if (TryOwner(latin) is { } okey) { SetFileKey(okey); IsOwner = true; return true; }
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
}
