using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.PdfA;

internal sealed partial class PdfAChecker
{
    private PdfFontResolver? _fontResolver;

    private static readonly HashSet<string> PredefinedCMaps = new(StringComparer.Ordinal)
    {
        "Identity-H", "Identity-V", "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V",
        "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V", "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V",
        "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H", "UniCNS-UTF16-V", "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H",
        "90msp-RKSJ-V", "90pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V", "UniJIS-UCS2-H", "UniJIS-UCS2-V",
        "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V", "UniJIS-UTF16-H", "UniJIS-UTF16-V", "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H",
        "KSCms-UHC-HW-V", "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
    };

    /// <summary>Font rules (P1 6.3, P2 6.2.11).</summary>
    private void CheckFont(PdfDictionary font, string where, bool invisibleOnly, List<byte[]>? shown, PdfDictionary? resources)
    {
        string? subtype = font.GetName("Subtype");
        string baseFont = font.GetName("BaseFont") ?? string.Empty;
        string label = baseFont.Length > 0 ? $"the font {baseFont}" : "a font";

        // Font dictionaries (P1 6.3.2, P2 6.2.11.2).
        if (font.GetName("Type") != "Font") Fail("6.3.2", "6.2.11.2", 1, $"{Cap(label)} is not of /Type /Font.", where);
        if (subtype is not ("Type0" or "Type1" or "MMType1" or "Type3" or "TrueType")) { Fail("6.3.2", "6.2.11.2", 2, $"{Cap(label)} has the subtype /{subtype}.", where); return; }
        if (subtype != "Type3" && baseFont.Length == 0) Fail("6.3.2", "6.2.11.2", 3, "A font has no /BaseFont.", where);
        if (subtype is not "Type0")
        {
            bool standard14 = Standard14Fonts.NormalizeName(baseFont) != null && (subtype == "Type1") && Descriptor(font) is null;
            var widths = _r.Resolve(font["Widths"]) as PdfArray;
            long? first = _r.Resolve(font["FirstChar"]) is PdfInteger fc ? fc.Value : null, last = _r.Resolve(font["LastChar"]) is PdfInteger lc ? lc.Value : null;
            if (!standard14 || _part >= 2)
            {
                if (first == null) Fail("6.3.2", "6.2.11.2", 4, $"{Cap(label)} has no /FirstChar.", where);
                if (last == null) Fail("6.3.2", "6.2.11.2", 5, $"{Cap(label)} has no /LastChar.", where);
                if (widths == null) Fail("6.3.2", "6.2.11.2", 6, $"{Cap(label)} has no /Widths.", where);
                else if (first != null && last != null && widths.Count != last - first + 1)
                    Fail("6.3.2", "6.2.11.2", 7, $"{Cap(label)} has {widths.Count} widths for codes {first} to {last}.", where);
            }
        }

        PdfDictionary? descendant = null;
        if (subtype == "Type0")
        {
            descendant = (_r.Resolve(font["DescendantFonts"]) as PdfArray)?.Items.Select(_r.Resolve).OfType<PdfDictionary>().FirstOrDefault();
            if (descendant == null) { Fail("6.3.3.1", "6.2.11.3.1", 1, $"{Cap(label)} has no descendant CIDFont.", where); return; }
            CheckComposite(font, descendant, label, where, invisibleOnly);
        }

        if (subtype == "Type3") { CheckType3(font, where, shown); return; }
        var descriptor = Descriptor(descendant ?? font);
        var program = descriptor == null ? null
            : (_r.Resolve(descriptor["FontFile"]) as PdfStream, _r.Resolve(descriptor["FontFile2"]) as PdfStream, _r.Resolve(descriptor["FontFile3"]) as PdfStream) switch
            {
                (var f1, _, _) when f1 != null => ("FontFile", f1),
                (_, var f2, _) when f2 != null => ("FontFile2", f2),
                (_, _, var f3) when f3 != null => ("FontFile3", f3),
                _ => ((string, PdfStream)?)null,
            };

        // Embedding (P1 6.3.4, P2 6.2.11.4.1): every font drawn visibly is embedded.
        if (program == null)
        {
            if (!invisibleOnly) Fail("6.3.4", "6.2.11.4.1", 1, $"{Cap(label)} is not embedded.", where);
            return;
        }
        string realSubtype = descendant?.GetName("Subtype") ?? subtype!;
        string? programType = program.Value.Item2.Dictionary.GetName("Subtype");
        bool kindOk = (realSubtype, program.Value.Item1) switch
        {
            ("Type1" or "MMType1", "FontFile") => true,
            ("Type1" or "MMType1", "FontFile3") => programType is "Type1C" || (_part >= 2 && programType == "OpenType"),
            ("TrueType", "FontFile2") => true,
            ("TrueType", "FontFile3") => _part >= 2 && programType == "OpenType",
            ("CIDFontType0", "FontFile3") => programType is "CIDFontType0C" || (_part >= 2 && programType == "OpenType"),
            ("CIDFontType2", "FontFile2") => true,
            ("CIDFontType2", "FontFile3") => _part >= 2 && programType == "OpenType",
            _ => false,
        };
        if (!kindOk) Fail("6.3.4", "6.2.11.4.1", 2, $"{Cap(label)} embeds a {program.Value.Item1}{(programType != null ? "/" + programType : "")} program, which does not suit a {realSubtype} font.", where);

        PdfFont? pdfFont = null;
        PreparedFontProgram? prepared = null;
        try
        {
            _fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default);
            pdfFont = _fontResolver.ResolveFont(font, "F");
            prepared = FontProgramPreparer.Prepare(pdfFont);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { prepared = null; }
        if (prepared == null && !invisibleOnly) Fail("6.3.4", "6.2.11.4.1", 1, $"{Cap(label)}'s embedded program cannot be read.", where);

        // Subsets (P1 6.3.5, P2 6.2.11.4.2).
        bool subset = baseFont.Length > 7 && baseFont[6] == '+' && baseFont.Take(6).All(char.IsAsciiLetterUpper);
        if (subset && descriptor != null)
        {
            if (_part == 1 && realSubtype is "Type1" or "MMType1" && descriptor["CharSet"] == null)
                Fail("6.3.5", 2, $"{Cap(label)} is a subset but its descriptor has no /CharSet.", where);
            if (_part == 1 && realSubtype.StartsWith("CIDFontType", StringComparison.Ordinal) && _r.Resolve(descriptor["CIDSet"]) is not PdfStream)
                Fail("6.3.5", 3, $"{Cap(label)} is a subset but its descriptor has no /CIDSet.", where);
        }

        TrueTypeFontFile? truetype = prepared?.Format == PdfFontProgramFormat.TrueType ? TrueTypeFontFile.TryLoad(prepared.Sfnt) : null;

        // Encodings (P1 6.3.7, P2 6.2.11.6).
        if (realSubtype == "TrueType" && descriptor != null)
        {
            bool symbolic = ((descriptor.GetInteger("Flags") ?? 0) & 4) != 0;
            var cmap = TrueTypeCmap.TryParse(Decode(program.Value.Item2));
            if (!symbolic)
            {
                var enc = _r.Resolve(font["Encoding"]);
                string? name = enc is PdfName en ? en.Value : (enc as PdfDictionary)?.GetName("BaseEncoding");
                if (name is not ("MacRomanEncoding" or "WinAnsiEncoding"))
                    Fail("6.3.7", "6.2.11.6", 1, $"{Cap(label)} is a non-symbolic TrueType font without the MacRoman or WinAnsi encoding.", where);
                if (enc is PdfDictionary ed && _r.Resolve(ed["Differences"]) is PdfArray diffs && diffs.OfType<PdfName>().Any(n => GlyphList.ToUnicode(n.Value) == null && n.Value != ".notdef"))
                    Fail("6.3.7", "6.2.11.6", 1, $"{Cap(label)}'s encoding differences use glyph names outside the Adobe Glyph List.", where);
                if (cmap != null && !cmap.Has(3, 1) && !cmap.Has(1, 0))
                    Fail("6.3.7", "6.2.11.6", 2, $"{Cap(label)} is a non-symbolic TrueType font whose cmap has neither a (3,1) nor a (1,0) subtable.", where);
            }
            else
            {
                if (font.ContainsKey("Encoding")) Fail("6.3.7", "6.2.11.6", 3, $"{Cap(label)} is a symbolic TrueType font with an /Encoding.", where);
                if (cmap != null && cmap.Subtables.Count != 1 && !cmap.Has(3, 0))
                    Fail("6.3.7", "6.2.11.6", 4, $"{Cap(label)} is a symbolic TrueType font whose cmap has {cmap.Subtables.Count} subtables and no (3,0) subtable.", where);
            }
        }

        if (shown == null || pdfFont == null) return;
        var codes = DecodeCodes(pdfFont, shown);
        if (subset && descriptor != null && prepared != null) CheckSubsetLists(descriptor, pdfFont, prepared, realSubtype, label, where, codes);

        // Metrics (P1 6.3.6, P2 6.2.11.5): the widths the document gives match the program's.
        if (truetype != null && prepared != null)
        {
            foreach (var (code, cid) in codes.Take(512))
            {
                int gid = prepared.GetGlyphId(code, cid);
                if (gid <= 0 || gid >= truetype.GlyphCount) continue;
                double declared = pdfFont.GetGlyphWidth(pdfFont.IsComposite ? cid : code), actual = truetype.Advance(gid);
                bool hasDeclared = pdfFont.IsComposite || (pdfFont.Widths != null && code >= pdfFont.FirstChar && code <= pdfFont.LastChar);
                if (hasDeclared && Math.Abs(declared - actual) > 1)
                {
                    Fail("6.3.6", "6.2.11.5", 1, $"{Cap(label)} gives code {code} a width of {declared:0.#}, but its program's glyph is {actual:0.#} wide.", where);
                    break;
                }
            }
        }

        // CIDs are at most 65,535 (P1 6.1.12, P2 6.1.13); every shown glyph is in the program (P1 6.3.5, P2 6.2.11.8: no .notdef).
        if (pdfFont.IsComposite && codes.Any(c => c.Cid > 65535))
            Fail("6.1.12", "6.1.13", 10, $"{Cap(label)} shows a CID above 65,535.", where);
        // An embedded CMap built on another embedded CMap maps codes through a chain this check does not follow.
        bool chainedCMap = _r.Resolve(font["Encoding"]) is PdfStream cm && (cm.Dictionary.ContainsKey("UseCMap")
                           || (Decode(cm) is { } cmData && System.Text.Encoding.Latin1.GetString(cmData).Contains("usecmap", StringComparison.Ordinal)));
        if (prepared != null && !chainedCMap)
        {
            var cff = prepared.Format == PdfFontProgramFormat.OpenTypeCff ? CffOf(prepared) : null;
            foreach (var (code, cid) in codes)
            {
                if (GlyphPresent(pdfFont, prepared, truetype, cff, code, cid)) continue;
                Fail("6.3.5", "6.2.11.8", 1, $"{Cap(label)} shows code {code}, whose glyph its program does not have (it draws .notdef).", where);
                break;
            }
        }

        // Unicode (P2 level U, 6.2.11.7): every shown code maps to Unicode.
        if (_unicode)
        {
            foreach (var (code, _) in codes)
            {
                if (HasUnicode(pdfFont, code) || BuiltInUnicode(prepared, code)) continue;
                Fail(null, "6.2.11.7", 1, $"{Cap(label)} shows code {code}, which has no Unicode value.", where);
                break;
            }
        }
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    private PdfDictionary? Descriptor(PdfDictionary font) => _r.Resolve(font["FontDescriptor"]) as PdfDictionary;

    private static List<(int Code, int Cid)> DecodeCodes(PdfFont font, List<byte[]> shown)
    {
        var set = new HashSet<(int, int)>();
        foreach (var s in shown)
        {
            int pos = 0;
            while (pos < s.Length)
            {
                int n = font.ReadCode(s, pos, out int code, out int cid);
                if (n <= 0) n = 1;
                set.Add((code, cid));
                pos += n;
                if (set.Count > 4096) break;
            }
        }
        return set.ToList();
    }

    private static bool HasUnicode(PdfFont font, int code)
    {
        if (font.ToUnicodeMap != null && font.ToUnicodeMap.TryGetValue(code, out var u)) return u.Length > 0 && u.All(c => c != 0 && c != 0xFFFE && c != 0xFEFF);
        if (!font.IsComposite && font.GetGlyphName(code) is { } g && GlyphList.ToUnicode(g) != null) return true;
        return font.IsComposite && font.CidOrdering != null;
    }

    private static CffFont? CffOf(PreparedFontProgram prepared)
    {
        var tables = Sfnt.ReadTables(prepared.Sfnt, out _);
        return tables != null && tables.TryGetValue("CFF ", out var cff) ? CffFont.TryParse(cff) : null;
    }

    /// <summary>Whether the embedded program has the glyph a code shows.</summary>
    private static bool GlyphPresent(PdfFont font, PreparedFontProgram prepared, TrueTypeFontFile? truetype, CffFont? cff, int code, int cid)
    {
        if (truetype != null)
        {
            int gid = prepared.GetGlyphId(code, cid);
            return gid > 0 && gid < truetype.GlyphCount;
        }
        // Any of the ways a reader finds the glyph will do: the program's own tables, or the mapping the renderer uses.
        if (prepared.GetGlyphId(code, cid) > 0) return true;
        if (cff != null)
        {
            if (font.IsComposite) return (cff.IsCidKeyed ? cff.GidForCid(cid) : cid) is > 0 and var g && g < cff.GlyphCount;
            string? name = font.GetGlyphName(code);
            if (cff.GidForBuiltInCode(code) > 0 && (font.IsSymbolic || name == null)) return true;
            return name != null && cff.GidForName(name) > 0;
        }
        return prepared.GetGlyphId(code, cid) < 0;
    }

    /// <summary>A code of a Type 1 or CFF font's own (built-in) encoding whose glyph name has a Unicode value.</summary>
    private static bool BuiltInUnicode(PreparedFontProgram? prepared, int code)
    {
        if (prepared?.Format != PdfFontProgramFormat.OpenTypeCff) return false;
        var tables = Sfnt.ReadTables(prepared.Sfnt, out _);
        if (tables == null || !tables.TryGetValue("CFF ", out var cff) || CffFont.TryParse(cff) is not { } font) return false;
        int gid = font.GidForBuiltInCode(code);
        return gid > 0 && GlyphList.ToUnicode(font.GlyphName(gid)) != null;
    }

    private void CheckComposite(PdfDictionary type0, PdfDictionary cidFont, string label, string where, bool invisibleOnly)
    {
        var encoding = _r.Resolve(type0["Encoding"]);
        string? cmapName = encoding switch { PdfName n => n.Value, PdfStream s => s.Dictionary.GetName("CMapName"), _ => null };
        var cmapInfo = encoding is PdfStream cs ? _r.Resolve(cs.Dictionary["CIDSystemInfo"]) as PdfDictionary : null;
        var cidInfo = _r.Resolve(cidFont["CIDSystemInfo"]) as PdfDictionary;
        if (encoding is PdfName predefined && !PredefinedCMaps.Contains(predefined.Value))
            Fail("6.3.3.3", "6.2.11.3.3", 1, $"{Cap(label)} uses the CMap /{predefined.Value}, which is neither predefined nor embedded.", where);
        if (cmapName is not ("Identity-H" or "Identity-V") && cmapInfo != null && cidInfo != null)
        {
            string? Str(PdfDictionary d, string k) => _r.Resolve(d[k]) is PdfString s ? s.AsDecodedString() : null;
            if (Str(cmapInfo, "Registry") != Str(cidInfo, "Registry") || Str(cmapInfo, "Ordering") != Str(cidInfo, "Ordering"))
                Fail("6.3.3.1", "6.2.11.3.1", 1, $"{Cap(label)}'s CMap and CIDFont have different character collections.", where);
            else if ((_r.Resolve(cidInfo["Supplement"]) is PdfInteger a ? a.Value : 0) > (_r.Resolve(cmapInfo["Supplement"]) is PdfInteger b ? b.Value : 0))
                Fail("6.3.3.1", "6.2.11.3.1", 1, $"{Cap(label)}'s CIDFont supplement is higher than its CMap's.", where);
        }
        if (encoding is PdfStream cmapStream)
        {
            long? dictWMode = cmapStream.Dictionary.GetInteger("WMode");
            if (Decode(cmapStream) is { } data)
            {
                string text = System.Text.Encoding.Latin1.GetString(data);
                var m = System.Text.RegularExpressions.Regex.Match(text, @"/WMode\s+(\d)");
                if (m.Success && dictWMode != null && m.Groups[1].Value != dictWMode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    Fail("6.3.3.3", "6.2.11.3.3", 2, $"{Cap(label)}'s CMap gives two writing modes.", where);
                var use = System.Text.RegularExpressions.Regex.Match(text, @"/([A-Za-z0-9\-]+)\s+usecmap");
                if (use.Success && !PredefinedCMaps.Contains(use.Groups[1].Value))
                    Fail("6.3.3.3", "6.2.11.3.3", 3, $"{Cap(label)}'s CMap uses /{use.Groups[1].Value}, which is not predefined.", where);
            }
            var useCMap = _r.Resolve(cmapStream.Dictionary["UseCMap"]);
            if (useCMap is PdfName useName && !PredefinedCMaps.Contains(useName.Value))
                Fail("6.3.3.3", "6.2.11.3.3", 3, $"{Cap(label)}'s CMap uses /{useName.Value}, which is not predefined.", where);
            else if (useCMap is PdfStream used && !PredefinedCMaps.Contains(used.Dictionary.GetName("CMapName") ?? string.Empty))
                Fail("6.3.3.3", "6.2.11.3.3", 3, $"{Cap(label)}'s CMap is built on an embedded CMap rather than a predefined one.", where);
        }
        if (!invisibleOnly && cidFont.GetName("Subtype") == "CIDFontType2" && Descriptor(cidFont) is { } desc && (desc.ContainsKey("FontFile2") || desc.ContainsKey("FontFile3")))
        {
            var map = _r.Resolve(cidFont["CIDToGIDMap"]);
            if (!(map is PdfStream || map is PdfName { Value: "Identity" }))
                Fail("6.3.3.2", "6.2.11.3.2", 1, $"{Cap(label)} is an embedded CIDFontType2 without a /CIDToGIDMap.", where);
        }
    }

    /// <summary>
    /// Type 3 glyphs: drawn content is checked through the page (glyph procedures are content), and
    /// the widths the font gives agree with each shown glyph's d0/d1 width (P1 6.3.6, P2 6.2.11.5).
    /// </summary>
    private void CheckType3(PdfDictionary font, string where, List<byte[]>? shown)
    {
        Type3Transparent(font);
        if (shown == null || _r.Resolve(font["CharProcs"]) is not PdfDictionary procs) return;
        var widths = _r.Resolve(font["Widths"]) as PdfArray;
        long first = _r.Resolve(font["FirstChar"]) is PdfInteger fc ? fc.Value : 0;
        var names = new string?[256];
        if (_r.Resolve(font["Encoding"]) is PdfDictionary enc && _r.Resolve(enc["Differences"]) is PdfArray diffs)
        {
            int code = 0;
            foreach (var d in diffs)
            {
                var v = _r.Resolve(d);
                if (v is PdfInteger n) code = (int)n.Value;
                else if (v is PdfName name && code is >= 0 and < 256) names[code++] = name.Value;
            }
        }
        var seen = new HashSet<int>();
        foreach (var s in shown)
            foreach (byte code in s)
            {
                if (!seen.Add(code) || names[code] is not { } glyph || _r.Resolve(procs[glyph]) is not PdfStream proc) continue;
                if (widths == null || code - first < 0 || code - first >= widths.Count) continue;
                double declared = Num(_r.Resolve(widths[(int)(code - first)]));
                if (Decode(proc) is not { } data) continue;
                var m = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.Latin1.GetString(data), @"(-?[\d.]+)\s+(-?[\d.]+)\s+(?:(?:-?[\d.]+\s+){4})?d[01]\b");
                if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double wx)) continue;
                if (Math.Abs(wx - declared) > 1e-3 * Math.Max(1, Math.Abs(wx)))
                {
                    Fail("6.3.6", "6.2.11.5", 1, $"A Type 3 font gives code {code} the width {declared:0.###}, but its glyph procedure says {wx:0.###}.", where);
                    return;
                }
            }
    }

    /// <summary>
    /// A subset's CharSet (Type 1) or CIDSet (CIDFont), when present, lists every glyph the text
    /// uses (P1 6.3.5, where they are required; P2 6.2.11.4.2).
    /// </summary>
    private void CheckSubsetLists(PdfDictionary descriptor, PdfFont font, PreparedFontProgram prepared, string subtype, string label, string where, List<(int Code, int Cid)> codes)
    {
        if (_r.Resolve(descriptor["CharSet"]) is PdfString charSet && subtype is "Type1" or "MMType1")
        {
            var listed = charSet.AsDecodedString().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var cff = CffOf(prepared);
            foreach (var (code, _) in codes)
            {
                string? name = font.GetGlyphName(code);
                if ((font.IsSymbolic || name == null) && cff != null && cff.GidForBuiltInCode(code) is > 0 and var g) name = cff.GlyphName(g);
                if (name == null || name == ".notdef" || listed.Contains(name)) continue;
                Fail("6.3.5", "6.2.11.4.2", _part == 1 ? 2 : 1, $"{Cap(label)}'s /CharSet leaves out the glyph /{name} the text uses.", where);
                break;
            }
        }
        if (!subtype.StartsWith("CIDFontType", StringComparison.Ordinal) || _r.Resolve(descriptor["CIDSet"]) is not PdfStream cidSet || Decode(cidSet) is not { } bits) return;
        foreach (var (_, cid) in codes)
        {
            if (cid <= 0 || (cid / 8 < bits.Length && (bits[cid / 8] & (0x80 >> (cid % 8))) != 0)) continue;
            Fail("6.3.5", "6.2.11.4.2", _part == 1 ? 3 : 2, $"{Cap(label)}'s /CIDSet leaves out CID {cid}, which the text uses.", where);
            break;
        }
    }
}
