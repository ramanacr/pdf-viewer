using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Images;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;
using static PdfEngine.Vector.Redaction.RedactionGeometry;

namespace PdfEngine.Vector.Redaction;

/// <summary>
/// Rewrites a content stream without what lies under the redaction rectangles: glyphs removed
/// (the rest keep their exact positions through TJ adjustments), filled and stroked paths cut,
/// image pixels blanked (in a new image object), form XObjects rewritten recursively, shadings
/// clipped away, and /ActualText or /Alt around removed text dropped. Everything else is copied
/// operator for operator.
/// </summary>
internal sealed class ContentRedactor
{
    private readonly PdfVectorDocument _doc;
    private readonly PdfObjectResolver _r;
    private readonly IReadOnlyList<PdfRect> _rects;
    private readonly PdfFontResolver _fonts;
    private readonly PdfStreamDecoder _decoder;
    private readonly PdfRedactionOptions _options;
    private readonly PdfRedactionResult _result;
    private readonly Func<int> _allocate;
    private readonly Dictionary<int, PdfObject> _newObjects;
    private readonly HashSet<int> _flaggedMcids;
    private int _depth;

    public ContentRedactor(PdfVectorDocument doc, IReadOnlyList<PdfRect> rects, PdfRedactionOptions options, PdfRedactionResult result,
        Func<int> allocate, Dictionary<int, PdfObject> newObjects, HashSet<int> flaggedMcids)
    {
        _doc = doc;
        _r = doc.Resolver;
        _rects = rects;
        _fonts = new PdfFontResolver(_r, PdfSecurityLimits.Default);
        _decoder = new PdfStreamDecoder(PdfSecurityLimits.Default, _r.Resolve);
        _options = options;
        _result = result;
        _allocate = allocate;
        _newObjects = newObjects;
        _flaggedMcids = flaggedMcids;
    }

    private sealed class State
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public string? FontName;
        public PdfFont? Font;
        public double FontSize = 1, Tc, Tw, Th = 100, TL, Rise;
        public double LineWidth = 1;
        public State Clone() => (State)MemberwiseClone();
    }

    private sealed class Subpath
    {
        public List<P> Points = new();
        public bool Closed;
    }

    /// <summary>The rewritten content, and the resources it needs (a copy when XObjects were replaced).</summary>
    public (byte[] Content, PdfDictionary Resources, bool Changed) Rewrite(byte[] content, PdfDictionary resources, PdfMatrix baseCtm)
    {
        if (++_depth > 12) { _depth--; return (content, resources, false); }
        try
        {
            return RewriteCore(content, resources, baseCtm);
        }
        finally
        {
            _depth--;
        }
    }

    private (byte[] Content, PdfDictionary Resources, bool Changed) RewriteCore(byte[] content, PdfDictionary resources, PdfMatrix baseCtm)
    {
        var output = new List<string>();
        bool changed = false;
        var gs = new State { Ctm = baseCtm };
        var stack = new Stack<State>();
        PdfMatrix tm = PdfMatrix.Identity, tlm = PdfMatrix.Identity;
        var operands = new List<PdfObject>(8);
        var operandText = new StringBuilder();
        var pathRaw = new StringBuilder();
        var subpaths = new List<Subpath>();
        Subpath? current = null;
        P userPoint = default, userStart = default;
        string? pendingClip = null;
        var addedXObjects = new Dictionary<string, PdfObject>();
        var replacedXObjects = new HashSet<string>();
        var keptXObjects = new HashSet<string>();
        // Marked content: (index of the BDC in output, inline properties, removed text inside, MCID).
        var marked = new Stack<(int Index, PdfDictionary? Props, bool Removed, int Mcid, string Tag)>();

        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);

        P ToPage(double x, double y) { var p = gs.Ctm.Transform(x, y); return new P(p.X, p.Y); }

        void Emit(string s) => output.Add(s);
        string Ops() => operandText.ToString();

        while (true)
        {
            long tokenPos = src.Position;
            var token = lexer.NextToken();
            if (token.Type == PdfTokenType.EndOfFile) break;
            if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
            {
                src.Position = tokenPos;
                var obj = token.Type == PdfTokenType.Keyword
                    ? (token.TextValue == "null" ? PdfNull.Instance : token.TextValue == "true" ? PdfBoolean.True : PdfBoolean.False)
                    : parser.ParseObject(lexer);
                if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                if (obj != null)
                {
                    operands.Add(obj);
                    operandText.Append(Serialize(obj)).Append(' ');
                }
                else if (src.Position == tokenPos) src.Position = tokenPos + 1;
                continue;
            }

            string op = token.TextValue;
            string verbatim = Ops() + op;
            switch (op)
            {
                case "q":
                    stack.Push(gs.Clone());
                    Emit(verbatim);
                    break;
                case "Q":
                    if (stack.Count > 0) gs = stack.Pop();
                    Emit(verbatim);
                    break;
                case "cm" when Numbers(operands, 6) is { } m:
                    gs.Ctm = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]) * gs.Ctm;
                    Emit(verbatim);
                    break;
                case "w" when Numbers(operands, 1) is { } lw:
                    gs.LineWidth = lw[0];
                    Emit(verbatim);
                    break;

                // ---------------------------------------------------------- text state
                case "BT":
                    tm = tlm = PdfMatrix.Identity;
                    Emit(verbatim);
                    break;
                case "Tf" when operands.Count >= 2 && operands[0] is PdfName fn && operands[1].TryGetNumber(out double fsize):
                    gs.FontName = fn.Value;
                    gs.FontSize = fsize;
                    gs.Font = null;
                    Emit(verbatim);
                    break;
                case "Tc" when Numbers(operands, 1) is { } tc: gs.Tc = tc[0]; Emit(verbatim); break;
                case "Tw" when Numbers(operands, 1) is { } tw: gs.Tw = tw[0]; Emit(verbatim); break;
                case "Tz" when Numbers(operands, 1) is { } tz: gs.Th = tz[0]; Emit(verbatim); break;
                case "TL" when Numbers(operands, 1) is { } tl: gs.TL = tl[0]; Emit(verbatim); break;
                case "Ts" when Numbers(operands, 1) is { } ts: gs.Rise = ts[0]; Emit(verbatim); break;
                case "Td" when Numbers(operands, 2) is { } td:
                    tlm = PdfMatrix.CreateTranslation(td[0], td[1]) * tlm; tm = tlm;
                    Emit(verbatim);
                    break;
                case "TD" when Numbers(operands, 2) is { } tdd:
                    gs.TL = -tdd[1];
                    tlm = PdfMatrix.CreateTranslation(tdd[0], tdd[1]) * tlm; tm = tlm;
                    Emit(verbatim);
                    break;
                case "Tm" when Numbers(operands, 6) is { } tmv:
                    tm = tlm = new PdfMatrix(tmv[0], tmv[1], tmv[2], tmv[3], tmv[4], tmv[5]);
                    Emit(verbatim);
                    break;
                case "T*":
                    tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                    Emit(verbatim);
                    break;
                case "Tj" when operands.Count >= 1 && operands[^1] is PdfString s:
                    { changed |= ShowText(gs, resources, ref tm, new PdfObject[] { s }, verbatim, string.Empty, marked, out var shown); Emit(shown); }
                    break;
                case "TJ" when operands.Count >= 1 && operands[^1] is PdfArray arr:
                    { changed |= ShowText(gs, resources, ref tm, arr.Items, verbatim, string.Empty, marked, out var shown); Emit(shown); }
                    break;
                case "'" when operands.Count >= 1 && operands[^1] is PdfString s1:
                    tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                    { changed |= ShowText(gs, resources, ref tm, new PdfObject[] { s1 }, verbatim, "T* ", marked, out var shown); Emit(shown); }
                    break;
                case "\"" when operands.Count >= 3 && operands[2] is PdfString s2 && operands[0].TryGetNumber(out double aw) && operands[1].TryGetNumber(out double ac):
                    gs.Tw = aw; gs.Tc = ac;
                    tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                    {
                        changed |= ShowText(gs, resources, ref tm, new PdfObject[] { s2 }, verbatim, $"{F(aw)} Tw {F(ac)} Tc T* ", marked, out var shown);
                        Emit(shown);
                    }
                    break;

                // ---------------------------------------------------------- paths
                case "m" when Numbers(operands, 2) is { } mv:
                    current = new Subpath();
                    subpaths.Add(current);
                    userPoint = userStart = new P(mv[0], mv[1]);
                    current.Points.Add(ToPage(mv[0], mv[1]));
                    pathRaw.Append(verbatim).Append('\n');
                    break;
                case "l" when Numbers(operands, 2) is { } lv:
                    if (current == null) { current = new Subpath(); subpaths.Add(current); current.Points.Add(ToPage(userPoint.X, userPoint.Y)); }
                    userPoint = new P(lv[0], lv[1]);
                    current.Points.Add(ToPage(lv[0], lv[1]));
                    pathRaw.Append(verbatim).Append('\n');
                    break;
                case "c" or "v" or "y" when Numbers(operands, op == "c" ? 6 : 4) is { } cv:
                {
                    if (current == null) { current = new Subpath(); subpaths.Add(current); current.Points.Add(ToPage(userPoint.X, userPoint.Y)); }
                    P p0 = userPoint, p1, p2, p3;
                    if (op == "c") { p1 = new(cv[0], cv[1]); p2 = new(cv[2], cv[3]); p3 = new(cv[4], cv[5]); }
                    else if (op == "v") { p1 = p0; p2 = new(cv[0], cv[1]); p3 = new(cv[2], cv[3]); }
                    else { p1 = new(cv[0], cv[1]); p2 = new(cv[2], cv[3]); p3 = p2; }
                    var flat = new List<P>();
                    Flatten(ToPage(p0.X, p0.Y), ToPage(p1.X, p1.Y), ToPage(p2.X, p2.Y), ToPage(p3.X, p3.Y), flat);
                    current.Points.AddRange(flat);
                    userPoint = p3;
                    pathRaw.Append(verbatim).Append('\n');
                    break;
                }
                case "h":
                    if (current != null) { current.Closed = true; userPoint = userStart; current = null; }
                    pathRaw.Append(verbatim).Append('\n');
                    break;
                case "re" when Numbers(operands, 4) is { } rv:
                {
                    var sp = new Subpath { Closed = true };
                    sp.Points.Add(ToPage(rv[0], rv[1]));
                    sp.Points.Add(ToPage(rv[0] + rv[2], rv[1]));
                    sp.Points.Add(ToPage(rv[0] + rv[2], rv[1] + rv[3]));
                    sp.Points.Add(ToPage(rv[0], rv[1] + rv[3]));
                    subpaths.Add(sp);
                    current = null;
                    userPoint = userStart = new P(rv[0], rv[1]);
                    pathRaw.Append(verbatim).Append('\n');
                    break;
                }
                case "W" or "W*":
                    pendingClip = op;
                    break;
                case "n" or "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*":
                    changed |= PaintPath(gs, op, pathRaw.ToString(), subpaths, pendingClip, Emit);
                    pathRaw.Clear();
                    subpaths.Clear();
                    current = null;
                    pendingClip = null;
                    break;

                // ---------------------------------------------------------- external objects
                case "Do" when operands.Count >= 1 && operands[^1] is PdfName xname:
                    if (DoXObject(gs, resources, xname.Value, verbatim, addedXObjects, Emit)) { changed = true; replacedXObjects.Add(xname.Value); }
                    else keptXObjects.Add(xname.Value);
                    break;
                case "sh":
                    if (_rects.Count > 0 && gs.Ctm.TryInvert(out var inv))
                    {
                        // The shading fills the clip; keep it out of the redacted areas.
                        Emit("q " + ExclusionClip(inv) + verbatim + " Q");
                        changed = true;
                    }
                    else Emit(verbatim);
                    break;
                case "BI":
                    changed |= InlineImage(gs, resources, src, lexer, parser, addedXObjects, Emit);
                    break;

                // ---------------------------------------------------------- marked content
                case "BDC":
                {
                    var props = operands.Count >= 2 ? operands[1] as PdfDictionary : null;
                    int mcid = props != null && props["MCID"] is PdfInteger mi ? (int)mi.Value : -1;
                    string tag = operands.Count >= 1 && operands[0] is PdfName tn ? tn.Value : "Span";
                    marked.Push((output.Count, props, false, mcid, tag));
                    Emit(verbatim);
                    break;
                }
                case "BMC":
                    marked.Push((output.Count, null, false, -1, operands.Count >= 1 && operands[0] is PdfName bn ? bn.Value : "Span"));
                    Emit(verbatim);
                    break;
                case "EMC":
                    if (marked.Count > 0)
                    {
                        var m = marked.Pop();
                        if (m.Removed)
                        {
                            if (m.Mcid >= 0) _flaggedMcids.Add(m.Mcid);
                            if (m.Props != null && (m.Props.ContainsKey("ActualText") || m.Props.ContainsKey("Alt") || m.Props.ContainsKey("E")))
                            {
                                var entries = new Dictionary<string, PdfObject>(m.Props.Entries);
                                entries.Remove("ActualText"); entries.Remove("Alt"); entries.Remove("E");
                                output[m.Index] = $"/{PdfObjectWriter.EscapeName(m.Tag)} {Serialize(new PdfDictionary(entries))} BDC";
                                _result.AlternateTextsRemoved++;
                            }
                            if (marked.Count > 0) { var parent = marked.Pop(); marked.Push(parent with { Removed = true }); }
                        }
                    }
                    Emit(verbatim);
                    break;

                default:
                    Emit(verbatim);
                    break;
            }
            operands.Clear();
            operandText.Clear();
        }

        var newResources = resources;
        if (addedXObjects.Count > 0 || replacedXObjects.Count > 0)
        {
            var res = new Dictionary<string, PdfObject>(resources.Entries);
            var xo = new Dictionary<string, PdfObject>((_r.Resolve(resources["XObject"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
            // An XObject replaced at every use leaves the resources: otherwise the original (the
            // unredacted image or form) would still be reachable, and written into the file.
            foreach (var name in replacedXObjects)
                if (!keptXObjects.Contains(name)) xo.Remove(name);
            foreach (var (k, v) in addedXObjects) xo[k] = v;
            res["XObject"] = new PdfDictionary(xo);
            newResources = new PdfDictionary(res);
        }
        return (Encoding.Latin1.GetBytes(string.Join("\n", output) + "\n"), newResources, changed);
    }

    // ------------------------------------------------------------------ text

    private bool ShowText(State gs, PdfDictionary resources, ref PdfMatrix tm, IReadOnlyList<PdfObject> items, string verbatim, string prefix,
        Stack<(int Index, PdfDictionary? Props, bool Removed, int Mcid, string Tag)> marked, out string shown)
    {
        var font = gs.Font;
        if (font == null && gs.FontName != null)
        {
            try { font = gs.Font = _fonts.ResolveFont(gs.FontName, resources); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { font = null; }
        }
        double fs = gs.FontSize, th = gs.Th / 100.0;
        bool vertical = font?.IsVertical ?? false;
        double ascent = font != null ? Math.Max(font.Ascent, 0.75) : 0.95, descent = font != null ? Math.Min(font.Descent, -0.2) : -0.3;
        var textToPage = tm * gs.Ctm;

        // Walk the glyphs exactly as they are shown, deciding which ones go.
        var pieces = new List<object>(); // byte[] kept code runs, or double adjustments (thousandths)
        var kept = new List<byte>();
        double pendingAdjust = 0, x = 0, y = 0;
        bool removedAny = false, keptAny = false;
        // Pieces in the order they occur: kept code runs, and the adjustments between them.
        void FlushKept()
        {
            if (kept.Count == 0) return;
            pieces.Add(kept.ToArray());
            kept.Clear();
        }
        void FlushAdjust()
        {
            if (Math.Abs(pendingAdjust) > 1e-9) pieces.Add(pendingAdjust);
            pendingAdjust = 0;
        }
        foreach (var item in items)
        {
            if (item is PdfString s)
            {
                var bytes = s.RawBytes.Span;
                int pos = 0;
                while (pos < bytes.Length)
                {
                    int code, cid, consumed;
                    if (font != null) { consumed = font.ReadCode(bytes, pos, out code, out cid); if (consumed <= 0) consumed = 1; }
                    else { code = cid = bytes[pos]; consumed = 1; }
                    var codeBytes = bytes.Slice(pos, consumed).ToArray();
                    pos += consumed;
                    double w0 = (font?.GetGlyphWidth(font.IsComposite ? cid : code) ?? 500) / 1000.0;
                    double tw = (font?.IsWordSpaceCode(code, consumed) ?? code == 32) ? gs.Tw : 0;
                    double adv, advY;
                    P[] quad;
                    if (vertical)
                    {
                        var vm = font!.GetVerticalMetrics(cid);
                        double ox = -vm.VX / 1000.0 * fs * th, oy = y - vm.VY / 1000.0 * fs;
                        advY = vm.W1y / 1000.0 * fs + gs.Tc + tw;
                        adv = 0;
                        quad = Quad(textToPage, ox, oy + gs.Rise, ox + w0 * fs * th, oy + gs.Rise + advY);
                    }
                    else
                    {
                        adv = (w0 * fs + gs.Tc + tw) * th;
                        advY = 0;
                        quad = Quad(textToPage, x, gs.Rise + descent * fs, x + Math.Max(w0 * fs * th, 0.01), gs.Rise + ascent * fs);
                    }
                    bool remove = Covered(quad);
                    if (remove)
                    {
                        FlushKept();
                        removedAny = true;
                        _result.GlyphsRemoved++;
                        // Keep everything after it where it was: shift by the removed glyph's advance.
                        double scale = vertical ? fs : fs * th;
                        if (Math.Abs(scale) > 1e-12) pendingAdjust += -(vertical ? advY : adv) * 1000.0 / scale;
                    }
                    else
                    {
                        FlushAdjust();
                        kept.AddRange(codeBytes);
                        keptAny = true;
                    }
                    if (vertical) y += advY; else x += adv;
                }
            }
            else if (item.TryGetNumber(out double adj) && !double.IsNaN(adj) && !double.IsInfinity(adj))
            {
                FlushKept();
                pendingAdjust += adj;
                if (vertical) y -= adj / 1000.0 * fs; else x -= adj / 1000.0 * fs * th;
            }
        }
        FlushKept();
        FlushAdjust();
        tm = PdfMatrix.CreateTranslation(x, y) * tm;

        if (!removedAny)
        {
            shown = verbatim; // unchanged: the operator as it was
            return false;
        }

        if (marked.Count > 0) { var top = marked.Pop(); marked.Push(top with { Removed = true }); }
        var tj = new StringBuilder(prefix).Append('[');
        foreach (var piece in pieces)
        {
            if (piece is byte[] b) tj.Append('<').Append(Convert.ToHexString(b)).Append('>');
            else tj.Append(' ').Append(F((double)piece)).Append(' ');
        }
        tj.Append("] TJ");
        _ = keptAny;
        shown = tj.ToString();
        return true;
    }

    private static P[] Quad(PdfMatrix m, double x0, double y0, double x1, double y1)
    {
        P T(double x, double y) { var p = m.Transform(x, y); return new P(p.X, p.Y); }
        return new[] { T(x0, y0), T(x1, y0), T(x1, y1), T(x0, y1) };
    }

    /// <summary>A glyph goes when its centre is under a mark, or a quarter of it is.</summary>
    private bool Covered(P[] quad)
    {
        var centre = new P(quad.Average(p => p.X), quad.Average(p => p.Y));
        var box = Bounds(quad);
        foreach (var r in _rects)
        {
            if (!box.IntersectsWith(r) && !Contains(r, centre)) continue;
            if (Contains(r, centre) || OverlapFraction(quad, r) >= 0.25) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ paths

    private bool PaintPath(State gs, string op, string raw, List<Subpath> subpaths, string? clip, Action<string> emit)
    {
        string clipTail = clip != null ? raw + clip + " n" : string.Empty;
        double halfWidth = Math.Max(gs.LineWidth, 0.5) * Math.Sqrt(Math.Abs(gs.Ctm.Determinant)) / 2 + 0.5;
        var all = subpaths.SelectMany(s => s.Points).ToList();
        var bounds = all.Count > 0 ? Bounds(all) : default;
        bool strokes = op is "S" or "s" or "B" or "B*" or "b" or "b*";
        var grown = strokes ? new PdfRect(bounds.X - halfWidth, bounds.Y - halfWidth, bounds.Width + 2 * halfWidth, bounds.Height + 2 * halfWidth) : bounds;
        var hit = _rects.Where(r => Overlaps(grown, r)).ToList();
        if (op == "n" || hit.Count == 0 || !gs.Ctm.TryInvert(out var inv))
        {
            emit(raw + (clip != null ? clip + " " : string.Empty) + op);
            return false;
        }

        bool fills = op is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*";
        bool evenOdd = op.EndsWith('*');
        bool closeForStroke = op is "s" or "b" or "b*";
        var sb = new StringBuilder();
        string U(P p) { var u = inv.Transform(p.X, p.Y); return F(u.X) + " " + F(u.Y); }

        if (fills)
        {
            var fill = new StringBuilder();
            foreach (var sp in subpaths)
            {
                if (sp.Points.Count < 3) continue;
                foreach (var piece in Subtract(sp.Points, hit))
                {
                    fill.Append(U(piece[0])).Append(" m ");
                    for (int i = 1; i < piece.Count; i++) fill.Append(U(piece[i])).Append(" l ");
                    fill.Append("h ");
                }
            }
            if (fill.Length > 0) sb.Append(fill).Append(evenOdd ? "f*" : "f").Append('\n');
        }
        if (strokes)
        {
            var stroke = new StringBuilder();
            foreach (var sp in subpaths)
            {
                var line = new List<P>(sp.Points);
                if ((sp.Closed || closeForStroke) && line.Count > 1) line.Add(line[0]);
                foreach (var run in CutPolyline(line, hit, halfWidth))
                {
                    stroke.Append(U(run[0])).Append(" m ");
                    for (int i = 1; i < run.Count; i++) stroke.Append(U(run[i])).Append(" l ");
                }
            }
            if (stroke.Length > 0) sb.Append(stroke).Append("S\n");
        }
        _result.PathsCut++;
        // The clip this path sets applies after the painting: set it after the new paint.
        emit(sb.ToString() + clipTail);
        return true;
    }

    private static bool Overlaps(PdfRect a, PdfRect b) =>
        a.X <= b.X + b.Width && b.X <= a.X + a.Width && a.Y <= b.Y + b.Height && b.Y <= a.Y + a.Height;

    /// <summary>A clip keeping everything except the redaction rectangles (one nested clip per rectangle), in user space.</summary>
    private string ExclusionClip(PdfMatrix inv)
    {
        var sb = new StringBuilder();
        string U(double x, double y) { var u = inv.Transform(x, y); return F(u.X) + " " + F(u.Y); }
        const double Far = 1e6;
        foreach (var r in _rects)
        {
            sb.Append(U(-Far, -Far)).Append(" m ").Append(U(Far, -Far)).Append(" l ").Append(U(Far, Far)).Append(" l ").Append(U(-Far, Far)).Append(" l h ");
            sb.Append(U(r.X, r.Y)).Append(" m ").Append(U(r.X + r.Width, r.Y)).Append(" l ").Append(U(r.X + r.Width, r.Y + r.Height)).Append(" l ")
              .Append(U(r.X, r.Y + r.Height)).Append(" l h W* n ");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ XObjects and images

    private bool DoXObject(State gs, PdfDictionary resources, string name, string verbatim, Dictionary<string, PdfObject> added, Action<string> emit)
    {
        var xobjects = _r.Resolve(resources["XObject"]) as PdfDictionary;
        var reference = xobjects?[name];
        if (_r.Resolve(reference) is not PdfStream stream)
        {
            emit(verbatim);
            return false;
        }
        string? subtype = stream.Dictionary.GetName("Subtype");
        if (subtype == "Image")
        {
            var quad = Quad(gs.Ctm, 0, 0, 1, 1);
            if (!_rects.Any(r => Overlaps(Bounds(quad), r)))
            {
                emit(verbatim);
                return false;
            }
            var replacement = RedactImage(stream, gs.Ctm, resources);
            if (replacement == null)
            {
                _result.ImagesRemoved++;
                _result.Warnings.Add($"An image under a redaction could not be decoded and was removed entirely.");
                return true; // the Do is dropped
            }
            if (ReferenceEquals(replacement, stream))
            {
                // No pixel mostly under a mark: the image stays, clipped away from the marks.
                emit(gs.Ctm.TryInvert(out var invKeep) ? $"q {ExclusionClip(invKeep)}{verbatim} Q" : verbatim);
                return false;
            }
            string newName = AddObject(replacement, added, "RdIm");
            emit(gs.Ctm.TryInvert(out var invImage) ? $"q {ExclusionClip(invImage)}/{newName} Do Q" : $"/{newName} Do");
            _result.ImagesRedacted++;
            return true;
        }
        if (subtype == "Form")
        {
            var matrix = Matrix(_r.Resolve(stream.Dictionary["Matrix"]) as PdfArray) ?? PdfMatrix.Identity;
            var bbox = _r.Resolve(stream.Dictionary["BBox"]) is PdfArray ba && ba.Count >= 4 ? RectOf(ba) : (PdfRect?)null;
            var formCtm = matrix * gs.Ctm;
            if (bbox is { } b && !_rects.Any(r => Overlaps(Bounds(Quad(formCtm, b.X, b.Y, b.X + b.Width, b.Y + b.Height)), r)))
            {
                emit(verbatim);
                return false;
            }
            var formResources = _r.Resolve(stream.Dictionary["Resources"]) as PdfDictionary ?? resources;
            byte[] data;
            try { data = _decoder.DecodeStream(stream); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _result.Warnings.Add("A form XObject under a redaction could not be read and was removed.");
                return true;
            }
            var (content, newRes, changed) = Rewrite(data, formResources, formCtm);
            if (!changed) { emit(verbatim); return false; }
            var dict = new Dictionary<string, PdfObject>(stream.Dictionary.Entries);
            dict.Remove("Filter"); dict.Remove("DecodeParms"); dict.Remove("Length");
            dict["Resources"] = newRes;
            var form = PdfObjectWriter.NewStream(dict, Deflate(content));
            form = form with { Dictionary = new PdfDictionary(new Dictionary<string, PdfObject>(form.Dictionary.Entries) { ["Filter"] = new PdfName("FlateDecode") }) };
            string formName = AddObject(form, added, "RdFm");
            emit($"/{formName} Do");
            return true;
        }
        emit(verbatim);
        return false;
    }

    private string AddObject(PdfObject obj, Dictionary<string, PdfObject> added, string prefix)
    {
        int number = _allocate();
        _newObjects[number] = obj;
        string name = prefix + number.ToString(CultureInfo.InvariantCulture);
        added[name] = new PdfIndirectRef(number);
        return name;
    }

    /// <summary>
    /// The image with every pixel under a redaction blanked, as a new image object: stencil masks
    /// stay stencils (the covered bits stop painting), others become DeviceRGB with the covered
    /// pixels black and a soft mask when the image had transparency. Null when it cannot be decoded.
    /// </summary>
    private PdfStream? RedactImage(PdfStream stream, PdfMatrix ctm, PdfDictionary? resources)
    {
        var dict = stream.Dictionary;
        int width = (int)(dict.GetInteger("Width") ?? 0), height = (int)(dict.GetInteger("Height") ?? 0);
        if (width <= 0 || height <= 0 || (long)width * height > 64_000_000) return null;
        if (!ctm.TryInvert(out var toImage)) return null;

        // Pixel rows and columns under each rectangle (image space: unit square, row 0 at the top).
        bool[] covered = new bool[width * height];
        bool any = false;
        foreach (var r in _rects)
        {
            var corners = new[] { toImage.Transform(r.X, r.Y), toImage.Transform(r.X + r.Width, r.Y), toImage.Transform(r.X + r.Width, r.Y + r.Height), toImage.Transform(r.X, r.Y + r.Height) };
            double u0 = corners.Min(c => c.X), u1 = corners.Max(c => c.X), v0 = corners.Min(c => c.Y), v1 = corners.Max(c => c.Y);
            int i0 = Math.Max(0, (int)Math.Floor(u0 * width) - 1), i1 = Math.Min(width - 1, (int)Math.Ceiling(u1 * width));
            int j0 = Math.Max(0, (int)Math.Floor((1 - v1) * height) - 1), j1 = Math.Min(height - 1, (int)Math.Ceiling((1 - v0) * height));
            // A pixel goes when at least half of it is under the mark. Its value is then mostly
            // hidden information; a pixel mostly outside shows its value there anyway. (A 1-pixel-wide
            // gradient stretched over the page has pixels as wide as the page: blanking every one
            // the mark touches would black out the whole background.) The image is drawn clipped
            // away from the marks, so no part of a kept pixel shows inside one.
            double pw = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B) / width, ph = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D) / height;
            bool small = pw < 1 && ph < 1;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (covered[j * width + i]) continue;
                    bool hit;
                    if (small)
                    {
                        var c = ctm.Transform((i + 0.5) / width, 1 - (j + 0.5) / height);
                        hit = Contains(r, new P(c.X, c.Y));
                    }
                    else
                    {
                        var quad = Quad(ctm, (double)i / width, 1 - (double)(j + 1) / height, (double)(i + 1) / width, 1 - (double)j / height);
                        hit = OverlapFraction(quad, r) >= 0.5;
                    }
                    if (hit) { covered[j * width + i] = true; any = true; }
                }
        }
        if (!any) return stream;

        bool isMask = _r.Resolve(dict["ImageMask"]) is PdfBoolean { Value: true };
        try
        {
            if (isMask)
            {
                // Decoded through the image decoder, which reads every image filter (CCITT fax,
                // JBIG2, …): a painted sample comes back opaque. Written back as a plain 1-bit
                // stencil (/Decode [0 1]: 0 paints) with the covered samples no longer painting.
                var maskSource = new PdfImageSource("redact-mask", stream, null, PdfColor.Black, _r, _decoder, PdfSecurityLimits.Default);
                var maskPixels = maskSource.Decode();
                if (maskPixels.Format != PdfEngine.Vector.PdfDecodedImageFormat.Bgra32 || maskPixels.Width != width || maskPixels.Height != height) return null;
                var bgraMask = maskPixels.Data.Span;
                int rowBytes = (width + 7) / 8;
                var bits = new byte[rowBytes * height];
                for (int j = 0; j < height; j++)
                    for (int i = 0; i < width; i++)
                    {
                        bool paints = !covered[j * width + i] && bgraMask[(j * width + i) * 4 + 3] >= 128;
                        if (!paints) bits[j * rowBytes + i / 8] |= (byte)(1 << (7 - i % 8));
                    }
                var maskDict = new Dictionary<string, PdfObject>(dict.Entries);
                foreach (var key in new[] { "Filter", "DecodeParms", "Length", "Decode", "JBIG2Globals" }) maskDict.Remove(key);
                maskDict["BitsPerComponent"] = new PdfInteger(1);
                maskDict["Filter"] = new PdfName("FlateDecode");
                return PdfObjectWriter.NewStream(maskDict, Deflate(bits));
            }

            var cs = dict["ColorSpace"] is { } csObj ? PdfColorSpace.Resolve(csObj, _r, resources) : null;
            var source = new PdfImageSource("redact", stream, cs, PdfColor.Black, _r, _decoder, PdfSecurityLimits.Default);
            var decoded = source.Decode();
            byte[]? bgra;
            if (decoded.Format == PdfEngine.Vector.PdfDecodedImageFormat.Jpeg)
                bgra = _options.JpegDecoder?.Invoke(decoded);
            else
                bgra = decoded.Data.ToArray();
            if (bgra == null || bgra.Length < width * height * 4) return null;
            byte[]? alpha = decoded.Alpha?.ToArray();

            var rgb = new byte[width * height * 3];
            var a = new byte[width * height];
            bool translucent = false;
            for (int k = 0; k < width * height; k++)
            {
                byte av = alpha != null && alpha.Length == width * height ? alpha[k] : bgra[k * 4 + 3];
                if (covered[k]) { rgb[k * 3] = rgb[k * 3 + 1] = rgb[k * 3 + 2] = 0; av = 255; }
                else { rgb[k * 3] = bgra[k * 4 + 2]; rgb[k * 3 + 1] = bgra[k * 4 + 1]; rgb[k * 3 + 2] = bgra[k * 4]; }
                a[k] = av;
                translucent |= av != 255;
            }
            var imageDict = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Image"),
                ["Width"] = new PdfInteger(width),
                ["Height"] = new PdfInteger(height),
                ["ColorSpace"] = new PdfName("DeviceRGB"),
                ["BitsPerComponent"] = new PdfInteger(8),
                ["Filter"] = new PdfName("FlateDecode"),
            };
            if (dict["Interpolate"] is { } interp) imageDict["Interpolate"] = interp;
            if (translucent)
            {
                int smask = _allocate();
                _newObjects[smask] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image"), ["Width"] = new PdfInteger(width),
                    ["Height"] = new PdfInteger(height), ["ColorSpace"] = new PdfName("DeviceGray"), ["BitsPerComponent"] = new PdfInteger(8),
                    ["Filter"] = new PdfName("FlateDecode"),
                }, Deflate(a));
                imageDict["SMask"] = new PdfIndirectRef(smask);
            }
            return PdfObjectWriter.NewStream(imageDict, Deflate(rgb));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>An inline image (BI … ID … EI): copied when clear of the redactions, else redacted as an image object.</summary>
    private bool InlineImage(State gs, PdfDictionary resources, MemoryByteSource src, PdfLexer lexer, PdfParser parser, Dictionary<string, PdfObject> added, Action<string> emit)
    {
        var entries = new Dictionary<string, PdfObject>();
        var raw = new StringBuilder("BI ");
        while (true)
        {
            long pos = src.Position;
            var tok = lexer.NextToken();
            if (tok.Type == PdfTokenType.EndOfFile) return false;
            if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID") break;
            if (tok.Type != PdfTokenType.Name) continue;
            var value = parser.ParseObject(lexer);
            if (value == null) continue;
            entries[tok.TextValue] = value;
            raw.Append('/').Append(PdfObjectWriter.EscapeName(tok.TextValue)).Append(' ').Append(Serialize(value)).Append(' ');
        }
        // One whitespace byte after ID, then the data up to a whitespace-delimited EI.
        long start = src.Position + 1;
        var all = src.ReadMemory(0, (int)src.Length).Span;
        long end = -1;
        for (long i = start; i + 1 < all.Length; i++)
        {
            if (all[(int)i] == 'E' && all[(int)i + 1] == 'I' && i > start && PdfLexer.IsWhitespace(all[(int)i - 1]) &&
                (i + 2 >= all.Length || PdfLexer.IsWhitespace(all[(int)i + 2]) || PdfLexer.IsDelimiter(all[(int)i + 2])))
            {
                end = i;
                break;
            }
        }
        if (end < 0) return false;
        byte[] data = all.Slice((int)start, (int)(end - 1 - start)).ToArray();
        src.Position = end + 2;

        var quad = Quad(gs.Ctm, 0, 0, 1, 1);
        if (!_rects.Any(r => Overlaps(Bounds(quad), r)))
        {
            emit(raw.ToString() + "ID " + Encoding.Latin1.GetString(data) + "\nEI");
            return false;
        }
        var expanded = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image") };
        foreach (var (k, v) in entries)
        {
            string key = k switch
            {
                "W" => "Width", "H" => "Height", "BPC" => "BitsPerComponent", "CS" => "ColorSpace", "F" => "Filter",
                "DP" => "DecodeParms", "IM" => "ImageMask", "D" => "Decode", "I" => "Interpolate", _ => k,
            };
            expanded[key] = key switch
            {
                "ColorSpace" when v is PdfName n => new PdfName(n.Value switch { "G" => "DeviceGray", "RGB" => "DeviceRGB", "CMYK" => "DeviceCMYK", "I" => "Indexed", _ => n.Value }),
                "Filter" => ExpandFilter(v),
                _ => v,
            };
        }
        var stream = PdfObjectWriter.NewStream(expanded, data);
        var replacement = RedactImage(stream, gs.Ctm, resources);
        if (replacement == null)
        {
            _result.ImagesRemoved++;
            _result.Warnings.Add("An inline image under a redaction could not be decoded and was removed entirely.");
            return true;
        }
        string name = AddObject(replacement, added, "RdIm");
        emit(gs.Ctm.TryInvert(out var invInline) ? $"q {ExclusionClip(invInline)}/{name} Do Q" : $"/{name} Do");
        _result.ImagesRedacted++;
        return true;
    }

    private static PdfObject ExpandFilter(PdfObject v)
    {
        static PdfObject One(PdfObject o) => o is PdfName n ? new PdfName(n.Value switch
        {
            "AHx" => "ASCIIHexDecode", "A85" => "ASCII85Decode", "LZW" => "LZWDecode", "Fl" => "FlateDecode",
            "RL" => "RunLengthDecode", "CCF" => "CCITTFaxDecode", "DCT" => "DCTDecode", _ => n.Value,
        }) : o;
        return v is PdfArray a ? new PdfArray(a.Select(One).ToArray()) : One(v);
    }

    // ------------------------------------------------------------------ helpers

    internal static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static double[]? Numbers(List<PdfObject> operands, int n)
    {
        if (operands.Count < n) return null;
        var v = new double[n];
        for (int i = 0; i < n; i++)
            if (!operands[operands.Count - n + i].TryGetNumber(out v[i])) return null;
        return v;
    }

    private static PdfMatrix? Matrix(PdfArray? a)
    {
        if (a == null || a.Count < 6) return null;
        var v = new double[6];
        for (int i = 0; i < 6; i++) if (!a[i].TryGetNumber(out v[i])) return null;
        return new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    private static PdfRect RectOf(PdfArray a)
    {
        a[0].TryGetNumber(out double x0); a[1].TryGetNumber(out double y0); a[2].TryGetNumber(out double x1); a[3].TryGetNumber(out double y1);
        return new PdfRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }

    private static string Serialize(PdfObject obj)
    {
        using var ms = new MemoryStream();
        PdfObjectWriter.Write(ms, obj);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 6));
}
