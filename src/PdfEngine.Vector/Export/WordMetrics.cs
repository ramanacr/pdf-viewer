using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;

namespace PdfEngine.Vector.Export;

/// <summary>
/// How wide Word sets text: the advance widths of the installed font Word will use for a run
/// (the run's font, or the stand-in of its kind where that is not installed). Null where no
/// installed font can be read (another operating system, a font with other outlines).
/// </summary>
internal static class WordMetrics
{
    private sealed class Face
    {
        public required TrueTypeFontFile Font;
        public readonly ConcurrentDictionary<int, double> Advances = new();
    }

    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), Face?> Faces = new();
    private static readonly ConcurrentDictionary<(string, int), TrueTypeFontFile?> Files = new();

    /// <summary>The width of <paramref name="text"/> in the run's look, in points (spacing aside), or null.</summary>
    public static double? Width(DocRun run, string text, Dictionary<string, (bool Serif, bool Fixed)> kinds)
    {
        var face = FaceFor(run.Font, run.Bold, run.Italic, kinds);
        if (face == null) return null;
        double units = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            units += face.Advances.GetOrAdd(cp, c =>
            {
                int gid = face.Font.GlyphFor(c);
                // A character the font lacks comes from another font: about half an em.
                return gid > 0 ? face.Font.Advance(gid) : 500;
            });
        }
        double size = run.VertAlign != 0 ? run.Size * 0.65 : run.Size;
        return units / 1000 * size * run.Scale / 100;
    }

    private static Face? FaceFor(string family, bool bold, bool italic, Dictionary<string, (bool Serif, bool Fixed)> kinds) =>
        Faces.GetOrAdd((family, bold, italic), key =>
        {
            var catalog = SystemFontCatalog.Installed;
            var found = catalog.FindFamily(key.Family, key.Bold, key.Italic);
            if (found == null)
            {
                // Word stands in the font the font table names for one that is not installed.
                var (serif, fixedPitch) = kinds.GetValueOrDefault(key.Family);
                found = catalog.FindFamily(fixedPitch ? "Courier New" : serif ? "Times New Roman" : "Arial", key.Bold, key.Italic);
            }
            if (found == null) return null;
            var font = Files.GetOrAdd((found.Path, found.Index), f =>
            {
                try { return TrueTypeFontFile.TryLoad(File.ReadAllBytes(f.Item1), f.Item2); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            });
            return font != null ? new Face { Font = font } : null;
        });
}
