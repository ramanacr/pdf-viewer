using System;
using System.Collections.Generic;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Mark positioning (GPOS mark-to-base, mark-to-ligature and mark-to-mark attachment, also through
/// extension lookups) and cursive attachment for shaping new text: each mark is placed on the anchor
/// of the glyph it sits on, and does not advance; each cursive glyph's entry anchor is put on the exit
/// anchor of the glyph before it. Offsets are in font units, from where the glyph would otherwise be drawn.
/// </summary>
internal sealed class OpenTypePositioning
{
    private static readonly string[] MarkFeatures = { "mark", "mkmk" };
    private static readonly string[] CursiveFeatures = { "curs" };
    private readonly byte[] _gpos;
    private readonly byte[]? _gdef;

    private OpenTypePositioning(byte[] gpos, byte[]? gdef)
    {
        _gpos = gpos;
        _gdef = gdef;
    }

    public static OpenTypePositioning? Read(IReadOnlyDictionary<string, byte[]> tables) =>
        tables.TryGetValue("GPOS", out var gpos) && gpos.Length >= 10 ? new OpenTypePositioning(gpos, tables.GetValueOrDefault("GDEF")) : null;

    /// <summary>A glyph's class in GDEF: 1 base, 2 ligature, 3 mark, 4 component (0 when there is none).</summary>
    public int GlyphClass(int gid)
    {
        if (_gdef == null || _gdef.Length < 6) return 0;
        int classDef = BigEndian.U16(_gdef, 4);
        return classDef == 0 ? 0 : OpenTypeLayout.ClassOf(_gdef, classDef, gid);
    }

    /// <summary>
    /// Joins the cursive glyphs of a run and places its marks, in logical order. <paramref name="advances"/>
    /// are the glyphs' advances (font units); a mark attached to a glyph gets its advance set to 0 and its
    /// offset set so it lands on the anchor. <paramref name="rightToLeft"/>: the run is drawn right to left
    /// (once it is reversed), which decides which advances of two joined glyphs the join changes.
    /// </summary>
    public void Apply(IReadOnlyList<int> gids, string script, double[] advances, double[] dx, double[] dy, bool rightToLeft = false)
    {
        try
        {
            // Marks do not advance (GDEF says which glyphs are marks), attached or not.
            for (int i = 0; i < gids.Count; i++) if (GlyphClass(gids[i]) == 3) advances[i] = 0;
            // Cursive joins first, so a mark follows its letter up or down the joined chain.
            var run = NewRun(gids, advances, dx, dy, rightToLeft);
            try
            {
                foreach (var (index, _) in OpenTypeLayout.Lookups(_gpos, script, CursiveFeatures))
                {
                    if (Lookup(index) is not { } lookup) continue;
                    for (int i = 0; i < gids.Count; i++)
                    {
                        if (OpenTypeLayout.Ignored(_gdef, gids[i], lookup.Flag, lookup.MarkSet)) continue;
                        foreach (var (type, sub) in lookup.Subtables)
                            if (type == 3 && BigEndian.U16(_gpos, sub) == 1 && Cursive(run, i, sub, lookup.Flag, lookup.MarkSet)) break;
                    }
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                // A damaged cursive lookup: the joins made so far stand, and the marks are still placed.
            }
            ResolveCursive(run);
            var t = _gpos;
            int lookupList = BigEndian.U16(t, 8), lookupCount = BigEndian.U16(t, lookupList);
            foreach (var (index, _) in OpenTypeLayout.Lookups(t, script, MarkFeatures))
            {
                if (index >= lookupCount) continue;
                int lookup = lookupList + BigEndian.U16(t, lookupList + 2 + index * 2);
                int type = BigEndian.U16(t, lookup), subtables = BigEndian.U16(t, lookup + 4);
                for (int s = 0; s < subtables; s++)
                {
                    int sub = lookup + BigEndian.U16(t, lookup + 6 + s * 2);
                    int subType = type;
                    if (type == 9)
                    {
                        subType = BigEndian.U16(t, sub + 2);
                        sub += (int)Math.Min(BigEndian.U32(t, sub + 4), int.MaxValue);
                    }
                    if (subType is 4 or 5 or 6 && BigEndian.U16(t, sub) == 1) Attach(sub, subType, gids, advances, dx, dy);
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // A damaged table: the marks placed so far stand.
        }
    }

    private void Attach(int sub, int type, IReadOnlyList<int> gids, double[] advances, double[] dx, double[] dy)
    {
        var t = _gpos;
        int markCoverage = sub + BigEndian.U16(t, sub + 2), baseCoverage = sub + BigEndian.U16(t, sub + 4);
        int classCount = BigEndian.U16(t, sub + 6);
        int markArray = sub + BigEndian.U16(t, sub + 8), baseArray = sub + BigEndian.U16(t, sub + 10);
        for (int i = 1; i < gids.Count; i++)
        {
            int markIndex = OpenTypeLayout.Coverage(t, markCoverage, gids[i]);
            if (markIndex < 0 || markIndex >= BigEndian.U16(t, markArray)) continue;
            // What it attaches to: the glyph before it that is not a mark (mark-to-mark: the mark right before it).
            int b = i - 1;
            if (type != 6) while (b >= 0 && GlyphClass(gids[b]) == 3) b--;
            if (b < 0) continue;
            int baseIndex = OpenTypeLayout.Coverage(t, baseCoverage, gids[b]);
            if (baseIndex < 0) continue;
            int markRecord = markArray + 2 + markIndex * 4;
            int markClass = BigEndian.U16(t, markRecord);
            if (markClass >= classCount) continue;
            var (mx, my) = Anchor(markArray + BigEndian.U16(t, markRecord + 2));
            int anchorOffset;
            if (type == 5)
            {
                // A ligature: the anchor of its last component (the mark follows the letters it joined).
                if (baseIndex >= BigEndian.U16(t, baseArray)) continue;
                int attach = baseArray + BigEndian.U16(t, baseArray + 2 + baseIndex * 2);
                int components = BigEndian.U16(t, attach);
                if (components == 0) continue;
                int component = attach + 2 + (components - 1) * classCount * 2;
                anchorOffset = BigEndian.U16(t, component + markClass * 2);
                if (anchorOffset == 0) continue;
                anchorOffset += attach;
            }
            else
            {
                if (baseIndex >= BigEndian.U16(t, baseArray)) continue;
                int record = baseArray + 2 + baseIndex * classCount * 2;
                anchorOffset = BigEndian.U16(t, record + markClass * 2);
                if (anchorOffset == 0) continue;
                anchorOffset += baseArray;
            }
            var (bx, by) = Anchor(anchorOffset);
            // From the base's origin: its offset, less the advances drawn between it and the mark.
            double between = 0;
            for (int k = b; k < i; k++) between += advances[k];
            advances[i] = 0;
            dx[i] = dx[b] + bx - mx - between;
            dy[i] = dy[b] + by - my;
        }
    }

    private (int X, int Y) Anchor(int at) => (BigEndian.S16(_gpos, at + 2), BigEndian.S16(_gpos, at + 4));

    // ------------------------------------------------------------------ all of positioning, for complex scripts

    /// <summary>
    /// Positions a run in logical order by the features' lookups, in lookup order: single and pair
    /// adjustments, mark attachment and (chained) contextual positioning, skipping the glyphs each
    /// lookup's flag skips. Unlike <see cref="Apply(IReadOnlyList{int}, string, double[], double[], double[], bool)"/>,
    /// marks keep their own advances (fonts for these scripts give them none), and a mark follows its
    /// glyph when a later lookup moves or widens that glyph.
    /// </summary>
    public void Apply(IReadOnlyList<int> gids, string script, IReadOnlyCollection<string> features, double[] advances, double[] dx, double[] dy, bool rightToLeft = false)
    {
        int n = gids.Count;
        var run = NewRun(gids, advances, dx, dy, rightToLeft);
        try
        {
            foreach (var (index, _) in OpenTypeLayout.Lookups(_gpos, script, features))
            {
                if (Lookup(index) is not { } lookup) continue;
                for (int i = 0; i < n; i++)
                {
                    if (OpenTypeLayout.Ignored(_gdef, gids[i], lookup.Flag, lookup.MarkSet)) continue;
                    foreach (var (type, sub) in lookup.Subtables)
                        if (PositionAt(run, i, type, sub, lookup.Flag, lookup.MarkSet, 0)) break;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // A damaged table: what was positioned so far stands.
        }
        // Cursive glyphs hang off the glyphs they join, and a mark sits where its anchor put it from
        // the glyph it is on, wherever that glyph ended up.
        ResolveCursive(run);
        for (int i = 0; i < n; i++)
        {
            int b = run.AttachedTo[i];
            if (b < 0) continue;
            double between = 0;
            for (int k = b; k < i; k++) between += advances[k];
            dx[i] = dx[b] + run.AnchorX[i] - between;
            dy[i] = dy[b] + run.AnchorY[i];
        }
    }

    // AttachedTo: the glyph a mark sits on (-1: none). CursiveTo: the glyph a cursive glyph hangs off,
    // its parent in the joined chain (-1: none), and CursiveY how far above that glyph it is.
    private sealed record Run(IReadOnlyList<int> Gids, double[] Advances, double[] Dx, double[] Dy, int[] AttachedTo, double[] AnchorX, double[] AnchorY,
        int[] CursiveTo, double[] CursiveY, bool RightToLeft);

    private static Run NewRun(IReadOnlyList<int> gids, double[] advances, double[] dx, double[] dy, bool rightToLeft)
    {
        int n = gids.Count;
        var run = new Run(gids, advances, dx, dy, new int[n], new double[n], new double[n], new int[n], new double[n], rightToLeft);
        Array.Fill(run.AttachedTo, -1);
        Array.Fill(run.CursiveTo, -1);
        return run;
    }

    private (int Flag, int MarkSet, List<(int Type, int Offset)> Subtables)? Lookup(int index)
    {
        var t = _gpos;
        int lookupList = BigEndian.U16(t, 8), lookupCount = BigEndian.U16(t, lookupList);
        if (index < 0 || index >= lookupCount) return null;
        int lookup = lookupList + BigEndian.U16(t, lookupList + 2 + index * 2);
        int type = BigEndian.U16(t, lookup), flag = BigEndian.U16(t, lookup + 2), subtables = BigEndian.U16(t, lookup + 4);
        var subs = new List<(int Type, int Offset)>();
        for (int s = 0; s < subtables; s++)
        {
            int sub = lookup + BigEndian.U16(t, lookup + 6 + s * 2);
            if (type == 9) subs.Add((BigEndian.U16(t, sub + 2), sub + (int)Math.Min(BigEndian.U32(t, sub + 4), int.MaxValue)));
            else subs.Add((type, sub));
        }
        return (flag, (flag & 0x10) != 0 ? BigEndian.U16(t, lookup + 6 + subtables * 2) : -1, subs);
    }

    private int Next(Run run, int from, int step, int flag, int markSet)
    {
        int k = from + step;
        while (k >= 0 && k < run.Gids.Count && OpenTypeLayout.Ignored(_gdef, run.Gids[k], flag, markSet)) k += step;
        return k;
    }

    private bool PositionAt(Run run, int i, int type, int sub, int flag, int markSet, int depth)
    {
        var t = _gpos;
        int format = BigEndian.U16(t, sub);
        switch (type)
        {
            case 1:
            {
                int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), run.Gids[i]);
                if (coverage < 0) return false;
                int valueFormat = BigEndian.U16(t, sub + 4);
                if (format == 1) Adjust(run, i, sub + 6, valueFormat);
                else if (format == 2 && coverage < BigEndian.U16(t, sub + 6)) Adjust(run, i, sub + 8 + coverage * ValueSize(valueFormat), valueFormat);
                else return false;
                return true;
            }
            case 2:
            {
                int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), run.Gids[i]);
                int j = Next(run, i, 1, flag, markSet);
                if (coverage < 0 || j >= run.Gids.Count) return false;
                int vf1 = BigEndian.U16(t, sub + 4), vf2 = BigEndian.U16(t, sub + 6);
                int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
                int values = -1;
                if (format == 1)
                {
                    if (coverage >= BigEndian.U16(t, sub + 8)) return false;
                    int set = sub + BigEndian.U16(t, sub + 10 + coverage * 2);
                    int count = BigEndian.U16(t, set), record = 2 + size1 + size2;
                    for (int lo = 0, hi = count - 1; lo <= hi;)
                    {
                        int mid = (lo + hi) >> 1;
                        int second = BigEndian.U16(t, set + 2 + mid * record);
                        if (second == run.Gids[j]) { values = set + 2 + mid * record + 2; break; }
                        if (second < run.Gids[j]) lo = mid + 1; else hi = mid - 1;
                    }
                }
                else if (format == 2)
                {
                    int class1 = OpenTypeLayout.ClassOf(t, sub + BigEndian.U16(t, sub + 8), run.Gids[i]);
                    int class2 = OpenTypeLayout.ClassOf(t, sub + BigEndian.U16(t, sub + 10), run.Gids[j]);
                    int class1Count = BigEndian.U16(t, sub + 12), class2Count = BigEndian.U16(t, sub + 14);
                    if (class1 < class1Count && class2 < class2Count) values = sub + 16 + (class1 * class2Count + class2) * (size1 + size2);
                }
                if (values < 0) return false;
                Adjust(run, i, values, vf1);
                Adjust(run, j, values + size1, vf2);
                return true;
            }
            case 3 when format == 1:
                return Cursive(run, i, sub, flag, markSet);
            case 4 or 5 or 6 when format == 1:
                return AttachAt(run, i, type, sub, flag, markSet);
            case 7 or 8 when depth < 4:
                return Contextual(run, i, type, format, sub, flag, markSet, depth);
        }
        return false;
    }

    // A value record's placement and advance (device tables are not read).
    private void Adjust(Run run, int i, int record, int format)
    {
        int at = record;
        short Next() { short v = BigEndian.S16(_gpos, at); at += 2; return v; }
        if ((format & 0x1) != 0) run.Dx[i] += Next();
        if ((format & 0x2) != 0) run.Dy[i] += Next();
        if ((format & 0x4) != 0) run.Advances[i] += Next();
    }

    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    private bool AttachAt(Run run, int i, int type, int sub, int flag, int markSet)
    {
        var t = _gpos;
        var gids = run.Gids;
        int markIndex = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), gids[i]);
        int classCount = BigEndian.U16(t, sub + 6);
        int markArray = sub + BigEndian.U16(t, sub + 8), baseArray = sub + BigEndian.U16(t, sub + 10);
        if (markIndex < 0 || markIndex >= BigEndian.U16(t, markArray)) return false;
        // The glyph it sits on: the one before it that is not a mark (mark-to-mark: the mark before it).
        int b = i - 1;
        if (type == 6) b = Next(run, i, -1, flag, markSet);
        else while (b >= 0 && GlyphClass(gids[b]) == 3) b--;
        if (b < 0 || (type == 6 && GlyphClass(gids[b]) != 3)) return false;
        int baseIndex = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 4), gids[b]);
        if (baseIndex < 0 || baseIndex >= BigEndian.U16(t, baseArray)) return false;
        int markRecord = markArray + 2 + markIndex * 4;
        int markClass = BigEndian.U16(t, markRecord);
        if (markClass >= classCount) return false;
        int anchor;
        if (type == 5)
        {
            // A ligature: the anchor of its last component.
            int attach = baseArray + BigEndian.U16(t, baseArray + 2 + baseIndex * 2);
            int components = BigEndian.U16(t, attach);
            if (components == 0) return false;
            anchor = BigEndian.U16(t, attach + 2 + ((components - 1) * classCount + markClass) * 2);
            if (anchor == 0) return false;
            anchor += attach;
        }
        else
        {
            anchor = BigEndian.U16(t, baseArray + 2 + (baseIndex * classCount + markClass) * 2);
            if (anchor == 0) return false;
            anchor += baseArray;
        }
        var (mx, my) = Anchor(markArray + BigEndian.U16(t, markRecord + 2));
        var (bx, by) = Anchor(anchor);
        run.AttachedTo[i] = b;
        run.AnchorX[i] = bx - mx;
        run.AnchorY[i] = by - my;
        return true;
    }

    // Cursive attachment (type 3): glyph j's entry anchor goes on the exit anchor of the glyph before
    // it (the lookup's flag says which glyphs are skipped). Along the line, the earlier glyph's advance
    // ends at its exit and the later one starts at its entry (in drawing order: right-to-left text draws
    // the later glyph first). Across it, one of the two hangs off the other: with the RightToLeft flag
    // the later glyph stays where it is (the last letter of a word sits on the baseline, as in
    // Nastaliq), else the earlier one does. Anchors of formats 2 and 3 are read as format 1.
    private bool Cursive(Run run, int j, int sub, int flag, int markSet)
    {
        var t = _gpos;
        int coverage = sub + BigEndian.U16(t, sub + 2), count = BigEndian.U16(t, sub + 4);
        int index = OpenTypeLayout.Coverage(t, coverage, run.Gids[j]);
        if (index < 0 || index >= count) return false;
        int entry = BigEndian.U16(t, sub + 6 + index * 4);
        if (entry == 0) return false;
        int i = Next(run, j, -1, flag, markSet);
        if (i < 0) return false;
        int previous = OpenTypeLayout.Coverage(t, coverage, run.Gids[i]);
        if (previous < 0 || previous >= count) return false;
        int exit = BigEndian.U16(t, sub + 6 + previous * 4 + 2);
        if (exit == 0) return false;
        var (entryX, entryY) = Anchor(sub + entry);
        var (exitX, exitY) = Anchor(sub + exit);
        if (run.RightToLeft)
        {
            // Drawn j, then i: i starts at its exit, and j's advance ends at its entry.
            double d = exitX + run.Dx[i];
            run.Advances[i] -= d;
            run.Dx[i] -= d;
            run.Advances[j] = entryX + run.Dx[j];
        }
        else
        {
            run.Advances[i] = exitX + run.Dx[i];
            double d = entryX + run.Dx[j];
            run.Advances[j] -= d;
            run.Dx[j] -= d;
        }
        int child = i, parent = j;
        double y = entryY - exitY;
        if ((flag & 0x1) == 0) (child, parent, y) = (j, i, -y);
        // A glyph hangs off one other: when the child already hangs off another, that chain is turned
        // round to hang off the child; a parent that hung off the child no longer does.
        ReverseCursive(run, child, parent);
        if (run.CursiveTo[parent] == child)
        {
            run.CursiveTo[parent] = -1;
            run.CursiveY[parent] = 0;
        }
        run.CursiveTo[child] = parent;
        run.CursiveY[child] = y;
        return true;
    }

    private static void ReverseCursive(Run run, int i, int newParent)
    {
        if (run.CursiveTo[i] < 0) return;
        // The chain from i up to its root, or up to the new parent (where it is cut).
        var chain = new List<int> { i };
        for (int k = run.CursiveTo[i]; k >= 0 && k != newParent && chain.Count <= run.Gids.Count && !chain.Contains(k); k = run.CursiveTo[k]) chain.Add(k);
        var ys = chain.ConvertAll(k => run.CursiveY[k]);
        run.CursiveTo[i] = -1;
        for (int m = chain.Count - 2; m >= 0; m--)
        {
            run.CursiveTo[chain[m + 1]] = chain[m];
            run.CursiveY[chain[m + 1]] = -ys[m];
        }
    }

    // Each cursive glyph moves by its offset from its parent and by all that its parent moved.
    private static void ResolveCursive(Run run)
    {
        int n = run.Gids.Count;
        var state = new byte[n]; // 0 not yet, 1 on the path being resolved, 2 done
        var path = new List<int>();
        for (int i = 0; i < n; i++)
        {
            path.Clear();
            for (int k = i; k >= 0 && state[k] == 0 && run.CursiveTo[k] >= 0; k = run.CursiveTo[k])
            {
                state[k] = 1;
                path.Add(k);
            }
            for (int m = path.Count - 1; m >= 0; m--)
            {
                int c = path[m], p = run.CursiveTo[c];
                if (state[p] != 1) run.Dy[c] += run.CursiveY[c] + run.Dy[p]; // a loop (a damaged font) is cut where it closes
                state[c] = 2;
            }
        }
    }

    // Contextual (7) and chained contextual (8) positioning: the rule that matches at glyph i applies
    // its lookups at their input positions.
    private bool Contextual(Run run, int i, int type, int format, int sub, int flag, int markSet, int depth)
    {
        var t = _gpos;
        var gids = run.Gids;
        List<int>? Input(int count, Func<int, int, bool> matches)
        {
            var positions = new List<int> { i };
            for (int n = 1, k = i; n < count; n++)
            {
                k = Next(run, k, 1, flag, markSet);
                if (k >= gids.Count || !matches(n, gids[k])) return null;
                positions.Add(k);
            }
            return positions;
        }
        bool Around(int from, int step, int count, Func<int, int, bool> matches)
        {
            for (int n = 0, k = from; n < count; n++)
            {
                k = Next(run, k, step, flag, markSet);
                if (k < 0 || k >= gids.Count || !matches(n, gids[k])) return false;
            }
            return true;
        }
        bool Apply(List<int> input, int records, int count)
        {
            for (int r = 0; r < count; r++)
            {
                int sequenceIndex = BigEndian.U16(t, records + r * 4);
                if (sequenceIndex >= input.Count || Lookup(BigEndian.U16(t, records + r * 4 + 2)) is not { } lookup) continue;
                foreach (var (subType, s) in lookup.Subtables)
                    if (PositionAt(run, input[sequenceIndex], subType, s, lookup.Flag, lookup.MarkSet, depth + 1)) break;
            }
            return true;
        }
        bool Covered(int coverages, int n, int gid) => OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, coverages + n * 2), gid) >= 0;

        if (format == 3)
        {
            if (type == 7)
            {
                int count = BigEndian.U16(t, sub + 2), records = BigEndian.U16(t, sub + 4);
                if (count == 0 || !Covered(sub + 6, 0, gids[i]) || Input(count, (n, g) => Covered(sub + 6, n, g)) is not { } input) return false;
                return Apply(input, sub + 6 + count * 2, records);
            }
            int backCount = BigEndian.U16(t, sub + 2), backs = sub + 4;
            int inputCount = BigEndian.U16(t, backs + backCount * 2), inputs = backs + backCount * 2 + 2;
            int aheadCount = BigEndian.U16(t, inputs + inputCount * 2), aheads = inputs + inputCount * 2 + 2;
            int recordCount = BigEndian.U16(t, aheads + aheadCount * 2);
            if (inputCount == 0 || !Covered(inputs, 0, gids[i])) return false;
            if (!Around(i, -1, backCount, (n, g) => Covered(backs, n, g))) return false;
            if (Input(inputCount, (n, g) => Covered(inputs, n, g)) is not { } matched) return false;
            if (!Around(matched[^1], 1, aheadCount, (n, g) => Covered(aheads, n, g))) return false;
            return Apply(matched, aheads + aheadCount * 2 + 2, recordCount);
        }

        int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), gids[i]);
        if (coverage < 0 || format is not (1 or 2)) return false;
        int backClasses = 0, inputClasses = 0, aheadClasses = 0, setCount, sets;
        if (type == 7)
        {
            if (format == 2) inputClasses = sub + BigEndian.U16(t, sub + 4);
            setCount = BigEndian.U16(t, sub + (format == 2 ? 6 : 4));
            sets = sub + (format == 2 ? 8 : 6);
        }
        else if (format == 2)
        {
            backClasses = sub + BigEndian.U16(t, sub + 4);
            inputClasses = sub + BigEndian.U16(t, sub + 6);
            aheadClasses = sub + BigEndian.U16(t, sub + 8);
            setCount = BigEndian.U16(t, sub + 10);
            sets = sub + 12;
        }
        else
        {
            setCount = BigEndian.U16(t, sub + 4);
            sets = sub + 6;
        }
        int setIndex = format == 2 ? OpenTypeLayout.ClassOf(t, inputClasses, gids[i]) : coverage;
        if (setIndex >= setCount || BigEndian.U16(t, sets + setIndex * 2) == 0) return false;
        int set = sub + BigEndian.U16(t, sets + setIndex * 2);
        bool Is(int classDef, int value, int gid) => format == 2 ? OpenTypeLayout.ClassOf(t, classDef, gid) == value : gid == value;
        for (int r = 0; r < BigEndian.U16(t, set); r++)
        {
            int rule = set + BigEndian.U16(t, set + 2 + r * 2);
            if (type == 7)
            {
                int count = BigEndian.U16(t, rule), records = BigEndian.U16(t, rule + 2);
                if (count == 0 || Input(count, (n, g) => Is(inputClasses, BigEndian.U16(t, rule + 4 + (n - 1) * 2), g)) is not { } input) continue;
                return Apply(input, rule + 4 + (count - 1) * 2, records);
            }
            int backCount = BigEndian.U16(t, rule), backs = rule + 2;
            int inputCount = BigEndian.U16(t, backs + backCount * 2), inputs = backs + backCount * 2 + 2;
            int aheadAt = inputs + Math.Max(inputCount - 1, 0) * 2;
            int aheadCount = BigEndian.U16(t, aheadAt), aheads = aheadAt + 2;
            int recordCount = BigEndian.U16(t, aheads + aheadCount * 2);
            if (inputCount == 0) continue;
            if (!Around(i, -1, backCount, (n, g) => Is(backClasses, BigEndian.U16(t, backs + n * 2), g))) continue;
            if (Input(inputCount, (n, g) => Is(inputClasses, BigEndian.U16(t, inputs + (n - 1) * 2), g)) is not { } matched) continue;
            if (!Around(matched[^1], 1, aheadCount, (n, g) => Is(aheadClasses, BigEndian.U16(t, aheads + n * 2), g))) continue;
            return Apply(matched, aheads + aheadCount * 2 + 2, recordCount);
        }
        return false;
    }
}
