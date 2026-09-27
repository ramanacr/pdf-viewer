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

    private static void WriteObject(Stream s, PdfObject obj) => PdfObjectWriter.Write(s, obj, stripCrypt: true);
}
