using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.PdfA;

/// <summary>
/// Takes out of the text-showing operators the codes whose glyph the embedded font program does
/// not have (they draw .notdef, which PDF/A forbids: P1 6.3.5, P2 6.2.11.8). Each removed glyph
/// becomes a TJ adjustment of its advance, as <see cref="PageContentWalker"/> does, so every other
/// glyph keeps its exact position; the removed ones drew only a .notdef box, which most readers
/// leave empty. Pages, the form XObjects and tiling patterns they draw, soft masks and annotation
/// appearances are rewritten; everything else in a content stream stays byte for byte.
/// </summary>
internal sealed class NotdefGlyphRemover
{
    private const int MaxDepth = 12;
    private readonly PdfVectorDocument _doc;
    private readonly PdfObjectResolver _r;
    private readonly PdfFontResolver _fonts;
    private readonly PdfStreamDecoder _decoder;
    private readonly Dictionary<PdfDictionary, FontGlyphs?> _fontCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, Drawn> _drawn = new();

    /// <summary>Replaced and new objects, by object number.</summary>
    public Dictionary<int, PdfObject> Objects { get; } = new();
    /// <summary>How many glyphs were taken out.</summary>
    public int Removed { get; private set; }

    public NotdefGlyphRemover(PdfVectorDocument doc)
    {
        _doc = doc;
        _r = doc.Resolver;
        _fonts = new PdfFontResolver(_r, PdfSecurityLimits.Default);
        _decoder = new PdfStreamDecoder(null, _r.Resolve);
    }

    /// <summary>A font whose embedded program can be asked which glyphs it has.</summary>
    private sealed class FontGlyphs
    {
        public required PdfFont Font;
        public required Func<int, int, bool> Present;
    }

    /// <summary>The text state that matters for a glyph's advance; a value inherited from wherever a form is drawn may be unknown.</summary>
    private sealed class State
    {
        public FontGlyphs? Font;
        public double Size;
        public double CharSpacing, WordSpacing;
        public bool FontKnown = true, CharSpacingKnown = true, WordSpacingKnown = true;
        public int RenderMode;

        public State Clone() => (State)MemberwiseClone();

        /// <summary>What two drawings of a form agree on.</summary>
        public static State Merge(State a, State b) => new()
        {
            Font = a.Font, Size = a.Size, CharSpacing = a.CharSpacing, WordSpacing = a.WordSpacing,
            FontKnown = a.FontKnown && b.FontKnown && ReferenceEquals(a.Font, b.Font) && a.Size == b.Size,
            CharSpacingKnown = a.CharSpacingKnown && b.CharSpacingKnown && a.CharSpacing == b.CharSpacing,
            WordSpacingKnown = a.WordSpacingKnown && b.WordSpacingKnown && a.WordSpacing == b.WordSpacing,
        };

        public bool Same(State o) => FontKnown == o.FontKnown && CharSpacingKnown == o.CharSpacingKnown && WordSpacingKnown == o.WordSpacingKnown
                                     && ReferenceEquals(Font, o.Font) && Size == o.Size && CharSpacing == o.CharSpacing && WordSpacing == o.WordSpacing;
    }

    /// <summary>A stream drawn from somewhere (a form, pattern, soft mask or appearance): the state it starts in and the resources it uses.</summary>
    private sealed class Drawn
    {
        public required State Entry;
        public PdfDictionary? Resources;
        /// <summary>Drawn with different resources in different places: left as it is.</summary>
        public bool Conflict;
    }

    public void Run(Func<int> allocate)
    {
        if (_r.Resolve(_doc.XrefTable.Trailer?["Root"]) is not PdfDictionary root) return;
        var pageObjects = PdfRedactor.PageObjectNumbers(root, _r);
        var pages = _doc.PageTree.Pages;
        // First every drawing is followed, so each form knows the state it starts in; then each stream is rewritten once.
        foreach (var page in pages)
        {
            if (PageContent(page) is { } content) Walk(content, page.Resources, new State(), 0, rewrite: false);
            foreach (var appearance in Appearances(page)) Draw(appearance, null, new State(), 0);
        }
        for (int i = 0; i < pages.Count && i < pageObjects.Count; i++)
        {
            var page = pages[i];
            if (PageContent(page) is not { } original || Walk(original, page.Resources, new State(), 0, rewrite: true) is not { } content) continue;
            int number = allocate();
            Objects[number] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, ContentRedactor.Deflate(content));
            Objects[pageObjects[i]] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Contents"] = new PdfIndirectRef(number) });
        }
        foreach (var (number, drawn) in _drawn)
        {
            if (drawn.Conflict || _r.Resolve(number) is not PdfStream stream || Decode(stream) is not { } data) continue;
            if (Walk(data, drawn.Resources, drawn.Entry.Clone(), 1, rewrite: true) is not { } content) continue;
            var entries = new Dictionary<string, PdfObject>(stream.Dictionary.Entries);
            foreach (var key in new[] { "Length", "Filter", "DecodeParms", "DL" }) entries.Remove(key);
            entries["Filter"] = new PdfName("FlateDecode");
            Objects[number] = PdfObjectWriter.NewStream(entries, ContentRedactor.Deflate(content));
        }
    }

    /// <summary>The page's content streams as the one stream they are (split only between tokens); null when one cannot be read.</summary>
    private byte[]? PageContent(PdfPageNode page)
    {
        using var ms = new MemoryStream();
        foreach (var s in page.Contents)
        {
            if (Decode(s) is not { } d) return null;
            ms.Write(d);
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }

    private IEnumerable<PdfIndirectRef> Appearances(PdfPageNode page)
    {
        if (_r.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) yield break;
        foreach (var item in annots)
        {
            if (_r.Resolve(item) is not PdfDictionary annot || _r.Resolve(annot["AP"]) is not PdfDictionary ap) continue;
            var normal = ap["N"];
            if (normal is PdfIndirectRef one && _r.Resolve(one) is PdfStream) yield return one;
            else if (_r.Resolve(normal) is PdfDictionary states)
                foreach (var state in states.Entries.Values.OfType<PdfIndirectRef>())
                    if (_r.Resolve(state) is PdfStream) yield return state;
        }
    }

    private byte[]? Decode(PdfStream s)
    {
        try { return _decoder.DecodeStream(s); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Records a drawing of a stream (a form, pattern, soft mask or appearance) and follows it when what it starts from changed.</summary>
    private void Draw(PdfIndirectRef reference, PdfDictionary? callerResources, State at, int depth)
    {
        if (depth > MaxDepth || _r.Resolve(reference) is not PdfStream stream) return;
        var own = _r.Resolve(stream.Dictionary["Resources"]) as PdfDictionary;
        var resources = own ?? callerResources;
        var entry = at.Clone();
        entry.RenderMode = 0;
        if (_drawn.TryGetValue(reference.ObjectNumber, out var drawn))
        {
            if (!ReferenceEquals(drawn.Resources, resources)) drawn.Conflict = true;
            var merged = State.Merge(drawn.Entry, entry);
            if (merged.Same(drawn.Entry)) return;
            drawn.Entry = merged;
        }
        else _drawn[reference.ObjectNumber] = drawn = new Drawn { Entry = entry, Resources = resources };
        if (drawn.Conflict || Decode(stream) is not { } data) return;
        Walk(data, resources, drawn.Entry.Clone(), depth + 1, rewrite: false);
    }

    private FontGlyphs? FontFor(PdfDictionary? fontDict)
    {
        if (fontDict == null) return null;
        if (_fontCache.TryGetValue(fontDict, out var known)) return known;
        FontGlyphs? result = null;
        try
        {
            var font = _fonts.ResolveFont(fontDict, "F");
            // As the validator judges it: an embedded program read through the same mapping; a CMap chained to another is not followed.
            bool chained = _r.Resolve(fontDict["Encoding"]) is PdfStream cm && (cm.Dictionary.ContainsKey("UseCMap")
                           || (Decode(cm) is { } cmData && Encoding.Latin1.GetString(cmData).Contains("usecmap", StringComparison.Ordinal)));
            if (fontDict.GetName("Subtype") != "Type3" && !chained && FontProgramPreparer.Prepare(font) is { } prepared)
            {
                var truetype = prepared.Format == PdfFontProgramFormat.TrueType ? TrueTypeFontFile.TryLoad(prepared.Sfnt) : null;
                var cff = prepared.Format == PdfFontProgramFormat.OpenTypeCff ? PdfAChecker.CffOf(prepared) : null;
                result = new FontGlyphs { Font = font, Present = (code, cid) => PdfAChecker.GlyphPresent(font, prepared, truetype, cff, code, cid) };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { result = null; }
        _fontCache[fontDict] = result;
        return result;
    }

    private PdfDictionary? Named(PdfDictionary? resources, string category, string name) =>
        _r.Resolve((_r.Resolve(resources?[category]) as PdfDictionary)?[name]) as PdfDictionary
        ?? (_r.Resolve((_r.Resolve(resources?[category]) as PdfDictionary)?[name]) as PdfStream)?.Dictionary;

    private PdfObject? NamedRaw(PdfDictionary? resources, string category, string name) => (_r.Resolve(resources?[category]) as PdfDictionary)?[name];

    /// <summary>
    /// Walks a content stream. Following, it records the drawings of forms, patterns and soft
    /// masks; rewriting, it returns the stream with the codes taken out, or null when nothing is.
    /// </summary>
    private byte[]? Walk(byte[] content, PdfDictionary? resources, State gs, int depth, bool rewrite)
    {
        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        var operands = new List<PdfObject>(8);
        var stack = new Stack<State>();
        var entry = gs.Clone();
        var output = rewrite ? new MemoryStream() : null;
        long copied = 0, operandStart = -1;
        bool changed = false;
        while (true)
        {
            long tokenPos = src.Position;
            PdfToken token;
            try { token = lexer.NextToken(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { break; }
            if (token.Type == PdfTokenType.EndOfFile) break;
            if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
            {
                if (operands.Count == 0) operandStart = tokenPos;
                src.Position = tokenPos;
                PdfObject? obj;
                try
                {
                    obj = token.Type == PdfTokenType.Keyword
                        ? (token.TextValue == "null" ? PdfNull.Instance : token.TextValue == "true" ? PdfBoolean.True : PdfBoolean.False)
                        : parser.ParseObject(lexer);
                    if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { obj = null; }
                if (obj != null) operands.Add(obj);
                else if (src.Position == tokenPos) src.Position = tokenPos + 1;
                continue;
            }
            string op = token.TextValue;
            long opEnd = src.Position;
            string? replacement = null;
            switch (op)
            {
                case "q": stack.Push(gs.Clone()); break;
                case "Q": if (stack.Count > 0) gs = stack.Pop(); break;
                case "Tf" when operands.Count >= 2 && operands[0] is PdfName fn && operands[1].TryGetNumber(out double size):
                    gs.Font = FontFor(Named(resources, "Font", fn.Value));
                    gs.Size = size;
                    gs.FontKnown = true;
                    break;
                case "Tc" when operands.Count >= 1 && operands[^1].TryGetNumber(out double tc): gs.CharSpacing = tc; gs.CharSpacingKnown = true; break;
                case "Tw" when operands.Count >= 1 && operands[^1].TryGetNumber(out double tw): gs.WordSpacing = tw; gs.WordSpacingKnown = true; break;
                case "Tr" when operands.Count >= 1 && operands[^1].TryGetNumber(out double tr): gs.RenderMode = (int)tr; break;
                case "gs" when operands.Count >= 1 && operands[^1] is PdfName gsName && Named(resources, "ExtGState", gsName.Value) is { } ext:
                    if (_r.Resolve(ext["Font"]) is PdfArray { Count: 2 } gf && _r.Resolve(gf[1]) is { } gsSize && gsSize.TryGetNumber(out double gfs))
                    {
                        gs.Font = FontFor(_r.Resolve(gf[0]) as PdfDictionary);
                        gs.Size = gfs;
                        gs.FontKnown = true;
                    }
                    if (!rewrite && _r.Resolve(ext["SMask"]) is PdfDictionary mask && mask["G"] is PdfIndirectRef group)
                        Draw(group, resources, gs, depth);
                    break;
                case "Tj" when operands.Count >= 1 && operands[^1] is PdfString s:
                    replacement = rewrite ? Show(gs, new PdfObject[] { s }, string.Empty) : null;
                    break;
                case "TJ" when operands.Count >= 1 && operands[^1] is PdfArray arr:
                    replacement = rewrite ? Show(gs, arr.Items, string.Empty) : null;
                    break;
                case "'" when operands.Count >= 1 && operands[^1] is PdfString s1:
                    replacement = rewrite ? Show(gs, new PdfObject[] { s1 }, "T* ") : null;
                    break;
                case "\"" when operands.Count >= 3 && operands[2] is PdfString s2 && operands[0].TryGetNumber(out double aw) && operands[1].TryGetNumber(out double ac):
                    gs.WordSpacing = aw; gs.WordSpacingKnown = true;
                    gs.CharSpacing = ac; gs.CharSpacingKnown = true;
                    replacement = rewrite ? Show(gs, new PdfObject[] { s2 }, $"{PageContentWalker.F(aw)} Tw {PageContentWalker.F(ac)} Tc T* ") : null;
                    break;
                case "Do" when !rewrite && operands.Count >= 1 && operands[^1] is PdfName xname:
                    if (NamedRaw(resources, "XObject", xname.Value) is PdfIndirectRef xref && _r.Resolve(xref) is PdfStream { } x && x.Dictionary.GetName("Subtype") == "Form")
                        Draw(xref, resources, gs, depth);
                    break;
                case "scn" or "SCN" when !rewrite && operands.Count >= 1 && operands[^1] is PdfName patternName:
                    // A tiling pattern's cell starts from the state of the content it is used in.
                    if (NamedRaw(resources, "Pattern", patternName.Value) is PdfIndirectRef pref && _r.Resolve(pref) is PdfStream { } p && p.Dictionary.GetInteger("PatternType") == 1)
                        Draw(pref, resources, entry, depth);
                    break;
                case "BI":
                    SkipInlineImage(src, lexer, parser);
                    opEnd = src.Position;
                    break;
            }
            if (replacement != null && output != null && operandStart >= 0)
            {
                output.Write(content, (int)copied, (int)(operandStart - copied));
                output.Write(Encoding.Latin1.GetBytes("\n" + replacement + "\n"));
                copied = opEnd;
                changed = true;
            }
            operands.Clear();
            operandStart = -1;
        }
        if (output == null || !changed) return null;
        output.Write(content, (int)copied, content.Length - (int)copied);
        return output.ToArray();
    }

    /// <summary>
    /// A text-showing operator with the codes whose glyph is missing taken out, as a TJ (after
    /// <paramref name="prefix"/>); null when it keeps every code. A code is kept when its advance
    /// cannot be known (a text state inherited from where a form is drawn).
    /// </summary>
    private string? Show(State gs, IReadOnlyList<PdfObject> items, string prefix)
    {
        if (!gs.FontKnown || gs.Font is not { } glyphs || gs.RenderMode is 3 or 7 || Math.Abs(gs.Size) < 1e-9) return null;
        var font = glyphs.Font;
        bool vertical = font.IsVertical;
        var pieces = new List<object>(); // byte[] kept codes, or double adjustments (thousandths)
        var kept = new List<byte>();
        double pendingAdjust = 0;
        int removed = 0;
        void FlushKept() { if (kept.Count > 0) { pieces.Add(kept.ToArray()); kept.Clear(); } }
        void FlushAdjust() { if (Math.Abs(pendingAdjust) > 1e-9) pieces.Add(pendingAdjust); pendingAdjust = 0; }
        foreach (var item in items)
        {
            if (item is PdfString s)
            {
                var bytes = s.RawBytes.Span;
                int pos = 0;
                while (pos < bytes.Length)
                {
                    int consumed = font.ReadCode(bytes, pos, out int code, out int cid);
                    if (consumed <= 0) consumed = 1;
                    var codeBytes = bytes.Slice(pos, Math.Min(consumed, bytes.Length - pos));
                    pos += consumed;
                    bool wordSpace = font.IsWordSpaceCode(code, consumed);
                    if (!glyphs.Present(code, cid) && gs.CharSpacingKnown && (!wordSpace || gs.WordSpacingKnown))
                    {
                        // The glyph's advance (as the walker computes it), in thousandths of text space.
                        double w0 = vertical ? font.GetVerticalMetrics(cid).W1y : font.GetGlyphWidth(font.IsComposite ? cid : code);
                        double spacing = gs.CharSpacing + (wordSpace ? gs.WordSpacing : 0);
                        FlushKept();
                        pendingAdjust += -(w0 + spacing * 1000.0 / gs.Size);
                        removed++;
                    }
                    else
                    {
                        FlushAdjust();
                        kept.AddRange(codeBytes.ToArray());
                    }
                }
            }
            else if (item.TryGetNumber(out double adj) && !double.IsNaN(adj) && !double.IsInfinity(adj))
            {
                FlushKept();
                pendingAdjust += adj;
            }
        }
        if (removed == 0) return null;
        FlushKept();
        FlushAdjust();
        Removed += removed;
        var tj = new StringBuilder(prefix).Append('[');
        foreach (var piece in pieces)
        {
            if (piece is byte[] b) tj.Append('<').Append(Convert.ToHexString(b)).Append('>');
            else tj.Append(' ').Append(PageContentWalker.F((double)piece)).Append(' ');
        }
        return tj.Append("] TJ").ToString();
    }

    /// <summary>Moves past an inline image's dictionary and data (to after its EI).</summary>
    private static void SkipInlineImage(MemoryByteSource src, PdfLexer lexer, PdfParser parser)
    {
        while (true)
        {
            var tok = lexer.NextToken();
            if (tok.Type == PdfTokenType.EndOfFile) return;
            if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID") break;
            if (tok.Type == PdfTokenType.Name) parser.ParseObject(lexer);
        }
        long start = src.Position + 1;
        var all = src.ReadMemory(0, (int)src.Length).Span;
        for (long i = start; i + 1 < all.Length; i++)
        {
            if (all[(int)i] == 'E' && all[(int)i + 1] == 'I' && i > start && PdfLexer.IsWhitespace(all[(int)i - 1]) &&
                (i + 2 >= all.Length || PdfLexer.IsWhitespace(all[(int)i + 2]) || PdfLexer.IsDelimiter(all[(int)i + 2])))
            {
                src.Position = i + 2;
                return;
            }
        }
        src.Position = all.Length;
    }
}
