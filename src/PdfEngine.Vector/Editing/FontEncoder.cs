using System;
using System.Collections.Generic;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Writes characters in a document's own font, when it can: the code that shows each character,
/// found through the font's ToUnicode map or encoding, and only when the font really has the glyph
/// (a subset embeds only the glyphs its text used).
/// </summary>
internal sealed class FontEncoder
{
    private readonly PdfFont _font;
    private readonly Dictionary<string, (byte[] Code, double Width)> _codes = new(StringComparer.Ordinal);

    public FontEncoder(PdfFont font)
    {
        _font = font;
        if (font.IsType3 || font.IsVertical) return;

        PreparedFontProgram? program = null;
        TrueTypeFontFile? outlines = null;
        if (font.EmbeddedFontData != null)
        {
            try
            {
                program = FontProgramPreparer.Prepare(font);
                if (program?.Format == PdfEngine.Vector.PdfFontProgramFormat.TrueType) outlines = TrueTypeFontFile.TryLoad(program.Sfnt);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { program = null; }
            if (program == null) return; // an embedded program we cannot read: its glyphs are unknown
        }

        bool Has(int code, int cid, string text)
        {
            if (program == null) return !font.IsComposite && font.GetGlyphName(code) is { } name && name != ".notdef";
            int gid = program.GetGlyphId(code, cid);
            if (gid <= 0) return false;
            // A subset keeps glyph ids but empties the glyphs it dropped; a space has no outline anyway.
            return outlines == null || outlines.HasOutline(gid) || text.Trim().Length == 0;
        }

        if (!font.IsComposite)
        {
            for (int code = 0; code < 256; code++)
            {
                if (font.ToUnicodeMap == null && font.GetGlyphName(code) == null) continue;
                if (font.ToUnicodeMap != null && !font.ToUnicodeMap.ContainsKey(code) && font.GetGlyphName(code) == null) continue;
                string text = font.MapToUnicode(code);
                if (text.Length == 0 || text == "?" && code != '?' || _codes.ContainsKey(text)) continue;
                if (!Has(code, code, text)) continue;
                double width = font.GetGlyphWidth(code);
                if (width <= 0 && text.Trim().Length > 0) continue;
                _codes[text] = (new[] { (byte)code }, width);
            }
        }
        else if (font.ToUnicodeMap != null)
        {
            foreach (var (code, text) in font.ToUnicodeMap)
            {
                if (text.Length == 0 || _codes.ContainsKey(text)) continue;
                if (CodeBytes(code) is not { } bytes) continue;
                font.ReadCode(bytes, 0, out _, out int cid);
                if (!Has(code, cid, text)) continue;
                _codes[text] = (bytes, font.GetGlyphWidth(cid));
            }
        }
    }

    /// <summary>The bytes of a composite-font code, of the length its CMap reads it at.</summary>
    private byte[]? CodeBytes(int code)
    {
        for (int n = 1; n <= 4; n++)
        {
            if (n < 4 && code >> (8 * n) != 0) continue;
            var bytes = new byte[n];
            for (int i = 0; i < n; i++) bytes[i] = (byte)(code >> (8 * (n - 1 - i)));
            if (_font.ReadCode(bytes, 0, out int read, out _) == n && read == code) return bytes;
        }
        return null;
    }

    /// <summary>The code for a character (one text element), and its width in 1/1000 em.</summary>
    public bool TryEncode(string text, out byte[] code, out double width)
    {
        if (_codes.TryGetValue(text, out var found))
        {
            code = found.Code;
            width = found.Width;
            return true;
        }
        code = Array.Empty<byte>();
        width = 0;
        return false;
    }
}
