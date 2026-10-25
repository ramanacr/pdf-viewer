using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEngine.Vector.Text;

/// <summary>
/// A substitute's run drawn from several faces when no one face has all its characters: which face
/// draws each glyph (the primary, the substitute with most of them, draws its own; every other
/// character goes to the face its script asks for), with Arabic and Indic words kept whole in one
/// face so they are shaped as one word. The Direct2D and WPF backends share it; each finds its own
/// faces and maps each part's glyphs.
/// </summary>
internal static class CharacterComposition
{
    /// <summary>Faces tried after the metric-compatible substitute, for glyphs it lacks (symbols, dingbats, other scripts).</summary>
    public static IReadOnlyList<string> UnicodeFallbackFamilies { get; } =
        new[] { "Segoe UI Symbol", "Arial", "Segoe UI", "Cambria Math", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Ebrima" };

    /// <summary>True for a glyph drawn as no glyph: no text, a space or a no-break space (its place is kept by the next glyph's offset).</summary>
    public static bool IsBlank(string? text) => string.IsNullOrEmpty(text) || text == " " || text == " ";

    /// <summary>The run's text, for choosing a CJK face by the language of its characters.</summary>
    public static string RunText(PdfGlyphRun run)
    {
        if (run.FullText != null) return run.FullText;
        var sb = new System.Text.StringBuilder(run.Glyphs.Count);
        foreach (var g in run.Glyphs) sb.Append(g.Unicode);
        return sb.ToString();
    }

    /// <summary>
    /// The face of each glyph of the run, by position: <paramref name="primary"/> where it has the
    /// glyph (<paramref name="primaryHas"/>), else the face <paramref name="faceFor"/> gives for its
    /// character; null for a glyph not drawn (blank, white space, control, or no face has it).
    /// </summary>
    public static TFace?[] Owners<TFace>(PdfGlyphRun run, TFace primary, bool[] primaryHas, Func<int, TFace?> faceFor)
        where TFace : class
    {
        var owners = new TFace?[run.Glyphs.Count];
        for (int i = 0; i < run.Glyphs.Count; i++)
        {
            string? text = run.Glyphs[i].Unicode;
            if (IsBlank(text)) continue;
            if (primaryHas[i])
            {
                owners[i] = primary;
                continue;
            }
            if (char.IsWhiteSpace(text!, 0) || char.IsControl(text!, 0)) continue;
            owners[i] = faceFor(i);
        }
        return owners;
    }

    /// <summary>
    /// Gives each Arabic or Indic word of the run (<see cref="SubstituteShaper.Words"/>) to one face in
    /// <paramref name="owners"/>, so that face shapes the whole word: <paramref name="primary"/> when it
    /// has every glyph of the word, else the first face a character of the word asks for
    /// (<paramref name="faceFor"/>) that has every glyph of it. A word that no such face has whole
    /// keeps the face of each glyph (each piece is drawn unshaped where the word is cut).
    /// </summary>
    /// <param name="has">Whether a face has the glyph at a position of the run.</param>
    public static void KeepWordsWhole<TFace>(PdfGlyphRun run, TFace?[] owners, TFace primary, Func<TFace, int, bool> has, Func<int, TFace?> faceFor)
        where TFace : class
    {
        if (SubstituteShaper.Words(run) is not { } words)
            return;
        foreach (var word in words)
        {
            TFace? owner = Whole(primary) ? primary : null;
            for (int k = word.Start; owner == null && k < word.End; k++)
                if (!IsJoiner(k) && faceFor(k) is { } candidate && !ReferenceEquals(candidate, primary) && Whole(candidate))
                    owner = candidate;
            if (owner == null)
                continue;
            for (int k = word.Start; k < word.End; k++)
                owners[k] = owner;

            bool Whole(TFace face)
            {
                for (int k = word.Start; k < word.End; k++)
                    if (!IsJoiner(k) && !has(face, k)) return false;
                return true;
            }
        }

        // Zero-width joiners are shaped with the word and drawn as no glyph: no face needs one.
        bool IsJoiner(int k) => run.Glyphs[k].Unicode!.All(c => c is '‌' or '‍');
    }

    /// <summary>
    /// The faces of <paramref name="owners"/> in the order their parts are drawn, each with the
    /// positions of its glyphs: <paramref name="primary"/> first, then each other face in the order it
    /// first appears. Null when no glyph has a face other than the primary.
    /// </summary>
    public static List<(TFace Face, bool[] Mine)>? Parts<TFace>(TFace?[] owners, TFace primary)
        where TFace : class
    {
        var order = new List<TFace> { primary };
        foreach (var o in owners)
            if (o != null && !order.Exists(f => ReferenceEquals(f, o))) order.Add(o);
        if (order.Count == 1)
            return null;
        return order.Select(f => (f, Array.ConvertAll(owners, o => ReferenceEquals(o, f)))).ToList();
    }
}
