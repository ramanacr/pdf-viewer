using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Shapes the Indic scripts (Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada,
/// Malayalam) the way the OpenType Indic shaping specification describes and Uniscribe and HarfBuzz
/// do: the text is cut into syllables, each syllable's base consonant is found, pre-base matras and
/// the parts of the syllable are put in drawing order, the basic features are applied one at a time
/// to the glyphs each is for (reph only to the reph, half forms only before the base), reph and
/// pre-base matras are moved to their final places, then the presentation features and positioning
/// apply. The v2 script tags are used, the first specification's tags when the font has only those.
/// </summary>
internal static class IndicShaper
{
    // Character categories (each a letter in the syllable patterns below) and the positions the
    // reordering sorts a syllable by, as the specification and HarfBuzz name them.
    private const int X = 0, C = 1, Ra = 2, V = 3, N = 4, H = 5, ZWNJ = 6, ZWJ = 7, M = 8, SM = 9, A = 10, Placeholder = 11, DottedCircle = 12, Symbol = 13, Repha = 14;
    private const string Letters = "xcrvnhzjmsapdyR";
    private const int Start = 0, RaToBecomeReph = 1, PreM = 2, PreC = 3, BaseC = 4, AfterMain = 5, AboveC = 6, BeforeSub = 7, BelowC = 8,
        AfterSub = 9, BeforePost = 10, PostC = 11, AfterPost = 12, FinalC = 13, Smvd = 14, End = 15;

    // The mask bits of the features that apply to some glyphs of a syllable only.
    private const uint Rphf = 1, Pref = 2, Blwf = 4, Abvf = 8, Half = 16, Pstf = 32, Init = 64;
    private static readonly Dictionary<string, uint> Masks = new(StringComparer.Ordinal)
    {
        ["rphf"] = Rphf, ["pref"] = Pref, ["blwf"] = Blwf, ["abvf"] = Abvf, ["half"] = Half, ["pstf"] = Pstf, ["init"] = Init,
    };
    private static readonly string[] LocalFeatures = { "locl", "ccmp" };
    private static readonly string[] BasicFeatures = { "nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "abvf", "half", "pstf", "vatu", "cjct" };
    private static readonly string[] PresentationFeatures = { "init", "pres", "abvs", "blws", "psts", "haln", "rlig", "calt", "liga", "clig", "rclt" };
    private static readonly string[] SyllableFeatures = { "locl", "ccmp", "init", "pres", "abvs", "blws", "psts", "haln" };
    private static readonly string[] PositionFeatures = { "abvm", "blwm", "mark", "mkmk", "dist", "kern" };

    private enum RephPlace { AfterMain, BeforeSub, AfterSub, BeforePost, AfterPost }
    private enum RephMode { Implicit, Explicit, Logical }

    private sealed record Script(string Tag, string OldTag, int Block, int Virama, RephPlace RephPlace, RephMode RephMode, bool BlwfPostOnly);

    private static readonly Script[] Scripts =
    {
        new("dev2", "deva", 0x0900, 0x094D, RephPlace.BeforePost, RephMode.Implicit, false),
        new("bng2", "beng", 0x0980, 0x09CD, RephPlace.AfterSub, RephMode.Implicit, false),
        new("gur2", "guru", 0x0A00, 0x0A4D, RephPlace.BeforeSub, RephMode.Implicit, false),
        new("gjr2", "gujr", 0x0A80, 0x0ACD, RephPlace.BeforePost, RephMode.Implicit, false),
        new("ory2", "orya", 0x0B00, 0x0B4D, RephPlace.AfterMain, RephMode.Implicit, false),
        new("tml2", "taml", 0x0B80, 0x0BCD, RephPlace.AfterPost, RephMode.Implicit, false),
        new("tel2", "telu", 0x0C00, 0x0C4D, RephPlace.AfterPost, RephMode.Explicit, true),
        new("knd2", "knda", 0x0C80, 0x0CCD, RephPlace.AfterPost, RephMode.Implicit, true),
        new("mlm2", "mlym", 0x0D00, 0x0D4D, RephPlace.AfterMain, RephMode.Logical, false),
    };

    private enum Kind { Consonant, Vowel, Standalone, Symbol, Broken, Other }

    // The syllable grammar of HarfBuzz's Indic machine, over the category letters (without the
    // rarer categories: consonant medials, stackers and the like are taken as other characters).
    private const string Cn = "[cr]j?(?:nn?)?";
    private const string HalantGroup = "[jz]?h(?:jn?)?";
    private const string Tail = "(?:[jz]?ss?z?)?a*";
    private const string Complex = "(?:" + HalantGroup + Cn + ")*(?:hz|" + HalantGroup + "|(?:[jz]*mn?h?)*)" + Tail;
    private const string Reph = "(?:rh|R)";
    private static readonly (Kind Kind, Regex Pattern)[] Syllables =
    {
        (Kind.Consonant, Whole("R?" + Cn + Complex)),
        (Kind.Vowel, Whole(Reph + "?v(?:nn?)?(?:j|" + Complex + ")")),
        (Kind.Standalone, Whole("(?:R?p|" + Reph + "?d)(?:nn?)?" + Complex)),
        (Kind.Symbol, Whole("yn?" + Tail)),
        (Kind.Broken, Whole(Reph + "?(?:nn?)?" + Complex)),
    };

    private static Regex Whole(string pattern) => new("^(?:" + pattern + ")$", RegexOptions.CultureInvariant);

    /// <summary>The text has a letter of an Indic script, so <see cref="Shape"/> shapes it.</summary>
    public static bool Handles(string text) => ScriptOf(text) != null;

    private static Script? ScriptOf(string text)
    {
        foreach (char c in text)
            if (c is >= 'ऀ' and <= 'ൿ')
                return Scripts[(c - 0x0900) >> 7];
        return null;
    }

    /// <summary>Shapes a run of one font and one embedding level: glyphs in drawing order, left to right.</summary>
    public static List<TextShaper.Shaped> Shape(string text, TrueTypeFontFile font, int level)
    {
        var script = ScriptOf(text) ?? Scripts[0];
        var gsub = font.Substitution;
        var plan = new Plan(script, gsub, font);

        // One glyph per character, the split matras split (as their canonical decompositions are).
        var glyphs = new List<ShapingGlyph>();
        var sources = new List<string>();
        for (int at = 0; at < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, at);
            string source = char.ConvertFromUtf32(cp);
            at += source.Length;
            var parts = Decomposed(cp, font) ?? new[] { cp };
            for (int k = 0; k < parts.Length; k++)
            {
                var (category, position) = Classify(parts[k]);
                glyphs.Add(new ShapingGlyph { Gid = font.GlyphFor(parts[k]), Text = char.ConvertFromUtf32(parts[k]), Category = category, Position = position, Mask = 0 });
                sources.Add(k == 0 ? source : string.Empty);
            }
        }
        // Consonants that have below-base or post-base forms in this font.
        var positions = new Dictionary<int, int>();
        foreach (var g in glyphs)
            if (g.Position == BaseC && g.Gid > 0)
            {
                if (!positions.TryGetValue(g.Gid, out int p)) positions[g.Gid] = p = plan.ConsonantPosition(g.Gid);
                g.Position = p;
            }

        var kinds = Segment(glyphs, sources, out var logical);
        InsertDottedCircles(glyphs, kinds, font);
        if (gsub != null) gsub.Apply(glyphs, plan.Tag, LocalFeatures, Masks, SyllableFeatures);
        foreach (var (start, end) in Ranges(glyphs))
            if (kinds[glyphs[start].Syllable] is not (Kind.Symbol or Kind.Other)) InitialReorder(glyphs, start, end, plan);
        if (gsub != null)
            foreach (string feature in BasicFeatures)
                gsub.Apply(glyphs, plan.Tag, new[] { feature }, Masks, BasicFeatures);
        foreach (var (start, end) in Ranges(glyphs))
            if (kinds[glyphs[start].Syllable] is not (Kind.Symbol or Kind.Other)) FinalReorder(glyphs, start, end, plan);
        if (gsub != null) gsub.Apply(glyphs, plan.Tag, PresentationFeatures, Masks, SyllableFeatures);
        // Joiners have done their work; they are not drawn.
        glyphs.RemoveAll(g => (g.Category is ZWJ or ZWNJ && !g.Ligated) || (g.Gid <= 0 && g.Text.Length == 0));

        var gids = glyphs.Select(g => g.Gid).ToArray();
        var advances = gids.Select(gid => (double)font.AdvanceUnits(gid)).ToArray();
        var dx = new double[gids.Length];
        var dy = new double[gids.Length];
        var own = (double[])advances.Clone();
        font.Positioning?.Apply(gids, plan.Tag, PositionFeatures, advances, dx, dy);

        var result = new List<TextShaper.Shaped>(glyphs.Count);
        foreach (var (start, end) in Ranges(glyphs))
        {
            // Glyphs that do not read, in drawing order, as the syllable's characters carry them as replacement text.
            string drawn = string.Concat(glyphs.Skip(start).Take(end - start).Select(g => g.Text));
            string reads = logical[glyphs[start].Syllable];
            var actual = drawn != reads || glyphs.Skip(start).Take(end - start).Any(g => g.Text.Length == 0) ? new ActualTextSpan(reads) : null;
            for (int k = start; k < end; k++)
            {
                bool moved = dx[k] != 0 || dy[k] != 0 || advances[k] != own[k];
                result.Add(moved
                    ? new TextShaper.Shaped(gids[k], glyphs[k].Text, level, font.ToThousandths((int)Math.Round(dx[k])), font.ToThousandths((int)Math.Round(dy[k])), font.ToThousandths((int)Math.Round(advances[k])), actual, Kerned: true)
                    : new TextShaper.Shaped(gids[k], glyphs[k].Text, level, Actual: actual, Kerned: true));
            }
        }
        return result;
    }

    /// <summary>What the shaper knows of the font for one script.</summary>
    private sealed class Plan
    {
        public readonly Script Script;
        public readonly string Tag;
        public readonly bool OldSpec, HasRphf, HasPref;
        public readonly int Virama;
        private readonly OpenTypeSubstitution? _gsub;

        public Plan(Script script, OpenTypeSubstitution? gsub, TrueTypeFontFile font)
        {
            Script = script;
            _gsub = gsub;
            Tag = script.Tag;
            // A font made for the first specification (its tag is the old one) orders halants its way.
            OldSpec = gsub?.ScriptTag(script.Tag) == script.OldTag;
            HasRphf = gsub?.Has(Tag, "rphf") == true;
            HasPref = gsub?.Has(Tag, "pref") == true;
            Virama = font.GlyphFor(script.Virama);
        }

        public bool Would(string feature, params int[] gids) => _gsub != null && _gsub.WouldSubstitute(Tag, feature, gids);

        // Whether a consonant takes a below-base or post-base form after a virama (or, as some fonts
        // made for the first specification have it, before one).
        public int ConsonantPosition(int consonant)
        {
            if (_gsub == null || Virama <= 0) return BaseC;
            bool Either(string feature) => Would(feature, Virama, consonant) || Would(feature, consonant, Virama);
            if (Either("blwf") || Either("vatu")) return BelowC;
            if (Either("pstf") || Either("pref")) return PostC;
            return BaseC;
        }
    }

    // ------------------------------------------------------------------ characters

    // Split matras and nukta letters the font draws from their parts (those Unicode does not compose again).
    private static int[]? Decomposed(int cp, TrueTypeFontFile font)
    {
        int[]? parts = cp switch
        {
            0x0958 => new[] { 0x0915, 0x093C }, 0x0959 => new[] { 0x0916, 0x093C }, 0x095A => new[] { 0x0917, 0x093C }, 0x095B => new[] { 0x091C, 0x093C },
            0x095C => new[] { 0x0921, 0x093C }, 0x095D => new[] { 0x0922, 0x093C }, 0x095E => new[] { 0x092B, 0x093C }, 0x095F => new[] { 0x092F, 0x093C },
            0x09CB => new[] { 0x09C7, 0x09BE }, 0x09CC => new[] { 0x09C7, 0x09D7 },
            0x0A33 => new[] { 0x0A32, 0x0A3C }, 0x0A36 => new[] { 0x0A38, 0x0A3C }, 0x0A59 => new[] { 0x0A16, 0x0A3C }, 0x0A5A => new[] { 0x0A17, 0x0A3C },
            0x0A5B => new[] { 0x0A1C, 0x0A3C }, 0x0A5E => new[] { 0x0A2B, 0x0A3C },
            0x0B48 => new[] { 0x0B47, 0x0B56 }, 0x0B4B => new[] { 0x0B47, 0x0B3E }, 0x0B4C => new[] { 0x0B47, 0x0B57 },
            0x0B5C => new[] { 0x0B21, 0x0B3C }, 0x0B5D => new[] { 0x0B22, 0x0B3C },
            0x0BCA => new[] { 0x0BC6, 0x0BBE }, 0x0BCB => new[] { 0x0BC7, 0x0BBE }, 0x0BCC => new[] { 0x0BC6, 0x0BD7 },
            0x0C48 => new[] { 0x0C46, 0x0C56 },
            0x0CC0 => new[] { 0x0CBF, 0x0CD5 }, 0x0CC7 => new[] { 0x0CC6, 0x0CD5 }, 0x0CC8 => new[] { 0x0CC6, 0x0CD6 }, 0x0CCA => new[] { 0x0CC6, 0x0CC2 },
            0x0CCB => new[] { 0x0CC6, 0x0CC2, 0x0CD5 },
            0x0D4A => new[] { 0x0D46, 0x0D3E }, 0x0D4B => new[] { 0x0D47, 0x0D3E }, 0x0D4C => new[] { 0x0D46, 0x0D57 },
            // Composed again when the font has the letter.
            0x0929 when font.GlyphFor(cp) <= 0 => new[] { 0x0928, 0x093C },
            0x0934 when font.GlyphFor(cp) <= 0 => new[] { 0x0933, 0x093C },
            0x09DF when font.GlyphFor(cp) <= 0 => new[] { 0x09AF, 0x09BC },
            _ => null,
        };
        return parts != null && parts.All(p => font.GlyphFor(p) > 0) ? parts : null;
    }

    private static (int Category, int Position) Classify(int u)
    {
        switch (u)
        {
            case 0x200C: return (ZWNJ, End);
            case 0x200D: return (ZWJ, End);
            case 0x25CC: return (DottedCircle, BaseC);
            case 0x00A0 or 0x00D7 or 0x2022 or >= 0x2010 and <= 0x2015 or >= 0x25FB and <= 0x25FE: return (Placeholder, BaseC);
        }
        if (u is >= 0x1CD0 and <= 0x1CFF or >= 0xA8E0 and <= 0xA8F1) return (A, Smvd);
        if (u is < 0x0900 or > 0x0D7F || CharUnicodeInfo.GetUnicodeCategory(u) == UnicodeCategory.OtherNotAssigned) return (X, End);
        int block = u & ~0x7F, o = u & 0x7F;
        int category = (block, o) switch
        {
            (0x0980, 0x00) or (0x0C80, 0x00) or (0x0D00, 0x04) or (0x0980, 0x7C) => Placeholder,
            (0x0B80, 0x03) => X, // Tamil aytham stands on its own
            (_, <= 0x03) => SM,
            (0x0900, 0x04) => V,
            (0x0C00, 0x04) => SM,
            (_, >= 0x05 and <= 0x14) => V,
            (0x0A00, 0x30) => C, // Gurmukhi has no reph
            (_, 0x30) => Ra,
            (_, >= 0x15 and <= 0x39) => C,
            (0x0900, 0x3A or 0x3B) => M,
            (0x0D00, 0x3B or 0x3C) => H,
            (_, 0x3C) => N,
            (_, 0x3D) => Symbol,
            (_, >= 0x3E and <= 0x4C) => M,
            (_, 0x4D) => H,
            (0x0900, 0x4E or 0x4F) => M,
            (0x0980, 0x4E) => C,
            (0x0D00, 0x4E) => Repha,
            (0x0900, 0x51 or 0x52) => A,
            (0x0900, 0x53 or 0x54) => SM,
            (0x0A00, 0x51) => M,
            (0x0D00, >= 0x54 and <= 0x56) => C,
            (_, >= 0x55 and <= 0x57) => M,
            (0x0D00, >= 0x58 and <= 0x5E) => X,
            (0x0D00, 0x5F) => V,
            (_, >= 0x58 and <= 0x5F) => C,
            (_, 0x60 or 0x61) => V,
            (_, 0x62 or 0x63) => M,
            (_, >= 0x66 and <= 0x6F) => Placeholder,
            (0x0900, >= 0x72 and <= 0x77) => V,
            (0x0900, >= 0x78) => C,
            (0x0980, 0x70) => Ra,
            (0x0980, 0x71) => C,
            (0x0A00, 0x70 or 0x71) => SM,
            (0x0A00, 0x72 or 0x73) => C,
            (0x0A00, 0x75) => M,
            (0x0A80, 0x79) => C,
            (0x0A80, 0x7B) => N,
            (0x0A80, 0x7A or >= 0x7C) => SM,
            (0x0B00, 0x71) => C,
            (0x0C80, 0x73) => SM,
            (0x0D00, >= 0x7A) => C,
            _ => X,
        };
        int position = category switch
        {
            C or Ra or V or Placeholder => BaseC,
            M => MatraPosition(u, block),
            SM or A or Symbol => u == 0x0B01 ? BeforeSub : Smvd,
            _ => End,
        };
        return (category, position);
    }

    // Where a matra goes: from the side of the consonant it is written on, per script as HarfBuzz has it.
    private static int MatraPosition(int u, int block)
    {
        char side = u switch
        {
            0x093F or 0x094E or 0x09BF or 0x09C7 or 0x09C8 or 0x0A3F or 0x0ABF or 0x0B47 or >= 0x0BC6 and <= 0x0BC8 or >= 0x0D46 and <= 0x0D48 => 'L',
            0x093A or >= 0x0945 and <= 0x0948 or 0x0955 or 0x0A47 or 0x0A48 or 0x0A4B or 0x0A4C or 0x0AC5 or 0x0AC7 or 0x0AC8 or 0x0B3F or 0x0B55 or 0x0B56
                or 0x0BC0 or >= 0x0C3E and <= 0x0C40 or 0x0C46 or 0x0C47 or >= 0x0C4A and <= 0x0C4C or 0x0C55 or 0x0CBF or 0x0CC6 or 0x0CCC => 'T',
            >= 0x0941 and <= 0x0944 or 0x0956 or 0x0957 or 0x0962 or 0x0963 or >= 0x09C1 and <= 0x09C4 or 0x09E2 or 0x09E3 or 0x0A41 or 0x0A42 or 0x0A51 or 0x0A75
                or >= 0x0AC1 and <= 0x0AC4 or 0x0AE2 or 0x0AE3 or >= 0x0B41 and <= 0x0B44 or 0x0B62 or 0x0B63 or 0x0C56 or 0x0C62 or 0x0C63 or 0x0CE2 or 0x0CE3
                or 0x0D43 or 0x0D44 or 0x0D62 or 0x0D63 => 'B',
            _ => 'R',
        };
        return (side, block) switch
        {
            ('L', _) => PreM,
            ('R', 0x0900) => AfterSub,
            ('R', 0x0C00) => u <= 0x0C42 ? BeforeSub : AfterSub,
            ('R', 0x0C80) => u < 0x0CC3 || u > 0x0CD6 ? BeforeSub : AfterSub,
            ('R', _) => AfterPost,
            ('T', 0x0A00) => AfterPost,
            ('T', 0x0B00) => AfterMain,
            ('T', 0x0C00 or 0x0C80) => BeforeSub,
            ('T', _) => AfterSub,
            ('B', 0x0A00 or 0x0A80 or 0x0B80 or 0x0D00) => AfterPost,
            ('B', 0x0C00 or 0x0C80) => BeforeSub,
            _ => AfterSub,
        };
    }

    // ------------------------------------------------------------------ syllables

    /// <summary>Numbers each glyph's syllable (the longest that matches, as HarfBuzz's machine takes it); returns their kinds and what each reads as.</summary>
    private static List<Kind> Segment(List<ShapingGlyph> glyphs, List<string> sources, out List<string> logical)
    {
        var letters = new string(glyphs.Select(g => Letters[g.Category]).ToArray());
        var kinds = new List<Kind>();
        logical = new List<string>();
        for (int i = 0; i < letters.Length;)
        {
            int length = 1;
            var kind = Kind.Other;
            for (int n = Math.Min(letters.Length - i, 32); n >= 1 && kind == Kind.Other; n--)
                foreach (var (k, pattern) in Syllables)
                    if (pattern.IsMatch(letters.AsSpan(i, n)))
                    {
                        (kind, length) = (k, n);
                        break;
                    }
            var sb = new StringBuilder();
            for (int k = i; k < i + length; k++)
            {
                glyphs[k].Syllable = kinds.Count;
                sb.Append(sources[k]);
            }
            kinds.Add(kind);
            logical.Add(sb.ToString());
            i += length;
        }
        return kinds;
    }

    // A syllable that does not start as one should (a lone matra or sign) is shown on a dotted circle.
    private static void InsertDottedCircles(List<ShapingGlyph> glyphs, List<Kind> kinds, TrueTypeFontFile font)
    {
        int circle = font.GlyphFor(0x25CC);
        if (circle <= 0) return;
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (kinds[glyphs[i].Syllable] != Kind.Broken || (i > 0 && glyphs[i - 1].Syllable == glyphs[i].Syllable)) continue;
            int at = i;
            while (at < glyphs.Count && glyphs[at].Syllable == glyphs[i].Syllable && glyphs[at].Category == Repha) at++;
            glyphs.Insert(at, new ShapingGlyph { Gid = circle, Category = DottedCircle, Position = BaseC, Mask = 0, Syllable = glyphs[i].Syllable });
            i = at;
        }
    }

    /// <summary>The runs of glyphs of one syllable each.</summary>
    private static List<(int Start, int End)> Ranges(List<ShapingGlyph> glyphs)
    {
        var ranges = new List<(int, int)>();
        for (int i = 0; i < glyphs.Count;)
        {
            int e = i + 1;
            while (e < glyphs.Count && glyphs[e].Syllable == glyphs[i].Syllable) e++;
            ranges.Add((i, e));
            i = e;
        }
        return ranges;
    }

    // What the reordering asks of a glyph: a ligature is none of these any more.
    private static bool IsConsonant(ShapingGlyph g) => !g.Ligated && g.Category is C or Ra or V or Placeholder or DottedCircle;
    private static bool IsHalant(ShapingGlyph g) => !g.Ligated && g.Category == H;
    private static bool IsJoiner(ShapingGlyph g) => !g.Ligated && g.Category is ZWJ or ZWNJ;
    private static bool IsMatra(ShapingGlyph g) => !g.Ligated && g.Category == M;

    // ------------------------------------------------------------------ initial reordering

    private static void InitialReorder(List<ShapingGlyph> g, int start, int end, Plan plan)
    {
        var script = plan.Script;
        // 1. The base consonant: the last consonant without a below-base or post-base form, not counting a Ra that becomes a reph.
        int basePos = end;
        bool hasReph = false;
        int limit = start;
        if (plan.HasRphf && start + 3 <= end
            && ((script.RephMode == RephMode.Implicit && !IsJoiner(g[start + 2])) || (script.RephMode == RephMode.Explicit && g[start + 2].Category == ZWJ)))
        {
            if (plan.Would("rphf", g[start].Gid, g[start + 1].Gid)
                || (script.RephMode == RephMode.Explicit && plan.Would("rphf", g[start].Gid, g[start + 1].Gid, g[start + 2].Gid)))
            {
                limit += 2;
                while (limit < end && IsJoiner(g[limit])) limit++;
                basePos = start;
                hasReph = true;
            }
        }
        else if (script.RephMode == RephMode.Logical && g[start].Category == Repha)
        {
            limit += 1;
            while (limit < end && IsJoiner(g[limit])) limit++;
            basePos = start;
            hasReph = true;
        }
        {
            int i = end;
            bool seenBelow = false;
            do
            {
                i--;
                if (IsConsonant(g[i]))
                {
                    if (g[i].Position != BelowC && (g[i].Position != PostC || seenBelow))
                    {
                        basePos = i;
                        break;
                    }
                    if (g[i].Position == BelowC) seenBelow = true;
                    basePos = i;
                }
                // A ZWJ after a halant asks for the half form: the search stops.
                else if (start < i && g[i].Category == ZWJ && g[i - 1].Category == H) break;
            } while (i > limit);
        }
        // With no other consonant the Ra is the base, not a reph.
        if (hasReph && basePos == start && limit - basePos <= 2) hasReph = false;

        // 2. Positions: what is before the base is pre-base, consonants after a matra are final.
        for (int i = start; i < basePos; i++) g[i].Position = Math.Min(PreC, g[i].Position);
        if (basePos < end) g[basePos].Position = BaseC;
        for (int i = basePos + 1; i < end; i++)
            if (g[i].Category == M)
            {
                for (int j = i + 1; j < end; j++)
                    if (IsConsonant(g[j])) { g[j].Position = FinalC; break; }
                break;
            }
        if (hasReph) g[start].Position = RaToBecomeReph;
        // Fonts made for the first specification want the first post-base halant after the last consonant.
        if (plan.OldSpec)
        {
            bool noDouble = script.Block == 0x0C80;
            for (int i = basePos + 1; i < end; i++)
                if (g[i].Category == H)
                {
                    int j;
                    for (j = end - 1; j > i; j--)
                        if (IsConsonant(g[j]) || (noDouble && g[j].Category == H)) break;
                    if (g[j].Category != H && j > i)
                    {
                        var moved = g[i];
                        g.RemoveAt(i);
                        g.Insert(j, moved);
                    }
                    break;
                }
        }
        // Nuktas, halants and joiners go with the character before them.
        int last = Start;
        for (int i = start; i < end; i++)
        {
            if (g[i].Category is ZWJ or ZWNJ or N or H)
            {
                g[i].Position = last;
                // A halant after a pre-base matra stays where the matra was written (as Uniscribe has it).
                if (g[i].Category == H && g[i].Position == PreM)
                    for (int j = i; j > start; j--)
                        if (g[j - 1].Position != PreM) { g[i].Position = g[j - 1].Position; break; }
            }
            else if (g[i].Position != Smvd) last = g[i].Position;
        }
        // Post-base consonants take what is between them and the consonant or matra before them.
        int previous = basePos;
        for (int i = basePos + 1; i < end; i++)
            if (IsConsonant(g[i]))
            {
                for (int j = previous + 1; j < i; j++)
                    if (g[j].Position < Smvd) g[j].Position = g[i].Position;
                previous = i;
            }
            else if (g[i].Category == M) previous = i;

        // 3. Sorted by position (a stable sort: characters of one position keep their order).
        var sorted = g.GetRange(start, end - start).OrderBy(x => x.Position).ToList();
        for (int i = 0; i < sorted.Count; i++) g[start + i] = sorted[i];
        int firstLeft = end, lastLeft = end;
        basePos = end;
        for (int i = start; i < end; i++)
        {
            if (g[i].Position == BaseC) { basePos = i; break; }
            if (g[i].Position == PreM)
            {
                if (firstLeft == end) firstLeft = i;
                lastLeft = i;
            }
        }
        // Several pre-base matras are drawn in the reverse of their order (each with its nukta or halant).
        if (firstLeft < lastLeft)
        {
            g.Reverse(firstLeft, lastLeft - firstLeft + 1);
            int from = firstLeft;
            for (int j = firstLeft; j <= lastLeft; j++)
                if (g[j].Category == M)
                {
                    g.Reverse(from, j - from + 1);
                    from = j + 1;
                }
        }

        // 4. Which basic features each glyph takes.
        for (int i = start; i < end && g[i].Position == RaToBecomeReph; i++) g[i].Mask |= Rphf;
        uint preBase = Half | (!plan.OldSpec && !script.BlwfPostOnly ? Blwf : 0);
        for (int i = start; i < basePos; i++) g[i].Mask |= preBase;
        for (int i = basePos + 1; i < end; i++) g[i].Mask |= Blwf | Abvf | Pstf;
        // The first specification's eyelash Ra takes the below-base form below half forms too (unless a ZWJ asks otherwise).
        if (plan.OldSpec && script.Block == 0x0900)
            for (int i = start; i + 1 < basePos; i++)
                if (g[i].Category == Ra && g[i + 1].Category == H && (i + 2 == basePos || g[i + 2].Category != ZWJ))
                {
                    g[i].Mask |= Blwf;
                    g[i + 1].Mask |= Blwf;
                }
        // A pre-base-reordering consonant (Malayalam and Telugu Ra, and the like): the pair that forms it.
        if (plan.HasPref && basePos + 2 < end)
            for (int i = basePos + 1; i + 1 < end; i++)
                if (plan.Would("pref", g[i].Gid, g[i + 1].Gid))
                {
                    g[i].Mask |= Pref;
                    g[i + 1].Mask |= Pref;
                    break;
                }
        // A ZWNJ keeps the consonants before it from taking half forms.
        for (int i = start + 1; i < end; i++)
            if (IsJoiner(g[i]) && g[i].Category == ZWNJ)
            {
                int j = i;
                do { j--; g[j].Mask &= ~Half; } while (j > start && !IsConsonant(g[j]));
            }
    }

    // ------------------------------------------------------------------ final reordering

    private static void FinalReorder(List<ShapingGlyph> g, int start, int end, Plan plan)
    {
        // A virama a ligature and a split produced is still a virama.
        if (plan.Virama > 0)
            for (int i = start; i < end; i++)
                if (g[i].Gid == plan.Virama && g[i].Ligated && g[i].Multiplied)
                {
                    g[i].Category = H;
                    g[i].Ligated = g[i].Multiplied = false;
                }

        bool tryPref = plan.HasPref;
        // The base again: after the basic features some glyphs joined.
        int basePos;
        for (basePos = start; basePos < end; basePos++)
            if (g[basePos].Position >= BaseC)
            {
                if (tryPref && basePos + 1 < end)
                {
                    for (int i = basePos + 1; i < end; i++)
                        if ((g[i].Mask & Pref) != 0)
                        {
                            if (!(g[i].Substituted && g[i].Ligated && !g[i].Multiplied))
                            {
                                // A pre-base-reordering candidate that formed nothing: the base is around it.
                                basePos = i;
                                while (basePos < end && IsHalant(g[basePos])) basePos++;
                                if (basePos < end) g[basePos].Position = BaseC;
                                tryPref = false;
                            }
                            break;
                        }
                    if (basePos == end) break;
                }
                // Malayalam: below-base forms that did not form leave the consonant after them the base.
                if (plan.Script.Block == 0x0D00)
                    for (int i = basePos + 1; i < end; i++)
                    {
                        while (i < end && IsJoiner(g[i])) i++;
                        if (i == end || !IsHalant(g[i])) break;
                        i++;
                        while (i < end && IsJoiner(g[i])) i++;
                        if (i < end && IsConsonant(g[i]) && g[i].Position == BelowC)
                        {
                            basePos = i;
                            g[basePos].Position = BaseC;
                        }
                    }
                if (start < basePos && g[basePos].Position > BaseC) basePos--;
                break;
            }
        if (basePos == end && start < basePos && !g[basePos - 1].Ligated && g[basePos - 1].Category == ZWJ) basePos--;
        if (basePos < end)
            while (start < basePos && !g[basePos].Ligated && g[basePos].Category is N or H) basePos--;

        // Pre-base matras: after the last halant that stayed a halant (a half form joined the rest), before the base.
        if (start + 1 < end && start < basePos)
        {
            int newPos = basePos == end ? basePos - 2 : basePos - 1;
            // Malayalam and Tamil have no half forms; what 'half' made is chillus, which the matra goes after.
            if (plan.Script.Block is not (0x0D00 or 0x0B80))
            {
                while (true)
                {
                    while (newPos > start && !(IsMatra(g[newPos]) || IsHalant(g[newPos]))) newPos--;
                    if (IsHalant(g[newPos]) && g[newPos].Position != PreM)
                    {
                        // A ZWJ after the halant: the matra is not put after it; keep looking.
                        if (newPos + 1 < end && g[newPos + 1].Category == ZWJ && newPos > start)
                        {
                            newPos--;
                            continue;
                        }
                    }
                    else newPos = start;
                    break;
                }
            }
            if (start < newPos && g[newPos].Position != PreM)
            {
                for (int i = newPos; i > start; i--)
                    if (g[i - 1].Position == PreM)
                    {
                        int oldPos = i - 1;
                        if (oldPos < basePos && basePos <= newPos) basePos--;
                        var matra = g[oldPos];
                        g.RemoveAt(oldPos);
                        g.Insert(newPos, matra);
                        newPos--;
                    }
            }
        }

        // The reph: where the script puts it, found from what the basic features formed. A Ra and
        // halant move only when they joined into a reph; a logical repha only when it did not join.
        if (start + 1 < end && g[start].Position == RaToBecomeReph
            && ((g[start].Category == Repha) ^ (g[start].Ligated && !g[start].Multiplied)))
        {
            var place = plan.Script.RephPlace;
            int newPos = -1;
            // After the first halant between the reph and the base (and a joiner after it).
            int AfterHalant()
            {
                int p = start + 1;
                while (p < basePos && !IsHalant(g[p])) p++;
                if (p < basePos && IsHalant(g[p]))
                {
                    if (p + 1 < basePos && IsJoiner(g[p + 1])) p++;
                    return p;
                }
                return -1;
            }
            if (place != RephPlace.AfterPost) newPos = AfterHalant();
            if (newPos < 0 && place == RephPlace.AfterMain)
            {
                int p = basePos;
                while (p + 1 < end && g[p + 1].Position <= AfterMain) p++;
                if (p < end) newPos = p;
            }
            if (newPos < 0 && place == RephPlace.AfterSub)
            {
                int p = basePos;
                while (p + 1 < end && g[p + 1].Position is not (PostC or AfterPost or Smvd)) p++;
                if (p < end) newPos = p;
            }
            if (newPos < 0) newPos = AfterHalant();
            if (newPos < 0)
            {
                // The end of the syllable, before its signs (and before a halant after a matra, to go with the matra).
                newPos = end - 1;
                while (newPos > start && g[newPos].Position == Smvd) newPos--;
                if (IsHalant(g[newPos]))
                    for (int i = basePos + 1; i < newPos; i++)
                        if (g[i].Category == M) newPos--;
            }
            var reph = g[start];
            g.RemoveAt(start);
            g.Insert(newPos, reph);
            if (start < basePos && basePos <= newPos) basePos--;
        }

        // A pre-base-reordering consonant that formed goes before the base (after a halant that stayed, like a matra).
        if (tryPref && basePos + 1 < end)
            for (int i = basePos + 1; i < end; i++)
                if ((g[i].Mask & Pref) != 0)
                {
                    if (g[i].Ligated && !g[i].Multiplied)
                    {
                        int newPos = basePos;
                        if (plan.Script.Block is not (0x0D00 or 0x0B80))
                            while (newPos > start && !(IsMatra(g[newPos - 1]) || IsHalant(g[newPos - 1]))) newPos--;
                        if (newPos > start && IsHalant(g[newPos - 1]) && newPos < end && IsJoiner(g[newPos])) newPos++;
                        var moved = g[i];
                        g.RemoveAt(i);
                        g.Insert(newPos, moved);
                        if (newPos <= basePos && basePos < i) basePos++;
                    }
                    break;
                }

        // 'init' for a pre-base matra that starts a word.
        if (g[start].Position == PreM && (start == 0 || !InWord(g[start - 1]))) g[start].Mask |= Init;
    }

    // A letter, mark or format character: the glyph before does not end a word.
    private static bool InWord(ShapingGlyph before) =>
        before.Text.Length == 0 || CharUnicodeInfo.GetUnicodeCategory(before.Text, before.Text.Length - 1) is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.NonSpacingMark;
}
