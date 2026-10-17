using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Text;

/// <summary>
/// The control box of CFF (Type 2) glyph outlines, in glyph units: every point of the outline,
/// curve control points included, as FreeType measures a glyph loaded without scaling. Text
/// extraction needs it for character boxes and to tell blank glyphs from drawn ones; nothing is
/// rasterized. Hints are skipped; arithmetic and storage operators are not supported (rare in
/// fonts) and end the glyph with what was measured so far.
/// </summary>
internal sealed class CffGlyphBounds
{
    private readonly byte[] _d;
    private readonly CffFont _font;
    private readonly List<CffFont.IndexEntry> _charStrings;
    private readonly List<CffFont.IndexEntry> _globalSubrs;
    private readonly List<CffFont.IndexEntry> _localSubrs;
    private readonly List<CffFont.IndexEntry>[]? _fdSubrs;
    private readonly byte[]? _fdSelect;

    private CffGlyphBounds(byte[] d, CffFont font, List<CffFont.IndexEntry> charStrings, List<CffFont.IndexEntry> globalSubrs,
        List<CffFont.IndexEntry> localSubrs, List<CffFont.IndexEntry>[]? fdSubrs, byte[]? fdSelect)
    {
        _d = d; _font = font; _charStrings = charStrings; _globalSubrs = globalSubrs; _localSubrs = localSubrs; _fdSubrs = fdSubrs; _fdSelect = fdSelect;
    }

    public static CffGlyphBounds? TryCreate(byte[] data, CffFont font)
    {
        try
        {
            int pos = data[2];
            CffFont.ReadIndex(data, ref pos); // names
            var tops = CffFont.ReadIndex(data, ref pos);
            CffFont.ReadIndex(data, ref pos); // strings
            var gsubrs = CffFont.ReadIndex(data, ref pos);
            if (tops.Count == 0) return null;
            var top = CffFont.ParseDict(data, tops[0].Offset, tops[0].Length);
            if (!top.TryGetValue(17, out var cs) || cs.Length < 1) return null;
            int csPos = (int)cs[0];
            var charStrings = CffFont.ReadIndex(data, ref csPos);
            var local = top.TryGetValue(18, out var pv) ? PrivateSubrs(data, pv) : new List<CffFont.IndexEntry>();

            List<CffFont.IndexEntry>[]? fdSubrs = null;
            byte[]? fdSelect = null;
            if (top.TryGetValue(1236, out var fa) && fa.Length >= 1 && top.TryGetValue(1237, out var fs) && fs.Length >= 1)
            {
                int p = (int)fa[0];
                var dicts = CffFont.ReadIndex(data, ref p);
                fdSubrs = new List<CffFont.IndexEntry>[dicts.Count];
                for (int i = 0; i < dicts.Count; i++)
                {
                    var fd = CffFont.ParseDict(data, dicts[i].Offset, dicts[i].Length);
                    fdSubrs[i] = fd.TryGetValue(18, out var fpv) ? PrivateSubrs(data, fpv) : new List<CffFont.IndexEntry>();
                }
                fdSelect = ReadFdSelect(data, (int)fs[0], charStrings.Count);
            }
            return new CffGlyphBounds(data, font, charStrings, gsubrs, local, fdSubrs, fdSelect);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or System.IO.InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static List<CffFont.IndexEntry> PrivateSubrs(byte[] d, double[] pv)
    {
        if (pv.Length < 2 || pv[0] <= 0 || pv[1] < 0 || pv[1] + pv[0] > d.Length) return new();
        var priv = CffFont.ParseDict(d, (int)pv[1], (int)pv[0]);
        if (!priv.TryGetValue(19, out var so) || so.Length < 1) return new();
        int p = (int)(pv[1] + so[0]);
        if (p <= 0 || p >= d.Length) return new();
        return CffFont.ReadIndex(d, ref p);
    }

    private static byte[]? ReadFdSelect(byte[] d, int q, int glyphs)
    {
        var select = new byte[glyphs];
        int format = d[q++];
        if (format == 0)
        {
            for (int g = 0; g < glyphs; g++) select[g] = d[q + g];
            return select;
        }
        if (format != 3) return null;
        int ranges = (d[q] << 8) | d[q + 1];
        q += 2;
        for (int r = 0; r < ranges; r++, q += 3)
        {
            int first = (d[q] << 8) | d[q + 1], next = (d[q + 3] << 8) | d[q + 4];
            for (int g = first; g < next && g < glyphs; g++) select[g] = d[q + 2];
        }
        return select;
    }

    internal string DumpCharString(int gid)
    {
        var cs = _charStrings[gid];
        return string.Join(" ", _d.Skip(cs.Offset).Take(cs.Length).Select(b => b.ToString()));
    }

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <summary>The glyph's control box (xMin, yMin, xMax, yMax) in glyph units; null for a blank glyph or one that cannot be read.</summary>
    public (double XMin, double YMin, double XMax, double YMax)? Bounds(int gid)
    {
        if (gid < 0 || gid >= _charStrings.Count) return null;
        var run = new Run(this, gid);
        try { run.Glyph(gid, 0, 0, 0); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException) { }
        return run.Any ? (run.XMin, run.YMin, run.XMax, run.YMax) : null;
    }

    private sealed class Run
    {
        private readonly CffGlyphBounds _o;
        private readonly List<CffFont.IndexEntry> _local;
        private readonly double[] _stack = new double[48];
        private int _sp, _stems, _depth;
        private double _x, _y, _dx, _dy;
        private bool _widthDone, _ended;
        public bool Any;
        public double XMin = double.MaxValue, YMin = double.MaxValue, XMax = double.MinValue, YMax = double.MinValue;

        public Run(CffGlyphBounds o, int gid)
        {
            _o = o;
            _local = o._fdSubrs != null && o._fdSelect != null && gid < o._fdSelect.Length && o._fdSelect[gid] < o._fdSubrs.Length
                ? o._fdSubrs[o._fdSelect[gid]] : o._localSubrs;
        }

        private void Point(double x, double y)
        {
            x += _dx; y += _dy;
            Any = true;
            if (x < XMin) XMin = x;
            if (x > XMax) XMax = x;
            if (y < YMin) YMin = y;
            if (y > YMax) YMax = y;
        }

        // A contour starts where a moveto left the pen, once something is drawn from there.
        private bool _open;

        private void Start()
        {
            if (_open) return;
            _open = true;
            Point(_x, _y);
        }

        private void Line(double dx, double dy) { Start(); _x += dx; _y += dy; Point(_x, _y); }

        private void Curve(double a, double b, double c, double d, double e, double f)
        {
            Start();
            _x += a; _y += b; Point(_x, _y);
            _x += c; _y += d; Point(_x, _y);
            _x += e; _y += f; Point(_x, _y);
        }

        private void Width(int expectedArgs)
        {
            if (_widthDone) return;
            _widthDone = true;
            if (_sp > expectedArgs) { Array.Copy(_stack, 1, _stack, 0, _sp - 1); _sp--; }
        }

        public void Glyph(int gid, double dx, double dy, int seacDepth)
        {
            _dx = dx; _dy = dy; _x = 0; _y = 0; _sp = 0; _stems = 0; _widthDone = false; _ended = false; _open = false;
            var cs = _o._charStrings[gid];
            Execute(cs.Offset, cs.Offset + cs.Length, seacDepth);
        }

        private void Execute(int p, int end, int seacDepth)
        {
            if (++_depth > 12) throw new InvalidOperationException("subroutine depth");
            var d = _o._d;
            try
            {
                while (p < end && !_ended)
                {
                    int b0 = d[p];
                    if (b0 >= 32 || b0 == 28)
                    {
                        double v;
                        if (b0 == 28) { v = (short)((d[p + 1] << 8) | d[p + 2]); p += 3; }
                        else if (b0 == 255) { v = ((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]) / 65536.0; p += 5; }
                        else if (b0 <= 246) { v = b0 - 139; p++; }
                        else if (b0 <= 250) { v = (b0 - 247) * 256 + d[p + 1] + 108; p += 2; }
                        else { v = -(b0 - 251) * 256 - d[p + 1] - 108; p += 2; }
                        if (_sp < _stack.Length) _stack[_sp++] = v;
                        continue;
                    }
                    p++;
                    switch (b0)
                    {
                        case 1: case 3: case 18: case 23: // stems
                            if (!_widthDone && _sp % 2 == 1) Width(_sp - 1);
                            _widthDone = true;
                            _stems += _sp / 2; _sp = 0;
                            break;
                        case 19: case 20: // hintmask, cntrmask (implicit vstems before)
                            if (!_widthDone && _sp % 2 == 1) Width(_sp - 1);
                            _widthDone = true;
                            _stems += _sp / 2; _sp = 0;
                            p += (_stems + 7) / 8;
                            break;
                        case 21: Width(2); _x += Arg(0); _y += Arg(1); _sp = 0; _open = false; break; // rmoveto
                        case 22: Width(1); _x += Arg(0); _sp = 0; _open = false; break; // hmoveto
                        case 4: Width(1); _y += Arg(0); _sp = 0; _open = false; break; // vmoveto
                        case 5: for (int i = 0; i + 1 < _sp; i += 2) Line(_stack[i], _stack[i + 1]); _sp = 0; break;
                        case 6: case 7: // hlineto, vlineto
                        {
                            bool h = b0 == 6;
                            for (int i = 0; i < _sp; i++, h = !h) Line(h ? _stack[i] : 0, h ? 0 : _stack[i]);
                            _sp = 0;
                            break;
                        }
                        case 8: for (int i = 0; i + 5 < _sp; i += 6) Curve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]); _sp = 0; break;
                        case 24: // rcurveline
                        {
                            int i = 0;
                            for (; i + 5 < _sp - 2; i += 6) Curve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            if (i + 1 < _sp) Line(_stack[i], _stack[i + 1]);
                            _sp = 0;
                            break;
                        }
                        case 25: // rlinecurve
                        {
                            int i = 0;
                            for (; i + 1 < _sp - 6; i += 2) Line(_stack[i], _stack[i + 1]);
                            if (i + 5 < _sp) Curve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            _sp = 0;
                            break;
                        }
                        case 26: // vvcurveto
                        {
                            int i = 0; double dx1 = 0;
                            if (_sp % 2 == 1) dx1 = _stack[i++];
                            for (; i + 3 < _sp; i += 4) { Curve(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]); dx1 = 0; }
                            _sp = 0;
                            break;
                        }
                        case 27: // hhcurveto
                        {
                            int i = 0; double dy1 = 0;
                            if (_sp % 2 == 1) dy1 = _stack[i++];
                            for (; i + 3 < _sp; i += 4) { Curve(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0); dy1 = 0; }
                            _sp = 0;
                            break;
                        }
                        case 30: case 31: // vhcurveto, hvcurveto
                        {
                            bool h = b0 == 31;
                            int i = 0;
                            while (i + 3 < _sp)
                            {
                                bool last = i + 4 >= _sp - 1;
                                double extra = last && _sp - i == 5 ? _stack[i + 4] : 0;
                                if (h) Curve(_stack[i], 0, _stack[i + 1], _stack[i + 2], extra, _stack[i + 3]);
                                else Curve(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], extra);
                                h = !h;
                                i += 4;
                            }
                            _sp = 0;
                            break;
                        }
                        case 10: case 29: // callsubr, callgsubr
                        {
                            if (_sp == 0) return;
                            var subrs = b0 == 10 ? _local : _o._globalSubrs;
                            int index = (int)_stack[--_sp] + Bias(subrs.Count);
                            if (index < 0 || index >= subrs.Count) return;
                            Execute(subrs[index].Offset, subrs[index].Offset + subrs[index].Length, seacDepth);
                            break;
                        }
                        case 11: return; // return
                        case 14: // endchar (with four operands: an accented character)
                        {
                            Width(4);
                            if (_sp >= 4 && seacDepth == 0)
                            {
                                double adx = _stack[0], ady = _stack[1];
                                int bchar = (int)_stack[2], achar = (int)_stack[3];
                                int bg = StandardGid(bchar), ag = StandardGid(achar);
                                _ended = true;
                                if (bg >= 0) { var save = _depth; _depth = 0; Glyph(bg, 0, 0, 1); _depth = save; }
                                if (ag >= 0) { var save = _depth; _depth = 0; Glyph(ag, adx, ady, 1); _depth = save; }
                            }
                            _ended = true;
                            return;
                        }
                        case 12:
                        {
                            int op = d[p++];
                            switch (op)
                            {
                                case 35: // flex
                                    if (_sp >= 12) { Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]); Curve(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]); }
                                    break;
                                case 34: // hflex
                                    if (_sp >= 7)
                                    {
                                        double y0 = _y;
                                        Curve(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                                        Curve(_stack[4], 0, _stack[5], y0 - _y, _stack[6], 0);
                                    }
                                    break;
                                case 36: // hflex1
                                    if (_sp >= 9)
                                    {
                                        Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                                        Curve(_stack[5], 0, _stack[6], _stack[7], _stack[8], -(_stack[1] + _stack[3] + _stack[7]));
                                    }
                                    break;
                                case 37: // flex1
                                    if (_sp >= 11)
                                    {
                                        double sx = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
                                        double sy = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];
                                        bool horizontal = Math.Abs(sx) > Math.Abs(sy);
                                        Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                                        Curve(_stack[6], _stack[7], _stack[8], _stack[9], horizontal ? _stack[10] : -sx, horizontal ? -sy : _stack[10]);
                                    }
                                    break;
                                case 0: // dotsection (deprecated hint)
                                    break;
                                default:
                                    if (Arithmetic(op)) continue; // the result stays on the stack
                                    break;
                            }
                            _sp = 0;
                            break;
                        }
                        default:
                            _sp = 0;
                            break;
                    }
                }
            }
            finally { _depth--; }
        }

        private double Arg(int i) => i < _sp ? _stack[i] : 0;

        private readonly double[] _transient = new double[32];

        /// <summary>Type 2 arithmetic and storage operators; false for an operator that is not one.</summary>
        private bool Arithmetic(int op)
        {
            double Pop() => _sp > 0 ? _stack[--_sp] : 0;
            void Push(double v) { if (_sp < _stack.Length) _stack[_sp++] = v; }
            switch (op)
            {
                case 3: { double b = Pop(), a = Pop(); Push(a != 0 && b != 0 ? 1 : 0); return true; }
                case 4: { double b = Pop(), a = Pop(); Push(a != 0 || b != 0 ? 1 : 0); return true; }
                case 5: Push(Pop() == 0 ? 1 : 0); return true;
                case 9: Push(Math.Abs(Pop())); return true;
                case 10: { double b = Pop(), a = Pop(); Push(a + b); return true; }
                case 11: { double b = Pop(), a = Pop(); Push(a - b); return true; }
                case 12: { double b = Pop(), a = Pop(); Push(b == 0 ? 0 : a / b); return true; }
                case 14: Push(-Pop()); return true;
                case 15: { double b = Pop(), a = Pop(); Push(a == b ? 1 : 0); return true; }
                case 18: Pop(); return true;
                case 20: { double i = Pop(), v = Pop(); if (i >= 0 && i < _transient.Length) _transient[(int)i] = v; return true; }
                case 21: { double i = Pop(); Push(i >= 0 && i < _transient.Length ? _transient[(int)i] : 0); return true; }
                case 22: { double v2 = Pop(), v1 = Pop(), s2 = Pop(), s1 = Pop(); Push(v1 <= v2 ? s1 : s2); return true; }
                case 23: Push(0.5); return true;
                case 24: { double b = Pop(), a = Pop(); Push(a * b); return true; }
                case 26: Push(Math.Sqrt(Math.Max(0, Pop()))); return true;
                case 27: { double a = Pop(); Push(a); Push(a); return true; }
                case 28: { double b = Pop(), a = Pop(); Push(b); Push(a); return true; }
                case 29:
                {
                    int i = (int)Pop();
                    Push(i >= 0 && i < _sp ? _stack[_sp - 1 - i] : _sp > 0 ? _stack[_sp - 1] : 0);
                    return true;
                }
                case 30:
                {
                    int j = (int)Pop(), n = (int)Pop();
                    if (n > 0 && n <= _sp)
                    {
                        var part = _stack.AsSpan(_sp - n, n).ToArray();
                        for (int k = 0; k < n; k++) _stack[_sp - n + (((k + j) % n + n) % n)] = part[k];
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        private int StandardGid(int code)
        {
            if (code < 0 || code >= CffStandardStrings.StandardEncoding.Length) return -1;
            int sid = CffStandardStrings.StandardEncoding[code];
            if (sid <= 0 || sid >= CffStandardStrings.Names.Length) return -1;
            int g = _o._font.GidForName(CffStandardStrings.Names[sid]);
            return g > 0 ? g : -1;
        }
    }
}
