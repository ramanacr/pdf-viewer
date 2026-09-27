using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Editing;

/// <summary>A glyph being shaped: its id, the characters it stands for and its joining form.</summary>
internal sealed class ShapingGlyph
{
    public int Gid;
    /// <summary>The characters it shows (a ligature: all of them; a glyph a substitution split off: none).</summary>
    public string Text = string.Empty;
    /// <summary>The positional form it takes in cursive scripts: isol, init, medi, fina, or null.</summary>
    public string? Form;
}

/// <summary>Coverage and class definition tables, shared by GSUB, GPOS and GDEF.</summary>
internal static class OpenTypeLayout
{
    /// <summary>The glyph's index in a coverage table; -1 when it is not covered.</summary>
    public static int Coverage(byte[] t, int table, int glyph)
    {
        int format = BigEndian.U16(t, table), count = BigEndian.U16(t, table + 2);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (format == 1)
            {
                int g = BigEndian.U16(t, table + 4 + mid * 2);
                if (g == glyph) return mid;
                if (g < glyph) lo = mid + 1; else hi = mid - 1;
            }
            else if (format == 2)
            {
                int at = table + 4 + mid * 6;
                int start = BigEndian.U16(t, at), end = BigEndian.U16(t, at + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return BigEndian.U16(t, at + 4) + glyph - start;
            }
            else return -1;
        }
        return -1;
    }

    /// <summary>The glyph's class in a class definition table (0 when it has none).</summary>
    public static int ClassOf(byte[] t, int table, int glyph)
    {
        int format = BigEndian.U16(t, table);
        if (format == 1)
        {
            int start = BigEndian.U16(t, table + 2), count = BigEndian.U16(t, table + 4);
            return glyph >= start && glyph < start + count ? BigEndian.U16(t, table + 6 + (glyph - start) * 2) : 0;
        }
        if (format == 2)
        {
            int count = BigEndian.U16(t, table + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int at = table + 4 + mid * 6;
                int start = BigEndian.U16(t, at), end = BigEndian.U16(t, at + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return BigEndian.U16(t, at + 4);
            }
        }
        return 0;
    }

    /// <summary>The lookups a script's default language system turns on for the given features, with the feature each is for.</summary>
    public static SortedDictionary<int, string> Lookups(byte[] t, string script, IReadOnlyCollection<string> features)
    {
        var result = new SortedDictionary<int, string>();
        if (t.Length < 10 || BigEndian.U16(t, 0) != 1) return result;
        int scriptList = BigEndian.U16(t, 4), featureList = BigEndian.U16(t, 6);
        int scripts = BigEndian.U16(t, scriptList);
        int chosen = -1;
        foreach (string tag in new[] { script, "DFLT", "latn" })
        {
            for (int i = 0; i < scripts && chosen < 0; i++)
                if (Tag(t, scriptList + 2 + i * 6) == tag) chosen = scriptList + BigEndian.U16(t, scriptList + 2 + i * 6 + 4);
            if (chosen >= 0) break;
        }
        if (chosen < 0) return result;
        int langSys = BigEndian.U16(t, chosen) is > 0 and var d ? chosen + d
            : BigEndian.U16(t, chosen + 2) > 0 ? chosen + BigEndian.U16(t, chosen + 4 + 4) : -1;
        if (langSys < 0) return result;
        int featureCount = BigEndian.U16(t, featureList);
        var indices = new List<int>();
        int required = BigEndian.U16(t, langSys + 2);
        if (required != 0xFFFF) indices.Add(required);
        int count = BigEndian.U16(t, langSys + 4);
        for (int i = 0; i < count; i++) indices.Add(BigEndian.U16(t, langSys + 6 + i * 2));
        foreach (int index in indices)
        {
            if (index >= featureCount) continue;
            int rec = featureList + 2 + index * 6;
            string tag = Tag(t, rec);
            if (!features.Contains(tag)) continue;
            int feature = featureList + BigEndian.U16(t, rec + 4);
            int lookups = BigEndian.U16(t, feature + 2);
            for (int j = 0; j < lookups; j++) result.TryAdd(BigEndian.U16(t, feature + 4 + j * 2), tag);
        }
        return result;
    }

    public static string Tag(byte[] t, int at) => new(new[] { (char)t[at], (char)t[at + 1], (char)t[at + 2], (char)t[at + 3] });
}

/// <summary>
/// Glyph substitution (GSUB) for shaping new text: single, multiple and ligature substitutions
/// (also through extension lookups), for the features a script's default language system turns on.
/// Positional forms (isol, init, medi, fina) apply only to the glyphs whose joining form they are.
/// Contextual lookups are not applied.
/// </summary>
internal sealed class OpenTypeSubstitution
{
    private static readonly string[] Positional = { "isol", "init", "medi", "fina" };
    private readonly byte[] _gsub;
    private readonly byte[]? _gdef;

    private OpenTypeSubstitution(byte[] gsub, byte[]? gdef)
    {
        _gsub = gsub;
        _gdef = gdef;
    }

    public static OpenTypeSubstitution? Read(IReadOnlyDictionary<string, byte[]> tables) =>
        tables.TryGetValue("GSUB", out var gsub) && gsub.Length >= 10 ? new OpenTypeSubstitution(gsub, tables.GetValueOrDefault("GDEF")) : null;

    /// <summary>Applies the features' substitutions to a run of glyphs in logical order.</summary>
    public void Apply(List<ShapingGlyph> glyphs, string script, IReadOnlyCollection<string> features)
    {
        try
        {
            var t = _gsub;
            int lookupList = BigEndian.U16(t, 8), lookupCount = BigEndian.U16(t, lookupList);
            foreach (var (index, feature) in OpenTypeLayout.Lookups(t, script, features))
            {
                if (index >= lookupCount) continue;
                int lookup = lookupList + BigEndian.U16(t, lookupList + 2 + index * 2);
                int type = BigEndian.U16(t, lookup), flag = BigEndian.U16(t, lookup + 2), subtables = BigEndian.U16(t, lookup + 4);
                var subs = new List<(int Type, int Offset)>();
                for (int s = 0; s < subtables; s++)
                {
                    int sub = lookup + BigEndian.U16(t, lookup + 6 + s * 2);
                    if (type == 7) subs.Add((BigEndian.U16(t, sub + 2), sub + (int)Math.Min(BigEndian.U32(t, sub + 4), int.MaxValue)));
                    else subs.Add((type, sub));
                }
                bool positional = Positional.Contains(feature);
                for (int i = 0; i < glyphs.Count; i++)
                {
                    if (Ignored(glyphs[i].Gid, flag) || (positional && glyphs[i].Form != feature)) continue;
                    foreach (var (subType, sub) in subs)
                        if (ApplyAt(glyphs, i, subType, sub, flag)) break;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // A damaged table: what was substituted so far stands.
        }
    }

    private bool ApplyAt(List<ShapingGlyph> glyphs, int i, int type, int sub, int flag)
    {
        var t = _gsub;
        int format = BigEndian.U16(t, sub);
        int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), glyphs[i].Gid);
        if (coverage < 0) return false;
        switch (type)
        {
            case 1 when format == 1:
                glyphs[i].Gid = (glyphs[i].Gid + BigEndian.S16(t, sub + 4)) & 0xFFFF;
                return true;
            case 1 when format == 2:
                if (coverage >= BigEndian.U16(t, sub + 4)) return false;
                glyphs[i].Gid = BigEndian.U16(t, sub + 6 + coverage * 2);
                return true;
            case 2 when format == 1:
            {
                if (coverage >= BigEndian.U16(t, sub + 4)) return false;
                int sequence = sub + BigEndian.U16(t, sub + 6 + coverage * 2);
                int count = BigEndian.U16(t, sequence);
                if (count == 0) return false;
                var first = glyphs[i];
                first.Gid = BigEndian.U16(t, sequence + 2);
                for (int k = 1; k < count; k++)
                    glyphs.Insert(i + k, new ShapingGlyph { Gid = BigEndian.U16(t, sequence + 2 + k * 2), Form = first.Form });
                return true;
            }
            case 4 when format == 1:
            {
                if (coverage >= BigEndian.U16(t, sub + 4)) return false;
                int set = sub + BigEndian.U16(t, sub + 6 + coverage * 2);
                int ligatures = BigEndian.U16(t, set);
                for (int l = 0; l < ligatures; l++)
                {
                    int lig = set + BigEndian.U16(t, set + 2 + l * 2);
                    int ligGlyph = BigEndian.U16(t, lig), components = BigEndian.U16(t, lig + 2);
                    var matched = new List<int>();
                    int j = i;
                    bool ok = true;
                    for (int c = 1; c < components && ok; c++)
                    {
                        j++;
                        while (j < glyphs.Count && Ignored(glyphs[j].Gid, flag)) j++;
                        ok = j < glyphs.Count && glyphs[j].Gid == BigEndian.U16(t, lig + 4 + (c - 1) * 2);
                        if (ok) matched.Add(j);
                    }
                    if (!ok) continue;
                    var head = glyphs[i];
                    head.Gid = ligGlyph;
                    foreach (int m in matched) head.Text += glyphs[m].Text;
                    // The joined letters take the form the ligature ends with.
                    if (matched.Count > 0 && head.Form != null)
                    {
                        string? last = glyphs[matched[^1]].Form;
                        head.Form = (head.Form, last) switch { ("init", "fina") => "isol", ("medi", "fina") => "fina", ("init", "medi") => "init", _ => head.Form };
                    }
                    for (int m = matched.Count - 1; m >= 0; m--) glyphs.RemoveAt(matched[m]);
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    // Lookup flags: 0x2 ignores base glyphs, 0x4 ligatures, 0x8 marks (GDEF classes 1, 2, 3).
    private bool Ignored(int gid, int flag)
    {
        if ((flag & 0x0E) == 0 || _gdef == null || _gdef.Length < 6) return false;
        int classDef = BigEndian.U16(_gdef, 4);
        if (classDef == 0) return false;
        int cls = OpenTypeLayout.ClassOf(_gdef, classDef, gid);
        return (cls == 1 && (flag & 0x2) != 0) || (cls == 2 && (flag & 0x4) != 0) || (cls == 3 && (flag & 0x8) != 0);
    }
}
