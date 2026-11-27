using System.Text;
using Android.Text;
using PdfEngine.Documents;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Text;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Compare with another PDF: the text of each page of the open document (with its edits) against
/// the same page of a PDF the reader picks, line by line (a longest-common-subsequence diff of the
/// page's lines, white space ignored). The report gives a summary, then for each page that differs
/// the lines added, deleted and changed, with counts. The lines are shown only when both documents
/// allow copying their text; otherwise only the counts are.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The largest page pair the line diff compares in full (lines × lines); larger pairs count the middle as changed.</summary>
    private const long MaxDiffCells = 4_000_000;

    /// <summary>At most this many lines are listed for a page (all are counted).</summary>
    private const int MaxListedLinesPerPage = 30;

    private enum LineEdit { Same, Deleted, Added }

    private sealed record PageComparison(int Page, int Added, int Deleted, int Changed, List<string> Lines, int? OnlyIn, int OnlyLines);

    private sealed record ComparisonResult(bool NeedsPassword, string Report);

    private void CompareDocuments()
    {
        if (!NeedDocument()) return;
        PickFiles(["application/pdf"], false, uris => _ = CompareWithAsync(uris[0], null, wrong: false));
    }

    private async Task CompareWithAsync(Uri other, string? password, bool wrong)
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (CurrentBytes() is not { } bytes) return;
        string otherName = DisplayName(other) ?? "the other PDF";
        var result = await RunBusyAsync("Comparing the documents…", token =>
        {
            byte[] otherBytes = ReadUri(other, Options.MaxFileBytes);
            token.ThrowIfCancellationRequested();
            using var mine = OpenForTool(bytes);
            PdfVectorDocument theirs;
            try
            {
                theirs = PdfVectorDocument.Open(new PdfMemoryByteSource(otherBytes), otherName, password: password, certificates: []);
            }
            catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
            {
                return new ComparisonResult(true, "");
            }
            using (theirs)
                return new ComparisonResult(false, CompareText(mine, theirs, otherName, token));
        });
        if (result == null || !ReferenceEquals(doc, _doc)) return;
        if (result.NeedsPassword)
        {
            AskLine(wrong ? "Wrong password" : "This PDF is protected",
                wrong ? "That password does not open it. Try again." : "Enter the password of the PDF to compare with.",
                "", "Password", InputTypes.ClassText | InputTypes.TextVariationPassword, "Compare",
                typed => _ = CompareWithAsync(other, typed, wrong: true));
            return;
        }
        ShowReport("Comparison", result.Report, BaseName + "_comparison");
    }

    private static bool AllowsCopy(PdfVectorDocument engine) =>
        engine.Security is not { } s || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanCopy;

    /// <summary>The page's text as lines, white space collapsed and empty lines dropped.</summary>
    private static List<string> PageLines(PdfVectorDocument doc, int page, CancellationToken token)
    {
        if (page > doc.PageCount) return [];
        string text = PdfTextExtractor.LoadPage(doc, page, token).GetAllText();
        return text.Split('\n', '\r')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string CompareText(PdfVectorDocument mine, PdfVectorDocument theirs, string otherName, CancellationToken token)
    {
        bool showLines = AllowsCopy(mine) && AllowsCopy(theirs);
        int pages = Math.Max(mine.PageCount, theirs.PageCount);
        var differing = new List<PageComparison>();
        for (int page = 1; page <= pages; page++)
        {
            token.ThrowIfCancellationRequested();
            var a = PageLines(mine, page, token);
            var b = PageLines(theirs, page, token);
            if (page > theirs.PageCount)
            {
                differing.Add(new PageComparison(page, 0, a.Count, 0, [], 1, a.Count));
                continue;
            }
            if (page > mine.PageCount)
            {
                differing.Add(new PageComparison(page, b.Count, 0, 0, [], 2, b.Count));
                continue;
            }
            if (ComparePage(page, a, b, showLines) is { } changed) differing.Add(changed);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"This document: {mine.PageCount} page(s).");
        sb.AppendLine($"Compared with: {otherName}, {theirs.PageCount} page(s).");
        sb.AppendLine("Each page is compared with the page of the same number.");
        sb.AppendLine();
        if (differing.Count == 0)
        {
            sb.AppendLine("The text of the documents is the same.");
            return sb.ToString().TrimEnd();
        }
        int added = differing.Sum(d => d.Added), deleted = differing.Sum(d => d.Deleted), changedLines = differing.Sum(d => d.Changed);
        sb.AppendLine($"Summary: {differing.Count} of {pages} page(s) differ. Lines added: {added}, deleted: {deleted}, changed: {changedLines}.");
        if (!showLines) sb.AppendLine("A document does not allow copying its text, so the lines are counted but not shown.");
        foreach (var d in differing)
        {
            sb.AppendLine();
            if (d.OnlyIn is { } only)
            {
                sb.AppendLine($"Page {d.Page}: only in {(only == 1 ? "this document" : "the other document")} ({d.OnlyLines} line(s))");
                continue;
            }
            var counts = new List<string>(3);
            if (d.Added > 0) counts.Add($"{d.Added} added");
            if (d.Deleted > 0) counts.Add($"{d.Deleted} deleted");
            if (d.Changed > 0) counts.Add($"{d.Changed} changed");
            sb.AppendLine($"Page {d.Page}: {string.Join(", ", counts)}");
            foreach (string line in d.Lines) sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One page pair: null when the lines are the same; else the counts and, with <paramref name="showLines"/>, the lines ("+", "−", "~ old / → new").</summary>
    private static PageComparison? ComparePage(int page, List<string> a, List<string> b, bool showLines)
    {
        var edits = DiffLines(a, b);
        if (edits.All(e => e.Edit == LineEdit.Same)) return null;
        int added = 0, deleted = 0, changed = 0, listed = 0, unlisted = 0;
        var lines = new List<string>();
        void List(string line)
        {
            if (!showLines) return;
            if (listed < MaxListedLinesPerPage)
            {
                lines.Add(line);
                listed++;
            }
            else
            {
                unlisted++;
            }
        }
        int i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Edit == LineEdit.Same)
            {
                i++;
                continue;
            }
            // A run of edits: deleted lines paired with added lines are changes; the rest are added or deleted.
            var gone = new List<string>();
            var come = new List<string>();
            for (; i < edits.Count && edits[i].Edit != LineEdit.Same; i++)
                (edits[i].Edit == LineEdit.Deleted ? gone : come).Add(edits[i].Text);
            int pairs = Math.Min(gone.Count, come.Count);
            for (int k = 0; k < pairs; k++)
            {
                changed++;
                List($"  ~ {gone[k]}");
                List($"    → {come[k]}");
            }
            for (int k = pairs; k < gone.Count; k++)
            {
                deleted++;
                List($"  − {gone[k]}");
            }
            for (int k = pairs; k < come.Count; k++)
            {
                added++;
                List($"  + {come[k]}");
            }
        }
        if (unlisted > 0) lines.Add($"  … and {unlisted} more line(s)");
        return new PageComparison(page, added, deleted, changed, lines, null, 0);
    }

    /// <summary>A line diff: the common start and end are kept, the middle compared by longest common subsequence.</summary>
    private static List<(LineEdit Edit, string Text)> DiffLines(List<string> a, List<string> b)
    {
        int start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
        int endA = a.Count, endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        var result = new List<(LineEdit, string)>(Math.Max(a.Count, b.Count));
        for (int k = 0; k < start; k++) result.Add((LineEdit.Same, a[k]));
        int n = endA - start, m = endB - start;
        if ((long)n * m > MaxDiffCells)
        {
            for (int k = start; k < endA; k++) result.Add((LineEdit.Deleted, a[k]));
            for (int k = start; k < endB; k++) result.Add((LineEdit.Added, b[k]));
        }
        else if (n > 0 || m > 0)
        {
            // lcs[i, j]: the longest common subsequence of a[start + i ..] and b[start + j ..].
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y])
                {
                    result.Add((LineEdit.Same, a[start + x]));
                    x++;
                    y++;
                }
                else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
                {
                    result.Add((LineEdit.Added, b[start + y]));
                    y++;
                }
                else
                {
                    result.Add((LineEdit.Deleted, a[start + x]));
                    x++;
                }
            }
        }
        for (int k = endA; k < a.Count; k++) result.Add((LineEdit.Same, a[k]));
        return result;
    }
}
