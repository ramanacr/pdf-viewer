using PdfEngine.Documents;
using PdfEngine.Safety;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Reports what a document carries that could act on the user's machine, from the engine's own
/// parse (object streams included), with the report <c>PdfiumSafetyInspector</c> gives: document
/// JavaScript (the /JavaScript name tree), embedded files (the /EmbeddedFiles name tree), the
/// security handler revision, and URI and launch actions of links on the first 50 pages.
/// </summary>
/// <remarks>
/// It also finds what PDFium's public API does not show, so its counts are never lower: scripts
/// and launch actions in the open action, in document, page, field and annotation event actions
/// (/AA), in the actions of form fields and other annotations, and along /Next chains. Nothing
/// found here is ever executed; the inspector only reports. Details are capped per finding.
/// </remarks>
public sealed class PdfNativeSafetyInspector : IPdfSafetyInspector
{
    /// <summary>Cap on the examples kept per finding; a hostile document can hold thousands.</summary>
    internal const int MaxDetailsPerFinding = 10;

    /// <summary>Pages scanned for page-level actions. Bounds the cost on very large documents.</summary>
    internal const int MaxPagesScanned = 50;

    public ValueTask<DocumentSafetyReport> InspectAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        return ValueTask.FromResult(Inspect(doc.Vector, cancellationToken));
    }

    public static DocumentSafetyReport Inspect(PdfVectorDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scan = PdfActiveContentScan.Run(document, MaxPagesScanned, cancellationToken);
        var findings = new List<DocumentRiskFinding>();

        int scripts = scan.NamedScripts.Count + scan.OtherScripts.Count;
        if (scripts > 0)
        {
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.JavaScript,
                Severity = RiskSeverity.Elevated,
                Count = scripts,
                Description = scripts == 1
                    ? "This document contains an embedded script. It has not been run."
                    : $"This document contains {scripts} embedded scripts. They have not been run.",
                Details = scan.NamedScripts.Select((n, i) => string.IsNullOrWhiteSpace(n) ? $"(unnamed script {i + 1})" : n)
                    .Concat(scan.OtherScripts).Take(MaxDetailsPerFinding).ToList(),
            });
        }

        if (scan.EmbeddedFiles.Count > 0)
        {
            int count = scan.EmbeddedFiles.Count;
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.EmbeddedFile,
                Severity = RiskSeverity.Elevated,
                Count = count,
                Description = count == 1 ? "This document carries an embedded file." : $"This document carries {count} embedded files.",
                Details = scan.EmbeddedFiles.Where(n => n.Length > 0).Take(MaxDetailsPerFinding).ToList(),
            });
        }

        if (document.Security is { } security)
        {
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.Encryption,
                Severity = RiskSeverity.Informational,
                Count = 1,
                Description = $"This document is encrypted (security handler revision {security.Revision}).",
            });
        }

        int launches = scan.LinkLaunches.Count + scan.OtherLaunches.Count;
        if (launches > 0)
        {
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.LaunchAction,
                Severity = RiskSeverity.Elevated,
                Count = launches,
                Description = launches == 1
                    ? "This document asks to launch an external program. It has not been run."
                    : $"This document contains {launches} requests to launch external programs. None have been run.",
                Details = scan.LinkLaunches.Concat(scan.OtherLaunches).Take(MaxDetailsPerFinding).ToList(),
            });
        }

        if (scan.Uris.Count > 0)
        {
            int count = scan.Uris.Count;
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.ExternalLink,
                Severity = RiskSeverity.Informational,
                Count = count,
                Description = count == 1 ? "This document contains a link to an external address." : $"This document contains {count} links to external addresses.",
                Details = scan.Uris.Where(u => u.Length > 0).Take(MaxDetailsPerFinding).ToList(),
            });
        }

        return new DocumentSafetyReport
        {
            Findings = findings,
            InspectionWasLimited = document.PageCount > MaxPagesScanned,
            LimitationReason = document.PageCount > MaxPagesScanned
                ? $"Link and launch actions were checked on the first {MaxPagesScanned} of {document.PageCount} pages."
                : string.Empty,
        };
    }
}

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

/// <summary>
/// Writes a copy of a document with its executable content removed, as <c>PdfiumSanitizer</c>
/// does, without PDFium: only the pages are carried into a new document (no outline, form,
/// name trees, open action or document actions), then annotations that launch, reach into
/// another file, or run an action of another kind are removed with media and file attachment
/// annotations, and every event action (/AA) and form field action goes too. The document's
/// properties and XMP metadata are left behind, and only objects the pages still use are
/// written, so nothing removed stays in the file unreferenced.
/// </summary>
/// <remarks>
/// The copy is re-opened and inspected before it is handed back; a copy that still reports
/// elevated risk is deleted and the run fails. Hidden optional content (layers) is kept as it
/// is, hidden.
/// </remarks>
public sealed class PdfNativeSanitizer : IPdfSanitizer
{
    private readonly IPdfSafetyInspector _inspector;

    public PdfNativeSanitizer() : this(new PdfNativeSafetyInspector()) { }

    public PdfNativeSanitizer(IPdfSafetyInspector inspector) => _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    private static readonly HashSet<string> PayloadSubtypes = new(StringComparer.Ordinal)
    {
        "FileAttachment", "Sound", "Movie", "Screen", "3D", "RichMedia",
    };

    public async ValueTask<SanitizationResult> SanitizeAsync(IPdfDocument document, string targetPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        if (!string.IsNullOrEmpty(doc.FilePath) &&
            string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(doc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSanitizationException("A sanitized copy cannot overwrite the original. The original is always kept.");
        cancellationToken.ThrowIfCancellationRequested();

        var (bytes, result) = Sanitize(doc.Vector, cancellationToken);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new PdfSanitizationException("Failed writing the sanitized copy to disk.", ex);
        }
        await VerifyOutputIsCleanAsync(targetPath, cancellationToken).ConfigureAwait(false);
        return result with { OutputPath = targetPath };
    }

    /// <summary>The clean copy's bytes and what was removed (<see cref="SanitizationResult.OutputPath"/> left empty).</summary>
    public static (byte[] Bytes, SanitizationResult Result) Sanitize(PdfVectorDocument source, CancellationToken cancellationToken = default)
    {
        var r = source.Resolver;
        var changes = new List<SanitizationChange>();
        var sideEffects = new List<string>();
        // Document level only: field and page actions are counted where they are removed, below.
        var scan = PdfActiveContentScan.Run(source, 0, cancellationToken, includeFields: false);

        int scripts = scan.NamedScripts.Count + scan.OtherScripts.Count;
        if (scripts > 0)
            changes.Add(Change(SanitizedContentKind.DocumentScript, scripts, "Removed 1 document-level script.", $"Removed {scripts} document-level scripts."));
        if (scan.EmbeddedFiles.Count > 0)
            changes.Add(Change(SanitizedContentKind.EmbeddedFile, scan.EmbeddedFiles.Count, "Removed 1 embedded file.", $"Removed {scan.EmbeddedFiles.Count} embedded files."));

        if (r.Resolve(source.Catalog["Outlines"]) is PdfDictionary outlines && outlines["First"] != null)
            sideEffects.Add("Bookmarks are not carried into the sanitized copy.");
        if (source.Security != null)
            sideEffects.Add("The copy is not encrypted; the original document's password and permissions do not apply to it.");
        var properties = PdfDocumentInfo.ReadProperties(source);
        if (!string.IsNullOrWhiteSpace(properties.Title) || !string.IsNullOrWhiteSpace(properties.Author))
            sideEffects.Add("Document properties such as title and author are not carried across.");
        if (source.Catalog["Metadata"] != null)
            sideEffects.Add("The document's XMP metadata is not carried across.");

        // 1. Form fields are drawn into their pages first, so they stay visible as they do in
        //    PDFium's copy (the form itself is not carried). An encrypted source is decrypted for it.
        PdfVectorDocument pageSource = source;
        PdfVectorDocument? flattened = null;
        bool hadWidgets = PdfAcroForm.Read(source) is { } form && form.Fields.Any(f => f.Widgets.Count > 0);
        if (hadWidgets)
        {
            byte[] plain = source.Security != null ? PdfDecryptedCopyWriter.Write(source) : PdfFullWriter.Rewrite(source);
            using (var decrypted = PdfVectorDocument.OpenAsync(plain, "Sanitized.pdf").AsTask().GetAwaiter().GetResult())
                flattened = PdfVectorDocument.OpenAsync(PdfNativeFormService.Flatten(decrypted, plain), "Sanitized.pdf").AsTask().GetAwaiter().GetResult();
            pageSource = flattened;
        }

        // 2. Only the pages, into a new document (FPDF_ImportPages into FPDF_CreateNewDocument).
        byte[] pagesOnly;
        try
        {
            pagesOnly = PdfPageAssembler.Build(
                Enumerable.Range(1, pageSource.PageCount).Select(n => new PdfAssemblyPage(pageSource, n)).ToList(),
                new PdfAssemblyOptions { KeepOutlines = false, KeepForms = false });
        }
        finally
        {
            flattened?.Dispose();
        }

        // 3. What survives page import, walked explicitly.
        using var copy = PdfVectorDocument.OpenAsync(pagesOnly, "Sanitized.pdf").AsTask().GetAwaiter().GetResult();
        var cr = copy.Resolver;
        var nav = new PdfNavigation(copy);
        var overrides = new Dictionary<int, PdfObject>();
        int launch = 0, remote = 0, unrecognized = 0, media = 0, triggered = 0;
        bool sawWidget = hadWidgets;

        // Why an annotation goes (null: it stays).
        SanitizedContentKind? Verdict(PdfDictionary annot)
        {
            string? subtype = annot.GetName("Subtype");
            if (subtype != null && PayloadSubtypes.Contains(subtype)) return SanitizedContentKind.EmbeddedMedia;
            if (subtype != "Link" || cr.Resolve(annot["A"]) is not PdfDictionary action) return null;
            return nav.ReadAction(action)?.Kind switch
            {
                PdfActionKind.GoTo or PdfActionKind.Uri => null,
                PdfActionKind.Launch => SanitizedContentKind.LaunchAction,
                PdfActionKind.GoToR or PdfActionKind.GoToE => SanitizedContentKind.RemoteAction,
                _ => SanitizedContentKind.UnrecognizedAction,
            };
        }

        // Annotations that go, by object number: what still points at them (a pop-up's /Parent,
        // a reply's /IRT) must not, or they would be written after all.
        var removed = new HashSet<int>();
        for (int p = 0; p < copy.PageCount; p++)
            if (cr.Resolve(copy.PageTree.Pages[p].Dictionary["Annots"]) is PdfArray list)
                foreach (var item in list)
                    if (item is PdfIndirectRef ir && cr.Resolve(ir) is PdfDictionary d && Verdict(d) != null) removed.Add(ir.ObjectNumber);

        // The annotation without event actions (and, but for links, without its own action and
        // its field); null when nothing had to go.
        PdfDictionary? Clean(PdfDictionary annot)
        {
            var e = new Dictionary<string, PdfObject>(annot.Entries);
            bool changed = false;
            if (e.Remove("AA")) { triggered++; changed = true; }
            if (annot.GetName("Subtype") == "Link")
            {
                if (cr.Resolve(annot["A"]) is PdfDictionary a && a["Next"] != null)
                {
                    e["A"] = PdfUpdate.With(a, ("Next", null)); // only the link's own action stays
                    changed = true;
                }
            }
            else
            {
                if (e.Remove("A")) { triggered++; changed = true; }
                // A widget's field (its /Parent) is not carried: the copy has no form; nor is
                // the parent of a pop-up whose annotation went.
                bool popup = annot.GetName("Subtype") == "Popup";
                if ((!popup || annot["Parent"] is PdfIndirectRef { ObjectNumber: var pn } && removed.Contains(pn)) && e.Remove("Parent"))
                    changed = true;
            }
            if (annot["IRT"] is PdfIndirectRef { ObjectNumber: var irt } && removed.Contains(irt) && e.Remove("IRT")) changed = true;
            return changed ? new PdfDictionary(e) : null;
        }

        for (int p = 0; p < copy.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pageNumber = copy.PageTree.PageObjectNumbers[p];
            var page = copy.PageTree.Pages[p].Dictionary;
            var pageEntries = new Dictionary<string, PdfObject>(page.Entries);
            bool pageChanged = pageEntries.Remove("AA");
            if (pageChanged) triggered++;

            if (cr.Resolve(page["Annots"]) is PdfArray annots)
            {
                var kept = new List<PdfObject>();
                foreach (var item in annots)
                {
                    if (cr.Resolve(item) is not PdfDictionary annot) { pageChanged = true; continue; }
                    if (annot.GetName("Subtype") == "Widget") sawWidget = true;
                    switch (Verdict(annot))
                    {
                        case SanitizedContentKind.EmbeddedMedia: media++; pageChanged = true; continue;
                        case SanitizedContentKind.LaunchAction: launch++; pageChanged = true; continue;
                        case SanitizedContentKind.RemoteAction: remote++; pageChanged = true; continue;
                        case SanitizedContentKind.UnrecognizedAction: unrecognized++; pageChanged = true; continue;
                    }
                    if (Clean(annot) is { } cleaned)
                    {
                        if (item is PdfIndirectRef ir) overrides[ir.ObjectNumber] = cleaned;
                        else { kept.Add(cleaned); pageChanged = true; continue; }
                    }
                    kept.Add(item);
                }
                if (kept.Count != annots.Count) pageChanged = true;
                if (pageChanged)
                {
                    if (kept.Count > 0) pageEntries["Annots"] = new PdfArray(kept);
                    else pageEntries.Remove("Annots");
                }
            }
            if (pageChanged && pageNumber > 0) overrides[pageNumber] = new PdfDictionary(pageEntries);
        }

        // The catalog keeps only what pages need (layers, viewer preferences, language).
        if (copy.XrefTable.Trailer?["Root"] is PdfIndirectRef root)
        {
            var catalog = new Dictionary<string, PdfObject>(copy.Catalog.Entries);
            foreach (var key in new[] { "Metadata", "AA", "OpenAction", "Names", "AcroForm", "Outlines", "URI", "Dests", "StructTreeRoot", "MarkInfo" })
                catalog.Remove(key);
            overrides[root.ObjectNumber] = new PdfDictionary(catalog);
        }

        if (launch > 0) changes.Add(Change(SanitizedContentKind.LaunchAction, launch, "Removed 1 action that would launch an external program.", $"Removed {launch} actions that would launch external programs."));
        if (remote > 0) changes.Add(Change(SanitizedContentKind.RemoteAction, remote, "Removed 1 action that reached into another file.", $"Removed {remote} actions that reached into other files."));
        if (unrecognized > 0) changes.Add(Change(SanitizedContentKind.UnrecognizedAction, unrecognized, "Removed 1 action of an unrecognised type.", $"Removed {unrecognized} actions of unrecognised types."));
        if (media > 0) changes.Add(Change(SanitizedContentKind.EmbeddedMedia, media, "Removed 1 embedded media or file annotation.", $"Removed {media} embedded media or file annotations."));
        if (triggered > 0) changes.Add(Change(SanitizedContentKind.TriggeredAction, triggered, "Removed 1 page, form field or annotation action.", $"Removed {triggered} page, form field and annotation actions."));
        if (sawWidget)
            sideEffects.Add("Form fields remain visible but are no longer fillable, because the document's form definition is not carried across.");

        // 4. Written whole from the catalog only (no /Info): unreferenced objects are left behind.
        byte[] clean = Redaction.PdfFullWriter.Write(copy, overrides, dropInfo: true);
        if (copy.PageCount <= 0)
            throw new PdfSanitizationException("The sanitized copy came out with no pages.");
        return (clean, new SanitizationResult { PageCount = copy.PageCount, Changes = changes, SideEffects = sideEffects });
    }

    private static SanitizationChange Change(SanitizedContentKind kind, int count, string one, string many) =>
        new() { Kind = kind, Count = count, Description = count == 1 ? one : many };

    /// <summary>Re-opens the written copy and inspects it; a copy that is not clean is deleted and the run fails.</summary>
    private async ValueTask VerifyOutputIsCleanAsync(string targetPath, CancellationToken cancellationToken)
    {
        DocumentSafetyReport verification;
        try
        {
            await using var written = await PdfNativeDocument.OpenAsync(targetPath, null, cancellationToken).ConfigureAwait(false);
            verification = await _inspector.InspectAsync(written, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(targetPath);
            throw new PdfSanitizationException("The sanitized copy could not be re-opened for verification, so it was discarded.", ex);
        }
        if (verification.IsClean) return;
        TryDelete(targetPath);
        string survived = string.Join("; ", verification.Findings.Where(f => f.Severity == RiskSeverity.Elevated).Select(f => f.Description));
        throw new PdfSanitizationException("The sanitized copy still contained active content and was discarded: " + survived);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reporting the sanitization failure matters more than the leftover file.
        }
    }
}
