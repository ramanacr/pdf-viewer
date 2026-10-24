using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Accessibility;

/// <summary>What an operator of a content stream does, as far as tagging is concerned.</summary>
internal enum ContentOpKind { Neutral, Text, PathConstruct, PathPaint, PathEnd, XObject, InlineImage, Shading, BeginMarked, EndMarked, BeginText, EndText, Save, Restore }

/// <summary>One operator of a content stream, with where it is and the state it is drawn in.</summary>
internal sealed class ContentOp
{
    public string Name = string.Empty;
    public ContentOpKind Kind;
    /// <summary>Lexer positions: before its first operand, of its keyword, and just after it.</summary>
    public long Start, Keyword, End;
    public List<PdfObject> Operands = new();
    /// <summary>Graphics state nesting (q) before the operator, and whether it is inside a text object.</summary>
    public int Depth;
    public bool InText;
    /// <summary>Inside a marked-content sequence the document already marks as an artifact.</summary>
    public bool InArtifact;
    /// <summary>Inside an existing sequence with /ActualText (its glyphs read as that text together).</summary>
    public bool InActualText;
    /// <summary>For painting operators and XObjects: what it covers, in user space.</summary>
    public PdfRect? Bounds;
    /// <summary>For path painting: the operator the path starts at.</summary>
    public int PathStart = -1;
    /// <summary>For path painting: its straight edges in user space (table rules).</summary>
    public List<(PdfPoint A, PdfPoint B)>? Edges;
    public bool Fill;
    /// <summary>For Do: the XObject's subtype (Image, Form, PS).</summary>
    public string? XObjectSubtype;
    /// <summary>For BMC/BDC: the tag, its property list and whether it carries an MCID.</summary>
    public string? Tag;
    public PdfDictionary? Properties;
    public bool PropertiesNamed;
}

/// <summary>The content of a page or form cannot be read to its end.</summary>
internal sealed class AutoTagContentException : Exception
{
    public AutoTagContentException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Reads a content stream into operators (the way readers lex it) and writes it again with marked content around them, every original byte kept.</summary>
internal static class AutoTagContent
{
    private readonly record struct Ctm(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Ctm Identity = new(1, 0, 0, 1, 0, 0);
        public Ctm Then(Ctm m) => new(A * m.A + B * m.C, A * m.B + B * m.D, C * m.A + D * m.C, C * m.B + D * m.D, E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);
        public PdfPoint Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
    }

    private static double Num(PdfObject? o) => o != null && o.TryGetNumber(out double v) && double.IsFinite(v) ? v : 0;

    /// <summary>The operators of <paramref name="content"/>; throws <see cref="AutoTagContentException"/> when it cannot be lexed.</summary>
    public static List<ContentOp> Scan(byte[] content, PdfDictionary? resources, PdfObjectResolver r)
    {
        var ops = new List<ContentOp>();
        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        var operands = new List<PdfObject>();
        long opStart = -1;
        int depth = 0;
        bool inText = false;
        var marks = new List<(bool Artifact, bool Actual)>();
        var ctmStack = new Stack<Ctm>();
        var ctm = Ctm.Identity;
        int pathStart = -1;
        var path = new List<(PdfPoint A, PdfPoint B)>();
        double px0 = double.MaxValue, py0 = double.MaxValue, px1 = double.MinValue, py1 = double.MinValue;
        PdfPoint current = default, subpath = default;
        void Point(PdfPoint p)
        {
            px0 = Math.Min(px0, p.X); py0 = Math.Min(py0, p.Y); px1 = Math.Max(px1, p.X); py1 = Math.Max(py1, p.Y);
        }
        void ResetPath()
        {
            pathStart = -1;
            path.Clear();
            px0 = py0 = double.MaxValue; px1 = py1 = double.MinValue;
        }
        PdfRect UnitSquare(Ctm m)
        {
            var pts = new[] { m.Apply(0, 0), m.Apply(1, 0), m.Apply(0, 1), m.Apply(1, 1) };
            double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y);
            return new PdfRect(x0, y0, pts.Max(p => p.X) - x0, pts.Max(p => p.Y) - y0);
        }
        var xobjects = r.Resolve(resources?["XObject"]) as PdfDictionary;
        var properties = r.Resolve(resources?["Properties"]) as PdfDictionary;

        while (true)
        {
            long pos = src.Position;
            PdfToken token;
            try { token = lexer.NextToken(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new AutoTagContentException("The content cannot be lexed.", ex); }
            if (token.Type == PdfTokenType.EndOfFile) break;
            if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
            {
                if (operands.Count == 0) opStart = pos;
                src.Position = pos;
                PdfObject? obj;
                try
                {
                    obj = token.Type == PdfTokenType.Keyword
                        ? (token.TextValue == "null" ? PdfNull.Instance : token.TextValue == "true" ? PdfBoolean.True : PdfBoolean.False)
                        : parser.ParseObject(lexer);
                    if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { throw new AutoTagContentException("An operand cannot be read.", ex); }
                if (obj != null) operands.Add(obj);
                else if (src.Position == pos) src.Position = pos + 1;
                continue;
            }
            string name = token.TextValue;
            var op = new ContentOp
            {
                Name = name, Start = operands.Count > 0 ? opStart : pos, Keyword = pos, Operands = new List<PdfObject>(operands),
                Depth = depth, InText = inText, InArtifact = marks.Any(m => m.Artifact), InActualText = marks.Any(m => m.Actual),
            };
            operands.Clear();
            int index = ops.Count;
            switch (name)
            {
                case "q":
                    op.Kind = ContentOpKind.Save;
                    depth++;
                    ctmStack.Push(ctm);
                    break;
                case "Q":
                    op.Kind = ContentOpKind.Restore;
                    if (depth > 0) depth--;
                    if (ctmStack.Count > 0) ctm = ctmStack.Pop();
                    break;
                case "cm" when op.Operands.Count >= 6:
                    var o = op.Operands;
                    ctm = new Ctm(Num(o[^6]), Num(o[^5]), Num(o[^4]), Num(o[^3]), Num(o[^2]), Num(o[^1])).Then(ctm);
                    break;
                case "BT": op.Kind = ContentOpKind.BeginText; inText = true; break;
                case "ET": op.Kind = ContentOpKind.EndText; inText = false; break;
                case "Tj" or "TJ" or "'" or "\"": op.Kind = ContentOpKind.Text; break;
                case "m" or "l" or "c" or "v" or "y" or "h" or "re":
                    op.Kind = ContentOpKind.PathConstruct;
                    if (pathStart < 0) pathStart = index;
                    var a = op.Operands;
                    switch (name)
                    {
                        case "m" when a.Count >= 2: current = subpath = ctm.Apply(Num(a[^2]), Num(a[^1])); Point(current); break;
                        case "l" when a.Count >= 2:
                            var to = ctm.Apply(Num(a[^2]), Num(a[^1]));
                            path.Add((current, to)); Point(to); current = to;
                            break;
                        case "c" when a.Count >= 6:
                            for (int k = 6; k > 0; k -= 2) Point(ctm.Apply(Num(a[^k]), Num(a[^(k - 1)])));
                            current = ctm.Apply(Num(a[^2]), Num(a[^1]));
                            break;
                        case "v" or "y" when a.Count >= 4:
                            for (int k = 4; k > 0; k -= 2) Point(ctm.Apply(Num(a[^k]), Num(a[^(k - 1)])));
                            current = ctm.Apply(Num(a[^2]), Num(a[^1]));
                            break;
                        case "h": path.Add((current, subpath)); current = subpath; break;
                        case "re" when a.Count >= 4:
                            double x = Num(a[^4]), y = Num(a[^3]), w = Num(a[^2]), h = Num(a[^1]);
                            var p00 = ctm.Apply(x, y); var p10 = ctm.Apply(x + w, y); var p11 = ctm.Apply(x + w, y + h); var p01 = ctm.Apply(x, y + h);
                            path.Add((p00, p10)); path.Add((p10, p11)); path.Add((p11, p01)); path.Add((p01, p00));
                            Point(p00); Point(p10); Point(p11); Point(p01);
                            current = subpath = p00;
                            break;
                    }
                    break;
                case "W" or "W*": break;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    op.Kind = ContentOpKind.PathPaint;
                    op.PathStart = pathStart >= 0 ? pathStart : index;
                    op.Fill = name is not ("S" or "s");
                    if (name is "s" or "b" or "b*") path.Add((current, subpath));
                    op.Bounds = px0 <= px1 ? new PdfRect(px0, py0, px1 - px0, py1 - py0) : null;
                    op.Edges = new List<(PdfPoint, PdfPoint)>(path);
                    ResetPath();
                    break;
                case "n":
                    op.Kind = ContentOpKind.PathEnd;
                    ResetPath();
                    break;
                case "sh": op.Kind = ContentOpKind.Shading; break;
                case "Do" when op.Operands.Count > 0 && op.Operands[^1] is PdfName xname:
                    op.Kind = ContentOpKind.XObject;
                    if (r.Resolve(xobjects?[xname.Value]) is PdfStream xo)
                    {
                        op.XObjectSubtype = xo.Dictionary.GetName("Subtype");
                        if (op.XObjectSubtype == "Image") op.Bounds = UnitSquare(ctm);
                        else if (op.XObjectSubtype == "Form" && r.Resolve(xo.Dictionary["BBox"]) is PdfArray { Count: 4 } box)
                        {
                            var m = r.Resolve(xo.Dictionary["Matrix"]) is PdfArray { Count: 6 } mm
                                ? new Ctm(Num(r.Resolve(mm[0])), Num(r.Resolve(mm[1])), Num(r.Resolve(mm[2])), Num(r.Resolve(mm[3])), Num(r.Resolve(mm[4])), Num(r.Resolve(mm[5])))
                                : Ctm.Identity;
                            double bx0 = Num(r.Resolve(box[0])), by0 = Num(r.Resolve(box[1])), bx1 = Num(r.Resolve(box[2])), by1 = Num(r.Resolve(box[3]));
                            var full = m.Then(ctm);
                            var pts = new[] { full.Apply(bx0, by0), full.Apply(bx1, by0), full.Apply(bx0, by1), full.Apply(bx1, by1) };
                            double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y);
                            op.Bounds = new PdfRect(x0, y0, pts.Max(p => p.X) - x0, pts.Max(p => p.Y) - y0);
                        }
                    }
                    break;
                case "BI":
                    op.Kind = ContentOpKind.InlineImage;
                    (string? Raw, int, int) inline;
                    try { inline = PageContentWalker.InlineImage(src, lexer, parser); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { throw new AutoTagContentException("An inline image cannot be read.", ex); }
                    if (inline.Raw == null) throw new AutoTagContentException("An inline image has no end.");
                    op.Bounds = UnitSquare(ctm);
                    break;
                case "BMC" or "BDC":
                    op.Kind = ContentOpKind.BeginMarked;
                    op.Tag = op.Operands.Count > 0 && op.Operands[0] is PdfName tn ? tn.Value : string.Empty;
                    if (name == "BDC" && op.Operands.Count >= 2)
                    {
                        if (op.Operands[^1] is PdfDictionary pd) op.Properties = pd;
                        else if (op.Operands[^1] is PdfName pn && r.Resolve(properties?[pn.Value]) is PdfDictionary named) { op.Properties = named; op.PropertiesNamed = true; }
                    }
                    marks.Add((op.Tag == "Artifact", op.Properties?.ContainsKey("ActualText") == true));
                    break;
                case "EMC":
                    op.Kind = ContentOpKind.EndMarked;
                    if (marks.Count > 0) marks.RemoveAt(marks.Count - 1);
                    break;
            }
            op.End = src.Position;
            ops.Add(op);
        }
        return ops;
    }

    /// <summary>An existing BDC with an MCID, written again without it (its other properties kept); null when it has none.</summary>
    public static string? WithoutMcid(ContentOp op)
    {
        if (op.Kind != ContentOpKind.BeginMarked || op.Properties == null || !op.Properties.ContainsKey("MCID")) return null;
        var entries = new Dictionary<string, PdfObject>(op.Properties.Entries);
        entries.Remove("MCID");
        string tag = "/" + PdfObjectWriter.EscapeName(op.Tag ?? "Span");
        return entries.Count == 0 && !op.PropertiesNamed ? $"{tag} BMC" : $"{tag} {PageContentWalker.Serialize(new PdfDictionary(entries))} BDC";
    }

    /// <summary>Text inserted between operators (at <c>Position</c>), or an operator replaced (from <c>Position</c> to <c>End</c>).</summary>
    public readonly record struct Edit(long Position, int Order, string Text, long End = -1);

    /// <summary>The content with the edits applied: every byte outside replaced ranges kept as it was.</summary>
    public static byte[] Apply(byte[] content, List<Edit> edits)
    {
        var sorted = edits.OrderBy(e => e.Position).ThenBy(e => e.Order).ToList();
        using var ms = new System.IO.MemoryStream(content.Length + sorted.Count * 24);
        long at = 0;
        foreach (var e in sorted)
        {
            long p = Math.Clamp(e.Position, at, content.Length);
            ms.Write(content, (int)at, (int)(p - at));
            at = p;
            ms.Write(Encoding.Latin1.GetBytes(e.Text));
            if (e.End >= 0) at = Math.Clamp(e.End, at, content.Length);
        }
        ms.Write(content, (int)at, (int)(content.Length - at));
        return ms.ToArray();
    }
}
