using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Text;

/// <summary>
/// Shaping of text drawn with a substitute: a font that is not embedded gives its text as
/// characters, so Arabic letters would be drawn in their isolated forms and Indic vowel signs where
/// they are typed. Each word of Arabic or Indic script is shaped as the editor shapes new text
/// (<see cref="TextShaper.ShapeLine"/>: joining forms, the face's GSUB ligatures and conjuncts,
/// GPOS marks, Indic reordering) and the shaped glyphs are fitted to the space the document's
/// /Widths give the word, as single glyphs are fitted. The Skia and Direct2D backends share it.
/// Only drawing changes: the text page reads the run's characters, not these glyphs.
/// </summary>
internal static class SubstituteShaper
{
    public enum Script { None, Joiner, Arabic, Indic }

    /// <summary>A word to shape: glyphs <see cref="Start"/> to <see cref="End"/> (exclusive) of the run, of one script.</summary>
    public readonly record struct Word(int Start, int End, Script Script);

    private static Script ScriptOf(int cp) => cp switch
    {
        0x200C or 0x200D => Script.Joiner,
        // Arabic letters and marks; not its digits and punctuation, which are not joined or reordered.
        >= 0x0660 and <= 0x066D or >= 0x06F0 and <= 0x06F9 or 0x060C or 0x061B or 0x061F or 0x06D4 => Script.None,
        >= 0x0610 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF => Script.Arabic,
        // Devanagari to Malayalam: the scripts IndicShaper shapes (presentation forms are shaped already).
        >= 0x0900 and <= 0x0D7F => Script.Indic,
        _ => Script.None,
    };

    /// <summary>The script of a glyph's text: one script throughout (joiners go with any), else None.</summary>
    private static Script ScriptOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Script.None;
        var script = Script.Joiner;
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var s = ScriptOf(char.ConvertToUtf32(text, i));
            if (s == Script.None || (s != Script.Joiner && script != Script.Joiner && s != script)) return Script.None;
            if (s != Script.Joiner) script = s;
        }
        return script;
    }

    /// <summary>
    /// The run's words to shape: runs of consecutive horizontal glyphs (of <paramref name="only"/>
    /// when set) of Arabic or Indic script, with two characters or more. Null when there are none.
    /// </summary>
    public static List<Word>? Words(PdfGlyphRun run, bool[]? only = null)
    {
        List<Word>? words = null;
        int i = 0;
        while (i < run.Glyphs.Count)
        {
            var script = Usable(i);
            if (script is Script.None or Script.Joiner)
            {
                i++;
                continue;
            }
            int end = i + 1, chars = run.Glyphs[i].Unicode!.Length;
            while (end < run.Glyphs.Count && Usable(end) is var s && (s == script || s == Script.Joiner))
                chars += run.Glyphs[end++].Unicode!.Length;
            if (chars >= 2)
                (words ??= new()).Add(new Word(i, end, script));
            i = end;
        }
        return words;

        Script Usable(int k)
        {
            var g = run.Glyphs[k];
            if (only != null && !only[k]) return Script.None;
            // Vertical glyphs (no width, a vertical advance) keep their own positions.
            if (g.Width < 0 || g.AdvanceY != 0) return Script.None;
            return ScriptOf(g.Unicode);
        }
    }

    private static bool IsMark(string text) =>
        text.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(text, 0) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// The word's glyphs shaped with <paramref name="font"/>: glyph indices, offsets (glyph space,
    /// ascender up, for a positive font size) and horizontal scales, left to right. Null when the
    /// font lacks a glyph the shaped word needs, so the word is drawn glyph by glyph as before.
    /// </summary>
    public static List<(ushort Gid, Vector2 Offset, float Scale)>? ShapeWord(TrueTypeFontFile font, PdfGlyphRun run, Word word, double th)
    {
        var glyphs = run.Glyphs;
        double em = Math.Abs(run.FontSize);
        double y = glyphs[word.Start].OffsetY;
        // The space the document gives the word: from its leftmost glyph to the right edge of its rightmost one.
        double left = double.MaxValue, right = double.MinValue;
        for (int k = word.Start; k < word.End; k++)
        {
            var g = glyphs[k];
            if (g.OffsetY != y) return null;
            double x = g.OffsetX / th;
            left = Math.Min(left, x);
            right = Math.Max(right, x + g.Width * em);
        }

        // The characters in reading order. A PDF lays its glyphs out left to right, so right-to-left
        // text is in the order it is seen (as PDFium and the text page read it) and is put back in
        // reading order here; the shaper then lays it out right to left again, so it is reversed once.
        // A word whose glyphs go right to left is in reading order already.
        var order = Enumerable.Range(word.Start, word.End - word.Start).ToList();
        if (word.Script == Script.Arabic && !(order.Count > 1 && glyphs[word.End - 1].OffsetX < glyphs[word.Start].OffsetX))
            order = SeenToReadingOrder(order, k => glyphs[k].Unicode!);
        string text = string.Concat(order.Select(k => glyphs[k].Unicode));
        if (word.Script == Script.Indic)
            text = PreBaseVowelsInReadingOrder(text);

        var shaped = TextShaper.ShapeLine(text, _ => font, f => f);
        var result = new List<(ushort, Vector2, float)>(shaped.Count);
        var placed = new List<(int Gid, double X, double Y)>(shaped.Count);
        double pen = 0;
        foreach (var (g, _) in shaped)
        {
            if (g.Gid <= 0)
            {
                // A joiner the font has no glyph for only steers the shaping.
                if (g.Text.All(c => c is '‌' or '‍')) continue;
                return null;
            }
            if (g.Gid > ushort.MaxValue) return null;
            placed.Add((g.Gid, pen + g.Dx / 1000.0 * em, g.Dy / 1000.0 * em));
            pen += (g.Advance ?? font.Advance(g.Gid)) / 1000.0 * em;
        }
        if (placed.Count == 0) return null;

        // Fitted as a single glyph is fitted to its width: narrowed when wider than the space, widened
        // by up to a tenth and centred when narrower.
        double space = right - left, scale = 1, shift = 0;
        if (space > 0 && pen > 0 && Math.Abs(space - pen) > pen * 0.01)
        {
            scale = Math.Min(space / pen, 1.10);
            shift = Math.Max(0, (space - pen * scale) / 2);
        }
        foreach (var (gid, x, dy) in placed)
            result.Add(((ushort)gid, new Vector2((float)(left + shift + x * scale), (float)(y + dy)), (float)scale));
        return result;
    }

    /// <summary>
    /// Right-to-left glyphs in the order they are seen, put back in reading order. Most producers
    /// reverse the characters, so a mark comes before its letter; a word that ends in a mark was
    /// reversed letter by letter with each mark kept after its letter, and is turned back so.
    /// </summary>
    private static List<int> SeenToReadingOrder(List<int> seen, Func<int, string> text)
    {
        if (!IsMark(text(seen[^1])))
        {
            var reversed = new List<int>(seen);
            reversed.Reverse();
            return reversed;
        }
        var clusters = new List<List<int>>();
        foreach (int k in seen)
        {
            if (IsMark(text(k)) && clusters.Count > 0) clusters[^1].Add(k);
            else clusters.Add(new List<int> { k });
        }
        clusters.Reverse();
        return clusters.SelectMany(c => c).ToList();
    }

    /// <summary>
    /// A pre-base vowel sign (Devanagari i, Bengali e) typed in reading order follows its consonant.
    /// One that comes before a consonant and after no consonant was written where it is seen: it is
    /// moved after the consonant cluster it belongs to, so the shaper does not reorder it twice.
    /// </summary>
    public static string PreBaseVowelsInReadingOrder(string text)
    {
        // The nine scripts share one layout (ISCII): the same offsets in each 128-character block.
        static int Offset(char c) => c is >= 'ऀ' and <= 'ൿ' ? (c - 0x0900) & 0x7F : -1;
        static bool Consonant(char c) => Offset(c) is >= 0x15 and <= 0x39 or >= 0x58 and <= 0x5F;
        static bool PreBase(char c) => c is 'ि' or 'ি' or 'ে' or 'ৈ' or 'ਿ' or 'િ' or 'େ'
            or 'ெ' or 'ே' or 'ை' or 'െ' or 'േ' or 'ൈ';
        const int Nukta = 0x3C, Virama = 0x4D;
        if (!text.Any(PreBase)) return text;
        var chars = text.ToCharArray().ToList();
        for (int i = 0; i < chars.Count - 1; i++)
        {
            if (!PreBase(chars[i]) || !Consonant(chars[i + 1])) continue;
            if (i > 0 && (Consonant(chars[i - 1]) || Offset(chars[i - 1]) is Nukta or Virama || chars[i - 1] is '‌' or '‍')) continue;
            // The cluster: a consonant, its nukta, and consonants joined to it by a virama.
            int end = i + 2;
            while (true)
            {
                if (end < chars.Count && Offset(chars[end]) == Nukta) end++;
                if (end + 1 < chars.Count && Offset(chars[end]) == Virama && Consonant(chars[end + 1])) end += 2;
                else break;
            }
            char vowel = chars[i];
            chars.RemoveAt(i);
            chars.Insert(end - 1, vowel);
            i = end - 1;
        }
        return new string(chars.ToArray());
    }
}
