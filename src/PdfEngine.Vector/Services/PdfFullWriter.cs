using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Security;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Writes a complete file (the FPDF_SaveAsCopy "no incremental" path): every object reachable
/// from the catalog and the information dictionary, renumbered densely from 1, with a classic
/// cross-reference table. Objects nothing refers to - earlier revisions, deleted pages - are left
/// behind. An encrypted document stays encrypted with its own key: strings and streams are sealed
/// again under their new numbers, and /Encrypt and the first /ID string are kept as they were.
/// </summary>
public static class PdfFullWriter
{
    /// <summary>Rewrites an open document, with <paramref name="overrides"/> replacing objects by number.</summary>
    public static byte[] Rewrite(PdfVectorDocument document, IReadOnlyDictionary<int, PdfObject>? overrides = null)
    {
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (trailer["Root"] is not PdfIndirectRef root)
            throw new InvalidOperationException("The document's catalog is not an indirect object.");
        int encryptNumber = trailer["Encrypt"] is PdfIndirectRef er ? er.ObjectNumber : -1;
        PdfDictionary? encrypt = document.Security != null ? document.Resolver.Resolve(trailer["Encrypt"]) as PdfDictionary : null;
        PdfObject? Get(int n)
        {
            if (n == encryptNumber) return null;
            if (overrides != null && overrides.TryGetValue(n, out var o)) return o;
            return document.Resolver.Resolve(n);
        }
        int? info = trailer["Info"] is PdfIndirectRef ir ? ir.ObjectNumber : null;
        string version = Max(document.HeaderVersion, "1.4");
        return Write(Get, root.ObjectNumber, info, version, document.Resolver.Resolve(trailer["ID"]) as PdfArray,
            encrypt != null ? document.Security : null, encrypt);
    }

    /// <summary>Writes a new document from objects keyed by any numbers; only what <paramref name="root"/> and <paramref name="info"/> reach is written.</summary>
    public static byte[] Write(IReadOnlyDictionary<int, PdfObject> objects, int root, int? info, string version = "1.7") =>
        Write(n => objects.TryGetValue(n, out var o) ? o : null, root, info, version, null, null, null);

    private static byte[] Write(Func<int, PdfObject?> get, int root, int? info, string version, PdfArray? id,
        PdfSecurityHandler? security, PdfDictionary? encrypt)
    {
        // Breadth-first from the roots, numbering objects as they are first referred to.
        var renumber = new Dictionary<int, int>();
        var order = new List<int>();
        var queue = new Queue<int>();
        void Reach(int n)
        {
            if (renumber.ContainsKey(n) || get(n) == null) return;
            renumber[n] = renumber.Count + 1;
            order.Add(n);
            queue.Enqueue(n);
        }
        Reach(root);
        if (info is int i) Reach(i);
        while (queue.Count > 0)
        {
            var obj = get(queue.Dequeue());
            if (obj != null) Visit(obj, Reach);
        }
        if (!renumber.ContainsKey(root))
            throw new InvalidOperationException("The document's catalog cannot be read.");

        PdfObject Renumbered(PdfObject o) => o switch
        {
            PdfIndirectRef r => renumber.TryGetValue(r.ObjectNumber, out int m) ? new PdfIndirectRef(m) : PdfNull.Instance,
            PdfArray a => new PdfArray(a.Items.Select(Renumbered).ToList()),
            PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(kv => kv.Key, kv => Renumbered(kv.Value))),
            PdfStream s => s with { Dictionary = (PdfDictionary)Renumbered(s.Dictionary) },
            _ => o,
        };

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W($"%PDF-{version}\n%âãÏÓ\n");
        int total = renumber.Count;
        var offsets = new long[total + 2];
        foreach (int old in order)
        {
            int n = renumber[old];
            var obj = Renumbered(get(old)!);
            if (obj is PdfStream st && st.Dictionary.GetName("Type") is "XRef" or "ObjStm")
                obj = PdfNull.Instance; // never reached by a reference in a valid file; harmless if it is
            if (security != null)
            {
                if (obj is PdfStream sealedStream)
                    obj = sealedStream with { Dictionary = PdfObjectWriter.WithoutCryptFilter(sealedStream.Dictionary, sealedStream.Decrypted) };
                obj = security.EncryptObject(obj, n, 0);
            }
            offsets[n] = ms.Position;
            W($"{n} 0 obj\n");
            PdfObjectWriter.Write(ms, obj, stripCrypt: security == null);
            W("\nendobj\n");
        }

        int size = total + 1;
        int encryptNumber = 0;
        if (encrypt != null)
        {
            encryptNumber = size++;
            offsets[encryptNumber] = ms.Position;
            W($"{encryptNumber} 0 obj\n");
            PdfObjectWriter.Write(ms, encrypt); // never encrypted itself
            W("\nendobj\n");
        }

        byte[] fresh = MD5.HashData(ms.ToArray());
        PdfObject first = id is { Count: > 0 } && id[0] is PdfString s0 ? s0 : new PdfString(fresh);
        var trailer = new Dictionary<string, PdfObject>
        {
            ["Size"] = new PdfInteger(size),
            ["Root"] = new PdfIndirectRef(renumber[root]),
        };
        if (info is int infoNumber && renumber.TryGetValue(infoNumber, out int newInfo)) trailer["Info"] = new PdfIndirectRef(newInfo);
        if (encrypt != null)
        {
            trailer["Encrypt"] = new PdfIndirectRef(encryptNumber);
            // Revisions 2 to 4 derive the key from the first /ID string: both stay exactly as they were.
            trailer["ID"] = id ?? new PdfArray(new PdfObject[] { first, first });
        }
        else
        {
            trailer["ID"] = new PdfArray(new PdfObject[] { first, new PdfString(fresh) });
        }

        long xref = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++) W($"{offsets[n].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        W("trailer\n");
        PdfObjectWriter.Write(ms, new PdfDictionary(trailer));
        W($"\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static void Visit(PdfObject obj, Action<int> reach)
    {
        switch (obj)
        {
            case PdfIndirectRef r: reach(r.ObjectNumber); break;
            case PdfArray a: foreach (var item in a) Visit(item, reach); break;
            case PdfDictionary d: foreach (var v in d.Entries.Values) Visit(v, reach); break;
            case PdfStream s: Visit(s.Dictionary, reach); break;
        }
    }

    internal static string Max(string? a, string b)
    {
        static double P(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        return P(a) > P(b) && P(a) <= 2.0 ? a! : b;
    }
}
