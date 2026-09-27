using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Redaction;

/// <summary>An area to redact: a rectangle on a page, in default user space (PDF points, bottom-left origin).</summary>
public sealed record PdfRedactionArea(int PageNumber, PdfRect Rect);

public sealed class PdfRedactionOptions
{
    /// <summary>The colour the redacted areas are filled with (RGB 0–1); null leaves them blank.</summary>
    public double[]? FillColor { get; init; } = { 0, 0, 0 };
    /// <summary>Text drawn in each box (for example "Redacted" or an exemption code), or null.</summary>
    public string? OverlayText { get; init; }
    /// <summary>
    /// Decodes a JPEG image to straight BGRA of the image's own size (the core has no JPEG
    /// decoder; the Windows backend's WicJpeg does). Without it, a JPEG under a mark is removed whole.
    /// </summary>
    public Func<PdfDecodedImage, byte[]?>? JpegDecoder { get; init; }
    /// <summary>Also remove the document information dictionary and XMP metadata.</summary>
    public bool RemoveMetadata { get; init; }
}

public sealed class PdfRedactionResult
{
    public byte[] Bytes { get; internal set; } = Array.Empty<byte>();
    public int GlyphsRemoved { get; internal set; }
    public int ImagesRedacted { get; internal set; }
    public int ImagesRemoved { get; internal set; }
    public int PathsCut { get; internal set; }
    public int AnnotationsRemoved { get; internal set; }
    public int AlternateTextsRemoved { get; internal set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Redaction that removes content rather than covering it (ISO 32000-2 12.5.6.23 describes the
/// marking; this applies it). For each marked area it removes the glyphs, cuts the vector art,
/// blanks the image pixels (in new image objects), removes the annotations and form fields, and
/// drops alternate text describing what was removed, then draws the fill colour over the area.
/// The result is written as a new file, not an incremental update, with only the objects still
/// in use: the original page content and images do not survive anywhere in it.
/// </summary>
public static class PdfRedactor
{
    public static PdfRedactionResult Apply(PdfVectorDocument document, IReadOnlyList<PdfRedactionArea> areas, PdfRedactionOptions? options = null)
    {
        options ??= new PdfRedactionOptions();
        var result = new PdfRedactionResult();
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (r.Resolve(trailer["Root"]) is not PdfDictionary root)
            throw new InvalidOperationException("The document has no catalog.");

        var overrides = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(document);
        int Allocate() => next++;
        var decoder = new PdfStreamDecoder(null, r.Resolve);
        var pageNumbers = PageObjectNumbers(root, r);
        var removedWidgets = new HashSet<int>();
        var flaggedByPage = new Dictionary<int, HashSet<int>>();

        foreach (var group in areas.Where(a => a.Rect.Width > 0 && a.Rect.Height > 0).GroupBy(a => a.PageNumber))
        {
            if (group.Key < 1 || group.Key > pageNumbers.Count) continue;
            int pageObj = pageNumbers[group.Key - 1];
            var node = document.PageTree.Pages[group.Key - 1];
            var rects = group.Select(a => a.Rect).ToList();
            var pageDict = (overrides.TryGetValue(pageObj, out var o) ? o : r.Resolve(pageObj)) as PdfDictionary
                ?? throw new InvalidOperationException($"Page {group.Key} is not a dictionary.");

            // The page's content, as one stream (content arrays are concatenated per 7.8.2).
            var content = new MemoryStream();
            try
            {
                foreach (var stream in node.Contents)
                {
                    content.Write(decoder.DecodeStream(stream));
                    content.WriteByte((byte)'\n');
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"Page {group.Key}'s content cannot be read ({ex.Message}), so it cannot be redacted safely.", ex);
            }
            var flagged = new HashSet<int>();
            var redactor = new ContentRedactor(document, rects, options, result, Allocate, overrides, flagged);
            var (rewritten, resources, _) = redactor.Rewrite(content.ToArray(), node.Resources, PdfMatrix.Identity);
            flaggedByPage[pageObj] = flagged;

            var sb = new StringBuilder("q\n").Append(Encoding.Latin1.GetString(rewritten)).Append("\nQ\n");
            AppendOverlay(sb, rects, options, ref resources, r);
            int contentObj = Allocate();
            var streamDict = new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") };
            overrides[contentObj] = PdfObjectWriter.NewStream(streamDict, ContentRedactor.Deflate(Encoding.Latin1.GetBytes(sb.ToString())));

            var entries = new Dictionary<string, PdfObject>(pageDict.Entries)
            {
                ["Contents"] = new PdfIndirectRef(contentObj),
                ["Resources"] = resources,
            };
            entries.Remove("Thumb"); // a picture of the page as it was

            // Annotations under a mark: mostly covered ones (and any form field touched, whose
            // value would stay in the file) are removed with their pop-ups; the others keep going
            // but with their appearance redacted too, as it is drawn over the page.
            if (r.Resolve(pageDict["Annots"]) is PdfArray annots)
            {
                var keptAnnots = new List<PdfObject>();
                var removedHere = new HashSet<int>();
                foreach (var a in annots)
                {
                    if (r.Resolve(a) is not PdfDictionary annot || RectOf(r.Resolve(annot["Rect"]) as PdfArray) is not { } ar || !rects.Any(x => ar.IntersectsWith(x)))
                    {
                        keptAnnots.Add(a);
                        continue;
                    }
                    bool widget = annot.GetName("Subtype") == "Widget";
                    double covered = rects.Sum(x => Overlap(ar, x)) / Math.Max(ar.Width * ar.Height, 1e-9);
                    if (widget || covered >= 0.5 || a is not PdfIndirectRef
                        || !RedactAppearance((PdfIndirectRef)a, annot, ar, rects, document, options, result, Allocate, overrides, flagged))
                    {
                        if (a is PdfIndirectRef aref)
                        {
                            removedHere.Add(aref.ObjectNumber);
                            if (widget) removedWidgets.Add(aref.ObjectNumber);
                            if (annot["Popup"] is PdfIndirectRef popup) removedHere.Add(popup.ObjectNumber);
                        }
                        result.AnnotationsRemoved++;
                        continue;
                    }
                    keptAnnots.Add(a);
                }
                keptAnnots.RemoveAll(a => a is PdfIndirectRef ir && removedHere.Contains(ir.ObjectNumber));
                entries["Annots"] = new PdfArray(keptAnnots);
            }
            overrides[pageObj] = new PdfDictionary(entries);
        }

        // Form fields whose widgets were removed leave the form, too.
        if (removedWidgets.Count > 0 && r.Resolve(root["AcroForm"]) is PdfDictionary acro)
        {
            var newAcro = new Dictionary<string, PdfObject>(acro.Entries)
            {
                ["Fields"] = new PdfArray(PruneFields(r.Resolve(acro["Fields"]) as PdfArray, removedWidgets, r, overrides, 0)),
            };
            if (root["AcroForm"] is PdfIndirectRef acroRef) overrides[acroRef.ObjectNumber] = new PdfDictionary(newAcro);
            else root = new PdfDictionary(new Dictionary<string, PdfObject>(root.Entries) { ["AcroForm"] = new PdfDictionary(newAcro) });
        }

        // Tagged structure: alternate text of the removed content goes.
        if (flaggedByPage.Values.Any(f => f.Count > 0) && r.Resolve(root["StructTreeRoot"]) is PdfDictionary str)
            ScrubStructure(str["K"], null, flaggedByPage, r, overrides, result, new HashSet<int>(), 0);

        var rootNumber = ((PdfIndirectRef)trailer["Root"]!).ObjectNumber;
        if (options.RemoveMetadata)
        {
            var catalog = new Dictionary<string, PdfObject>(((overrides.TryGetValue(rootNumber, out var rc) ? rc : root) as PdfDictionary ?? root).Entries);
            catalog.Remove("Metadata");
            overrides[rootNumber] = new PdfDictionary(catalog);
        }
        else if (!overrides.ContainsKey(rootNumber) && !ReferenceEquals(root, r.Resolve(trailer["Root"])))
        {
            overrides[rootNumber] = root;
        }

        result.Bytes = PdfFullWriter.Write(document, overrides, dropInfo: options.RemoveMetadata);
        return result;
    }

    private static void AppendOverlay(StringBuilder sb, List<PdfRect> rects, PdfRedactionOptions options, ref PdfDictionary resources, Parsing.PdfObjectResolver resolver)
    {
        if (options.FillColor is not { Length: 3 } c) return;
        sb.Append("q ").Append(F(c[0])).Append(' ').Append(F(c[1])).Append(' ').Append(F(c[2])).Append(" rg\n");
        foreach (var r in rects)
            sb.Append(F(r.X)).Append(' ').Append(F(r.Y)).Append(' ').Append(F(r.Width)).Append(' ').Append(F(r.Height)).Append(" re\n");
        sb.Append("f Q\n");
        if (string.IsNullOrWhiteSpace(options.OverlayText)) return;

        // The label in Helvetica, white on dark fills and black on light, fitted to each box.
        var res = new Dictionary<string, PdfObject>(resources.Entries);
        var fonts = new Dictionary<string, PdfObject>((resolver.Resolve(resources["Font"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>())
        {
            ["RdHelv"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("Type1"),
                ["BaseFont"] = new PdfName("Helvetica"), ["Encoding"] = new PdfName("WinAnsiEncoding"),
            }),
        };
        res["Font"] = new PdfDictionary(fonts);
        resources = new PdfDictionary(res);
        bool dark = 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2] < 0.5;
        string text = options.OverlayText!;
        double widthAt1 = text.Sum(ch => Fonts.Standard14Fonts.TryGetWidth("Helvetica", ch, out double w) ? w : 556) / 1000.0;
        foreach (var r in rects)
        {
            double size = Math.Min(12, Math.Min(r.Height * 0.7, (r.Width - 4) / Math.Max(widthAt1, 0.01)));
            if (size < 3) continue;
            double tx = r.X + (r.Width - widthAt1 * size) / 2, ty = r.Y + (r.Height - size * 0.7) / 2;
            sb.Append("BT /RdHelv ").Append(F(size)).Append(" Tf ").Append(dark ? "1 g " : "0 g ")
              .Append(F(tx)).Append(' ').Append(F(ty)).Append(" Td (").Append(Escape(text)).Append(") Tj ET\n");
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0xFF) sb.Append('?');
            else if (c > 0x7E) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<PdfObject> PruneFields(PdfArray? fields, HashSet<int> removed, Parsing.PdfObjectResolver r, Dictionary<int, PdfObject> overrides, int depth)
    {
        var kept = new List<PdfObject>();
        if (fields == null || depth > 32) return kept;
        foreach (var f in fields)
        {
            if (f is PdfIndirectRef fr && removed.Contains(fr.ObjectNumber)) continue;
            if (f is PdfIndirectRef kr && r.Resolve(kr) is PdfDictionary fd && r.Resolve(fd["Kids"]) is PdfArray kids)
            {
                var keptKids = PruneFields(kids, removed, r, overrides, depth + 1);
                if (keptKids.Count == 0 && kids.Count > 0) continue; // every widget of the field went
                if (keptKids.Count != kids.Count)
                    overrides[kr.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(fd.Entries) { ["Kids"] = new PdfArray(keptKids) });
            }
            kept.Add(f);
        }
        return kept;
    }

    private static void ScrubStructure(PdfObject? node, int? page, Dictionary<int, HashSet<int>> flagged, Parsing.PdfObjectResolver r,
        Dictionary<int, PdfObject> overrides, PdfRedactionResult result, HashSet<int> visited, int depth)
    {
        if (depth > 64 || node == null) return;
        if (node is PdfArray arr)
        {
            foreach (var k in arr) ScrubStructure(k, page, flagged, r, overrides, result, visited, depth + 1);
            return;
        }
        if (node is not PdfIndirectRef nref || !visited.Add(nref.ObjectNumber) || r.Resolve(nref) is not PdfDictionary el) return;
        int? pg = el["Pg"] is PdfIndirectRef pr ? pr.ObjectNumber : page;
        bool touches = false;
        void Check(PdfObject? k)
        {
            if (k is PdfInteger mcid && pg is int p && flagged.TryGetValue(p, out var set) && set.Contains((int)mcid.Value)) touches = true;
            else if (r.Resolve(k) is PdfDictionary mcr && mcr.GetName("Type") == "MCR" && r.Resolve(mcr["MCID"]) is PdfInteger m2)
            {
                int? mp = mcr["Pg"] is PdfIndirectRef mpr ? mpr.ObjectNumber : pg;
                if (mp is int p2 && flagged.TryGetValue(p2, out var s2) && s2.Contains((int)m2.Value)) touches = true;
            }
        }
        var kids = el["K"];
        if (r.Resolve(kids) is PdfArray ka) foreach (var k in ka) Check(k is PdfIndirectRef ? r.Resolve(k) is PdfInteger ? r.Resolve(k) : k : k);
        else Check(r.Resolve(kids) is PdfInteger ? r.Resolve(kids) : kids);
        if (touches && (el.ContainsKey("ActualText") || el.ContainsKey("Alt") || el.ContainsKey("E")))
        {
            var entries = new Dictionary<string, PdfObject>(el.Entries);
            entries.Remove("ActualText"); entries.Remove("Alt"); entries.Remove("E");
            overrides[nref.ObjectNumber] = new PdfDictionary(entries);
            result.AlternateTextsRemoved++;
        }
        ScrubStructure(kids, pg, flagged, r, overrides, result, visited, depth + 1);
    }

    internal static List<int> PageObjectNumbers(PdfDictionary root, Parsing.PdfObjectResolver r)
    {
        var result = new List<int>();
        var visited = new HashSet<int>();
        void Walk(PdfObject? node, int depth)
        {
            if (depth > 64 || node is not PdfIndirectRef nr || !visited.Add(nr.ObjectNumber)) return;
            if (r.Resolve(nr) is not PdfDictionary d) return;
            if (d.GetName("Type") == "Pages" || d["Kids"] != null && d.GetName("Type") != "Page")
            {
                if (r.Resolve(d["Kids"]) is PdfArray kids)
                    foreach (var k in kids) Walk(k, depth + 1);
            }
            else result.Add(nr.ObjectNumber);
        }
        Walk(root["Pages"], 0);
        return result;
    }

    private static double Overlap(PdfRect a, PdfRect b)
    {
        double w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>
    /// Redacts an annotation's normal appearance (12.5.5: its BBox, through /Matrix, is fitted to
    /// /Rect), dropping the rollover and down appearances. False when it has no appearance that
    /// can be rewritten, and the annotation should go instead.
    /// </summary>
    private static bool RedactAppearance(PdfIndirectRef aref, PdfDictionary annot, PdfRect rect, List<PdfRect> rects, PdfVectorDocument document,
        PdfRedactionOptions options, PdfRedactionResult result, Func<int> allocate, Dictionary<int, PdfObject> overrides, HashSet<int> flagged)
    {
        var r = document.Resolver;
        if (r.Resolve(annot["AP"]) is not PdfDictionary ap) return false;
        var normal = r.Resolve(ap["N"]);
        if (normal is PdfDictionary) return false; // per-state appearances (check boxes): not text-bearing, remove instead
        if (normal is not PdfStream form) return false;
        var bboxArr = r.Resolve(form.Dictionary["BBox"]) as PdfArray;
        if (RectOf(bboxArr) is not { } bbox || bbox.Width <= 0 || bbox.Height <= 0) return false;
        var matrix = MatrixOf(r.Resolve(form.Dictionary["Matrix"]) as PdfArray) ?? PdfMatrix.Identity;
        var box = matrix.Transform(bbox);
        if (box.Width <= 0 || box.Height <= 0) return false;
        // Form space -> page: the matrix, then the transformed box scaled onto /Rect.
        var fit = new PdfMatrix(rect.Width / box.Width, 0, 0, rect.Height / box.Height,
            rect.X - box.X * rect.Width / box.Width, rect.Y - box.Y * rect.Height / box.Height);
        var toPage = matrix * fit;
        byte[] content;
        try { content = new PdfStreamDecoder(null, r.Resolve).DecodeStream(form); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        var resources = r.Resolve(form.Dictionary["Resources"]) as PdfDictionary ?? new PdfDictionary(new Dictionary<string, PdfObject>());
        var redactor = new ContentRedactor(document, rects, options, result, allocate, overrides, flagged);
        var (rewritten, newResources, _) = redactor.Rewrite(content, resources, toPage);
        var dict = new Dictionary<string, PdfObject>(form.Dictionary.Entries);
        foreach (var key in new[] { "Filter", "DecodeParms", "Length" }) dict.Remove(key);
        dict["Resources"] = newResources;
        dict["Filter"] = new PdfName("FlateDecode");
        int number = allocate();
        overrides[number] = PdfObjectWriter.NewStream(dict, ContentRedactor.Deflate(rewritten));
        var newAp = new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(number) };
        var newAnnot = new Dictionary<string, PdfObject>((overrides.TryGetValue(aref.ObjectNumber, out var o) ? o as PdfDictionary : annot)?.Entries ?? annot.Entries)
        {
            ["AP"] = new PdfDictionary(newAp),
        };
        overrides[aref.ObjectNumber] = new PdfDictionary(newAnnot);
        return true;
    }

    private static PdfMatrix? MatrixOf(PdfArray? a)
    {
        if (a == null || a.Count < 6) return null;
        var v = new double[6];
        for (int i = 0; i < 6; i++) if (!a[i].TryGetNumber(out v[i])) return null;
        return new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    private static PdfRect? RectOf(PdfArray? a)
    {
        if (a == null || a.Count < 4) return null;
        a[0].TryGetNumber(out double x0); a[1].TryGetNumber(out double y0); a[2].TryGetNumber(out double x1); a[3].TryGetNumber(out double y1);
        return new PdfRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));
}

/// <summary>
/// Writes a document as a new file (no incremental history): only the objects reachable from
/// the catalog (and the information dictionary), with overrides applied, a classic
/// cross-reference table, and the original encryption kept (each object re-encrypted with its key).
/// </summary>
internal static class PdfFullWriter
{
    public static byte[] Write(PdfVectorDocument document, IReadOnlyDictionary<int, PdfObject> overrides, bool dropInfo)
    {
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer!;
        var security = document.Security;
        int encryptNumber = trailer["Encrypt"] is PdfIndirectRef er ? er.ObjectNumber : -1;
        PdfObject? Get(int n) => overrides.TryGetValue(n, out var o) ? o : r.Resolve(n);

        // Reachability: everything the catalog (and /Info) refers to, through the overrides.
        var reachable = new SortedSet<int>();
        var stack = new Stack<PdfObject>();
        stack.Push(trailer["Root"]!);
        if (!dropInfo && trailer["Info"] is { } info) stack.Push(info);
        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            switch (obj)
            {
                case PdfIndirectRef ir:
                    if (ir.ObjectNumber == encryptNumber || !reachable.Add(ir.ObjectNumber)) break;
                    if (Get(ir.ObjectNumber) is { } target) stack.Push(target);
                    break;
                case PdfArray a:
                    foreach (var item in a) stack.Push(item);
                    break;
                case PdfDictionary d:
                    foreach (var v in d.Entries.Values) stack.Push(v);
                    break;
                case PdfStream s:
                    foreach (var v in s.Dictionary.Entries.Values) stack.Push(v);
                    break;
            }
        }

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new Dictionary<int, (long Offset, int Generation)>();
        void WriteObject(int number, PdfObject obj, bool encrypt)
        {
            int generation = document.XrefTable.Entries.TryGetValue(number, out var e) && !e.IsCompressed ? e.GenerationNumber : 0;
            offsets[number] = (ms.Position, generation);
            W($"{number} {generation} obj\n");
            PdfObjectWriter.Write(ms, encrypt && security != null ? security.EncryptObject(obj, number, generation) : obj, stripCrypt: security == null);
            W("\nendobj\n");
        }
        foreach (int number in reachable)
        {
            var obj = Get(number);
            if (obj == null || obj is PdfStream { Dictionary: var sd } && sd.GetName("Type") is "XRef" or "ObjStm") continue;
            WriteObject(number, obj, encrypt: true);
        }
        if (security != null && encryptNumber > 0 && r.Resolve(encryptNumber) is { } enc)
            WriteObject(encryptNumber, enc, encrypt: false);

        int size = Math.Max(offsets.Keys.DefaultIfEmpty(0).Max() + 1, 1);
        long xrefPos = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
            W(offsets.TryGetValue(n, out var o) ? $"{o.Offset.ToString("D10", CultureInfo.InvariantCulture)} {o.Generation:D5} n \n" : "0000000000 00000 f \n");
        var entries = new Dictionary<string, PdfObject> { ["Size"] = new PdfInteger(size), ["Root"] = trailer["Root"]! };
        if (!dropInfo && trailer["Info"] is { } infoRef) entries["Info"] = infoRef;
        if (trailer["ID"] is { } id) entries["ID"] = id;
        if (security != null && trailer["Encrypt"] is { } encRef) entries["Encrypt"] = encRef;
        W("trailer\n");
        PdfObjectWriter.Write(ms, new PdfDictionary(entries));
        W($"\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }
}
