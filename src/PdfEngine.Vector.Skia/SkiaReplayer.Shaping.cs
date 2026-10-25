using System.Numerics;
using SubstituteShaper = PdfEngine.Vector.Text.SubstituteShaper;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Shaping of text drawn with a substitute (a font that is not embedded): Arabic and Indic words
/// are shaped with the substitute's own tables by <see cref="SubstituteShaper"/>, the engine code
/// the Direct2D backend uses too.
/// </summary>
internal sealed partial class SkiaReplayer
{
    /// <summary>A pre-base vowel sign seen first, put after its consonant cluster (<see cref="SubstituteShaper.PreBaseVowelsInReadingOrder"/>).</summary>
    internal static string PreBaseVowelsInReadingOrder(string text) => SubstituteShaper.PreBaseVowelsInReadingOrder(text);

    /// <summary>The glyph indices and offsets the run is drawn with, part by part (for tests).</summary>
    internal IReadOnlyList<(ushort Gid, Vector2 Offset, float Scale)>? ResolvedGlyphs(PdfGlyphRun run) =>
        ResolveText(run) is { } r
            ? r.Parts.SelectMany(p => p.Indices.Select((gid, i) => (gid, p.Offsets[i], p.Scales?[i] ?? 1f))).ToList()
            : null;
}
