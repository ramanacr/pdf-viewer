using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>The document information dictionary (14.3.3) and the file's security, as PDFium's FPDF_GetMetaText and friends report them.</summary>
public sealed record PdfDocumentProperties
{
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string Producer { get; init; } = string.Empty;

    /// <summary>The raw /CreationDate and /ModDate strings ("D:..."), empty when absent.</summary>
    public string CreationDateText { get; init; } = string.Empty;
    public string ModDateText { get; init; } = string.Empty;
    public DateTime? CreationDate => PdfTextStrings.ParseDate(CreationDateText);
    public DateTime? ModDate => PdfTextStrings.ParseDate(ModDateText);

    /// <summary>The header version, e.g. "1.7", or null when the file has no header.</summary>
    public string? HeaderVersion { get; init; }

    /// <summary>The catalog's /Version, which overrides the header when later (7.2.2).</summary>
    public string? CatalogVersion { get; init; }

    public int PageCount { get; init; }

    /// <summary>The security handler revision (/R), or -1 when the file is not encrypted (FPDF_GetSecurityHandlerRevision).</summary>
    public int SecurityRevision { get; init; } = -1;

    /// <summary>/P as an unsigned value, 0xFFFFFFFF for an unencrypted file or the owner (FPDF_GetDocPermissions).</summary>
    public uint Permissions { get; init; } = 0xFFFFFFFF;

    public bool IsEncrypted => SecurityRevision >= 0;
    public bool IsOwner { get; init; } = true;
    public string? EncryptionAlgorithm { get; init; }

    /// <summary>An /Info value by key, for keys other than the standard ones.</summary>
    public IReadOnlyDictionary<string, string> Custom { get; init; } = new Dictionary<string, string>();
}

/// <summary>A page's boxes (14.11.2) in default user space, its /Rotate and its displayed size.</summary>
public sealed record PdfPageGeometry(int PageNumber, PdfRect MediaBox, PdfRect CropBox, PdfRect BleedBox, PdfRect TrimBox, PdfRect ArtBox, int Rotation)
{
    /// <summary>The size as displayed: the crop box, turned by /Rotate (FPDF_GetPageSizeByIndexF).</summary>
    public PdfSize DisplaySize => Rotation is 90 or 270 ? new PdfSize(CropBox.Height, CropBox.Width) : new PdfSize(CropBox.Width, CropBox.Height);
}

/// <summary>One outline (bookmark) item and its children.</summary>
public sealed record PdfOutlineItem(string Title, PdfDestination? Destination, PdfAction? Action, IReadOnlyList<PdfOutlineItem> Children)
{
    /// <summary>The page this item goes to (0-based): its /Dest, else its go-to action's destination; -1 when none.</summary>
    public int PageIndex => (Destination ?? (Action?.Kind == PdfActionKind.GoTo ? Action.Destination : null))?.PageIndex ?? -1;

    /// <summary>/Count: positive when the item is open.</summary>
    public int Count { get; init; }

    /// <summary>/C, the title colour (RGB), and /F (1 italic, 2 bold).</summary>
    public double[]? Color { get; init; }
    public int Style { get; init; }

    public bool IsOpen => Count > 0;
}

/// <summary>A link annotation: its rectangle, quadrilaterals, and where it goes.</summary>
public sealed record PdfLink(int PageNumber, int Index, PdfRect Rect, IReadOnlyList<double[]> QuadPoints, PdfDestination? Destination, PdfAction? Action)
{
    /// <summary>The page this link goes to in this document (0-based), or -1.</summary>
    public int TargetPageIndex => (Destination ?? (Action?.Kind == PdfActionKind.GoTo ? Action.Destination : null))?.PageIndex ?? -1;
}

/// <summary>
/// Reads document-level information from the vector engine's parse: properties, page geometry,
/// the outline and links. Nothing here depends on PDFium; results mirror what PDFium reports so
/// the two can be compared on a corpus.
/// </summary>
public static class PdfDocumentInfo
{
    private static readonly string[] StandardKeys = { "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped" };

    public static PdfDocumentProperties ReadProperties(PdfVectorDocument document)
    {
        var r = document.Resolver;
        var info = document.XrefTable.Trailer is { } trailer ? r.Resolve(trailer["Info"]) as PdfDictionary : null;
        string Text(string key)
        {
            switch (info != null ? r.Resolve(info[key]) : null)
            {
                case PdfString s:
                    return PdfTextStrings.Decode(s.RawBytes.Span);
                case PdfStream st:
                    // Not a text string, but PDFium reads a stream's data as one; so do we, to agree.
                    try { return PdfTextStrings.Decode(new Streams.PdfStreamDecoder(null, r.Resolve).DecodeStream(st)); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { return string.Empty; }
                default:
                    return string.Empty;
            }
        }

        var custom = new Dictionary<string, string>();
        if (info != null)
        {
            foreach (var (key, value) in info.Entries)
            {
                if (Array.IndexOf(StandardKeys, key) >= 0) continue;
                if (PdfTextStrings.Decode(r.Resolve(value)) is { } text) custom[key] = text;
            }
        }

        var security = document.Security;
        return new PdfDocumentProperties
        {
            Title = Text("Title"),
            Author = Text("Author"),
            Subject = Text("Subject"),
            Keywords = Text("Keywords"),
            Creator = Text("Creator"),
            Producer = Text("Producer"),
            CreationDateText = Text("CreationDate"),
            ModDateText = Text("ModDate"),
            HeaderVersion = document.HeaderVersion,
            CatalogVersion = document.Catalog.GetName("Version"),
            PageCount = document.PageCount,
            SecurityRevision = security?.Revision ?? -1,
            Permissions = PermissionBits(document, security),
            IsOwner = security?.IsOwner ?? true,
            EncryptionAlgorithm = security?.Algorithm,
            Custom = custom,
        };
    }

    /// <summary>
    /// /P as FPDF_GetDocPermissions reports it: every bit for the owner; for the Standard handler
    /// the two low bits cleared and the reserved high bits set (Table 22).
    /// </summary>
    private static uint PermissionBits(PdfVectorDocument document, Security.PdfSecurityHandler? security)
    {
        if (security == null) return 0xFFFFFFFF;
        uint p = security.IsOwner ? 0xFFFFFFFF : unchecked((uint)security.Permissions);
        var encrypt = document.XrefTable.Trailer is { } t ? document.Resolver.Resolve(t["Encrypt"]) as PdfDictionary : null;
        if (encrypt?.GetName("Filter") == "Standard") p = (p & 0xFFFFFFFC) | 0xFFFFF0C0;
        return p;
    }

    public static PdfPageGeometry ReadPageGeometry(PdfVectorDocument document, int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var node = document.PageTree.Pages[pageNumber - 1];
        var r = document.Resolver;
        PdfRect Box(string key, PdfRect fallback)
        {
            var box = Inherited(node.Dictionary, key, r) is { } value ? ReadRect(r.Resolve(value), r) : null;
            if (box is not { } b || b.IsEmpty) return fallback;
            // Clipped to the media box, as 14.11.2 asks of every box but the media box itself.
            double x0 = Math.Max(b.Left, node.MediaBox.Left), y0 = Math.Max(b.Top, node.MediaBox.Top);
            double x1 = Math.Min(b.Right, node.MediaBox.Right), y1 = Math.Min(b.Bottom, node.MediaBox.Bottom);
            return x1 > x0 && y1 > y0 ? new PdfRect(x0, y0, x1 - x0, y1 - y0) : fallback;
        }
        // Bleed, trim and art boxes are not inheritable; Inherited only finds them on the page itself.
        return new PdfPageGeometry(pageNumber, node.MediaBox, node.CropBox,
            Box("BleedBox", node.CropBox), Box("TrimBox", node.CropBox), Box("ArtBox", node.CropBox), node.RotationDegrees);
    }

    /// <summary>A page attribute, looked up the /Parent chain for the inheritable ones (Table 31).</summary>
    internal static PdfObject? Inherited(PdfDictionary page, string key, PdfObjectResolver r)
    {
        bool inheritable = key is "Resources" or "MediaBox" or "CropBox" or "Rotate";
        var node = page;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int depth = 0; node != null && depth < 64 && seen.Add(node); depth++)
        {
            if (node[key] is { } value) return value;
            if (!inheritable) return null;
            node = r.Resolve(node["Parent"]) as PdfDictionary;
        }
        return null;
    }

    internal static PdfRect? ReadRect(PdfObject? obj, PdfObjectResolver r)
    {
        if (r.Resolve(obj) is not PdfArray a || a.Count < 4) return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (r.Resolve(a[i]) is not { } n || !n.TryGetNumber(out v[i]) || !double.IsFinite(v[i])) return null;
        }
        double x0 = Math.Min(v[0], v[2]), y0 = Math.Min(v[1], v[3]);
        return new PdfRect(x0, y0, Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1]));
    }

    /// <summary>The outline tree (12.3.3). Items that loop back on themselves are read once.</summary>
    public static IReadOnlyList<PdfOutlineItem> ReadOutline(PdfVectorDocument document, PdfNavigation? navigation = null)
    {
        var nav = navigation ?? new PdfNavigation(document);
        var r = document.Resolver;
        if (r.Resolve(document.Catalog["Outlines"]) is not PdfDictionary root) return Array.Empty<PdfOutlineItem>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        return Level(root["First"], 0);

        List<PdfOutlineItem> Level(PdfObject? first, int depth)
        {
            var items = new List<PdfOutlineItem>();
            if (depth > 64) return items;
            var current = first;
            for (int guard = 0; current != null && guard < 100_000; guard++)
            {
                if (r.Resolve(current) is not PdfDictionary item || !visited.Add(item)) break;
                var children = Level(item["First"], depth + 1);
                items.Add(new PdfOutlineItem(Title(item, r), nav.ResolveDestination(item["Dest"]), item["Dest"] == null ? nav.ReadAction(item["A"]) : null, children)
                {
                    Count = (int)(r.Resolve(item["Count"]) is { } c && c.TryGetInteger(out long n) ? Math.Clamp(n, int.MinValue, int.MaxValue) : 0),
                    Color = Numbers(r.Resolve(item["C"]), r),
                    Style = (int)(r.Resolve(item["F"]) is { } f && f.TryGetInteger(out long fl) ? fl & 3 : 0),
                });
                current = item["Next"];
            }
            return items;
        }
    }

    /// <summary>An outline title with control characters shown as spaces, as PDFium's CPDF_Bookmark::GetTitle returns it.</summary>
    private static string Title(PdfDictionary item, PdfObjectResolver r)
    {
        string title = PdfTextStrings.Decode(r.Resolve(item["Title"])) ?? string.Empty;
        return string.Create(title.Length, title, static (span, t) =>
        {
            for (int i = 0; i < t.Length; i++) span[i] = t[i] > ' ' ? t[i] : ' ';
        });
    }

    /// <summary>The page's link annotations in /Annots order (FPDFLink_Enumerate).</summary>
    public static IReadOnlyList<PdfLink> ReadLinks(PdfVectorDocument document, int pageNumber, PdfNavigation? navigation = null)
    {
        var nav = navigation ?? new PdfNavigation(document);
        var r = document.Resolver;
        var page = document.PageTree.Pages[pageNumber - 1];
        var links = new List<PdfLink>();
        if (r.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) return links;
        for (int i = 0; i < annots.Count; i++)
        {
            if (r.Resolve(annots[i]) is not PdfDictionary a || a.GetName("Subtype") != "Link") continue;
            var rect = ReadRect(a["Rect"], r) ?? PdfRect.Empty;
            var quads = new List<double[]>();
            if (Numbers(r.Resolve(a["QuadPoints"]), r) is { } q)
                for (int k = 0; k + 7 < q.Length; k += 8) quads.Add(q[k..(k + 8)]);
            var dest = nav.ResolveDestination(a["Dest"]);
            links.Add(new PdfLink(pageNumber, i, rect, quads, dest, dest == null ? nav.ReadAction(a["A"]) : null));
        }
        return links;
    }

    internal static double[]? Numbers(PdfObject? obj, PdfObjectResolver r)
    {
        if (obj is not PdfArray a) return null;
        var values = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            if (r.Resolve(a[i]) is not { } n || !n.TryGetNumber(out values[i]) || !double.IsFinite(values[i])) return null;
        }
        return values;
    }
}
