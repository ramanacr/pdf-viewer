using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Metric and character advance tables for the 14 Standard PDF Fonts.
/// </summary>
public static class Standard14Fonts
{
    private static readonly HashSet<string> StandardNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Symbol", "ZapfDingbats"
    };

    public static bool IsStandardFont(string fontName)
    {
        string clean = CleanFontName(fontName);
        return StandardNames.Contains(clean);
    }

    public static string CleanFontName(string fontName)
    {
        // Strip subset prefix (e.g. "ABCDEF+Helvetica" -> "Helvetica")
        int plusIdx = fontName.IndexOf('+');
        if (plusIdx >= 0 && plusIdx < fontName.Length - 1)
        {
            return fontName.Substring(plusIdx + 1);
        }
        return fontName;
    }

    public static bool TryGetWidth(string baseFont, int charCode, out double width)
    {
        string clean = CleanFontName(baseFont);

        if (clean.StartsWith("Courier", StringComparison.OrdinalIgnoreCase))
        {
            // Courier is monospaced: exactly 600 units
            width = 600.0;
            return true;
        }

        // Standard proportional widths approximations for Latin ASCII
        if (charCode >= 32 && charCode <= 126)
        {
            if (clean.StartsWith("Times", StringComparison.OrdinalIgnoreCase))
            {
                width = GetTimesAsciiWidth(charCode);
                return true;
            }

            // Helvetica default
            width = GetHelveticaAsciiWidth(charCode);
            return true;
        }

        width = 500.0;
        return false;
    }

    private static double GetHelveticaAsciiWidth(int c) => c switch
    {
        32 => 278, // space
        (int)'i' or (int)'l' or (int)'.' or (int)':' or (int)';' or (int)'!' or (int)'\'' => 222,
        (int)'f' or (int)'t' or (int)'j' or (int)'r' => 278,
        (int)'m' or (int)'w' or (int)'M' or (int)'W' => 833,
        (int)'C' or (int)'D' or (int)'G' or (int)'H' or (int)'N' or (int)'O' or (int)'Q' or (int)'U' => 722,
        >= (int)'A' and <= (int)'Z' => 667,
        _ => 556 // average lowercase/digits
    };

    private static double GetTimesAsciiWidth(int c) => c switch
    {
        32 => 250, // space
        (int)'i' or (int)'l' or (int)'.' or (int)':' or (int)';' or (int)'!' or (int)'\'' => 250,
        (int)'f' or (int)'t' or (int)'j' or (int)'r' => 333,
        (int)'m' or (int)'w' => 722,
        (int)'M' or (int)'W' => 889,
        >= (int)'A' and <= (int)'Z' => 667,
        _ => 500 // average lowercase/digits
    };
}
