using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfEngine.Vector.Fonts.Programs;

/// <summary>
/// Converts an embedded Type 1 font program (FontFile, Adobe Type 1 Font Format) into a bare CFF
/// with Type 2 charstrings, so platform rasterizers that only accept sfnt/CFF can draw the
/// author's glyphs. Outlines are converted exactly; hints are dropped (the viewer renders
/// antialiased at arbitrary scales, where Type 1 hints carry no visual weight).
/// </summary>
/// <remarks>
/// Handled: PFB segment headers, binary or hex eexec, lenIV, Subrs (inlined), hsbw/sbw, all path
/// operators, closepath, div, flex (OtherSubrs 0/1/2), hint replacement (OtherSubr 3) and seac
/// accented composites. Budgets: recursion depth, operators per glyph, glyph count.
/// </remarks>
internal static class Type1Converter
{
    public sealed record Result(byte[] Cff, Dictionary<string, int> NameToGid, string?[] BuiltInEncoding);

    private const int MaxGlyphs = 65000;
    private const int MaxOpsPerGlyph = 20000;
    private const int MaxSubrDepth = 10;

    public static Result? TryConvert(byte[] program)
    {
        try
        {
            return Convert(program);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException
                                       or InvalidDataException or OverflowException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Result? Convert(byte[] program)
    {
        var (clear, encrypted) = SplitSegments(program);
        string clearText = Encoding.Latin1.GetString(clear);

        byte[] priv = Decrypt(encrypted, 55665, 4);
        int lenIV = FindInt(priv, "/lenIV") ?? 4;

        var subrs = ReadSubrs(priv, lenIV);
        var charStrings = ReadCharStrings(priv, lenIV);
        if (charStrings.Count == 0)
            return null;

        string fontName = FindName(clearText, "/FontName") ?? "Type1Font";
        double[] fontMatrix = FindNumbers(clearText, "/FontMatrix", 6) ?? new[] { 0.001, 0, 0, 0.001, 0, 0 };
        double[] fontBBox = FindNumbers(clearText, "/FontBBox", 4) ?? new double[] { 0, -200, 1000, 800 };
        var encoding = ReadEncoding(clearText);

        // Glyph order: .notdef first, then the charstring order of the font.
        var names = new List<string> { ".notdef" };
        foreach (var name in charStrings.Keys)
        {
            if (name != ".notdef") names.Add(name);
            if (names.Count >= MaxGlyphs) break;
        }

        // OpenType requires CFF outlines in a uniform 1/unitsPerEm space. Bake the Type 1 FontMatrix
        // (obliques carry a skew, some fonts use other scales) into the outlines at 1000 units/em.
        var norm = new double[6];
        for (int k = 0; k < 6; k++) norm[k] = fontMatrix[k] * 1000.0;
        bool identity = Math.Abs(norm[0] - 1) < 1e-9 && Math.Abs(norm[1]) < 1e-9 && Math.Abs(norm[2]) < 1e-9 &&
                        Math.Abs(norm[3] - 1) < 1e-9 && Math.Abs(norm[4]) < 1e-9 && Math.Abs(norm[5]) < 1e-9;

        var converted = new List<byte[]>(names.Count);
        foreach (var name in names)
        {
            byte[] type2;
            if (charStrings.TryGetValue(name, out var cs))
            {
                var interp = new Interpreter(subrs, charStrings);
                interp.Run(cs, 0, 0, depth: 0);
                type2 = interp.EmitType2(identity ? null : norm);
            }
            else
            {
                type2 = new byte[] { 14 }; // endchar: empty .notdef
            }
            converted.Add(type2);
        }

        var nameToGid = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int g = 0; g < names.Count; g++) nameToGid.TryAdd(names[g], g);
        if (!identity)
        {
            // Transform the bounding box corners too.
            double[] xs = new double[4], ys = new double[4];
            int c = 0;
            foreach (var (bx, by) in new[] { (fontBBox[0], fontBBox[1]), (fontBBox[2], fontBBox[1]), (fontBBox[0], fontBBox[3]), (fontBBox[2], fontBBox[3]) })
            {
                xs[c] = norm[0] * bx + norm[2] * by + norm[4];
                ys[c] = norm[1] * bx + norm[3] * by + norm[5];
                c++;
            }
            fontBBox = new[] { xs.Min(), ys.Min(), xs.Max(), ys.Max() };
        }
        byte[] cff = CffWriter.Write(fontName, names, converted, new[] { 0.001, 0, 0, 0.001, 0, 0 }, fontBBox);
        return new Result(cff, nameToGid, encoding);
    }

    // ------------------------------------------------------------------ program structure

    /// <summary>Clear-text and encrypted portions; understands PFB segment headers.</summary>
    private static (byte[] Clear, byte[] Encrypted) SplitSegments(byte[] p)
    {
        if (p.Length > 6 && p[0] == 0x80 && p[1] == 1)
        {
            var clear = new MemoryStream();
            var enc = new MemoryStream();
            int pos = 0;
            while (pos + 6 <= p.Length && p[pos] == 0x80)
            {
                int type = p[pos + 1];
                if (type == 3) break;
                int len = p[pos + 2] | (p[pos + 3] << 8) | (p[pos + 4] << 16) | (p[pos + 5] << 24);
                pos += 6;
                if (len < 0 || pos + len > p.Length) len = p.Length - pos;
                (type == 1 ? clear : enc).Write(p, pos, len);
                pos += len;
                if (type == 1 && enc.Length > 0) break; // trailing cleartext (zeros/cleartomark)
            }
            return (clear.ToArray(), enc.ToArray());
        }

        int idx = IndexOf(p, "eexec"u8);
        if (idx < 0)
            throw new InvalidDataException("Type1: no eexec section");
        int start = idx + 5;
        while (start < p.Length && (p[start] == '\r' || p[start] == '\n' || p[start] == ' ' || p[start] == '\t')) start++;
        var encrypted = p.AsSpan(start).ToArray();

        // eexec data may be hex; decide from the first four bytes (Type 1 spec 7.2).
        bool hex = encrypted.Length >= 4;
        for (int i = 0; i < 4 && i < encrypted.Length; i++)
            hex &= Uri.IsHexDigit((char)encrypted[i]);
        if (hex)
            encrypted = FromHex(encrypted);
        return (p.AsSpan(0, idx).ToArray(), encrypted);
    }

    private static byte[] FromHex(byte[] data)
    {
        var ms = new MemoryStream(data.Length / 2);
        int hi = -1;
        foreach (byte b in data)
        {
            int v = b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
            if (v < 0) continue;
            if (hi < 0) hi = v;
            else { ms.WriteByte((byte)((hi << 4) | v)); hi = -1; }
        }
        return ms.ToArray();
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> data, ushort key, int skip)
    {
        const ushort c1 = 52845, c2 = 22719;
        ushort r = key;
        var result = new byte[Math.Max(0, data.Length - skip)];
        for (int i = 0; i < data.Length; i++)
        {
            byte cipher = data[i];
            byte plain = (byte)(cipher ^ (r >> 8));
            r = (ushort)((cipher + r) * c1 + c2);
            if (i >= skip) result[i - skip] = plain;
        }
        return result;
    }

    /// <summary>lenIV −1 means charstrings are not encrypted (Type 1 spec 7.2).</summary>
    private static byte[] DecryptCharString(ReadOnlySpan<byte> data, int lenIV) =>
        lenIV < 0 ? data.ToArray() : Decrypt(data, 4330, lenIV);

    private static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle, int from = 0) =>
        from >= hay.Length ? -1 : hay[from..].IndexOf(needle) is int i and >= 0 ? i + from : -1;

    private static int? FindInt(byte[] d, string key)
    {
        int i = IndexOf(d, Encoding.ASCII.GetBytes(key));
        if (i < 0) return null;
        int p = i + key.Length;
        return ReadIntToken(d, ref p);
    }

    private static int? ReadIntToken(byte[] d, ref int p)
    {
        while (p < d.Length && (d[p] == ' ' || d[p] == '\t' || d[p] == '\r' || d[p] == '\n')) p++;
        int start = p;
        if (p < d.Length && (d[p] == '-' || d[p] == '+')) p++;
        while (p < d.Length && d[p] >= '0' && d[p] <= '9') p++;
        return p > start && int.TryParse(Encoding.ASCII.GetString(d, start, p - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    private static string ReadToken(byte[] d, ref int p)
    {
        while (p < d.Length && (d[p] == ' ' || d[p] == '\t' || d[p] == '\r' || d[p] == '\n')) p++;
        int start = p;
        while (p < d.Length && d[p] != ' ' && d[p] != '\t' && d[p] != '\r' && d[p] != '\n') p++;
        return Encoding.Latin1.GetString(d, start, p - start);
    }

    /// <summary>dup i n RD &lt;n bytes&gt; NP</summary>
    private static List<byte[]> ReadSubrs(byte[] d, int lenIV)
    {
        var subrs = new List<byte[]>();
        int i = IndexOf(d, "/Subrs"u8);
        if (i < 0) return subrs;
        int p = i + 6;
        int count = ReadIntToken(d, ref p) ?? 0;
        if (count < 0 || count > 65535) return subrs;
        for (int k = 0; k < count; k++) subrs.Add(Array.Empty<byte>());

        for (int k = 0; k < count; k++)
        {
            int dup = IndexOf(d, "dup"u8, p);
            if (dup < 0) break;
            int q = dup + 3;
            int? index = ReadIntToken(d, ref q);
            int? length = ReadIntToken(d, ref q);
            if (index == null || length == null || length < 0) break;
            ReadToken(d, ref q); // RD / -|
            q++; // single space before binary data
            if (q + length > d.Length) break;
            if (index >= 0 && index < count)
                subrs[index.Value] = DecryptCharString(d.AsSpan(q, length.Value), lenIV);
            p = q + length.Value;
            // Stop at the CharStrings dictionary.
            int nextDup = IndexOf(d, "dup"u8, p);
            int charStrings = IndexOf(d, "/CharStrings"u8, p);
            if (nextDup < 0 || (charStrings >= 0 && charStrings < nextDup)) break;
        }
        return subrs;
    }

    /// <summary>/name n RD &lt;n bytes&gt; ND</summary>
    private static Dictionary<string, byte[]> ReadCharStrings(byte[] d, int lenIV)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int i = IndexOf(d, "/CharStrings"u8);
        if (i < 0) return result;
        int p = i + 12;
        ReadIntToken(d, ref p);
        int begin = IndexOf(d, "begin"u8, p);
        if (begin < 0) return result;
        p = begin + 5;

        while (p < d.Length && result.Count < MaxGlyphs)
        {
            while (p < d.Length && d[p] != '/' && !(d[p] == 'e' && d.AsSpan(p).StartsWith("end"u8))) p++;
            if (p >= d.Length || d[p] != '/') break;
            p++;
            int nameStart = p;
            while (p < d.Length && d[p] != ' ' && d[p] != '\t' && d[p] != '\r' && d[p] != '\n') p++;
            string name = Encoding.Latin1.GetString(d, nameStart, p - nameStart);
            int? length = ReadIntToken(d, ref p);
            if (length == null || length < 0) break;
            ReadToken(d, ref p); // RD
            p++;
            if (p + length > d.Length) break;
            result[name] = DecryptCharString(d.AsSpan(p, length.Value), lenIV);
            p += length.Value;
        }
        return result;
    }

    private static string? FindName(string text, string key)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int s = text.IndexOf('/', i + key.Length);
        if (s < 0) return null;
        int e = s + 1;
        while (e < text.Length && !char.IsWhiteSpace(text[e]) && text[e] is not ('/' or '[' or '{' or '(')) e++;
        return text[(s + 1)..e];
    }

    private static double[]? FindNumbers(string text, string key, int count)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int open = text.IndexOfAny(new[] { '[', '{' }, i);
        int close = open < 0 ? -1 : text.IndexOfAny(new[] { ']', '}' }, open);
        if (open < 0 || close < 0) return null;
        var parts = text[(open + 1)..close].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < count) return null;
        var v = new double[count];
        for (int k = 0; k < count; k++)
        {
            if (!double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k])) return null;
        }
        return v;
    }

    /// <summary>Built-in encoding from the clear-text part ("StandardEncoding" or dup code /name put).</summary>
    private static string?[] ReadEncoding(string text)
    {
        var enc = new string?[256];
        int i = text.IndexOf("/Encoding", StringComparison.Ordinal);
        if (i < 0) return enc;
        int standard = text.IndexOf("StandardEncoding", i, StringComparison.Ordinal);
        int firstDup = text.IndexOf("dup", i, StringComparison.Ordinal);
        if (standard >= 0 && (firstDup < 0 || standard < firstDup))
        {
            for (int c = 0; c < 256; c++)
            {
                int sid = CffStandardStrings.StandardEncoding[c];
                if (sid > 0) enc[c] = CffStandardStrings.Names[sid];
            }
            return enc;
        }

        int end = text.IndexOf("readonly def", i, StringComparison.Ordinal);
        if (end < 0) end = text.IndexOf(" def", i, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        int p = i;
        while (true)
        {
            int dup = text.IndexOf("dup ", p, StringComparison.Ordinal);
            if (dup < 0 || dup > end) break;
            var parts = text.Substring(dup + 4, Math.Min(64, text.Length - dup - 4)).Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) &&
                code is >= 0 and < 256 && parts[1].StartsWith('/'))
            {
                enc[code] = parts[1][1..];
            }
            p = dup + 4;
        }
        return enc;
    }

    // ------------------------------------------------------------------ charstring interpretation

    /// <summary>
    /// Interprets Type 1 charstrings into an absolute outline, then emits an equivalent Type 2
    /// charstring (rmoveto/rlineto/rrcurveto/endchar only).
    /// </summary>
    private sealed class Interpreter
    {
        private readonly List<byte[]> _subrs;
        private readonly Dictionary<string, byte[]> _charStrings;
        private readonly List<(char Op, double[] P)> _path = new();
        private readonly List<double> _stack = new();
        private readonly Stack<double> _psStack = new();
        private readonly List<(double X, double Y)> _flex = new();
        private bool _inFlex;
        private double _x, _y;
        private double _offsetX, _offsetY;
        private int _ops;

        public Interpreter(List<byte[]> subrs, Dictionary<string, byte[]> charStrings)
        {
            _subrs = subrs;
            _charStrings = charStrings;
        }

        public void Run(byte[] cs, double offsetX, double offsetY, int depth)
        {
            _offsetX = offsetX;
            _offsetY = offsetY;
            Execute(cs, depth);
        }

        private void MoveTo(double x, double y)
        {
            _x = x; _y = y;
            if (_inFlex) { _flex.Add((x, y)); return; }
            _path.Add(('M', new[] { x + _offsetX, y + _offsetY }));
        }

        private void LineTo(double x, double y)
        {
            _x = x; _y = y;
            _path.Add(('L', new[] { x + _offsetX, y + _offsetY }));
        }

        private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            _x = x3; _y = y3;
            _path.Add(('C', new[] { x1 + _offsetX, y1 + _offsetY, x2 + _offsetX, y2 + _offsetY, x3 + _offsetX, y3 + _offsetY }));
        }

        /// <summary>Returns true when the glyph ended (endchar / seac).</summary>
        private bool Execute(byte[] cs, int depth)
        {
            if (depth > MaxSubrDepth)
                throw new InvalidDataException("Type1: subroutine nesting too deep");

            int p = 0;
            while (p < cs.Length)
            {
                if (++_ops > MaxOpsPerGlyph)
                    throw new InvalidDataException("Type1: charstring too long");

                int v = cs[p++];
                if (v >= 32)
                {
                    if (v <= 246) _stack.Add(v - 139);
                    else if (v <= 250) _stack.Add((v - 247) * 256 + cs[p++] + 108);
                    else if (v <= 254) _stack.Add(-(v - 251) * 256 - cs[p++] - 108);
                    else { _stack.Add((cs[p] << 24) | (cs[p + 1] << 16) | (cs[p + 2] << 8) | cs[p + 3]); p += 4; }
                    if (_stack.Count > 48) throw new InvalidDataException("Type1: operand stack overflow");
                    continue;
                }

                switch (v)
                {
                    case 1: case 3: _stack.Clear(); break;                 // hstem, vstem
                    case 4: MoveTo(_x, _y + Arg(0)); _stack.Clear(); break; // vmoveto
                    case 5: LineTo(_x + Arg(0), _y + Arg(1)); _stack.Clear(); break;
                    case 6: LineTo(_x + Arg(0), _y); _stack.Clear(); break;
                    case 7: LineTo(_x, _y + Arg(0)); _stack.Clear(); break;
                    case 8:
                    {
                        double x1 = _x + Arg(0), y1 = _y + Arg(1), x2 = x1 + Arg(2), y2 = y1 + Arg(3), x3 = x2 + Arg(4), y3 = y2 + Arg(5);
                        CurveTo(x1, y1, x2, y2, x3, y3);
                        _stack.Clear();
                        break;
                    }
                    case 9: _path.Add(('Z', Array.Empty<double>())); _stack.Clear(); break; // closepath
                    case 10:
                    {
                        int index = (int)Pop();
                        if (index >= 0 && index < _subrs.Count && Execute(_subrs[index], depth + 1))
                            return true;
                        break;
                    }
                    case 11: return false; // return
                    case 13: // hsbw sbx wx
                        _x = Arg(0); _y = 0;
                        _stack.Clear();
                        break;
                    case 14: return true;   // endchar
                    case 21: MoveTo(_x + Arg(0), _y + Arg(1)); _stack.Clear(); break;
                    case 22: MoveTo(_x + Arg(0), _y); _stack.Clear(); break;
                    case 30:
                    {
                        double x1 = _x, y1 = _y + Arg(0), x2 = x1 + Arg(1), y2 = y1 + Arg(2), x3 = x2 + Arg(3), y3 = y2;
                        CurveTo(x1, y1, x2, y2, x3, y3);
                        _stack.Clear();
                        break;
                    }
                    case 31:
                    {
                        double x1 = _x + Arg(0), y1 = _y, x2 = x1 + Arg(1), y2 = y1 + Arg(2), x3 = x2, y3 = y2 + Arg(3);
                        CurveTo(x1, y1, x2, y2, x3, y3);
                        _stack.Clear();
                        break;
                    }
                    case 12:
                    {
                        int op = cs[p++];
                        switch (op)
                        {
                            case 0: case 1: case 2: _stack.Clear(); break; // dotsection, vstem3, hstem3
                            case 6: // seac asb adx ady bchar achar
                            {
                                double asb = Arg(0), adx = Arg(1), ady = Arg(2);
                                int bchar = (int)Arg(3), achar = (int)Arg(4);
                                _stack.Clear();
                                Seac(asb, adx, ady, bchar, achar, depth);
                                return true;
                            }
                            case 7: // sbw sbx sby wx wy
                                _x = Arg(0); _y = Arg(1);
                                _stack.Clear();
                                break;
                            case 12: // div
                            {
                                double b = Pop(), a = Pop();
                                _stack.Add(b == 0 ? 0 : a / b);
                                break;
                            }
                            case 16: CallOtherSubr(); break;
                            case 17: _stack.Add(_psStack.Count > 0 ? _psStack.Pop() : 0); break; // pop
                            case 33: // setcurrentpoint
                                _x = Arg(0); _y = Arg(1);
                                _stack.Clear();
                                break;
                            default: _stack.Clear(); break;
                        }
                        break;
                    }
                    default:
                        _stack.Clear();
                        break;
                }
            }
            return false;
        }

        private double Arg(int i) => i < _stack.Count ? _stack[i] : 0;

        private double Pop()
        {
            if (_stack.Count == 0) return 0;
            double v = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return v;
        }

        /// <summary>OtherSubrs 0–3 per the Type 1 spec (flex and hint replacement); others pass args through.</summary>
        private void CallOtherSubr()
        {
            int othersubr = (int)Pop();
            int n = (int)Pop();
            var args = new double[Math.Clamp(n, 0, _stack.Count)];
            for (int i = args.Length - 1; i >= 0; i--) args[i] = Pop();

            switch (othersubr)
            {
                case 1: // start flex
                    _inFlex = true;
                    _flex.Clear();
                    break;
                case 2: // add flex point (the preceding rmoveto already recorded it)
                    break;
                case 0: // end flex: args = flexheight, endX, endY
                    _inFlex = false;
                    // Points: [0] reference, [1..6] two curves' control and end points.
                    if (_flex.Count >= 7)
                    {
                        CurveTo(_flex[1].X, _flex[1].Y, _flex[2].X, _flex[2].Y, _flex[3].X, _flex[3].Y);
                        CurveTo(_flex[4].X, _flex[4].Y, _flex[5].X, _flex[5].Y, _flex[6].X, _flex[6].Y);
                    }
                    else if (_flex.Count > 0)
                    {
                        LineTo(_flex[^1].X, _flex[^1].Y);
                    }
                    _flex.Clear();
                    // The following "pop pop setcurrentpoint" expects the end point.
                    if (args.Length >= 3)
                    {
                        _psStack.Push(args[2]);
                        _psStack.Push(args[1]);
                    }
                    break;
                case 3: // hint replacement unsupported: return 3, so "pop callsubr" runs the no-op Subrs[3]
                    _psStack.Push(3);
                    break;
                default:
                    for (int i = args.Length - 1; i >= 0; i--) _psStack.Push(args[i]);
                    break;
            }
        }

        private void Seac(double asb, double adx, double ady, int bchar, int achar, int depth)
        {
            string? baseName = StandardName(bchar), accentName = StandardName(achar);
            if (baseName != null && _charStrings.TryGetValue(baseName, out var baseCs))
            {
                var baseInterp = new Interpreter(_subrs, _charStrings);
                baseInterp.Run(baseCs, _offsetX, _offsetY, depth + 1);
                _path.AddRange(baseInterp._path);
            }
            if (accentName != null && _charStrings.TryGetValue(accentName, out var accentCs))
            {
                // The accent's own hsbw re-establishes its sidebearing; shift its origin by adx − asb.
                var accentInterp = new Interpreter(_subrs, _charStrings);
                accentInterp.Run(accentCs, _offsetX + adx - asb, _offsetY + ady, depth + 1);
                _path.AddRange(accentInterp._path);
            }
        }

        private static string? StandardName(int code)
        {
            if (code < 0 || code > 255) return null;
            int sid = CffStandardStrings.StandardEncoding[code];
            return sid > 0 ? CffStandardStrings.Names[sid] : null;
        }

        /// <param name="transform">Optional glyph-space → 1000-unit matrix (FontMatrix × 1000).</param>
        public byte[] EmitType2(double[]? transform = null)
        {
            using var ms = new MemoryStream();
            double cx = 0, cy = 0;
            foreach (var (op, raw) in _path)
            {
                var pts = raw;
                if (transform != null && raw.Length > 0)
                {
                    pts = new double[raw.Length];
                    for (int i = 0; i + 1 < raw.Length; i += 2)
                    {
                        pts[i] = transform[0] * raw[i] + transform[2] * raw[i + 1] + transform[4];
                        pts[i + 1] = transform[1] * raw[i] + transform[3] * raw[i + 1] + transform[5];
                    }
                }
                switch (op)
                {
                    case 'M':
                        Num(ms, pts[0] - cx); Num(ms, pts[1] - cy); ms.WriteByte(21);
                        cx = pts[0]; cy = pts[1];
                        break;
                    case 'L':
                        Num(ms, pts[0] - cx); Num(ms, pts[1] - cy); ms.WriteByte(5);
                        cx = pts[0]; cy = pts[1];
                        break;
                    case 'C':
                        Num(ms, pts[0] - cx); Num(ms, pts[1] - cy);
                        Num(ms, pts[2] - pts[0]); Num(ms, pts[3] - pts[1]);
                        Num(ms, pts[4] - pts[2]); Num(ms, pts[5] - pts[3]);
                        ms.WriteByte(8);
                        cx = pts[4]; cy = pts[5];
                        break;
                    // 'Z': Type 2 closes subpaths implicitly at the next moveto / endchar.
                }
            }
            ms.WriteByte(14);
            return ms.ToArray();
        }

        private static void Num(Stream s, double v)
        {
            double r = Math.Round(v);
            if (Math.Abs(v - r) < 1e-6 && r >= -32768 && r <= 32767)
            {
                int i = (int)r;
                if (i is >= -107 and <= 107) s.WriteByte((byte)(i + 139));
                else if (i is >= 108 and <= 1131) { i -= 108; s.WriteByte((byte)((i >> 8) + 247)); s.WriteByte((byte)i); }
                else if (i is >= -1131 and <= -108) { i = -i - 108; s.WriteByte((byte)((i >> 8) + 251)); s.WriteByte((byte)i); }
                else { s.WriteByte(28); BigEndian.W16(s, i); }
                return;
            }
            // 16.16 fixed (Type 2 operand 255).
            int fixedValue = (int)Math.Round(Math.Clamp(v, -32768, 32767) * 65536);
            s.WriteByte(255);
            BigEndian.W32(s, unchecked((uint)fixedValue));
        }
    }
}
