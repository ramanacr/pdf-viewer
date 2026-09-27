using System;
using System.Collections.Generic;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Mark positioning (GPOS mark-to-base, mark-to-ligature and mark-to-mark attachment, also through
/// extension lookups) for shaping new text: each mark is placed on the anchor of the glyph it sits
/// on, and does not advance. Offsets are in font units, from where the glyph would otherwise be drawn.
/// </summary>
internal sealed class OpenTypePositioning
{
    private static readonly string[] MarkFeatures = { "mark", "mkmk" };
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
    /// Places the marks of a run in logical order. <paramref name="advances"/> are the glyphs' advances
    /// (font units); a mark attached to a glyph gets its advance set to 0 and its offset set so it
    /// lands on the anchor.
    /// </summary>
    public void Apply(IReadOnlyList<int> gids, string script, double[] advances, double[] dx, double[] dy)
    {
        try
        {
            // Marks do not advance (GDEF says which glyphs are marks), attached or not.
            for (int i = 0; i < gids.Count; i++) if (GlyphClass(gids[i]) == 3) advances[i] = 0;
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
}
