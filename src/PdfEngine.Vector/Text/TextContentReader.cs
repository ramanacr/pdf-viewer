using System;
using System.Collections.Generic;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Text;

/// <summary>The objects of one content stream (the page's, or one drawing of a form XObject), in drawing order.</summary>
internal sealed class TextHolder
{
    public readonly List<object> Objects = new(); // TextObject or FormObject
}

/// <summary>A form XObject drawn by its holder: its own objects, and the CTM of its holder where it is drawn.</summary>
internal sealed class FormObject
{
    public required TextHolder Holder { get; init; }
    public required PdfMatrix FormMatrix { get; init; }
}

/// <summary>One item of a text object: a character code, or (between two strings of a TJ array) a kerning.</summary>
internal readonly record struct TextItem(int Code, int Cid, double X, double Y)
{
    public bool IsKerning => Code == TextFontInfo.InvalidCode;
}

/// <summary>
/// One text-showing operator, as PDFium's page model holds it: the codes it shows (with a
/// kerning item between the strings of a TJ array), the text matrix with the horizontal scale
/// and the start position, the box of its glyphs, and the marked content it is in.
/// </summary>
internal sealed class TextObject
{
    public required TextFontInfo Font { get; init; }
    public double FontSize { get; init; }
    public double CharSpace { get; init; }
    public double WordSpace { get; init; }
    /// <summary>Tz as a fraction (1 = 100 %).</summary>
    public double HorzScale { get; init; }
    public int RenderMode { get; init; }
    /// <summary>[Tz 0 0 1] × Tm × CTM, with the text position (rise included) as the translation.</summary>
    public PdfMatrix Matrix { get; set; }
    public PdfPoint Pos => new(Matrix.E, Matrix.F);
    /// <summary>The font size along the text in user space (PDFium's GetFontSizeH).</summary>
    public double FontSizeH => Math.Abs(Math.Sqrt(Matrix.A * Matrix.A + Matrix.B * Matrix.B) * FontSize);
    public int[] Codes = Array.Empty<int>();
    public int[] Cids = Array.Empty<int>();
    /// <summary>Per item after the first: its position along the line (text space before Tz), or for a kerning item the kerning (1/1000 em).</summary>
    public double[] CharPos = Array.Empty<double>();
    public TextRect Rect;
    /// <summary>The property lists of the marked-content sequences it is in, outermost first (null for BMC or an unresolved name).</summary>
    public PdfDictionary?[] Marks = Array.Empty<PdfDictionary?>();
    public int Mcid = -1;
    /// <summary>Index of the text-showing operator over the whole page (forms included), for diagnostics.</summary>
    public int Order;

    public int CountItems => Codes.Length;

    public int CountChars
    {
        get
        {
            int n = 0;
            foreach (int c in Codes) if (c != TextFontInfo.InvalidCode) n++;
            return n;
        }
    }

    public TextItem GetItemInfo(int index)
    {
        int code = Codes[index];
        double x = index > 0 ? CharPos[index - 1] : 0;
        if (code == TextFontInfo.InvalidCode) return new TextItem(code, code, x, 0);
        int cid = Cids[index];
        if (!Font.IsVertical) return new TextItem(code, cid, x, 0);
        var (vx, vy) = Font.VertOrigin(cid);
        return new TextItem(code, cid, -FontSize * vx / 1000, x - FontSize * vy / 1000);
    }

    public TextItem GetCharInfo(int n)
    {
        for (int i = 0; i < Codes.Length; i++)
        {
            if (Codes[i] == TextFontInfo.InvalidCode) continue;
            if (n-- == 0) return GetItemInfo(i);
        }
        return default;
    }

    /// <summary>The glyph's advance in text space (font size applied, no spacing).</summary>
    public double GetCharWidth(int code, int cid)
    {
        double fs = FontSize / 1000;
        return Font.IsVertical ? Font.VertWidth(cid) * fs : Font.GetCharWidthF(code, cid) * fs;
    }
}

/// <summary>
/// Reads a page's content into text objects the way PDFium's content parser builds them, so that
/// the text page built over them (<see cref="PdfTextPage"/>) sees the same objects, positions and
/// boxes. Only the state text depends on is kept: the CTM, the text state, marked content and form
/// XObjects. Paths, images and colours are skipped.
/// </summary>
internal sealed class TextContentReader
{
    private const int MaxFormDepth = 12;
    private const int MaxTextObjects = 500_000;

    private readonly PdfObjectResolver _r;
    private readonly PdfFontResolver _fonts;
    private readonly Func<PdfFont, PdfDictionary?, TextFontInfo> _fontInfo;
    private readonly PdfStreamDecoder _decoder;
    private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfStream, byte[]> _forms = new(ReferenceEqualityComparer.Instance);
    private int _order, _depth;
    private TextFontInfo? _missing;

    /// <summary>A font name the resources do not have: Helvetica, as PDFium substitutes.</summary>
    private TextFontInfo Missing() => _missing ??= _missingInfo?.Invoke() ?? _fontInfo(_fonts.ResolveFont("\0missing", null), null);

    private readonly Func<TextFontInfo>? _missingInfo;

    /// <param name="missing">The substitute for missing fonts, when the caller keeps one (it is the same for every page).</param>
    public TextContentReader(PdfObjectResolver resolver, PdfFontResolver fonts, Func<PdfFont, PdfDictionary?, TextFontInfo> fontInfo, Func<TextFontInfo>? missing = null)
    {
        _missingInfo = missing;
        _r = resolver;
        _fonts = fonts;
        _fontInfo = fontInfo;
        _decoder = new PdfStreamDecoder(null, resolver.Resolve);
    }

    private sealed class State
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public TextFontInfo? Font;
        public double FontSize, Tc, Tw, Th = 1, TL, Rise;
        public int Tr;
        public State Clone() => (State)MemberwiseClone();
    }

    public TextHolder Read(byte[] content, PdfDictionary resources)
    {
        var holder = new TextHolder();
        Walk(content, resources, new State(), holder);
        return holder;
    }

    // Buffers of one text-showing operator, reused (the walk is single-threaded and Show does not nest).
    private readonly List<int> _codes = new();
    private readonly List<int> _cids = new();
    private readonly List<int> _lengths = new();
    private readonly List<double> _kernAt = new();
    private readonly List<PdfString> _strings = new();
    private double[] _kernings = new double[16];

    private void Walk(byte[] content, PdfDictionary resources, State gs, TextHolder holder)
    {
        var stack = new Stack<State>();
        var marks = new List<PdfDictionary?>();
        // The marks array and MCID handed to text objects, rebuilt only when the marks change.
        PdfDictionary?[]? marksArray = Array.Empty<PdfDictionary?>();
        int marksMcid = -1;
        PdfMatrix tm = PdfMatrix.Identity;
        float posX = 0, posY = 0, lineX = 0, lineY = 0; // single precision, as PDFium
        var n = new double[6];

        var lexer = new TextContentLexer(content);

        void Show(List<PdfString> strings, double initKerning, double[] kernings, int total, int segments)
        {
            // Text shown before any font is set is not text to PDFium (nothing is drawn either).
            if (gs.Font is not { } font) return;
            bool vertical = font.IsVertical;
            if (initKerning != 0)
            {
                if (vertical) posY -= (float)(initKerning * gs.FontSize / 1000);
                else posX -= (float)(initKerning * gs.FontSize / 1000 * gs.Th);
            }
            if (segments == 0) return;
            if (holder.Objects.Count >= MaxTextObjects) return;

            var codes = _codes;
            var cids = _cids;
            var lengths = _lengths;
            var kernAt = _kernAt;
            codes.Clear(); cids.Clear(); lengths.Clear(); kernAt.Clear();
            for (int i = 0; i < segments; i++)
            {
                var bytes = strings[i].RawBytes.Span;
                int pos = 0;
                while (pos < bytes.Length)
                {
                    int consumed = font.Font.ReadCode(bytes, pos, out int code, out int cid);
                    if (consumed <= 0) consumed = 1;
                    pos += consumed;
                    codes.Add(code);
                    cids.Add(cid);
                    lengths.Add(consumed);
                    kernAt.Add(0);
                }
                if (i != segments - 1)
                {
                    codes.Add(TextFontInfo.InvalidCode);
                    cids.Add(TextFontInfo.InvalidCode);
                    lengths.Add(0);
                    kernAt.Add(kernings[i]);
                }
            }

            if (marksArray == null)
            {
                marksArray = marks.ToArray();
                marksMcid = -1;
                foreach (var m in marksArray)
                    if (m?["MCID"] is PdfInteger id) { marksMcid = (int)id.Value; break; }
            }

            int count = codes.Count;
            var codeArray = codes.ToArray();
            var obj = new TextObject
            {
                Font = font,
                FontSize = gs.FontSize,
                CharSpace = gs.Tc,
                WordSpace = gs.Tw,
                HorzScale = gs.Th,
                RenderMode = font.IsType3 ? 0 : gs.Tr,
                Codes = codeArray,
                // A simple font's CIDs are its codes: the arrays are shared (neither is written after).
                Cids = font.IsComposite ? cids.ToArray() : codeArray,
                CharPos = count > 1 ? new double[count - 1] : Array.Empty<double>(),
                Order = _order++,
                Marks = marksArray,
                Mcid = marksMcid,
            };
            // Position data (PDFium CalcPositionData): item origins along the line and the glyph box.
            float curpos = 0; double minX = 10000, maxX = -10000, minY = 10000, maxY = -10000;
            double fs = gs.FontSize;
            var objCodes = obj.Codes;
            var objCids = obj.Cids;
            var charPos = obj.CharPos;
            for (int i = 0; i < objCodes.Length; i++)
            {
                int code = objCodes[i];
                if (i > 0)
                {
                    if (code == TextFontInfo.InvalidCode)
                    {
                        charPos[i - 1] = kernAt[i];
                        curpos -= (float)(kernAt[i] * fs / 1000);
                        continue;
                    }
                    charPos[i - 1] = curpos;
                }
                int cid = objCids[i];
                var box = font.GetCharBBox(code, cid);
                double charWidth;
                if (vertical)
                {
                    var (vx, vy) = font.VertOrigin(cid);
                    double l = box.Left - vx, r = box.Right - vx, t = box.Top - vy, b = box.Bottom - vy;
                    minX = Math.Min(minX, Math.Min(l, r));
                    maxX = Math.Max(maxX, Math.Max(l, r));
                    double top = curpos + t * fs / 1000, bottom = curpos + b * fs / 1000;
                    minY = Math.Min(minY, Math.Min(top, bottom));
                    maxY = Math.Max(maxY, Math.Max(top, bottom));
                    charWidth = font.VertWidth(cid) * fs / 1000;
                }
                else
                {
                    minY = Math.Min(minY, Math.Min(box.Top, box.Bottom));
                    maxY = Math.Max(maxY, Math.Max(box.Top, box.Bottom));
                    double left = curpos + box.Left * fs / 1000, right = curpos + box.Right * fs / 1000;
                    minX = Math.Min(minX, Math.Min(left, right));
                    maxX = Math.Max(maxX, Math.Max(left, right));
                    charWidth = font.GetCharWidthF(code, cid) * fs / 1000;
                }
                curpos += (float)charWidth;
                if (code == 32 && lengths[i] == 1) curpos += (float)gs.Tw;
                curpos += (float)gs.Tc;
            }
            if (vertical) { minX = minX * fs / 1000; maxX = maxX * fs / 1000; }
            else { minY = minY * fs / 1000; maxY = maxY * fs / 1000; }

            var linear = new PdfMatrix(gs.Th, 0, 0, 1, 0, 0) * tm * gs.Ctm;
            var origin = (tm * gs.Ctm).Transform(posX, posY + gs.Rise);
            obj.Matrix = new PdfMatrix(linear.A, linear.B, linear.C, linear.D, origin.X, origin.Y).ToSingle();
            obj.Rect = new TextRect(minX, minY, maxX, maxY).Transform(obj.Matrix);
            obj.Rect = new TextRect(TextMatrixMath.F(obj.Rect.Left), TextMatrixMath.F(obj.Rect.Bottom), TextMatrixMath.F(obj.Rect.Right), TextMatrixMath.F(obj.Rect.Top));
            for (int i = 0; i < charPos.Length; i++) charPos[i] = TextMatrixMath.F(charPos[i]);

            if (vertical) posY += curpos;
            else posX += curpos * (float)gs.Th;
            holder.Objects.Add(obj);

            double trailing = total > 0 ? kernings[total - 1] : 0;
            if (trailing != 0)
            {
                if (vertical) posY -= (float)(trailing * gs.FontSize / 1000);
                else posX -= (float)(trailing * gs.FontSize / 1000 * gs.Th);
            }
        }

        void ShowOne(PdfString s)
        {
            if (s.RawBytes.Length == 0) return;
            var strings = _strings;
            strings.Clear();
            strings.Add(Limit(s));
            _kernings[0] = 0;
            Show(strings, 0, _kernings, 1, 1);
        }

        void NextLine()
        {
            lineY -= (float)gs.TL;
            posX = lineX; posY = lineY;
        }

        while (lexer.Next(out var op))
        {
            int count = lexer.Count;
            switch (op)
            {
                case TextOperator.q:
                    if (stack.Count < 512) stack.Push(gs.Clone());
                    break;
                case TextOperator.Q:
                    if (stack.Count > 0) gs = stack.Pop();
                    break;
                case TextOperator.cm when lexer.Numbers(6, n):
                    gs.Ctm = new PdfMatrix(n[0], n[1], n[2], n[3], n[4], n[5]) * gs.Ctm;
                    break;
                case TextOperator.BT:
                    tm = PdfMatrix.Identity;
                    posX = posY = lineX = lineY = 0;
                    break;
                case TextOperator.Tf when count >= 2:
                {
                    if (lexer.TryNumber(1, out double size)) gs.FontSize = size;
                    if (lexer.NameAt(2) is { } fn)
                    {
                        PdfFont font;
                        var fontDict = _r.Resolve((_r.Resolve(resources["Font"]) as PdfDictionary)?[fn]) as PdfDictionary;
                        try { font = fontDict != null ? _fonts.ResolveFont(fontDict, fn) : _fonts.ResolveFont(fn, resources); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { gs.Font = Missing(); break; }
                        gs.Font = font.IsMissingResource ? Missing() : _fontInfo(font, fontDict);
                    }
                    break;
                }
                case TextOperator.gs when count >= 1 && lexer.NameAt(1) is { } gsName:
                {
                    // An ExtGState's /Font [font size] sets the font as Tf does.
                    var ext = _r.Resolve((_r.Resolve(resources["ExtGState"]) as PdfDictionary)?[gsName]) as PdfDictionary;
                    if (ext != null && _r.Resolve(ext["Font"]) is PdfArray { Count: >= 2 } fontEntry &&
                        _r.Resolve(fontEntry.Items[0]) is PdfDictionary gsFontDict && _r.Resolve(fontEntry.Items[1]) is { } sizeObj && sizeObj.TryGetNumber(out double gsSize))
                    {
                        gs.FontSize = gsSize;
                        try { gs.Font = _fontInfo(_fonts.ResolveFont(gsFontDict, gsName), gsFontDict); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { gs.Font = Missing(); }
                    }
                    break;
                }
                case TextOperator.Tc when lexer.Numbers(1, n): gs.Tc = n[0]; break;
                case TextOperator.Tw when lexer.Numbers(1, n): gs.Tw = n[0]; break;
                case TextOperator.Tz when count == 1 && lexer.Numbers(1, n): gs.Th = n[0] / 100.0; break;
                case TextOperator.TL when lexer.Numbers(1, n): gs.TL = n[0]; break;
                case TextOperator.Ts when lexer.Numbers(1, n): gs.Rise = n[0]; break;
                case TextOperator.Tr when lexer.Numbers(1, n): gs.Tr = (int)n[0]; break;
                case TextOperator.Td when lexer.Numbers(2, n):
                    lineX += (float)n[0]; lineY += (float)n[1];
                    posX = lineX; posY = lineY;
                    break;
                case TextOperator.TD when lexer.Numbers(2, n):
                    gs.TL = -n[1];
                    lineX += (float)n[0]; lineY += (float)n[1];
                    posX = lineX; posY = lineY;
                    break;
                case TextOperator.Tm when lexer.Numbers(6, n):
                    tm = new PdfMatrix(n[0], n[1], n[2], n[3], n[4], n[5]);
                    posX = posY = lineX = lineY = 0;
                    break;
                case TextOperator.TStar:
                    NextLine();
                    break;
                case TextOperator.Tj when count >= 1 && lexer.StringAt(1) is { } s:
                    ShowOne(s);
                    break;
                case TextOperator.Quote when count >= 1 && lexer.StringAt(1) is { } s1:
                    NextLine();
                    ShowOne(s1);
                    break;
                case TextOperator.DoubleQuote when count >= 3 && lexer.StringAt(1) is { } s2 && lexer.TryNumber(3, out double aw) && lexer.TryNumber(2, out double ac):
                    gs.Tw = aw; gs.Tc = ac;
                    NextLine();
                    ShowOne(s2);
                    break;
                case TextOperator.TJ when count >= 1:
                {
                    var last = lexer.At(1);
                    int items = last.Kind == TextContentLexer.Kind.Array ? last.Count : last.Value is PdfArray a ? a.Count : -1;
                    if (items < 0) break;
                    var arr = last.Value as PdfArray;
                    // One TJ item: a string, or a number (the kerning), else nothing.
                    (PdfString? Str, bool IsNumber, double Number) Item(int i)
                    {
                        if (arr == null)
                        {
                            var it = lexer.ArrayItems[last.Start + i];
                            return it.Kind == TextContentLexer.Kind.String ? ((PdfString)it.Value!, false, 0) : (null, true, it.Number);
                        }
                        var item = _r.Resolve(arr.Items[i]);
                        if (item is PdfString str) return (str, false, 0);
                        return item != null && item.TryGetNumber(out double num) ? (null, true, num) : (null, false, 0);
                    }

                    int total = 0;
                    for (int i = 0; i < items; i++)
                        if (Item(i).Str != null) total++;
                    if (total == 0)
                    {
                        for (int i = 0; i < items; i++)
                            if (Item(i) is { IsNumber: true, Number: var k } && k != 0)
                                posX -= (float)(k * gs.FontSize / 1000 * gs.Th);
                        break;
                    }
                    var strs = _strings;
                    strs.Clear();
                    if (_kernings.Length < total) _kernings = new double[Math.Max(total, _kernings.Length * 2)];
                    var kernings = _kernings;
                    Array.Clear(kernings, 0, total);
                    int seg = 0;
                    double init = 0;
                    for (int i = 0; i < items; i++)
                    {
                        var (str, isNumber, num) = Item(i);
                        if (str != null)
                        {
                            if (str.RawBytes.Length == 0) continue;
                            strs.Add(Limit(str));
                            kernings[seg++] = 0;
                        }
                        else if (isNumber && double.IsFinite(num))
                        {
                            if (seg == 0) init += num;
                            else kernings[seg - 1] += num;
                        }
                    }
                    Show(strs, init, kernings, total, seg);
                    break;
                }
                case TextOperator.BI:
                    lexer.SkipInlineImage();
                    break;
                case TextOperator.Do when count >= 1 && lexer.NameAt(1) is { } xname:
                {
                    var xobject = _r.Resolve((_r.Resolve(resources["XObject"]) as PdfDictionary)?[xname]) as PdfStream;
                    if (xobject?.Dictionary.GetName("Subtype") != "Form" || _depth >= MaxFormDepth || !_activeForms.Add(xobject)) break;
                    _depth++;
                    try
                    {
                        byte[] formContent;
                        // A form drawn several times on the page is decoded once.
                        if (!_forms.TryGetValue(xobject, out formContent!))
                        {
                            try { formContent = _decoder.DecodeStream(xobject); }
                            catch (Exception ex) when (ex is not OutOfMemoryException) { break; }
                            _forms[xobject] = formContent;
                        }
                        var dict = xobject.Dictionary;
                        var formResources = _r.Resolve(dict["Resources"]) as PdfDictionary ?? resources;
                        // The form's own content starts from its /Matrix with the text state
                        // (font, size, spacing, mode) of where it is drawn; Tz, TL and Ts start over.
                        var inner = new State
                        {
                            Font = gs.Font, FontSize = gs.FontSize, Tc = gs.Tc, Tw = gs.Tw, Tr = gs.Tr,
                            Ctm = FormMatrix(dict),
                        };
                        var formHolder = new TextHolder();
                        Walk(formContent, formResources, inner, formHolder);
                        holder.Objects.Add(new FormObject { Holder = formHolder, FormMatrix = gs.Ctm });
                    }
                    finally
                    {
                        _depth--;
                        _activeForms.Remove(xobject);
                    }
                    break;
                }
                case TextOperator.BDC:
                {
                    PdfDictionary? props = null;
                    if (count >= 2)
                    {
                        props = lexer.ValueAt(1) as PdfDictionary
                            ?? (lexer.NameAt(1) is { } pn ? _r.Resolve((_r.Resolve(resources["Properties"]) as PdfDictionary)?[pn]) as PdfDictionary : null);
                    }
                    if (marks.Count < 256) { marks.Add(props); marksArray = null; }
                    break;
                }
                case TextOperator.BMC:
                    if (marks.Count < 256) { marks.Add(null); marksArray = null; }
                    break;
                case TextOperator.EMC:
                    if (marks.Count > 0) { marks.RemoveAt(marks.Count - 1); marksArray = null; }
                    break;
            }
            lexer.Clear();
        }
    }

    /// <summary>PDFium's content parser keeps at most 32 767 bytes of a string.</summary>
    private static PdfString Limit(PdfString s) => s.RawBytes.Length > 32767 ? s with { RawBytes = s.RawBytes[..32767] } : s;

    private PdfMatrix FormMatrix(PdfDictionary dict)
    {
        if (_r.Resolve(dict["Matrix"]) is PdfArray { Count: 6 } m)
        {
            var v = new double[6];
            for (int i = 0; i < 6; i++)
                if (!(_r.Resolve(m.Items[i])?.TryGetNumber(out v[i]) ?? false)) return PdfMatrix.Identity;
            return new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        }
        return PdfMatrix.Identity;
    }

    /// <summary>A text string (ISO 32000-2 7.9.2.2): UTF-16BE or UTF-8 with a byte-order mark, else PDFDocEncoding.</summary>
    internal static string TextString(PdfString s)
    {
        var span = s.RawBytes.Span;
        if (span.Length >= 2 && ((span[0] == 0xFE && span[1] == 0xFF) || (span[0] == 0xEF && span.Length >= 3 && span[1] == 0xBB && span[2] == 0xBF)))
            return s.AsDecodedString();
        var sb = new StringBuilder(span.Length);
        foreach (byte b in span) sb.Append(PdfDocEncoding(b));
        return sb.ToString();
    }

    private static readonly char[] DocHigh =
    {
        '•', '†', '‡', '…', '—', '–', 'ƒ', '⁄', '‹', '›', '−', '‰', '„', '“', '”', '‘',
        '’', '‚', '™', 'ﬁ', 'ﬂ', 'Ł', 'Œ', 'Š', 'Ÿ', 'Ž', 'ı', 'ł', 'œ', 'š', 'ž', '�',
    };

    private static readonly char[] DocLow = { '˘', 'ˇ', 'ˆ', '˙', '˝', '˛', '˚', '˜' };

    private static char PdfDocEncoding(byte b) => b switch
    {
        >= 0x18 and <= 0x1F => DocLow[b - 0x18],
        >= 0x80 and <= 0x9F => DocHigh[b - 0x80],
        0xA0 => '€',
        _ => (char)b,
    };
}
