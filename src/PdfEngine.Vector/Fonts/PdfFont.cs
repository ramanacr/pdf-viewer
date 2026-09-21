using System;
using System.Collections.Generic;
using System.Text;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Resolved PDF font representation containing metric tables, encoding, and ToUnicode mappings.
/// Supports both simple (Type1, TrueType) and composite (Type0, CIDFontType0, CIDFontType2) fonts.
/// </summary>
public sealed class PdfFont
{
    public string Name { get; }
    public string BaseFont { get; }
    public string Subtype { get; }
    public bool IsComposite => Subtype == "Type0";
    public int FirstChar { get; }
    public int LastChar { get; }
    public IReadOnlyList<double>? Widths { get; }
    public Dictionary<int, double>? CidWidths { get; }
    public double MissingWidth { get; }
    public double DefaultWidth { get; }
    public Dictionary<int, string>? ToUnicodeMap { get; }
    public byte[]? EmbeddedFontData { get; }

    public PdfFont(
        string name,
        string baseFont,
        string subtype,
        int firstChar = 0,
        int lastChar = 255,
        IReadOnlyList<double>? widths = null,
        Dictionary<int, double>? cidWidths = null,
        double missingWidth = 500.0,
        double defaultWidth = 1000.0,
        Dictionary<int, string>? toUnicodeMap = null,
        byte[]? embeddedFontData = null)
    {
        Name = name;
        BaseFont = baseFont;
        Subtype = subtype;
        FirstChar = firstChar;
        LastChar = lastChar;
        Widths = widths;
        CidWidths = cidWidths;
        MissingWidth = missingWidth;
        DefaultWidth = defaultWidth;
        ToUnicodeMap = toUnicodeMap;
        EmbeddedFontData = embeddedFontData;
    }

    public double GetGlyphWidth(int charOrCid)
    {
        if (IsComposite)
        {
            if (CidWidths != null && CidWidths.TryGetValue(charOrCid, out double cidWidth))
            {
                return cidWidth;
            }
            return DefaultWidth;
        }

        if (Widths != null && charOrCid >= FirstChar && (charOrCid - FirstChar) < Widths.Count)
        {
            return Widths[charOrCid - FirstChar];
        }

        // Check standard 14 font widths
        if (Standard14Fonts.TryGetWidth(BaseFont, charOrCid, out double stdWidth))
        {
            return stdWidth;
        }

        return MissingWidth;
    }

    public string MapToUnicode(int charOrCid)
    {
        if (ToUnicodeMap != null && ToUnicodeMap.TryGetValue(charOrCid, out string? unicode))
        {
            return unicode;
        }

        // Standard WinAnsi / Latin-1 mapping fallback
        if (charOrCid >= 0 && charOrCid <= 255)
        {
            return ((char)charOrCid).ToString();
        }

        return "?";
    }
}
