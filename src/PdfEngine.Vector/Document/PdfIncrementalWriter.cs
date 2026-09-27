using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Document;

/// <summary>
/// Appends an incremental update (ISO 32000-2 7.5.6): the original bytes untouched, then the
/// changed and new objects, a cross-reference section for them and a trailer whose /Prev points
/// at the previous section. Nothing before the update changes, so existing digital signatures
/// stay valid and the edit can be undone by truncating. The new section has the same form as
/// the previous one (a classic table, or a cross-reference stream after one).
/// </summary>
public static class PdfIncrementalWriter
{
    /// <summary>
    /// The original file with <paramref name="objects"/> (object number → object) appended. Changed
    /// objects keep their number and generation; new ones use numbers from <see cref="NextObjectNumber"/>.
    /// </summary>
    public static byte[] Append(byte[] original, PdfVectorDocument document, IReadOnlyDictionary<int, PdfObject> objects)
    {
        if (document.IsEncrypted)
            throw new NotSupportedException("Updating an encrypted document is not supported yet.");
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        long prev = FindStartXref(original);
        if (prev < 0 || document.WasRepaired)
            throw new NotSupportedException("The document's cross-reference data is damaged; save a repaired copy first.");
        bool streamXref = IsXrefStream(original, prev);

        int oldSize = (int)(trailer.GetInteger("Size") ?? 0);
        int maxNumber = Math.Max(oldSize - 1, document.XrefTable.Entries.Keys.DefaultIfEmpty(0).Max());

        using var ms = new MemoryStream();
        ms.Write(original);
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        if (original.Length > 0 && original[^1] != '\n' && original[^1] != '\r') W("\n");

        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        foreach (var (number, obj) in objects.OrderBy(o => o.Key))
        {
            int generation = document.XrefTable.Entries.TryGetValue(number, out var e) && !e.IsCompressed ? e.GenerationNumber : 0;
            offsets[number] = (ms.Position, generation);
            W($"{number} {generation} obj\n");
            PdfObjectWriter.Write(ms, obj);
            W("\nendobj\n");
            maxNumber = Math.Max(maxNumber, number);
        }

        var trailerEntries = new Dictionary<string, PdfObject>();
        foreach (var key in new[] { "Root", "Info", "ID" })
            if (trailer[key] is { } v) trailerEntries[key] = v;
        trailerEntries["Prev"] = new PdfInteger(prev);

        long xrefPos = ms.Position;
        if (!streamXref)
        {
            trailerEntries["Size"] = new PdfInteger(maxNumber + 1);
            W("xref\n");
            foreach (var run in Runs(offsets.Keys))
            {
                W($"{run.First} {run.Count}\n");
                for (int n = run.First; n < run.First + run.Count; n++)
                    W($"{offsets[n].Offset.ToString("D10", CultureInfo.InvariantCulture)} {offsets[n].Generation:D5} n \n");
            }
            W("trailer\n");
            PdfObjectWriter.Write(ms, new PdfDictionary(trailerEntries));
        }
        else
        {
            // A cross-reference stream (7.5.8), itself an object in the new section.
            int self = maxNumber + 1;
            offsets[self] = (xrefPos, 0);
            var index = new List<PdfObject>();
            var rows = new MemoryStream();
            foreach (var run in Runs(offsets.Keys))
            {
                index.Add(new PdfInteger(run.First));
                index.Add(new PdfInteger(run.Count));
                for (int n = run.First; n < run.First + run.Count; n++)
                {
                    var (offset, generation) = offsets[n];
                    rows.WriteByte(1);
                    for (int shift = 24; shift >= 0; shift -= 8) rows.WriteByte((byte)(offset >> shift));
                    rows.WriteByte((byte)(generation >> 8));
                    rows.WriteByte((byte)generation);
                }
            }
            trailerEntries["Type"] = new PdfName("XRef");
            trailerEntries["Size"] = new PdfInteger(self + 1);
            trailerEntries["W"] = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(4), new PdfInteger(2) });
            trailerEntries["Index"] = new PdfArray(index);
            W($"{self} 0 obj\n");
            PdfObjectWriter.Write(ms, PdfObjectWriter.NewStream(trailerEntries, rows.ToArray()));
            W("\nendobj\n");
        }
        W($"\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The first object number free for a new object in an update of this document.</summary>
    public static int NextObjectNumber(PdfVectorDocument document)
    {
        int size = (int)(document.XrefTable.Trailer?.GetInteger("Size") ?? 0);
        return Math.Max(size, document.XrefTable.Entries.Keys.DefaultIfEmpty(0).Max() + 1);
    }

    private static IEnumerable<(int First, int Count)> Runs(IEnumerable<int> numbers)
    {
        int first = -1, count = 0;
        foreach (int n in numbers)
        {
            if (count > 0 && n == first + count) { count++; continue; }
            if (count > 0) yield return (first, count);
            first = n; count = 1;
        }
        if (count > 0) yield return (first, count);
    }

    /// <summary>Offset after the last <c>startxref</c> keyword, or -1.</summary>
    internal static long FindStartXref(byte[] data)
    {
        var key = Encoding.ASCII.GetBytes("startxref");
        int from = Math.Max(0, data.Length - 2048);
        int at = -1;
        for (int i = data.Length - key.Length; i >= from; i--)
        {
            if (data.AsSpan(i, key.Length).SequenceEqual(key)) { at = i; break; }
        }
        if (at < 0) return -1;
        int p = at + key.Length;
        while (p < data.Length && (data[p] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')) p++;
        long value = 0;
        int digits = 0;
        while (p < data.Length && data[p] is >= (byte)'0' and <= (byte)'9') { value = value * 10 + (data[p++] - '0'); digits++; }
        return digits == 0 || value >= data.Length ? -1 : value;
    }

    private static bool IsXrefStream(byte[] data, long offset)
    {
        long p = offset;
        while (p < data.Length && (data[p] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')) p++;
        return !(p + 4 <= data.Length && data[p] == 'x' && data[p + 1] == 'r' && data[p + 2] == 'e' && data[p + 3] == 'f');
    }
}
