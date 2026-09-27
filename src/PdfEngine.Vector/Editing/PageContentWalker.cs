using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Editing;

/// <summary>How a glyph looks, apart from where it is: everything needed to show it again elsewhere.</summary>
internal sealed record GlyphStyle(
    string FontResource, PdfFont? Font, double FontSize, double CharSpacing, double WordSpacing, double Scaling,
    double Rise, int RenderMode, string StateOps, double[] FillRgb, string ColorOps = "")
{
    public bool Vertical => Font?.IsVertical ?? false;
}

/// <summary>A glyph shown by the page's content.</summary>
internal sealed class ContentGlyph
{
    /// <summary>Which text-showing operator (in content order) and which glyph of it.</summary>
    public int Op, Index;
    public string Text = string.Empty;
    public byte[] Code = Array.Empty<byte>();
    public GlyphStyle Style = null!;
    /// <summary>Text space at the glyph's origin to user space (the text matrix there times the CTM).</summary>
    public PdfMatrix Matrix;
    /// <summary>Glyph width (1/1000 em, before size) and the advance in text space.</summary>
    public double Width0, Advance;
    /// <summary>A code the word spacing applies to (a single-byte 32).</summary>
    public bool IsWordSpace;
    public PdfPoint Origin => new(Matrix.E, Matrix.F);
}

/// <summary>An image the page's content draws (an image XObject or an inline image).</summary>
internal sealed class ContentImage
{
    public int Index;
    public string? ResourceName;
    public bool Inline;
    /// <summary>The unit square of the image to user space.</summary>
    public PdfMatrix Placement;
    public int PixelWidth, PixelHeight;
}

/// <summary>What happens to an image when the content is rewritten.</summary>
internal abstract record ImageChange;
internal sealed record ImageDeleted : ImageChange;
/// <summary>The image drawn again with <c>Cm</c> first (a cm operator's operands) and, optionally, another XObject.</summary>
internal sealed record ImageRedrawn(string Cm, string? NewResource) : ImageChange;

/// <summary>
/// Walks a page's content stream the way a reader draws it, finding every glyph (with its code,
/// text, style and exact position) and every image; and rewrites it with chosen glyphs taken out
/// (the rest keep their exact positions) and images deleted, moved or replaced in place, so the
/// order they are drawn in and the clipping that applies to them stay as they were.
/// </summary>
internal sealed class PageContentWalker
{
    private readonly PdfObjectResolver _r;
    private readonly PdfFontResolver _fonts;
    private readonly PdfDictionary _resources;
    private readonly byte[] _content;

    public List<ContentGlyph> Glyphs { get; } = new();
    public List<ContentImage> Images { get; } = new();
    /// <summary>Marked-content sequences with /ActualText, /Alt or /E around taken-out glyphs had them removed.</summary>
    public int AlternateTextsRemoved { get; private set; }

    public PageContentWalker(PdfVectorDocument doc, byte[] content, PdfDictionary resources)
    {
        _r = doc.Resolver;
        _fonts = new PdfFontResolver(_r, PdfSecurityLimits.Default);
        _resources = resources;
        _content = content;
    }

    private sealed class State
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public string? FontName;
        public PdfFont? Font;
        public double FontSize = 1, Tc, Tw, Th = 100, TL, Rise;
        public int Tr;
        public string FillSpace = string.Empty, FillColor = string.Empty, StrokeSpace = string.Empty, StrokeColor = string.Empty, LineWidth = string.Empty;
        public double[] FillRgb = { 0, 0, 0 };
        public List<string> ExtGStates = new();
        public State Clone()
        {
            var s = (State)MemberwiseClone();
            s.ExtGStates = new List<string>(ExtGStates);
            return s;
        }
        public string StateOps()
        {
            var sb = new StringBuilder();
            foreach (var g in ExtGStates) sb.Append(g).Append(' ');
            return sb.Append(ColorOps()).ToString();
        }
        /// <summary>The colours and line width alone (drawing where the rest of the state already holds).</summary>
        public string ColorOps()
        {
            var sb = new StringBuilder();
            foreach (var part in new[] { FillSpace, FillColor, StrokeSpace, StrokeColor, LineWidth })
                if (part.Length > 0) sb.Append(part).Append(' ');
            return sb.ToString();
        }
    }

    /// <summary>Reads the content: fills <see cref="Glyphs"/> and <see cref="Images"/>.</summary>
    public void Read() => Walk(null, null, null);

    /// <summary>
    /// The content again, with <paramref name="removed"/> glyphs (by op and index) taken out and
    /// images changed. The result draws with every q it opens closed, so what follows it starts
    /// from the page's initial graphics state.
    /// </summary>
    /// <param name="insertions">Content (in user space) drawn right after the text object that
    /// shows the given text-showing operator, so it stacks and clips like the text there did.</param>
    public byte[] Rewrite(ISet<(int Op, int Index)> removed, IReadOnlyDictionary<int, ImageChange> images, IReadOnlyDictionary<int, string>? insertions = null)
    {
        Glyphs.Clear();
        Images.Clear();
        return Walk(removed, images, insertions)!;
    }

    private byte[]? Walk(ISet<(int Op, int Index)>? removed, IReadOnlyDictionary<int, ImageChange>? imageChanges, IReadOnlyDictionary<int, string>? insertions)
    {
        bool rewrite = removed != null;
        var output = new List<string>();
        var pending = new List<string>();
        void Flush(State at)
        {
            if (pending.Count == 0) return;
            // Glyph positions are in user space: undo the CTM in force here.
            if (at.Ctm.TryInvert(out var inverse))
                foreach (var content in pending) output.Add($"q {PdfContentEditor.Mat(inverse)} cm\n{content}Q");
            pending.Clear();
        }
        var gs = new State();
        var stack = new Stack<State>();
        PdfMatrix tm = PdfMatrix.Identity, tlm = PdfMatrix.Identity;
        var operands = new List<PdfObject>(8);
        var operandText = new StringBuilder();
        int showOp = 0, imageIndex = 0;
        bool inText = false;
        var marked = new Stack<(int Index, PdfDictionary? Props, bool Removed, string Tag)>();

        using var src = new MemoryByteSource(_content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        void Emit(string s) { if (rewrite) output.Add(s); }

        void Show(IReadOnlyList<PdfObject> items, string verbatim, string prefix)
        {
            if (insertions != null && insertions.TryGetValue(showOp, out var inserted)) pending.Add(inserted);
            string shown = ShowText(gs, ref tm, items, showOp++, removed, verbatim, prefix, out bool removedAny);
            if (removedAny && marked.Count > 0) { var top = marked.Pop(); marked.Push(top with { Removed = true }); }
            Emit(shown);
            if (rewrite && pending.Count > 0 && inText) BreakTextObject();
        }

        // Draws what is pending right here, between this operator and the next, by ending the text
        // object and starting another where the text position and line start are what they were.
        void BreakTextObject()
        {
            double fs = gs.FontSize, th = gs.Th / 100.0;
            if (gs.Tr >= 4 || Math.Abs(fs * th) < 1e-9 || !tlm.TryInvert(out var inverseLine)) return; // text clipping ends with ET: wait for it
            var offset = tm * inverseLine; // the text position relative to the line start
            bool vertical = gs.Font?.IsVertical ?? false;
            if (Math.Abs(offset.A - 1) > 1e-6 || Math.Abs(offset.D - 1) > 1e-6 || Math.Abs(offset.B) > 1e-6 || Math.Abs(offset.C) > 1e-6
                || Math.Abs(vertical ? offset.E : offset.F) > 1e-6) return;
            output.Add("ET");
            Flush(gs);
            var restore = new StringBuilder("BT ").Append(PdfContentEditor.Mat(tlm)).Append(" Tm");
            double along = vertical ? offset.F : offset.E;
            if (Math.Abs(along) > 1e-9)
                restore.Append(" [").Append(F(-along * 1000 / (vertical ? fs : fs * th))).Append("] TJ");
            output.Add(restore.ToString());
        }

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
            string verbatim = operandText + op;
            switch (op)
            {
                case "q":
                    stack.Push(gs.Clone());
                    Emit(verbatim);
                    break;
                case "Q":
                    // A Q without its q would pop the state the rewritten content is wrapped in.
                    if (stack.Count > 0) { gs = stack.Pop(); Emit(verbatim); }
                    break;
                case "cm" when Numbers(operands, 6) is { } m:
                    gs.Ctm = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]) * gs.Ctm;
                    Emit(verbatim);
                    break;
                case "w": gs.LineWidth = verbatim; Emit(verbatim); break;
                case "gs":
                    gs.ExtGStates.Add(verbatim);
                    if (gs.ExtGStates.Count > 16) gs.ExtGStates.RemoveAt(0);
                    Emit(verbatim);
                    break;

                // ---------------------------------------------------------- colour
                case "g" when Numbers(operands, 1) is { } gv: gs.FillSpace = string.Empty; gs.FillColor = verbatim; gs.FillRgb = new[] { gv[0], gv[0], gv[0] }; Emit(verbatim); break;
                case "rg" when Numbers(operands, 3) is { } rv: gs.FillSpace = string.Empty; gs.FillColor = verbatim; gs.FillRgb = rv; Emit(verbatim); break;
                case "k" when Numbers(operands, 4) is { } kv:
                    gs.FillSpace = string.Empty; gs.FillColor = verbatim;
                    gs.FillRgb = new[] { (1 - kv[0]) * (1 - kv[3]), (1 - kv[1]) * (1 - kv[3]), (1 - kv[2]) * (1 - kv[3]) };
                    Emit(verbatim);
                    break;
                case "cs": gs.FillSpace = verbatim; gs.FillColor = string.Empty; gs.FillRgb = new double[] { 0, 0, 0 }; Emit(verbatim); break;
                case "sc" or "scn":
                    gs.FillColor = verbatim;
                    if (operands.Count is 1 or 3 && operands.All(o => o.TryGetNumber(out _)))
                        gs.FillRgb = operands.Count == 1 ? Enumerable.Repeat(N(operands[0]), 3).ToArray() : operands.Select(N).ToArray();
                    Emit(verbatim);
                    break;
                case "G" or "RG" or "K": gs.StrokeSpace = string.Empty; gs.StrokeColor = verbatim; Emit(verbatim); break;
                case "CS": gs.StrokeSpace = verbatim; gs.StrokeColor = string.Empty; Emit(verbatim); break;
                case "SC" or "SCN": gs.StrokeColor = verbatim; Emit(verbatim); break;

                // ---------------------------------------------------------- text state
                case "BT":
                    tm = tlm = PdfMatrix.Identity;
                    inText = true;
                    Emit(verbatim);
                    break;
                case "ET":
                    inText = false;
                    Emit(verbatim);
                    if (rewrite) Flush(gs);
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
                case "Tr" when Numbers(operands, 1) is { } tr: gs.Tr = (int)tr[0]; Emit(verbatim); break;
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
                    Show(new PdfObject[] { s }, verbatim, string.Empty);
                    break;
                case "TJ" when operands.Count >= 1 && operands[^1] is PdfArray arr:
                    Show(arr.Items, verbatim, string.Empty);
                    break;
                case "'" when operands.Count >= 1 && operands[^1] is PdfString s1:
                    tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                    Show(new PdfObject[] { s1 }, verbatim, "T* ");
                    break;
                case "\"" when operands.Count >= 3 && operands[2] is PdfString s2 && operands[0].TryGetNumber(out double aw) && operands[1].TryGetNumber(out double ac):
                    gs.Tw = aw; gs.Tc = ac;
                    tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                    Show(new PdfObject[] { s2 }, verbatim, $"{F(aw)} Tw {F(ac)} Tc T* ");
                    break;

                // ---------------------------------------------------------- images
                case "Do" when operands.Count >= 1 && operands[^1] is PdfName xname:
                {
                    var xobject = _r.Resolve((_r.Resolve(_resources["XObject"]) as PdfDictionary)?[xname.Value]) as PdfStream;
                    if (xobject?.Dictionary.GetName("Subtype") != "Image")
                    {
                        Emit(verbatim);
                        break;
                    }
                    int index = imageIndex++;
                    Images.Add(new ContentImage
                    {
                        Index = index, ResourceName = xname.Value, Placement = gs.Ctm,
                        PixelWidth = (int)(xobject.Dictionary.GetInteger("Width") ?? 0), PixelHeight = (int)(xobject.Dictionary.GetInteger("Height") ?? 0),
                    });
                    Emit(ChangedImage(imageChanges, index, verbatim, "/" + PdfObjectWriter.EscapeName(xname.Value) + " Do"));
                    break;
                }
                case "BI":
                {
                    var (raw, width, height) = InlineImage(src, lexer, parser);
                    if (raw == null) break;
                    int index = imageIndex++;
                    Images.Add(new ContentImage { Index = index, Inline = true, Placement = gs.Ctm, PixelWidth = width, PixelHeight = height });
                    Emit(ChangedImage(imageChanges, index, raw, null));
                    break;
                }

                // ---------------------------------------------------------- marked content
                case "BDC":
                {
                    var props = operands.Count >= 2 ? operands[1] as PdfDictionary : null;
                    marked.Push((output.Count, props, false, operands.Count >= 1 && operands[0] is PdfName tn ? tn.Value : "Span"));
                    Emit(verbatim);
                    break;
                }
                case "BMC":
                    marked.Push((output.Count, null, false, operands.Count >= 1 && operands[0] is PdfName bn ? bn.Value : "Span"));
                    Emit(verbatim);
                    break;
                case "EMC":
                    if (marked.Count > 0)
                    {
                        var m = marked.Pop();
                        // The words it described are gone or elsewhere: its replacement text would be wrong.
                        if (m.Removed && rewrite && m.Props != null && (m.Props.ContainsKey("ActualText") || m.Props.ContainsKey("Alt") || m.Props.ContainsKey("E")))
                        {
                            var entries = new Dictionary<string, PdfObject>(m.Props.Entries);
                            entries.Remove("ActualText"); entries.Remove("Alt"); entries.Remove("E");
                            output[m.Index] = $"/{PdfObjectWriter.EscapeName(m.Tag)} {Serialize(new PdfDictionary(entries))} BDC";
                            AlternateTextsRemoved++;
                        }
                        if (m.Removed && marked.Count > 0) { var parent = marked.Pop(); marked.Push(parent with { Removed = true }); }
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
        if (!rewrite) return null;
        Flush(gs);
        for (int i = 0; i < stack.Count; i++) output.Add("Q");
        return Encoding.Latin1.GetBytes(string.Join("\n", output) + "\n");
    }

    private static string ChangedImage(IReadOnlyDictionary<int, ImageChange>? changes, int index, string verbatim, string? doOperator)
    {
        if (changes == null || !changes.TryGetValue(index, out var change)) return verbatim;
        return change switch
        {
            ImageDeleted => string.Empty,
            ImageRedrawn r when r.NewResource != null => $"q {r.Cm} cm /{PdfObjectWriter.EscapeName(r.NewResource)} Do Q",
            ImageRedrawn r => $"q {r.Cm} cm {doOperator ?? verbatim} Q",
            _ => verbatim,
        };
    }

    // ------------------------------------------------------------------ text

    private string ShowText(State gs, ref PdfMatrix tm, IReadOnlyList<PdfObject> items, int op, ISet<(int Op, int Index)>? removed,
        string verbatim, string prefix, out bool removedAny)
    {
        var font = gs.Font;
        if (font == null && gs.FontName != null)
        {
            try { font = gs.Font = _fonts.ResolveFont(gs.FontName, _resources); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { font = null; }
        }
        double fs = gs.FontSize, th = gs.Th / 100.0;
        bool vertical = font?.IsVertical ?? false;
        var style = new GlyphStyle(gs.FontName ?? string.Empty, font, fs, gs.Tc, gs.Tw, gs.Th, gs.Rise, gs.Tr, gs.StateOps(), gs.FillRgb, gs.ColorOps());

        var pieces = new List<object>(); // byte[] kept codes, or double adjustments (thousandths)
        var kept = new List<byte>();
        double pendingAdjust = 0, x = 0, y = 0;
        removedAny = false;
        void FlushKept() { if (kept.Count > 0) { pieces.Add(kept.ToArray()); kept.Clear(); } }
        void FlushAdjust() { if (Math.Abs(pendingAdjust) > 1e-9) pieces.Add(pendingAdjust); pendingAdjust = 0; }

        int index = 0;
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
                    var codeBytes = bytes.Slice(pos, Math.Min(consumed, bytes.Length - pos)).ToArray();
                    pos += consumed;
                    double w0 = font?.GetGlyphWidth(font.IsComposite ? cid : code) ?? 500;
                    bool wordSpace = font?.IsWordSpaceCode(code, consumed) ?? code == 32;
                    double tw = wordSpace ? gs.Tw : 0;
                    double adv = vertical
                        ? font!.GetVerticalMetrics(cid).W1y / 1000.0 * fs + gs.Tc + tw
                        : (w0 / 1000.0 * fs + gs.Tc + tw) * th;

                    var at = (vertical ? PdfMatrix.CreateTranslation(0, y) : PdfMatrix.CreateTranslation(x, 0)) * tm * gs.Ctm;
                    Glyphs.Add(new ContentGlyph
                    {
                        Op = op, Index = index, Code = codeBytes, Style = style, Matrix = at, Width0 = w0, Advance = adv, IsWordSpace = wordSpace,
                        Text = font?.MapToUnicode(code) ?? ((char)code).ToString(),
                    });

                    if (removed != null && removed.Contains((op, index)))
                    {
                        FlushKept();
                        removedAny = true;
                        double scale = vertical ? fs : fs * th;
                        if (Math.Abs(scale) > 1e-12) pendingAdjust += -adv * 1000.0 / scale;
                    }
                    else
                    {
                        FlushAdjust();
                        kept.AddRange(codeBytes);
                    }
                    if (vertical) y += adv; else x += adv;
                    index++;
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
        tm = (vertical ? PdfMatrix.CreateTranslation(0, y) : PdfMatrix.CreateTranslation(x, 0)) * tm;
        if (!removedAny) return verbatim;

        var tj = new StringBuilder(prefix).Append('[');
        foreach (var piece in pieces)
        {
            if (piece is byte[] b) tj.Append('<').Append(Convert.ToHexString(b)).Append('>');
            else tj.Append(' ').Append(F((double)piece)).Append(' ');
        }
        return tj.Append("] TJ").ToString();
    }

    // ------------------------------------------------------------------ inline images

    private static (string? Raw, int Width, int Height) InlineImage(MemoryByteSource src, PdfLexer lexer, PdfParser parser)
    {
        var raw = new StringBuilder("BI ");
        int width = 0, height = 0;
        while (true)
        {
            var tok = lexer.NextToken();
            if (tok.Type == PdfTokenType.EndOfFile) return (null, 0, 0);
            if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID") break;
            if (tok.Type != PdfTokenType.Name) continue;
            var value = parser.ParseObject(lexer);
            if (value == null) continue;
            if (tok.TextValue is "W" or "Width" && value is PdfInteger wi) width = (int)wi.Value;
            if (tok.TextValue is "H" or "Height" && value is PdfInteger hi) height = (int)hi.Value;
            raw.Append('/').Append(PdfObjectWriter.EscapeName(tok.TextValue)).Append(' ').Append(Serialize(value)).Append(' ');
        }
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
        if (end < 0) return (null, 0, 0);
        byte[] data = all.Slice((int)start, (int)(end - 1 - start)).ToArray();
        src.Position = end + 2;
        return (raw + "ID " + Encoding.Latin1.GetString(data) + "\nEI", width, height);
    }

    // ------------------------------------------------------------------ helpers

    private static double N(PdfObject o) => o.TryGetNumber(out double v) ? v : 0;

    private static double[]? Numbers(List<PdfObject> operands, int n)
    {
        if (operands.Count < n) return null;
        var v = new double[n];
        for (int i = 0; i < n; i++)
            if (!operands[operands.Count - n + i].TryGetNumber(out v[i])) return null;
        return v;
    }

    internal static string Serialize(PdfObject obj)
    {
        using var ms = new MemoryStream();
        PdfObjectWriter.Write(ms, obj);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    internal static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 6));
}
