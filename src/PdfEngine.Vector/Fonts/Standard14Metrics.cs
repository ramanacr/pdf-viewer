using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Generated Standard 14 advance-width tables (see <c>Standard14Metrics.g.cs</c>) plus lookup helpers.
/// </summary>
internal static partial class Standard14Metrics
{
    /// <summary>Unicode string → index into <see cref="GlyphNames"/>.</summary>
    internal static readonly Lazy<Dictionary<string, int>> UnicodeIndex = new(() =>
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < GlyphNames.Length; i++)
        {
            string? u = GlyphList.ToUnicode(GlyphNames[i]);
            if (u != null && !map.ContainsKey(u))
                map[u] = i;
        }
        return map;
    });
}
