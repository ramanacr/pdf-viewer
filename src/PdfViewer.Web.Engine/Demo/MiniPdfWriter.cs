using System.IO.Compression;
using System.Text;

namespace PdfViewer.Web.Engine.Demo;

/// <summary>
/// The smallest PDF writer the demo document needs: numbered objects, optionally Flate-compressed
/// streams, a classic cross-reference table and a trailer.
/// </summary>
internal sealed class MiniPdfWriter
{
    private readonly List<byte[]?> _objects = new();

    /// <summary>Reserves an object number, for objects that refer to each other.</summary>
    public int Reserve()
    {
        _objects.Add(null);
        return _objects.Count;
    }

    public int Add(string body)
    {
        int id = Reserve();
        Set(id, body);
        return id;
    }

    public void Set(int id, string body) => _objects[id - 1] = Latin1($"{id} 0 obj\n{body}\nendobj\n");

    public int AddStream(string dictionaryEntries, string content, bool flate = true) =>
        AddStream(dictionaryEntries, Latin1(content), flate);

    public int AddStream(string dictionaryEntries, byte[] data, bool flate = true)
    {
        int id = Reserve();
        SetStream(id, dictionaryEntries, data, flate);
        return id;
    }

    public void SetStream(int id, string dictionaryEntries, byte[] data, bool flate = true)
    {
        if (flate)
        {
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            data = packed.ToArray();
            dictionaryEntries += " /Filter /FlateDecode";
        }
        using var ms = new MemoryStream();
        ms.Write(Latin1($"{id} 0 obj\n<< {dictionaryEntries} /Length {data.Length} >>\nstream\n"));
        ms.Write(data);
        ms.Write(Latin1("\nendstream\nendobj\n"));
        _objects[id - 1] = ms.ToArray();
    }

    public byte[] Build(int catalog, int? info = null)
    {
        using var ms = new MemoryStream();
        ms.Write(Latin1("%PDF-1.7\n%âãÏÓ\n"));
        var offsets = new long[_objects.Count];
        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = ms.Position;
            ms.Write(_objects[i] ?? throw new InvalidOperationException($"Object {i + 1} was reserved but never written."));
        }
        long xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(_objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (long offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(_objects.Count + 1).Append(" /Root ").Append(catalog).Append(" 0 R");
        if (info != null) sb.Append(" /Info ").Append(info).Append(" 0 R");
        sb.Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        ms.Write(Latin1(sb.ToString()));
        return ms.ToArray();
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>A PDF literal string: parentheses and backslashes escaped.</summary>
    public static string Literal(string text) =>
        "(" + text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
}
