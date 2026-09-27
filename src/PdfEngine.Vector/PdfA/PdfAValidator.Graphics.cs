using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.PdfA;

internal sealed partial class PdfAChecker
{
    /// <summary>The colour space of the PDF/A output intent's profile ("RGB ", "CMYK", "GRAY"), or null when there is none.</summary>
    private string? _intentSpace;
    /// <summary>The last graphics state set overprinting on with OPM 1.</summary>
    private bool _overprintFill, _overprintStroke, _opm1;

    /// <summary>
    /// P2 6.2.4.2: painting in an ICCBased CMYK space with overprinting on (for that operation) needs
    /// overprint mode 0, unless the profile is the output intent's.
    /// </summary>
    private void CheckOverprint(PdfObject? fill, PdfObject? stroke, string where)
    {
        if (_part < 2 || !_opm1) return;
        foreach (var (space, on) in new[] { (fill, _overprintFill), (stroke, _overprintStroke) })
        {
            if (!on || space is not PdfArray { Count: >= 2 } a || _r.Resolve(a[0]) is not PdfName { Value: "ICCBased" } || _r.Resolve(a[1]) is not PdfStream icc) continue;
            if ((icc.Dictionary.GetInteger("N") ?? 0) != 4) continue;
            var data = Decode(icc);
            if (data != null && _intentProfile != null && data.AsSpan().SequenceEqual(_intentProfile)) continue;
            Fail(null, "6.2.4.2", 2, "An ICCBased CMYK colour space is painted with overprinting in overprint mode 1.", where);
            return;
        }
    }
    private byte[]? _intentProfile;
    private readonly HashSet<PdfObject> _checkedResources = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfObject> _checkedObjects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (PdfObject? Alternate, PdfObject? Tint)> _separations = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ output intents (P1 6.2.2, P2 6.2.3)

    private void CheckOutputIntents()
    {
        if (Catalog == null) return;
        var intents = (_r.Resolve(Catalog["OutputIntents"]) as PdfArray)?.Items.Select(_r.Resolve).OfType<PdfDictionary>().ToList() ?? new List<PdfDictionary>();
        // Also on pages (PDF/A-2): their intents must use the same profile.
        var pdfa = intents.Where(i => i.GetName("S") == "GTS_PDFA1").ToList();
        if (pdfa.Count == 0) return;
        var profiles = pdfa.Select(i => i["DestOutputProfile"]).ToList();
        var first = profiles[0];
        if (profiles.Skip(1).Any(p => !SameReference(p, first)))
            Fail("6.2.2", "6.2.3", 1, "The PDF/A output intents do not share one destination profile.");
        if (_r.Resolve(first) is not PdfStream profile)
        {
            Fail("6.2.2", "6.2.3", 2, "The PDF/A output intent has no destination output profile.");
            return;
        }
        var data = Decode(profile);
        var header = data == null ? null : IccHeader.Read(data);
        if (header == null)
        {
            Fail("6.2.2", "6.2.3", 2, "The output intent's profile is not a valid ICC profile.");
            return;
        }
        if (header.Class is not ("prtr" or "mntr") || header.Space is not ("RGB " or "CMYK" or "GRAY"))
            Fail("6.2.2", "6.2.3", 3, $"The output intent's profile is a {header.Class.Trim()} profile for {header.Space.Trim()}, not a printer or monitor profile for RGB, CMYK or gray.");
        if (header.Major > (_part == 1 ? 2 : 4))
            Fail("6.2.2", "6.2.3", 3, $"The output intent's profile is ICC version {header.Major}, later than this part allows.");
        if (_part == 1 && pdfa[0].ContainsKey("DestOutputProfileRef")) Fail("6.2.2", 1, "The output intent refers to an external profile.");
        if (_part >= 2 && pdfa.Any(i => i.ContainsKey("DestOutputProfileRef"))) Fail(null, "6.2.3", 4, "The output intent refers to an external profile (DestOutputProfileRef).");
        _intentSpace = header.Space;
        _intentProfile = data;
    }

    private bool SameReference(PdfObject? a, PdfObject? b) =>
        a is PdfIndirectRef ra && b is PdfIndirectRef rb ? ra.ObjectNumber == rb.ObjectNumber : ReferenceEquals(_r.Resolve(a), _r.Resolve(b));

    internal sealed record IccHeader(int Major, string Class, string Space, int Channels)
    {
        public static IccHeader? Read(byte[] d)
        {
            if (d.Length < 128 || d[36] != 'a' || d[37] != 'c' || d[38] != 's' || d[39] != 'p') return null;
            string cls = System.Text.Encoding.ASCII.GetString(d, 12, 4), space = System.Text.Encoding.ASCII.GetString(d, 16, 4);
            int channels = space switch { "GRAY" => 1, "RGB " or "Lab " or "XYZ " or "YCbr" or "Luv " or "Yxy " or "HSV " or "HLS " or "CMY " => 3, "CMYK" => 4, _ => space.StartsWith("0", StringComparison.Ordinal) || space.EndsWith("CLR", StringComparison.Ordinal) ? Convert.ToInt32(space[0].ToString(), 16) : 0 };
            return new IccHeader(d[8], cls, space, channels);
        }
    }

    // ------------------------------------------------------------------ pages

    private void CheckPages()
    {
        foreach (var page in _doc.PageTree.Pages)
        {
            string where = $"page {page.PageNumber}";
            var dict = page.Dictionary;
            // The media and crop boxes as inherited from the page tree; the others as the page gives them.
            var boxes = new List<(string Name, double W, double H)>
            {
                ("MediaBox", page.MediaBox.Width, page.MediaBox.Height), ("CropBox", page.CropBox.Width, page.CropBox.Height),
            };
            // The crop box as written (the page tree may give one the media box would otherwise clip).
            if (Inherited(dict, "CropBox") is PdfArray crop && crop.Count >= 4)
                boxes[1] = ("CropBox", Math.Abs(Num(crop[2]) - Num(crop[0])), Math.Abs(Num(crop[3]) - Num(crop[1])));
            foreach (var box in new[] { "BleedBox", "TrimBox", "ArtBox" })
                if (_r.Resolve(dict[box]) is PdfArray a && a.Count >= 4) boxes.Add((box, Math.Abs(Num(a[2]) - Num(a[0])), Math.Abs(Num(a[3]) - Num(a[1]))));
            foreach (var (box, w, h) in boxes)
                if (_part >= 2 && (w < 3 || h < 3 || w > 14400 || h > 14400))
                    Fail(null, "6.1.13", 11, $"The {box} is {w:0.#} x {h:0.#}, outside 3 to 14,400 units.", where);
            if (dict.ContainsKey("AA")) Fail("6.6.2", "6.5.2", 2, "A page has additional actions (/AA).", where);
            if (_part >= 2 && dict.ContainsKey("PresSteps")) Fail(null, "6.10", 1, "A page has presentation steps.", where);
            var group = _r.Resolve(dict["Group"]) as PdfDictionary;
            if (group?.GetName("S") == "Transparency")
            {
                if (_part == 1) Fail("6.4", 3, "A page is a transparency group.", where);
                else CheckGroupColourSpace(group, where);
            }
            var usage = new Usage();
            foreach (var content in page.Contents)
                if (Decode(content) is { } data) CheckContent(data, page.Resources, usage, where, 0);
            if (_part >= 2 && usage.Transparency && _intentSpace == null && group?["CS"] == null)
                Fail(null, "6.2.10", 1, "A page uses transparency but has neither a PDF/A output intent nor a group colour space.", where);
        }
    }

    /// <summary>How a font is used anywhere in the document: shown visibly (with which codes), only invisibly, or not at all.</summary>
    private sealed class FontUsage
    {
        public bool Visible, Invisible;
        /// <summary>The strings shown in it (bounded), decoded into codes by the font's own encoding when checked.</summary>
        public readonly List<byte[]> Shown = new();
        public PdfDictionary? Resources;
        public string? Where;
    }

    private readonly Dictionary<PdfDictionary, FontUsage> _fontUses = new(ReferenceEqualityComparer.Instance);

    private FontUsage FontUse(PdfDictionary font, PdfDictionary? resources)
    {
        if (!_fontUses.TryGetValue(font, out var use)) _fontUses[font] = use = new FontUsage { Resources = resources };
        return use;
    }

    private void CheckFonts()
    {
        foreach (var (font, use) in _fontUses)
            CheckFont(font, use.Where ?? "a font", use.Invisible && !use.Visible, use.Visible ? use.Shown : null, use.Resources);
    }

    private PdfObject? Inherited(PdfDictionary node, string key)
    {
        for (int i = 0; i < 64 && node != null; i++)
        {
            if (_r.Resolve(node[key]) is { } v) return v;
            node = (_r.Resolve(node["Parent"]) as PdfDictionary)!;
        }
        return null;
    }

    private void CheckGroupColourSpace(PdfDictionary group, string where)
    {
        if (group["CS"] is { } cs) InClause("6.2.10", () => CheckColourSpace(cs, where, new Usage(), 0));
    }

    private static double Num(PdfObject? o) => o != null && o.TryGetNumber(out double v) ? v : 0;

    /// <summary>What a content stream (and what it draws) uses, for the rules that depend on it.</summary>
    private sealed class Usage
    {
        public bool Transparency;
        public bool DeviceRgb, DeviceCmyk, DeviceGray;
    }

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI", "EMC", "ET", "EX", "f", "F", "f*",
        "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n", "q", "Q", "re", "RG", "rg", "ri", "s", "S", "SC", "sc", "SCN", "scn",
        "sh", "T*", "Tc", "Td", "TD", "Tf", "TJ", "Tj", "TL", "Tm", "Tr", "Ts", "Tw", "Tz", "v", "w", "W", "W*", "y", "'", "\"",
    };

    private static readonly HashSet<string> Intents = new(StringComparer.Ordinal) { "RelativeColorimetric", "AbsoluteColorimetric", "Perceptual", "Saturation" };

    /// <summary>Content stream rules (P1 6.2.10, P2 6.2.2), limits of q nesting, colour operators, intents, text.</summary>
    private void CheckContent(byte[] content, PdfDictionary? resources, Usage usage, string where, int depth)
    {
        if (depth > 12) return;
        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        var operands = new List<PdfObject>();
        int q = 0, tr = 0;
        string? font = null;
        var trStack = new Stack<int>();
        // Whether a fill or stroke colour was set: until then painting uses the initial DeviceGray black.
        bool fillSet = depth > 0, strokeSet = depth > 0;
        var colourStack = new Stack<(bool, bool)>();
        PdfObject? fillSpace = null, strokeSpace = null;
        while (true)
        {
            long pos = src.Position;
            PdfToken token;
            try { token = lexer.NextToken(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { break; }
            if (token.Type == PdfTokenType.EndOfFile) break;
            if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
            {
                src.Position = pos;
                PdfObject? obj;
                try { obj = token.Type == PdfTokenType.Keyword ? PdfNull.Instance : parser.ParseObject(lexer); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { obj = null; }
                if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                if (obj != null)
                {
                    operands.Add(obj);
                    CheckOperandLimits(obj, where);
                }
                else if (src.Position == pos) src.Position = pos + 1;
                continue;
            }
            string op = token.TextValue;
            // Even between BX and EX: PDF/A allows no operator PDF does not define.
            if (!Operators.Contains(op)) Fail("6.2.10", "6.2.2", 1, $"The content uses the operator \"{Short(op)}\", which PDF does not define.", where);
            if (op is "sc" or "cs" or "scn" or "g" or "rg" or "k") fillSet = true;
            if (op is "g" or "rg" or "k") fillSpace = null;
            if (op is "G" or "RG" or "K") strokeSpace = null;
            if (op is "SC" or "CS" or "SCN" or "G" or "RG" or "K") strokeSet = true;
            switch (op)
            {
                case "q":
                    q++;
                    trStack.Push(tr);
                    colourStack.Push((fillSet, strokeSet));
                    if (q > 28) Fail("6.1.12", "6.1.13", 8, "The content nests q more than 28 deep.", where);
                    break;
                case "Q":
                    if (q > 0) q--;
                    if (trStack.Count > 0) tr = trStack.Pop();
                    if (colourStack.Count > 0) (fillSet, strokeSet) = colourStack.Pop();
                    break;
                // Painting in the initial colour paints in DeviceGray.
                case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s":
                    bool fills = op is not ("S" or "s"), strokes = op is "B" or "B*" or "b" or "b*" or "S" or "s";
                    if ((fills && !fillSet) || (strokes && !strokeSet)) DeviceUsed("DeviceGray", where, resources);
                    CheckOverprint(fills ? fillSpace : null, strokes ? strokeSpace : null, where);
                    break;
                case "rg" or "RG": usage.DeviceRgb = true; DeviceUsed("DeviceRGB", where, resources); break;
                case "k" or "K": usage.DeviceCmyk = true; DeviceUsed("DeviceCMYK", where, resources); break;
                case "g" or "G": usage.DeviceGray = true; DeviceUsed("DeviceGray", where, resources); break;
                case "cs" or "CS" when operands.Count > 0 && operands[^1] is PdfName csName:
                    ColourSpaceByName(csName.Value, resources, where, usage);
                    var space = _r.Resolve((_r.Resolve(resources?["ColorSpace"]) as PdfDictionary)?[csName.Value]);
                    if (op == "CS") strokeSpace = space; else fillSpace = space;
                    break;
                case "scn" or "SCN" when operands.Count > 0 && operands[^1] is PdfName patternName:
                    CheckPattern(_r.Resolve((_r.Resolve(resources?["Pattern"]) as PdfDictionary)?[patternName.Value]), where, usage, depth);
                    break;
                case "ri" when operands.Count > 0 && operands[^1] is PdfName intent && !Intents.Contains(intent.Value):
                    Fail("6.2.9", "6.2.6", 1, $"The rendering intent /{intent.Value} is not a standard one.", where);
                    break;
                case "Tf" when operands.Count >= 2 && operands[0] is PdfName f:
                    font = f.Value;
                    break;
                case "Tr" when operands.Count > 0 && operands[^1].TryGetNumber(out double mode):
                    tr = (int)mode;
                    break;
                case "Tj" or "'" or "\"" or "TJ" when font != null:
                    if (_r.Resolve((_r.Resolve(resources?["Font"]) as PdfDictionary)?[font]) is PdfDictionary fontDict)
                    {
                        var use = FontUse(fontDict, resources);
                        use.Where ??= where;
                        if (tr is 3 or 7) use.Invisible = true;
                        else
                        {
                            use.Visible = true;
                            CollectStrings(operands, use.Shown);
                            if ((tr is 0 or 2 or 4 or 6 && !fillSet) || (tr is 1 or 2 or 5 or 6 && !strokeSet)) DeviceUsed("DeviceGray", where, resources);
                        }
                        if (fontDict.GetName("Subtype") == "Type3" && Type3Transparent(fontDict, resources)) usage.Transparency = true;
                    }
                    break;
                case "gs" when operands.Count > 0 && operands[^1] is PdfName gsName:
                    if (_r.Resolve((_r.Resolve(resources?["ExtGState"]) as PdfDictionary)?[gsName.Value]) is PdfDictionary gsDict)
                        CheckExtGState(gsDict, where, usage);
                    break;
                case "Do" when operands.Count > 0 && operands[^1] is PdfName xname:
                    if (_r.Resolve((_r.Resolve(resources?["XObject"]) as PdfDictionary)?[xname.Value]) is PdfStream xobject)
                        CheckXObject(xobject, where, usage, depth, resources);
                    break;
                case "sh" when operands.Count > 0 && operands[^1] is PdfName shName:
                    if (_r.Resolve((_r.Resolve(resources?["Shading"]) as PdfDictionary)?[shName.Value]) is { } shading)
                        CheckShading(shading, where, usage, depth);
                    break;
                case "BI":
                    CheckInlineImage(src, lexer, parser, where, usage, resources);
                    break;
            }
            operands.Clear();
        }
    }

    private static string Short(string s) => s.Length > 20 ? s[..20] + "..." : s;

    /// <summary>Numbers and strings in content streams have the same limits as in the file (P1 6.1.12, P2 6.1.13).</summary>
    private void CheckOperandLimits(PdfObject obj, string where)
    {
        switch (obj)
        {
            case PdfInteger i when i.Value < int.MinValue || i.Value > int.MaxValue:
                Fail("6.1.12", "6.1.13", 1, $"The content has the integer {i.Value}, outside the allowed range.", where);
                break;
            case PdfReal r when Math.Abs(r.Value) > (_part == 1 ? 32767 : 3.403e38):
                Fail("6.1.12", "6.1.13", 2, $"The content has the number {r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, outside the allowed range.", where);
                break;
            case PdfString s when s.RawBytes.Length > (_part == 1 ? 65535 : 32767):
                Fail("6.1.12", "6.1.13", 3, $"The content has a string of {s.RawBytes.Length} bytes.", where);
                break;
            case PdfArray a:
                foreach (var item in a) CheckOperandLimits(item, where);
                break;
        }
    }

    private readonly Dictionary<PdfDictionary, bool> _type3Transparency = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a Type 3 font's glyph procedures draw with transparency.</summary>
    private bool Type3Transparent(PdfDictionary font, PdfDictionary? callerResources = null)
    {
        if (_type3Transparency.TryGetValue(font, out bool known)) return known;
        _type3Transparency[font] = false;
        var usage = new Usage();
        var res = _r.Resolve(font["Resources"]) as PdfDictionary ?? callerResources;
        if (_r.Resolve(font["CharProcs"]) is PdfDictionary procs)
            foreach (var (_, value) in procs.Entries)
                if (_r.Resolve(value) is PdfStream proc && Decode(proc) is { } data) CheckContent(data, res, usage, "a Type 3 glyph", 1);
        return _type3Transparency[font] = usage.Transparency;
    }

    private static void CollectStrings(List<PdfObject> operands, List<byte[]> shown)
    {
        foreach (var o in operands)
        {
            if (shown.Count > 2000) return;
            if (o is PdfString s) shown.Add(s.RawBytes.ToArray());
            else if (o is PdfArray a) foreach (var i in a) if (i is PdfString si) shown.Add(si.RawBytes.ToArray());
        }
    }

    /// <summary>Device colour spaces need a matching output intent (or a default colour space) (P1 6.2.3.3, P2 6.2.4.3).</summary>
    /// <summary>The PDF/A-2 clause a device colour space found inside another colour space belongs to (Separation, Indexed, a blending space).</summary>
    private string? _colourClause;

    private void InClause(string clause, Action action)
    {
        var saved = _colourClause;
        _colourClause ??= clause;
        try { action(); }
        finally { _colourClause = saved; }
    }

    private void DeviceUsed(string space, string where, PdfDictionary? resources = null)
    {
        bool hasDefault = resources != null && _r.Resolve(resources["ColorSpace"]) is PdfDictionary csd && csd.ContainsKey(space switch
        {
            "DeviceRGB" => "DefaultRGB", "DeviceCMYK" => "DefaultCMYK", _ => "DefaultGray",
        });
        if (hasDefault) return;
        string c2 = _colourClause ?? "6.2.4.3";
        switch (space)
        {
            case "DeviceRGB" when _intentSpace != "RGB ":
                Fail("6.2.3.3", c2, 2, "DeviceRGB is used without an RGB output intent.", where);
                break;
            case "DeviceCMYK" when _intentSpace != "CMYK":
                Fail("6.2.3.3", c2, 3, "DeviceCMYK is used without a CMYK output intent.", where);
                break;
            case "DeviceGray" when _intentSpace == null:
                Fail("6.2.3.3", c2, 4, "DeviceGray is used without an output intent.", where);
                break;
        }
    }

    private void ColourSpaceByName(string name, PdfDictionary? resources, string where, Usage usage)
    {
        switch (name)
        {
            case "DeviceRGB" or "RGB": DeviceUsed("DeviceRGB", where, resources); return;
            case "DeviceCMYK" or "CMYK": DeviceUsed("DeviceCMYK", where, resources); return;
            case "DeviceGray" or "G": DeviceUsed("DeviceGray", where, resources); return;
            case "Pattern": return;
        }
        if (_r.Resolve((_r.Resolve(resources?["ColorSpace"]) as PdfDictionary)?[name]) is { } cs) CheckColourSpace(cs, where, usage, 0, resources);
    }

    /// <summary>A colour space and what it is built on (P1 6.2.3, P2 6.2.4).</summary>
    private void CheckColourSpace(PdfObject cs, string where, Usage usage, int depth, PdfDictionary? resources = null)
    {
        if (depth > 8) return;
        var resolved = _r.Resolve(cs);
        if (resolved is PdfName n)
        {
            ColourSpaceByName(n.Value, resources, where, usage);
            return;
        }
        if (resolved is not PdfArray a || a.Count == 0 || _r.Resolve(a[0]) is not PdfName family) return;
        switch (family.Value)
        {
            case "ICCBased":
                if (a.Count < 2 || _r.Resolve(a[1]) is not PdfStream icc) { Fail("6.2.3.2", "6.2.4.2", 1, "An ICCBased colour space has no profile.", where); return; }
                if (!_checkedObjects.Add(icc)) return;
                var data = Decode(icc);
                var header = data == null ? null : IccHeader.Read(data);
                if (header == null) { Fail("6.2.3.2", "6.2.4.2", 1, "An ICCBased colour space's profile is not a valid ICC profile.", where); return; }
                long nValue = icc.Dictionary.GetInteger("N") ?? 0;

                if (nValue != header.Channels && header.Channels > 0)
                    Fail("6.2.3.2", "6.2.4.2", 1, $"An ICCBased colour space says /N {nValue}, but its profile has {header.Channels} components.", where);
                if (header.Major > (_part == 1 ? 2 : 4))
                    Fail("6.2.3.2", "6.2.4.2", 2, $"An ICCBased profile is ICC version {header.Major}, later than this part allows.", where);
                break;
            case "Indexed" or "I":
                if (a.Count > 1) InClause("6.2.4.5", () => CheckColourSpace(a[1], where, usage, depth + 1, resources));
                break;
            case "Pattern":
                if (a.Count > 1) CheckColourSpace(a[1], where, usage, depth + 1, resources);
                break;
            case "Separation":
                if (a.Count >= 4 && _r.Resolve(a[1]) is PdfName colorant)
                {
                    InClause("6.2.4.4", () => CheckColourSpace(a[2], where, usage, depth + 1, resources));
                    if (_part >= 2 && colorant.Value is not ("All" or "None"))
                    {
                        if (_separations.TryGetValue(colorant.Value, out var known))
                        {
                            if (!SameValue(known.Alternate, a[2]) || !SameValue(known.Tint, a[3]))
                                Fail(null, "6.2.4.4", 2, $"Two Separation colour spaces for /{colorant.Value} have different alternates or tint transforms.", where);
                        }
                        else _separations[colorant.Value] = (a[2], a[3]);
                    }
                }
                break;
            case "DeviceN":
                if (a.Count >= 4)
                {
                    int components = (_r.Resolve(a[1]) as PdfArray)?.Count ?? 0;
                    if (components > (_part == 1 ? 8 : 32)) Fail("6.1.12", "6.1.13", 9, $"A DeviceN colour space has {components} colorants.", where);
                    InClause("6.2.4.4", () => CheckColourSpace(a[2], where, usage, depth + 1, resources));
                    var attrs = a.Count >= 5 ? _r.Resolve(a[4]) as PdfDictionary : null;
                    var colorants = attrs == null ? null : _r.Resolve(attrs["Colorants"]) as PdfDictionary;
                    if (_part >= 2 && colorants != null)
                        foreach (var (_, value) in colorants.Entries) CheckColourSpace(value, where, usage, depth + 1, resources);
                    // PDF/A-2 6.2.4.4: every spot colorant of a DeviceN has its Separation in the Colorants dictionary.
                    if (_part >= 2 && _r.Resolve(a[1]) is PdfArray names)
                    {
                        var process = (_r.Resolve((_r.Resolve(attrs?["Process"]) as PdfDictionary)?["Components"]) as PdfArray)?.Items.Select(_r.Resolve).OfType<PdfName>().Select(p => p.Value).ToHashSet()
                                      ?? new HashSet<string>();
                        foreach (var spot in names.Items.Select(_r.Resolve).OfType<PdfName>())
                        {
                            if (spot.Value is "None" or "Cyan" or "Magenta" or "Yellow" or "Black" || process.Contains(spot.Value)) continue;
                            if (colorants == null || !colorants.ContainsKey(spot.Value))
                            {
                                Fail(null, "6.2.4.4", 3, $"The DeviceN colorant /{spot.Value} is not in its Colorants dictionary.", where);
                                break;
                            }
                        }
                    }
                }
                break;
        }
    }

    /// <summary>Two values are the same when they are the same object or have the same content (references followed).</summary>
    private bool SameValue(PdfObject? a, PdfObject? b, int depth = 0)
    {
        if (depth > 16) return false;
        if (a is PdfIndirectRef ra && b is PdfIndirectRef rb && ra.ObjectNumber == rb.ObjectNumber) return true;
        var x = _r.Resolve(a);
        var y = _r.Resolve(b);
        if (ReferenceEquals(x, y)) return true;
        switch (x, y)
        {
            case (PdfStream sx, PdfStream sy):
                return SameValue(sx.Dictionary, sy.Dictionary, depth + 1) && Decode(sx) is { } dx && Decode(sy) is { } dy && dx.AsSpan().SequenceEqual(dy);
            case (PdfArray ax, PdfArray ay):
                if (ax.Count != ay.Count) return false;
                for (int i = 0; i < ax.Count; i++) if (!SameValue(ax[i], ay[i], depth + 1)) return false;
                return true;
            case (PdfDictionary dx2, PdfDictionary dy2):
                var keys = dx2.Entries.Keys.Where(k => k != "Length").ToHashSet();
                if (!keys.SetEquals(dy2.Entries.Keys.Where(k => k != "Length"))) return false;
                return keys.All(k => SameValue(dx2[k], dy2[k], depth + 1));
            case (null, null):
                return true;
            default:
                if (x != null && y != null && x.TryGetNumber(out double nx) && y.TryGetNumber(out double ny)) return Math.Abs(nx - ny) < 1e-9;
                return Equals(x, y);
        }
    }

    private void CheckPattern(PdfObject? pattern, string where, Usage usage, int depth)
    {
        if (pattern == null || !_checkedObjects.Add(pattern)) return;
        if (pattern is PdfStream tiling)
        {
            var res = _r.Resolve(tiling.Dictionary["Resources"]) as PdfDictionary;
            if (Decode(tiling) is { } data) CheckContent(data, res, usage, where + ", pattern", depth + 1);
        }
        else if (pattern is PdfDictionary shadingPattern)
        {
            if (_r.Resolve(shadingPattern["Shading"]) is { } sh) CheckShading(sh, where, usage, depth);
            if (_r.Resolve(shadingPattern["ExtGState"]) is PdfDictionary gs) CheckExtGState(gs, where, usage);
        }
    }

    private void CheckShading(PdfObject shading, string where, Usage usage, int depth)
    {
        var dict = shading is PdfStream s ? s.Dictionary : shading as PdfDictionary;
        if (dict == null || !_checkedObjects.Add(dict)) return;
        if (dict["ColorSpace"] is { } cs) CheckColourSpace(cs, where, usage, 0);
    }

    /// <summary>Graphics state parameters (P1 6.2.8 and 6.4, P2 6.2.5 and 6.2.10).</summary>
    private void CheckExtGState(PdfDictionary gs, string where, Usage usage)
    {
        bool first = _checkedObjects.Add(gs);
        bool overprint = _r.Resolve(gs["OP"]) is PdfBoolean { Value: true } || _r.Resolve(gs["op"]) is PdfBoolean { Value: true };
        _ = overprint;
        // Overprinting for stroking (OP) and for filling (op, which defaults to OP), in overprint mode 1.
        if (gs.ContainsKey("OP")) _overprintStroke = _r.Resolve(gs["OP"]) is PdfBoolean { Value: true };
        if (gs.ContainsKey("op")) _overprintFill = _r.Resolve(gs["op"]) is PdfBoolean { Value: true };
        else if (gs.ContainsKey("OP")) _overprintFill = _overprintStroke;
        if (gs.ContainsKey("OPM")) _opm1 = Num(_r.Resolve(gs["OPM"])) == 1;
        var smask = _r.Resolve(gs["SMask"]);
        bool softMask = smask is PdfDictionary || (smask is PdfName sm && sm.Value != "None");
        // Compared as readers store them (single precision): 0.9999999 is 1.
        double ca = gs["ca"] is { } fa ? (float)Num(fa) : 1, CA = gs["CA"] is { } sa ? (float)Num(sa) : 1;
        string? bm = _r.Resolve(gs["BM"]) switch { PdfName n => n.Value, PdfArray arr when arr.Count > 0 && _r.Resolve(arr[0]) is PdfName n0 => n0.Value, _ => null };
        if (softMask || ca < 1 - 1e-6 || CA < 1 - 1e-6 || (bm != null && bm is not ("Normal" or "Compatible"))) usage.Transparency = true;
        if (!first) return;
        if (_part == 1)
        {
            if (softMask) Fail("6.4", 1, "A graphics state uses a soft mask.", where);
            if (ca < 1 - 1e-6 || CA < 1 - 1e-6) Fail("6.4", 2, "A graphics state sets a constant alpha below 1.", where);
            if (bm != null && bm is not ("Normal" or "Compatible")) Fail("6.4", 4, $"A graphics state uses the blend mode /{bm}.", where);
        }
        else if (bm != null && bm is not ("Normal" or "Compatible" or "Multiply" or "Screen" or "Overlay" or "Darken" or "Lighten" or "ColorDodge" or "ColorBurn"
                     or "HardLight" or "SoftLight" or "Difference" or "Exclusion" or "Hue" or "Saturation" or "Color" or "Luminosity"))
            Fail(null, "6.2.10", 2, $"A graphics state uses the non-standard blend mode /{bm}.", where);
        if (gs.ContainsKey("TR")) Fail("6.2.8", "6.2.5", 1, "A graphics state has a transfer function (/TR).", where);
        if (_r.Resolve(gs["TR2"]) is { } tr2 && !(tr2 is PdfName { Value: "Default" })) Fail("6.2.8", "6.2.5", 2, "A graphics state has a transfer function (/TR2) other than /Default.", where);
        if (_part >= 2 && gs.ContainsKey("HTP")) Fail(null, "6.2.5", 3, "A graphics state has a halftone phase (/HTP).", where);
        if (_r.Resolve(gs["HT"]) is { } ht && ht is not PdfName)
        {
            var htDict = ht is PdfStream hs ? hs.Dictionary : ht as PdfDictionary;
            long type = htDict?.GetInteger("HalftoneType") ?? 0;
            if (_part >= 2 && type is not (1 or 5)) Fail(null, "6.2.5", 4, $"A graphics state's halftone is of type {type}, not 1 or 5.", where);
            if (_part >= 2 && htDict?.ContainsKey("HalftoneName") == true) Fail(null, "6.2.5", 5, "A halftone has a /HalftoneName.", where);
            if (_part == 1 && htDict?.ContainsKey("TransferFunction") == true) Fail("6.2.8", 3, "A halftone has a transfer function.", where);
            // PDF/A-2: in a type 5 halftone, colorants other than the primaries have a transfer function, primaries none.
            if (_part >= 2 && type == 5 && htDict != null)
                foreach (var (colorant, value) in htDict.Entries)
                {
                    if (colorant is "Type" or "HalftoneType" or "HalftoneName") continue;
                    var component = _r.Resolve(value);
                    var cd = component is PdfStream cs ? cs.Dictionary : component as PdfDictionary;
                    if (cd == null) continue;
                    bool primary = colorant is "Cyan" or "Magenta" or "Yellow" or "Black" or "Red" or "Green" or "Blue" or "Gray" or "Default";
                    if (primary && cd.ContainsKey("TransferFunction")) Fail(null, "6.2.5", 6, $"The halftone for the primary colorant /{colorant} has a transfer function.", where);
                    else if (!primary && !cd.ContainsKey("TransferFunction")) Fail(null, "6.2.5", 7, $"The halftone for the colorant /{colorant} has no transfer function.", where);
                }
        }
        if (_r.Resolve(gs["RI"]) is PdfName ri && !Intents.Contains(ri.Value)) Fail("6.2.9", "6.2.6", 1, $"The rendering intent /{ri.Value} is not a standard one.", where);
        if (smask is PdfDictionary maskDict && _r.Resolve(maskDict["G"]) is PdfStream group && _part >= 2)
            CheckXObject(group, where, usage, 1);
    }

    /// <summary>Images, forms, reference and PostScript XObjects (P1 6.2.4–6.2.7, P2 6.2.8, 6.2.9).</summary>
    private void CheckXObject(PdfStream x, string where, Usage usage, int depth, PdfDictionary? resources = null)
    {
        var d = x.Dictionary;
        string? subtype = d.GetName("Subtype");
        if (subtype == "Image")
        {
            if (d.ContainsKey("SMask") && _r.Resolve(d["SMask"]) is PdfStream) usage.Transparency = true;
            if (!_checkedObjects.Add(x)) return;
            var imageFilters = Filters(d).ToList();
            CheckImage(d, where, usage, imageFilters, resources);
            if (_part >= 2 && imageFilters.Contains("JPXDecode")) CheckJpxData(x, d, where);
            return;
        }
        if (subtype == "PS" || d.GetName("Subtype2") == "PS" || d.ContainsKey("PS"))
        {
            Fail("6.2.7", "6.2.9.3", 1, "A PostScript XObject is used.", where);
            return;
        }
        if (subtype != "Form") return;
        if (d.ContainsKey("Ref")) Fail("6.2.6", "6.2.9.2", 1, "A reference XObject is used.", where);
        if (d.ContainsKey("OPI")) Fail("6.2.5", "6.2.9.1", 1, "A form XObject has OPI information.", where);
        var group = _r.Resolve(d["Group"]) as PdfDictionary;
        if (group?.GetName("S") == "Transparency")
        {
            usage.Transparency = true;
            if (_part == 1 && _checkedObjects.Add(group)) Fail("6.4", 3, "A form XObject is a transparency group.", where);
            if (_part >= 2) CheckGroupColourSpace(group, where);
        }
        if (depth > 12 || !_checkedObjects.Add(x)) return;
        // A form without resources uses those of the content that draws it (ISO 32000-1 7.8.3).
        var res = _r.Resolve(d["Resources"]) as PdfDictionary ?? resources;
        if (Decode(x) is { } data) CheckContent(data, res, usage, where, depth + 1);
    }

    private void CheckImage(PdfDictionary d, string where, Usage usage, List<string> filters, PdfDictionary? resources)
    {
        if (d.ContainsKey("Alternates")) Fail("6.2.4", "6.2.8.1", 1, "An image has alternates.", where);
        if (d.ContainsKey("OPI")) Fail("6.2.4", "6.2.8.1", 2, "An image has OPI information.", where);
        if (_r.Resolve(d["Interpolate"]) is PdfBoolean { Value: true }) Fail("6.2.4", "6.2.8.1", 3, "An image asks to be interpolated.", where);
        if (_r.Resolve(d["Intent"]) is PdfName intent && !Intents.Contains(intent.Value)) Fail("6.2.9", "6.2.6", 1, $"An image's rendering intent /{intent.Value} is not a standard one.", where);
        if (_part == 1 && d.ContainsKey("SMask") && _r.Resolve(d["SMask"]) is PdfStream) Fail("6.4", 1, "An image has a soft mask.", where);
        bool jpx = filters.Contains("JPXDecode");
        if (jpx && _part == 1) Fail("6.1.10", 2, "An image uses JPEG 2000 (JPXDecode), which PDF/A-1 does not allow.", where);
        long bpc = d.GetInteger("BitsPerComponent") ?? (jpx ? 8 : 0);
        bool mask = _r.Resolve(d["ImageMask"]) is PdfBoolean { Value: true };
        if (!mask && !jpx && bpc is not (1 or 2 or 4 or 8 or 16)) Fail("6.2.4", "6.2.8.1", 4, $"An image has {bpc} bits per component.", where);
        if (d["ColorSpace"] is { } cs) CheckColourSpace(cs, where, usage, 0, resources);
        else if (jpx && _part >= 2) CheckJpx(d, where);
    }

    /// <summary>JPEG 2000 images (P2 6.2.8.3): components, bit depth and colour specifications of the codestream.</summary>
    private void CheckJpx(PdfDictionary d, string where) => _ = (d, where);

    /// <summary>
    /// JPEG 2000 data (P2 6.2.8.3): 1, 3 or 4 components of 1 to 38 bits; when the image dictionary
    /// has no /ColorSpace, the colour specification comes from exactly one best 'colr' box, using
    /// method 1, 2 or 3 and not the CIEJab enumerated space.
    /// </summary>
    private void CheckJpxData(PdfStream image, PdfDictionary d, string where)
    {
        if (image.CachedRawBytes is { } cached ? cached.Length == 0 : image.StreamOffset <= 0) return;
        ReadOnlySpan<byte> data = image.CachedRawBytes is { } c ? c.Span
            : image.StreamOffset + image.StreamLength <= _file.Length ? _file.AsSpan((int)image.StreamOffset, (int)image.StreamLength) : ReadOnlySpan<byte>.Empty;
        if (data.Length < 12 || !(data[4] == 'j' && data[5] == 'P')) return; // a bare codestream carries no boxes
        int nc = -1, bpc = -1;
        var colr = new List<(int Meth, int Approx, uint Enum)>();
        int bpccMax = -1;
        void Walk(ReadOnlySpan<byte> span, int depth)
        {
            int p = 0;
            while (p + 8 <= span.Length && depth < 4)
            {
                long len = (long)((uint)(span[p] << 24 | span[p + 1] << 16 | span[p + 2] << 8 | span[p + 3]));
                string type = System.Text.Encoding.ASCII.GetString(span.Slice(p + 4, 4));
                int header = 8;
                if (len == 1 && p + 16 <= span.Length) { len = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(span.Slice(p + 8, 8)); header = 16; }
                else if (len == 0) len = span.Length - p;
                if (len < header || p + len > span.Length) return;
                var body = span.Slice(p + header, (int)len - header);
                switch (type)
                {
                    case "jp2h": Walk(body, depth + 1); break;
                    case "ihdr" when body.Length >= 11:
                        nc = body[8] << 8 | body[9];
                        bpc = body[10];
                        break;
                    case "bpcc":
                        foreach (byte b in body) bpccMax = Math.Max(bpccMax, (b & 0x7F) + 1);
                        break;
                    case "colr" when body.Length >= 3:
                        uint en = body.Length >= 7 ? (uint)(body[3] << 24 | body[4] << 16 | body[5] << 8 | body[6]) : 0;
                        colr.Add((body[0], body[2], en));
                        break;
                }
                p += (int)len;
            }
        }
        Walk(data, 0);
        if (nc >= 0 && nc is not (1 or 3 or 4)) Fail(null, "6.2.8.3", 1, $"A JPEG 2000 image has {nc} colour channels, not 1, 3 or 4.", where);
        int depth = bpc == 255 ? bpccMax : bpc >= 0 ? (bpc & 0x7F) + 1 : -1;
        if (depth > 38) Fail(null, "6.2.8.3", 5, $"A JPEG 2000 image has a bit depth of {depth}.", where);
        if (d.ContainsKey("ColorSpace")) return;
        if (colr.Count > 1 && colr.Count(x => x.Approx == 1) != 1)
            Fail(null, "6.2.8.3", 2, "A JPEG 2000 image without /ColorSpace has several colour specifications, but not exactly one marked as the best.", where);
        var chosen = colr.Count == 1 ? colr[0] : colr.FirstOrDefault(x => x.Approx == 1);
        if (colr.Count > 0 && chosen.Meth is not (1 or 2 or 3)) Fail(null, "6.2.8.3", 3, $"A JPEG 2000 image's colour specification uses method {chosen.Meth}.", where);
        else if (chosen.Meth == 1 && chosen.Enum == 19) Fail(null, "6.2.8.3", 4, "A JPEG 2000 image uses the CIEJab enumerated colour space.", where);
    }

    private void CheckInlineImage(MemoryByteSource src, PdfLexer lexer, PdfParser parser, string where, Usage usage, PdfDictionary? resources)
    {
        var entries = new Dictionary<string, PdfObject>();
        while (true)
        {
            PdfToken tok;
            try { tok = lexer.NextToken(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
            if (tok.Type == PdfTokenType.EndOfFile) return;
            if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID") break;
            if (tok.Type != PdfTokenType.Name) continue;
            PdfObject? value;
            try { value = parser.ParseObject(lexer); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { value = null; }
            if (value != null) entries[tok.TextValue] = value;
        }
        // Skip the data up to EI.
        var all = src.ReadMemory(0, (int)src.Length).Span;
        long start = src.Position + 1;
        for (long i = start; i + 1 < all.Length; i++)
        {
            if (all[(int)i] == 'E' && all[(int)i + 1] == 'I' && PdfLexer.IsWhitespace(all[(int)i - 1]) &&
                (i + 2 >= all.Length || PdfLexer.IsWhitespace(all[(int)i + 2]) || PdfLexer.IsDelimiter(all[(int)i + 2])))
            {
                src.Position = i + 2;
                break;
            }
        }
        var filters = new List<string>();
        var f = entries.TryGetValue("F", out var fv) ? fv : entries.TryGetValue("Filter", out var fv2) ? fv2 : null;
        if (f is PdfName fn) filters.Add(fn.Value);
        else if (f is PdfArray fa) filters.AddRange(fa.OfType<PdfName>().Select(n => n.Value));
        foreach (var name in filters)
        {
            if (name is "LZW" or "LZWDecode") Fail("6.1.10", "6.1.10", 1, "An inline image uses the LZW filter.", where);
            else if (name == "Crypt") Fail(null, "6.1.10", 2, "An inline image uses a Crypt filter.", where);
            else if (name is not ("AHx" or "A85" or "Fl" or "RL" or "CCF" or "DCT" or "ASCIIHexDecode" or "ASCII85Decode" or "FlateDecode" or "RunLengthDecode" or "CCITTFaxDecode" or "DCTDecode"))
                Fail(null, "6.1.10", 3, $"An inline image uses the filter /{name}.", where);
        }
        if ((entries.TryGetValue("I", out var interp) || entries.TryGetValue("Interpolate", out interp)) && interp is PdfBoolean { Value: true })
            Fail("6.2.4", "6.2.8.1", 3, "An inline image asks to be interpolated.", where);
        if ((entries.TryGetValue("Intent", out var intent)) && intent is PdfName iname && !Intents.Contains(iname.Value))
            Fail("6.2.9", "6.2.6", 1, $"An inline image's rendering intent /{iname.Value} is not a standard one.", where);
        if (entries.TryGetValue("CS", out var cs) || entries.TryGetValue("ColorSpace", out cs))
        {
            if (cs is PdfName n) ColourSpaceByName(n.Value switch { "RGB" => "DeviceRGB", "G" => "DeviceGray", "CMYK" => "DeviceCMYK", "I" => "Indexed", _ => n.Value }, resources, where, usage);
            else CheckColourSpace(cs, where, usage, 0, resources);
        }
    }
}
