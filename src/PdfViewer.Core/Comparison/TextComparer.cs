using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfViewer.Core.Comparison;

/// <summary>A word of a document: its text, page and box (normalized to the page, top-left origin).</summary>
public sealed record CompareWord(string Text, int PageNumber, double X, double Y, double Width, double Height);

public enum ChangeKind { Inserted, Deleted, Replaced }

/// <summary>A run of words that differ: removed from the old document, added in the new, or both.</summary>
public sealed record TextChange(ChangeKind Kind, IReadOnlyList<CompareWord> Old, IReadOnlyList<CompareWord> New)
{
    public string OldText => string.Join(" ", Old.Select(w => w.Text));
    public string NewText => string.Join(" ", New.Select(w => w.Text));
    /// <summary>Where to show the change: its first word in either document.</summary>
    public int OldPage => Old.Count > 0 ? Old[0].PageNumber : 0;
    public int NewPage => New.Count > 0 ? New[0].PageNumber : 0;
}

/// <summary>
/// Word-level comparison of two documents' text (Myers' O(ND) difference algorithm over the
/// whole document, so text that moved across a page break still matches). Changes next to each
/// other become one replacement.
/// </summary>
public static class TextComparer
{
    public static IReadOnlyList<TextChange> Compare(IReadOnlyList<CompareWord> oldWords, IReadOnlyList<CompareWord> newWords, bool ignoreCase = false)
    {
        var cmp = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var a = oldWords.Select(w => Normalize(w.Text)).ToArray();
        var b = newWords.Select(w => Normalize(w.Text)).ToArray();

        // Common prefix and suffix first: most documents compared are mostly the same.
        int start = 0;
        while (start < a.Length && start < b.Length && cmp.Equals(a[start], b[start])) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && cmp.Equals(a[endA - 1], b[endB - 1])) { endA--; endB--; }

        var script = Myers(a, start, endA, b, start, endB, cmp);
        var changes = new List<TextChange>();
        List<CompareWord>? del = null, ins = null;
        void Flush()
        {
            if ((del?.Count ?? 0) == 0 && (ins?.Count ?? 0) == 0) { del = ins = null; return; }
            var kind = del is { Count: > 0 } ? ins is { Count: > 0 } ? ChangeKind.Replaced : ChangeKind.Deleted : ChangeKind.Inserted;
            changes.Add(new TextChange(kind, (IReadOnlyList<CompareWord>?)del ?? Array.Empty<CompareWord>(), (IReadOnlyList<CompareWord>?)ins ?? Array.Empty<CompareWord>()));
            del = ins = null;
        }
        foreach (var (op, i, j) in script)
        {
            switch (op)
            {
                case '=': Flush(); break;
                case '-': (del ??= new()).Add(oldWords[i]); break;
                case '+': (ins ??= new()).Add(newWords[j]); break;
            }
        }
        Flush();
        return changes;
    }

    /// <summary>Hyphenation and typographic variants are not changes: curly quotes, dashes, soft hyphens.</summary>
    private static string Normalize(string s) => s
        .Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"')
        .Replace('–', '-').Replace('—', '-').Replace("­", string.Empty).Trim();

    /// <summary>The edit script between a[lo..hi) and b[lo..hi): '=' kept, '-' deleted from a, '+' inserted from b, in order.</summary>
    private static List<(char Op, int I, int J)> Myers(string[] a, int aLo, int aHi, string[] b, int bLo, int bHi, StringComparer cmp)
    {
        var script = new List<(char, int, int)>();
        for (int k = 0; k < aLo; k++) script.Add(('=', k, k));
        int n = aHi - aLo, m = bHi - bLo, max = n + m;
        if (max > 0)
        {
            int offset = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();
            int dEnd = -1;
            for (int d = 0; d <= max && dEnd < 0; d++)
            {
                trace.Add((int[])v.Clone());
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && cmp.Equals(a[aLo + x], b[bLo + y])) { x++; y++; }
                    v[offset + k] = x;
                    if (x >= n && y >= m) { dEnd = d; break; }
                }
            }
            // Walk back through the trace to recover the path.
            var path = new List<(char, int, int)>();
            int px = n, py = m;
            for (int d = dEnd; d > 0; d--)
            {
                var vd = trace[d];
                int k = px - py;
                int prevK = k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]) ? k + 1 : k - 1;
                int prevX = vd[offset + prevK], prevY = prevX - prevK;
                while (px > prevX && py > prevY) { px--; py--; path.Add(('=', aLo + px, bLo + py)); }
                if (px == prevX) { py--; path.Add(('+', aLo + px, bLo + py)); }
                else { px--; path.Add(('-', aLo + px, bLo + py)); }
            }
            while (px > 0 && py > 0) { px--; py--; path.Add(('=', aLo + px, bLo + py)); }
            path.Reverse();
            script.AddRange(path);
        }
        int suffix = a.Length - aHi;
        for (int k = 0; k < suffix; k++) script.Add(('=', aHi + k, bHi + k));
        return script;
    }
}
