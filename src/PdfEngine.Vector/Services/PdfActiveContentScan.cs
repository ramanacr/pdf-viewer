using PdfEngine.Documents;
using PdfEngine.Safety;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>What one pass over a document's actions found (shared by the inspector and the sanitizer).</summary>
internal sealed class PdfActiveContentScan
{
    /// <summary>Keys of the /JavaScript name tree (what FPDFDoc_GetJavaScriptActionCount counts).</summary>
    public List<string> NamedScripts { get; } = new();
    /// <summary>Scripts outside the name tree: open action, event actions, field and annotation actions.</summary>
    public List<string> OtherScripts { get; } = new();
    /// <summary>File names of the /EmbeddedFiles name tree (what FPDFDoc_GetAttachmentCount counts).</summary>
    public List<string> EmbeddedFiles { get; } = new();
    /// <summary>Launch actions of links (what PDFium reports), one entry per link.</summary>
    public List<string> LinkLaunches { get; } = new();
    /// <summary>Launch actions anywhere else.</summary>
    public List<string> OtherLaunches { get; } = new();
    /// <summary>URI targets of links.</summary>
    public List<string> Uris { get; } = new();

    /// <param name="maxPages">Pages whose page and annotation actions are scanned (0: document level only).</param>
    /// <param name="includeFields">Scan the event actions of the form's field dictionaries.</param>
    public static PdfActiveContentScan Run(PdfVectorDocument document, int maxPages, CancellationToken ct, bool includeFields = true)
    {
        var scan = new PdfActiveContentScan();
        var r = document.Resolver;
        var nav = new PdfNavigation(document);
        var catalog = document.Catalog;
        var names = r.Resolve(catalog["Names"]) as PdfDictionary;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        if (names != null)
        {
            foreach (var (key, value) in PdfNameTree.Enumerate(names["JavaScript"], r))
            {
                scan.NamedScripts.Add(key);
                if (r.Resolve(value) is PdfDictionary a) seen.Add(a);
            }
            foreach (var (key, value) in PdfNameTree.Enumerate(names["EmbeddedFiles"], r))
                scan.EmbeddedFiles.Add(nav.FileName(value) is { Length: > 0 } n ? n : key);
        }

        // Document-level actions: the open action and the catalog's event actions.
        scan.Actions(catalog["OpenAction"], r, seen, "Script run when the document opens", "Launch action when the document opens");
        scan.EventActions(catalog["AA"], r, seen, "Script on a document event", "Launch action on a document event");

        // Field event actions (the field dictionaries above the widgets).
        if (includeFields && r.Resolve(catalog["AcroForm"]) is PdfDictionary acro)
            scan.FieldActions(r.Resolve(acro["Fields"]) as PdfArray, r, seen, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);

        int pages = Math.Min(document.PageCount, maxPages);
        for (int p = 0; p < pages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = document.PageTree.Pages[p].Dictionary;
            scan.EventActions(page["AA"], r, seen, $"Script on a page event (page {p + 1})", $"Launch action on a page event (page {p + 1})");
            if (r.Resolve(page["Annots"]) is not PdfArray annots) continue;
            foreach (var item in annots)
            {
                if (r.Resolve(item) is not PdfDictionary annot) continue;
                string? subtype = annot.GetName("Subtype");
                if (subtype == "Link" && r.Resolve(annot["A"]) is PdfDictionary linkAction)
                {
                    // What PDFium reports: the link's own action, by its type.
                    switch (nav.ReadAction(linkAction)?.Kind)
                    {
                        case PdfActionKind.Uri:
                            scan.Uris.Add(nav.ReadAction(linkAction)?.Uri ?? string.Empty);
                            break;
                        case PdfActionKind.Launch:
                            scan.LinkLaunches.Add($"Launch action on page {p + 1}");
                            break;
                        case PdfActionKind.JavaScript:
                            if (seen.Add(linkAction)) scan.OtherScripts.Add($"Script in a link (page {p + 1})");
                            break;
                    }
                    seen.Add(linkAction);
                    scan.Actions(linkAction["Next"], r, seen, $"Script in a link (page {p + 1})", $"Launch action in a link (page {p + 1})");
                }
                else
                {
                    scan.Actions(annot["A"], r, seen, $"Script in a form field or annotation (page {p + 1})", $"Launch action in an annotation (page {p + 1})");
                }
                scan.EventActions(annot["AA"], r, seen, $"Script in a form field or annotation (page {p + 1})", $"Launch action in an annotation (page {p + 1})");
            }
        }
        return scan;
    }

    private void FieldActions(PdfArray? fields, PdfObjectResolver r, HashSet<PdfDictionary> seen, HashSet<PdfDictionary> visited, int depth)
    {
        if (fields == null || depth > 32) return;
        foreach (var f in fields)
        {
            if (r.Resolve(f) is not PdfDictionary field || !visited.Add(field)) continue;
            EventActions(field["AA"], r, seen, "Script in a form field", "Launch action in a form field");
            FieldActions(r.Resolve(field["Kids"]) as PdfArray, r, seen, visited, depth + 1);
        }
    }

    private void EventActions(PdfObject? aa, PdfObjectResolver r, HashSet<PdfDictionary> seen, string script, string launch)
    {
        if (r.Resolve(aa) is not PdfDictionary events) return;
        foreach (var value in events.Entries.Values) Actions(value, r, seen, script, launch);
    }

    /// <summary>An action and its /Next chain (a dictionary or an array of them).</summary>
    private void Actions(PdfObject? action, PdfObjectResolver r, HashSet<PdfDictionary> seen, string script, string launch, int depth = 0)
    {
        if (depth > 32) return;
        switch (r.Resolve(action))
        {
            case PdfArray chain:
                foreach (var a in chain) Actions(a, r, seen, script, launch, depth + 1);
                return;
            case PdfDictionary a:
                if (!seen.Add(a)) return;
                switch (a.GetName("S"))
                {
                    case "JavaScript": OtherScripts.Add(script); break;
                    case "Launch": OtherLaunches.Add(launch); break;
                }
                Actions(a["Next"], r, seen, script, launch, depth + 1);
                return;
        }
    }
}
