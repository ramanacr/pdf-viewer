using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Pdfium.Native;
using PdfEngine.Safety;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Writes a copy of a document with its executable content removed.
///
/// The approach is subtractive by construction rather than by editing. PDFium's public API
/// cannot delete arbitrary catalog entries, so instead of trying to excise
/// /Names/JavaScript, /OpenAction and /Names/EmbeddedFiles from the original - and hoping
/// nothing was missed - a fresh document is created and only the pages are imported into it.
/// Everything that lives at the document level is left behind because it was never copied.
/// What survives page import is then walked explicitly: annotations that launch, reach
/// outside the document, or embed a payload are removed one by one.
///
/// The output is re-inspected before it is handed back. A copy that still carries elevated
/// risk is deleted rather than returned, because a "clean copy" the user cannot trust is
/// worse than not offering one.
/// </summary>
public sealed class PdfiumSanitizer : IPdfSanitizer
{
    private readonly IPdfSafetyInspector _inspector;

    public PdfiumSanitizer() : this(new PdfiumSafetyInspector()) { }

    public PdfiumSanitizer(IPdfSafetyInspector inspector)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    /// <summary>Annotation subtypes that carry or trigger a payload rather than marking up the page.</summary>
    private static readonly HashSet<int> PayloadAnnotationSubtypes = new()
    {
        PdfiumNativeBridge.FPDF_ANNOT_FILEATTACHMENT,
        PdfiumNativeBridge.FPDF_ANNOT_SOUND,
        PdfiumNativeBridge.FPDF_ANNOT_MOVIE,
        PdfiumNativeBridge.FPDF_ANNOT_SCREEN,
        PdfiumNativeBridge.FPDF_ANNOT_THREED,
        PdfiumNativeBridge.FPDF_ANNOT_RICHMEDIA
    };

    public async ValueTask<SanitizationResult> SanitizeAsync(
        IPdfDocument document,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(pdfiumDoc.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PdfSanitizationException(
                "A sanitized copy cannot overwrite the original. The original is always kept.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var changes = new List<SanitizationChange>();
        var sideEffects = new List<string>();
        int pageCount;

        lock (pdfiumDoc.SyncLock)
        {
            RecordDocumentLevelRemovals(pdfiumDoc, changes);
            RecordSideEffects(pdfiumDoc, sideEffects);

            using var stripped = PdfiumNativeBridge.FPDF_CreateNewDocument();
            if (stripped == null || stripped.IsInvalid)
                throw new PdfSanitizationException("Failed to create the destination document.");

            // A null page range imports every page, in order.
            if (PdfiumNativeBridge.FPDF_ImportPages(stripped, pdfiumDoc.Handle, null, 0) == 0)
                throw new PdfSanitizationException("Failed to import pages into the sanitized copy.");

            pageCount = PdfiumNativeBridge.FPDF_GetPageCount(stripped);
            if (pageCount <= 0)
                throw new PdfSanitizationException("The sanitized copy came out with no pages.");

            // Belt and braces: page import should not carry document-level attachments, but
            // this is the one thing worth confirming rather than assuming.
            DeleteAllAttachments(stripped);

            StripPageLevelActiveContent(stripped, pageCount, changes, sideEffects, cancellationToken);

            // Removing an annotation only unlinks it from the page's /Annots array. PDFium's
            // save writes out every object it holds, orphaned or not, so saving here would
            // leave "/S /Launch /F (calc.exe)" sitting in the file - inert, because nothing
            // references it, but plainly still there to anyone who reads the bytes or scans
            // the file. A copy the user cannot grep and trust is not a clean copy.
            //
            // Importing once more copies only what is reachable from the stripped pages, so
            // the unlinked objects are left behind rather than serialized.
            using var clean = PdfiumNativeBridge.FPDF_CreateNewDocument();
            if (clean == null || clean.IsInvalid)
                throw new PdfSanitizationException("Failed to create the final sanitized document.");

            if (PdfiumNativeBridge.FPDF_ImportPages(clean, stripped, null, 0) == 0)
                throw new PdfSanitizationException("Failed to compact the sanitized copy.");

            if (PdfiumNativeBridge.FPDF_GetPageCount(clean) != pageCount)
                throw new PdfSanitizationException("Pages were lost while compacting the sanitized copy.");

            SaveToDisk(clean, targetPath);
        }

        await VerifyOutputIsCleanAsync(targetPath, cancellationToken);

        return new SanitizationResult
        {
            OutputPath = targetPath,
            PageCount = pageCount,
            Changes = changes,
            SideEffects = sideEffects
        };
    }

    /// <summary>
    /// Counts what the source carries at the document level. These are removed by not being
    /// imported, so they are counted here rather than after the fact.
    /// </summary>
    private static void RecordDocumentLevelRemovals(PdfiumDocument doc, List<SanitizationChange> changes)
    {
        int scripts = PdfiumNativeBridge.FPDFDoc_GetJavaScriptActionCount(doc.Handle);
        if (scripts > 0)
        {
            changes.Add(new SanitizationChange
            {
                Kind = SanitizedContentKind.DocumentScript,
                Count = scripts,
                Description = scripts == 1
                    ? "Removed 1 document-level script."
                    : $"Removed {scripts} document-level scripts."
            });
        }

        int attachments = PdfiumNativeBridge.FPDFDoc_GetAttachmentCount(doc.Handle);
        if (attachments > 0)
        {
            changes.Add(new SanitizationChange
            {
                Kind = SanitizedContentKind.EmbeddedFile,
                Count = attachments,
                Description = attachments == 1
                    ? "Removed 1 embedded file."
                    : $"Removed {attachments} embedded files."
            });
        }
    }

    /// <summary>
    /// Names what the rebuild costs. None of these are threats; they are simply things a
    /// fresh document does not inherit, and the user should hear about them up front rather
    /// than discover them later.
    /// </summary>
    private static void RecordSideEffects(PdfiumDocument doc, List<string> sideEffects)
    {
        if (PdfiumNativeBridge.FPDFBookmark_GetFirstChild(doc.Handle, IntPtr.Zero) != IntPtr.Zero)
        {
            sideEffects.Add("Bookmarks are not carried into the sanitized copy.");
        }

        if (PdfiumNativeBridge.FPDF_GetSecurityHandlerRevision(doc.Handle) >= 0)
        {
            sideEffects.Add("The copy is not encrypted; the original document's password and permissions do not apply to it.");
        }

        if (!string.IsNullOrWhiteSpace(doc.Metadata.Title) || !string.IsNullOrWhiteSpace(doc.Metadata.Author))
        {
            sideEffects.Add("Document properties such as title and author are not carried across.");
        }
    }

    private static void DeleteAllAttachments(SafeDocumentHandle doc)
    {
        // Always delete index 0: each deletion shifts the rest down.
        for (int remaining = PdfiumNativeBridge.FPDFDoc_GetAttachmentCount(doc); remaining > 0; remaining--)
        {
            if (PdfiumNativeBridge.FPDFDoc_DeleteAttachment(doc, 0) == 0) break;
        }
    }

    /// <summary>
    /// Walks every imported page and removes the annotations that can act. Iteration runs
    /// backwards because removing an annotation renumbers the ones after it.
    /// </summary>
    private static void StripPageLevelActiveContent(
        SafeDocumentHandle doc,
        int pageCount,
        List<SanitizationChange> changes,
        List<string> sideEffects,
        CancellationToken cancellationToken)
    {
        int launch = 0, remote = 0, unrecognized = 0, media = 0;
        bool sawFormField = false;

        for (int p = 0; p < pageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var page = PdfiumNativeBridge.FPDF_LoadPage(doc, p);
            if (page == null || page.IsInvalid) continue;

            for (int i = PdfiumNativeBridge.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
            {
                var verdict = ClassifyAnnotation(page, i, ref sawFormField);
                if (verdict == AnnotationVerdict.Keep) continue;

                if (PdfiumNativeBridge.FPDFPage_RemoveAnnot(page, i) == 0) continue;

                switch (verdict)
                {
                    case AnnotationVerdict.RemoveLaunch: launch++; break;
                    case AnnotationVerdict.RemoveRemote: remote++; break;
                    case AnnotationVerdict.RemoveUnrecognized: unrecognized++; break;
                    case AnnotationVerdict.RemoveMedia: media++; break;
                }
            }
        }

        if (launch > 0)
        {
            changes.Add(new SanitizationChange
            {
                Kind = SanitizedContentKind.LaunchAction,
                Count = launch,
                Description = launch == 1
                    ? "Removed 1 action that would launch an external program."
                    : $"Removed {launch} actions that would launch external programs."
            });
        }

        if (remote > 0)
        {
            changes.Add(new SanitizationChange
            {
                Kind = SanitizedContentKind.RemoteAction,
                Count = remote,
                Description = remote == 1
                    ? "Removed 1 action that reached into another file."
                    : $"Removed {remote} actions that reached into other files."
            });
        }

        if (unrecognized > 0)
        {
            changes.Add(new SanitizationChange
            {
                Kind = SanitizedContentKind.UnrecognizedAction,
                Count = unrecognized,
                Description = unrecognized == 1
                    ? "Removed 1 action of an unrecognised type."
                    : $"Removed {unrecognized} actions of unrecognised types."
            });
        }

        if (media > 0)
        {
            changes.Add(new SanitizationChange
            {
                Kind = SanitizedContentKind.EmbeddedMedia,
                Count = media,
                Description = media == 1
                    ? "Removed 1 embedded media or file annotation."
                    : $"Removed {media} embedded media or file annotations."
            });
        }

        if (sawFormField)
        {
            sideEffects.Add("Form fields remain visible but are no longer fillable, because the document's form definition is not carried across.");
        }
    }

    private enum AnnotationVerdict
    {
        Keep,
        RemoveLaunch,
        RemoveRemote,
        RemoveUnrecognized,
        RemoveMedia
    }

    /// <summary>
    /// Decides whether one annotation may stay. Highlights, notes, ink and ordinary web
    /// links are kept: this reader never follows a link on its own, so a URI is information,
    /// not a capability. Anything that runs, reaches outside the file, or that PDFium cannot
    /// classify is removed - unrecognised is not the same as harmless.
    /// </summary>
    private static AnnotationVerdict ClassifyAnnotation(SafePageHandle page, int index, ref bool sawFormField)
    {
        using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(page, index);
        if (annot == null || annot.IsInvalid) return AnnotationVerdict.Keep;

        int subtype = PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot);

        if (PayloadAnnotationSubtypes.Contains(subtype)) return AnnotationVerdict.RemoveMedia;

        if (subtype == PdfiumNativeBridge.FPDF_ANNOT_WIDGET) sawFormField = true;

        if (subtype != PdfiumNativeBridge.FPDF_ANNOT_LINK) return AnnotationVerdict.Keep;

        IntPtr link = PdfiumNativeBridge.FPDFAnnot_GetLink(annot);
        if (link == IntPtr.Zero) return AnnotationVerdict.Keep;

        IntPtr action = PdfiumNativeBridge.FPDFLink_GetAction(link);
        if (action == IntPtr.Zero) return AnnotationVerdict.Keep;

        return PdfiumNativeBridge.FPDFAction_GetType(action) switch
        {
            PdfiumNativeBridge.PDFACTION_LAUNCH => AnnotationVerdict.RemoveLaunch,
            PdfiumNativeBridge.PDFACTION_REMOTEGOTO => AnnotationVerdict.RemoveRemote,

            // PDFium reports JavaScript and every other action it does not model as
            // unsupported. A copy that is meant to be inert cannot keep those.
            PdfiumNativeBridge.PDFACTION_UNSUPPORTED => AnnotationVerdict.RemoveUnrecognized,

            _ => AnnotationVerdict.Keep
        };
    }

    private static void SaveToDisk(SafeDocumentHandle doc, string targetPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Exception? writeFailure = null;

        var fileWrite = new FPDF_FILEWRITE
        {
            version = 1,
            WriteBlock = (pThis, pData, size) =>
            {
                // Never unwind a managed exception through PDFium's C++ frames.
                try
                {
                    byte[] buffer = new byte[size];
                    Marshal.Copy(pData, buffer, 0, (int)size);
                    outStream.Write(buffer, 0, (int)size);
                    return 1;
                }
                catch (Exception ex)
                {
                    writeFailure ??= ex;
                    return 0;
                }
            }
        };

        int saveResult = PdfiumNativeBridge.FPDF_SaveAsCopy(doc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
        GC.KeepAlive(fileWrite);

        if (writeFailure != null)
            throw new PdfSanitizationException("Failed writing the sanitized copy to disk.", writeFailure);

        if (saveResult == 0)
            throw new PdfSanitizationException("PDFium refused to write the sanitized copy.");
    }

    /// <summary>
    /// Re-opens the copy that was just written and inspects it with the same inspector the
    /// viewer uses. Without this the feature would be asserting its own success.
    /// </summary>
    private async ValueTask VerifyOutputIsCleanAsync(string targetPath, CancellationToken cancellationToken)
    {
        DocumentSafetyReport verification;

        try
        {
            using var engine = new PdfiumEngine();
            await using var written = await engine.OpenDocumentAsync(targetPath, null, cancellationToken);
            verification = await _inspector.InspectAsync(written, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(targetPath);
            throw new PdfSanitizationException(
                "The sanitized copy could not be re-opened for verification, so it was discarded.", ex);
        }

        if (verification.IsClean) return;

        TryDelete(targetPath);

        string survived = string.Join("; ", verification.Findings
            .Where(f => f.Severity == RiskSeverity.Elevated)
            .Select(f => f.Description));

        throw new PdfSanitizationException(
            "The sanitized copy still contained active content and was discarded: " + survived);
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
