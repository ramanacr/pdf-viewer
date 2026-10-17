using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// Writes minimal ICC profiles for tests (ICC.1:2004-10): a header, a tag table and tag elements, all
/// generated here so no third-party profile is ever committed.
/// </summary>
internal sealed class IccProfileBuilder
{
    private readonly List<(string Sig, byte[] Data)> _tags = new();
    private readonly string _class, _space, _pcs;
    private readonly int _version;

    public IccProfileBuilder(string deviceClass, string space, string pcs, int majorVersion = 2)
    {
        _class = deviceClass;
        _space = space;
        _pcs = pcs;
        _version = majorVersion;
    }

    public IccProfileBuilder Tag(string sig, byte[] data)
    {
        _tags.Add((sig, data));
        return this;
    }

    public byte[] Build()
    {
        int tableEnd = 128 + 4 + 12 * _tags.Count;
        var offsets = new List<int>();
        int at = Align(tableEnd);
        foreach (var (_, data) in _tags)
        {
            offsets.Add(at);
            at = Align(at + data.Length);
        }
        var p = new byte[at];
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)p.Length);
        p[8] = (byte)_version;
        p[9] = _version == 4 ? (byte)0x20 : (byte)0x10;
        Sig(p, 12, _class);
        Sig(p, 16, _space);
        Sig(p, 20, _pcs);
        Sig(p, 36, "acsp");
        WriteXyz(p, 68, 0.9642, 1.0, 0.8249); // PCS illuminant D50
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(128), (uint)_tags.Count);
        for (int i = 0; i < _tags.Count; i++)
        {
            int e = 132 + 12 * i;
            Sig(p, e, _tags[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(e + 4), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(e + 8), (uint)_tags[i].Data.Length);
            _tags[i].Data.CopyTo(p, offsets[i]);
        }
        return p;
    }

    private static int Align(int v) => (v + 3) & ~3;

    private static void Sig(byte[] p, int at, string sig) => Encoding.ASCII.GetBytes(sig.PadRight(4)).CopyTo(p, at);

    private static void WriteXyz(byte[] p, int at, double x, double y, double z)
    {
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(at), Fixed(x));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(at + 4), Fixed(y));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(at + 8), Fixed(z));
    }

    private static int Fixed(double v) => (int)Math.Round(v * 65536);

    // ------------------------------------------------------------------ tag elements

    public static byte[] Xyz(double x, double y, double z)
    {
        var d = new byte[20];
        Sig(d, 0, "XYZ ");
        WriteXyz(d, 8, x, y, z);
        return d;
    }

    /// <summary>A 'curv' gamma curve (u8Fixed8 exponent).</summary>
    public static byte[] Gamma(double gamma)
    {
        var d = new byte[14];
        Sig(d, 0, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return d;
    }

    /// <summary>A 'para' curve of the given function type and parameters.</summary>
    public static byte[] Parametric(int function, params double[] parameters)
    {
        var d = new byte[12 + 4 * parameters.Length];
        Sig(d, 0, "para");
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), (ushort)function);
        for (int i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12 + 4 * i), Fixed(parameters[i]));
        return d;
    }

    /// <summary>
    /// A lut16Type ('mft2') with identity input and output tables and the given CLUT: <paramref name="clut"/>
    /// holds grid^inputs nodes of three normalised outputs, the first input varying slowest.
    /// </summary>
    public static byte[] Lut16(int inputs, int grid, double[] clut)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); w.Write(b); }
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); w.Write(b); }
        w.Write(Encoding.ASCII.GetBytes("mft2"));
        U32(0);
        w.Write((byte)inputs);
        w.Write((byte)3);
        w.Write((byte)grid);
        w.Write((byte)0);
        foreach (double m in (ReadOnlySpan<double>)[1, 0, 0, 0, 1, 0, 0, 0, 1])
            U32((uint)Fixed(m));
        U16(2);
        U16(2);
        for (int i = 0; i < inputs; i++) { U16(0); U16(65535); }
        foreach (double v in clut) U16((int)Math.Round(Math.Clamp(v, 0, 1) * 65535));
        for (int i = 0; i < 3; i++) { U16(0); U16(65535); }
        return ms.ToArray();
    }

    /// <summary>
    /// A lutAtoBType ('mAB ') with identity B curves, a 3x3 matrix plus offsets and identity M curves, and a
    /// CLUT with identity A curves (all curves are 'curv' with no entries).
    /// </summary>
    public static byte[] LutAToB(int inputs, int grid, double[] clut, double[] matrix12)
    {
        static byte[] Identity()
        {
            var d = new byte[12];
            Sig(d, 0, "curv");
            return d;
        }
        var body = new MemoryStream();
        void U32(MemoryStream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
        // Layout: header (32), B curves, matrix, M curves, CLUT, A curves.
        int offB = 32;
        int offMatrix = offB + 3 * 12;
        int offM = offMatrix + 48;
        int offClut = offM + 3 * 12;
        int clutBytes = 20 + clut.Length * 2;
        int offA = Align(offClut + clutBytes);

        body.Write(Encoding.ASCII.GetBytes("mAB "));
        U32(body, 0);
        body.WriteByte((byte)inputs);
        body.WriteByte(3);
        body.WriteByte(0);
        body.WriteByte(0);
        U32(body, (uint)offB);
        U32(body, (uint)offMatrix);
        U32(body, (uint)offM);
        U32(body, (uint)offClut);
        U32(body, (uint)offA);
        for (int i = 0; i < 3; i++) body.Write(Identity());
        foreach (double m in matrix12) U32(body, (uint)Fixed(m));
        for (int i = 0; i < 3; i++) body.Write(Identity());
        var dims = new byte[16];
        for (int i = 0; i < inputs; i++) dims[i] = (byte)grid;
        body.Write(dims);
        body.WriteByte(2); // 16-bit precision
        body.Write(new byte[3]);
        foreach (double v in clut)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535));
            body.Write(b);
        }
        while (body.Length < offA) body.WriteByte(0);
        for (int i = 0; i < inputs; i++) body.Write(Identity());
        return body.ToArray();
    }

    // ------------------------------------------------------------------ ready-made profiles

    /// <summary>An RGB matrix/TRC profile with the Adobe RGB (1998) primaries adapted to D50 and gamma 2.2.</summary>
    public static byte[] WideGamutRgb(double gamma = 563 / 256.0) =>
        new IccProfileBuilder("mntr", "RGB ", "XYZ ")
            .Tag("wtpt", Xyz(0.9642, 1.0, 0.8249))
            .Tag("rXYZ", Xyz(0.60974, 0.31111, 0.01947))
            .Tag("gXYZ", Xyz(0.20528, 0.62567, 0.06087))
            .Tag("bXYZ", Xyz(0.14919, 0.06322, 0.74457))
            .Tag("rTRC", Gamma(gamma))
            .Tag("gTRC", Gamma(gamma))
            .Tag("bTRC", Gamma(gamma))
            .Build();

    /// <summary>A gray profile whose kTRC is the given gamma.</summary>
    public static byte[] GrayGamma(double gamma) =>
        new IccProfileBuilder("mntr", "GRAY", "XYZ ")
            .Tag("wtpt", Xyz(0.9642, 1.0, 0.8249))
            .Tag("kTRC", Gamma(gamma))
            .Build();

    /// <summary>
    /// A CMYK printer profile with a 2-point lut16 A2B0 into Lab whose corners are those of
    /// <paramref name="corners"/> (16 Lab values, C varying slowest, K fastest).
    /// </summary>
    public static byte[] TwoPointCmyk(double[][] cornersLab)
    {
        var clut = new double[16 * 3];
        for (int i = 0; i < 16; i++)
        {
            // lut16 Lab uses the legacy encoding: L 100 is 0xFF00, a/b 0 is 0x8000.
            clut[3 * i] = cornersLab[i][0] / 100 * 65280 / 65535;
            clut[3 * i + 1] = (cornersLab[i][1] + 128) * 256 / 65535;
            clut[3 * i + 2] = (cornersLab[i][2] + 128) * 256 / 65535;
        }
        var lut = Lut16(4, 2, clut);
        return new IccProfileBuilder("prtr", "CMYK", "Lab ")
            .Tag("wtpt", Xyz(0.9642, 1.0, 0.8249))
            .Tag("A2B0", lut)
            .Tag("A2B1", lut)
            .Build();
    }
}
