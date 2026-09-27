using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEngine.Vector.PdfA;

/// <summary>Conformance level: basic (visual appearance), Unicode (text extractable), accessible (tagged).</summary>
public enum PdfAConformance { B, U, A }

/// <summary>A PDF/A part and level: PDF/A-1b, PDF/A-2b, PDF/A-2u, PDF/A-3b and so on.</summary>
public sealed record PdfAFlavour(int Part, PdfAConformance Level)
{
    public static readonly PdfAFlavour A1b = new(1, PdfAConformance.B);
    public static readonly PdfAFlavour A2b = new(2, PdfAConformance.B);
    public static readonly PdfAFlavour A2u = new(2, PdfAConformance.U);
    public static readonly PdfAFlavour A3b = new(3, PdfAConformance.B);
    public static readonly PdfAFlavour A3u = new(3, PdfAConformance.U);

    public override string ToString() => $"PDF/A-{Part}{char.ToLowerInvariant(Level.ToString()[0])}";

    public static PdfAFlavour? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant().Replace("PDF/A-", string.Empty).Replace("PDFA", string.Empty).Replace("-", string.Empty);
        if (t.Length < 2 || !int.TryParse(t[..1], out int part) || part is < 1 or > 3) return null;
        return t[1] switch
        {
            'B' => new PdfAFlavour(part, PdfAConformance.B),
            'U' when part >= 2 => new PdfAFlavour(part, PdfAConformance.U),
            'A' => new PdfAFlavour(part, PdfAConformance.A),
            _ => null,
        };
    }
}

/// <summary>A requirement the document does not meet: the ISO 19005 clause, the test within it (veraPDF's numbering), and where.</summary>
public sealed record PdfAIssue(string Clause, int Test, string Message, string? Location = null)
{
    public string RuleId => $"{Clause}-{Test}";
    public override string ToString() => Location == null ? $"{RuleId}: {Message}" : $"{RuleId}: {Message} ({Location})";
}

public sealed class PdfAReport
{
    public required PdfAFlavour Flavour { get; init; }
    /// <summary>The conformance the document's metadata claims, or null.</summary>
    public PdfAFlavour? Claimed { get; init; }
    public IReadOnlyList<PdfAIssue> Issues { get; init; } = Array.Empty<PdfAIssue>();
    public bool IsCompliant => Issues.Count == 0;

    /// <summary>Issues grouped by rule, each with how often it occurs.</summary>
    public IEnumerable<(PdfAIssue First, int Count)> Summary =>
        Issues.GroupBy(i => i.RuleId).Select(g => (g.First(), g.Count())).OrderBy(g => g.Item1.Clause, StringComparer.Ordinal).ThenBy(g => g.Item1.Test);
}
