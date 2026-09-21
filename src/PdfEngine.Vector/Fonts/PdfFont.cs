using System;
using System.Collections.Generic;
using System.Text;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Resolved PDF font representation containing metric tables, encoding, and ToUnicode mappings.
/// </summary>
public sealed class PdfFont
{
    public string Name { get; }
    public string BaseFont { get; }
    public string Subtype { get; }
    public int FirstChar { get; }
    public int LastChar { get; }
    public IReadOnlyList<double>? Widths { get; }
    public double MissingWidth { get; }
    public Dictionary<int, string>? ToUnicodeMap { get; }
    public byte[]? EmbeddedFontData { get; }

    public PdfFont(
        string name,
        string baseFont,
        string subtype,
        int firstChar = 0,
        int lastChar = 255,
        IReadOnlyList<double>? widths = null,
        double missingWidth = 500.0,
        Dictionary<int, string>? toUnicodeMap = null,
        byte[]? embeddedFontData = null)
    {
        Name = name;
        BaseFont = baseFont;
        Subtype = subtype;
        FirstChar = firstChar;
        LastChar = lastChar;
        Widths = widths;
        MissingWidth = missingWidth;
        ToUnicodeMap = toUnicodeMap;
        EmbeddedFontData = embeddedFontData;
    }

    public double GetGlyphWidth(int charCode)
    {
        if (Widths != null && charCode >= FirstChar && (charCode - FirstChar) < Widths.Count)
        {
            return Widths[charCode - FirstChar];
        }

        // Check standard 14 font widths
        if (Standard14Fonts.TryGetWidth(BaseFont, charCode, out double stdWidth))
        {
            return stdWidth;
        }

        return MissingWidth;
    }

    public string MapToUnicode(int charCode)
    {
        if (ToUnicodeMap != null && ToUnicodeMap.TryGetValue(charCode, out string? unicode))
        {
            return unicode;
        }

        // Standard WinAnsi / Latin-1 mapping fallback
        if (charCode >= 0 && charCode <= 255)
        {
            return ((char)charCode).ToString();
        }

        return "?";
    }
}
