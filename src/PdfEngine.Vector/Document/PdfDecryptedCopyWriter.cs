using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Document;

/// <summary>
/// Serializes an opened (decrypted) document as an unencrypted PDF: every object as read through
/// the security handler, object-stream members as ordinary objects, stream data with /Crypt filters
/// removed, and a classic cross-reference table. Used in memory so a renderer or editor that cannot
/// open the original security handler (e.g. PDFium with public-key encryption) can work on it.
/// The result is not encrypted: callers must not write it where the original's protection is expected.
/// </summary>
public static class PdfDecryptedCopyWriter
{
    public static byte[] Write(PdfVectorDocument document)
    {
        var xref = document.XrefTable;
        var resolver = document.Resolver;
        var trailer = xref.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        int encryptNumber = trailer["Encrypt"] is PdfIndirectRef er ? er.ObjectNumber : -1;

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%âãÏÓ\n");

        int maxNumber = xref.Entries.Keys.DefaultIfEmpty(0).Max();
        var offsets = new Dictionary<int, (long Offset, int Generation)>();
        foreach (var (number, entry) in xref.Entries.OrderBy(e => e.Key))
        {
            if (!entry.IsInUse || number == encryptNumber || number == 0)
                continue;
            var obj = resolver.Resolve(number);
            if (obj == null)
                continue;
            if (obj is PdfStream st && st.Dictionary.GetName("Type") is "XRef" or "ObjStm")
                continue; // replaced by the classic table and by writing members individually
            int generation = entry.IsCompressed ? 0 : entry.GenerationNumber;
            offsets[number] = (ms.Position, generation);
            W($"{number} {generation} obj\n");
            WriteObject(ms, obj);
            W("\nendobj\n");
        }

        long xrefPos = ms.Position;
        W($"xref\n0 {maxNumber + 1}\n0000000000 65535 f \n");
        for (int n = 1; n <= maxNumber; n++)
        {
            W(offsets.TryGetValue(n, out var o)
                ? $"{o.Offset.ToString("D10", CultureInfo.InvariantCulture)} {o.Generation:D5} n \n"
                : "0000000000 00000 f \n");
        }
        var trailerEntries = new Dictionary<string, PdfObject> { ["Size"] = new PdfInteger(maxNumber + 1) };
        foreach (var key in new[] { "Root", "Info", "ID" })
            if (trailer[key] is { } v) trailerEntries[key] = v;
        W("trailer\n");
        WriteObject(ms, new PdfDictionary(trailerEntries));
        W($"\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }

    private static void WriteObject(Stream s, PdfObject obj)
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
                    WriteObject(s, a[k]);
                }
                W("]");
                break;
            case PdfDictionary d:
                W("<<");
                foreach (var (key, value) in d.Entries)
                {
                    W("/" + EscapeName(key) + " ");
                    WriteObject(s, value);
                    W(" ");
                }
                W(">>");
                break;
            case PdfStream st:
            {
                byte[] data = st.GetRawBytes().ToArray();
                var dict = WithoutCryptFilter(st.Dictionary, st.Decrypted);
                var entries = new Dictionary<string, PdfObject>(dict.Entries) { ["Length"] = new PdfInteger(data.Length) };
                WriteObject(s, new PdfDictionary(entries));
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

    /// <summary>Drops /Crypt from /Filter (and its /DecodeParms slot) once the data is decrypted.</summary>
    private static PdfDictionary WithoutCryptFilter(PdfDictionary dict, bool decrypted)
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

    private static string FormatReal(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
        string s = v.ToString("0.##########", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string EscapeName(string name)
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
