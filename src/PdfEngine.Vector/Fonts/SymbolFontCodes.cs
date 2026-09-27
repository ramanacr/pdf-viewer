using System.Collections.Generic;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Where a substitute finds a symbol: the code the Symbol font's own encoding gives a character
/// (a document may have re-encoded its Symbol font, so the code it shows is not that one), and the
/// standard character for one Adobe's glyph list puts in the private-use area.
/// </summary>
public static class SymbolFontCodes
{
    /// <summary>
    /// Adobe's private-use characters with a standard equivalent: the serif and sans forms of
    /// the copyright, registered and trademark signs, and the pieces of tall brackets.
    /// </summary>
    private static readonly Dictionary<int, int> Equivalents = new()
    {
        [0xF6D9] = 0x00A9, [0xF6DA] = 0x00AE, [0xF6DB] = 0x2122,
        [0xF8E8] = 0x00AE, [0xF8E9] = 0x00A9, [0xF8EA] = 0x2122,
        [0xF8E5] = 0x203E, [0xF8E6] = 0x23D0, [0xF8E7] = 0x23AF,
        [0xF8EB] = 0x239B, [0xF8EC] = 0x239C, [0xF8ED] = 0x239D, [0xF8EE] = 0x23A1, [0xF8EF] = 0x23A2, [0xF8F0] = 0x23A3,
        [0xF8F1] = 0x23A7, [0xF8F2] = 0x23A8, [0xF8F3] = 0x23A9, [0xF8F4] = 0x23AA, [0xF8F5] = 0x23AE,
        [0xF8F6] = 0x239E, [0xF8F7] = 0x239F, [0xF8F8] = 0x23A0, [0xF8F9] = 0x23A4, [0xF8FA] = 0x23A5, [0xF8FB] = 0x23A6,
        [0xF8FC] = 0x23AB, [0xF8FD] = 0x23AC, [0xF8FE] = 0x23AD,
    };

    // After the equivalents, which it reads.
    private static readonly Dictionary<int, int> SymbolCodes = Build();

    private static Dictionary<int, int> Build()
    {
        var codes = new Dictionary<int, int>();
        var names = FontEncodings.Symbol;
        for (int code = 0; code < names.Length; code++)
        {
            if (names[code] is not { } name || GlyphList.ToUnicode(name) is not { Length: > 0 } text) continue;
            codes.TryAdd(char.ConvertToUtf32(text, 0), code);
        }
        foreach (var (pua, standard) in Equivalents)
            if (codes.TryGetValue(standard, out int code)) codes.TryAdd(pua, code);
        return codes;
    }

    /// <summary>The code of a character in the Symbol font's built-in encoding, or null when it has none.</summary>
    public static int? CodeFor(int codePoint) => SymbolCodes.TryGetValue(codePoint, out int code) ? code : null;

    /// <summary>The standard character for one of Adobe's private-use characters, or null.</summary>
    public static int? StandardFor(int codePoint) => Equivalents.TryGetValue(codePoint, out int standard) ? standard : null;
}
