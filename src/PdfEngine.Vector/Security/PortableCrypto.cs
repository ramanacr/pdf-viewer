using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;

namespace PdfEngine.Vector.Security;

/// <summary>
/// MD5 and AES-CBC for the Standard security handler. The platform's implementations are used
/// wherever they exist (Windows, Linux, macOS: unchanged behaviour). Browser WebAssembly builds
/// of .NET 9 have neither, so there the engine falls back to the managed implementations below,
/// which exist only so that encrypted documents can be read; they are checked against the
/// platform's on every test run. They are not constant-time and are not meant for anything else.
/// </summary>
internal static class PortableCrypto
{
    private static readonly Lazy<bool> SystemMd5 = new(() => Works(() => MD5.HashData(new byte[1])));
    private static readonly Lazy<bool> SystemAes = new(() => Works(() =>
    {
        using var aes = Aes.Create();
        aes.Key = new byte[16];
        aes.EncryptCbc(new byte[16], new byte[16], PaddingMode.None);
    }));

    // Per async flow, so that a test forcing the managed code does not change what other tests see.
    private static readonly AsyncLocal<bool> ForceManaged = new();

    private static bool Works(Action probe)
    {
        try
        {
            probe();
            return true;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException or TypeInitializationException)
        {
            return false;
        }
    }

    /// <summary>True when the managed fallback is in use for MD5 (for diagnostics and tests).</summary>
    internal static bool UsesManagedMd5 => ForceManaged.Value || !SystemMd5.Value;

    /// <summary>True when the managed fallback is in use for AES.</summary>
    internal static bool UsesManagedAes => ForceManaged.Value || !SystemAes.Value;

    /// <summary>Tests: use the managed implementations in the calling flow, as a browser does.</summary>
    internal static IDisposable UseManagedImplementations()
    {
        bool previous = ForceManaged.Value;
        ForceManaged.Value = true;
        return new Restore(() => ForceManaged.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public static byte[] Md5(ReadOnlySpan<byte> data) => UsesManagedMd5 ? ManagedMd5.Hash(data) : MD5.HashData(data);

    public static byte[] AesCbcNoPadding(byte[] key, byte[] iv, byte[] data, bool decrypt)
    {
        if (UsesManagedAes)
            return ManagedAes.Cbc(key, iv, data, decrypt);
        using var aes = Aes.Create();
        aes.Key = key;
        return decrypt
            ? aes.DecryptCbc(data, iv, PaddingMode.None)
            : aes.EncryptCbc(data, iv, PaddingMode.None);
    }
}

/// <summary>MD5 (RFC 1321).</summary>
internal static class ManagedMd5
{
    private static readonly uint[] K =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    private static readonly int[] Shift =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        // Padding: 0x80, zeros, then the bit length (little-endian), to a multiple of 64 bytes.
        int padded = (message.Length + 8) / 64 * 64 + 64;
        var buffer = new byte[padded];
        message.CopyTo(buffer);
        buffer[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(padded - 8), (ulong)message.Length * 8);

        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
        Span<uint> m = stackalloc uint[16];
        for (int offset = 0; offset < padded; offset += 64)
        {
            for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4 * i));
            uint a = a0, b = b0, c = c0, d = d0;
            for (int i = 0; i < 64; i++)
            {
                uint f;
                int g;
                if (i < 16) { f = (b & c) | (~b & d); g = i; }
                else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) & 15; }
                else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) & 15; }
                else { f = c ^ (b | ~d); g = (7 * i) & 15; }
                f += a + K[i] + m[g];
                a = d;
                d = c;
                c = b;
                b += BitOperations.RotateLeft(f, Shift[i]);
            }
            a0 += a; b0 += b; c0 += c; d0 += d;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), a0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d0);
        return digest;
    }
}

/// <summary>AES (FIPS 197) with 128-, 192- and 256-bit keys, CBC mode without padding, table-driven.</summary>
internal static class ManagedAes
{
    private static readonly byte[] S = new byte[256], Si = new byte[256];
    private static readonly uint[] Te0 = new uint[256], Te1 = new uint[256], Te2 = new uint[256], Te3 = new uint[256];
    private static readonly uint[] Td0 = new uint[256], Td1 = new uint[256], Td2 = new uint[256], Td3 = new uint[256];

    static ManagedAes()
    {
        // The S-box from GF(2^8) inverses (generator 3) and the affine transform.
        var exp = new byte[256];
        var log = new byte[256];
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = (byte)x;
            log[x] = (byte)i;
            x ^= Mul2(x);
        }
        for (int v = 0; v < 256; v++)
        {
            int inv = v == 0 ? 0 : exp[(255 - log[v]) % 255];
            int s = inv ^ Rotl8(inv, 1) ^ Rotl8(inv, 2) ^ Rotl8(inv, 3) ^ Rotl8(inv, 4) ^ 0x63;
            S[v] = (byte)s;
            Si[s] = (byte)v;
        }
        for (int v = 0; v < 256; v++)
        {
            uint s = S[v];
            uint e = (Mul(s, 2) << 24) | (s << 16) | (s << 8) | Mul(s, 3);
            Te0[v] = e;
            Te1[v] = BitOperations.RotateRight(e, 8);
            Te2[v] = BitOperations.RotateRight(e, 16);
            Te3[v] = BitOperations.RotateRight(e, 24);
            uint si = Si[v];
            uint d = (Mul(si, 14) << 24) | (Mul(si, 9) << 16) | (Mul(si, 13) << 8) | Mul(si, 11);
            Td0[v] = d;
            Td1[v] = BitOperations.RotateRight(d, 8);
            Td2[v] = BitOperations.RotateRight(d, 16);
            Td3[v] = BitOperations.RotateRight(d, 24);
        }
    }

    private static int Mul2(int v) => ((v << 1) ^ ((v & 0x80) != 0 ? 0x1b : 0)) & 0xFF;

    private static int Rotl8(int v, int n) => ((v << n) | (v >> (8 - n))) & 0xFF;

    private static uint Mul(uint v, int factor)
    {
        uint result = 0;
        int a = (int)v;
        for (; factor > 0; factor >>= 1)
        {
            if ((factor & 1) != 0) result ^= (uint)a;
            a = Mul2(a);
        }
        return result;
    }

    /// <summary>The encryption round keys (FIPS 197 5.2).</summary>
    private static uint[] ExpandKey(byte[] key, out int rounds)
    {
        if (key.Length is not (16 or 24 or 32)) throw new CryptographicException("AES keys are 16, 24 or 32 bytes.");
        int nk = key.Length / 4;
        rounds = nk + 6;
        var w = new uint[4 * (rounds + 1)];
        for (int i = 0; i < nk; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(4 * i));
        uint rcon = 1;
        for (int i = nk; i < w.Length; i++)
        {
            uint t = w[i - 1];
            if (i % nk == 0)
            {
                t = SubWord(BitOperations.RotateLeft(t, 8)) ^ (rcon << 24);
                rcon = (uint)Mul2((int)rcon);
            }
            else if (nk > 6 && i % nk == 4)
            {
                t = SubWord(t);
            }
            w[i] = w[i - nk] ^ t;
        }
        return w;
    }

    private static uint SubWord(uint w) =>
        ((uint)S[w >> 24] << 24) | ((uint)S[(w >> 16) & 0xFF] << 16) | ((uint)S[(w >> 8) & 0xFF] << 8) | S[w & 0xFF];

    /// <summary>The round keys of the equivalent inverse cipher (FIPS 197 5.3.5): reversed, InvMixColumns on the middle rounds.</summary>
    private static uint[] DecryptionKeys(uint[] ek, int rounds)
    {
        var dk = new uint[ek.Length];
        for (int r = 0; r <= rounds; r++)
            for (int j = 0; j < 4; j++)
                dk[4 * r + j] = ek[4 * (rounds - r) + j];
        for (int i = 4; i < 4 * rounds; i++)
        {
            uint w = dk[i];
            dk[i] = Td0[S[w >> 24]] ^ Td1[S[(w >> 16) & 0xFF]] ^ Td2[S[(w >> 8) & 0xFF]] ^ Td3[S[w & 0xFF]];
        }
        return dk;
    }

    public static byte[] Cbc(byte[] key, byte[] iv, byte[] data, bool decrypt)
    {
        if (iv.Length != 16) throw new CryptographicException("The AES IV is 16 bytes.");
        if (data.Length % 16 != 0) throw new CryptographicException("The data is not a whole number of AES blocks.");
        var ek = ExpandKey(key, out int rounds);
        var output = new byte[data.Length];
        Span<byte> chain = stackalloc byte[16];
        Span<byte> block = stackalloc byte[16];
        iv.CopyTo(chain);
        if (decrypt)
        {
            var dk = DecryptionKeys(ek, rounds);
            for (int o = 0; o < data.Length; o += 16)
            {
                DecryptBlock(dk, rounds, data.AsSpan(o, 16), block);
                for (int i = 0; i < 16; i++) output[o + i] = (byte)(block[i] ^ chain[i]);
                data.AsSpan(o, 16).CopyTo(chain);
            }
        }
        else
        {
            for (int o = 0; o < data.Length; o += 16)
            {
                for (int i = 0; i < 16; i++) block[i] = (byte)(data[o + i] ^ chain[i]);
                EncryptBlock(ek, rounds, block, output.AsSpan(o, 16));
                output.AsSpan(o, 16).CopyTo(chain);
            }
        }
        return output;
    }

    private static void EncryptBlock(uint[] rk, int rounds, ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint s0 = BinaryPrimitives.ReadUInt32BigEndian(input) ^ rk[0];
        uint s1 = BinaryPrimitives.ReadUInt32BigEndian(input[4..]) ^ rk[1];
        uint s2 = BinaryPrimitives.ReadUInt32BigEndian(input[8..]) ^ rk[2];
        uint s3 = BinaryPrimitives.ReadUInt32BigEndian(input[12..]) ^ rk[3];
        for (int r = 1; r < rounds; r++)
        {
            int k = 4 * r;
            uint t0 = Te0[s0 >> 24] ^ Te1[(s1 >> 16) & 0xFF] ^ Te2[(s2 >> 8) & 0xFF] ^ Te3[s3 & 0xFF] ^ rk[k];
            uint t1 = Te0[s1 >> 24] ^ Te1[(s2 >> 16) & 0xFF] ^ Te2[(s3 >> 8) & 0xFF] ^ Te3[s0 & 0xFF] ^ rk[k + 1];
            uint t2 = Te0[s2 >> 24] ^ Te1[(s3 >> 16) & 0xFF] ^ Te2[(s0 >> 8) & 0xFF] ^ Te3[s1 & 0xFF] ^ rk[k + 2];
            uint t3 = Te0[s3 >> 24] ^ Te1[(s0 >> 16) & 0xFF] ^ Te2[(s1 >> 8) & 0xFF] ^ Te3[s2 & 0xFF] ^ rk[k + 3];
            (s0, s1, s2, s3) = (t0, t1, t2, t3);
        }
        int last = 4 * rounds;
        BinaryPrimitives.WriteUInt32BigEndian(output, Last(S, s0, s1, s2, s3) ^ rk[last]);
        BinaryPrimitives.WriteUInt32BigEndian(output[4..], Last(S, s1, s2, s3, s0) ^ rk[last + 1]);
        BinaryPrimitives.WriteUInt32BigEndian(output[8..], Last(S, s2, s3, s0, s1) ^ rk[last + 2]);
        BinaryPrimitives.WriteUInt32BigEndian(output[12..], Last(S, s3, s0, s1, s2) ^ rk[last + 3]);
    }

    private static void DecryptBlock(uint[] rk, int rounds, ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint s0 = BinaryPrimitives.ReadUInt32BigEndian(input) ^ rk[0];
        uint s1 = BinaryPrimitives.ReadUInt32BigEndian(input[4..]) ^ rk[1];
        uint s2 = BinaryPrimitives.ReadUInt32BigEndian(input[8..]) ^ rk[2];
        uint s3 = BinaryPrimitives.ReadUInt32BigEndian(input[12..]) ^ rk[3];
        for (int r = 1; r < rounds; r++)
        {
            int k = 4 * r;
            uint t0 = Td0[s0 >> 24] ^ Td1[(s3 >> 16) & 0xFF] ^ Td2[(s2 >> 8) & 0xFF] ^ Td3[s1 & 0xFF] ^ rk[k];
            uint t1 = Td0[s1 >> 24] ^ Td1[(s0 >> 16) & 0xFF] ^ Td2[(s3 >> 8) & 0xFF] ^ Td3[s2 & 0xFF] ^ rk[k + 1];
            uint t2 = Td0[s2 >> 24] ^ Td1[(s1 >> 16) & 0xFF] ^ Td2[(s0 >> 8) & 0xFF] ^ Td3[s3 & 0xFF] ^ rk[k + 2];
            uint t3 = Td0[s3 >> 24] ^ Td1[(s2 >> 16) & 0xFF] ^ Td2[(s1 >> 8) & 0xFF] ^ Td3[s0 & 0xFF] ^ rk[k + 3];
            (s0, s1, s2, s3) = (t0, t1, t2, t3);
        }
        int last = 4 * rounds;
        BinaryPrimitives.WriteUInt32BigEndian(output, Last(Si, s0, s3, s2, s1) ^ rk[last]);
        BinaryPrimitives.WriteUInt32BigEndian(output[4..], Last(Si, s1, s0, s3, s2) ^ rk[last + 1]);
        BinaryPrimitives.WriteUInt32BigEndian(output[8..], Last(Si, s2, s1, s0, s3) ^ rk[last + 2]);
        BinaryPrimitives.WriteUInt32BigEndian(output[12..], Last(Si, s3, s2, s1, s0) ^ rk[last + 3]);
    }

    /// <summary>The last round's substitution and shift: one byte from each of four words.</summary>
    private static uint Last(byte[] box, uint a, uint b, uint c, uint d) =>
        ((uint)box[a >> 24] << 24) | ((uint)box[(b >> 16) & 0xFF] << 16) | ((uint)box[(c >> 8) & 0xFF] << 8) | box[d & 0xFF];
}
