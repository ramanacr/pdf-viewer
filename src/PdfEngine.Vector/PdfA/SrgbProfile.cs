using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfEngine.Vector.PdfA;

/// <summary>
/// An ICC version 2 display profile for sRGB (IEC 61966-2-1), built from the standard's numbers:
/// D50-adapted (Bradford) colorants, the D65 media white and the sRGB tone curve sampled at 1,024
/// points. Written here rather than shipped, so no third-party profile is redistributed.
/// </summary>
public static class SrgbProfile
{
    private static readonly Lazy<byte[]> Cached = new(Build);

    public static byte[] Bytes => Cached.Value;

    private static byte[] Build()
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", Desc("sRGB IEC61966-2.1")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9505, 1.0, 1.0891)),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
        };
        var curve = Curve();
        tags.Add(("rTRC", curve));
        tags.Add(("gTRC", curve));
        tags.Add(("bTRC", curve));

        // Tag data follows the header (128) and the tag table (4 + 12 per tag), 4-byte aligned; the curves share one copy.
        int tableSize = 4 + 12 * tags.Count;
        int offset = 128 + tableSize;
        var placed = new List<(string Sig, int Offset, int Size)>();
        var data = new MemoryStream();
        var shared = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        foreach (var (sig, bytes) in tags)
        {
            if (!shared.TryGetValue(bytes, out int at))
            {
                at = offset + (int)data.Length;
                data.Write(bytes);
                while (data.Length % 4 != 0) data.WriteByte(0);
                shared[bytes] = at;
            }
            placed.Add((sig, at, bytes.Length));
        }
        int total = offset + (int)data.Length;

        var ms = new MemoryStream();
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void Sig(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        U32((uint)total);
        U32(0);                 // preferred CMM
        U32(0x02100000);        // version 2.1
        Sig("mntr"); Sig("RGB "); Sig("XYZ ");
        U16(2026); U16(1); U16(1); U16(0); U16(0); U16(0); // date
        Sig("acsp"); Sig("MSFT");
        U32(0); U32(0); U32(0); U32(0); U32(0); // flags, manufacturer, model, attributes
        U32(0);                 // rendering intent: perceptual
        WriteS15(ms, 0.9642); WriteS15(ms, 1.0); WriteS15(ms, 0.8249); // PCS illuminant D50
        U32(0);                 // creator
        ms.Write(new byte[44]); // profile id (v4) and reserved
        U32((uint)placed.Count);
        foreach (var (sig, at, size) in placed) { Sig(sig); U32((uint)at); U32((uint)size); }
        ms.Write(data.ToArray());
        return ms.ToArray();
    }

    private static void WriteS15(Stream s, double v)
    {
        int f = (int)Math.Round(v * 65536);
        s.WriteByte((byte)(f >> 24)); s.WriteByte((byte)(f >> 16)); s.WriteByte((byte)(f >> 8)); s.WriteByte((byte)f);
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("XYZ "));
        ms.Write(new byte[4]);
        WriteS15(ms, x); WriteS15(ms, y); WriteS15(ms, z);
        return ms.ToArray();
    }

    private static byte[] Curve()
    {
        const int n = 1024;
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("curv"));
        ms.Write(new byte[4]);
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte((byte)(n >> 8)); ms.WriteByte(n & 0xFF);
        for (int i = 0; i < n; i++)
        {
            double v = i / (double)(n - 1);
            double linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            int u = (int)Math.Round(linear * 65535);
            ms.WriteByte((byte)(u >> 8)); ms.WriteByte((byte)u);
        }
        return ms.ToArray();
    }

    private static byte[] Text(string text)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("text"));
        ms.Write(new byte[4]);
        ms.Write(Encoding.ASCII.GetBytes(text));
        ms.WriteByte(0);
        return ms.ToArray();
    }

    /// <summary>textDescriptionType (ICC.1:2001-04 6.5.17): ASCII, then empty Unicode and ScriptCode parts.</summary>
    private static byte[] Desc(string text)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("desc"));
        ms.Write(new byte[4]);
        int count = text.Length + 1;
        ms.WriteByte((byte)(count >> 24)); ms.WriteByte((byte)(count >> 16)); ms.WriteByte((byte)(count >> 8)); ms.WriteByte((byte)count);
        ms.Write(Encoding.ASCII.GetBytes(text));
        ms.WriteByte(0);
        ms.Write(new byte[8]);  // Unicode language code and count
        ms.Write(new byte[3]);  // ScriptCode code and count
        ms.Write(new byte[67]); // ScriptCode string
        return ms.ToArray();
    }
}
