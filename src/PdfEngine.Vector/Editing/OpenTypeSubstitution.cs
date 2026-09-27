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
    /// <summary>Which masked features may apply to it (bits a shaper gives out; see <see cref="OpenTypeSubstitution.Apply(List{ShapingGlyph}, string, IReadOnlyCollection{string}, IReadOnlyDictionary{string, uint}?, IReadOnlyCollection{string}?)"/>).</summary>
    public uint Mask = uint.MaxValue;
    /// <summary>The syllable it is part of, for features whose matches stay within one.</summary>
    public int Syllable;
    /// <summary>A shaper's classes for the character it came from (Indic: its category and position).</summary>
    public int Category, Position;
    /// <summary>A substitution changed it; it is a ligature; a multiple substitution made it.</summary>
    public bool Substituted, Ligated, Multiplied;

    /// <summary>Another glyph with this one's properties.</summary>
    public ShapingGlyph Copy(int gid) => new()
    {
        Gid = gid, Form = Form, Mask = Mask, Syllable = Syllable, Category = Category, Position = Position,
        Substituted = Substituted, Ligated = Ligated, Multiplied = Multiplied,
    };
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

    // The tags the first Indic specification used, for fonts made for it (the v2 tags are tried first).
    private static readonly Dictionary<string, string> OldScriptTags = new(StringComparer.Ordinal)
    {
        ["dev2"] = "deva", ["bng2"] = "beng", ["gur2"] = "guru", ["gjr2"] = "gujr", ["ory2"] = "orya",
        ["tml2"] = "taml", ["tel2"] = "telu", ["knd2"] = "knda", ["mlm2"] = "mlym", ["mym2"] = "mymr",
    };

    /// <summary>
    /// The script the table's lookups are taken from, and its offset: the script's own tag, else the
    /// older tag of an Indic script, else DFLT, else latn; null when the table has none of them.
    /// </summary>
    public static (string Tag, int Offset)? Script(byte[] t, string script)
    {
        if (t.Length < 10 || BigEndian.U16(t, 0) != 1) return null;
        int scriptList = BigEndian.U16(t, 4);
        int scripts = BigEndian.U16(t, scriptList);
        foreach (string tag in OldScriptTags.TryGetValue(script, out var old) ? new[] { script, old, "DFLT", "latn" } : new[] { script, "DFLT", "latn" })
            for (int i = 0; i < scripts; i++)
                if (Tag(t, scriptList + 2 + i * 6) == tag) return (tag, scriptList + BigEndian.U16(t, scriptList + 2 + i * 6 + 4));
        return null;
    }

    /// <summary>The lookups a script's default language system turns on for the given features, with the feature each is for.</summary>
    public static SortedDictionary<int, string> Lookups(byte[] t, string script, IReadOnlyCollection<string> features)
    {
        var result = new SortedDictionary<int, string>();
        if (Script(t, script) is not { } found) return result;
        int featureList = BigEndian.U16(t, 6);
        int chosen = found.Offset;
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

    /// <summary>
    /// Whether a lookup skips a glyph: flag 0x2 skips base glyphs, 0x4 ligatures, 0x8 marks (GDEF
    /// classes 1, 2, 3); of the other marks, 0x10 keeps those in mark set <paramref name="markSet"/>
    /// and a mark attachment type (the high byte) those of that attachment class.
    /// </summary>
    public static bool Ignored(byte[]? gdef, int gid, int flag, int markSet)
    {
        if ((flag & 0xFF1E) == 0 || gdef == null || gdef.Length < 6) return false;
        int classDef = BigEndian.U16(gdef, 4);
        if (classDef == 0) return false;
        int cls = ClassOf(gdef, classDef, gid);
        if ((cls == 1 && (flag & 0x2) != 0) || (cls == 2 && (flag & 0x4) != 0) || (cls == 3 && (flag & 0x8) != 0)) return true;
        if (cls != 3) return false;
        if ((flag & 0x10) != 0 && markSet >= 0 && gdef.Length >= 14 && BigEndian.U32(gdef, 0) >= 0x00010002 && BigEndian.U16(gdef, 12) is > 0 and var sets)
        {
            int set = sets;
            if (markSet < BigEndian.U16(gdef, set + 2))
                return Coverage(gdef, set + (int)Math.Min(BigEndian.U32(gdef, set + 4 + markSet * 4), int.MaxValue), gid) < 0;
            return false;
        }
        if ((flag & 0xFF00) != 0 && gdef.Length >= 12 && BigEndian.U16(gdef, 10) is > 0 and var attach)
            return ClassOf(gdef, attach, gid) != flag >> 8;
        return false;
    }

    public static string Tag(byte[] t, int at) => new(new[] { (char)t[at], (char)t[at + 1], (char)t[at + 2], (char)t[at + 3] });
}

/// <summary>
/// Glyph substitution (GSUB) for shaping new text: single, multiple and ligature substitutions, and
/// contextual and chained contextual ones that apply them where a sequence matches (all three
/// formats), also through extension lookups, for the features a script's default language system
/// turns on. Positional forms (isol, init, medi, fina) apply only to the glyphs whose joining form they are.
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
    public void Apply(List<ShapingGlyph> glyphs, string script, IReadOnlyCollection<string> features) => Apply(glyphs, script, features, null, null);

    /// <summary>
    /// Applies the features' substitutions to a run of glyphs in logical order. A feature in
    /// <paramref name="masks"/> applies only to glyphs whose <see cref="ShapingGlyph.Mask"/> has its
    /// bits (the glyph a lookup starts at and those it joins); the matches of a feature in
    /// <paramref name="bySyllable"/> stay within one syllable.
    /// </summary>
    public void Apply(List<ShapingGlyph> glyphs, string script, IReadOnlyCollection<string> features, IReadOnlyDictionary<string, uint>? masks, IReadOnlyCollection<string>? bySyllable)
    {
        try
        {
            var t = _gsub;
            foreach (var (index, feature) in OpenTypeLayout.Lookups(t, script, features))
            {
                if (Lookup(index) is not { } lookup) continue;
                uint mask = masks != null && masks.TryGetValue(feature, out uint m) ? m : 0;
                bool positional = mask == 0 && Positional.Contains(feature); // a mask says where 'init' applies instead
                bool syllable = bySyllable?.Contains(feature) == true;
                for (int i = 0; i < glyphs.Count; i++)
                {
                    if (Ignored(glyphs[i].Gid, lookup.Flag, lookup.MarkSet) || (positional && glyphs[i].Form != feature) || (glyphs[i].Mask & mask) != mask) continue;
                    var scope = new Scope(mask, syllable ? glyphs[i].Syllable : -1);
                    foreach (var (subType, sub) in lookup.Subtables)
                        if (ApplyAt(glyphs, i, subType, sub, lookup.Flag, lookup.MarkSet, 0, scope)) break;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // A damaged table: what was substituted so far stands.
        }
    }

    /// <summary>
    /// Whether the feature would substitute exactly this glyph sequence, with nothing around it (how
    /// shapers ask a font whether a consonant has a reph, below-base or post-base form).
    /// </summary>
    public bool WouldSubstitute(string script, string feature, params int[] gids)
    {
        try
        {
            foreach (var (index, _) in OpenTypeLayout.Lookups(_gsub, script, new[] { feature }))
                if (Lookup(index) is { } lookup)
                    foreach (var (type, sub) in lookup.Subtables)
                        if (WouldApply(type, sub, gids)) return true;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
        }
        return false;
    }

    /// <summary>The script's default language system has the feature.</summary>
    public bool Has(string script, string feature) => OpenTypeLayout.Lookups(_gsub, script, new[] { feature }).Count > 0;

    /// <summary>The script tag the substitutions for a script come from (see <see cref="OpenTypeLayout.Script"/>).</summary>
    public string? ScriptTag(string script) => OpenTypeLayout.Script(_gsub, script)?.Tag;

    /// <summary>What a match may take in: glyphs with the feature's mask bits (its input), in one syllable (-1: any).</summary>
    private readonly record struct Scope(uint Mask, int Syllable)
    {
        public bool Admits(ShapingGlyph g, bool input) => (!input || (g.Mask & Mask) == Mask) && (Syllable < 0 || g.Syllable == Syllable);
    }

    /// <summary>A lookup's flag, its mark filtering set (-1: none) and its subtables (extension subtables resolved), or null when there is no such lookup.</summary>
    private (int Flag, int MarkSet, List<(int Type, int Offset)> Subtables)? Lookup(int index)
    {
        var t = _gsub;
        int lookupList = BigEndian.U16(t, 8), lookupCount = BigEndian.U16(t, lookupList);
        if (index < 0 || index >= lookupCount) return null;
        int lookup = lookupList + BigEndian.U16(t, lookupList + 2 + index * 2);
        int type = BigEndian.U16(t, lookup), flag = BigEndian.U16(t, lookup + 2), subtables = BigEndian.U16(t, lookup + 4);
        var subs = new List<(int Type, int Offset)>();
        for (int s = 0; s < subtables; s++)
        {
            int sub = lookup + BigEndian.U16(t, lookup + 6 + s * 2);
            if (type == 7) subs.Add((BigEndian.U16(t, sub + 2), sub + (int)Math.Min(BigEndian.U32(t, sub + 4), int.MaxValue)));
            else subs.Add((type, sub));
        }
        int markSet = (flag & 0x10) != 0 ? BigEndian.U16(t, lookup + 6 + subtables * 2) : -1;
        return (flag, markSet, subs);
    }

    private bool WouldApply(int type, int sub, int[] gids)
    {
        var t = _gsub;
        if (gids.Length == 0) return false;
        int format = BigEndian.U16(t, sub);
        if (type is 5 or 6)
        {
            if (format == 3)
            {
                int at = sub + 2;
                if (type == 6)
                {
                    if (BigEndian.U16(t, at) != 0) return false; // no backtrack
                    at += 2;
                }
                int count = BigEndian.U16(t, at);
                if (count != gids.Length) return false;
                if (type == 5) at += 2; // the lookup count
                for (int n = 0; n < count; n++)
                    if (OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, at + 2 + n * 2), gids[n]) < 0) return false;
                return type == 5 || BigEndian.U16(t, at + 2 + count * 2) == 0; // no lookahead
            }
            int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), gids[0]);
            if (coverage < 0 || format is not (1 or 2)) return false;
            int inputClasses = format != 2 ? 0 : sub + BigEndian.U16(t, sub + (type == 5 ? 4 : 6));
            int setCount = BigEndian.U16(t, sub + (type, format) switch { (5, 1) => 4, (5, _) => 6, (_, 1) => 4, _ => 10 });
            int sets = sub + (type, format) switch { (5, 1) => 6, (5, _) => 8, (_, 1) => 6, _ => 12 };
            int setIndex = format == 2 ? OpenTypeLayout.ClassOf(t, inputClasses, gids[0]) : coverage;
            if (setIndex >= setCount || BigEndian.U16(t, sets + setIndex * 2) == 0) return false;
            int set = sub + BigEndian.U16(t, sets + setIndex * 2);
            for (int r = 0; r < BigEndian.U16(t, set); r++)
            {
                int rule = set + BigEndian.U16(t, set + 2 + r * 2);
                if (type == 6)
                {
                    if (BigEndian.U16(t, rule) != 0) continue;
                    rule += 2;
                }
                int count = BigEndian.U16(t, rule);
                if (count != gids.Length) continue;
                int inputs = rule + (type == 5 ? 4 : 2);
                bool ok = true;
                for (int n = 1; n < count && ok; n++)
                {
                    int value = BigEndian.U16(t, inputs + (n - 1) * 2);
                    ok = format == 2 ? OpenTypeLayout.ClassOf(t, inputClasses, gids[n]) == value : gids[n] == value;
                }
                if (ok && (type == 5 || BigEndian.U16(t, inputs + (count - 1) * 2) == 0)) return true;
            }
            return false;
        }
        int covered = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), gids[0]);
        if (covered < 0) return false;
        if (type is 1 or 2 or 3) return gids.Length == 1;
        if (type != 4 || format != 1 || covered >= BigEndian.U16(t, sub + 4)) return false;
        int ligSet = sub + BigEndian.U16(t, sub + 6 + covered * 2);
        for (int l = 0; l < BigEndian.U16(t, ligSet); l++)
        {
            int lig = ligSet + BigEndian.U16(t, ligSet + 2 + l * 2);
            if (BigEndian.U16(t, lig + 2) != gids.Length) continue;
            bool ok = true;
            for (int c = 1; c < gids.Length && ok; c++) ok = BigEndian.U16(t, lig + 4 + (c - 1) * 2) == gids[c];
            if (ok) return true;
        }
        return false;
    }

    private bool ApplyAt(List<ShapingGlyph> glyphs, int i, int type, int sub, int flag, int markSet, int depth, Scope scope)
    {
        var t = _gsub;
        int format = BigEndian.U16(t, sub);
        if (type is 5 or 6) return depth < 4 && Contextual(glyphs, i, type, format, sub, flag, markSet, depth, scope);
        int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), glyphs[i].Gid);
        if (coverage < 0) return false;
        switch (type)
        {
            case 1 when format == 1:
                glyphs[i].Gid = (glyphs[i].Gid + BigEndian.S16(t, sub + 4)) & 0xFFFF;
                glyphs[i].Substituted = true;
                return true;
            case 1 when format == 2:
                if (coverage >= BigEndian.U16(t, sub + 4)) return false;
                glyphs[i].Gid = BigEndian.U16(t, sub + 6 + coverage * 2);
                glyphs[i].Substituted = true;
                return true;
            case 2 when format == 1:
            {
                if (coverage >= BigEndian.U16(t, sub + 4)) return false;
                int sequence = sub + BigEndian.U16(t, sub + 6 + coverage * 2);
                int count = BigEndian.U16(t, sequence);
                if (count == 0) return false;
                var first = glyphs[i];
                first.Gid = BigEndian.U16(t, sequence + 2);
                first.Substituted = true;
                first.Multiplied |= count > 1;
                for (int k = 1; k < count; k++)
                    glyphs.Insert(i + k, first.Copy(BigEndian.U16(t, sequence + 2 + k * 2)));
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
                        while (j < glyphs.Count && Ignored(glyphs[j].Gid, flag, markSet)) j++;
                        ok = j < glyphs.Count && scope.Admits(glyphs[j], true) && glyphs[j].Gid == BigEndian.U16(t, lig + 4 + (c - 1) * 2);
                        if (ok) matched.Add(j);
                    }
                    if (!ok) continue;
                    var head = glyphs[i];
                    head.Gid = ligGlyph;
                    head.Substituted = head.Ligated = true;
                    head.Multiplied = false; // as Uniscribe does: of joining and splitting, the last counts
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

    // ------------------------------------------------------------------ contextual (5) and chained contextual (6)

    /// <summary>
    /// Matches a rule at glyph <paramref name="i"/> (the input sequence from there, and for chained
    /// rules the glyphs before and after it) and applies its lookups at their input positions.
    /// </summary>
    private bool Contextual(List<ShapingGlyph> glyphs, int i, int type, int format, int sub, int flag, int markSet, int depth, Scope scope)
    {
        var t = _gsub;
        // Glyphs the lookup sees: those its flag does not skip.
        int Next(int from, int step)
        {
            int k = from + step;
            while (k >= 0 && k < glyphs.Count && Ignored(glyphs[k].Gid, flag, markSet)) k += step;
            return k;
        }
        // The positions of a sequence that matches, or null.
        List<int>? Input(int count, Func<int, int, bool> matches)
        {
            var positions = new List<int> { i };
            int k = i;
            for (int n = 1; n < count; n++)
            {
                k = Next(k, 1);
                if (k >= glyphs.Count || !scope.Admits(glyphs[k], true) || !matches(n, glyphs[k].Gid)) return null;
                positions.Add(k);
            }
            return positions;
        }
        bool Before(int count, Func<int, int, bool> matches)
        {
            int k = i;
            for (int n = 0; n < count; n++)
            {
                k = Next(k, -1);
                if (k < 0 || !scope.Admits(glyphs[k], false) || !matches(n, glyphs[k].Gid)) return false;
            }
            return true;
        }
        bool After(int last, int count, Func<int, int, bool> matches)
        {
            int k = last;
            for (int n = 0; n < count; n++)
            {
                k = Next(k, 1);
                if (k >= glyphs.Count || !scope.Admits(glyphs[k], false) || !matches(n, glyphs[k].Gid)) return false;
            }
            return true;
        }

        if (format == 3)
        {
            if (type == 5)
            {
                int count = BigEndian.U16(t, sub + 2), records = BigEndian.U16(t, sub + 4);
                bool Covered(int n, int gid) => OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 6 + n * 2), gid) >= 0;
                if (count == 0 || !Covered(0, glyphs[i].Gid) || Input(count, Covered) is not { } input) return false;
                return ApplyRecords(glyphs, input, sub + 6 + count * 2, records, depth, scope);
            }
            int at = sub + 2;
            int backCount = BigEndian.U16(t, at);
            int backs = at + 2;
            at = backs + backCount * 2;
            int inputCount = BigEndian.U16(t, at);
            int inputs = at + 2;
            at = inputs + inputCount * 2;
            int aheadCount = BigEndian.U16(t, at);
            int aheads = at + 2;
            at = aheads + aheadCount * 2;
            int recordCount = BigEndian.U16(t, at);
            bool InputCovered(int n, int gid) => OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, inputs + n * 2), gid) >= 0;
            if (inputCount == 0 || !InputCovered(0, glyphs[i].Gid)) return false;
            if (!Before(backCount, (n, gid) => OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, backs + n * 2), gid) >= 0)) return false;
            if (Input(inputCount, InputCovered) is not { } matched) return false;
            if (!After(matched[^1], aheadCount, (n, gid) => OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, aheads + n * 2), gid) >= 0)) return false;
            return ApplyRecords(glyphs, matched, at + 2, recordCount, depth, scope);
        }

        int coverage = OpenTypeLayout.Coverage(t, sub + BigEndian.U16(t, sub + 2), glyphs[i].Gid);
        if (coverage < 0 || format is not (1 or 2)) return false;
        int backClasses = 0, inputClasses, aheadClasses = 0, setCount, sets;
        if (type == 5)
        {
            inputClasses = format == 2 ? sub + BigEndian.U16(t, sub + 4) : 0;
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
            inputClasses = 0;
            setCount = BigEndian.U16(t, sub + 4);
            sets = sub + 6;
        }
        // Rules are grouped by the first glyph: its coverage index, or (format 2) its class.
        int setIndex = format == 2 ? OpenTypeLayout.ClassOf(t, inputClasses, glyphs[i].Gid) : coverage;
        if (setIndex >= setCount) return false;
        int setOffset = BigEndian.U16(t, sets + setIndex * 2);
        if (setOffset == 0) return false;
        int set = sub + setOffset;
        int rules = BigEndian.U16(t, set);
        for (int r = 0; r < rules; r++)
        {
            int rule = set + BigEndian.U16(t, set + 2 + r * 2);
            bool Is(int classDef, int value, int gid) => format == 2 ? OpenTypeLayout.ClassOf(t, classDef, gid) == value : gid == value;
            if (type == 5)
            {
                int count = BigEndian.U16(t, rule), records = BigEndian.U16(t, rule + 2);
                if (Input(count, (n, gid) => Is(inputClasses, BigEndian.U16(t, rule + 4 + (n - 1) * 2), gid)) is not { } input) continue;
                return ApplyRecords(glyphs, input, rule + 4 + (count - 1) * 2, records, depth, scope);
            }
            int at = rule;
            int backCount = BigEndian.U16(t, at);
            int backs = at + 2;
            at = backs + backCount * 2;
            int inputCount = BigEndian.U16(t, at);
            int inputs = at + 2;
            at = inputs + Math.Max(inputCount - 1, 0) * 2;
            int aheadCount = BigEndian.U16(t, at);
            int aheads = at + 2;
            at = aheads + aheadCount * 2;
            int recordCount = BigEndian.U16(t, at);
            if (inputCount == 0) continue;
            if (!Before(backCount, (n, gid) => Is(backClasses, BigEndian.U16(t, backs + n * 2), gid))) continue;
            if (Input(inputCount, (n, gid) => Is(inputClasses, BigEndian.U16(t, inputs + (n - 1) * 2), gid)) is not { } matched) continue;
            if (!After(matched[^1], aheadCount, (n, gid) => Is(aheadClasses, BigEndian.U16(t, aheads + n * 2), gid))) continue;
            return ApplyRecords(glyphs, matched, at + 2, recordCount, depth, scope);
        }
        return false;
    }

    // Each record applies a lookup at one input position (later positions shift when a lookup
    // splits or joins glyphs).
    private bool ApplyRecords(List<ShapingGlyph> glyphs, List<int> input, int records, int count, int depth, Scope scope)
    {
        var t = _gsub;
        for (int r = 0; r < count; r++)
        {
            int sequenceIndex = BigEndian.U16(t, records + r * 4), lookupIndex = BigEndian.U16(t, records + r * 4 + 2);
            if (sequenceIndex >= input.Count || Lookup(lookupIndex) is not { } lookup) continue;
            int at = input[sequenceIndex];
            if (at >= glyphs.Count) continue;
            int before = glyphs.Count;
            foreach (var (subType, sub) in lookup.Subtables)
                if (ApplyAt(glyphs, at, subType, sub, lookup.Flag, lookup.MarkSet, depth + 1, scope)) break;
            int change = glyphs.Count - before;
            if (change != 0)
                for (int k = sequenceIndex + 1; k < input.Count; k++) input[k] += change;
        }
        return true;
    }

    private bool Ignored(int gid, int flag, int markSet) => OpenTypeLayout.Ignored(_gdef, gid, flag, markSet);
}
