using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.PdfA.Xmp;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Accessibility;

/// <summary>
/// Checks a document against PDF/UA-1 (ISO 14289-1) as far as a machine can: the Matterhorn
/// Protocol's machine-checkable failure conditions, from tagging and role mapping to alternate
/// text, tables, lists, headings, languages, fonts, annotations and forms. Each issue carries its
/// Matterhorn checkpoint and ISO 14289-1 clause (numbered as veraPDF's PDF/UA-1 profile numbers
/// them), and the page, object and structure element it is about. Only reads the document.
/// </summary>
public static class PdfUaValidator
{
    public static PdfUaReport Validate(PdfVectorDocument document, byte[] file)
    {
        var checker = new PdfUaChecker(document, file);
        checker.Run();
        return new PdfUaReport { Issues = checker.Issues, ClaimsPdfUa = checker.ClaimsPdfUa };
    }

    /// <summary>Whether the XMP metadata claims PDF/UA (pdfuaid:part), without checking anything else.</summary>
    public static bool ClaimsPdfUa(PdfVectorDocument document) =>
        PdfAValidator.ReadMetadata(document) is { Error: null } xmp && xmp.Simple(PdfUaChecker.PdfUaIdNs, "part") != null;

    /// <summary>A BCP 47 language tag as PDF/UA checks it: a 1 to 8 letter primary subtag, then 1 to 8 letter or digit subtags.</summary>
    public static bool IsValidLanguageTag(string? tag) =>
        !string.IsNullOrEmpty(tag) && Regex.IsMatch(tag, "^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant);
}

/// <summary>The checks, split by topic over partial files.</summary>
internal sealed partial class PdfUaChecker
{
    public const string PdfUaIdNs = "http://www.aiim.org/pdfua/ns/id/";
    private const int MaxPerCheckpoint = 50;

    private readonly PdfVectorDocument _doc;
    private readonly byte[] _file;
    private readonly PdfObjectResolver _r;
    private readonly PdfStreamDecoder _decoder;
    private readonly Dictionary<string, int> _perCheckpoint = new(StringComparer.Ordinal);
    private readonly PdfDictionary? _catalog;
    private readonly PdfDictionary _trailer;

    /// <summary>Page object number → 0-based page index.</summary>
    private readonly Dictionary<int, int> _pageIndex = new();

    /// <summary>The catalog's /Lang, when present and not empty.</summary>
    private string? _documentLang;

    public List<PdfUaIssue> Issues { get; } = new();
    public bool ClaimsPdfUa { get; private set; }

    public PdfUaChecker(PdfVectorDocument doc, byte[] file)
    {
        _doc = doc;
        _file = file;
        _r = doc.Resolver;
        _decoder = new PdfStreamDecoder(null, _r.Resolve);
        _trailer = doc.XrefTable.Trailer ?? new PdfDictionary(new Dictionary<string, PdfObject>());
        _catalog = _r.Resolve(_trailer["Root"]) as PdfDictionary;
    }

    public void Run()
    {
        Guard(IndexPages);
        Guard(BuildStructureTree);
        Guard(CheckDocument);
        Guard(CheckSecurity);
        Guard(CheckOptionalContent);
        Guard(CheckEmbeddedFiles);
        Guard(CheckXfa);
        Guard(CheckContent);
        Guard(CheckStructure);
        Guard(CheckAnnotations);
        Guard(CheckMediaClips);
        Guard(CheckFonts);
    }

    /// <summary>A check that cannot be evaluated (a broken structure) is reported, not thrown.</summary>
    private void Guard(Action check)
    {
        try { check(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Report("XX-000", "7.1", PdfUaCategory.Document, $"Part of the document could not be checked ({ex.GetType().Name}).");
        }
    }

    private void Report(string checkpoint, string clause, PdfUaCategory category, string message, int? page = null, int? obj = null,
        StructElem? elem = null, PdfRect? bounds = null, PdfUaSeverity severity = PdfUaSeverity.Error)
    {
        _perCheckpoint.TryGetValue(checkpoint, out int n);
        _perCheckpoint[checkpoint] = n + 1;
        if (n >= MaxPerCheckpoint) return;
        if (elem != null)
        {
            page ??= elem.PageIndex is { } pi ? pi + 1 : null;
            obj ??= elem.ObjectNumber;
            bounds ??= ElementBounds(elem, page);
        }
        PdfRect? normalized = null;
        if (bounds is { } b && page is { } p && p >= 1 && p <= _doc.PageTree.Count)
        {
            var crop = _doc.PageTree.Pages[p - 1].CropBox;
            if (crop.Width > 0 && crop.Height > 0)
            {
                double x = Math.Clamp((b.X - crop.X) / crop.Width, 0, 1), y = Math.Clamp(1 - (b.Y + b.Height - crop.Y) / crop.Height, 0, 1);
                double w = Math.Max(0.005, Math.Min(b.Width / crop.Width, 1 - x)), h = Math.Max(0.005, Math.Min(b.Height / crop.Height, 1 - y));
                normalized = new PdfRect(x, y, w, h);
            }
        }
        Issues.Add(new PdfUaIssue(checkpoint, clause, category, message)
        {
            Severity = severity, Page = page, ObjectNumber = obj, StructurePath = elem?.Path, Bounds = bounds, NormalizedBounds = normalized,
        });
    }

    private byte[]? Decode(PdfStream stream)
    {
        try { return _decoder.DecodeStream(stream); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static string? Text(PdfObject? o) => o is PdfString s ? s.AsDecodedString() : null;

    private string? TextOf(PdfDictionary d, string key) => Text(_r.Resolve(d[key]));

    private IEnumerable<(int Number, PdfObject Object)> AllObjects()
    {
        foreach (var (number, entry) in _doc.XrefTable.Entries.OrderBy(e => e.Key))
        {
            if (!entry.IsInUse || number == 0) continue;
            PdfObject? obj;
            try { obj = _r.Resolve(number); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (obj != null) yield return (number, obj);
        }
    }

    /// <summary>Numbers the pages by walking /Pages in the order the page tree loads them.</summary>
    private void IndexPages()
    {
        if (_catalog == null) return;
        int index = 0;
        var seen = new HashSet<int>();
        void Walk(PdfObject? node, int depth)
        {
            if (depth > 64) return;
            int? number = node is PdfIndirectRef r ? r.ObjectNumber : null;
            if (number is { } n && !seen.Add(n)) return;
            if (_r.Resolve(node) is not PdfDictionary d) return;
            if (d.GetName("Type") == "Pages" || (d.GetName("Type") == null && d.ContainsKey("Kids")))
            {
                if (_r.Resolve(d["Kids"]) is PdfArray kids) foreach (var k in kids) Walk(k, depth + 1);
                return;
            }
            if (number is { } pn) _pageIndex[pn] = index;
            index++;
        }
        Walk(_catalog["Pages"], 0);
    }

    // ------------------------------------------------------------------ document (5, 7.1, 7.2)

    private void CheckDocument()
    {
        const PdfUaCategory doc = PdfUaCategory.Document;
        if (_catalog == null)
        {
            Report("XX-000", "7.1", doc, "The document has no catalog.");
            return;
        }
        var markInfo = _r.Resolve(_catalog["MarkInfo"]) as PdfDictionary;
        if (_r.Resolve(markInfo?["Marked"]) is not PdfBoolean { Value: true })
            Report("01-005", "7.1", doc, "The document is not marked as tagged (MarkInfo /Marked is not true).");
        if (_r.Resolve(markInfo?["Suspects"]) is PdfBoolean { Value: true })
            Report("01-007", "7.1", doc, "The tags are marked as suspect (MarkInfo /Suspects is true).");
        if (_r.Resolve(_catalog["StructTreeRoot"]) is not PdfDictionary)
            Report("01-005", "7.1", doc, "The document has no structure tree (StructTreeRoot), so none of its content is tagged.");

        var prefs = _r.Resolve(_catalog["ViewerPreferences"]) as PdfDictionary;
        switch (_r.Resolve(prefs?["DisplayDocTitle"]))
        {
            case PdfBoolean { Value: true }: break;
            case PdfBoolean: Report("07-002", "7.1", doc, "The window title shows the file name: ViewerPreferences /DisplayDocTitle is false."); break;
            default: Report("07-001", "7.1", doc, "ViewerPreferences has no /DisplayDocTitle, so the window title shows the file name."); break;
        }

        string? lang = TextOf(_catalog, "Lang");
        if (lang != null)
        {
            if (!PdfUaValidator.IsValidLanguageTag(lang)) Report("11-001", "7.2", doc, "The document language (catalog /Lang) is not a valid language tag.");
            if (lang.Length > 0) _documentLang = lang;
        }
        else Report("11-001", "7.2", doc, "The document has no language (catalog /Lang); every element must then give its own.", severity: PdfUaSeverity.Warning);

        CheckMetadata();
    }

    private void CheckMetadata()
    {
        const PdfUaCategory doc = PdfUaCategory.Document;
        if (_r.Resolve(_catalog!["Metadata"]) is not PdfStream stream || Decode(stream) is not { } data)
        {
            Report("06-001", "7.1", doc, "The document has no XMP metadata stream.");
            Report("06-002", "5", doc, "The metadata does not identify the document as PDF/UA (pdfuaid:part).");
            return;
        }
        var xmp = XmpPacket.Parse(data);
        if (xmp.Error != null)
        {
            Report("06-001", "7.1", doc, "The XMP metadata cannot be read.");
            return;
        }
        var part = xmp.Get(PdfUaIdNs, "part");
        if (part == null) Report("06-002", "5", doc, "The metadata does not identify the document as PDF/UA (pdfuaid:part is missing).");
        else
        {
            ClaimsPdfUa = true;
            if (part.Value.Text.Trim() != "1") Report("06-002", "5", doc, $"The metadata claims PDF/UA part {Short(part.Value.Text.Trim())}, not 1.");
        }
        foreach (var name in new[] { "part", "amd", "corr" })
            if (xmp.Get(PdfUaIdNs, name) is { } p && p.Prefix != "pdfuaid")
                Report("06-002", "5", doc, $"The PDF/UA identification property {name} uses the prefix \"{Short(p.Prefix)}\" instead of \"pdfuaid\".");

        var title = xmp.Get(XmpSchemas.Dc, "title");
        var titles = title == null ? new List<XmpValue>() : title.Value.Kind == XmpKind.Simple ? new List<XmpValue> { title.Value } : title.Value.Items;
        if (!titles.Any(t => t.Text.Trim().Length > 0)) Report("06-003", "7.1", doc, "The metadata has no title (dc:title).");
        // A title in the default language takes the document's; one with its own xml:lang has it.
        else if (_catalog!["Lang"] == null && titles.All(t => t.Lang is null or "x-default"))
            Report("11-006", "7.2", doc, "The language of the document title cannot be determined: dc:title has no xml:lang and the catalog has no /Lang.");
    }

    private static string Short(string s) => s.Length > 16 ? s[..16] + "..." : s;

    // ------------------------------------------------------------------ security (7.16), optional content (7.10), embedded files (7.11), XFA (7.15)

    private void CheckSecurity()
    {
        if (_r.Resolve(_trailer["Encrypt"]) is not PdfDictionary encrypt) return;
        if (_r.Resolve(encrypt["P"]) is not { } p || !p.TryGetInteger(out long bits))
            Report("26-001", "7.16", PdfUaCategory.Document, "The document is encrypted but its encryption dictionary has no permissions (/P).");
        else if ((bits & (1 << 9)) == 0)
            Report("26-002", "7.16", PdfUaCategory.Document, "The encryption forbids extracting text for accessibility (permission bit 10 is off), so screen readers cannot read it.");
    }

    private void CheckOptionalContent()
    {
        if (_catalog == null || _r.Resolve(_catalog["OCProperties"]) is not PdfDictionary oc) return;
        const PdfUaCategory doc = PdfUaCategory.Document;
        if (_r.Resolve(oc["D"]) is PdfDictionary d)
        {
            if (string.IsNullOrEmpty(TextOf(d, "Name"))) Report("20-002", "7.10", doc, "The default optional content configuration has no name.");
            if (d.ContainsKey("AS")) Report("20-003", "7.10", doc, "The default optional content configuration has an /AS entry.");
        }
        if (_r.Resolve(oc["Configs"]) is PdfArray configs)
            foreach (var c in configs.Items.Select(_r.Resolve).OfType<PdfDictionary>())
            {
                if (string.IsNullOrEmpty(TextOf(c, "Name"))) Report("20-001", "7.10", doc, "An optional content configuration has no name.");
                if (c.ContainsKey("AS")) Report("20-003", "7.10", doc, "An optional content configuration has an /AS entry.");
            }
    }

    private void CheckEmbeddedFiles()
    {
        foreach (var (number, obj) in AllObjects())
        {
            if (obj is not PdfDictionary spec || !spec.ContainsKey("EF")) continue;
            if (!spec.ContainsKey("F") || !spec.ContainsKey("UF"))
                Report("21-001", "7.11", PdfUaCategory.Document, $"An embedded file's specification lacks /{(spec.ContainsKey("F") ? "UF" : "F")}.", obj: number);
        }
    }

    private static readonly Regex DynamicRender = new(@"<dynamicRender[^>]*>\s*required\s*</dynamicRender\s*>",RegexOptions.CultureInvariant);

    private void CheckXfa()
    {
        if (_catalog == null || _r.Resolve(_catalog["AcroForm"]) is not PdfDictionary form || _r.Resolve(form["XFA"]) is not { } xfa) return;
        var streams = xfa is PdfStream s ? new[] { s } : (xfa as PdfArray)?.Items.Select(_r.Resolve).OfType<PdfStream>().ToArray() ?? Array.Empty<PdfStream>();
        bool dynamic = streams.Any(x => Decode(x) is { } data && DynamicRender.IsMatch(System.Text.Encoding.UTF8.GetString(data)));
        if (dynamic) Report("25-001", "7.15", PdfUaCategory.Forms, "The form is a dynamic XFA form (dynamicRender is required).");
        else Report("25-001", "7.15", PdfUaCategory.Forms, "The form carries XFA; assistive technology reads only the AcroForm fields.", severity: PdfUaSeverity.Warning);
    }
}
