using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Resolves font resources from page dictionaries, resolving embedded font descriptors,
/// Type0 composite fonts, CID width tables, and ToUnicode CMaps.
/// </summary>
public sealed class PdfFontResolver
{
    private readonly PdfObjectResolver _resolver;
    private readonly Dictionary<string, PdfFont> _fontCache = new();

    public PdfFontResolver(PdfObjectResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public PdfFont ResolveFont(string fontResourceName, PdfDictionary? pageResources)
    {
        if (_fontCache.TryGetValue(fontResourceName, out var cached))
            return cached;

        if (pageResources == null || !pageResources.TryGetValue("Font", out var fontDictObj))
        {
            var fallback = new PdfFont(fontResourceName, "Helvetica", "Type1");
            _fontCache[fontResourceName] = fallback;
            return fallback;
        }

        var fontsDict = _resolver.Resolve(fontDictObj) as PdfDictionary;
        if (fontsDict == null || !fontsDict.TryGetValue(fontResourceName, out var fontRef))
        {
            var fallback = new PdfFont(fontResourceName, "Helvetica", "Type1");
            _fontCache[fontResourceName] = fallback;
            return fallback;
        }

        var fontObj = _resolver.Resolve(fontRef) as PdfDictionary;
        if (fontObj == null)
        {
            var fallback = new PdfFont(fontResourceName, "Helvetica", "Type1");
            _fontCache[fontResourceName] = fallback;
            return fallback;
        }

        string baseFont = fontObj.GetName("BaseFont") ?? "Helvetica";
        string subtype = fontObj.GetName("Subtype") ?? "Type1";
        int firstChar = (int)(fontObj.GetInteger("FirstChar") ?? 0);
        int lastChar = (int)(fontObj.GetInteger("LastChar") ?? 255);

        // Resolve Widths array for simple fonts
        List<double>? widths = null;
        var widthsObj = _resolver.Resolve(fontObj["Widths"]);
        if (widthsObj is PdfArray wArr)
        {
            widths = new List<double>(wArr.Count);
            foreach (var item in wArr)
            {
                widths.Add(item.TryGetNumber(out double w) ? w : 500.0);
            }
        }

        // Resolve ToUnicode CMap if present
        Dictionary<int, string>? toUnicodeMap = null;
        var toUnicodeObj = _resolver.Resolve(fontObj["ToUnicode"]);
        if (toUnicodeObj is PdfStream toUnicodeStream)
        {
            toUnicodeMap = ParseToUnicodeCMap(toUnicodeStream);
        }

        // Resolve FontDescriptor and embedded font data
        byte[]? embeddedFontBytes = null;
        var descObj = _resolver.Resolve(fontObj["FontDescriptor"]);
        if (descObj is PdfDictionary descDict)
        {
            var fontFileObj = _resolver.Resolve(descDict["FontFile2"] ?? descDict["FontFile3"] ?? descDict["FontFile"]);
            if (fontFileObj is PdfStream fontStream)
            {
                var decoder = new PdfStreamDecoder();
                embeddedFontBytes = decoder.DecodeStream(fontStream);
            }
        }

        // Handle Type0 Composite Fonts and CIDFont descendant
        Dictionary<int, double>? cidWidths = null;
        double defaultWidth = 1000.0;

        if (subtype == "Type0")
        {
            var descendantsObj = _resolver.Resolve(fontObj["DescendantFonts"]);
            if (descendantsObj is PdfArray descArr && descArr.Count > 0)
            {
                var cidFont = _resolver.Resolve(descArr[0]) as PdfDictionary;
                if (cidFont != null)
                {
                    defaultWidth = cidFont.GetNumber("DW") ?? 1000.0;
                    cidWidths = ParseCidWidths(cidFont["W"]);

                    var cidDesc = _resolver.Resolve(cidFont["FontDescriptor"]) as PdfDictionary;
                    if (cidDesc != null && embeddedFontBytes == null)
                    {
                        var fontFile = _resolver.Resolve(cidDesc["FontFile2"] ?? cidDesc["FontFile3"] ?? cidDesc["FontFile"]) as PdfStream;
                        if (fontFile != null)
                        {
                            var decoder = new PdfStreamDecoder();
                            embeddedFontBytes = decoder.DecodeStream(fontFile);
                        }
                    }
                }
            }
        }

        var resolvedFont = new PdfFont(
            fontResourceName,
            baseFont,
            subtype,
            firstChar,
            lastChar,
            widths,
            cidWidths,
            missingWidth: 500.0,
            defaultWidth,
            toUnicodeMap,
            embeddedFontBytes);

        _fontCache[fontResourceName] = resolvedFont;
        return resolvedFont;
    }

    private Dictionary<int, double> ParseCidWidths(PdfObject? wObj)
    {
        var map = new Dictionary<int, double>();
        var resolved = _resolver.Resolve(wObj);
        if (resolved is not PdfArray arr)
            return map;

        int i = 0;
        while (i < arr.Count)
        {
            if (i + 1 >= arr.Count) break;

            int c = (int)(arr[i].TryGetInteger(out long cVal) ? cVal : 0);
            var next = _resolver.Resolve(arr[i + 1]);

            if (next is PdfArray widthsArr)
            {
                // Format: c [w1 w2 w3 ...]
                for (int j = 0; j < widthsArr.Count; j++)
                {
                    double w = widthsArr[j].TryGetNumber(out double wv) ? wv : 1000.0;
                    map[c + j] = w;
                }
                i += 2;
            }
            else if (i + 2 < arr.Count && next is PdfInteger cLastInt && arr[i + 2].TryGetNumber(out double constWidth))
            {
                // Format: cFirst cLast width
                int cLast = (int)cLastInt.Value;
                for (int code = c; code <= cLast; code++)
                {
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

    private Dictionary<int, string> ParseToUnicodeCMap(PdfStream stream)
    {
        var map = new Dictionary<int, string>();
        var decoder = new PdfStreamDecoder();
        byte[] data = decoder.DecodeStream(stream);
        string text = Encoding.ASCII.GetString(data);

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.EndsWith("beginbfrange"))
            {
                // Format: <startHex> <endHex> <dstHex>
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.EndsWith("endbfrange")) break;
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 3 &&
                        tokens[0].StartsWith("<") && tokens[0].EndsWith(">") &&
                        tokens[1].StartsWith("<") && tokens[1].EndsWith(">") &&
                        tokens[2].StartsWith("<") && tokens[2].EndsWith(">"))
                    {
                        int start = Convert.ToInt32(tokens[0].Trim('<', '>'), 16);
                        int end = Convert.ToInt32(tokens[1].Trim('<', '>'), 16);
                        int dst = Convert.ToInt32(tokens[2].Trim('<', '>'), 16);

                        for (int code = start; code <= end; code++)
                        {
                            map[code] = char.ConvertFromUtf32(dst + (code - start));
                        }
                    }
                }
            }
            else if (line.EndsWith("beginbfchar"))
            {
                // Format: <srcHex> <dstHex>
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.EndsWith("endbfchar")) break;
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 &&
                        tokens[0].StartsWith("<") && tokens[0].EndsWith(">") &&
                        tokens[1].StartsWith("<") && tokens[1].EndsWith(">"))
                    {
                        int src = Convert.ToInt32(tokens[0].Trim('<', '>'), 16);
                        int dst = Convert.ToInt32(tokens[1].Trim('<', '>'), 16);
                        map[src] = char.ConvertFromUtf32(dst);
                    }
                }
            }
        }

        return map;
    }
}
