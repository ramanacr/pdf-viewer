using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfEngine.Vector.Objects;

/// <summary>
/// Serializes PDF objects (ISO 32000-2 7.3). Strings are written in hex (no escaping questions),
/// names with #-escapes, streams with a recomputed /Length. A stream created in memory carries
/// its data in <see cref="PdfStream.CachedRawBytes"/>.
/// </summary>
public static class PdfObjectWriter
{
    /// <param name="stripCrypt">Drop a /Crypt filter from streams whose data was decrypted on read.</param>
    public static void Write(Stream s, PdfObject obj, bool stripCrypt = false)
    {
        void W(string t) => s.Write(Encoding.Latin1.GetBytes(t));
        switch (obj)
        {
            case PdfNull: W("null"); break;
            case PdfBoolean b: W(b.Value ? "true" : "false"); break;
            case PdfInteger i: W(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfReal r: W(FormatReal(r.Value)); break;
            case PdfName n: W("/" + EscapeName(n.Value)); break;
            case PdfString str: W("<" + Convert.ToHexString(str.RawBytes.Span) + ">"); break;
            case PdfIndirectRef ir: W($"{ir.ObjectNumber} {ir.GenerationNumber} R"); break;
            case PdfArray a:
                W("[");
                for (int k = 0; k < a.Count; k++)
                {
                    if (k > 0) W(" ");
                    Write(s, a[k], stripCrypt);
                }
                W("]");
                break;
            case PdfDictionary d:
                W("<<");
                foreach (var (key, value) in d.Entries)
                {
                    W("/" + EscapeName(key) + " ");
                    Write(s, value, stripCrypt);
                    W(" ");
                }
                W(">>");
                break;
            case PdfStream st:
            {
                byte[] data = st.GetRawBytes().ToArray();
                var dict = stripCrypt ? WithoutCryptFilter(st.Dictionary, st.Decrypted) : st.Dictionary;
                var entries = new Dictionary<string, PdfObject>(dict.Entries) { ["Length"] = new PdfInteger(data.Length) };
                Write(s, new PdfDictionary(entries), stripCrypt);
                W("\nstream\n");
                s.Write(data);
                W("\nendstream");
                break;
            }
            default:
                W("null");
                break;
        }
    }

    /// <summary>A text string (7.9.2.2): PDFDocEncoding-compatible Latin-1 when it fits, else UTF-16BE with a BOM.</summary>
    public static PdfString TextString(string text)
    {
        if (text.All(c => c >= 0x20 && c <= 0x7E || c is '\n' or '\r' or '\t'))
            return new PdfString(Encoding.Latin1.GetBytes(text));
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var bytes = new byte[utf16.Length + 2];
        bytes[0] = 0xFE; bytes[1] = 0xFF;
        utf16.CopyTo(bytes, 2);
        return new PdfString(bytes);
    }

    /// <summary>A stream object created in memory.</summary>
    public static PdfStream NewStream(IReadOnlyDictionary<string, PdfObject> entries, byte[] data) =>
        new(new PdfDictionary(entries), 0, data.Length, null, data);

    /// <summary>Drops /Crypt from /Filter (and its /DecodeParms slot) once the data is decrypted.</summary>
    internal static PdfDictionary WithoutCryptFilter(PdfDictionary dict, bool decrypted)
    {
        if (!decrypted)
            return dict;
        var filter = dict["Filter"];
        var parms = dict["DecodeParms"];
        List<PdfObject> filters = filter is PdfArray fa ? fa.Items.ToList() : filter != null ? new List<PdfObject> { filter } : new();
        List<PdfObject>? parmList = parms is PdfArray pa ? pa.Items.ToList() : parms != null ? new List<PdfObject> { parms } : null;
        int index = filters.FindIndex(f => f is PdfName { Value: "Crypt" });
        if (index < 0)
            return dict;
        filters.RemoveAt(index);
        if (parmList != null && index < parmList.Count) parmList.RemoveAt(index);
        var entries = new Dictionary<string, PdfObject>(dict.Entries);
        entries.Remove("Filter");
        entries.Remove("DecodeParms");
        if (filters.Count > 0) entries["Filter"] = filters.Count == 1 ? filters[0] : new PdfArray(filters);
        if (parmList is { Count: > 0 }) entries["DecodeParms"] = parmList.Count == 1 ? parmList[0] : new PdfArray(parmList);
        return new PdfDictionary(entries);
    }

    public static string FormatReal(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
        string s = v.ToString("0.##########", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    public static string EscapeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (byte b in Encoding.Latin1.GetBytes(name)) // names are byte-per-char (see PdfLexer)
        {
            if (b < 0x21 || b > 0x7E || b is (byte)'#' or (byte)'/' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
                sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append((char)b);
        }
        return sb.ToString();
    }
}
