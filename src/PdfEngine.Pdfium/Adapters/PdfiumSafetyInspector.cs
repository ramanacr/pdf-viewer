using PdfEngine.Documents;
using PdfEngine.Pdfium.Native;
using PdfEngine.Safety;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Reports what a document carries that could act on the user's machine.
///
/// Detection is authoritative rather than heuristic: PDFium parses the document properly, so
/// script and attachments hidden inside compressed object streams are still found. A raw
/// byte scan for "/JavaScript" would miss exactly the documents that matter most.
///
/// Nothing found here is ever executed. The inspector only reports.
/// </summary>
public sealed class PdfiumSafetyInspector : IPdfSafetyInspector
{
    /// <summary>Cap on the examples kept per finding; a hostile document can hold thousands.</summary>
    private const int MaxDetailsPerFinding = 10;

    /// <summary>Pages scanned for link actions. Bounds the cost on very large documents.</summary>
    private const int MaxPagesScannedForLinks = 50;

    public ValueTask<DocumentSafetyReport> InspectAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        var findings = new List<DocumentRiskFinding>();
        bool limited = false;
        string limitation = string.Empty;

        lock (pdfiumDoc.SyncLock)
        {
            AddJavaScriptFinding(pdfiumDoc, findings);
            AddAttachmentFinding(pdfiumDoc, findings);
            AddEncryptionFinding(pdfiumDoc, findings);

            int pagesToScan = Math.Min(pdfiumDoc.PageCount, MaxPagesScannedForLinks);
            if (pdfiumDoc.PageCount > pagesToScan)
            {
                limited = true;
                limitation =
                    $"Link and launch actions were checked on the first {pagesToScan} of " +
                    $"{pdfiumDoc.PageCount} pages.";
            }

            AddActionFindings(pdfiumDoc, pagesToScan, findings, cancellationToken);
        }

        return ValueTask.FromResult(new DocumentSafetyReport
        {
            Findings = findings,
            InspectionWasLimited = limited,
            LimitationReason = limitation
        });
    }

    private static void AddJavaScriptFinding(PdfiumDocument doc, List<DocumentRiskFinding> findings)
    {
        int count = PdfiumNativeBridge.FPDFDoc_GetJavaScriptActionCount(doc.Handle);
        if (count <= 0) return;

        var names = new List<string>();
        for (int i = 0; i < count && names.Count < MaxDetailsPerFinding; i++)
        {
            IntPtr action = PdfiumNativeBridge.FPDFDoc_GetJavaScriptAction(doc.Handle, i);
            if (action == IntPtr.Zero) continue;

            try
            {
                uint len = PdfiumNativeBridge.FPDFJavaScriptAction_GetName(action, null, 0);
                if (len > 0)
                {
                    byte[] buffer = new byte[len];
                    PdfiumNativeBridge.FPDFJavaScriptAction_GetName(action, buffer, len);
                    string name = PdfiumNativeBridge.Utf16BytesToString(buffer, (int)len);
                    names.Add(string.IsNullOrWhiteSpace(name) ? $"(unnamed script {i + 1})" : name);
                }
                else
                {
                    names.Add($"(unnamed script {i + 1})");
                }
            }
            finally
            {
                PdfiumNativeBridge.FPDFDoc_CloseJavaScriptAction(action);
            }
        }

        findings.Add(new DocumentRiskFinding
        {
            Kind = DocumentRiskKind.JavaScript,
            Severity = RiskSeverity.Elevated,
            Count = count,
            Description = count == 1
                ? "This document contains an embedded script. It has not been run."
                : $"This document contains {count} embedded scripts. They have not been run.",
            Details = names
        });
    }

    private static void AddAttachmentFinding(PdfiumDocument doc, List<DocumentRiskFinding> findings)
    {
        int count = PdfiumNativeBridge.FPDFDoc_GetAttachmentCount(doc.Handle);
        if (count <= 0) return;

        var names = new List<string>();
        for (int i = 0; i < count && names.Count < MaxDetailsPerFinding; i++)
        {
            IntPtr attachment = PdfiumNativeBridge.FPDFDoc_GetAttachment(doc.Handle, i);
            if (attachment == IntPtr.Zero) continue;

            uint len = PdfiumNativeBridge.FPDFAttachment_GetName(attachment, null, 0);
            if (len == 0) continue;

            byte[] buffer = new byte[len];
            PdfiumNativeBridge.FPDFAttachment_GetName(attachment, buffer, len);
            names.Add(PdfiumNativeBridge.Utf16BytesToString(buffer, (int)len));
        }

        findings.Add(new DocumentRiskFinding
        {
            Kind = DocumentRiskKind.EmbeddedFile,
            Severity = RiskSeverity.Elevated,
            Count = count,
            Description = count == 1
                ? "This document carries an embedded file."
                : $"This document carries {count} embedded files.",
            Details = names
        });
    }

    private static void AddEncryptionFinding(PdfiumDocument doc, List<DocumentRiskFinding> findings)
    {
        // -1 means the document is not encrypted.
        int revision = PdfiumNativeBridge.FPDF_GetSecurityHandlerRevision(doc.Handle);
        if (revision < 0) return;

        findings.Add(new DocumentRiskFinding
        {
            Kind = DocumentRiskKind.Encryption,
            Severity = RiskSeverity.Informational,
            Count = 1,
            Description = $"This document is encrypted (security handler revision {revision}).",
            Details = Array.Empty<string>()
        });
    }

    /// <summary>
    /// Walks link annotations looking for URI and /Launch actions. A launch action asks the
    /// reader to start an external program, so it is reported as elevated even though this
    /// application never acts on one.
    /// </summary>
    private static void AddActionFindings(
        PdfiumDocument doc,
        int pageCount,
        List<DocumentRiskFinding> findings,
        CancellationToken cancellationToken)
    {
        var uriTargets = new List<string>();
        var launchTargets = new List<string>();
        int uriCount = 0;
        int launchCount = 0;

        for (int p = 0; p < pageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var page = PdfiumNativeBridge.FPDF_LoadPage(doc.Handle, p);
            if (page == null || page.IsInvalid) continue;

            int annotCount = PdfiumNativeBridge.FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < annotCount; i++)
            {
                using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(page, i);
                if (annot == null || annot.IsInvalid) continue;

                if (PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot) != PdfiumNativeBridge.FPDF_ANNOT_LINK)
                    continue;

                IntPtr link = PdfiumNativeBridge.FPDFAnnot_GetLink(annot);
                if (link == IntPtr.Zero) continue;

                IntPtr action = PdfiumNativeBridge.FPDFLink_GetAction(link);
                if (action == IntPtr.Zero) continue;

                uint type = PdfiumNativeBridge.FPDFAction_GetType(action);
                if (type == PdfiumNativeBridge.PDFACTION_URI)
                {
                    uriCount++;
                    if (uriTargets.Count < MaxDetailsPerFinding)
                    {
                        string uri = ReadUriPath(doc, action);
                        if (!string.IsNullOrEmpty(uri)) uriTargets.Add(uri);
                    }
                }
                else if (type == PdfiumNativeBridge.PDFACTION_LAUNCH)
                {
                    launchCount++;
                    if (launchTargets.Count < MaxDetailsPerFinding)
                    {
                        launchTargets.Add($"Launch action on page {p + 1}");
                    }
                }
            }
        }

        if (launchCount > 0)
        {
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.LaunchAction,
                Severity = RiskSeverity.Elevated,
                Count = launchCount,
                Description = launchCount == 1
                    ? "This document asks to launch an external program. It has not been run."
                    : $"This document contains {launchCount} requests to launch external programs. None have been run.",
                Details = launchTargets
            });
        }

        if (uriCount > 0)
        {
            findings.Add(new DocumentRiskFinding
            {
                Kind = DocumentRiskKind.ExternalLink,
                Severity = RiskSeverity.Informational,
                Count = uriCount,
                Description = uriCount == 1
                    ? "This document contains a link to an external address."
                    : $"This document contains {uriCount} links to external addresses.",
                Details = uriTargets
            });
        }
    }

    private static string ReadUriPath(PdfiumDocument doc, IntPtr action)
    {
        // FPDFAction_GetURIPath returns a plain byte string, not UTF-16.
        uint len = PdfiumNativeBridge.FPDFAction_GetURIPath(doc.Handle, action, null, 0);
        if (len == 0) return string.Empty;

        byte[] buffer = new byte[len];
        PdfiumNativeBridge.FPDFAction_GetURIPath(doc.Handle, action, buffer, len);
        return System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }
}
