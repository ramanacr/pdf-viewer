using System;
using System.Collections.Generic;
using PdfEngine.Geometry;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Resolves font resources into <see cref="PdfFont"/> instances: encodings and /Differences,
/// ToUnicode CMaps, code→CID CMaps, CID widths (/W, /W2), font descriptors, embedded font programs,
/// CIDToGIDMap and Type3 font data.
/// </summary>
/// <remarks>
/// Fonts are cached by the identity of the resolved font dictionary, never by resource name: pages
/// and Form XObjects routinely reuse names such as "F1" for different fonts. Thread-safe.
/// </remarks>
public sealed class PdfFontResolver
{
    /// <summary>Maximum /Differences entries honoured.</summary>
    private const int MaxDifferencesEntries = 4096;

    /// <summary>Maximum CID width entries expanded from /W or /W2.</summary>
    private const int MaxCidWidthEntries = 1_000_000;

    /// <summary>Maximum codes expanded from one cFirst cLast w entry.</summary>
    private const int MaxCidWidthRange = 65536;

    private readonly PdfObjectResolver _resolver;
    private readonly PdfStreamDecoder _decoder;
    private readonly Dictionary<PdfDictionary, PdfFont> _fontCache = new(ReferenceEqualityComparer.Instance);
    private readonly object _cacheLock = new();

    public PdfFontResolver(PdfObjectResolver resolver, PdfSecurityLimits? limits = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _decoder = new PdfStreamDecoder(limits);
    }

    /// <summary>
    /// Resolves the font named <paramref name="fontResourceName"/> in the /Font sub-dictionary of
    /// <paramref name="resources"/>. A missing font yields a Helvetica substitute with
    /// <see cref="PdfFont.IsMissingResource"/> set.
    /// </summary>
    public PdfFont ResolveFont(string fontResourceName, PdfDictionary? resources)
    {
        var fontsDict = resources != null ? _resolver.Resolve(resources["Font"]) as PdfDictionary : null;
        var fontDict = fontsDict != null ? _resolver.Resolve(fontsDict[fontResourceName]) as PdfDictionary : null;
        if (fontDict == null)
            return CreateMissing(fontResourceName);
        return ResolveFont(fontDict, fontResourceName);
    }

    /// <summary>Resolves a font dictionary (cached by dictionary identity).</summary>
    public PdfFont ResolveFont(PdfDictionary fontDict, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(fontDict);
        lock (_cacheLock)
        {
            if (_fontCache.TryGetValue(fontDict, out var cached))
                return cached;
        }

        PdfFont font;
        try
        {
            font = BuildFont(fontDict, resourceName ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            font = CreateMissing(resourceName ?? string.Empty, PdfFallbackReason.UnsupportedFontType);
        }

        lock (_cacheLock)
        {
            if (_fontCache.TryGetValue(fontDict, out var raced))
                return raced;
            _fontCache[fontDict] = font;
        }
        return font;
    }

    private static PdfFont CreateMissing(string name, PdfFallbackReason? reason = null) =>
        new(name, "Helvetica", "Type1")
        {
            IsMissingResource = true,
            UnsupportedReason = reason,
            PostScriptName = "Helvetica",
        };

    private PdfFont BuildFont(PdfDictionary fontObj, string resourceName)
    {
        string subtype = fontObj.GetName("Subtype") ?? "Type1";
        string baseFont = fontObj.GetName("BaseFont") ?? (subtype == "Type3" ? "Type3" : "Helvetica");
        return subtype == "Type0"
            ? BuildCompositeFont(fontObj, resourceName, baseFont)
            : BuildSimpleFont(fontObj, resourceName, baseFont, subtype);
    }

    // ---- Simple fonts -----------------------------------------------------------------------

    private PdfFont BuildSimpleFont(PdfDictionary fontObj, string resourceName, string baseFont, string subtype)
    {
        bool isType3 = subtype == "Type3";
        int firstChar = ClampInt(fontObj.GetInteger("FirstChar") ?? 0, 0, 255);
        int lastChar = ClampInt(fontObj.GetInteger("LastChar") ?? 255, 0, 255);

        List<double>? widths = null;
        if (_resolver.Resolve(fontObj["Widths"]) is PdfArray wArr)
        {
            widths = new List<double>(Math.Min(wArr.Count, 256));
            foreach (var item in wArr)
            {
                if (widths.Count >= 256)
                    break;
                widths.Add(_resolver.Resolve(item) is { } r && r.TryGetNumber(out double w) && double.IsFinite(w) ? w : 0.0);
            }
        }

        var descriptor = _resolver.Resolve(fontObj["FontDescriptor"]) as PdfDictionary;
        var info = ReadDescriptor(descriptor, baseFont);
        var (programKind, programData) = ReadFontProgram(descriptor);

        Dictionary<int, string>? toUnicode = ReadToUnicode(fontObj);
        string?[] glyphNames = BuildSimpleEncoding(fontObj, baseFont, isType3, info.IsSymbolic, programKind);

        PdfMatrix fontMatrix = isType3 ? ReadMatrix(fontObj["FontMatrix"]) : new PdfMatrix(0.001, 0, 0, 0.001, 0, 0);
        PdfRect? bbox = isType3 ? ReadRect(fontObj["FontBBox"]) ?? info.BBox : info.BBox;

        PdfFallbackReason? reason = subtype switch
        {
            "Type1" or "MMType1" or "TrueType" => null,
            "Type3" => PdfFallbackReason.Type3Font,
            _ => PdfFallbackReason.UnsupportedFontType,
        };

        TrueTypeCmap? ttCmap = programKind is PdfFontProgramKind.TrueType or PdfFontProgramKind.OpenType
            ? TrueTypeCmap.TryParse(programData)
            : null;

        return new PdfFont(
            resourceName,
            baseFont,
            subtype,
            firstChar,
            lastChar,
            widths,
            cidWidths: null,
            missingWidth: info.MissingWidth,
            defaultWidth: 1000.0,
            toUnicode,
            programData)
        {
            PostScriptName = PdfFont.StripSubsetPrefix(baseFont),
            GlyphNames = glyphNames,
            EmbeddedProgramKind = programKind,
            TrueTypeCmap = ttCmap,
            FontMatrix = fontMatrix,
            FontBBox = bbox,
            Ascent = info.Ascent,
            Descent = info.Descent,
            Flags = info.Flags,
            IsSymbolic = info.IsSymbolic,
            IsSerif = info.IsSerif,
            IsFixedPitch = info.IsFixedPitch,
            IsItalic = info.IsItalic,
            IsBold = info.IsBold,
            CharProcs = isType3 ? _resolver.Resolve(fontObj["CharProcs"]) as PdfDictionary : null,
            Type3Resources = isType3 ? _resolver.Resolve(fontObj["Resources"]) as PdfDictionary : null,
            UnsupportedReason = reason,
            MetricsFontName = isType3 ? null : Standard14Fonts.NormalizeName(baseFont),
        };
    }

    private string?[] BuildSimpleEncoding(PdfDictionary fontObj, string baseFont, bool isType3, bool isSymbolic, PdfFontProgramKind kind)
    {
        // Built-in encoding default (ISO 32000-2 9.6.5): Symbol/ZapfDingbats have their own; other
        // non-symbolic fonts use StandardEncoding. Symbolic embedded programs and Type3 fonts have
        // no knowable built-in encoding here.
        string?[]? builtIn = Standard14Fonts.NormalizeName(baseFont) switch
        {
            "Symbol" => FontEncodings.Symbol,
            "ZapfDingbats" => FontEncodings.ZapfDingbats,
            _ when isType3 => null,
            _ when isSymbolic => null,
            _ => FontEncodings.Standard,
        };

        string?[]? baseTable = builtIn;
        PdfArray? differences = null;

        var encObj = _resolver.Resolve(fontObj["Encoding"]);
        if (encObj is PdfName encName)
        {
            baseTable = FontEncodings.ForName(encName.Value) ?? builtIn;
        }
        else if (encObj is PdfDictionary encDict)
        {
            if (encDict.GetName("BaseEncoding") is { } baseName)
                baseTable = FontEncodings.ForName(baseName) ?? builtIn;
            differences = _resolver.Resolve(encDict["Differences"]) as PdfArray;
        }

        var names = new string?[256];
        if (baseTable != null)
            Array.Copy(baseTable, names, 256);

        if (differences != null)
        {
            int code = -1;
            int processed = 0;
            foreach (var raw in differences)
            {
                if (++processed > MaxDifferencesEntries)
                    break;
                var item = _resolver.Resolve(raw);
                if (item is PdfName n)
                {
                    if (code >= 0 && code <= 255)
                        names[code] = n.Value;
                    if (code >= 0)
                        code++;
                }
                else if (item != null && item.TryGetNumber(out double num) && double.IsFinite(num))
                {
                    code = num < 0 ? -1 : (int)Math.Min(num, 1000);
                }
            }
        }

        return names;
    }

    // ---- Composite fonts --------------------------------------------------------------------

    private PdfFont BuildCompositeFont(PdfDictionary fontObj, string resourceName, string baseFont)
    {
        PdfFallbackReason? reason = null;

        // Encoding → CMap
        PdfCMap cmap = PdfCMap.IdentityH;
        bool codesAreUnicode = false;
        var encObj = _resolver.Resolve(fontObj["Encoding"]);
        if (encObj is PdfName encName)
        {
            switch (encName.Value)
            {
                case "Identity-H":
                    cmap = PdfCMap.IdentityH;
                    break;
                case "Identity-V":
                    cmap = PdfCMap.IdentityV;
                    break;
                default:
                    // Predefined CJK CMaps are not shipped: flag, keep 2-byte identity as the best effort.
                    reason = PdfFallbackReason.UnsupportedCMap;
                    cmap = encName.Value.EndsWith("-V", StringComparison.Ordinal) ? PdfCMap.IdentityV : PdfCMap.IdentityH;
                    codesAreUnicode = encName.Value.Contains("UCS2", StringComparison.Ordinal) ||
                                      encName.Value.Contains("UTF16", StringComparison.Ordinal);
                    break;
            }
        }
        else if (encObj is PdfStream cmapStream)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var embedded = ReadEmbeddedCMap(cmapStream, 0, visited, out bool unsupported);
            if (embedded != null)
                cmap = embedded;
            if (unsupported || embedded == null)
                reason = PdfFallbackReason.UnsupportedCMap;
        }
        else if (encObj != null)
        {
            reason = PdfFallbackReason.UnsupportedCMap;
        }

        // Descendant CIDFont
        PdfDictionary? cidFont = null;
        if (_resolver.Resolve(fontObj["DescendantFonts"]) is PdfArray descArr && descArr.Count > 0)
            cidFont = _resolver.Resolve(descArr[0]) as PdfDictionary;

        string? cidSubtype = cidFont?.GetName("Subtype");
        if (cidFont == null || cidSubtype is not ("CIDFontType0" or "CIDFontType2"))
            reason ??= PdfFallbackReason.UnsupportedFontType;

        double defaultWidth = 1000.0;
        Dictionary<int, double>? cidWidths = null;
        Dictionary<int, PdfVerticalMetrics>? verticalWidths = null;
        double dw2Vy = 880, dw2W1 = -1000;
        byte[]? cidToGid = null;
        PdfDictionary? descriptor = null;

        if (cidFont != null)
        {
            if (_resolver.Resolve(cidFont["DW"]) is { } dwObj && dwObj.TryGetNumber(out double dw) && double.IsFinite(dw))
                defaultWidth = dw;
            cidWidths = ParseCidWidths(cidFont["W"]);

            if (_resolver.Resolve(cidFont["DW2"]) is PdfArray dw2 && dw2.Count >= 2 &&
                _resolver.Resolve(dw2[0]) is { } a0 && a0.TryGetNumber(out double vy) &&
                _resolver.Resolve(dw2[1]) is { } a1 && a1.TryGetNumber(out double w1))
            {
                dw2Vy = vy;
                dw2W1 = w1;
            }
            verticalWidths = ParseCidVerticalWidths(cidFont["W2"]);

            if (cidSubtype == "CIDFontType2" && _resolver.Resolve(cidFont["CIDToGIDMap"]) is PdfStream gidStream)
                cidToGid = TryDecode(gidStream);

            descriptor = _resolver.Resolve(cidFont["FontDescriptor"]) as PdfDictionary;
        }

        string descendantName = cidFont?.GetName("BaseFont") ?? baseFont;
        var info = ReadDescriptor(descriptor, descendantName);
        var (programKind, programData) = ReadFontProgram(descriptor);
        if (programData == null)
        {
            // Some producers put the descriptor on the Type0 dictionary itself.
            var outerDesc = _resolver.Resolve(fontObj["FontDescriptor"]) as PdfDictionary;
            (programKind, programData) = ReadFontProgram(outerDesc);
        }

        return new PdfFont(
            resourceName,
            baseFont,
            "Type0",
            firstChar: 0,
            lastChar: 0xFFFF,
            widths: null,
            cidWidths,
            missingWidth: info.MissingWidth,
            defaultWidth,
            ReadToUnicode(fontObj),
            programData)
        {
            CidFontSubtype = cidSubtype,
            PostScriptName = StripCMapSuffix(PdfFont.StripSubsetPrefix(descendantName)),
            CMap = cmap,
            CodesAreUnicode = codesAreUnicode,
            VerticalWidths = verticalWidths,
            DefaultVerticalPositionY = dw2Vy,
            DefaultVerticalAdvance = dw2W1,
            CidToGidMap = cidToGid,
            EmbeddedProgramKind = programKind,
            FontBBox = info.BBox,
            Ascent = info.Ascent,
            Descent = info.Descent,
            Flags = info.Flags,
            IsSymbolic = info.IsSymbolic,
            IsSerif = info.IsSerif,
            IsFixedPitch = info.IsFixedPitch,
            IsItalic = info.IsItalic,
            IsBold = info.IsBold,
            UnsupportedReason = reason,
        };
    }

    private PdfCMap? ReadEmbeddedCMap(PdfStream stream, int depth, HashSet<object> visited, out bool unsupported)
    {
        unsupported = false;
        if (depth > PdfCMap.MaxUseCMapDepth || !visited.Add(stream))
        {
            unsupported = true;
            return null;
        }

        byte[]? data = TryDecode(stream);
        if (data == null)
            return null;
        var parsed = CMapParser.Parse(data);

        PdfCMap? baseCMap = null;
        var useObj = _resolver.Resolve(stream.Dictionary["UseCMap"]);
        if (useObj is PdfStream baseStream)
        {
            baseCMap = ReadEmbeddedCMap(baseStream, depth + 1, visited, out bool baseUnsupported);
            unsupported |= baseUnsupported;
        }
        else if (useObj is PdfName useName)
        {
            parsed.UseCMapName ??= useName.Value;
        }

        bool? vertical = stream.Dictionary.GetInteger("WMode") is long wm ? wm == 1 : null;
        if (vertical == null && parsed.WMode == 0 && parsed.CMapName?.EndsWith("-V", StringComparison.Ordinal) == true)
            vertical = true;
        var cmap = PdfCMap.FromParsed(parsed, baseCMap, vertical, out bool unsupportedBase);
        unsupported |= unsupportedBase;
        return cmap;
    }

    private static string StripCMapSuffix(string name)
    {
        foreach (string suffix in new[] { "-Identity-H", "-Identity-V" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                return name[..^suffix.Length];
        }
        return name;
    }

    /// <summary>
    /// Parses /W: <c>c [w1 w2 …]</c> and <c>cFirst cLast w</c> entries. Tolerates real numbers,
    /// indirect items and malformed tails; expansion is capped.
    /// </summary>
    private Dictionary<int, double> ParseCidWidths(PdfObject? wObj)
    {
        var map = new Dictionary<int, double>();
        if (_resolver.Resolve(wObj) is not PdfArray arr)
            return map;

        int i = 0;
        while (i < arr.Count && map.Count < MaxCidWidthEntries)
        {
            var first = _resolver.Resolve(arr[i]);
            if (first == null || !TryGetCid(first, out int c))
            {
                i++;
                continue;
            }
            if (i + 1 >= arr.Count)
                break;

            var next = _resolver.Resolve(arr[i + 1]);
            if (next is PdfArray widthsArr)
            {
                for (int j = 0; j < widthsArr.Count && map.Count < MaxCidWidthEntries; j++)
                {
                    if (_resolver.Resolve(widthsArr[j]) is { } wItem && wItem.TryGetNumber(out double w) && double.IsFinite(w))
                        map[c + j] = w;
                }
                i += 2;
            }
            else if (next != null && TryGetCid(next, out int cLast) && i + 2 < arr.Count &&
                     _resolver.Resolve(arr[i + 2]) is { } wObj2 && wObj2 is not PdfArray &&
                     wObj2.TryGetNumber(out double constWidth) && double.IsFinite(constWidth))
            {
                if (cLast >= c)
                {
                    int last = (int)Math.Min(cLast, (long)c + MaxCidWidthRange - 1);
                    for (int code = c; code <= last && map.Count < MaxCidWidthEntries; code++)
                        map[code] = constWidth;
                }
                i += 3;
            }
            else
            {
                i++;
            }
        }

        return map;
    }

    /// <summary>Parses /W2: <c>c [w1y v1x v1y …]</c> and <c>cFirst cLast w1y v1x v1y</c>.</summary>
    private Dictionary<int, PdfVerticalMetrics>? ParseCidVerticalWidths(PdfObject? w2Obj)
    {
        if (_resolver.Resolve(w2Obj) is not PdfArray arr)
            return null;
        var map = new Dictionary<int, PdfVerticalMetrics>();

        int i = 0;
        while (i < arr.Count && map.Count < MaxCidWidthEntries)
        {
            var first = _resolver.Resolve(arr[i]);
            if (first == null || !TryGetCid(first, out int c) || i + 1 >= arr.Count)
            {
                i++;
                continue;
            }

            var next = _resolver.Resolve(arr[i + 1]);
            if (next is PdfArray vals)
            {
                for (int j = 0; j + 2 < vals.Count && map.Count < MaxCidWidthEntries; j += 3)
                {
                    if (TryNumber(vals[j], out double w1y) && TryNumber(vals[j + 1], out double vx) && TryNumber(vals[j + 2], out double vy))
                        map[c + j / 3] = new PdfVerticalMetrics(w1y, vx, vy);
                }
                i += 2;
            }
            else if (next != null && TryGetCid(next, out int cLast) && i + 4 < arr.Count &&
                     TryNumber(arr[i + 2], out double w1y) && TryNumber(arr[i + 3], out double vx) && TryNumber(arr[i + 4], out double vy))
            {
                if (cLast >= c)
                {
                    int last = (int)Math.Min(cLast, (long)c + MaxCidWidthRange - 1);
                    var vm = new PdfVerticalMetrics(w1y, vx, vy);
                    for (int code = c; code <= last && map.Count < MaxCidWidthEntries; code++)
                        map[code] = vm;
                }
                i += 5;
            }
            else
            {
                i++;
            }
        }
        return map;
    }

    private bool TryNumber(PdfObject? obj, out double value)
    {
        value = 0;
        return _resolver.Resolve(obj) is { } r && r is not PdfArray && r.TryGetNumber(out value) && double.IsFinite(value);
    }

    private static bool TryGetCid(PdfObject obj, out int cid)
    {
        cid = 0;
        if (obj is PdfArray || !obj.TryGetNumber(out double v) || !double.IsFinite(v) || v < 0 || v > int.MaxValue)
            return false;
        cid = (int)Math.Round(v);
        return true;
    }

    // ---- Shared helpers ---------------------------------------------------------------------

    private Dictionary<int, string>? ReadToUnicode(PdfDictionary fontObj)
    {
        if (_resolver.Resolve(fontObj["ToUnicode"]) is not PdfStream stream)
            return null;
        byte[]? data = TryDecode(stream);
        if (data == null)
            return null;
        return CMapParser.Parse(data).Unicode;
    }

    private byte[]? TryDecode(PdfStream stream)
    {
        try
        {
            return _decoder.DecodeStream(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Corrupt or over-limit stream: behave as if absent.
            return null;
        }
    }

    private (PdfFontProgramKind Kind, byte[]? Data) ReadFontProgram(PdfDictionary? descriptor)
    {
        if (descriptor == null)
            return (PdfFontProgramKind.None, null);

        if (_resolver.Resolve(descriptor["FontFile2"]) is PdfStream ff2)
            return TryDecode(ff2) is { Length: > 0 } d2 ? (PdfFontProgramKind.TrueType, d2) : (PdfFontProgramKind.None, null);

        if (_resolver.Resolve(descriptor["FontFile3"]) is PdfStream ff3)
        {
            var kind = ff3.Dictionary.GetName("Subtype") switch
            {
                "Type1C" => PdfFontProgramKind.Type1C,
                "CIDFontType0C" => PdfFontProgramKind.CidFontType0C,
                "OpenType" => PdfFontProgramKind.OpenType,
                _ => PdfFontProgramKind.None,
            };
            if (kind != PdfFontProgramKind.None && TryDecode(ff3) is { Length: > 0 } d3)
                return (kind, d3);
            return (PdfFontProgramKind.None, null);
        }

        if (_resolver.Resolve(descriptor["FontFile"]) is PdfStream ff1)
            return TryDecode(ff1) is { Length: > 0 } d1 ? (PdfFontProgramKind.Type1, d1) : (PdfFontProgramKind.None, null);

        return (PdfFontProgramKind.None, null);
    }

    private readonly record struct DescriptorInfo(
        double Ascent, double Descent, PdfRect? BBox, int Flags, double MissingWidth,
        bool IsSymbolic, bool IsSerif, bool IsFixedPitch, bool IsItalic, bool IsBold);

    private DescriptorInfo ReadDescriptor(PdfDictionary? desc, string baseFont)
    {
        string? std = Standard14Fonts.NormalizeName(baseFont);
        string clean = PdfFont.StripSubsetPrefix(baseFont);

        double ascent = 0.8, descent = -0.2;
        // Adobe Core14 AFM Ascender/Descender for non-embedded standard fonts without a descriptor.
        if (std != null)
        {
            if (std.StartsWith("Helvetica", StringComparison.Ordinal)) { ascent = 0.718; descent = -0.207; }
            else if (std.StartsWith("Times", StringComparison.Ordinal)) { ascent = 0.683; descent = -0.217; }
            else if (std.StartsWith("Courier", StringComparison.Ordinal)) { ascent = 0.629; descent = -0.157; }
        }

        int flags = 0;
        double missingWidth = 0, italicAngle = 0, weight = 0;
        PdfRect? bbox = null;
        if (desc != null)
        {
            if (TryNumber(desc["Ascent"], out double a) && a != 0)
                ascent = a / 1000.0;
            if (TryNumber(desc["Descent"], out double d) && d != 0)
                descent = d / 1000.0;
            if (TryNumber(desc["Flags"], out double f))
                flags = (int)(long)Math.Clamp(f, int.MinValue, int.MaxValue);
            if (TryNumber(desc["MissingWidth"], out double mw))
                missingWidth = mw;
            if (TryNumber(desc["ItalicAngle"], out double ia))
                italicAngle = ia;
            if (TryNumber(desc["FontWeight"], out double fw))
                weight = fw;
            bbox = ReadRect(desc["FontBBox"]);
        }

        bool symbolic = desc != null ? (flags & (1 << 2)) != 0 : std is "Symbol" or "ZapfDingbats";
        bool fixedPitch = (flags & 1) != 0 || std?.StartsWith("Courier", StringComparison.Ordinal) == true;
        bool serif = (flags & (1 << 1)) != 0 || std?.StartsWith("Times", StringComparison.Ordinal) == true;
        bool italic = (flags & (1 << 6)) != 0 || italicAngle != 0 ||
                      clean.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
                      clean.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        bool bold = (flags & (1 << 18)) != 0 || weight >= 600 ||
                    clean.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("Heavy", StringComparison.OrdinalIgnoreCase);

        return new DescriptorInfo(ascent, descent, bbox, flags, missingWidth, symbolic, serif, fixedPitch, italic, bold);
    }

    private PdfMatrix ReadMatrix(PdfObject? obj)
    {
        var fallback = new PdfMatrix(0.001, 0, 0, 0.001, 0, 0);
        if (_resolver.Resolve(obj) is not PdfArray arr || arr.Count < 6)
            return fallback;
        var v = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (!TryNumber(arr[i], out v[i]))
                return fallback;
        }
        var m = new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        return m.IsInvertible ? m : fallback;
    }

    private PdfRect? ReadRect(PdfObject? obj)
    {
        if (_resolver.Resolve(obj) is not PdfArray arr || arr.Count < 4)
            return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!TryNumber(arr[i], out v[i]))
                return null;
        }
        double x0 = Math.Min(v[0], v[2]), x1 = Math.Max(v[0], v[2]);
        double y0 = Math.Min(v[1], v[3]), y1 = Math.Max(v[1], v[3]);
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static int ClampInt(long value, int min, int max) => (int)Math.Clamp(value, min, max);
}
