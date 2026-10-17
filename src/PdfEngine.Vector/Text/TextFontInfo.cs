using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Geometry;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Text;

/// <summary>A glyph box in 1/1000 em (glyph space after the font matrix), PDF orientation.</summary>
internal readonly record struct GlyphBox(int Left, int Bottom, int Right, int Top)
{
    public static readonly GlyphBox Zero = new(0, 0, 0, 0);
    public bool IsDegenerate => Left == Right;
}

/// <summary>
/// What text extraction needs from a font beyond what rendering uses: character code to Unicode
/// with PDFium's fallbacks, glyph ink boxes (embedded TrueType and CFF outlines, Type 3 /d1, a
/// system face for fonts that are not embedded), the font box and the vertical metrics. One per
/// <see cref="PdfFont"/>, cached by the document's text context.
/// </summary>
internal sealed class TextFontInfo
{
    public const int InvalidCode = -1;

    private readonly PdfFont _font;
    private readonly Dictionary<int, GlyphBox> _boxes = new();
    private readonly Func<int, int, int>? _glyphId;
    private readonly int[]? _loca;
    private readonly byte[]? _glyf;
    private readonly double _ttScale; // glyph units → 1/1000 em
    private readonly CffGlyphBounds? _cff;
    private readonly CffFont? _cffFont;
    private readonly SystemFace? _system;
    private readonly PdfObjectResolver? _resolver;
    private int _spaceCode = int.MinValue;

    public PdfFont Font => _font;
    public bool IsVertical => _font.IsComposite && _font.IsVertical;
    public bool IsComposite => _font.IsComposite;
    public bool IsType3 => _font.IsType3;

    /// <summary>The font box in 1/1000 em (FontBBox, else the face's, else the union of the glyph boxes).</summary>
    public GlyphBox FontBBox { get; }

    /// <summary>The font dictionary (null for a substitute).</summary>
    public PdfDictionary? Dictionary { get; }

    public TextFontInfo(PdfFont font, PdfDictionary? dictionary, PdfObjectResolver? resolver)
    {
        _font = font;
        Dictionary = dictionary;
        _resolver = resolver;
        try
        {
            if (font.IsType3)
            {
                // Glyph boxes come from the char procs (d1).
            }
            else if (FontProgramPreparer.Prepare(font) is { } prepared)
            {
                _glyphId = prepared.GetGlyphId;
                if (prepared.Format == PdfFontProgramFormat.TrueType && Sfnt.ReadTables(prepared.Sfnt, out _) is { } tables)
                {
                    if (tables.TryGetValue("head", out var head) && head.Length >= 54 && tables.TryGetValue("loca", out var loca) &&
                        tables.TryGetValue("glyf", out var glyf) && tables.TryGetValue("maxp", out var maxp) && maxp.Length >= 6)
                    {
                        int upm = BigEndian.U16(head, 18);
                        int format = BigEndian.S16(head, 50);
                        int n = BigEndian.U16(maxp, 4);
                        if (upm > 0)
                        {
                            _ttScale = 1000.0 / upm;
                            _loca = ReadLoca(loca, format, n);
                            _glyf = glyf;
                            // Left side bearings from the program as embedded (preparation may replace a broken hmtx).
                            var original = font.EmbeddedFontData != null ? Sfnt.ReadTables(font.EmbeddedFontData, out _) : null;
                            if (original != null && original.TryGetValue("hmtx", out var hmtx) && original.TryGetValue("hhea", out var hhea) && hhea.Length >= 36)
                            {
                                int metrics = BigEndian.U16(hhea, 34);
                                if (metrics >= 1 && hmtx.Length >= metrics * 4) { _hmtx = hmtx; _hMetrics = metrics; }
                            }
                        }
                    }
                }
                else if (prepared.Format == PdfFontProgramFormat.OpenTypeCff && OriginalCff(font) is { } cffData && CffFont.TryParse(cffData) is { } cff)
                {
                    _cffFont = cff;
                    _cff = CffGlyphBounds.TryCreate(cffData, cff);
                }
            }
            else if (font.EmbeddedFontData == null)
            {
                _system = SystemFace.For(font);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            _glyphId = null; _cff = null; _glyf = null;
        }
        FontBBox = ComputeFontBBox();
    }

    private static byte[]? OriginalCff(PdfFont font)
    {
        var data = font.EmbeddedFontData;
        if (data == null) return null;
        switch (font.EmbeddedProgramKind)
        {
            case PdfFontProgramKind.Type1C:
            case PdfFontProgramKind.CidFontType0C:
                return data;
            case PdfFontProgramKind.Type1:
                return Type1Converter.TryConvert(data)?.Cff;
            default:
                return Sfnt.ReadTables(data, out _) is { } t && t.TryGetValue("CFF ", out var c) ? c : null;
        }
    }

    private static int[]? ReadLoca(byte[] loca, int format, int glyphs)
    {
        var offsets = new int[glyphs + 1];
        for (int i = 0; i <= glyphs; i++)
        {
            if (format == 0)
            {
                if (i * 2 + 2 > loca.Length) return null;
                offsets[i] = BigEndian.U16(loca, i * 2) * 2;
            }
            else
            {
                if (i * 4 + 4 > loca.Length) return null;
                offsets[i] = (int)Math.Min(BigEndian.U32(loca, i * 4), int.MaxValue);
            }
        }
        return offsets;
    }

    // ------------------------------------------------------------------ Unicode

    /// <summary>
    /// The text of a character code, as PDFium reads it: /ToUnicode; for simple fonts the
    /// encoding's glyph name; for composite fonts a Unicode CMap's code or an Adobe collection's
    /// CID. Empty when none (the caller then uses the code itself).
    /// </summary>
    public string UnicodeFromCharCode(int code)
    {
        if (ToUnicode() is { } map)
        {
            string u = map.Lookup(code);
            if (u.Length > 0) return u;
        }
        else if (Dictionary == null && _font.ToUnicodeMap != null && _font.ToUnicodeMap.TryGetValue(code, out var engineMapped) && engineMapped.Length > 0)
            return engineMapped;
        if (_font.IsComposite)
        {
            if (_font.CodesAreUnicode && code is > 0 and <= 0xFFFF)
                return ((char)code).ToString();
            // PDFium (as built for this viewer's oracle) has no CID-to-Unicode tables: a composite
            // font without /ToUnicode reads as its character codes unless its CMap is a Unicode one.
            return string.Empty;
        }
        if (code is < 0 or > 255) return string.Empty;
        return SimpleTable()[code] ?? string.Empty;
    }

    private string?[]? _simpleUnicode, _simpleNames;

    private string?[] SimpleTable()
    {
        if (_simpleUnicode == null) (_simpleUnicode, _simpleNames) = SimpleFontUnicode.Build(_font, Dictionary, _resolver);
        return _simpleUnicode;
    }

    /// <summary>The glyph name PDFium selects a simple font's glyph by, or null.</summary>
    private string? SimpleGlyphName(int code)
    {
        if (code is < 0 or > 255 || _font.IsComposite) return null;
        SimpleTable();
        return _simpleNames![code];
    }

    private string UnicodeFromCharCodeNoMap(int code)
    {
        return SimpleTable()[code] ?? string.Empty;
    }

    /// <summary>The code PDFium finds for a Unicode character (reverse /ToUnicode, then the encoding); -1 when none, 0 when the font has no way to say.</summary>
    public int CharCodeFromUnicode(char unicode)
    {
        if (ToUnicode() is { } map && map.ReverseLookup(unicode) is int reverse and not 0)
            return reverse;
        if (_font.IsComposite)
            return _font.CodesAreUnicode ? unicode : 0;
        string target = unicode.ToString();
        for (int code = 0; code < 256; code++)
            if (UnicodeFromCharCodeNoMap(code) == target)
                return code;
        return InvalidCode;
    }

    private ToUnicodeReader? _toUnicode;
    private bool _toUnicodeRead;

    /// <summary>The font's /ToUnicode CMap read with PDFium's rules; null when it has none.</summary>
    private ToUnicodeReader? ToUnicode()
    {
        if (_toUnicodeRead) return _toUnicode;
        _toUnicodeRead = true;
        if (Dictionary != null && _resolver != null && _resolver.Resolve(Dictionary["ToUnicode"]) is PdfStream stream)
        {
            try { _toUnicode = ToUnicodeReader.Parse(new PdfStreamDecoder(null, _resolver.Resolve).DecodeStream(stream)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _toUnicode = null; }
        }
        return _toUnicode;
    }

    public int SpaceCode
    {
        get
        {
            if (_spaceCode == int.MinValue) _spaceCode = CharCodeFromUnicode(' ');
            return _spaceCode;
        }
    }

    // ------------------------------------------------------------------ metrics

    /// <summary>Horizontal width in 1/1000 em (whole units, as PDFium stores them); for composite fonts by CID.</summary>
    public double GetCharWidthF(int code, int cid)
    {
        double w = _font.IsComposite ? _font.GetGlyphWidth(cid) : code is < 0 or > 255 ? 0 : _font.GetGlyphWidth(code);
        if (!double.IsFinite(w)) return 0;
        // A Type 3 glyph without a width in /Widths advances by its procedure's own width (d0/d1).
        if (_font.IsType3 && w == 0 && code is >= 0 and < 256 && Type3Metrics(code).Wx is double wx)
            w = wx * _font.FontMatrix.A * 1000.0;
        return _font.IsType3 ? Math.Round(w) : Math.Truncate(w);
    }

    /// <summary>PDFium's text-page width: the font width, else the glyph box width.</summary>
    public double GetCharWidth(int code, int cid)
    {
        if (code == InvalidCode) return 0;
        double w = GetCharWidthF(code, cid);
        if (w > 0) return w;
        var box = GetCharBBox(code, cid);
        return Math.Max(box.Right - box.Left, 0);
    }

    /// <summary>The CID a code selects (the code itself for simple fonts).</summary>
    /// <summary>The font's ascent and descent in 1/1000 em, as PDFium takes them (the descriptor's, else the face's).</summary>
    public int TypeAscent => _typeMetrics.Value.Ascent;
    public int TypeDescent => _typeMetrics.Value.Descent;

    private Lazy<(int Ascent, int Descent)> _typeMetrics => _typeMetricsField ??= new Lazy<(int, int)>(ReadTypeMetrics);
    private Lazy<(int Ascent, int Descent)>? _typeMetricsField;

    private (int, int) ReadTypeMetrics()
    {
        var desc = Descriptor();
        bool hasAscent = desc?["Ascent"] is { } a && a.TryGetNumber(out _);
        bool hasDescent = desc?["Descent"] is { } d && d.TryGetNumber(out _);
        int ascent = hasAscent && desc!["Ascent"]!.TryGetNumber(out double av) ? (int)av : R(_font.Ascent * 1000);
        int descent = hasDescent && desc!["Descent"]!.TryGetNumber(out double dv) ? (int)dv : R(_font.Descent * 1000);
        if (!hasAscent && !hasDescent && _font.IsType3)
        {
            // No face to ask: the top of 'A' and the bottom of 'g' (else the font box), as PDFium measures.
            var capA = GetCharBBox('A', 'A');
            var g = GetCharBBox('g', 'g');
            ascent = capA.Bottom != capA.Top ? capA.Top : FontBBox.Top;
            descent = g.Bottom != g.Top ? g.Bottom : FontBBox.Bottom;
        }
        if (descent > 10) descent = -descent;
        return (ascent, descent);
    }

    /// <summary>The font descriptor (a composite font's from its descendant).</summary>
    private PdfDictionary? Descriptor()
    {
        if (Dictionary == null || _resolver == null) return null;
        var dict = Dictionary;
        if (_font.IsComposite && _resolver.Resolve(dict["DescendantFonts"]) is PdfArray { Count: > 0 } kids && _resolver.Resolve(kids.Items[0]) is PdfDictionary kid)
            dict = kid;
        return _resolver.Resolve(dict["FontDescriptor"]) as PdfDictionary;
    }

    public int CidOf(int code) => _font.IsComposite ? (_font.CMap ?? PdfCMap.IdentityH).LookupAnyLength(code) : code;

    public (double VX, double VY) VertOrigin(int cid)
    {
        var vm = _font.GetVerticalMetrics(cid);
        return (Math.Truncate(vm.VX), Math.Truncate(vm.VY));
    }

    public double VertWidth(int cid) => Math.Truncate(_font.GetVerticalMetrics(cid).W1y);

    /// <summary>The glyph's ink box in 1/1000 em; zero for a blank glyph.</summary>
    public GlyphBox GetCharBBox(int code, int cid)
    {
        int key = _font.IsComposite ? cid : code;
        if (_boxes.TryGetValue(key, out var box)) return box;
        box = ComputeBox(code, cid);
        _boxes[key] = box;
        return box;
    }

    private GlyphBox ComputeBox(int code, int cid)
    {
        if (_font.IsType3) return Type3Box(code);
        if (_glyphId != null)
        {
            int gid;
            try { gid = _glyphId(code, cid); } catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { gid = 0; }
            if (gid < 0) gid = 0;
            if (_glyf != null && _loca != null) return TrueTypeBox(gid);
            if (_cff != null && _cffFont != null)
            {
                // PDFium selects a CFF glyph by the name its encoding gives the code, when the program has it.
                if (!_cffFont.IsCidKeyed && SimpleGlyphName(code) is { } name && _cffFont.GidForName(name) is int byName and > 0) gid = byName;
                return CffBox(gid);
            }
        }
        if (_system != null && !_font.IsComposite)
        {
            string text = UnicodeFromCharCode(code);
            if (text.Length == 0 && code is > 0 and < 256) text = ((char)code).ToString();
            if (_system.Box(text, code, out var sb, out double advance))
            {
                // A substitute is stretched to the PDF's widths (left and right only), as PDFium does.
                double w = GetCharWidthF(code, cid);
                if (advance > 0 && w > 0 && Math.Abs(advance - w) > 0.5)
                    return new GlyphBox((int)(sb.Left * w / advance), sb.Bottom, (int)(sb.Right * w / advance), sb.Top);
                return sb;
            }
        }
        return GenericBox(code, cid);
    }

    /// <summary>No outline to measure: the advance by the font's ascent and descent; nothing for white space.</summary>
    private GlyphBox GenericBox(int code, int cid)
    {
        string text = UnicodeFromCharCode(code);
        if (text.Length == 0 && !_font.IsComposite && code is >= 0 and < 256) text = ((char)code).ToString();
        if (text.Length == 0 || string.IsNullOrWhiteSpace(text) || text == " ") return GlyphBox.Zero;
        double w = GetCharWidthF(code, cid);
        if (w <= 0) w = 500;
        return new GlyphBox(0, (int)Math.Round(_font.Descent * 1000), (int)Math.Round(w), (int)Math.Round(_font.Ascent * 1000));
    }

    private GlyphBox TrueTypeBox(int gid)
    {
        if (gid < 0 || gid + 1 >= _loca!.Length) return GlyphBox.Zero;
        int start = _loca[gid], end = _loca[gid + 1];
        if (end <= start || start + 10 > _glyf!.Length) return GlyphBox.Zero;
        short xMin = BigEndian.S16(_glyf, start + 2), yMin = BigEndian.S16(_glyf, start + 4);
        short xMax = BigEndian.S16(_glyf, start + 6), yMax = BigEndian.S16(_glyf, start + 8);
        // FreeType places an outline so that its left edge is at the hmtx side bearing.
        if (_hmtx != null && Lsb(gid) is int lsb && lsb != xMin)
        {
            int shift = lsb - xMin;
            xMin = (short)Math.Clamp(xMin + shift, short.MinValue, short.MaxValue);
            xMax = (short)Math.Clamp(xMax + shift, short.MinValue, short.MaxValue);
        }
        return new GlyphBox(R(xMin * _ttScale), R(yMin * _ttScale), R(xMax * _ttScale), R(yMax * _ttScale));
    }

    private byte[]? _hmtx;
    private int _hMetrics;

    private int? Lsb(int gid)
    {
        if (_hmtx == null) return null;
        int offset = gid < _hMetrics ? gid * 4 + 2 : _hMetrics * 4 + (gid - _hMetrics) * 2;
        return offset + 2 <= _hmtx.Length ? BigEndian.S16(_hmtx, offset) : null;
    }

    private GlyphBox CffBox(int gid)
    {
        if (_cff!.Bounds(gid) is not { } b) return GlyphBox.Zero;
        var m = _cffFont!.OutlineMatrix(gid);
        var r = new TextRect(b.XMin, b.YMin, b.XMax, b.YMax).Transform(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]));
        return new GlyphBox(R(r.Left * 1000), R(r.Bottom * 1000), R(r.Right * 1000), R(r.Top * 1000));
    }

    private GlyphBox Type3Box(int code)
    {
        var fm = _font.FontMatrix;
        if (Type3Metrics(code).Box is { } d1)
        {
            var r = d1.Transform(fm);
            return new GlyphBox(R(r.Left * 1000), R(r.Bottom * 1000), R(r.Right * 1000), R(r.Top * 1000));
        }
        if (_font.FontBBox is { } fb && !fb.IsEmpty)
        {
            var r = new TextRect(fb.Left, fb.Top, fb.Right, fb.Bottom).Transform(fm);
            return new GlyphBox(R(r.Left * 1000), R(r.Bottom * 1000), R(r.Right * 1000), R(r.Top * 1000));
        }
        return GlyphBox.Zero;
    }

    private readonly Dictionary<int, (double? Wx, TextRect? Box)> _type3 = new();

    /// <summary>A Type 3 glyph's d0/d1 operands: its width and (d1) its box, in glyph space.</summary>
    private (double? Wx, TextRect? Box) Type3Metrics(int code)
    {
        if (_type3.TryGetValue(code, out var cached)) return cached;
        (double? Wx, TextRect? Box) result = (null, null);
        if (_font.GetGlyphName(code) is { } name && _resolver != null && _font.CharProcs != null &&
            _resolver.Resolve(_font.CharProcs[name]) is PdfStream proc)
        {
            try
            {
                var bytes = new PdfStreamDecoder(null, _resolver.Resolve).DecodeStream(proc);
                using var src = new MemoryByteSource(bytes.Length > 512 ? bytes.AsSpan(0, 512).ToArray() : bytes);
                var lexer = new PdfLexer(src, Limits.PdfSecurityLimits.Default);
                var nums = new List<double>();
                for (int i = 0; i < 64; i++)
                {
                    var t = lexer.NextToken();
                    if (t.Type == PdfTokenType.EndOfFile) break;
                    if (t.Type == PdfTokenType.Integer || t.Type == PdfTokenType.Real)
                    {
                        nums.Add(t.Type == PdfTokenType.Integer ? t.IntValue : t.RealValue);
                        continue;
                    }
                    int n = nums.Count;
                    if (t.Type == PdfTokenType.Keyword && t.TextValue == "d1" && n >= 6)
                    {
                        result = (nums[n - 6], new TextRect(Math.Min(nums[n - 4], nums[n - 2]), Math.Min(nums[n - 3], nums[n - 1]),
                            Math.Max(nums[n - 4], nums[n - 2]), Math.Max(nums[n - 3], nums[n - 1])));
                        break;
                    }
                    if (t.Type == PdfTokenType.Keyword && t.TextValue == "d0" && n >= 2)
                    {
                        result = (nums[n - 2], null);
                        break;
                    }
                    if (t.Type == PdfTokenType.Keyword) break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        _type3[code] = result;
        return result;
    }

    private static int R(double v) => (int)Math.Clamp(Math.Round(v), int.MinValue / 2, int.MaxValue / 2);

    private GlyphBox ComputeFontBBox()
    {
        if (_font.FontBBox is { } fb && (fb.Width != 0 || fb.Height != 0))
        {
            if (_font.IsType3)
            {
                var r = new TextRect(fb.Left, fb.Top, fb.Right, fb.Bottom).Transform(_font.FontMatrix);
                return new GlyphBox(R(r.Left * 1000), R(r.Bottom * 1000), R(r.Right * 1000), R(r.Top * 1000));
            }
            return new GlyphBox(R(fb.Left), R(fb.Top), R(fb.Right), R(fb.Bottom));
        }
        if (Standard14Box(_font.BaseFont) is { } std && _font.EmbeddedFontData == null) return std;
        bool first = true;
        GlyphBox acc = GlyphBox.Zero;
        for (int code = 0; code < 256; code++)
        {
            var b = GetCharBBox(code, code);
            if (b.IsDegenerate) continue;
            acc = first ? b : new GlyphBox(Math.Min(acc.Left, b.Left), Math.Min(acc.Bottom, b.Bottom), Math.Max(acc.Right, b.Right), Math.Max(acc.Top, b.Top));
            first = false;
        }
        return acc;
    }

    private static GlyphBox? Standard14Box(string baseFont) => Standard14Fonts.NormalizeName(baseFont) switch
    {
        "Helvetica" => new GlyphBox(-166, -225, 1000, 931),
        "Helvetica-Bold" => new GlyphBox(-170, -228, 1003, 962),
        "Helvetica-Oblique" => new GlyphBox(-170, -225, 1116, 931),
        "Helvetica-BoldOblique" => new GlyphBox(-174, -228, 1114, 962),
        "Times-Roman" => new GlyphBox(-168, -218, 1000, 898),
        "Times-Bold" => new GlyphBox(-168, -218, 1000, 935),
        "Times-Italic" => new GlyphBox(-169, -217, 1010, 883),
        "Times-BoldItalic" => new GlyphBox(-200, -218, 996, 921),
        "Courier" => new GlyphBox(-23, -250, 715, 805),
        "Courier-Bold" => new GlyphBox(-113, -250, 749, 801),
        "Courier-Oblique" => new GlyphBox(-27, -250, 849, 805),
        "Courier-BoldOblique" => new GlyphBox(-57, -250, 869, 801),
        "Symbol" => new GlyphBox(-180, -293, 1090, 1010),
        "ZapfDingbats" => new GlyphBox(-1, -143, 981, 820),
        _ => null,
    };

    // ------------------------------------------------------------------ system faces

    /// <summary>
    /// A Windows core font standing in for a font that is not embedded, measured by Unicode: the
    /// glyph boxes then follow what a reader shows. Without the font file, glyphs get the generic box.
    /// </summary>
    /// <summary>The TrueType fonts installed on this system by family (from their name tables), read once.</summary>
    private static class SystemFontIndex
    {
        private static readonly Lazy<Dictionary<string, List<(string File, bool Bold, bool Italic)>>> Index = new(Build);

        public static string? Find(string baseFont, bool bold, bool italic)
        {
            string family = baseFont;
            int comma = family.IndexOf(',');
            string style = string.Empty;
            if (comma >= 0) { style = family[(comma + 1)..]; family = family[..comma]; }
            int dash = family.IndexOf('-');
            if (dash > 0) { style += family[(dash + 1)..]; family = family[..dash]; }
            foreach (string suffix in new[] { "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique", "Regular", "MT", "PS" })
                if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.Ordinal)) { style += suffix; family = family[..^suffix.Length]; }
            bold |= style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || style.Contains("Black", StringComparison.OrdinalIgnoreCase);
            italic |= style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            if (!Index.Value.TryGetValue(Key(family), out var faces)) return null;
            (string File, bool Bold, bool Italic)? best = null;
            int bestScore = -1;
            foreach (var f in faces)
            {
                int score = (f.Bold == bold ? 2 : 0) + (f.Italic == italic ? 1 : 0);
                if (score > bestScore) { best = f; bestScore = score; }
            }
            return best?.File;
        }

        private static string Key(string family)
        {
            var sb = new System.Text.StringBuilder(family.Length);
            foreach (char c in family) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static Dictionary<string, List<(string, bool, bool)>> Build()
        {
            var index = new Dictionary<string, List<(string, bool, bool)>>();
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return index;
                foreach (string path in Directory.EnumerateFiles(dir, "*.ttf"))
                {
                    if (ReadNames(path) is not var (family, subfamily) || family.Length == 0) continue;
                    bool bold = subfamily.Contains("Bold", StringComparison.OrdinalIgnoreCase) || subfamily.Contains("Black", StringComparison.OrdinalIgnoreCase);
                    bool italic = subfamily.Contains("Italic", StringComparison.OrdinalIgnoreCase) || subfamily.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                    string key = Key(family);
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new();
                    list.Add((Path.GetFileName(path), bold, italic));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return index;
        }

        /// <summary>The family and subfamily names (name IDs 1 and 2, Windows Unicode) of a font file, reading only its name table.</summary>
        private static (string, string)? ReadNames(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                var header = new byte[12];
                if (fs.Read(header, 0, 12) != 12) return null;
                int tables = BigEndian.U16(header, 4);
                if (tables is 0 or > 200) return null;
                var dirBytes = new byte[tables * 16];
                if (fs.Read(dirBytes, 0, dirBytes.Length) != dirBytes.Length) return null;
                for (int i = 0; i < tables; i++)
                {
                    if (dirBytes[i * 16] != 'n' || dirBytes[i * 16 + 1] != 'a' || dirBytes[i * 16 + 2] != 'm' || dirBytes[i * 16 + 3] != 'e') continue;
                    long offset = BigEndian.U32(dirBytes, i * 16 + 8);
                    int length = (int)Math.Min(BigEndian.U32(dirBytes, i * 16 + 12), 1 << 20);
                    var name = new byte[length];
                    fs.Position = offset;
                    if (fs.Read(name, 0, length) != length) return null;
                    int count = BigEndian.U16(name, 2), strings = BigEndian.U16(name, 4);
                    string family = string.Empty, sub = string.Empty;
                    for (int r = 0; r < count; r++)
                    {
                        int rec = 6 + r * 12;
                        if (rec + 12 > name.Length) break;
                        int platform = BigEndian.U16(name, rec), id = BigEndian.U16(name, rec + 6);
                        int len = BigEndian.U16(name, rec + 8), off = BigEndian.U16(name, rec + 10);
                        if (platform != 3 || id is not (1 or 2) || strings + off + len > name.Length) continue;
                        string value = System.Text.Encoding.BigEndianUnicode.GetString(name, strings + off, len);
                        if (id == 1 && family.Length == 0) family = value;
                        if (id == 2 && sub.Length == 0) sub = value;
                    }
                    return (family, sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            return null;
        }
    }

    private sealed class SystemFace
    {
        private static readonly ConcurrentDictionary<string, SystemFace?> Faces = new(StringComparer.OrdinalIgnoreCase);
        private readonly TrueTypeCmap _cmap;
        private readonly int[] _loca;
        private readonly byte[] _glyf;
        private readonly byte[]? _hmtx;
        private readonly int _hMetrics;
        private readonly double _scale;

        private SystemFace(TrueTypeCmap cmap, int[] loca, byte[] glyf, byte[]? hmtx, int hMetrics, double scale)
        {
            _cmap = cmap; _loca = loca; _glyf = glyf; _hmtx = hmtx; _hMetrics = hMetrics; _scale = scale;
        }

        public static SystemFace? For(PdfFont font)
        {
            string name = PdfFont.StripSubsetPrefix(font.BaseFont);
            string? std = Standard14Fonts.NormalizeName(name);
            bool bold = font.IsBold || std?.Contains("Bold", StringComparison.Ordinal) == true;
            bool italic = font.IsItalic || std?.Contains("Italic", StringComparison.Ordinal) == true || std?.Contains("Oblique", StringComparison.Ordinal) == true;
            string file;
            // A font installed under its own name is the face a reader on this system shows (PDFium
            // asks the system's font mapper first); the core fonts stand in for the rest.
            if (std == null && SystemFontIndex.Find(name, bold, italic) is { } installed) return Faces.GetOrAdd(installed, Load);
            if (std == "Symbol" || name.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase)) file = "symbol.ttf";
            else if (std == "ZapfDingbats") return null;
            else if (font.IsFixedPitch || std?.StartsWith("Courier", StringComparison.Ordinal) == true || name.Contains("Courier", StringComparison.OrdinalIgnoreCase))
                file = bold ? (italic ? "courbi.ttf" : "courbd.ttf") : (italic ? "couri.ttf" : "cour.ttf");
            else if (font.IsSerif || std?.StartsWith("Times", StringComparison.Ordinal) == true || name.Contains("Times", StringComparison.OrdinalIgnoreCase))
                file = bold ? (italic ? "timesbi.ttf" : "timesbd.ttf") : (italic ? "timesi.ttf" : "times.ttf");
            else
                file = bold ? (italic ? "arialbi.ttf" : "arialbd.ttf") : (italic ? "ariali.ttf" : "arial.ttf");
            return Faces.GetOrAdd(file, Load);
        }

        private static SystemFace? Load(string file)
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (string.IsNullOrEmpty(dir)) return null;
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                var tables = Sfnt.ReadTables(data, out _);
                var cmap = TrueTypeCmap.TryParse(data);
                if (tables == null || cmap == null || !tables.TryGetValue("head", out var head) || !tables.TryGetValue("loca", out var loca) ||
                    !tables.TryGetValue("glyf", out var glyf) || !tables.TryGetValue("maxp", out var maxp)) return null;
                int upm = BigEndian.U16(head, 18);
                var offsets = ReadLoca(loca, BigEndian.S16(head, 50), BigEndian.U16(maxp, 4));
                if (upm <= 0 || offsets == null) return null;
                tables.TryGetValue("hmtx", out var hmtx);
                int hMetrics = tables.TryGetValue("hhea", out var hhea) && hhea.Length >= 36 ? BigEndian.U16(hhea, 34) : 0;
                return new SystemFace(cmap, offsets, glyf, hmtx, hMetrics, 1000.0 / upm);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
        }

        private int Gid(char c)
        {
            int gid = _cmap.Lookup(3, 1, c);
            if (gid == 0 && _cmap.Has(3, 0)) gid = _cmap.Lookup(3, 0, 0xF000 + c);
            return gid;
        }

        public bool Box(string text, int code, out GlyphBox box, out double advance)
        {
            box = GlyphBox.Zero; advance = 0;
            int gid = text.Length > 0 ? Gid(text[0]) : 0;
            if (gid <= 0 && _cmap.Has(3, 0) && code is >= 0 and < 256) gid = _cmap.Lookup(3, 0, 0xF000 + code);
            if (gid <= 0 || gid + 1 >= _loca.Length) return false;
            if (_hmtx != null && _hMetrics > 0)
            {
                int m = Math.Min(gid, _hMetrics - 1);
                if (m * 4 + 2 <= _hmtx.Length) advance = BigEndian.U16(_hmtx, m * 4) * _scale;
            }
            int start = _loca[gid], end = _loca[gid + 1];
            if (end <= start || start + 10 > _glyf.Length) return true; // blank
            box = new GlyphBox(R(BigEndian.S16(_glyf, start + 2) * _scale), R(BigEndian.S16(_glyf, start + 4) * _scale),
                R(BigEndian.S16(_glyf, start + 6) * _scale), R(BigEndian.S16(_glyf, start + 8) * _scale));
            return true;
        }

    }
}
