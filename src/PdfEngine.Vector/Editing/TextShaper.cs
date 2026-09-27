using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Shapes new text for drawing: the bidirectional algorithm's levels and reordering (UAX #9, without
/// explicit embeddings), Arabic joining forms, and the font's substitutions (ligatures, positional
/// forms, required ligatures such as lam-alef). The result is glyphs in the order they are drawn,
/// left to right.
/// </summary>
internal static class TextShaper
{
    /// <summary>
    /// A shaped glyph, in drawing order, with the embedding level of the text it shows (odd: right to
    /// left); where it is drawn from the pen (1/1000 em) and how far it moves the pen (null: its own advance).
    /// Glyphs drawn out of reading order share the <paramref name="Actual"/> text they read as; a
    /// <paramref name="Kerned"/> glyph's advance has the font's pair kerning in it already.
    /// </summary>
    public readonly record struct Shaped(int Gid, string Text, int Level, double Dx = 0, double Dy = 0, double? Advance = null, ActualTextSpan? Actual = null, bool Kerned = false);

    private static readonly string[] CursiveFeatures = { "ccmp", "isol", "fina", "medi", "init", "rlig", "calt", "liga" };
    private static readonly string[] DefaultFeatures = { "ccmp", "liga", "rlig" };

    /// <summary>
    /// Shapes one line: <paramref name="fontFor"/> picks the font for each word (a run of
    /// non-space characters) and returns null when no font has it.
    /// </summary>
    public static List<(Shaped Glyph, T Font)> ShapeLine<T>(string line, Func<string, T?> fontFor, Func<T, TrueTypeFontFile> file) where T : class
    {
        var levels = Bidi.Levels(line);
        // Runs of one level and one font, in logical order (words of Indic script in runs of their own).
        var runs = new List<(int Start, int End, int Level, T? Font, bool Indic)>();
        int i = 0;
        while (i < line.Length)
        {
            int wordEnd = i;
            bool space = line[i] == ' ';
            while (wordEnd < line.Length && (line[wordEnd] == ' ') == space) wordEnd++;
            var font = fontFor(line[i..wordEnd]);
            bool indic = !space && IndicShaper.Handles(line[i..wordEnd]);
            for (int k = i; k < wordEnd;)
            {
                int e = k;
                while (e < wordEnd && levels[e] == levels[k]) e++;
                if (runs.Count > 0 && runs[^1].End == k && runs[^1].Level == levels[k] && ReferenceEquals(runs[^1].Font, font) && runs[^1].Indic == indic)
                    runs[^1] = (runs[^1].Start, e, levels[k], font, indic);
                else runs.Add((k, e, levels[k], font, indic));
                k = e;
            }
            i = wordEnd;
        }

        var glyphs = new List<(Shaped Glyph, T Font)>();
        foreach (var (start, end, level, font, indic) in runs)
        {
            if (font == null) continue;
            var f = file(font);
            string text = line[start..end];
            if (indic)
            {
                foreach (var shaped in IndicShaper.Shape(text, f, level)) glyphs.Add((shaped, font));
                continue;
            }
            bool rtl = (level & 1) != 0;
            var buffer = new List<ShapingGlyph>();
            var forms = ArabicJoining.Forms(text);
            // One glyph per character (a base and its marks each get their own); substitutions join them.
            for (int at = 0; at < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, at);
                string s = char.ConvertFromUtf32(cp);
                int shown = rtl && Bidi.Mirror(cp) is int mirrored && f.GlyphFor(mirrored) > 0 ? mirrored : cp;
                // A mirrored bracket reads as the bracket it looks like, as readers expect of right-to-left text.
                buffer.Add(new ShapingGlyph { Gid = f.GlyphFor(shown), Text = char.ConvertFromUtf32(shown), Form = forms[at] });
                at += s.Length;
            }
            bool cursive = forms.Any(x => x != null);
            string script = cursive ? "arab" : ScriptOf(text);
            f.Substitution?.Apply(buffer, script, cursive ? CursiveFeatures : DefaultFeatures);
            buffer.RemoveAll(g => g.Gid <= 0 && g.Text.Length == 0);
            // Marks placed on the glyphs they belong to (in reading order, before any reversal).
            var gids = buffer.Select(g => g.Gid).ToArray();
            var advances = gids.Select(gid => (double)f.AdvanceUnits(gid)).ToArray();
            var dx = new double[gids.Length];
            var dy = new double[gids.Length];
            var own = (double[])advances.Clone();
            f.Positioning?.Apply(gids, script, advances, dx, dy);
            for (int k = 0; k < buffer.Count; k++)
            {
                bool moved = dx[k] != 0 || dy[k] != 0 || advances[k] != own[k];
                glyphs.Add((moved ? new Shaped(buffer[k].Gid, buffer[k].Text, level, f.ToThousandths((int)Math.Round(dx[k])), f.ToThousandths((int)Math.Round(dy[k])), f.ToThousandths((int)Math.Round(advances[k])))
                    : new Shaped(buffer[k].Gid, buffer[k].Text, level), font));
            }
        }
        // A base and the marks after it move as one when right-to-left text is reversed, so each
        // mark still follows its base (zero-width marks are drawn over the glyph before them).
        var clusters = new List<List<(Shaped Glyph, T Font)>>();
        foreach (var g in glyphs)
        {
            bool mark = g.Glyph.Text.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(g.Glyph.Text, 0) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
            if (mark && clusters.Count > 0 && clusters[^1][0].Glyph.Level == g.Glyph.Level) clusters[^1].Add(g);
            else clusters.Add(new List<(Shaped Glyph, T Font)> { g });
        }
        return Bidi.Reorder(clusters, c => c[0].Glyph.Level).SelectMany(c => c).ToList();
    }

    private static string ScriptOf(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '֐' and <= '׿') return "hebr";
            if (c is >= 'Ͱ' and <= 'Ͽ') return "grek";
            if (c is >= 'Ѐ' and <= 'ӿ') return "cyrl";
        }
        return "latn";
    }
}

/// <summary>What a run of glyphs reads as when their order or number does not tell it (written as /ActualText); one per run, compared by reference.</summary>
internal sealed class ActualTextSpan
{
    public ActualTextSpan(string text) => Text = text;

    public string Text { get; }
}

/// <summary>Arabic joining (Unicode chapter 9.2): the positional form each letter takes.</summary>
internal static class ArabicJoining
{
    private enum Type { None, Right, Dual, Causing, Transparent }

    private static Type Of(char c)
    {
        if (c == '‍' || c == 'ـ') return Type.Causing;
        if (c == '‌') return Type.None;
        if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return Type.Transparent;
        return c switch
        {
            'آ' or 'أ' or 'ؤ' or 'إ' or 'ا' or 'ة' or 'و' => Type.Right,
            >= 'د' and <= 'ز' => Type.Right,
            'ئ' or 'ب' => Type.Dual,
            >= 'ت' and <= 'خ' => Type.Dual,
            >= 'س' and <= 'ؿ' => Type.Dual,
            >= 'ف' and <= 'ه' => Type.Dual,
            'ى' or 'ي' or 'ٮ' or 'ٯ' => Type.Dual,
            >= 'ٱ' and <= 'ٳ' => Type.Right,
            >= 'ٵ' and <= 'ٷ' => Type.Right,
            >= 'ٸ' and <= 'ڇ' => Type.Dual,
            >= 'ڈ' and <= 'ڙ' => Type.Right,
            >= 'ښ' and <= 'ڿ' => Type.Dual,
            'ۀ' or 'ۍ' or 'ۏ' or 'ے' or 'ۓ' or 'ە' or 'ۮ' or 'ۯ' => Type.Right,
            >= 'ۃ' and <= 'ۋ' => Type.Right,
            'ہ' or 'ۂ' or 'ی' or 'ێ' or 'ې' or 'ۑ' or 'ۿ' => Type.Dual,
            >= 'ۺ' and <= 'ۼ' => Type.Dual,
            >= 'ݐ' and <= 'ݿ' => Type.Dual,
            _ => Type.None,
        };
    }

    /// <summary>The letter joins its neighbours (it has positional forms).</summary>
    public static bool Joins(char c) => Of(c) is Type.Right or Type.Dual;

    /// <summary>For each UTF-16 position: isol, init, medi or fina for a joining letter, else null.</summary>
    public static string?[] Forms(string text)
    {
        var forms = new string?[text.Length];
        var types = text.Select(Of).ToArray();
        for (int i = 0; i < text.Length; i++)
        {
            if (types[i] is not (Type.Right or Type.Dual)) continue;
            int p = i - 1;
            while (p >= 0 && types[p] == Type.Transparent) p--;
            int n = i + 1;
            while (n < text.Length && types[n] == Type.Transparent) n++;
            bool joinsBefore = p >= 0 && types[p] is Type.Dual or Type.Causing;
            bool joinsAfter = types[i] == Type.Dual && n < text.Length && types[n] is Type.Right or Type.Dual or Type.Causing;
            forms[i] = (joinsBefore, joinsAfter) switch { (true, true) => "medi", (true, false) => "fina", (false, true) => "init", _ => "isol" };
        }
        return forms;
    }
}

/// <summary>The Unicode bidirectional algorithm (UAX #9) for plain text: no explicit embeddings or isolates.</summary>
internal static class Bidi
{
    private enum Class { L, R, AL, EN, AN, ES, ET, CS, NSM, WS, ON }

    private static Class Of(char c)
    {
        if (c is >= '0' and <= '9') return Class.EN;
        if (c is >= '٠' and <= '٩' or '٫' or '٬') return Class.AN;
        if (c is >= '۰' and <= '۹') return Class.EN;
        if (c is '+' or '-') return Class.ES;
        if (c is '#' or '$' or '%' or '°' or '¢' or '£' or '¥' or '€') return Class.ET;
        if (c is ',' or '.' or ':' or '/' or ' ' or '،') return Class.CS;
        if (c is ' ' or '\t') return Class.WS;
        if (c is >= '֐' and <= '׿' or >= 'יִ' and <= 'ﭏ') return CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark ? Class.NSM : Class.R;
        if (c is >= '؀' and <= '޿' or >= 'ࢠ' and <= 'ࣿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
            return CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark ? Class.NSM : Class.AL;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return Class.NSM;
        if (char.IsLetter(c) || cat is UnicodeCategory.SpacingCombiningMark or UnicodeCategory.LetterNumber) return Class.L;
        if (char.IsSurrogate(c)) return Class.L;
        return Class.ON;
    }

    /// <summary>0 when the first strong character is left to right (or there is none), 1 when it is right to left.</summary>
    public static int ParagraphLevel(string text)
    {
        foreach (char c in text)
        {
            var k = Of(c);
            if (k == Class.L) return 0;
            if (k is Class.R or Class.AL) return 1;
        }
        return 0;
    }

    /// <summary>The resolved embedding level of each UTF-16 position.</summary>
    public static int[] Levels(string text, int? paragraphLevel = null)
    {
        int n = text.Length, p = paragraphLevel ?? ParagraphLevel(text);
        var t = text.Select(Of).ToArray();
        var sor = (p & 1) != 0 ? Class.R : Class.L;
        // W1: marks take the class of what they follow.
        for (int i = 0; i < n; i++) if (t[i] == Class.NSM) t[i] = i > 0 ? t[i - 1] : sor;
        // W2, W3: European numbers after Arabic letters are Arabic numbers; Arabic letters are R.
        var last = sor;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is Class.L or Class.R or Class.AL) last = t[i];
            else if (t[i] == Class.EN && last == Class.AL) t[i] = Class.AN;
        }
        for (int i = 0; i < n; i++) if (t[i] == Class.AL) t[i] = Class.R;
        // W4: one separator between two numbers of a kind joins them.
        for (int i = 1; i + 1 < n; i++)
        {
            if (t[i] == Class.ES && t[i - 1] == Class.EN && t[i + 1] == Class.EN) t[i] = Class.EN;
            else if (t[i] == Class.CS && t[i - 1] == t[i + 1] && t[i - 1] is Class.EN or Class.AN) t[i] = t[i - 1];
        }
        // W5: terminators next to European numbers are part of them.
        for (int i = 0; i < n; i++)
        {
            if (t[i] != Class.ET) continue;
            int e = i;
            while (e < n && t[e] == Class.ET) e++;
            bool touches = (i > 0 && t[i - 1] == Class.EN) || (e < n && t[e] == Class.EN);
            if (touches) for (int k = i; k < e; k++) t[k] = Class.EN;
            i = e - 1;
        }
        // W6: other separators and terminators are neutral.
        for (int i = 0; i < n; i++) if (t[i] is Class.ES or Class.ET or Class.CS) t[i] = Class.ON;
        // W7: European numbers after L are L.
        last = sor;
        for (int i = 0; i < n; i++)
        {
            if (t[i] is Class.L or Class.R) last = t[i];
            else if (t[i] == Class.EN && last == Class.L) t[i] = Class.L;
        }
        // N1, N2: neutrals between two of one direction take it (numbers count as R); else the paragraph's.
        for (int i = 0; i < n; i++)
        {
            if (t[i] is not (Class.WS or Class.ON)) continue;
            int e = i;
            while (e < n && t[e] is Class.WS or Class.ON) e++;
            var before = i > 0 ? Strong(t[i - 1]) : sor;
            var after = e < n ? Strong(t[e]) : sor;
            var resolved = before == after ? before : (p & 1) != 0 ? Class.R : Class.L;
            for (int k = i; k < e; k++) t[k] = resolved;
            i = e - 1;
        }
        // I1, I2.
        var levels = new int[n];
        for (int i = 0; i < n; i++)
            levels[i] = (p & 1) == 0
                ? t[i] switch { Class.R => 1, Class.AN or Class.EN => 2, _ => 0 }
                : t[i] switch { Class.L or Class.EN or Class.AN => 2, _ => 1 };
        // L1: trailing white space goes back to the paragraph level.
        for (int i = n - 1; i >= 0 && text[i] is ' ' or '\t'; i--) levels[i] = p;
        return levels;
    }

    private static Class Strong(Class c) => c is Class.EN or Class.AN ? Class.R : c;

    /// <summary>L2: reverses every run at each level from the highest down to the lowest odd level.</summary>
    public static List<T> Reorder<T>(List<T> items, Func<T, int> level)
    {
        var result = new List<T>(items);
        if (result.Count == 0) return result;
        int max = result.Max(level), minOdd = Math.Max(1, result.Min(level) | 1);
        if (max == 0) return result;
        for (int lv = max; lv >= minOdd; lv--)
        {
            int i = 0;
            while (i < result.Count)
            {
                if (level(result[i]) < lv) { i++; continue; }
                int e = i;
                while (e < result.Count && level(result[e]) >= lv) e++;
                result.Reverse(i, e - i);
                i = e;
            }
        }
        return result;
    }

    /// <summary>The mirrored character drawn in right-to-left text (brackets and the like), or null.</summary>
    public static int? Mirror(int cp) => cp switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<',
        '«' => '»', '»' => '«', '‹' => '›', '›' => '‹',
        _ => null,
    };
}
