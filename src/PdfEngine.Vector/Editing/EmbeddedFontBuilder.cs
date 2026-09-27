using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// A system font embedded for new text: a Type0 font (Identity-H, CID = glyph id) over a
/// CIDFontType2 whose program is the TrueType font subset to the glyphs used, with widths and a
/// ToUnicode map so the text can be searched and copied.
/// </summary>
internal sealed class EmbeddedFontBuilder
{
    private readonly Dictionary<int, string> _used = new();

    public TrueTypeFontFile Font { get; }
    /// <summary>The resource name the page uses for it.</summary>
    public string ResourceName { get; }

    public EmbeddedFontBuilder(TrueTypeFontFile font, string resourceName)
    {
        Font = font;
        ResourceName = resourceName;
    }

    /// <summary>Two-byte code for a glyph, remembered for the subset and the ToUnicode map.</summary>
    public byte[] Encode(int gid, string text)
    {
        if (!_used.ContainsKey(gid) || _used[gid].Length == 0) _used[gid] = text;
        return new[] { (byte)(gid >> 8), (byte)gid };
    }

    public bool IsUsed => _used.Count > 0;

    /// <summary>Adds the font's objects; returns the Type0 font's object number.</summary>
    public int Write(Func<int> allocate, IDictionary<int, PdfObject> objects)
    {
        int type0 = allocate(), cidFont = allocate(), descriptor = allocate(), file = allocate(), toUnicode = allocate();
        string tag = SubsetTag();
        string baseFont = tag + "+" + Font.PostScriptName;

        byte[] program = Font.Subset(_used.Keys);
        objects[file] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
        {
            ["Filter"] = new PdfName("FlateDecode"),
            ["Length1"] = new PdfInteger(program.Length),
        }, Redaction.ContentRedactor.Deflate(program));

        double k = 1000.0 / Font.UnitsPerEm;
        int flags = (Font.IsFixedPitch ? 1 : 0) | (Font.IsSerif ? 2 : 0) | 4 | (Font.IsItalic ? 64 : 0);
        objects[descriptor] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("FontDescriptor"),
            ["FontName"] = new PdfName(baseFont),
            ["Flags"] = new PdfInteger(flags),
            ["FontBBox"] = new PdfArray(new PdfObject[]
            {
                new PdfInteger((long)Math.Round(Font.BBox.XMin * k)), new PdfInteger((long)Math.Round(Font.BBox.YMin * k)),
                new PdfInteger((long)Math.Round(Font.BBox.XMax * k)), new PdfInteger((long)Math.Round(Font.BBox.YMax * k)),
            }),
            ["ItalicAngle"] = new PdfReal(Font.ItalicAngle),
            ["Ascent"] = new PdfInteger((long)Math.Round(Font.Ascender * k)),
            ["Descent"] = new PdfInteger((long)Math.Round(Font.Descender * k)),
            ["CapHeight"] = new PdfInteger((long)Math.Round(Font.CapHeight * k)),
            ["StemV"] = new PdfInteger(Font.IsBold ? 140 : 80),
            ["FontWeight"] = new PdfInteger(Font.Weight),
            ["FontFile2"] = new PdfIndirectRef(file),
        });

        // /W: runs of consecutive glyph ids with their widths.
        var w = new List<PdfObject>();
        var gids = _used.Keys.OrderBy(g => g).ToList();
        for (int i = 0; i < gids.Count;)
        {
            int start = gids[i];
            var widths = new List<PdfObject>();
            int j = i;
            while (j < gids.Count && gids[j] == start + (j - i))
            {
                widths.Add(new PdfInteger((long)Math.Round(Font.Advance(gids[j]))));
                j++;
            }
            w.Add(new PdfInteger(start));
            w.Add(new PdfArray(widths));
            i = j;
        }
        objects[cidFont] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("CIDFontType2"),
            ["BaseFont"] = new PdfName(baseFont),
            ["CIDSystemInfo"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Registry"] = PdfObjectWriter.TextString("Adobe"), ["Ordering"] = PdfObjectWriter.TextString("Identity"), ["Supplement"] = new PdfInteger(0),
            }),
            ["FontDescriptor"] = new PdfIndirectRef(descriptor),
            ["DW"] = new PdfInteger(1000),
            ["W"] = new PdfArray(w),
            ["CIDToGIDMap"] = new PdfName("Identity"),
        });
        objects[toUnicode] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") },
            Redaction.ContentRedactor.Deflate(Encoding.ASCII.GetBytes(ToUnicodeCMap(_used))));
        objects[type0] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type0"),
            ["BaseFont"] = new PdfName(baseFont),
            ["Encoding"] = new PdfName("Identity-H"),
            ["DescendantFonts"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(cidFont) }),
            ["ToUnicode"] = new PdfIndirectRef(toUnicode),
        });
        return type0;
    }

    /// <summary>Six capital letters derived from the glyphs used (ISO 32000-2 9.9.2).</summary>
    private string SubsetTag()
    {
        uint h = 2166136261;
        foreach (char ch in Font.PostScriptName) h = (h ^ ch) * 16777619;
        foreach (int g in _used.Keys.OrderBy(g => g)) h = (h ^ (uint)g) * 16777619;
        var sb = new StringBuilder(6);
        for (int i = 0; i < 6; i++) { sb.Append((char)('A' + h % 26)); h /= 26; if (h == 0) h = 2166136261u + (uint)i; }
        return sb.ToString();
    }

    internal static string ToUnicodeCMap(IReadOnlyDictionary<int, string> map)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = map.Where(e => e.Value.Length > 0).OrderBy(e => e.Key).ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (code, text) in chunk)
                sb.Append('<').Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                  .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append(">\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }
}
