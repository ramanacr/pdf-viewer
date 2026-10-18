using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Create;

/// <summary>How a plain text file is set on pages.</summary>
public sealed record PdfTextLayoutOptions
{
    /// <summary>The paper in points; A4 by default.</summary>
    public double PageWidth { get; init; } = 595.276;
    public double PageHeight { get; init; } = 841.89;
    public double Margin { get; init; } = 54;
    public double FontSize { get; init; } = 10;
    /// <summary>The font family, tried first; characters it lacks come from the system's fallback fonts.</summary>
    public string FontFamily { get; init; } = "Consolas";
    /// <summary>Columns between tab stops.</summary>
    public int TabSize { get; init; } = 8;
    public double LineSpacing { get; init; } = 1.2;
}

/// <summary>What a text layout produced.</summary>
public sealed record PdfTextLayoutResult(int Pages, int Lines, int Fonts);

/// <summary>
/// Plain text to PDF with our own layout: lines wrap at the margins (at a space when there is one),
/// a form feed starts a page, tabs go to the next stop. The text is set in an installed font
/// embedded as a subset, with a ToUnicode map, so it can be searched, selected and copied; a
/// character the font lacks is taken from the first fallback font that has it. Pages are written
/// as they are filled. Scripts that need shaping (Arabic joining, Indic reordering) are set
/// character by character, so they read correctly when copied but may not look joined.
/// </summary>
public static class PdfTextDocument
{
    /// <summary>Reads a text file's bytes as text: by its byte-order mark, as UTF-8 when it is valid UTF-8, else as Windows-1252.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return Encoding.UTF32.GetString(bytes[4..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: most likely the Windows ANSI code page; Latin-1 is its subset when that is unavailable.
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }

    public static PdfTextLayoutResult Write(string text, Stream output, PdfTextLayoutOptions? options = null, PdfCreateInfo? info = null,
        SystemFontCatalog? catalog = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfTextLayoutOptions();
        catalog ??= SystemFontCatalog.Installed;
        var primaryFace = catalog.FindFamily(options.FontFamily, false, false) ?? catalog.FindFamily("Courier New", false, false)
            ?? catalog.FindFamily("Arial", false, false) ?? catalog.Faces.FirstOrDefault();
        var primary = primaryFace != null ? catalog.Load(primaryFace) : null;
        if (primary == null) throw new InvalidOperationException("No installed font can be embedded for the text.");

        using var writer = new PdfDocumentWriter(output);
        var fonts = new List<(EmbeddedFontBuilder Builder, int Number)>();
        var fallbackFonts = new List<TrueTypeFontFile>();
        IEnumerator<SystemFontFace>? fallbackFaces = null;
        var fontOf = new Dictionary<int, (int Font, int Gid)>();

        int FontIndex(TrueTypeFontFile font)
        {
            for (int i = 0; i < fonts.Count; i++) if (ReferenceEquals(fonts[i].Builder.Font, font)) return i;
            fonts.Add((new EmbeddedFontBuilder(font, "F" + fonts.Count), writer.Reserve()));
            return fonts.Count - 1;
        }

        (int Font, int Gid) Glyph(int codePoint)
        {
            if (fontOf.TryGetValue(codePoint, out var known)) return known;
            int gid = primary.GlyphFor(codePoint);
            (int, int) result = (FontIndex(primary), gid);
            if (gid == 0)
            {
                // The loaded fallbacks first, then the next ones, as far as needed.
                var found = fallbackFonts.Select(f => (Font: f, Gid: f.GlyphFor(codePoint))).FirstOrDefault(f => f.Gid != 0);
                fallbackFaces ??= catalog.Fallbacks(false, false).GetEnumerator();
                while (found.Font == null && fallbackFaces.MoveNext())
                {
                    if (catalog.Load(fallbackFaces.Current) is not { } next || fallbackFonts.Contains(next)) continue;
                    fallbackFonts.Add(next);
                    if (next.GlyphFor(codePoint) is int g and not 0) found = (next, g);
                }
                if (found.Font != null) result = (FontIndex(found.Font), found.Gid);
            }
            fontOf[codePoint] = result;
            return result;
        }

        double size = options.FontSize, leading = size * options.LineSpacing;
        double margin = Math.Clamp(options.Margin, 0, Math.Min(options.PageWidth, options.PageHeight) / 3);
        double width = options.PageWidth - 2 * margin;
        double top = options.PageHeight - margin - primary.Ascender * size / primary.UnitsPerEm;
        int linesPerPage = Math.Max(1, (int)Math.Floor((options.PageHeight - 2 * margin) / leading));

        var content = new StringBuilder();
        var pageFonts = new SortedSet<int>();
        int lineOnPage = 0, lines = 0;

        void FlushPage(bool always)
        {
            if (lineOnPage == 0 && !always) return;
            var fontRes = pageFonts.ToDictionary(i => fonts[i].Builder.ResourceName, i => (PdfObject)new PdfIndirectRef(fonts[i].Number));
            var resources = new PdfDictionary(new Dictionary<string, PdfObject> { ["Font"] = new PdfDictionary(fontRes) });
            writer.AddPage(options.PageWidth, options.PageHeight, Encoding.ASCII.GetBytes(content.ToString()), resources);
            progress?.Report(writer.PageCount);
            content.Clear();
            pageFonts.Clear();
            lineOnPage = 0;
        }

        void EmitLine(List<(int Font, int Gid, string Text, double Advance)> glyphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lineOnPage >= linesPerPage) FlushPage(false);
            double y = top - lineOnPage * leading;
            lineOnPage++;
            lines++;
            if (glyphs.Count == 0) return;
            content.Append("BT\n1 0 0 1 ").Append(F(margin)).Append(' ').Append(F(y)).Append(" Tm\n");
            int current = -1;
            var run = new StringBuilder();
            void EndRun()
            {
                if (run.Length > 0) content.Append('<').Append(run).Append("> Tj\n");
                run.Clear();
            }
            foreach (var (font, gid, chars, _) in glyphs)
            {
                if (font != current)
                {
                    EndRun();
                    content.Append('/').Append(fonts[font].Builder.ResourceName).Append(' ').Append(F(size)).Append(" Tf\n");
                    pageFonts.Add(font);
                    current = font;
                }
                byte[] code = fonts[font].Builder.Encode(gid, chars);
                run.Append(code[0].ToString("X2", CultureInfo.InvariantCulture)).Append(code[1].ToString("X2", CultureInfo.InvariantCulture));
            }
            EndRun();
            content.Append("ET\n");
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string pageText in text.Split('\f'))
        {
            if (writer.PageCount > 0 || lineOnPage > 0) FlushPage(true);
            foreach (string rawLine in pageText.Split('\n'))
            {
                // Glyphs of the logical line, tabs expanded to the next stop.
                var glyphs = new List<(int Font, int Gid, string Text, double Advance)>();
                int column = 0;
                foreach (var rune in rawLine.EnumerateRunes())
                {
                    if (rune.Value == '\t')
                    {
                        int spaces = options.TabSize - column % Math.Max(1, options.TabSize);
                        for (int s = 0; s < spaces; s++) Add(' ');
                        continue;
                    }
                    if (rune.Value < 0x20 || rune.Value is >= 0x7F and < 0xA0) continue; // other control characters are not drawn
                    Add(rune.Value);

                    void Add(int cp)
                    {
                        var (font, gid) = Glyph(cp);
                        double advance = fonts[font].Builder.Font.Advance(gid) * size / 1000;
                        glyphs.Add((font, gid, char.ConvertFromUtf32(cp), advance));
                        column++;
                    }
                }

                // Wrapped at the last space that fits, else wherever the line is full.
                int start = 0;
                while (true)
                {
                    double x = 0;
                    int end = start, lastBreak = -1;
                    while (end < glyphs.Count && (x + glyphs[end].Advance <= width || end == start))
                    {
                        if (glyphs[end].Text == " ") lastBreak = end;
                        x += glyphs[end].Advance;
                        end++;
                    }
                    if (end >= glyphs.Count)
                    {
                        EmitLine(glyphs.GetRange(start, glyphs.Count - start));
                        break;
                    }
                    int cut = lastBreak > start ? lastBreak + 1 : end;
                    EmitLine(glyphs.GetRange(start, cut - start));
                    start = cut;
                }
            }
        }
        FlushPage(writer.PageCount == 0);

        foreach (var (builder, number) in fonts)
        {
            if (!builder.IsUsed) { writer.Write(number, PdfNull.Instance); continue; }
            var objects = new Dictionary<int, PdfObject>();
            bool first = true;
            builder.Write(() => { if (first) { first = false; return number; } return writer.Reserve(); }, objects);
            foreach (var (n, obj) in objects) writer.Write(n, obj);
        }
        writer.Finish(info);
        return new PdfTextLayoutResult(writer.PageCount, lines, fonts.Count(f => f.Builder.IsUsed));
    }

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 3));
}
