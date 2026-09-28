using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Geometry;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Accessibility;

/// <summary>
/// Page content: every painting operator is either inside marked content with an MCID that the
/// structure tree owns (real content) or inside an Artifact sequence, never both (7.1); text has a
/// determinable language (7.2); form XObjects are not reference XObjects and tagged ones are drawn
/// once (7.20). Records where each MCID draws, so issues about structure elements can show them.
/// </summary>
internal sealed partial class PdfUaChecker
{
    private readonly Dictionary<(int Page, int Stm, int Mcid), PdfRect> _mcidBounds = new();
    private readonly Dictionary<int, (int Page, PdfRect Rect)> _annotRects = new();
    private readonly Dictionary<int, int> _taggedFormUses = new();
    private readonly Dictionary<int, bool> _formHasMcid = new();
    private PdfFontResolver? _fontResolver;
    private readonly Dictionary<PdfDictionary, PdfFont?> _fonts = new(ReferenceEqualityComparer.Instance);

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        /// <summary>This transformation followed by <paramref name="m"/>.</summary>
        public Matrix Then(Matrix m) => new(A * m.A + B * m.C, A * m.B + B * m.D, C * m.A + D * m.C, C * m.B + D * m.D, E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    /// <summary>An open marked-content sequence.</summary>
    private sealed record Marked(string Tag, bool Artifact, int? Mcid, int Stm, string? Lang, StructElem? Owner);

    /// <summary>What one page's walk has found so far.</summary>
    private sealed class PageScan
    {
        public int Page;
        public int Untagged;
        public PdfRect? FirstUntagged;
        public bool TextWithoutLanguage;
        public PdfRect? FirstTextWithoutLanguage;
        public readonly HashSet<int> Forms = new();
    }

    private void CheckContent()
    {
        bool tagged = _structRoot != null;
        for (int i = 0; i < _doc.PageTree.Count; i++)
        {
            var page = _doc.PageTree.Pages[i];
            var scan = new PageScan { Page = i };
            int? structParents = _r.Resolve(page.Dictionary["StructParents"]) is { } sp && sp.TryGetInteger(out long spv) ? (int)spv : null;
            var data = new List<byte>();
            foreach (var content in page.Contents)
                if (Decode(content) is { } bytes) { data.AddRange(bytes); data.Add((byte)'\n'); }
            Walk(data.ToArray(), page.Resources, Matrix.Identity, new List<Marked>(), scan, 0, structParents, 0);
            // Without a structure tree the missing tree is the one issue; every page would repeat it.
            if (scan.Untagged > 0 && tagged)
                Report("01-005", "7.1", PdfUaCategory.PageContent,
                    $"{scan.Untagged} content item(s) on the page are neither tagged nor marked as artifacts.", page: i + 1, bounds: scan.FirstUntagged);
            if (scan.TextWithoutLanguage && tagged)
                Report("11-001", "7.2", PdfUaCategory.PageContent, "The language of text on the page cannot be determined: neither the document, its structure elements nor its marked content give a /Lang.",
                    page: i + 1, bounds: scan.FirstTextWithoutLanguage);
        }
        foreach (var (form, uses) in _taggedFormUses)
            if (uses > 1)
                Report("30-002", "7.20", PdfUaCategory.PageContent, $"A form XObject with tagged content is drawn {uses} times; its tags can belong to only one place.", obj: form);
    }

    private static double Num(PdfObject? o) => o != null && o.TryGetNumber(out double v) ? v : 0;

    private void Walk(byte[] content, PdfDictionary? resources, Matrix ctm, List<Marked> inherited, PageScan scan, int stm, int? structParents, int depth)
    {
        if (depth > 12) return;
        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        var operands = new List<PdfObject>();
        var marked = new List<Marked>(inherited);
        int baseDepth = inherited.Count;
        var gstack = new Stack<Matrix>();
        Matrix tm = Matrix.Identity, tlm = Matrix.Identity;
        PdfFont? font = null;
        double fontSize = 0, tc = 0, tw = 0, th = 1, tl = 0;
        var fontStack = new Stack<(PdfFont?, double, double, double, double, double)>();
        double px0 = double.MaxValue, py0 = double.MaxValue, px1 = double.MinValue, py1 = double.MinValue;
        void AddPoint(double x, double y)
        {
            var (dx, dy) = ctm.Apply(x, y);
            px0 = Math.Min(px0, dx); py0 = Math.Min(py0, dy); px1 = Math.Max(px1, dx); py1 = Math.Max(py1, dy);
        }
        void ResetPath() { px0 = py0 = double.MaxValue; px1 = py1 = double.MinValue; }
        PdfRect? PathBounds() => px0 <= px1 ? new PdfRect(px0, py0, px1 - px0, py1 - py0) : null;
        PdfRect UnitSquare()
        {
            var pts = new[] { ctm.Apply(0, 0), ctm.Apply(1, 0), ctm.Apply(0, 1), ctm.Apply(1, 1) };
            double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y);
            return new PdfRect(x0, y0, pts.Max(p => p.X) - x0, pts.Max(p => p.Y) - y0);
        }

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
                if (obj != null) operands.Add(obj);
                else if (src.Position == pos) src.Position = pos + 1;
                continue;
            }
            string op = token.TextValue;
            switch (op)
            {
                case "q": gstack.Push(ctm); fontStack.Push((font, fontSize, tc, tw, th, tl)); break;
                case "Q":
                    if (gstack.Count > 0) ctm = gstack.Pop();
                    if (fontStack.Count > 0) (font, fontSize, tc, tw, th, tl) = fontStack.Pop();
                    break;
                case "cm" when operands.Count >= 6:
                    ctm = new Matrix(Num(operands[^6]), Num(operands[^5]), Num(operands[^4]), Num(operands[^3]), Num(operands[^2]), Num(operands[^1])).Then(ctm);
                    break;
                case "m" or "l" when operands.Count >= 2: AddPoint(Num(operands[^2]), Num(operands[^1])); break;
                case "c" when operands.Count >= 6:
                    for (int k = 6; k > 0; k -= 2) AddPoint(Num(operands[^k]), Num(operands[^(k - 1)]));
                    break;
                case "v" or "y" when operands.Count >= 4:
                    for (int k = 4; k > 0; k -= 2) AddPoint(Num(operands[^k]), Num(operands[^(k - 1)]));
                    break;
                case "re" when operands.Count >= 4:
                    double rx = Num(operands[^4]), ry = Num(operands[^3]), rw = Num(operands[^2]), rh = Num(operands[^1]);
                    AddPoint(rx, ry); AddPoint(rx + rw, ry + rh); AddPoint(rx + rw, ry); AddPoint(rx, ry + rh);
                    break;
                case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s":
                    Paint(PathBounds(), false);
                    ResetPath();
                    break;
                case "n": ResetPath(); break;
                case "sh": Paint(null, false); break;
                case "BT": tm = tlm = Matrix.Identity; break;
                case "Tf" when operands.Count >= 2:
                    fontSize = Num(operands[^1]);
                    font = operands[^2] is PdfName fn ? FontOf(resources, fn.Value) : null;
                    break;
                case "Tc" when operands.Count >= 1: tc = Num(operands[^1]); break;
                case "Tw" when operands.Count >= 1: tw = Num(operands[^1]); break;
                case "Tz" when operands.Count >= 1: th = Num(operands[^1]) / 100; break;
                case "TL" when operands.Count >= 1: tl = Num(operands[^1]); break;
                case "Td" or "TD" when operands.Count >= 2:
                    if (op == "TD") tl = -Num(operands[^1]);
                    tlm = new Matrix(1, 0, 0, 1, Num(operands[^2]), Num(operands[^1])).Then(tlm);
                    tm = tlm;
                    break;
                case "Tm" when operands.Count >= 6:
                    tlm = tm = new Matrix(Num(operands[^6]), Num(operands[^5]), Num(operands[^4]), Num(operands[^3]), Num(operands[^2]), Num(operands[^1]));
                    break;
                case "T*":
                    tlm = new Matrix(1, 0, 0, 1, 0, -tl).Then(tlm);
                    tm = tlm;
                    break;
                case "Tj" or "'" or "\"" or "TJ":
                    if (op is "'" or "\"")
                    {
                        if (op == "\"" && operands.Count >= 3) { tw = Num(operands[^3]); tc = Num(operands[^2]); }
                        tlm = new Matrix(1, 0, 0, 1, 0, -tl).Then(tlm);
                        tm = tlm;
                    }
                    Paint(ShowText(operands, font, fontSize, tc, tw, th, ref tm, ctm), true);
                    break;
                case "BI":
                    SkipInlineImage(src, lexer, parser);
                    Paint(UnitSquare(), false);
                    break;
                case "Do" when operands.Count > 0 && operands[^1] is PdfName xname:
                    var xref = (_r.Resolve(resources?["XObject"]) as PdfDictionary)?[xname.Value];
                    if (_r.Resolve(xref) is PdfStream xobject)
                    {
                        string? subtype = xobject.Dictionary.GetName("Subtype");
                        if (subtype == "Image") Paint(UnitSquare(), false);
                        else if (subtype == "Form") DrawForm(xobject, xref as PdfIndirectRef, resources, ctm, marked, scan, depth);
                    }
                    break;
                case "BMC" or "BDC" when operands.Count > 0:
                    BeginMarked(op, operands, resources, marked, scan, stm, structParents);
                    break;
                case "EMC":
                    if (marked.Count > baseDepth) marked.RemoveAt(marked.Count - 1);
                    break;
            }
            operands.Clear();
        }

        void Paint(PdfRect? bounds, bool text)
        {
            bool real = marked.Any(m => m.Mcid != null), artifact = marked.Any(m => m.Artifact);
            if (!real && !artifact)
            {
                scan.Untagged++;
                scan.FirstUntagged ??= bounds;
            }
            if (bounds is { } b)
                for (int k = marked.Count - 1; k >= 0; k--)
                    if (marked[k].Mcid is { } id)
                    {
                        // The MCID of the innermost tagged sequence owns the content; forms' own MCIDs carry the form's number.
                        var key = (scan.Page, marked[k].Stm, id);
                        _mcidBounds[key] = _mcidBounds.TryGetValue(key, out var old) ? Union(old, b) : b;
                        break;
                    }
            if (text && real && !artifact && _documentLang == null && LanguageOf(marked) == null)
            {
                scan.TextWithoutLanguage = true;
                scan.FirstTextWithoutLanguage ??= bounds;
            }
        }
    }

    /// <summary>The language that applies inside the open sequences: their own /Lang or their structure elements'.</summary>
    private static string? LanguageOf(List<Marked> marked)
    {
        for (int k = marked.Count - 1; k >= 0; k--)
        {
            if (!string.IsNullOrEmpty(marked[k].Lang)) return marked[k].Lang;
            if (marked[k].Owner?.InheritedLang is { } l) return l;
        }
        return null;
    }

    private void BeginMarked(string op, List<PdfObject> operands, PdfDictionary? resources, List<Marked> marked, PageScan scan, int stm, int? structParents)
    {
        string tag = (operands[0] as PdfName)?.Value ?? string.Empty;
        PdfDictionary? props = null;
        if (op == "BDC" && operands.Count >= 2)
            props = operands[^1] switch
            {
                PdfDictionary d => d,
                PdfName n => _r.Resolve((_r.Resolve(resources?["Properties"]) as PdfDictionary)?[n.Value]) as PdfDictionary,
                _ => null,
            };
        int? mcid = props != null && _r.Resolve(props["MCID"]) is { } m && m.TryGetInteger(out long id) ? (int)id : null;
        string? lang = props == null ? null : TextOf(props, "Lang");
        bool artifact = tag == "Artifact";
        bool inReal = marked.Any(x => x.Mcid != null), inArtifact = marked.Any(x => x.Artifact);
        var bounds = default(PdfRect?);
        if (artifact && inReal)
            Report("01-003", "7.1", PdfUaCategory.PageContent, "An artifact is marked inside tagged content.", page: scan.Page + 1, bounds: bounds);
        if (mcid != null && inArtifact)
            Report("01-004", "7.1", PdfUaCategory.PageContent, "Tagged content (with an MCID) is inside an artifact.", page: scan.Page + 1);
        StructElem? owner = null;
        if (mcid is { } mc && !inArtifact && _structRoot != null)
        {
            owner = OwnerOf(scan.Page, stm, mc, structParents);
            if (owner == null)
                Report("01-005", "7.1", PdfUaCategory.PageContent, $"Marked content (MCID {mc}) is not referenced by any structure element.", page: scan.Page + 1);
        }
        if (lang != null && !PdfUaValidator.IsValidLanguageTag(lang))
            Report("11-001", "7.2", PdfUaCategory.PageContent, "A marked-content sequence's language (/Lang) is not a valid language tag.", page: scan.Page + 1);
        var entry = new Marked(tag, artifact, mcid, stm, lang, owner);
        if (props != null && (props.ContainsKey("ActualText") || props.ContainsKey("Alt") || props.ContainsKey("E")) && _documentLang == null
            && LanguageOf(new List<Marked>(marked) { entry }) == null)
            Report("11-002", "7.2", PdfUaCategory.AlternateText, "The language of a marked-content sequence's alternate text, actual text or expansion cannot be determined.", page: scan.Page + 1);
        marked.Add(entry);
    }

    private void DrawForm(PdfStream form, PdfIndirectRef? reference, PdfDictionary? resources, Matrix ctm, List<Marked> marked, PageScan scan, int depth)
    {
        int number = reference?.ObjectNumber ?? 0;
        if (form.Dictionary.ContainsKey("Ref"))
            Report("30-001", "7.20", PdfUaCategory.PageContent, "A reference XObject is drawn; its content is in another file.", page: scan.Page + 1, obj: reference?.ObjectNumber);
        if (Decode(form) is not { } data) return;
        if (number != 0)
        {
            if (!_formHasMcid.TryGetValue(number, out bool hasMcid))
                _formHasMcid[number] = hasMcid = System.Text.Encoding.Latin1.GetString(data).Contains("/MCID", StringComparison.Ordinal);
            if (hasMcid) _taggedFormUses[number] = _taggedFormUses.GetValueOrDefault(number) + 1;
            if (!scan.Forms.Add(number) && depth > 0) return;
        }
        var matrix = _r.Resolve(form.Dictionary["Matrix"]) is PdfArray a && a.Count == 6
            ? new Matrix(Num(_r.Resolve(a[0])), Num(_r.Resolve(a[1])), Num(_r.Resolve(a[2])), Num(_r.Resolve(a[3])), Num(_r.Resolve(a[4])), Num(_r.Resolve(a[5])))
            : Matrix.Identity;
        var formResources = _r.Resolve(form.Dictionary["Resources"]) as PdfDictionary ?? resources;
        int? structParents = _r.Resolve(form.Dictionary["StructParents"]) is { } sp && sp.TryGetInteger(out long v) ? (int)v : null;
        Walk(data, formResources, matrix.Then(ctm), marked, scan, number, structParents, depth + 1);
        if (number != 0) scan.Forms.Remove(number);
    }

    private PdfFont? FontOf(PdfDictionary? resources, string name)
    {
        if (_r.Resolve((_r.Resolve(resources?["Font"]) as PdfDictionary)?[name]) is not PdfDictionary dict) return null;
        if (_fonts.TryGetValue(dict, out var font)) return font;
        try
        {
            _fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default);
            font = _fontResolver.ResolveFont(dict, name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { font = null; }
        _fonts[dict] = font;
        return font;
    }

    /// <summary>Advances the text matrix over the shown strings and returns the box they cover on the page.</summary>
    private static PdfRect? ShowText(List<PdfObject> operands, PdfFont? font, double size, double tc, double tw, double th, ref Matrix tm, Matrix ctm)
    {
        double x = 0;
        void Advance(PdfString s)
        {
            var bytes = s.RawBytes.Span;
            int pos = 0;
            while (pos < bytes.Length)
            {
                int code, cid, n;
                if (font != null) n = font.ReadCode(bytes, pos, out code, out cid);
                else { code = cid = bytes[pos]; n = 1; }
                if (n <= 0) n = 1;
                double w0 = font != null ? font.GetGlyphWidth(font.IsComposite ? cid : code) / 1000.0 : 0.5;
                x += (w0 * size + tc + (n == 1 && code == 32 ? tw : 0)) * th;
                pos += n;
            }
        }
        if (operands.Count == 0) return null;
        if (operands[^1] is PdfArray array)
        {
            foreach (var item in array)
                if (item is PdfString str) Advance(str);
                else if (item.TryGetNumber(out double adjust)) x -= adjust / 1000.0 * size * th;
        }
        else if (operands[^1] is PdfString str) Advance(str);
        double ascent = font?.Ascent is > 0 and var a ? a : 0.8, descent = font?.Descent is < 0 and var d ? d : -0.2;
        var start = tm.Then(ctm);
        var corners = new[] { start.Apply(0, descent * size), start.Apply(x, descent * size), start.Apply(0, ascent * size), start.Apply(x, ascent * size) };
        tm = new Matrix(1, 0, 0, 1, x, 0).Then(tm);
        double x0 = corners.Min(p => p.X), y0 = corners.Min(p => p.Y);
        return new PdfRect(x0, y0, corners.Max(p => p.X) - x0, corners.Max(p => p.Y) - y0);
    }

    private static void SkipInlineImage(MemoryByteSource src, PdfLexer lexer, PdfParser parser)
    {
        while (true)
        {
            PdfToken tok;
            try { tok = lexer.NextToken(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
            if (tok.Type == PdfTokenType.EndOfFile) return;
            if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID") break;
            if (tok.Type != PdfTokenType.Name) continue;
            try { parser.ParseObject(lexer); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        var all = src.ReadMemory(0, (int)src.Length).Span;
        for (long i = src.Position + 1; i + 1 < all.Length; i++)
        {
            if (all[(int)i] == 'E' && all[(int)i + 1] == 'I' && PdfLexer.IsWhitespace(all[(int)i - 1]) &&
                (i + 2 >= all.Length || PdfLexer.IsWhitespace(all[(int)i + 2]) || PdfLexer.IsDelimiter(all[(int)i + 2])))
            {
                src.Position = i + 2;
                return;
            }
        }
        src.Position = all.Length;
    }
}
