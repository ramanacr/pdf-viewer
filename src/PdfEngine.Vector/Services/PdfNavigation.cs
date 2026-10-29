using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>How a destination fits the page in the window (ISO 32000-2 Table 149).</summary>
public enum PdfDestinationFit { Unknown, XYZ, Fit, FitH, FitV, FitR, FitB, FitBH, FitBV }

/// <summary>
/// An explicit destination: the 0-based page index (-1 when it names no page of this document,
/// as for a remote go-to), the view, and its parameters (null where the file says "unchanged").
/// </summary>
public sealed record PdfDestination(int PageIndex, PdfDestinationFit Fit, IReadOnlyList<double?> Parameters)
{
    /// <summary>The named destination this was looked up from, if it was named.</summary>
    public string? Name { get; init; }
}

/// <summary>The action types PDFium's FPDFAction_GetType distinguishes, and the others by name.</summary>
public enum PdfActionKind { Unsupported, GoTo, GoToR, Uri, Launch, GoToE, Named, JavaScript, Other }

/// <summary>An action (12.6) as far as navigation needs it. Text values are untrusted document data.</summary>
public sealed record PdfAction(PdfActionKind Kind, string Type)
{
    public PdfDestination? Destination { get; init; }

    /// <summary>The URI of a URI action, with the catalog's /URI /Base prefixed to a relative one.</summary>
    public string? Uri { get; init; }

    /// <summary>The file a remote go-to, embedded go-to or launch action names.</summary>
    public string? FilePath { get; init; }

    /// <summary>The name of a Named action (NextPage, PrevPage, FirstPage, LastPage...).</summary>
    public string? Name { get; init; }
}

/// <summary>Name trees (7.9.6) read in key order, cycle- and depth-safe.</summary>
internal static class PdfNameTree
{
    private const int MaxDepth = 32;

    public static IEnumerable<(string Key, PdfObject? Value)> Enumerate(PdfObject? root, PdfObjectResolver resolver)
    {
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var result = new List<(string, PdfObject?)>();
        Walk(resolver.Resolve(root) as PdfDictionary, 0);
        return result;

        void Walk(PdfDictionary? node, int depth)
        {
            if (node == null || depth > MaxDepth || !visited.Add(node)) return;
            if (resolver.Resolve(node["Names"]) is PdfArray names)
            {
                for (int i = 0; i + 1 < names.Count; i += 2)
                {
                    if (resolver.Resolve(names[i]) is PdfString key)
                        result.Add((PdfTextStrings.Decode(key.RawBytes.Span), names[i + 1]));
                }
            }
            if (resolver.Resolve(node["Kids"]) is PdfArray kids)
            {
                foreach (var kid in kids) Walk(resolver.Resolve(kid) as PdfDictionary, depth + 1);
            }
        }
    }

    /// <summary>Keys compare as raw bytes (7.9.6); the decoded key is compared, which is the same for every key a writer produces.</summary>
    public static PdfObject? Lookup(PdfObject? root, string key, PdfObjectResolver resolver)
    {
        foreach (var (k, v) in Enumerate(root, resolver))
            if (string.Equals(k, key, StringComparison.Ordinal)) return v;
        return null;
    }
}

/// <summary>
/// Destinations, named destinations and actions of one document, resolved the way PDFium's
/// FPDFDest_* and FPDFAction_* functions resolve them.
/// </summary>
public sealed class PdfNavigation
{
    private readonly PdfVectorDocument _doc;
    private readonly PdfObjectResolver _r;
    private readonly Dictionary<int, int> _pageIndexByObject = new();
    private readonly Dictionary<PdfDictionary, int> _pageIndexByDict = new(ReferenceEqualityComparer.Instance);
    private Dictionary<string, PdfObject?>? _namedTree;

    public PdfNavigation(PdfVectorDocument document)
    {
        _doc = document;
        _r = document.Resolver;
        var tree = document.PageTree;
        for (int i = 0; i < tree.Count; i++)
        {
            if (tree.PageObjectNumbers[i] > 0) _pageIndexByObject.TryAdd(tree.PageObjectNumbers[i], i);
            _pageIndexByDict.TryAdd(tree.Pages[i].Dictionary, i);
        }
    }

    public PdfVectorDocument Document => _doc;

    /// <summary>The 0-based index of a page given by reference or dictionary, or -1.</summary>
    public int PageIndexOf(PdfObject? page)
    {
        if (page is PdfIndirectRef r && _pageIndexByObject.TryGetValue(r.ObjectNumber, out int byNumber))
            return byNumber;
        return _r.Resolve(page) is PdfDictionary d && _pageIndexByDict.TryGetValue(d, out int byDict) ? byDict : -1;
    }

    /// <summary>
    /// Resolves a destination: an explicit array, a name or string looked up in the /Dests name
    /// tree and then the catalog's /Dests dictionary, or a dictionary carrying it under /D.
    /// </summary>
    public PdfDestination? ResolveDestination(PdfObject? dest) => ResolveDestination(dest, 0, named: false);

    /// <param name="named">A dictionary with /D is a destination only as the value of a named destination (12.3.2.3), as PDFium reads it.</param>
    private PdfDestination? ResolveDestination(PdfObject? dest, int depth, bool named)
    {
        if (depth > 8) return null;
        var value = _r.Resolve(dest);
        switch (value)
        {
            case PdfArray array:
                return Explicit(array, local: true);
            case PdfName name:
                return LookupNamed(name.Value, depth);
            case PdfString str:
                return LookupNamed(PdfTextStrings.Decode(str.RawBytes.Span), depth);
            case PdfDictionary dict when named && dict["D"] != null:
                return ResolveDestination(dict["D"], depth + 1, named: false);
            default:
                return null;
        }
    }

    private PdfDestination? LookupNamed(string name, int depth)
    {
        PdfObject? target = null;
        if (_r.Resolve(_doc.Catalog["Names"]) is PdfDictionary names && names["Dests"] != null)
        {
            _namedTree ??= BuildNamedTree(names["Dests"]);
            _namedTree.TryGetValue(name, out target);
        }
        if (target == null && _r.Resolve(_doc.Catalog["Dests"]) is PdfDictionary dests)
            target = dests[name];
        if (target == null) return null;
        var resolved = ResolveDestination(target, depth + 1, named: true);
        return resolved == null ? null : resolved with { Name = name };
    }

    private Dictionary<string, PdfObject?> BuildNamedTree(PdfObject? root)
    {
        var map = new Dictionary<string, PdfObject?>(StringComparer.Ordinal);
        foreach (var (key, value) in PdfNameTree.Enumerate(root, _r))
            map.TryAdd(key, value);
        return map;
    }

    /// <summary>[page /Fit ...]: a page reference, or a page number in a remote destination.</summary>
    private PdfDestination? Explicit(PdfArray array, bool local)
    {
        if (array.Count == 0) return null;
        int page = -1;
        var first = _r.Resolve(array[0]);
        if (first is PdfDictionary)
            page = PageIndexOf(array[0]);
        else if (first is PdfInteger or PdfReal && first.TryGetInteger(out long number))
            page = (int)Math.Clamp(number, int.MinValue, int.MaxValue); // PDFium returns the number as the index, even out of range
        var fit = array.Count > 1 && _r.Resolve(array[1]) is PdfName fitName && Enum.TryParse<PdfDestinationFit>(fitName.Value, out var f) && f != PdfDestinationFit.Unknown
            ? f : PdfDestinationFit.Unknown;
        var parameters = new List<double?>();
        for (int i = 2; i < array.Count; i++)
            parameters.Add(_r.Resolve(array[i]) is { } p && p.TryGetNumber(out double v) && p is not PdfNull ? v : null);
        return new PdfDestination(local ? page : -1, fit, parameters);
    }

    /// <summary>An action dictionary; null when there is none.</summary>
    public PdfAction? ReadAction(PdfObject? action)
    {
        if (_r.Resolve(action) is not PdfDictionary a) return null;
        string type = a.GetName("S") ?? string.Empty;
        // /Type is optional, but an action carrying another one is not an action (PDFium's CPDF_Action::GetType).
        if (a["Type"] != null && a.GetName("Type") != "Action")
            return new PdfAction(PdfActionKind.Unsupported, type);
        switch (type)
        {
            case "GoTo":
                return new PdfAction(PdfActionKind.GoTo, type) { Destination = ResolveDestination(a["D"]) };
            case "GoToR":
            {
                // A remote destination names a page of the other file by number; it is never a page here.
                var d = _r.Resolve(a["D"]) is PdfArray remote ? Explicit(remote, local: false) : null;
                return new PdfAction(PdfActionKind.GoToR, type) { Destination = d, FilePath = FileName(a["F"]) };
            }
            case "GoToE":
                return new PdfAction(PdfActionKind.GoToE, type) { FilePath = FileName(a["F"]) };
            case "Launch":
                return new PdfAction(PdfActionKind.Launch, type) { FilePath = FileName(a["F"]) ?? FileName((_r.Resolve(a["Win"]) as PdfDictionary)?["F"]) };
            case "URI":
                return new PdfAction(PdfActionKind.Uri, type) { Uri = Uri(a) };
            case "Named":
                return new PdfAction(PdfActionKind.Named, type) { Name = a.GetName("N") };
            case "JavaScript":
                return new PdfAction(PdfActionKind.JavaScript, type);
            default:
                return new PdfAction(type.Length == 0 ? PdfActionKind.Unsupported : PdfActionKind.Other, type);
        }
    }

    /// <summary>
    /// The /URI string as bytes (7-bit ASCII by definition), prefixed with the catalog's
    /// /URI /Base when it has no scheme, as PDFium's CPDF_Action::GetURI does.
    /// </summary>
    private string? Uri(PdfDictionary action)
    {
        string uri = _r.Resolve(action["URI"]) is PdfString s ? System.Text.Encoding.Latin1.GetString(s.RawBytes.Span) : string.Empty;
        int colon = uri.IndexOf(':');
        if (colon <= 0 && _r.Resolve(_doc.Catalog["URI"]) is PdfDictionary uriDict && _r.Resolve(uriDict["Base"]) is PdfString b)
            uri = System.Text.Encoding.Latin1.GetString(b.RawBytes.Span) + uri;
        return uri;
    }

    /// <summary>
    /// A file specification's name (7.11): a string, or /UF, /F, /Unix, /Mac, /DOS of a dictionary,
    /// in Windows form as PDFium's CPDF_FileSpec::GetFileName gives it ("/c/dir/f" is "c:\dir\f").
    /// </summary>
    public string? FileName(PdfObject? spec)
    {
        string? raw = null;
        switch (_r.Resolve(spec))
        {
            case PdfString s:
                raw = PdfTextStrings.Decode(s.RawBytes.Span);
                break;
            case PdfDictionary d:
                foreach (var key in new[] { "UF", "F", "DOS", "Mac", "Unix" })
                {
                    if (_r.Resolve(d[key]) is PdfString v && v.RawBytes.Length > 0)
                    {
                        raw = PdfTextStrings.Decode(v.RawBytes.Span);
                        if (raw.Length > 0) break;
                    }
                }
                if (raw != null && d.GetName("FS") == "URL") return raw; // a URL is not a file name
                break;
        }
        return raw == null ? null : PlatformFileName(raw);
    }

    /// <summary>The 7.11.2 file name syntax in Windows form (CPDF_FileSpec::DecodeFileName).</summary>
    internal static string PlatformFileName(string path)
    {
        if (path.Length <= 1) return string.Empty;
        static string Slashes(string s) => s.Replace('/', '\\');
        if (path[0] != '/') return Slashes(path);
        if (path[1] == '/') return Slashes(path[1..]);
        if (path.Length > 2 && path[2] == '/') return path[1] + ":" + Slashes(path[2..]);
        return "\\" + Slashes(path);
    }
}
