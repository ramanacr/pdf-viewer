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
public sealed class PdfSafetyInspector : IPdfSafetyInspector
{
    /// <summary>Cap on the examples kept per finding; a hostile document can hold thousands.</summary>
    internal const int MaxDetailsPerFinding = 10;

    /// <summary>Pages scanned for page-level actions. Bounds the cost on very large documents.</summary>
    internal const int MaxPagesScanned = 50;

    public ValueTask<DocumentSafetyReport> InspectAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
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
