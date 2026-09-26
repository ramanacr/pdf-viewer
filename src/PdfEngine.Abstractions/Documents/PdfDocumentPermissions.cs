using System.Collections.Generic;

namespace PdfEngine.Documents;

/// <summary>
/// What the reader may do with an encrypted document (ISO 32000-2 7.6.4.2, Table 22), resolved for
/// the way it was opened: the owner password (or a public-key recipient granted everything)
/// lifts every restriction. For revision 2, the flags that revision 3 introduced (bits 9–12) follow
/// the older bits they refine.
/// </summary>
public sealed record PdfDocumentPermissions(
    bool IsEncrypted,
    bool Unrestricted,
    bool CanPrint,
    bool CanPrintHighQuality,
    bool CanModify,
    bool CanCopy,
    bool CanAnnotate,
    bool CanFillForms,
    bool CanExtractForAccessibility,
    bool CanAssemble)
{
    /// <summary>An unencrypted document: everything is allowed.</summary>
    public static readonly PdfDocumentPermissions Unencrypted = new(false, true, true, true, true, true, true, true, true, true);

    /// <summary>Encrypted but opened with full rights.</summary>
    public static readonly PdfDocumentPermissions Owner = Unencrypted with { IsEncrypted = true };

    /// <summary>Resolves /P (bit numbers are 1-based as in Table 22) for a security handler revision.</summary>
    public static PdfDocumentPermissions FromFlags(int p, int revision, bool unrestricted)
    {
        if (unrestricted)
            return Owner;
        bool Bit(int n) => (p & (1 << (n - 1))) != 0;
        bool print = Bit(3), modify = Bit(4), copy = Bit(5), annotate = Bit(6);
        if (revision <= 2)
            return new(true, false, print, print, modify, copy, annotate, annotate, copy, modify);
        return new(true, false,
            CanPrint: print,
            CanPrintHighQuality: print && Bit(12),
            CanModify: modify,
            CanCopy: copy,
            CanAnnotate: annotate,
            CanFillForms: annotate || Bit(9),
            CanExtractForAccessibility: copy || Bit(10),
            CanAssemble: modify || Bit(11));
    }

    /// <summary>A short, human description of what is restricted, or empty when nothing is.</summary>
    public string RestrictionSummary
    {
        get
        {
            if (Unrestricted) return string.Empty;
            var denied = new List<string>();
            if (!CanPrint) denied.Add("printing");
            else if (!CanPrintHighQuality) denied.Add("high-quality printing");
            if (!CanCopy) denied.Add("copying");
            if (!CanAnnotate) denied.Add("commenting");
            if (!CanFillForms) denied.Add("form filling");
            if (!CanModify) denied.Add("editing");
            if (!CanAssemble) denied.Add("page assembly");
            return denied.Count == 0 ? string.Empty : "Not allowed: " + string.Join(", ", denied);
        }
    }
}
