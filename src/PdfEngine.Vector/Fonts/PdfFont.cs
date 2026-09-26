using System;
using System.Collections.Generic;
using PdfEngine.Geometry;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Fonts;

/// <summary>Kind of font program embedded through the font descriptor (ISO 32000-2 9.9).</summary>
public enum PdfFontProgramKind
{
    /// <summary>No embedded font program.</summary>
    None = 0,

    /// <summary>/FontFile2: TrueType (sfnt) program.</summary>
    TrueType,

    /// <summary>/FontFile: Type 1 program (PFA/PFB-style).</summary>
    Type1,

    /// <summary>/FontFile3 with /Subtype /Type1C: bare CFF.</summary>
    Type1C,

    /// <summary>/FontFile3 with /Subtype /CIDFontType0C: CID-keyed bare CFF.</summary>
    CidFontType0C,

    /// <summary>/FontFile3 with /Subtype /OpenType.</summary>
    OpenType,
}

/// <summary>Vertical metrics of one CID (ISO 32000-2 9.7.4.3): advance W1y and position vector (VX, VY).</summary>
public readonly record struct PdfVerticalMetrics(double W1y, double VX, double VY);

/// <summary>
/// Resolved PDF font representation containing metric tables, encoding, and ToUnicode mappings.
/// Supports simple (Type1, MMType1, TrueType, Type3) and composite (Type0 over CIDFontType0 /
/// CIDFontType2) fonts. Instances are immutable after resolution and safe to share across threads.
/// </summary>
/// <remarks>
/// Character codes are read with <see cref="ReadCode"/>. Widths are in text-space 1/1000 em units for
/// every font kind: <see cref="GetGlyphWidth"/> takes the character code for simple fonts and the
/// CID for composite fonts. <see cref="MapToUnicode"/> always takes the character code.
/// </remarks>
public sealed class PdfFont
{
    private static readonly PdfMatrix DefaultFontMatrix = new(0.001, 0, 0, 0.001, 0, 0);

    public PdfFont(
        string name,
        string baseFont,
        string subtype,
        int firstChar = 0,
        int lastChar = 255,
        IReadOnlyList<double>? widths = null,
        Dictionary<int, double>? cidWidths = null,
        double missingWidth = 0.0,
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
        PostScriptName = StripSubsetPrefix(baseFont);
        MetricsFontName = Standard14Fonts.NormalizeName(baseFont);
        if (!IsComposite && subtype != "Type3")
        {
            GlyphNames = MetricsFontName switch
            {
                "Symbol" => FontEncodings.Symbol,
                "ZapfDingbats" => FontEncodings.ZapfDingbats,
                _ => FontEncodings.Standard
            };
        }
    }

    // ---- Identity ---------------------------------------------------------------------------

    /// <summary>Resource name the font was first resolved under (e.g. "F1").</summary>
    public string Name { get; }

    /// <summary>/BaseFont as written (may include a subset prefix).</summary>
    public string BaseFont { get; }

    /// <summary>/Subtype of the font dictionary (Type1, MMType1, TrueType, Type3, Type0).</summary>
    public string Subtype { get; }

    /// <summary>True for Type0 (composite) fonts.</summary>
    public bool IsComposite => Subtype == "Type0";

    /// <summary>True for Type3 fonts (glyphs are content-stream procedures in <see cref="CharProcs"/>).</summary>
    public bool IsType3 => Subtype == "Type3";

    /// <summary>/Subtype of the descendant CIDFont (CIDFontType0 / CIDFontType2) for composite fonts.</summary>
    public string? CidFontSubtype { get; internal init; }

    /// <summary>CIDSystemInfo /Ordering of an Adobe character collection (Japan1, GB1, CNS1, Korea1), else null.</summary>
    public string? CidOrdering { get; internal init; }

    /// <summary>BaseFont without the ABCDEF+ subset prefix (descendant name for composite fonts).</summary>
    public string? PostScriptName { get; internal init; }

    /// <summary>
    /// True when the font resource could not be found or resolved and this is a substitute
    /// (Helvetica metrics). Rendering should report the gap.
    /// </summary>
    public bool IsMissingResource { get; internal init; }

    /// <summary>Classified reason why this font cannot be rendered faithfully by the vector path, if any.</summary>
    public PdfFallbackReason? UnsupportedReason { get; internal init; }

    // ---- Metrics ----------------------------------------------------------------------------

    public int FirstChar { get; }
    public int LastChar { get; }

    /// <summary>/Widths for simple fonts (glyph space for Type3, 1/1000 em otherwise).</summary>
    public IReadOnlyList<double>? Widths { get; }

    /// <summary>Horizontal CID widths from /W (1/1000 em).</summary>
    public Dictionary<int, double>? CidWidths { get; }

    /// <summary>/FontDescriptor /MissingWidth (default 0) used for simple-font codes outside /Widths.</summary>
    public double MissingWidth { get; }

    /// <summary>/DW of the CIDFont (default 1000).</summary>
    public double DefaultWidth { get; }

    /// <summary>Vertical metrics from /W2 keyed by CID.</summary>
    public Dictionary<int, PdfVerticalMetrics>? VerticalWidths { get; internal init; }

    /// <summary>/DW2 first element: default vertical origin VY (default 880).</summary>
    public double DefaultVerticalPositionY { get; internal init; } = 880;

    /// <summary>/DW2 second element: default vertical advance W1y (default -1000).</summary>
    public double DefaultVerticalAdvance { get; internal init; } = -1000;

    /// <summary>/FontMatrix (Type3) or the implicit [0.001 0 0 0.001 0 0].</summary>
    public PdfMatrix FontMatrix { get; internal init; } = DefaultFontMatrix;

    /// <summary>Ascent as an em fraction (descriptor /Ascent / 1000; default 0.8).</summary>
    public double Ascent { get; internal init; } = 0.8;

    /// <summary>Descent as an em fraction (descriptor /Descent / 1000; default -0.2).</summary>
    public double Descent { get; internal init; } = -0.2;

    /// <summary>/FontBBox (glyph space units; 1/1000 em for non-Type3 fonts).</summary>
    public PdfRect? FontBBox { get; internal init; }

    /// <summary>Raw /FontDescriptor /Flags.</summary>
    public int Flags { get; internal init; }

    public bool IsSymbolic { get; internal init; }
    public bool IsSerif { get; internal init; }
    public bool IsFixedPitch { get; internal init; }
    public bool IsItalic { get; internal init; }
    public bool IsBold { get; internal init; }

    // ---- Encoding / Unicode -----------------------------------------------------------------

    /// <summary>Code → Unicode from the /ToUnicode CMap.</summary>
    public Dictionary<int, string>? ToUnicodeMap { get; }

    /// <summary>Effective simple-font encoding: code → glyph name (base encoding + /Differences).</summary>
    internal string?[]? GlyphNames { get; init; }

    /// <summary>Code → CID CMap for composite fonts.</summary>
    internal PdfCMap? CMap { get; init; }

    /// <summary>True when composite codes are UCS-2/UTF-16 (predefined Uni*-UCS2/UTF16 CMaps).</summary>
    internal bool CodesAreUnicode { get; init; }

    /// <summary>True for Identity-V or a vertical (WMode 1) CMap.</summary>
    public bool IsVertical => CMap?.IsVertical ?? false;

    // ---- Font program -----------------------------------------------------------------------

    /// <summary>Decoded embedded font program bytes, or null.</summary>
    public byte[]? EmbeddedFontData { get; }

    /// <summary>Kind of <see cref="EmbeddedFontData"/>.</summary>
    public PdfFontProgramKind EmbeddedProgramKind { get; internal init; }

    /// <summary>Decoded /CIDToGIDMap stream (2-byte big-endian GIDs), null for Identity/absent.</summary>
    internal byte[]? CidToGidMap { get; init; }

    internal TrueTypeCmap? TrueTypeCmap { get; init; }

    // ---- Type3 ------------------------------------------------------------------------------

    /// <summary>Type3 /CharProcs (glyph name → content stream).</summary>
    public PdfDictionary? CharProcs { get; internal init; }

    /// <summary>Type3 /Resources for executing the glyph procedures.</summary>
    public PdfDictionary? Type3Resources { get; internal init; }

    /// <summary>Standard 14 font whose metrics are used when /Widths is absent (normalized name).</summary>
    internal string? MetricsFontName { get; init; }

    // ---- API --------------------------------------------------------------------------------

    /// <summary>
    /// Reads one character code at <paramref name="offset"/>. Returns the number of bytes consumed
    /// (always ≥ 1, so callers cannot loop forever). For simple fonts code == cid == the byte.
    /// </summary>
    public int ReadCode(ReadOnlySpan<byte> bytes, int offset, out int code, out int cid)
    {
        if (offset < 0 || offset >= bytes.Length)
        {
            code = 0;
            cid = 0;
            return 1;
        }

        if (!IsComposite)
        {
            code = bytes[offset];
            cid = code;
            return 1;
        }

        return (CMap ?? PdfCMap.IdentityH).ReadCode(bytes, offset, out code, out cid);
    }

    /// <summary>
    /// Word spacing (Tw) applies only to the single-byte code 32 (ISO 32000-2 9.3.3), for simple
    /// and composite fonts alike.
    /// </summary>
    public bool IsWordSpaceCode(int code, int bytesConsumed) => bytesConsumed == 1 && code == 32;

    /// <summary>
    /// Horizontal advance in text-space 1/1000 em units. The argument is the character code for simple
    /// fonts and the CID for composite fonts. Type3 widths are converted through
    /// <see cref="FontMatrix"/> (glyph width × A × 1000).
    /// </summary>
    public double GetGlyphWidth(int codeOrCid)
    {
        if (IsComposite)
        {
            if (CidWidths != null && CidWidths.TryGetValue(codeOrCid, out double cidWidth))
                return cidWidth;
            return DefaultWidth;
        }

        if (IsType3)
        {
            double w = MissingWidth;
            if (Widths != null && codeOrCid >= FirstChar && codeOrCid - FirstChar < Widths.Count)
                w = Widths[codeOrCid - FirstChar];
            return w * FontMatrix.A * 1000.0;
        }

        if (Widths != null && Widths.Count > 0)
        {
            if (codeOrCid >= FirstChar && codeOrCid - FirstChar < Widths.Count)
                return Widths[codeOrCid - FirstChar];
            return MissingWidth;
        }

        // No /Widths: standard 14 metrics (or a substitute chosen from the descriptor flags).
        string metricsFont = MetricsFontName ?? SubstituteMetricsFont();
        string? glyph = GetGlyphName(codeOrCid);
        if (glyph == null)
            return MissingWidth;
        if (Standard14Fonts.TryGetWidthByName(metricsFont, glyph, out double stdWidth))
            return stdWidth;
        return metricsFont.StartsWith("Courier", StringComparison.Ordinal) ? 600 : (MissingWidth > 0 ? MissingWidth : 500);
    }

    /// <summary>Vertical metrics for a CID (/W2, else /DW2 with VX = w0 / 2).</summary>
    public PdfVerticalMetrics GetVerticalMetrics(int cid)
    {
        if (VerticalWidths != null && VerticalWidths.TryGetValue(cid, out var vm))
            return vm;
        return new PdfVerticalMetrics(DefaultVerticalAdvance, GetGlyphWidth(cid) / 2.0, DefaultVerticalPositionY);
    }

    /// <summary>
    /// Glyph name for a simple-font code after /Encoding, /BaseEncoding and /Differences (or the
    /// built-in encoding); for Type3 this is the key into <see cref="CharProcs"/>. Null when unknown
    /// or for composite fonts.
    /// </summary>
    public string? GetGlyphName(int code)
    {
        if (IsComposite || GlyphNames == null || code < 0 || code >= GlyphNames.Length)
            return null;
        return GlyphNames[code];
    }

    /// <summary>
    /// Unicode text for a character code. Priority: /ToUnicode CMap, then encoding glyph name via the
    /// Adobe Glyph List, then the code as Latin-1 (last resort).
    /// </summary>
    public string MapToUnicode(int code)
    {
        if (ToUnicodeMap != null && ToUnicodeMap.TryGetValue(code, out string? unicode))
            return unicode;

        if (IsComposite)
        {
            if (CodesAreUnicode && code is > 0 and <= 0xFFFF && !(code >= 0xD800 && code <= 0xDFFF))
                return ((char)code).ToString();
            // No /ToUnicode: an Adobe collection's CID still identifies the character.
            if (CidOrdering != null && CMap != null && PredefinedCMaps.CidToUnicode(CidOrdering, CMap.LookupAnyLength(code)) is { } fromCid)
                return fromCid;
        }
        else if (GetGlyphName(code) is { } glyph && GlyphList.ToUnicode(glyph) is { } fromName)
        {
            return fromName;
        }

        if (code >= 0 && code <= 255)
            return ((char)code).ToString();

        return "?";
    }

    /// <summary>
    /// Glyph index in <see cref="EmbeddedFontData"/>: CIDToGIDMap for CIDFontType2; the sfnt cmap
    /// (ISO 32000-2 9.6.5.4) for simple TrueType/OpenType programs, 0 (.notdef) when unmapped;
    /// -1 when unknown (no usable program — the renderer substitutes by Unicode).
    /// </summary>
    public int GetGlyphId(int code, int cid)
    {
        if (IsComposite)
        {
            if (CidFontSubtype != "CIDFontType2" || EmbeddedFontData == null)
                return -1;
            if (cid < 0)
                return 0;
            if (CidToGidMap == null)
                return cid;
            long idx = (long)cid * 2;
            if (idx + 1 >= CidToGidMap.Length)
                return 0;
            return (CidToGidMap[idx] << 8) | CidToGidMap[idx + 1];
        }

        if (TrueTypeCmap is not { } cmap || code < 0 || code > 255)
            return -1;

        if (!IsSymbolic && GetGlyphName(code) is { } glyph)
        {
            if (cmap.Has(3, 1) && GlyphList.ToUnicode(glyph) is { } u)
            {
                int scalar = char.ConvertToUtf32(u, 0);
                int gid = cmap.Lookup(3, 1, scalar);
                if (gid > 0)
                    return gid;
            }
            if (cmap.Has(1, 0) && FontEncodings.TryGetMacRomanCode(glyph, out int macCode))
            {
                int gid = cmap.Lookup(1, 0, macCode);
                if (gid > 0)
                    return gid;
            }
        }

        if (cmap.Has(3, 0))
        {
            foreach (int prefix in new[] { 0, 0xF000, 0xF100, 0xF200 })
            {
                int gid = cmap.Lookup(3, 0, prefix + code);
                if (gid > 0)
                    return gid;
            }
        }
        if (cmap.Has(1, 0))
        {
            int gid = cmap.Lookup(1, 0, code);
            if (gid > 0)
                return gid;
        }

        // Lenient last resort for mis-flagged fonts: the code in any subtable.
        return cmap.LookupFirst(code);
    }

    private string SubstituteMetricsFont()
    {
        if (IsFixedPitch)
            return IsBold ? (IsItalic ? "Courier-BoldOblique" : "Courier-Bold") : (IsItalic ? "Courier-Oblique" : "Courier");
        if (IsSerif)
            return IsBold ? (IsItalic ? "Times-BoldItalic" : "Times-Bold") : (IsItalic ? "Times-Italic" : "Times-Roman");
        return IsBold ? (IsItalic ? "Helvetica-BoldOblique" : "Helvetica-Bold") : (IsItalic ? "Helvetica-Oblique" : "Helvetica");
    }

    /// <summary>Removes a subset tag (six uppercase letters followed by '+').</summary>
    internal static string StripSubsetPrefix(string name)
    {
        if (name.Length > 7 && name[6] == '+')
        {
            for (int i = 0; i < 6; i++)
            {
                if (name[i] is < 'A' or > 'Z')
                    return name;
            }
            return name[7..];
        }
        return name;
    }
}
