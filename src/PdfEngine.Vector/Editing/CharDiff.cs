using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Editing;

/// <summary>Which characters of an edited text are the old ones (Myers' O(ND) diff, after trimming what did not change at either end).</summary>
internal static class CharDiff
{
    /// <summary>For each old character, its new index (-1 when deleted); for each new character, its old index (-1 when inserted).</summary>
    public static (int[] OldToNew, int[] NewToOld) Map(string a, string b)
    {
        var oldToNew = new int[a.Length];
        var newToOld = new int[b.Length];
        Array.Fill(oldToNew, -1);
        Array.Fill(newToOld, -1);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) { oldToNew[prefix] = prefix; newToOld[prefix] = prefix; prefix++; }
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            oldToNew[a.Length - 1 - suffix] = b.Length - 1 - suffix;
            newToOld[b.Length - 1 - suffix] = a.Length - 1 - suffix;
            suffix++;
        }
        int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
        if (n == 0 || m == 0) return (oldToNew, newToOld);

        // Myers: furthest-reaching paths per diagonal, kept per step to walk back the matches.
        int max = Math.Min(n + m, 4000);
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        int offset = max;
        bool found = false;
        for (int d = 0; d <= max && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[prefix + x] == b[prefix + y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }
        if (!found) return (oldToNew, newToOld); // too different: the middle counts as replaced

        int cx = n, cy = m;
        for (int d = trace.Count - 1; d > 0 && (cx > 0 || cy > 0); d--)
        {
            var pv = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
            int prevX = pv[offset + prevK], prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--; cy--;
                oldToNew[prefix + cx] = prefix + cy;
                newToOld[prefix + cy] = prefix + cx;
            }
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0 && a[prefix + cx - 1] == b[prefix + cy - 1])
        {
            cx--; cy--;
            oldToNew[prefix + cx] = prefix + cy;
            newToOld[prefix + cy] = prefix + cx;
        }
        return (oldToNew, newToOld);
    }
}
