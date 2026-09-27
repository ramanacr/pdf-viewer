using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.PdfA.Xmp;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Optimize;

/// <summary>Decodes and encodes JPEG images for downsampling; the core has no JPEG codec, so the host supplies one.</summary>
public interface IPdfJpegCodec
{
    /// <summary>Interleaved 8-bit samples (1 or 3 components), or null when the JPEG cannot be decoded.</summary>
    (byte[] Samples, int Width, int Height, int Components)? Decode(byte[] jpeg);
    byte[]? Encode(byte[] samples, int width, int height, int components, int quality);
}

public sealed class PdfOptimizeOptions
{
    /// <summary>Pack objects into compressed object streams with a cross-reference stream (PDF 1.5).</summary>
    public bool CompressObjects { get; init; } = true;
    /// <summary>Decode streams and write them again with the strongest Flate, when that is smaller.</summary>
    public bool RecompressStreams { get; init; } = true;
    /// <summary>Store identical fonts, images and other shared resources once.</summary>
    public bool MergeDuplicates { get; init; } = true;
    /// <summary>Drop page thumbnails and applications' private data (PieceInfo).</summary>
    public bool RemoveThumbnailsAndPrivateData { get; init; } = true;
    /// <summary>Downsample images shown above this resolution (pixels per inch) to it; null leaves images as they are.</summary>
    public double? ImageResolution { get; init; }
    /// <summary>Only images above this multiple of <see cref="ImageResolution"/> are downsampled (Acrobat uses 1.5).</summary>
    public double DownsampleThreshold { get; init; } = 1.5;
    /// <summary>For downsampled JPEG images; without it, JPEG images keep their resolution.</summary>
    public IPdfJpegCodec? JpegCodec { get; init; }
    public int JpegQuality { get; init; } = 80;
}

public sealed class PdfOptimizeResult
{
    public required byte[] Bytes { get; init; }
    public long OriginalSize { get; init; }
    public int ObjectsRemoved { get; init; }
    public int DuplicatesMerged { get; init; }
    public int StreamsRecompressed { get; init; }
    public int ImagesDownsampled { get; init; }
    /// <summary>The document was signed: a rewrite breaks its signatures.</summary>
    public bool SignaturesInvalidated { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Makes a document smaller without changing how it looks: a complete rewrite of what the
/// catalog reaches (unused objects go), with identical shared resources stored once, streams
/// recompressed, objects packed into object streams, page thumbnails and private application data
/// dropped, and, when asked, images shown above a resolution downsampled to it. The document's
/// text, structure, forms, annotations, metadata and PDF/A claim are kept.
/// </summary>
public static class PdfOptimizer
{
    public static PdfOptimizeResult Optimize(PdfVectorDocument document, byte[] original, PdfOptimizeOptions? options = null)
    {
        options ??= new PdfOptimizeOptions();
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (trailer["Encrypt"] != null)
            throw new InvalidOperationException("The document is encrypted. Remove its security first, then reduce its size, and protect it again.");
        if (trailer["Root"] is not PdfIndirectRef root || r.Resolve(root) is not PdfDictionary catalog)
            throw new InvalidOperationException("The document has no catalog.");
        var notes = new List<string>();

        // PDF/A-1 allows neither object streams nor compressed metadata (ISO 19005-1 6.1.4, 6.7.2).
        int pdfaPart = PdfAPart(r, catalog);
        bool objectStreams = options.CompressObjects && pdfaPart != 1;
        if (options.CompressObjects && pdfaPart == 1) notes.Add("The document claims PDF/A-1, which does not allow object streams: objects are not packed.");

        var objects = Reachable(r, trailer);
        int before = document.XrefTable.Entries.Count(e => e.Value.IsInUse && e.Key != 0);
        bool signed = objects.Values.Any(o => o is PdfDictionary d && d.GetName("Type") is "Sig" or "DocTimeStamp" || o is PdfDictionary f && f.GetName("FT") == "Sig" && f["V"] != null);

        if (options.RemoveThumbnailsAndPrivateData) StripThumbnailsAndPrivateData(objects);

        int downsampled = 0;
        if (options.ImageResolution is double ppi && ppi > 0)
            downsampled = DownsampleImages(document, objects, ppi, options, notes);

        int recompressed = 0;
        if (options.RecompressStreams)
            foreach (int n in objects.Keys.ToList())
                if (objects[n] is PdfStream s && Recompress(r, s, pdfaPart) is { } smaller)
                {
                    objects[n] = smaller;
                    recompressed++;
                }

        int merged = options.MergeDuplicates ? MergeDuplicates(objects, root.ObjectNumber) : 0;
        byte[] bytes = Write(objects, trailer, root.ObjectNumber, objectStreams, pdfaPart == 1 ? "1.4" : objectStreams ? "1.7" : Header(original));
        if (bytes.Length >= original.Length && downsampled == 0)
            notes.Add("The document was already compact: the rewrite is not smaller.");
        return new PdfOptimizeResult
        {
            Bytes = bytes, OriginalSize = original.Length, ObjectsRemoved = Math.Max(0, before - objects.Count - merged),
            DuplicatesMerged = merged, StreamsRecompressed = recompressed, ImagesDownsampled = downsampled,
            SignaturesInvalidated = signed, Notes = notes,
        };
    }

    private static string Header(byte[] original)
    {
        string head = Encoding.Latin1.GetString(original, 0, Math.Min(16, original.Length));
        int at = head.IndexOf("%PDF-", StringComparison.Ordinal);
        return at >= 0 && at + 8 <= head.Length ? head.Substring(at + 5, 3) : "1.7";
    }

    private static int PdfAPart(PdfObjectResolver r, PdfDictionary catalog)
    {
        if (r.Resolve(catalog["Metadata"]) is not PdfStream metadata) return 0;
        try
        {
            var xmp = XmpPacket.Parse(new PdfStreamDecoder(null, r.Resolve).DecodeStream(metadata));
            return int.TryParse(xmp.Simple("http://www.aiim.org/pdfa/ns/id/", "part"), out int part) ? part : 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ what the document uses

    /// <summary>Every object the trailer's Root and Info reach, by object number (streams decrypted as read).</summary>
    private static Dictionary<int, PdfObject> Reachable(PdfObjectResolver r, PdfDictionary trailer)
    {
        var found = new Dictionary<int, PdfObject>();
        var stack = new Stack<PdfObject>();
        if (trailer["Root"] is { } root) stack.Push(root);
        if (trailer["Info"] is { } info) stack.Push(info);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case PdfIndirectRef ir:
                    if (found.ContainsKey(ir.ObjectNumber)) break;
                    if (r.Resolve(ir.ObjectNumber) is not { } target) break;
                    if (target is PdfStream { Dictionary: var sd } && sd.GetName("Type") is "XRef" or "ObjStm") break;
                    found[ir.ObjectNumber] = target;
                    stack.Push(target);
                    break;
                case PdfArray a: foreach (var i in a) stack.Push(i); break;
                case PdfDictionary d: foreach (var v in d.Entries.Values) stack.Push(v); break;
                case PdfStream s: foreach (var v in s.Dictionary.Entries.Values) stack.Push(v); break;
            }
        }
        return found;
    }

    private static void StripThumbnailsAndPrivateData(Dictionary<int, PdfObject> objects)
    {
        foreach (int n in objects.Keys.ToList())
        {
            if (objects[n] is not PdfDictionary d) continue;
            bool page = d.GetName("Type") == "Page";
            bool drop = (page && d.ContainsKey("Thumb")) || d.ContainsKey("PieceInfo");
            if (!drop) continue;
            // PieceInfo on pages and forms holds editors' private data; Acrobat's page marks (headers, watermarks) keep theirs.
            if (d["PieceInfo"] is { } pieceInfo && Resolve(objects, pieceInfo) is PdfDictionary info && info.ContainsKey("ADBE_CompoundType")) continue;
            var entries = new Dictionary<string, PdfObject>(d.Entries);
            if (page) entries.Remove("Thumb");
            entries.Remove("PieceInfo");
            objects[n] = new PdfDictionary(entries);
        }
    }

    private static PdfObject? Resolve(Dictionary<int, PdfObject> objects, PdfObject o) =>
        o is PdfIndirectRef ir ? objects.GetValueOrDefault(ir.ObjectNumber) : o;

    // ------------------------------------------------------------------ streams

    /// <summary>
    /// The stream with its general-purpose filters (Flate with predictors, LZW, ASCII85, ASCIIHex,
    /// RunLength) replaced by the strongest Flate, when that is smaller; image codecs are kept as they
    /// are. Null when nothing is gained or the stream cannot be decoded.
    /// </summary>
    private static PdfStream? Recompress(PdfObjectResolver r, PdfStream s, int pdfaPart)
    {
        var d = s.Dictionary;
        if (d.GetName("Type") == "Metadata") return null; // left readable, as PDF/A and XMP readers expect
        if (d["F"] != null && d["Filter"] == null && d["DecodeParms"] == null) return null; // external data
        PdfDecodedStream decoded;
        try { decoded = new PdfStreamDecoder(null, r.Resolve).DecodeImageStream(s); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        byte[] raw = s.GetRawBytes().ToArray();
        var entries = new Dictionary<string, PdfObject>(d.Entries);
        entries.Remove("Length"); entries.Remove("DL"); entries.Remove("DecodeParms"); entries.Remove("Filter");
        byte[] data;
        if (decoded.ImageFilter != null)
        {
            // Only the wrappers around the image codec go (an ASCII85 JPEG).
            if (decoded.Data.Length >= raw.Length) return null;
            data = decoded.Data;
            entries["Filter"] = new PdfName(decoded.ImageFilter);
            if (decoded.ImageFilterParms != null) entries["DecodeParms"] = decoded.ImageFilterParms;
        }
        else
        {
            data = Deflate(decoded.Data);
            entries["Filter"] = new PdfName("FlateDecode");
            if (data.Length + 16 >= raw.Length) return null;
        }
        return PdfObjectWriter.NewStream(entries, data);
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ duplicates

    // Objects whose identity matters (pages, annotations, fields, outline items, structure
    // elements, optional content groups, signatures) are never merged, even when they look alike.
    private static bool Mergeable(PdfObject o)
    {
        var d = o switch { PdfDictionary dict => dict, PdfStream s => s.Dictionary, _ => null };
        if (d == null) return o is PdfArray;
        if (d.ContainsKey("PdfViewer_Marks")) return false; // page marks keep one stream per page, which Update and Remove rewrite
        if (d.GetName("Type") is "Page" or "Pages" or "Catalog" or "Annot" or "Outlines" or "StructElem" or "StructTreeRoot" or "OCG" or "OCMD" or "Sig" or "DocTimeStamp" or "Metadata")
            return false;
        foreach (string key in new[] { "Parent", "Kids", "FT", "P", "First", "Last", "Next", "Prev", "Subtype" })
            if (d.ContainsKey(key) && !(key == "Subtype" && d.GetName("Subtype") is "Image" or "Form" or "Type1" or "TrueType" or "Type0" or "CIDFontType0" or "CIDFontType2" or "Type3" or "Type1C" or "CIDFontType0C" or "OpenType" or "MMType1"))
                return false;
        return true;
    }

    /// <summary>Merges objects that serialise the same (after earlier merges), until nothing changes; returns how many went.</summary>
    private static int MergeDuplicates(Dictionary<int, PdfObject> objects, int root)
    {
        int merged = 0;
        var map = new Dictionary<int, int>();
        for (int round = 0; round < 6; round++)
        {
            var byHash = new Dictionary<string, int>(StringComparer.Ordinal);
            var found = new Dictionary<int, int>();
            foreach (var (n, o) in objects.OrderBy(e => e.Key))
            {
                if (n == root || !Mergeable(o)) continue;
                string key = Convert.ToHexString(SHA256.HashData(Serialize(Remap(o, map))));
                if (byHash.TryGetValue(key, out int keep)) found[n] = keep;
                else byHash[key] = n;
            }
            if (found.Count == 0) break;
            foreach (var (from, to) in found)
            {
                map[from] = to;
                objects.Remove(from);
                merged++;
            }
            // Earlier targets that were merged themselves now point at the survivor.
            foreach (int k in map.Keys.ToList())
            {
                int t = map[k];
                while (map.TryGetValue(t, out int next) && next != t) t = next;
                map[k] = t;
            }
        }
        if (map.Count > 0)
            foreach (int n in objects.Keys.ToList()) objects[n] = Remap(objects[n], map);
        return merged;
    }

    private static PdfObject Remap(PdfObject o, Dictionary<int, int> map) => map.Count == 0 ? o : o switch
    {
        PdfIndirectRef r => map.TryGetValue(r.ObjectNumber, out int m) ? new PdfIndirectRef(m) : r,
        PdfArray a => new PdfArray(a.Items.Select(i => Remap(i, map)).ToList()),
        PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(e => e.Key, e => Remap(e.Value, map))),
        PdfStream s => s with { Dictionary = (PdfDictionary)Remap(s.Dictionary, map) },
        _ => o,
    };

    private static byte[] Serialize(PdfObject o)
    {
        using var ms = new MemoryStream();
        PdfObjectWriter.Write(ms, o, stripCrypt: true);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ images

    private static int DownsampleImages(PdfVectorDocument document, Dictionary<int, PdfObject> objects, double target, PdfOptimizeOptions options, List<string> notes)
    {
        // The lowest resolution each image is shown at, over every page it is drawn on (in forms too).
        var shownAt = new Dictionary<int, double>();
        var decoder = new PdfStreamDecoder(null, document.Resolver.Resolve);
        for (int p = 0; p < document.PageTree.Pages.Count; p++)
        {
            var node = document.PageTree.Pages[p];
            PageContentWalker walker;
            try
            {
                using var content = new MemoryStream();
                foreach (var stream in node.Contents) { content.Write(decoder.DecodeStream(stream)); content.WriteByte((byte)'\n'); }
                walker = new PageContentWalker(document, content.ToArray(), node.Resources);
                walker.Read();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                continue;
            }
            foreach (var image in walker.Images)
            {
                if (image.ObjectNumber < 0 || image.PixelWidth <= 0 || image.PixelHeight <= 0) continue;
                var m = image.Placement;
                double w = Math.Sqrt(m.A * m.A + m.B * m.B) / 72, h = Math.Sqrt(m.C * m.C + m.D * m.D) / 72;
                if (w < 1e-6 || h < 1e-6) continue;
                double ppi = Math.Min(image.PixelWidth / w, image.PixelHeight / h);
                shownAt[image.ObjectNumber] = shownAt.TryGetValue(image.ObjectNumber, out double was) ? Math.Min(was, ppi) : ppi;
            }
        }
        // Images also drawn where the page walk does not see their size (pattern cells, annotation
        // appearances, Type 3 glyphs, soft-mask groups) are left alone.
        var elsewhere = new HashSet<int>();
        var r = document.Resolver;
        void Under(PdfObject? owner, int depth)
        {
            if (depth > 8 || r.Resolve(owner) is not { } o) return;
            var d = o is PdfStream st ? st.Dictionary : o as PdfDictionary;
            if (d == null || r.Resolve(d["Resources"]) is not PdfDictionary res) return;
            if (r.Resolve(res["XObject"]) is PdfDictionary xobjects)
                foreach (var v in xobjects.Entries.Values)
                {
                    if (v is PdfIndirectRef ir) elsewhere.Add(ir.ObjectNumber);
                    if (r.Resolve(v) is PdfStream { Dictionary: var xd } && xd.GetName("Subtype") == "Form") Under(v, depth + 1);
                }
        }
        foreach (var o in objects.Values)
        {
            var d = o is PdfStream st ? st.Dictionary : o as PdfDictionary;
            if (d == null) continue;
            if (d.GetName("Type") == "Pattern" || d.GetName("Subtype") == "Type3" || d.GetName("Type") == "Mask") Under(o, 0);
            if (d.GetName("Type") == "Annot" && r.Resolve(d["AP"]) is PdfDictionary ap)
                foreach (var state in ap.Entries.Values)
                {
                    if (r.Resolve(state) is PdfStream) Under(state, 0);
                    else if (r.Resolve(state) is PdfDictionary states) foreach (var s in states.Entries.Values) Under(s, 0);
                }
            if (d.GetName("Type") == "Mask" || d.ContainsKey("G") && d.GetName("S") is "Alpha" or "Luminosity") Under(d["G"], 0);
        }

        int count = 0;
        bool jpegSkipped = false;
        foreach (var (n, ppi) in shownAt)
        {
            if (ppi <= target * options.DownsampleThreshold || elsewhere.Contains(n) || objects.GetValueOrDefault(n) is not PdfStream image) continue;
            double factor = target / ppi;
            if (ImageDownsampler.Downsample(document.Resolver, image, factor, options, out bool jpegNeedsCodec) is { } smaller)
            {
                objects[n] = smaller.Image;
                if (smaller.Mask is { } mask && image.Dictionary["SMask"] is PdfIndirectRef maskRef && !shownAt.ContainsKey(maskRef.ObjectNumber))
                    objects[maskRef.ObjectNumber] = mask;
                count++;
            }
            jpegSkipped |= jpegNeedsCodec;
        }
        if (jpegSkipped) notes.Add("JPEG images were left at their resolution: no JPEG codec was available.");
        return count;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// A complete rewrite: objects renumbered densely; with <paramref name="objectStreams"/>, every
    /// object that may be (not streams) packed a hundred to a compressed object stream, and a
    /// cross-reference stream; otherwise a classic table. A new file identifier keeps the first half.
    /// </summary>
    private static byte[] Write(Dictionary<int, PdfObject> objects, PdfDictionary trailer, int root, bool objectStreams, string version)
    {
        var renumber = new Dictionary<int, int>();
        foreach (int n in objects.Keys.OrderBy(k => k == root ? -1 : k)) renumber[n] = renumber.Count + 1;
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
        var offsets = new long[total + 1];
        var inStream = new (int Stream, int Index)[total + 1];

        var loose = new List<(int Old, int New)>();
        var packed = new List<(int Old, int New)>();
        foreach (var (old, n) in renumber.OrderBy(x => x.Value))
            (objectStreams && objects[old] is not PdfStream ? packed : loose).Add((old, n));

        foreach (var (old, n) in loose)
        {
            offsets[n] = ms.Position;
            W($"{n} 0 obj\n");
            PdfObjectWriter.Write(ms, Renumbered(objects[old]), stripCrypt: true);
            W("\nendobj\n");
        }

        int next = total + 1;
        var streamNumbers = new List<int>();
        for (int start = 0; start < packed.Count; start += 100)
        {
            var group = packed.Skip(start).Take(100).ToList();
            var header = new StringBuilder();
            using var body = new MemoryStream();
            for (int i = 0; i < group.Count; i++)
            {
                header.Append(group[i].New).Append(' ').Append(body.Length).Append(' ');
                PdfObjectWriter.Write(body, Renumbered(objects[group[i].Old]), stripCrypt: true);
                body.WriteByte((byte)'\n');
                inStream[group[i].New] = (next, i);
            }
            byte[] first = Encoding.Latin1.GetBytes(header.ToString());
            byte[] data = Deflate(first.Concat(body.ToArray()).ToArray());
            int number = next++;
            streamNumbers.Add(number);
            Array.Resize(ref offsets, next);
            offsets[number] = ms.Position;
            W($"{number} 0 obj\n");
            PdfObjectWriter.Write(ms, PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("ObjStm"), ["N"] = new PdfInteger(group.Count), ["First"] = new PdfInteger(first.Length), ["Filter"] = new PdfName("FlateDecode"),
            }, data));
            W("\nendobj\n");
        }

        var r = trailer;
        byte[] fresh = MD5.HashData(ms.ToArray());
        PdfObject firstId = r["ID"] is PdfArray { Count: 2 } ids && ids[0] is PdfString s0 && s0.RawBytes.Length > 0 ? s0 : new PdfString(fresh);
        var entries = new Dictionary<string, PdfObject>
        {
            ["Root"] = new PdfIndirectRef(renumber[root]),
            ["ID"] = new PdfArray(new PdfObject[] { firstId, new PdfString(fresh) }),
        };
        if (r["Info"] is PdfIndirectRef info && renumber.TryGetValue(info.ObjectNumber, out int infoNumber)) entries["Info"] = new PdfIndirectRef(infoNumber);

        if (!objectStreams)
        {
            long xref = ms.Position;
            W($"xref\n0 {total + 1}\n0000000000 65535 f \n");
            for (int n = 1; n <= total; n++) W($"{offsets[n]:D10} 00000 n \n");
            entries["Size"] = new PdfInteger(total + 1);
            W("trailer\n");
            PdfObjectWriter.Write(ms, new PdfDictionary(entries));
            W($"\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }

        // Cross-reference stream: type 1 (offset) for loose objects and object streams, type 2 (stream, index) for packed ones.
        int xrefNumber = next++;
        long xrefOffset = ms.Position;
        Array.Resize(ref offsets, next);
        offsets[xrefNumber] = xrefOffset;
        int size = next;
        using var table = new MemoryStream();
        void Row(int type, long field2, int field3)
        {
            table.WriteByte((byte)type);
            for (int k = 3; k >= 0; k--) table.WriteByte((byte)(field2 >> (8 * k)));
            table.WriteByte((byte)(field3 >> 8));
            table.WriteByte((byte)field3);
        }
        Row(0, 0, 65535);
        for (int n = 1; n < size; n++)
        {
            if (n <= total && inStream[n].Stream != 0) Row(2, inStream[n].Stream, inStream[n].Index);
            else Row(1, offsets[n], 0);
        }
        entries["Type"] = new PdfName("XRef");
        entries["Size"] = new PdfInteger(size);
        entries["W"] = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(4), new PdfInteger(2) });
        entries["Filter"] = new PdfName("FlateDecode");
        W($"{xrefNumber} 0 obj\n");
        PdfObjectWriter.Write(ms, PdfObjectWriter.NewStream(entries, Deflate(table.ToArray())));
        W($"\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }
}
