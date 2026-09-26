using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Functions;
using PdfEngine.Vector.Graphics;
using PdfEngine.Vector.Images;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Content;

/// <summary>
/// Interprets PDF page content streams into an immutable display list of vector commands
/// (ISO 32000-2 clauses 8 and 9).
/// </summary>
/// <remarks>
/// Anything the vector path cannot reproduce faithfully is emitted as a classified
/// <see cref="DrawFallbackRegion"/> with conservative page-space bounds instead of being
/// silently dropped or approximated (06_PDFIUM_FALLBACK_AND_EXIT_PLAN, 11 "Error policy").
/// </remarks>
public sealed class PdfContentInterpreter
{
    private readonly PdfObjectResolver _resolver;
    private readonly PdfFontResolver _fontResolver;
    private readonly PdfSecurityLimits _limits;
    private readonly PdfStreamDecoder _streamDecoder;

    /// <summary>Optional-content groups that are OFF in the default configuration (never painted).</summary>
    public IReadOnlySet<PdfDictionary>? HiddenOptionalContentGroups { get; init; }

    /// <summary>Stable prefix for backend cache keys of resources from this document.</summary>
    public string DocumentKey { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Render annotation normal appearances, matching the PDFium path's FPDF_ANNOT flag.</summary>
    public bool RenderAnnotations { get; init; } = true;

    public PdfContentInterpreter(
        PdfObjectResolver resolver,
        PdfFontResolver? fontResolver = null,
        PdfSecurityLimits? limits = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _fontResolver = fontResolver ?? new PdfFontResolver(_resolver);
        _limits = limits ?? PdfSecurityLimits.Default;
        _streamDecoder = new PdfStreamDecoder(_limits, o => _resolver.Resolve(o));
    }

    public IPdfDisplayList BuildDisplayList(PdfPageNode page) => BuildDisplayList(page, CancellationToken.None);

    public IPdfDisplayList BuildDisplayList(PdfPageNode page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PageRun(this, page, cancellationToken).Build();
    }

    private sealed record PreparedFace(PdfFontFace Face, PreparedFontProgram? Program);

    private readonly ConditionalWeakTable<PdfFont, PreparedFace> _prepared = new();

    internal PdfFontFace FaceFor(PdfFont font) => PrepareFont(font).Face;

    /// <summary>
    /// Face plus prepared program. Every embedded kind (TrueType, OpenType, bare CFF, Type 1) is
    /// normalized into a loadable sfnt; when that fails the face is <see cref="PdfFontProgramFormat.Unsupported"/>
    /// and the backend classifies its text as fallback instead of substituting another font.
    /// </summary>
    private PreparedFace PrepareFont(PdfFont font)
    {
        return _prepared.GetValue(font, static f =>
        {
            PreparedFontProgram? program = f.EmbeddedProgramKind == PdfFontProgramKind.None ? null : FontProgramPreparer.Prepare(f);
            var format = program?.Format
                         ?? (f.EmbeddedProgramKind == PdfFontProgramKind.None ? PdfFontProgramFormat.None : PdfFontProgramFormat.Unsupported);

            string key = program != null
                ? "fontprog:" + Convert.ToHexString(SHA256.HashData(program.Sfnt), 0, 12)
                : "fontsub:" + (f.PostScriptName ?? f.BaseFont);

            var face = new PdfFontFace(
                key,
                f.PostScriptName ?? f.BaseFont,
                format,
                program != null ? program.Sfnt : ReadOnlyMemory<byte>.Empty,
                f.IsBold,
                f.IsItalic,
                f.IsSerif,
                f.IsFixedPitch,
                f.IsSymbolic,
                NormalizeEm(f.Ascent, 0.8),
                NormalizeEm(f.Descent, -0.2));
            return new PreparedFace(face, program);
        });
    }

    private static double NormalizeEm(double value, double fallback)
    {
        if (double.IsNaN(value) || value == 0) return fallback;
        return Math.Abs(value) > 2 ? value / 1000.0 : value;
    }

    /// <summary>Per-page interpretation state. Not thread-safe; one instance per build.</summary>
    private sealed class PageRun
    {
        private readonly PdfContentInterpreter _owner;
        private readonly PdfPageNode _page;
        private readonly CancellationToken _ct;
        private readonly PdfObjectResolver _resolver;
        private readonly PdfSecurityLimits _limits;
        private readonly PdfParser _parser;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly List<PdfDrawCommand> _commands = new();
        private readonly List<PdfFallbackToken> _fallbacks = new();
        private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
        private PdfFeatureSet _features;

        private long _operators;
        private long _segments;
        private long _glyphs;
        private long _imagePixels;
        private int _imageSerial;

        public PageRun(PdfContentInterpreter owner, PdfPageNode page, CancellationToken ct)
        {
            _owner = owner;
            _page = page;
            _ct = ct;
            _resolver = owner._resolver;
            _limits = owner._limits;
            _parser = new PdfParser(_limits);
        }

        private PdfRect Crop => _page.CropBox;

        public IPdfDisplayList Build()
        {
            var gs = new GraphicsState { ClipBoundsPage = Crop };

            try
            {
                byte[] content = ConcatenateContentStreams(_page.Contents);
                if (content.Length > 0)
                {
                    Execute(content, _page.Resources, gs, new ContentContext(PdfMatrix.Identity, 0, 0, false));
                }

                if (_owner.RenderAnnotations)
                {
                    AppendAnnotationAppearances();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PdfResourceLimitException ex)
            {
                PageFallback(PdfFallbackReason.ResourceLimit, $"Resource limit reached: {ex.LimitName}");
            }
            catch (PdfUnsupportedFeatureException ex)
            {
                PageFallback(ex.Reason, ex.Message);
            }
            catch (Exception ex) when (ex is PdfVectorException or InvalidDataException or FormatException
                                           or IndexOutOfRangeException or ArgumentException or OverflowException
                                           or InvalidOperationException)
            {
                PageFallback(PdfFallbackReason.MalformedContentRecovery, $"Content interpretation failed ({ex.GetType().Name})");
            }

            return new PdfDisplayList(
                _page.PageNumber,
                _page.PageSize,
                _page.MediaBox,
                _page.CropBox,
                _page.RotationDegrees,
                _commands,
                _features,
                _fallbacks);
        }

        // ------------------------------------------------------------------ content execution

        private readonly record struct ContentContext(
            PdfMatrix PatternBase,
            int FormDepth,
            int Type3Depth,
            bool UncoloredGlyph);

        private sealed class PathState
        {
            public readonly List<PdfPathSegment> Segments = new();
            public PdfPoint Current;
            public PdfPoint SubpathStart;
            public bool ClipPending;
            public PdfFillRule ClipRule;
        }

        private void Execute(ReadOnlyMemory<byte> content, PdfDictionary? resources, GraphicsState gs, ContentContext ctx)
        {
            resources ??= _page.Resources;
            var stack = new Stack<GraphicsState>();
            var path = new PathState();
            var operands = new List<PdfObject>(8);
            var ocStack = new Stack<bool>();
            int hiddenDepth = 0;
            int compatDepth = 0;
            PdfRect? textClip = null;
            bool inText = false;

            using var src = new MemoryByteSource(content);
            var lexer = new PdfLexer(src, _limits);

            while (true)
            {
                long tokenPos = src.Position;
                var token = lexer.NextToken();
                if (token.Type == PdfTokenType.EndOfFile)
                    break;

                if (token.Type != PdfTokenType.Keyword)
                {
                    src.Position = tokenPos;
                    var obj = _parser.ParseObject(lexer);
                    if (obj != null)
                    {
                        if (operands.Count >= 64)
                            operands.RemoveAt(0); // bounded recovery for runaway operand lists
                        operands.Add(obj);
                    }
                    else if (src.Position == tokenPos)
                    {
                        src.Position = tokenPos + 1; // never stall on an unparsable byte
                    }
                    continue;
                }

                string op = token.TextValue;
                // true/false/null are operands, not operators.
                if (op is "true" or "false" or "null")
                {
                    operands.Add(op == "null" ? PdfNull.Instance : op == "true" ? PdfBoolean.True : PdfBoolean.False);
                    continue;
                }

                Tick();
                bool hidden = hiddenDepth > 0;

                switch (op)
                {
                    // ---------------------------------------------------------- graphics state
                    case "q":
                        if (stack.Count < _limits.MaxGraphicsStateDepth)
                        {
                            stack.Push(gs.Clone());
                            _commands.Add(new SaveState());
                        }
                        break;

                    case "Q":
                        if (stack.Count > 0)
                        {
                            var restored = stack.Pop();
                            CopyInto(restored, gs);
                            _commands.Add(new RestoreState());
                        }
                        break;

                    case "cm":
                        if (TryNumbers(operands, 6, out var cm))
                        {
                            var m = new PdfMatrix(cm[0], cm[1], cm[2], cm[3], cm[4], cm[5]);
                            gs.CTM = m * gs.CTM;
                            _commands.Add(new ConcatTransform(m));
                        }
                        break;

                    case "w":
                        if (TryNumbers(operands, 1, out var w)) gs.LineWidth = Math.Max(0, w[0]);
                        break;
                    case "J":
                        if (TryNumbers(operands, 1, out var cap)) gs.LineCap = (PdfLineCap)Math.Clamp((int)cap[0], 0, 2);
                        break;
                    case "j":
                        if (TryNumbers(operands, 1, out var join)) gs.LineJoin = (PdfLineJoin)Math.Clamp((int)join[0], 0, 2);
                        break;
                    case "M":
                        if (TryNumbers(operands, 1, out var ml)) gs.MiterLimit = Math.Max(1, ml[0]);
                        break;
                    case "d":
                        if (operands.Count >= 2 && operands[^2] is PdfArray dashArr && operands[^1].TryGetNumber(out double phase))
                        {
                            SetDash(gs, dashArr, phase);
                        }
                        break;
                    case "ri":
                    case "i":
                        break; // rendering intent / flatness: device-dependent, no visual contract here

                    case "gs":
                        if (LastName(operands) is string gsName)
                            ApplyExtGState(gsName, resources, gs);
                        break;

                    // ---------------------------------------------------------- colour
                    case "CS":
                    case "cs":
                        if (!ctx.UncoloredGlyph && LastName(operands) is string csName)
                            SetColorSpace(gs, op == "CS", csName, resources);
                        break;

                    case "SC":
                    case "SCN":
                    case "sc":
                    case "scn":
                        if (!ctx.UncoloredGlyph)
                            SetColor(gs, stroke: op[0] == 'S', operands, resources);
                        break;

                    case "G":
                    case "g":
                        if (!ctx.UncoloredGlyph && TryNumbers(operands, 1, out var gray))
                            SetDeviceColor(gs, op == "G", PdfColorSpace.DeviceGray, gray);
                        break;
                    case "RG":
                    case "rg":
                        if (!ctx.UncoloredGlyph && TryNumbers(operands, 3, out var rgb))
                            SetDeviceColor(gs, op == "RG", PdfColorSpace.DeviceRgb, rgb);
                        break;
                    case "K":
                    case "k":
                        if (!ctx.UncoloredGlyph && TryNumbers(operands, 4, out var cmyk))
                            SetDeviceColor(gs, op == "K", PdfColorSpace.DeviceCmyk, cmyk);
                        break;

                    case "sh":
                        if (!hidden && LastName(operands) is string shName)
                            PaintShadingOperator(shName, resources, gs);
                        break;

                    // ---------------------------------------------------------- path construction
                    case "m":
                        if (TryNumbers(operands, 2, out var mv))
                        {
                            path.Current = new PdfPoint(mv[0], mv[1]);
                            path.SubpathStart = path.Current;
                            AddSegment(path, new PdfMoveTo(path.Current));
                        }
                        break;
                    case "l":
                        if (TryNumbers(operands, 2, out var lv))
                        {
                            path.Current = new PdfPoint(lv[0], lv[1]);
                            AddSegment(path, new PdfLineTo(path.Current));
                        }
                        break;
                    case "c":
                        if (TryNumbers(operands, 6, out var cv))
                        {
                            var end = new PdfPoint(cv[4], cv[5]);
                            AddSegment(path, new PdfCubicBezierTo(new(cv[0], cv[1]), new(cv[2], cv[3]), end));
                            path.Current = end;
                        }
                        break;
                    case "v":
                        if (TryNumbers(operands, 4, out var vv))
                        {
                            var end = new PdfPoint(vv[2], vv[3]);
                            AddSegment(path, new PdfCubicBezierTo(path.Current, new(vv[0], vv[1]), end));
                            path.Current = end;
                        }
                        break;
                    case "y":
                        if (TryNumbers(operands, 4, out var yv))
                        {
                            var end = new PdfPoint(yv[2], yv[3]);
                            AddSegment(path, new PdfCubicBezierTo(new(yv[0], yv[1]), end, end));
                            path.Current = end;
                        }
                        break;
                    case "h":
                        if (path.Segments.Count > 0)
                        {
                            AddSegment(path, new PdfCloseSubpath());
                            path.Current = path.SubpathStart;
                        }
                        break;
                    case "re":
                        if (TryNumbers(operands, 4, out var re))
                        {
                            double x = re[0], y = re[1], rw = re[2], rh = re[3];
                            AddSegment(path, new PdfMoveTo(new(x, y)));
                            AddSegment(path, new PdfLineTo(new(x + rw, y)));
                            AddSegment(path, new PdfLineTo(new(x + rw, y + rh)));
                            AddSegment(path, new PdfLineTo(new(x, y + rh)));
                            AddSegment(path, new PdfCloseSubpath());
                            path.Current = path.SubpathStart = new PdfPoint(x, y);
                        }
                        break;

                    // ---------------------------------------------------------- painting / clipping
                    case "S": PaintPath(path, gs, resources, ctx, hidden, fill: false, stroke: true, PdfFillRule.NonZero, close: false); break;
                    case "s": PaintPath(path, gs, resources, ctx, hidden, fill: false, stroke: true, PdfFillRule.NonZero, close: true); break;
                    case "f":
                    case "F": PaintPath(path, gs, resources, ctx, hidden, fill: true, stroke: false, PdfFillRule.NonZero, close: false); break;
                    case "f*": PaintPath(path, gs, resources, ctx, hidden, fill: true, stroke: false, PdfFillRule.EvenOdd, close: false); break;
                    case "B": PaintPath(path, gs, resources, ctx, hidden, fill: true, stroke: true, PdfFillRule.NonZero, close: false); break;
                    case "B*": PaintPath(path, gs, resources, ctx, hidden, fill: true, stroke: true, PdfFillRule.EvenOdd, close: false); break;
                    case "b": PaintPath(path, gs, resources, ctx, hidden, fill: true, stroke: true, PdfFillRule.NonZero, close: true); break;
                    case "b*": PaintPath(path, gs, resources, ctx, hidden, fill: true, stroke: true, PdfFillRule.EvenOdd, close: true); break;
                    case "n": PaintPath(path, gs, resources, ctx, hidden, fill: false, stroke: false, PdfFillRule.NonZero, close: false); break;
                    case "W":
                        path.ClipPending = true;
                        path.ClipRule = PdfFillRule.NonZero;
                        break;
                    case "W*":
                        path.ClipPending = true;
                        path.ClipRule = PdfFillRule.EvenOdd;
                        break;

                    // ---------------------------------------------------------- text objects & state
                    case "BT":
                        inText = true;
                        gs.TextMatrix = PdfMatrix.Identity;
                        gs.TextLineMatrix = PdfMatrix.Identity;
                        textClip = null;
                        _textClipRuns.Clear();
                        _textClipExact = true;
                        break;

                    case "ET":
                        inText = false;
                        if (textClip is PdfRect tc)
                        {
                            EndTextClip(gs, tc);
                        }
                        textClip = null;
                        _textClipRuns.Clear();
                        break;

                    case "Tf":
                        if (operands.Count >= 2 && operands[^2].TryGetName(out string fontName) && operands[^1].TryGetNumber(out double size))
                        {
                            gs.CurrentFontResource = fontName;
                            gs.FontSize = size;
                            gs.Font = _owner._fontResolver.ResolveFont(fontName, resources);
                        }
                        break;
                    case "Tc":
                        if (TryNumbers(operands, 1, out var tcv)) gs.CharacterSpacing = tcv[0];
                        break;
                    case "Tw":
                        if (TryNumbers(operands, 1, out var twv)) gs.WordSpacing = twv[0];
                        break;
                    case "Tz":
                        if (TryNumbers(operands, 1, out var tzv)) gs.HorizontalScaling = tzv[0];
                        break;
                    case "TL":
                        if (TryNumbers(operands, 1, out var tlv)) gs.Leading = tlv[0];
                        break;
                    case "Tr":
                        if (TryNumbers(operands, 1, out var trv)) gs.TextRenderingMode = Math.Clamp((int)trv[0], 0, 7);
                        break;
                    case "Ts":
                        if (TryNumbers(operands, 1, out var tsv)) gs.TextRise = tsv[0];
                        break;
                    case "Td":
                        if (TryNumbers(operands, 2, out var td))
                            MoveTextLine(gs, td[0], td[1]);
                        break;
                    case "TD":
                        if (TryNumbers(operands, 2, out var tdd))
                        {
                            gs.Leading = -tdd[1];
                            MoveTextLine(gs, tdd[0], tdd[1]);
                        }
                        break;
                    case "Tm":
                        if (TryNumbers(operands, 6, out var tm))
                        {
                            gs.TextMatrix = new PdfMatrix(tm[0], tm[1], tm[2], tm[3], tm[4], tm[5]);
                            gs.TextLineMatrix = gs.TextMatrix;
                        }
                        break;
                    case "T*":
                        MoveTextLine(gs, 0, -gs.Leading);
                        break;

                    case "Tj":
                        if (operands.Count >= 1 && operands[^1] is PdfString tj)
                            ShowText(gs, resources, ctx, hidden, ref textClip, tj, null);
                        break;
                    case "'":
                        MoveTextLine(gs, 0, -gs.Leading);
                        if (operands.Count >= 1 && operands[^1] is PdfString q1)
                            ShowText(gs, resources, ctx, hidden, ref textClip, q1, null);
                        break;
                    case "\"":
                        if (operands.Count >= 3 && operands[^3].TryGetNumber(out double aw) &&
                            operands[^2].TryGetNumber(out double ac) && operands[^1] is PdfString q2)
                        {
                            gs.WordSpacing = aw;
                            gs.CharacterSpacing = ac;
                            MoveTextLine(gs, 0, -gs.Leading);
                            ShowText(gs, resources, ctx, hidden, ref textClip, q2, null);
                        }
                        break;
                    case "TJ":
                        if (operands.Count >= 1 && operands[^1] is PdfArray tjArr)
                            ShowText(gs, resources, ctx, hidden, ref textClip, null, tjArr);
                        break;

                    // ---------------------------------------------------------- Type3 glyph metrics
                    case "d0":
                        break;
                    case "d1":
                        // Uncoloured glyph: colour operators inside the procedure are ignored (9.6.4).
                        ctx = ctx with { UncoloredGlyph = true };
                        break;

                    // ---------------------------------------------------------- XObjects & inline images
                    case "Do":
                        if (LastName(operands) is string xName)
                            DoXObject(xName, resources, gs, ctx, hidden);
                        break;

                    case "BI":
                        operands.Clear();
                        ReadInlineImage(src, lexer, resources, gs, hidden);
                        break;

                    // ---------------------------------------------------------- marked content
                    case "BMC":
                        ocStack.Push(false);
                        break;
                    case "BDC":
                    {
                        bool isHidden = false;
                        if (operands.Count >= 2 && operands[^2].TryGetName(out string tag) && tag == "OC")
                        {
                            var props = operands[^1] is PdfName pn ? LookupResource(resources, "Properties", pn.Value) : _resolver.Resolve(operands[^1]);
                            isHidden = IsOptionalContentHidden(props);
                        }
                        ocStack.Push(isHidden);
                        if (isHidden) hiddenDepth++;
                        break;
                    }
                    case "EMC":
                        if (ocStack.Count > 0 && ocStack.Pop())
                            hiddenDepth--;
                        break;
                    case "MP":
                    case "DP":
                        break;
                    case "BX":
                        compatDepth++;
                        break;
                    case "EX":
                        if (compatDepth > 0) compatDepth--;
                        break;

                    default:
                        // Unknown operators inside BX/EX must be ignored (7.8.2). Elsewhere they mean
                        // content we do not understand: classify rather than guess.
                        if (compatDepth == 0)
                        {
                            AddFallback(PdfFallbackReason.UnknownOperator,
                                $"Unsupported PDF operator '{Sanitize(op)}'", gs.ClipBoundsPage);
                        }
                        break;
                }

                operands.Clear();
                _ = inText;
            }

            // Content streams must balance q/Q; unwind what the stream left open so the display
            // list stays balanced for the replaying backend.
            while (stack.Count > 0)
            {
                CopyInto(stack.Pop(), gs);
                _commands.Add(new RestoreState());
            }
        }

        private void Tick()
        {
            _operators++;
            if ((_operators & 0xFF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
                if (_clock.Elapsed > _limits.MaxPageBuildTime)
                    throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxPageBuildTime), "Page interpretation exceeded its time budget.");
            }
            if (_operators > _limits.MaxOperatorsPerPage)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxOperatorsPerPage), "Page exceeded the operator budget.");
            if (_commands.Count > _limits.MaxCommandsPerPage)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxCommandsPerPage), "Page exceeded the draw-command budget.");
        }

        private void AddSegment(PathState path, PdfPathSegment segment)
        {
            if (++_segments > _limits.MaxPathSegmentsPerPage)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxPathSegmentsPerPage), "Page exceeded the path-segment budget.");
            path.Segments.Add(segment);
        }

        private static void CopyInto(GraphicsState from, GraphicsState to)
        {
            // Q restores everything except the text matrices, which are not part of the gstate stack
            // in a way that survives outside BT/ET anyway; copying the whole state is the spec behaviour.
            var clone = from.Clone();
            to.CTM = clone.CTM;
            to.LineWidth = clone.LineWidth;
            to.LineCap = clone.LineCap;
            to.LineJoin = clone.LineJoin;
            to.MiterLimit = clone.MiterLimit;
            to.DashArray = clone.DashArray;
            to.DashPhase = clone.DashPhase;
            to.StrokeColor = clone.StrokeColor;
            to.FillColor = clone.FillColor;
            to.StrokeColorSpace = clone.StrokeColorSpace;
            to.FillColorSpace = clone.FillColorSpace;
            to.FillPattern = clone.FillPattern;
            to.StrokePattern = clone.StrokePattern;
            to.StrokeAlpha = clone.StrokeAlpha;
            to.FillAlpha = clone.FillAlpha;
            to.BlendMode = clone.BlendMode;
            to.SoftMaskActive = clone.SoftMaskActive;
            to.SoftMask = clone.SoftMask;
            to.AlphaIsShape = clone.AlphaIsShape;
            to.ClipPath = clone.ClipPath;
            to.ClipRule = clone.ClipRule;
            to.ClipBoundsPage = clone.ClipBoundsPage;
            to.CurrentFontResource = clone.CurrentFontResource;
            to.Font = clone.Font;
            to.FontSize = clone.FontSize;
            to.CharacterSpacing = clone.CharacterSpacing;
            to.WordSpacing = clone.WordSpacing;
            to.HorizontalScaling = clone.HorizontalScaling;
            to.Leading = clone.Leading;
            to.TextRenderingMode = clone.TextRenderingMode;
            to.TextRise = clone.TextRise;
            to.TextMatrix = clone.TextMatrix;
            to.TextLineMatrix = clone.TextLineMatrix;
        }

        // ------------------------------------------------------------------ operands

        private static bool TryNumbers(List<PdfObject> operands, int count, out double[] values)
        {
            values = Array.Empty<double>();
            if (operands.Count < count)
                return false;
            var result = new double[count];
            int start = operands.Count - count;
            for (int i = 0; i < count; i++)
            {
                if (!operands[start + i].TryGetNumber(out double v) || double.IsNaN(v) || double.IsInfinity(v))
                    return false;
                result[i] = v;
            }
            values = result;
            return true;
        }

        private static string? LastName(List<PdfObject> operands) =>
            operands.Count >= 1 && operands[^1].TryGetName(out string name) ? name : null;

        private static string Sanitize(string op) => op.Length > 16 ? op[..16] : op;

        private PdfObject? LookupResource(PdfDictionary? resources, string category, string name)
        {
            if (resources == null || !resources.TryGetValue(category, out var catObj))
                return null;
            if (_resolver.Resolve(catObj) is not PdfDictionary cat || !cat.TryGetValue(name, out var entry))
                return null;
            return _resolver.Resolve(entry);
        }

        // ------------------------------------------------------------------ bounds & fallback

        private static PdfRect Intersect(PdfRect a, PdfRect b)
        {
            double x0 = Math.Max(a.Left, b.Left), y0 = Math.Max(a.Top, b.Top);
            double x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
            return x1 > x0 && y1 > y0 ? new PdfRect(x0, y0, x1 - x0, y1 - y0) : PdfRect.Empty;
        }

        private static PdfRect Union(PdfRect a, PdfRect b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            double x0 = Math.Min(a.Left, b.Left), y0 = Math.Min(a.Top, b.Top);
            double x1 = Math.Max(a.Right, b.Right), y1 = Math.Max(a.Bottom, b.Bottom);
            return new PdfRect(x0, y0, x1 - x0, y1 - y0);
        }

        private static PdfRect Inflate(PdfRect r, double d) =>
            new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

        /// <summary>User-space box → conservative page-space box, clipped to the current clip.</summary>
        private static PdfRect ToPage(GraphicsState gs, PdfRect userRect, double inflate = 0)
        {
            var r = gs.CTM.Transform(inflate > 0 ? Inflate(userRect, inflate) : userRect);
            // Zero-area geometry (hairlines, degenerate rects) still paints a device pixel.
            if (r.Width <= 0 || r.Height <= 0)
                r = Inflate(r, 0.5);
            return Intersect(r, gs.ClipBoundsPage);
        }

        private void AddFallback(PdfFallbackReason reason, string description, PdfRect pageBounds,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            // Anti-aliasing bleeds a little beyond geometric bounds.
            var bounds = Intersect(Inflate(pageBounds, 1.0), Crop);
            if (bounds.IsEmpty)
                return;
            var token = new PdfFallbackToken(_page.PageNumber, bounds, reason, description, metadata);
            _fallbacks.Add(token);
            _commands.Add(new DrawFallbackRegion(token, bounds));
            _features |= PdfFeatureSet.Fallback;
        }

        private void PageFallback(PdfFallbackReason reason, string description) =>
            AddFallback(reason, description, Crop);

        private static PdfFallbackReason? PaintUnsupported(GraphicsState gs, bool stroke)
        {
            // Blend modes and captured soft masks are composited natively (BeginCompositingGroup);
            // only a soft mask that could not be captured still falls back.
            if (gs.SoftMaskActive && gs.SoftMask == null) return PdfFallbackReason.SoftMask;
            var cs = stroke ? gs.StrokeColorSpace : gs.FillColorSpace;
            if (cs.IsPattern) return null; // handled by the pattern paint path
            return cs.UnsupportedReason;
        }

        // ------------------------------------------------------------------ paths

        private void PaintPath(PathState path, GraphicsState gs, PdfDictionary resources, ContentContext ctx,
            bool hidden, bool fill, bool stroke, PdfFillRule rule, bool close)
        {
            if (close && path.Segments.Count > 0)
                AddSegment(path, new PdfCloseSubpath());

            if (path.Segments.Count == 0)
            {
                path.ClipPending = false;
                return;
            }

            var pdfPath = new PdfPath(path.Segments.ToArray());

            if (!hidden && (fill || stroke))
            {
                _features |= PdfFeatureSet.Paths;
                double strokeExpand = stroke ? StrokeExpansion(gs) : 0;
                var pageBounds = ToPage(gs, pdfPath.Bounds, strokeExpand);

                if (!pageBounds.IsEmpty)
                {
                    if (fill)
                        PaintFill(pdfPath, rule, gs, resources, pageBounds);
                    if (stroke)
                        PaintStroke(pdfPath, gs, pageBounds);
                }
            }

            if (path.ClipPending)
            {
                _features |= PdfFeatureSet.Clipping;
                _commands.Add(new PushClip(pdfPath, path.ClipRule, ToPage(gs, pdfPath.Bounds)));
                gs.ClipBoundsPage = Intersect(gs.ClipBoundsPage, gs.CTM.Transform(pdfPath.Bounds));
                path.ClipPending = false;
            }

            path.Segments.Clear();
        }

        private static double StrokeExpansion(GraphicsState gs)
        {
            double half = Math.Max(gs.LineWidth, 1.0) / 2.0;
            if (gs.LineJoin == PdfLineJoin.Miter)
                half *= Math.Max(1.0, gs.MiterLimit);
            else if (gs.LineCap == PdfLineCap.Square)
                half *= Math.Sqrt(2);
            return half;
        }

        private void PaintFill(PdfPath pdfPath, PdfFillRule rule, GraphicsState gs, PdfDictionary resources, PdfRect pageBounds)
        {
            if (PaintUnsupported(gs, stroke: false) is PdfFallbackReason reason)
            {
                AddFallback(reason, "Fill uses an unsupported paint", pageBounds);
                return;
            }

            bool composite = BeginComposite(gs, pageBounds);
            if (gs.FillColorSpace.IsPattern)
                PaintPatternFill(pdfPath, rule, gs, pageBounds);
            else
                _commands.Add(new FillPath(pdfPath, gs.CreateFillPaint(), rule, pageBounds));
            EndComposite(composite);
        }

        /// <summary>
        /// An object painted under a non-Normal blend mode or a soft mask is composited as a group
        /// of its own (the specified result for a single object, ISO 32000-2 11.3, 11.6.5).
        /// </summary>
        private bool BeginComposite(GraphicsState gs, PdfRect bounds)
        {
            var blend = gs.Blend;
            if (blend == PdfBlendMode.Normal && gs.SoftMask == null)
                return false;
            _features |= PdfFeatureSet.Transparency;
            _commands.Add(new BeginCompositingGroup(bounds, 1.0, blend, gs.SoftMask, Isolated: true, Knockout: false));
            return true;
        }

        private void EndComposite(bool open)
        {
            if (open)
                _commands.Add(new EndCompositingGroup());
        }

        private void PaintStroke(PdfPath pdfPath, GraphicsState gs, PdfRect pageBounds)
        {
            if (PaintUnsupported(gs, stroke: true) is PdfFallbackReason reason)
            {
                AddFallback(reason, "Stroke uses an unsupported paint", pageBounds);
                return;
            }

            bool composite = BeginComposite(gs, pageBounds);
            if (gs.StrokeColorSpace.IsPattern)
                PaintPatternFill(pdfPath, PdfFillRule.NonZero, gs, pageBounds, gs.CreateStroke());
            else
                _commands.Add(new StrokePath(pdfPath, gs.CreateStroke(), gs.CreateStrokePaint(), pageBounds));
            EndComposite(composite);
        }

        /// <summary>
        /// Paints the current fill (or, with <paramref name="stroke"/>, stroke) pattern inside the
        /// path's area (or stroke outline).
        /// </summary>
        private void PaintPatternFill(PdfPath? pdfPath, PdfFillRule rule, GraphicsState gs, PdfRect pageBounds, PdfStroke? stroke = null,
            PdfDrawCommand? clipOverride = null)
        {
            _features |= PdfFeatureSet.Patterns;
            var pattern = stroke != null ? gs.StrokePattern : gs.FillPattern;
            double alpha = stroke != null ? gs.StrokeAlpha : gs.FillAlpha;
            PdfDrawCommand clip = clipOverride
                ?? (stroke != null ? new PushStrokeClip(pdfPath!, stroke, pageBounds) : new PushClip(pdfPath!, rule, pageBounds));
            var patternDict = pattern switch
            {
                PdfStream ps => ps.Dictionary,
                PdfDictionary pd => pd,
                _ => null,
            };

            if (patternDict == null)
            {
                // Unresolvable pattern: PDF viewers paint nothing; say so instead of guessing a colour.
                AddFallback(PdfFallbackReason.Pattern, "Unresolvable pattern", pageBounds);
                return;
            }

            long patternType = patternDict.GetInteger("PatternType") ?? 0;
            if (patternType == 1 && pattern is PdfStream tiling)
            {
                PaintTilingFill(clip, gs, pageBounds, tiling, alpha, stroke != null ? gs.StrokeColor : gs.FillColor);
                return;
            }
            if (patternType != 2)
            {
                AddFallback(PdfFallbackReason.Pattern, "Tiling pattern", pageBounds);
                return;
            }

            var shadingDict = _resolver.Resolve(patternDict["Shading"]) switch
            {
                PdfStream s => s.Dictionary,
                PdfDictionary d => d,
                _ => null,
            };
            if (shadingDict == null || BuildShading(shadingDict, out var reason) is not PdfShading shading)
            {
                AddFallback(PdfFallbackReason.Shading, "Unsupported shading pattern", pageBounds);
                return;
            }
            _ = reason;

            if (_resolver.Resolve(patternDict["ExtGState"]) is PdfDictionary)
            {
                AddFallback(PdfFallbackReason.Pattern, "Shading pattern with its own graphics state", pageBounds);
                return;
            }

            // Pattern space maps to the default space of the pattern's parent stream (8.7.2), not the CTM.
            var patternMatrix = ReadMatrix(patternDict["Matrix"]) ?? PdfMatrix.Identity;
            var target = patternMatrix * _currentPatternBase;
            if (!gs.CTM.TryInvert(out var invCtm))
                return;

            _features |= PdfFeatureSet.Shading;
            _commands.Add(new SaveState());
            _commands.Add(clip);
            _commands.Add(new ConcatTransform(target * invCtm));
            bool group = alpha < 1.0;
            if (group) _commands.Add(new BeginTransparencyGroup(pageBounds, alpha));
            _commands.Add(new DrawShading(shading, pageBounds));
            if (group) _commands.Add(new EndTransparencyGroup());
            _commands.Add(new RestoreState());
        }

        private PdfMatrix _currentPatternBase = PdfMatrix.Identity;

        // ------------------------------------------------------------------ tiling patterns

        private readonly Dictionary<(PdfStream Pattern, PdfColor? Color), PdfTilingPattern?> _tilings = new();
        private int _tilingDepth;
        private const int MaxTilingDepth = 4;

        private void PaintTilingFill(PdfDrawCommand clip, GraphicsState gs, PdfRect pageBounds, PdfStream patternStream, double alpha, PdfColor color)
        {
            var d = patternStream.Dictionary;
            bool uncolored = (d.GetInteger("PaintType") ?? 1) == 2;
            var tiling = CaptureTiling(patternStream, uncolored ? color : null);
            if (tiling == null)
            {
                AddFallback(PdfFallbackReason.Pattern, "Tiling pattern could not be reproduced", pageBounds);
                return;
            }

            // Pattern space maps to the default space of the pattern's parent stream (8.7.2), not the CTM.
            var patternMatrix = ReadMatrix(d["Matrix"]) ?? PdfMatrix.Identity;
            var target = patternMatrix * _currentPatternBase;
            if (!gs.CTM.TryInvert(out var invCtm))
                return;

            _commands.Add(new SaveState());
            _commands.Add(clip);
            _commands.Add(new ConcatTransform(target * invCtm));
            bool group = alpha < 1.0;
            if (group) _commands.Add(new BeginTransparencyGroup(pageBounds, alpha));
            _commands.Add(new DrawTilingPattern(tiling, pageBounds));
            if (group) _commands.Add(new EndTransparencyGroup());
            _commands.Add(new RestoreState());
        }

        /// <summary>
        /// Records one pattern cell in pattern space. Null (fallback) for malformed steps or boxes,
        /// self-referencing or too deeply nested patterns, and cells whose content needs fallback.
        /// </summary>
        private PdfTilingPattern? CaptureTiling(PdfStream patternStream, PdfColor? color)
        {
            var key = (patternStream, color);
            if (_tilings.TryGetValue(key, out var cached))
                return cached;

            PdfTilingPattern? result = null;
            var d = patternStream.Dictionary;
            if (ReadRect(d["BBox"]) is PdfRect bbox && !bbox.IsEmpty &&
                _resolver.Resolve(d["XStep"]) is { } xo && xo.TryGetNumber(out double xStep) &&
                _resolver.Resolve(d["YStep"]) is { } yo && yo.TryGetNumber(out double yStep) &&
                Math.Abs(xStep) > 1e-6 && Math.Abs(yStep) > 1e-6 && Math.Abs(xStep) < 1e7 && Math.Abs(yStep) < 1e7 &&
                _tilingDepth < MaxTilingDepth && _activeForms.Add(patternStream))
            {
                int mark = _commands.Count, fallbackMark = _fallbacks.Count;
                var savedBase = _currentPatternBase;
                _tilingDepth++;
                try
                {
                    var cellState = new GraphicsState { CTM = PdfMatrix.Identity, ClipBoundsPage = bbox };
                    if (color is PdfColor c)
                    {
                        // Uncoloured pattern: the cell paints in the colour given with scn.
                        cellState.FillColorSpace = PdfColorSpace.DeviceRgb;
                        cellState.StrokeColorSpace = PdfColorSpace.DeviceRgb;
                        cellState.FillColor = c;
                        cellState.StrokeColor = c;
                    }
                    _currentPatternBase = PdfMatrix.Identity; // nested patterns map to this pattern's space
                    byte[] content = _owner._streamDecoder.DecodeStream(patternStream);
                    var resources = _resolver.Resolve(d["Resources"]) as PdfDictionary ?? _page.Resources;
                    _commands.Add(new SaveState());
                    _commands.Add(new PushClip(RectPath(bbox), PdfFillRule.NonZero, bbox));
                    Execute(content, resources, cellState, new ContentContext(PdfMatrix.Identity, 0, 0, color != null));
                    _commands.Add(new RestoreState());

                    if (_fallbacks.Count == fallbackMark)
                    {
                        var cell = new PdfDisplayList(_page.PageNumber, _page.PageSize, _page.MediaBox, _page.CropBox, _page.RotationDegrees,
                            _commands.GetRange(mark, _commands.Count - mark), _features & ~PdfFeatureSet.Fallback, Array.Empty<PdfFallbackToken>());
                        result = new PdfTilingPattern(cell, bbox, Math.Abs(xStep), Math.Abs(yStep));
                    }
                }
                catch (Exception ex) when (ex is PdfVectorException or InvalidDataException or FormatException
                                               or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
                {
                    result = null;
                }
                finally
                {
                    _tilingDepth--;
                    _currentPatternBase = savedBase;
                    _activeForms.Remove(patternStream);
                    _commands.RemoveRange(mark, _commands.Count - mark);
                    _fallbacks.RemoveRange(fallbackMark, _fallbacks.Count - fallbackMark);
                }
            }
            _tilings[key] = result;
            return result;
        }

        private PdfMatrix? ReadMatrix(PdfObject? obj)
        {
            if (_resolver.Resolve(obj) is not PdfArray arr || arr.Count < 6)
                return null;
            var v = new double[6];
            for (int i = 0; i < 6; i++)
            {
                if (!_resolver.Resolve(arr[i])!.TryGetNumber(out v[i]) || double.IsNaN(v[i]) || double.IsInfinity(v[i]))
                    return null;
            }
            return new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        }

        private PdfRect? ReadRect(PdfObject? obj)
        {
            if (_resolver.Resolve(obj) is not PdfArray arr || arr.Count < 4)
                return null;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!(_resolver.Resolve(arr[i])?.TryGetNumber(out v[i]) ?? false))
                    return null;
            }
            double x0 = Math.Min(v[0], v[2]), x1 = Math.Max(v[0], v[2]);
            double y0 = Math.Min(v[1], v[3]), y1 = Math.Max(v[1], v[3]);
            return new PdfRect(x0, y0, x1 - x0, y1 - y0);
        }

        private static PdfPath RectPath(PdfRect r) => new(new PdfPathSegment[]
        {
            new PdfMoveTo(new(r.Left, r.Top)),
            new PdfLineTo(new(r.Right, r.Top)),
            new PdfLineTo(new(r.Right, r.Bottom)),
            new PdfLineTo(new(r.Left, r.Bottom)),
            new PdfCloseSubpath(),
        });

        /// <summary>Page-space rectangle expressed as a (possibly skewed) path in current user space.</summary>
        private static PdfPath? PageRectInUserSpace(GraphicsState gs, PdfRect pageRect)
        {
            if (!gs.CTM.TryInvert(out var inv))
                return null;
            var p0 = inv.Transform(pageRect.Left, pageRect.Top);
            var p1 = inv.Transform(pageRect.Right, pageRect.Top);
            var p2 = inv.Transform(pageRect.Right, pageRect.Bottom);
            var p3 = inv.Transform(pageRect.Left, pageRect.Bottom);
            return new PdfPath(new PdfPathSegment[]
            {
                new PdfMoveTo(p0), new PdfLineTo(p1), new PdfLineTo(p2), new PdfLineTo(p3), new PdfCloseSubpath(),
            });
        }

        private static void SetDash(GraphicsState gs, PdfArray dashArr, double phase)
        {
            var list = new List<double>(dashArr.Count);
            double total = 0;
            foreach (var item in dashArr)
            {
                if (item.TryGetNumber(out double dv) && dv >= 0 && !double.IsInfinity(dv))
                {
                    list.Add(dv);
                    total += dv;
                }
            }
            // An all-zero dash array is invalid (8.4.3.6); treat it as a solid line.
            gs.DashArray = list.Count == 0 || total <= 0 ? null : list;
            gs.DashPhase = phase;
        }

        // ------------------------------------------------------------------ colour

        private void SetColorSpace(GraphicsState gs, bool stroke, string name, PdfDictionary resources)
        {
            PdfColorSpace cs = name == "Pattern"
                ? PdfColorSpace.Resolve(new PdfName("Pattern"), _resolver, resources)
                : PdfColorSpace.Resolve(new PdfName(name), _resolver, resources);
            var initial = cs.InitialComponents;
            var color = cs.IsPattern ? PdfColor.Black : cs.ToRgbColor(initial, 1f);
            if (stroke)
            {
                gs.StrokeColorSpace = cs;
                gs.StrokeColor = color;
                gs.StrokePattern = null;
            }
            else
            {
                gs.FillColorSpace = cs;
                gs.FillColor = color;
                gs.FillPattern = null;
            }
        }

        private void SetColor(GraphicsState gs, bool stroke, List<PdfObject> operands, PdfDictionary resources)
        {
            var cs = stroke ? gs.StrokeColorSpace : gs.FillColorSpace;
            if (cs.IsPattern)
            {
                if (LastName(operands) is string patternName)
                {
                    var pattern = LookupResource(resources, "Pattern", patternName);
                    if (stroke) gs.StrokePattern = pattern; else gs.FillPattern = pattern;

                    // [/Pattern base] with components before the name: the uncoloured pattern's colour.
                    if (cs.PatternBaseColorSpace is { } baseCs)
                    {
                        int bn = baseCs.NumberOfComponents;
                        var bcomps = new double[bn];
                        int got = 0;
                        for (int i = operands.Count - 2; i >= 0 && got < bn; i--)
                        {
                            if (!operands[i].TryGetNumber(out double v)) break;
                            bcomps[bn - 1 - got] = v;
                            got++;
                        }
                        if (got == bn)
                        {
                            var pc = baseCs.ToRgbColor(bcomps, 1f);
                            if (stroke) gs.StrokeColor = pc; else gs.FillColor = pc;
                        }
                    }
                }
                return;
            }

            int n = cs.NumberOfComponents;
            var comps = new double[n];
            int available = 0;
            for (int i = operands.Count - 1; i >= 0 && available < n; i--)
            {
                if (!operands[i].TryGetNumber(out double v)) break;
                comps[n - 1 - available] = v;
                available++;
            }
            if (available < n)
                return; // malformed: keep the previous colour

            var color = cs.ToRgbColor(comps, 1f);
            if (stroke) gs.StrokeColor = color; else gs.FillColor = color;
        }

        private static void SetDeviceColor(GraphicsState gs, bool stroke, PdfColorSpace cs, double[] comps)
        {
            var color = cs.ToRgbColor(comps, 1f);
            if (stroke)
            {
                gs.StrokeColorSpace = cs;
                gs.StrokeColor = color;
                gs.StrokePattern = null;
            }
            else
            {
                gs.FillColorSpace = cs;
                gs.FillColor = color;
                gs.FillPattern = null;
            }
        }

        // ------------------------------------------------------------------ ExtGState

        private void ApplyExtGState(string gsName, PdfDictionary resources, GraphicsState gs)
        {
            if (LookupResource(resources, "ExtGState", gsName) is not PdfDictionary d)
                return;

            foreach (var (key, raw) in d.Entries)
            {
                var value = _resolver.Resolve(raw);
                if (value == null) continue;
                switch (key)
                {
                    case "LW": if (value.TryGetNumber(out double lw)) gs.LineWidth = Math.Max(0, lw); break;
                    case "LC": if (value.TryGetInteger(out long lc)) gs.LineCap = (PdfLineCap)Math.Clamp((int)lc, 0, 2); break;
                    case "LJ": if (value.TryGetInteger(out long lj)) gs.LineJoin = (PdfLineJoin)Math.Clamp((int)lj, 0, 2); break;
                    case "ML": if (value.TryGetNumber(out double mlv)) gs.MiterLimit = Math.Max(1, mlv); break;
                    case "D":
                        if (value is PdfArray da && da.Count >= 2 && _resolver.Resolve(da[0]) is PdfArray dashes && da[1].TryGetNumber(out double ph))
                            SetDash(gs, dashes, ph);
                        break;
                    case "CA":
                        if (value.TryGetNumber(out double ca)) { gs.StrokeAlpha = Math.Clamp(ca, 0, 1); if (ca < 1) _features |= PdfFeatureSet.Transparency; }
                        break;
                    case "ca":
                        if (value.TryGetNumber(out double cas)) { gs.FillAlpha = Math.Clamp(cas, 0, 1); if (cas < 1) _features |= PdfFeatureSet.Transparency; }
                        break;
                    case "BM":
                        // Blend mode may be an array; the first recognised mode applies (11.3.5).
                        string? bm = value is PdfArray bmArr && bmArr.Count > 0 && bmArr[0].TryGetName(out string first) ? first
                                   : value.TryGetName(out string single) ? single : null;
                        if (bm != null)
                        {
                            gs.BlendMode = bm;
                            if (!gs.IsNormalBlend) _features |= PdfFeatureSet.Transparency;
                        }
                        break;
                    case "SMask":
                        gs.SoftMaskActive = !(value is PdfName n && n.Value == "None");
                        gs.SoftMask = gs.SoftMaskActive && value is PdfDictionary smd ? CaptureSoftMask(smd, gs, resources) : null;
                        if (gs.SoftMaskActive) _features |= PdfFeatureSet.Transparency;
                        break;
                    case "AIS":
                        gs.AlphaIsShape = value.TryGetBoolean(out bool ais) && ais;
                        break;
                    case "Font":
                        if (value is PdfArray fa && fa.Count >= 2 && _resolver.Resolve(fa[0]) is PdfDictionary fontDict && fa[1].TryGetNumber(out double fsz))
                        {
                            gs.Font = _owner._fontResolver.ResolveFont(fontDict, gsName + "#Font");
                            gs.FontSize = fsz;
                        }
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ soft masks

        private readonly Dictionary<(PdfDictionary Mask, PdfMatrix Ctm), PdfSoftMask?> _softMasks = new();
        private int _softMaskDepth;
        private const int MaxSoftMaskDepth = 4;

        /// <summary>
        /// Records a soft mask's group into its own display list, in page space, using the CTM in
        /// effect when the ExtGState is set (ISO 32000-2 11.6.5.2). Null when the mask cannot be
        /// reproduced natively: an unknown subtype, an unusable transfer function, or mask content
        /// that itself needs fallback.
        /// </summary>
        private PdfSoftMask? CaptureSoftMask(PdfDictionary smd, GraphicsState gs, PdfDictionary resources)
        {
            var key = (smd, gs.CTM);
            if (_softMasks.TryGetValue(key, out var cached))
                return cached;
            PdfSoftMask? mask = null;
            if (_softMaskDepth < MaxSoftMaskDepth)
            {
                _softMaskDepth++;
                try
                {
                    mask = BuildSoftMask(smd, gs);
                }
                catch (Exception ex) when (ex is PdfVectorException or InvalidDataException or FormatException
                                               or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
                {
                    mask = null;
                }
                finally
                {
                    _softMaskDepth--;
                }
            }
            _softMasks[key] = mask;
            return mask;
        }

        private PdfSoftMask? BuildSoftMask(PdfDictionary smd, GraphicsState gs)
        {
            string? subtype = smd.GetName("S");
            bool luminosity = subtype == "Luminosity";
            if (!luminosity && subtype != "Alpha")
                return null;
            if (_resolver.Resolve(smd["G"]) is not PdfStream group)
                return null;

            float[]? transfer = null;
            var tr = _resolver.Resolve(smd["TR"]);
            if (tr != null && tr is not PdfName { Value: "Identity" })
            {
                var fn = PdfFunction.Parse(tr, _resolver, _limits, PdfFallbackReason.SoftMask);
                if (fn.InputCount != 1 || fn.OutputCount < 1)
                    return null;
                transfer = new float[256];
                Span<double> input = stackalloc double[1];
                var output = new double[fn.OutputCount];
                for (int i = 0; i < 256; i++)
                {
                    input[0] = i / 255.0;
                    fn.Evaluate(input, output);
                    transfer[i] = (float)Math.Clamp(output[0], 0, 1);
                }
            }

            var backdrop = new PdfColor(0, 0, 0);
            if (luminosity && _resolver.Resolve(smd["BC"]) is PdfArray bc && bc.Count > 0)
            {
                var groupDict = _resolver.Resolve(group.Dictionary["Group"]) as PdfDictionary;
                var groupResources = _resolver.Resolve(group.Dictionary["Resources"]) as PdfDictionary ?? _page.Resources;
                var cs = groupDict?["CS"] is { } csObj
                    ? PdfColorSpace.Resolve(csObj, _resolver, groupResources)
                    : bc.Count >= 4 ? PdfColorSpace.DeviceCmyk : bc.Count == 3 ? PdfColorSpace.DeviceRgb : PdfColorSpace.DeviceGray;
                var comps = new double[cs.NumberOfComponents];
                for (int i = 0; i < comps.Length && i < bc.Count; i++)
                    comps[i] = _resolver.Resolve(bc[i]) is { } c && c.TryGetNumber(out double v) ? v : 0;
                backdrop = cs.ToRgbColor(comps);
            }

            // The mask group is painted with the CTM at the time of gs and otherwise default state.
            int mark = _commands.Count, fallbackMark = _fallbacks.Count;
            var maskState = new GraphicsState { CTM = gs.CTM, ClipBoundsPage = Crop };
            _commands.Add(new SaveState());
            _commands.Add(new ConcatTransform(gs.CTM));
            var savedBase = _currentPatternBase;
            try
            {
                ExecuteForm(group, _page.Resources, maskState, new ContentContext(gs.CTM, 0, 0, false), hidden: false);
            }
            finally
            {
                _currentPatternBase = savedBase;
            }
            _commands.Add(new RestoreState());

            var content = _commands.GetRange(mark, _commands.Count - mark);
            bool needsFallback = _fallbacks.Count > fallbackMark;
            _commands.RemoveRange(mark, _commands.Count - mark);
            _fallbacks.RemoveRange(fallbackMark, _fallbacks.Count - fallbackMark);
            if (needsFallback)
                return null;

            var list = new PdfDisplayList(_page.PageNumber, _page.PageSize, _page.MediaBox, _page.CropBox, _page.RotationDegrees,
                content, _features & ~PdfFeatureSet.Fallback, Array.Empty<PdfFallbackToken>());
            return new PdfSoftMask(luminosity, list, PdfMatrix.Identity, backdrop, transfer);
        }

        // ------------------------------------------------------------------ text

        private static void MoveTextLine(GraphicsState gs, double tx, double ty)
        {
            gs.TextLineMatrix = PdfMatrix.CreateTranslation(tx, ty) * gs.TextLineMatrix;
            gs.TextMatrix = gs.TextLineMatrix;
        }

        private void ShowText(GraphicsState gs, PdfDictionary resources, ContentContext ctx, bool hidden,
            ref PdfRect? textClip, PdfString? single, PdfArray? tj)
        {
            _features |= PdfFeatureSet.Text;
            var font = gs.Font ??= _owner._fontResolver.ResolveFont(gs.CurrentFontResource, resources);

            if (font.IsType3)
            {
                ShowType3Text(gs, resources, ctx, hidden, ref textClip, font, single, tj);
                return;
            }

            double fs = gs.FontSize;
            double th = gs.HorizontalScaling / 100.0;
            var glyphs = new List<PdfGlyph>();
            var text = new StringBuilder();
            double x = 0, y = 0;
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            bool vertical = font.IsVertical;
            bool allGlyphIdsKnown = true;
            var prepared = _owner.PrepareFont(font);
            var face = prepared.Face;
            bool faceHasProgram = prepared.Program != null;

            void AddString(PdfString s)
            {
                var bytes = s.RawBytes.Span;
                int pos = 0;
                while (pos < bytes.Length)
                {
                    int consumed = font.ReadCode(bytes, pos, out int code, out int cid);
                    if (consumed <= 0) consumed = 1;
                    pos += consumed;

                    if (++_glyphs > _limits.MaxGlyphsPerPage)
                        throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxGlyphsPerPage), "Page exceeded the glyph budget.");

                    double w0 = font.GetGlyphWidth(font.IsComposite ? cid : code) / 1000.0;
                    double tw = font.IsWordSpaceCode(code, consumed) ? gs.WordSpacing : 0;

                    int gid = faceHasProgram ? prepared.Program!.GetGlyphId(code, cid) : -1;
                    if (faceHasProgram && gid < 0) allGlyphIdsKnown = false;
                    string unicode = font.MapToUnicode(code);
                    ushort glyphId = (ushort)Math.Clamp(gid >= 0 ? gid : cid, 0, ushort.MaxValue);

                    if (vertical)
                    {
                        // Vertical writing (9.7.4.3): the glyph's position vector v = (VX, VY) is placed
                        // on the current point; the point moves by ty = W1y·Tfs + Tc + Tw (no Th).
                        var vm = font.GetVerticalMetrics(cid);
                        double ox = -vm.VX / 1000.0 * fs * th, oy = y - vm.VY / 1000.0 * fs;
                        double ty = vm.W1y / 1000.0 * fs + gs.CharacterSpacing + tw;
                        glyphs.Add(new PdfGlyph(glyphId, 0, ty, ox, oy, unicode, code));
                        text.Append(unicode);
                        minX = Math.Min(minX, ox);
                        maxX = Math.Max(maxX, ox + w0 * fs * th);
                        minY = Math.Min(minY, Math.Min(y, y + ty));
                        maxY = Math.Max(maxY, Math.Max(y, y + ty));
                        y += ty;
                        continue;
                    }

                    double advance = (w0 * fs + gs.CharacterSpacing + tw) * th;
                    glyphs.Add(new PdfGlyph(glyphId, advance, 0, x, 0, unicode, code));
                    text.Append(unicode);

                    minX = Math.Min(minX, Math.Min(x, x + advance));
                    maxX = Math.Max(maxX, Math.Max(x, x + advance));
                    x += advance;
                }
            }

            if (single != null)
            {
                AddString(single);
            }
            else if (tj != null)
            {
                foreach (var item in tj)
                {
                    if (item is PdfString s)
                        AddString(s);
                    else if (item.TryGetNumber(out double adj) && !double.IsNaN(adj) && !double.IsInfinity(adj))
                    {
                        // TJ numbers are thousandths of text space, subtracted (9.4.3); vertical: from ty.
                        if (vertical) y -= adj / 1000.0 * fs;
                        else x -= adj / 1000.0 * fs * th;
                    }
                }
            }

            var runStart = gs.TextMatrix;
            gs.TextMatrix = PdfMatrix.CreateTranslation(x, y) * gs.TextMatrix;

            if (glyphs.Count == 0)
                return;

            // Glyph box in text space → page space.
            double ascent = face.Ascent * Math.Abs(fs), descent = face.Descent * Math.Abs(fs);
            var textBox = vertical && glyphs.Count > 0
                ? new PdfRect(minX, gs.TextRise + glyphs.Min(g => g.OffsetY) + Math.Min(ascent, descent), Math.Max(0, maxX - minX),
                    glyphs.Max(g => g.OffsetY) - glyphs.Min(g => g.OffsetY) + Math.Abs(ascent - descent))
                : new PdfRect(minX, gs.TextRise + Math.Min(ascent, descent), Math.Max(0, maxX - minX), Math.Abs(ascent - descent));
            _ = minY; _ = maxY;
            var textToPage = runStart * gs.CTM;
            int mode = gs.TextRenderingMode;
            bool strokes = mode is 1 or 2 or 5 or 6;
            double inflate = strokes ? StrokeExpansion(gs) : 0;
            var pageBounds = Intersect(textToPage.Transform(Inflate(textBox, inflate)), gs.ClipBoundsPage);

            PdfFallbackReason? reason = font.UnsupportedReason;
            bool paints = mode is not 3 and not 7;
            if (reason == null && paints)
            {
                bool fills = mode is 0 or 2 or 4 or 6;
                reason = (fills ? PaintUnsupported(gs, false) : null) ?? (strokes ? PaintUnsupported(gs, true) : null);
                if (reason == null && ((fills && gs.FillColorSpace.IsPattern) || (strokes && gs.StrokeColorSpace.IsPattern)))
                    reason = PdfFallbackReason.Pattern;
            }

            var effectiveFace = faceHasProgram && !allGlyphIdsKnown
                ? face with { Format = PdfFontProgramFormat.None, ProgramData = ReadOnlyMemory<byte>.Empty }
                : face;

            // Pattern-filled text: the pattern is painted through the glyph outlines.
            bool patternText = !hidden && reason == PdfFallbackReason.Pattern && font.UnsupportedReason == null &&
                               mode is 0 or 4 && gs.FillColorSpace.IsPattern;
            if (patternText)
                reason = null;

            bool drawVisible = !hidden && paints && reason == null && !patternText;
            var run = new PdfGlyphRun(
                gs.CurrentFontResource,
                fs,
                glyphs,
                runStart,
                gs.HorizontalScaling,
                gs.CharacterSpacing,
                gs.WordSpacing,
                gs.TextRise,
                drawVisible ? mode : 3,
                FontFamilyName: font.BaseFont,
                FullText: text.ToString())
            {
                TextToPage = textToPage,
                Face = effectiveFace,
                StrokePaint = strokes ? gs.CreateStrokePaint() : null,
                Stroke = strokes ? gs.CreateStroke() : null,
            };

            // Invisible runs are kept: selection, search and copy work on them (OCR layers use mode 3).
            bool composite = (drawVisible || patternText) && BeginComposite(gs, pageBounds);
            _commands.Add(new DrawGlyphRun(run, gs.CreateFillPaint(), pageBounds));
            if (patternText && !pageBounds.IsEmpty)
                PaintPatternFill(null, PdfFillRule.NonZero, gs, pageBounds, clipOverride: new PushTextClip(new[] { run with { RenderingMode = 0 } }, pageBounds));
            EndComposite(composite);

            if (!hidden && paints && reason is PdfFallbackReason r && !pageBounds.IsEmpty)
            {
                AddFallback(r, "Text uses an unsupported font or paint", pageBounds,
                    new Dictionary<string, string> { ["font"] = font.PostScriptName ?? font.BaseFont, ["subtype"] = font.Subtype });
            }

            if (mode >= 4)
            {
                _features |= PdfFeatureSet.Clipping;
                textClip = Union(textClip ?? PdfRect.Empty, pageBounds);
                if (font.UnsupportedReason != null) _textClipExact = false;
                else _textClipRuns.Add(run with { RenderingMode = 7 });
            }
        }

        // Glyph runs of the current text object that add to the clip (modes 4–7).
        private readonly List<PdfGlyphRun> _textClipRuns = new();
        private bool _textClipExact = true;

        /// <summary>
        /// Text rendering modes 4–7 add glyph outlines to the clip at ET. The IR has no glyph-outline
        /// clip, so the clipped area is classified as fallback and later content is confined to the
        /// text's bounding box (a conservative superset of the true clip).
        /// </summary>
        private void EndTextClip(GraphicsState gs, PdfRect clipBounds)
        {
            if (_textClipExact && _textClipRuns.Count > 0)
            {
                // Glyph outlines as the clip (backends that cannot outline the font classify it).
                _commands.Add(new PushTextClip(_textClipRuns.ToArray(), clipBounds));
                gs.ClipBoundsPage = Intersect(gs.ClipBoundsPage, clipBounds);
                return;
            }
            AddFallback(PdfFallbackReason.TextClipping, "Text rendering mode adds glyphs to the clip", clipBounds);
            var userPath = PageRectInUserSpace(gs, clipBounds);
            if (userPath != null)
            {
                _commands.Add(new PushClip(userPath, PdfFillRule.NonZero, clipBounds));
            }
            gs.ClipBoundsPage = Intersect(gs.ClipBoundsPage, clipBounds);
        }

        private void ShowType3Text(GraphicsState gs, PdfDictionary resources, ContentContext ctx, bool hidden,
            ref PdfRect? textClip, PdfFont font, PdfString? single, PdfArray? tj)
        {
            if (ctx.Type3Depth >= _limits.MaxType3Depth)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxType3Depth), "Type3 glyph procedures nest too deeply.");

            double fs = gs.FontSize;
            double th = gs.HorizontalScaling / 100.0;
            var fontMatrix = font.FontMatrix;
            var charProcs = font.CharProcs;
            var glyphResources = font.Type3Resources ?? resources;
            int mode = gs.TextRenderingMode;
            bool paints = mode is not 3 and not 7;
            var glyphs = new List<PdfGlyph>();
            var text = new StringBuilder();
            var runStart = gs.TextMatrix;
            double x = 0;
            var pageBounds = PdfRect.Empty;

            if (mode >= 4)
            {
                // Type3 glyphs as clip: classify the whole show operation.
                textClip = Union(textClip ?? PdfRect.Empty, gs.ClipBoundsPage);
                _textClipExact = false;
            }

            void ShowGlyph(int code)
            {
                if (++_glyphs > _limits.MaxGlyphsPerPage)
                    throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxGlyphsPerPage), "Page exceeded the glyph budget.");

                double w0 = font.GetGlyphWidth(code) / 1000.0;
                double tw = code == 32 ? gs.WordSpacing : 0;
                double advance = (w0 * fs + gs.CharacterSpacing + tw) * th;

                // glyph space → text space → user space (9.6.4)
                var glyphToUser = fontMatrix
                                  * new PdfMatrix(fs * th, 0, 0, fs, 0, gs.TextRise)
                                  * PdfMatrix.CreateTranslation(x, 0)
                                  * runStart;

                string? glyphName = font.GetGlyphName(code);
                if (paints && !hidden && glyphName != null && charProcs != null &&
                    _resolver.Resolve(charProcs[glyphName]) is PdfStream proc)
                {
                    var glyphState = gs.Clone();
                    glyphState.CTM = glyphToUser * gs.CTM;
                    // Text state inside a glyph procedure starts fresh.
                    glyphState.TextMatrix = PdfMatrix.Identity;
                    glyphState.TextLineMatrix = PdfMatrix.Identity;
                    glyphState.TextRenderingMode = 0;
                    _commands.Add(new SaveState());
                    _commands.Add(new ConcatTransform(glyphToUser));
                    int before = _commands.Count;
                    Execute(_owner._streamDecoder.DecodeStream(proc), glyphResources, glyphState,
                        ctx with { Type3Depth = ctx.Type3Depth + 1, UncoloredGlyph = false });
                    for (int i = before; i < _commands.Count; i++)
                    {
                        if (_commands[i].Bounds is PdfRect b) pageBounds = Union(pageBounds, b);
                    }
                    _commands.Add(new RestoreState());
                }

                glyphs.Add(new PdfGlyph((ushort)Math.Clamp(code, 0, ushort.MaxValue), advance, 0, x, 0, font.MapToUnicode(code), code));
                text.Append(font.MapToUnicode(code));
                x += advance;
            }

            void AddString(PdfString s)
            {
                foreach (byte b in s.RawBytes.Span)
                    ShowGlyph(b);
            }

            if (single != null)
            {
                AddString(single);
            }
            else if (tj != null)
            {
                foreach (var item in tj)
                {
                    if (item is PdfString s) AddString(s);
                    else if (item.TryGetNumber(out double adj) && !double.IsNaN(adj)) x -= adj / 1000.0 * fs * th;
                }
            }

            gs.TextMatrix = PdfMatrix.CreateTranslation(x, 0) * runStart;
            if (glyphs.Count == 0)
                return;

            var textToPage = runStart * gs.CTM;
            var box = new PdfRect(0, gs.TextRise - 0.2 * Math.Abs(fs), Math.Max(0, x), Math.Abs(fs));
            var semanticBounds = Intersect(textToPage.Transform(box), gs.ClipBoundsPage);

            // Semantic run (never painted — the procedures above are the glyph shapes).
            _commands.Add(new DrawGlyphRun(
                new PdfGlyphRun(gs.CurrentFontResource, fs, glyphs, runStart, gs.HorizontalScaling,
                    gs.CharacterSpacing, gs.WordSpacing, gs.TextRise, 3, font.BaseFont, text.ToString())
                {
                    TextToPage = textToPage,
                },
                gs.CreateFillPaint(),
                Union(semanticBounds, pageBounds)));
        }

        // ------------------------------------------------------------------ shading

        private void PaintShadingOperator(string name, PdfDictionary resources, GraphicsState gs)
        {
            _features |= PdfFeatureSet.Shading;
            var shObj = LookupResource(resources, "Shading", name);
            var shDict = shObj switch
            {
                PdfStream s => s.Dictionary,
                PdfDictionary d => d,
                _ => null,
            };
            if (shDict == null)
                return;

            var bounds = gs.ClipBoundsPage;
            if (ReadRect(shDict["BBox"]) is PdfRect bbox)
                bounds = Intersect(bounds, gs.CTM.Transform(bbox));
            if (bounds.IsEmpty)
                return;

            if (PaintUnsupported(gs, stroke: false) is PdfFallbackReason paintReason && paintReason != PdfFallbackReason.UnsupportedColorSpace)
            {
                AddFallback(paintReason, "Shading painted with unsupported transparency", bounds);
                return;
            }

            if (BuildShading(shDict, out var reason) is not PdfShading shading)
            {
                AddFallback(reason, "Unsupported shading type", bounds);
                return;
            }

            bool group = gs.FillAlpha < 1.0;
            bool clipBox = ReadRect(shDict["BBox"]) is not null;
            bool composite = BeginComposite(gs, bounds);
            if (clipBox)
            {
                _commands.Add(new SaveState());
                _commands.Add(new PushClip(RectPath(ReadRect(shDict["BBox"])!.Value), PdfFillRule.NonZero, bounds));
            }
            if (group) _commands.Add(new BeginTransparencyGroup(bounds, gs.FillAlpha));
            _commands.Add(new DrawShading(shading, bounds));
            if (group) _commands.Add(new EndTransparencyGroup());
            if (clipBox) _commands.Add(new RestoreState());
            EndComposite(composite);
        }

        /// <summary>
        /// Axial and the radial cases a gradient brush reproduces exactly (point or concentric start
        /// circle). Colours are sampled from the shading function into stops.
        /// </summary>
        private PdfShading? BuildShading(PdfDictionary sh, out PdfFallbackReason reason)
        {
            reason = PdfFallbackReason.Shading;
            long type = sh.GetInteger("ShadingType") ?? 0;
            if (type is not 2 and not 3)
                return null;

            var cs = PdfColorSpace.Resolve(sh["ColorSpace"], _resolver, _page.Resources);
            if (cs.IsPattern || cs.UnsupportedReason != null)
            {
                reason = cs.UnsupportedReason ?? PdfFallbackReason.Shading;
                return null;
            }

            var coords = _resolver.Resolve(sh["Coords"]) as PdfArray;
            int need = type == 2 ? 4 : 6;
            if (coords == null || coords.Count < need)
                return null;
            var c = new double[need];
            for (int i = 0; i < need; i++)
            {
                if (!(_resolver.Resolve(coords[i])?.TryGetNumber(out c[i]) ?? false))
                    return null;
            }

            double t0 = 0, t1 = 1;
            if (_resolver.Resolve(sh["Domain"]) is PdfArray dom && dom.Count >= 2)
            {
                dom[0].TryGetNumber(out t0);
                dom[1].TryGetNumber(out t1);
            }

            bool extendStart = false, extendEnd = false;
            if (_resolver.Resolve(sh["Extend"]) is PdfArray ext && ext.Count >= 2)
            {
                ext[0].TryGetBoolean(out extendStart);
                ext[1].TryGetBoolean(out extendEnd);
            }

            IReadOnlyList<PdfGradientStop> stops;
            try
            {
                stops = SampleStops(sh["Function"], cs, t0, t1);
            }
            catch (Exception ex) when (ex is PdfVectorException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
            if (stops.Count == 0)
                return null;

            if (type == 2)
            {
                return new PdfAxialShading(new(c[0], c[1]), new(c[2], c[3]), stops[0].Color, stops[^1].Color, extendStart, extendEnd)
                {
                    Stops = stops,
                };
            }

            double x0 = c[0], y0 = c[1], r0 = c[2], x1 = c[3], y1 = c[4], r1 = c[5];
            if (r0 < 0 || r1 <= 0)
                return null;
            bool concentric = Math.Abs(x0 - x1) < 1e-9 && Math.Abs(y0 - y1) < 1e-9 && r1 > r0;
            bool focalPoint = r0 < 1e-9 && Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) < r1;
            if ((!concentric && !focalPoint) || (concentric && r0 > 0 && !extendStart))
            {
                // Any other circle pair (or an unpainted hole inside r0): evaluated per pixel.
                return new PdfRadialShading(new(x0, y0), r0, new(x1, y1), r1, stops[0].Color, stops[^1].Color, extendStart, extendEnd)
                {
                    Stops = stops,
                    IsGeneral = true,
                };
            }

            // Brushes interpolate from the focal point (offset 0) to the end circle (offset 1):
            // remap stops so offset reflects the radius r(t) = r0 + t (r1 - r0).
            if (concentric && r0 > 0)
            {
                var remapped = new List<PdfGradientStop>(stops.Count + 1) { new(0, stops[0].Color) };
                foreach (var s in stops)
                    remapped.Add(new((r0 + s.Offset * (r1 - r0)) / r1, s.Color));
                stops = remapped;
            }

            return new PdfRadialShading(new(x0, y0), r0, new(x1, y1), r1, stops[0].Color, stops[^1].Color, extendStart, extendEnd)
            {
                Stops = stops,
            };
        }

        private IReadOnlyList<PdfGradientStop> SampleStops(PdfObject? fnObj, PdfColorSpace cs, double t0, double t1)
        {
            var resolved = _resolver.Resolve(fnObj);
            var functions = new List<PdfFunction>();
            if (resolved is PdfArray arr)
            {
                foreach (var f in arr)
                    functions.Add(PdfFunction.Parse(f, _resolver, _limits));
            }
            else if (resolved != null)
            {
                functions.Add(PdfFunction.Parse(resolved, _resolver, _limits));
            }
            else
            {
                return Array.Empty<PdfGradientStop>();
            }

            const int Samples = 33;
            int n = cs.NumberOfComponents;
            var stops = new List<PdfGradientStop>(Samples);
            var input = new double[1];
            var comps = new double[Math.Max(n, 1)];
            for (int i = 0; i < Samples; i++)
            {
                double u = i / (double)(Samples - 1);
                input[0] = t0 + u * (t1 - t0);
                if (functions.Count == 1)
                {
                    var outBuf = new double[Math.Max(functions[0].OutputCount, n)];
                    functions[0].Evaluate(input, outBuf);
                    Array.Copy(outBuf, comps, Math.Min(outBuf.Length, comps.Length));
                }
                else
                {
                    var one = new double[1];
                    for (int k = 0; k < functions.Count && k < comps.Length; k++)
                    {
                        functions[k].Evaluate(input, one);
                        comps[k] = one[0];
                    }
                }
                stops.Add(new PdfGradientStop(u, cs.ToRgbColor(comps, 1f)));
            }
            return stops;
        }

        // ------------------------------------------------------------------ XObjects

        private void DoXObject(string name, PdfDictionary resources, GraphicsState gs, ContentContext ctx, bool hidden)
        {
            if (LookupResource(resources, "XObject", name) is not PdfStream xobj)
                return;

            if (IsOptionalContentHidden(_resolver.Resolve(xobj.Dictionary["OC"])))
                hidden = true;

            switch (xobj.Dictionary.GetName("Subtype"))
            {
                case "Image":
                    if (!hidden)
                        PaintImage(xobj, xobj.Dictionary, gs, resources, isInline: false, ImageKey(xobj));
                    break;
                case "Form":
                    ExecuteForm(xobj, resources, gs, ctx, hidden);
                    break;
            }
        }

        private string ImageKey(PdfStream stream) =>
            $"{_owner.DocumentKey}:img:{stream.StreamOffset}:{stream.StreamLength}";

        private void ExecuteForm(PdfStream form, PdfDictionary parentResources, GraphicsState gs, ContentContext ctx, bool hidden)
        {
            _features |= PdfFeatureSet.Forms;
            var dict = form.Dictionary;
            var matrix = ReadMatrix(dict["Matrix"]) ?? PdfMatrix.Identity;
            var bbox = ReadRect(dict["BBox"]);
            var formCtm = matrix * gs.CTM;
            var formBounds = bbox is PdfRect bb ? Intersect(formCtm.Transform(bb), gs.ClipBoundsPage) : gs.ClipBoundsPage;

            if (ctx.FormDepth >= _limits.MaxFormXObjectDepth)
            {
                if (!hidden) AddFallback(PdfFallbackReason.ResourceLimit, "Form XObject nesting too deep", formBounds);
                return;
            }
            if (!_activeForms.Add(form))
            {
                return; // a form that paints itself: recursion guard (09_SECURITY "cycle detection")
            }

            try
            {
                if (formBounds.IsEmpty)
                    return;

                var groupDict = _resolver.Resolve(dict["Group"]) as PdfDictionary;
                bool isTransparencyGroup = groupDict?.GetName("S") == "Transparency";

                bool knockout = false;
                if (isTransparencyGroup && !hidden)
                {
                    _features |= PdfFeatureSet.Transparency;
                    knockout = groupDict!.TryGetValue("K", out var k) && k != null && k.TryGetBoolean(out bool kb) && kb;
                    if (gs.SoftMaskActive && gs.SoftMask == null)
                    {
                        AddFallback(PdfFallbackReason.SoftMask, "Transparency group composited with unsupported parameters", formBounds);
                        return;
                    }
                }
                int formMark = _commands.Count, formFallbackMark = _fallbacks.Count;

                byte[] content = _owner._streamDecoder.DecodeStream(form);
                var formResources = _resolver.Resolve(dict["Resources"]) as PdfDictionary ?? parentResources;

                var inner = gs.Clone();
                inner.CTM = formCtm;
                if (bbox is PdfRect clipBox)
                    inner.ClipBoundsPage = formBounds;

                _commands.Add(new SaveState());
                _commands.Add(new ConcatTransform(matrix));
                if (bbox is PdfRect clip)
                    _commands.Add(new PushClip(RectPath(clip), PdfFillRule.NonZero, formBounds));

                bool compositing = isTransparencyGroup && !hidden && (gs.SoftMask != null || !gs.IsNormalBlend);
                bool group = isTransparencyGroup && !hidden && (gs.FillAlpha < 1.0 || compositing);
                if (isTransparencyGroup)
                {
                    // The group's elements start with alpha 1, Normal blend, no soft mask; the group
                    // result is composited with the alpha in effect at Do (11.6.6).
                    inner.FillAlpha = 1;
                    inner.StrokeAlpha = 1;
                    inner.BlendMode = "Normal";
                    inner.SoftMaskActive = false;
                    inner.SoftMask = null;
                }
                if (group)
                {
                    bool isolated = groupDict!.TryGetValue("I", out var iso) && iso != null && iso.TryGetBoolean(out bool ib) && ib;
                    if (compositing)
                        _commands.Add(new BeginCompositingGroup(formBounds, gs.FillAlpha, gs.Blend, gs.SoftMask, isolated, false));
                    else
                        _commands.Add(new BeginTransparencyGroup(formBounds, gs.FillAlpha, "Normal", isolated, false));
                }

                int knockoutElements = _commands.Count; // the group's own Begin is not an element
                var savedBase = _currentPatternBase;
                _currentPatternBase = formCtm;
                try
                {
                    ExecuteNested(content, formResources, inner, ctx with { FormDepth = ctx.FormDepth + 1 }, hidden);
                }
                finally
                {
                    _currentPatternBase = savedBase;
                }

                if (group) _commands.Add(compositing ? new EndCompositingGroup() : new EndTransparencyGroup());
                _commands.Add(new RestoreState());

                // Knockout (11.4.8): each element replaces earlier ones by its shape instead of
                // accumulating. For opaque, Normal-blend elements that is exactly source-over
                // (C·(1 − shape) + E), so such groups are kept; anything else still falls back.
                if (knockout && !KnockoutIsSourceOver(knockoutElements))
                {
                    _commands.RemoveRange(formMark, _commands.Count - formMark);
                    _fallbacks.RemoveRange(formFallbackMark, _fallbacks.Count - formFallbackMark);
                    AddFallback(PdfFallbackReason.TransparencyGroup, "Knockout group with translucent or blended elements", formBounds);
                }
            }
            finally
            {
                _activeForms.Remove(form);
            }
        }

        private bool KnockoutIsSourceOver(int start)
        {
            for (int i = start; i < _commands.Count; i++)
            {
                switch (_commands[i])
                {
                    case FillPath f when f.Paint.Alpha < 1:
                    case StrokePath sp when sp.Paint.Alpha < 1:
                    case DrawGlyphRun g when !g.Run.IsInvisible && (g.Paint.Alpha < 1 || g.Run.StrokePaint is { Alpha: < 1 }):
                    case DrawImage im when im.Image.HasAlpha || im.Image.Opacity < 1:
                    case BeginTransparencyGroup:
                    case BeginCompositingGroup:
                    case DrawFallbackRegion:
                        return false;
                }
            }
            return true;
        }

        private void ExecuteNested(byte[] content, PdfDictionary resources, GraphicsState gs, ContentContext ctx, bool hidden)
        {
            if (!hidden)
            {
                Execute(content, resources, gs, ctx);
                return;
            }

            // Hidden by optional content: interpret for state balance but discard painting output.
            int mark = _commands.Count;
            int fallbackMark = _fallbacks.Count;
            Execute(content, resources, gs, ctx);
            _commands.RemoveRange(mark, _commands.Count - mark);
            _fallbacks.RemoveRange(fallbackMark, _fallbacks.Count - fallbackMark);
        }

        // ------------------------------------------------------------------ images

        private static readonly HashSet<string> NativeImageFilters = new(StringComparer.Ordinal)
        {
            "FlateDecode", "Fl", "LZWDecode", "LZW", "ASCIIHexDecode", "AHx", "ASCII85Decode", "A85",
            "RunLengthDecode", "RL", "DCTDecode", "DCT", "Crypt", "JPXDecode",
        };

        private void PaintImage(PdfStream stream, PdfDictionary dict, GraphicsState gs, PdfDictionary resources, bool isInline, string key)
        {
            _features |= PdfFeatureSet.Images;
            var unit = new PdfRect(0, 0, 1, 1);
            var pageBounds = ToPage(gs, unit);
            if (pageBounds.IsEmpty)
                return;

            long width = dict.GetInteger("Width") ?? 0;
            long height = dict.GetInteger("Height") ?? 0;
            if (width <= 0 || height <= 0)
                return;
            long pixels = width * height;
            if (pixels > _limits.MaxImagePixels || (_imagePixels += pixels) > _limits.MaxImagePixelsPerPage)
            {
                AddFallback(PdfFallbackReason.ResourceLimit, "Image exceeds the pixel budget", pageBounds);
                return;
            }

            bool isMask = _resolver.Resolve(dict["ImageMask"]) is PdfBoolean { Value: true };

            foreach (string filter in FilterNames(dict))
            {
                if (!NativeImageFilters.Contains(filter))
                {
                    AddFallback(PdfFallbackReason.UnsupportedImageFilter, $"Image filter {Sanitize(filter)}", pageBounds,
                        new Dictionary<string, string> { ["filter"] = filter });
                    return;
                }
            }

            if (PaintUnsupported(gs, stroke: false) is PdfFallbackReason paintReason &&
                (isMask || paintReason is PdfFallbackReason.SoftMask or PdfFallbackReason.BlendMode))
            {
                AddFallback(paintReason, "Image painted with unsupported transparency or paint", pageBounds);
                return;
            }
            if (isMask && gs.FillColorSpace.IsPattern)
            {
                AddFallback(PdfFallbackReason.Pattern, "Stencil mask painted with a pattern", pageBounds);
                return;
            }

            PdfColorSpace? cs = null;
            if (!isMask)
            {
                var csObj = dict["ColorSpace"];
                bool jpx = FilterNames(dict).Contains("JPXDecode");
                if (csObj == null && !jpx)
                {
                    AddFallback(PdfFallbackReason.UnsupportedColorSpace, "Image without colour space", pageBounds);
                    return;
                }
                // JPX images may omit /ColorSpace: the codestream's own colour applies (8.9.5).
                cs = csObj == null ? null : PdfColorSpace.Resolve(csObj, _resolver, resources);
                if (cs != null && (cs.IsPattern || cs.UnsupportedReason != null))
                {
                    AddFallback(cs.UnsupportedReason ?? PdfFallbackReason.UnsupportedColorSpace, "Image colour space unsupported", pageBounds);
                    return;
                }
            }

            // Soft mask / explicit mask streams must use supported filters too.
            foreach (var maskKey in new[] { "SMask", "Mask" })
            {
                if (_resolver.Resolve(dict[maskKey]) is PdfStream maskStream)
                {
                    foreach (string filter in FilterNames(maskStream.Dictionary))
                    {
                        if (!NativeImageFilters.Contains(filter))
                        {
                            AddFallback(PdfFallbackReason.UnsupportedImageFilter, $"Mask filter {Sanitize(filter)}", pageBounds);
                            return;
                        }
                    }
                }
            }

            var source = new PdfImageSource(
                key + (isMask ? $":mask:{gs.FillColor.R:F3},{gs.FillColor.G:F3},{gs.FillColor.B:F3}" : string.Empty),
                stream,
                cs,
                isMask ? gs.FillColor : PdfColor.Black,
                _resolver,
                _owner._streamDecoder,
                _limits,
                resources);

            bool interpolate = _resolver.Resolve(dict["Interpolate"]) is PdfBoolean { Value: true };
            var imageRef = new PdfImageRef(
                key + ":" + (++_imageSerial).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (int)width,
                (int)height,
                (int)(dict.GetInteger("BitsPerComponent") ?? (isMask ? 1 : 8)),
                cs?.Name ?? (isMask ? "ImageMask" : "JPX"),
                HasAlpha: isMask || dict.ContainsKey("SMask") || dict.ContainsKey("Mask") || (dict.GetInteger("SMaskInData") ?? 0) != 0,
                ReadOnlyMemory<byte>.Empty,
                FilterNames(dict) is { Count: > 0 } f ? string.Join(",", f) : null)
            {
                Source = source,
                Interpolate = interpolate,
                Opacity = gs.FillAlpha,
            };

            bool composite = BeginComposite(gs, pageBounds);
            _commands.Add(new DrawImage(imageRef, PdfMatrix.Identity, pageBounds));
            EndComposite(composite);
        }

        private List<string> FilterNames(PdfDictionary dict)
        {
            var names = new List<string>();
            var filter = _resolver.Resolve(dict["Filter"] ?? dict["F"]);
            if (filter is PdfName n)
            {
                names.Add(n.Value);
            }
            else if (filter is PdfArray arr)
            {
                foreach (var item in arr)
                {
                    if (_resolver.Resolve(item) is PdfName an)
                        names.Add(an.Value);
                }
            }
            return names;
        }

        private static readonly Dictionary<string, string> InlineKeys = new(StringComparer.Ordinal)
        {
            ["BPC"] = "BitsPerComponent", ["CS"] = "ColorSpace", ["D"] = "Decode", ["DP"] = "DecodeParms",
            ["F"] = "Filter", ["H"] = "Height", ["IM"] = "ImageMask", ["I"] = "Interpolate", ["W"] = "Width",
            ["L"] = "Length",
        };

        private static readonly Dictionary<string, string> InlineNames = new(StringComparer.Ordinal)
        {
            ["G"] = "DeviceGray", ["RGB"] = "DeviceRGB", ["CMYK"] = "DeviceCMYK", ["I"] = "Indexed",
            ["AHx"] = "ASCIIHexDecode", ["A85"] = "ASCII85Decode", ["LZW"] = "LZWDecode", ["Fl"] = "FlateDecode",
            ["RL"] = "RunLengthDecode", ["CCF"] = "CCITTFaxDecode", ["DCT"] = "DCTDecode",
        };

        private static PdfObject ExpandInline(PdfObject value) => value switch
        {
            PdfName n when InlineNames.TryGetValue(n.Value, out var full) => new PdfName(full),
            PdfArray a => new PdfArray(a.Select(ExpandInline).ToList()),
            _ => value,
        };

        /// <summary>BI … ID &lt;data&gt; EI (8.9.7).</summary>
        private void ReadInlineImage(MemoryByteSource src, PdfLexer lexer, PdfDictionary resources, GraphicsState gs, bool hidden)
        {
            var entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            while (true)
            {
                long pos = src.Position;
                var tok = lexer.NextToken();
                if (tok.Type == PdfTokenType.EndOfFile)
                    return;
                if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID")
                    break;
                if (tok.Type != PdfTokenType.Name)
                    continue; // bounded recovery: skip junk
                var value = _parser.ParseObject(lexer);
                if (value == null)
                    continue;
                string key = InlineKeys.TryGetValue(tok.TextValue, out var full) ? full : tok.TextValue;
                entries[key] = ExpandInline(value);
                _ = pos;
            }

            // Exactly one whitespace byte separates ID from the data.
            int ws = src.ReadByte();
            if (ws >= 0 && !PdfLexer.IsWhitespace((byte)ws))
                src.Position -= 1;

            var dict = new PdfDictionary(entries);
            long dataStart = src.Position;
            long dataLength = -1;

            if (dict.GetInteger("Length") is long explicitLength && explicitLength >= 0)
            {
                dataLength = explicitLength;
            }
            else if (FilterNames(dict).Count == 0)
            {
                // Unfiltered: the size is fully determined by the image parameters.
                long w = dict.GetInteger("Width") ?? 0, h = dict.GetInteger("Height") ?? 0;
                long bpc = dict.GetInteger("BitsPerComponent") ?? 1;
                bool mask = dict["ImageMask"] is PdfBoolean { Value: true };
                int comps = mask ? 1 : InlineComponents(dict["ColorSpace"], resources);
                if (w > 0 && h > 0 && comps > 0)
                    dataLength = checked((w * comps * bpc + 7) / 8 * h);
            }

            if (dataLength < 0 || dataStart + dataLength > src.Length)
            {
                dataLength = ScanForEi(src, dataStart);
            }

            if (dataLength > _limits.MaxTokenLength)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxTokenLength), "Inline image data too large.");

            var data = src.ReadMemory(dataStart, (int)Math.Max(0, dataLength)).ToArray();
            src.Position = dataStart + Math.Max(0, dataLength);

            // Consume up to and including EI.
            long guard = src.Position;
            while (true)
            {
                var tok = lexer.NextToken();
                if (tok.Type == PdfTokenType.EndOfFile || (tok.Type == PdfTokenType.Keyword && tok.TextValue == "EI"))
                    break;
                if (src.Position - guard > 64)
                    break;
            }

            if (hidden)
                return;

            var stream = new PdfStream(dict, 0, data.Length, null, data);
            PaintImage(stream, dict, gs, resources, isInline: true, $"{_owner.DocumentKey}:inline:{_page.PageNumber}:{dataStart}");
        }

        private int InlineComponents(PdfObject? csObj, PdfDictionary resources)
        {
            if (csObj == null) return 1;
            try
            {
                return PdfColorSpace.Resolve(csObj, _resolver, resources).NumberOfComponents;
            }
            catch (Exception ex) when (ex is PdfVectorException or InvalidDataException)
            {
                return -1;
            }
        }

        private static long ScanForEi(MemoryByteSource src, long start)
        {
            var span = src.ReadMemory(start, (int)Math.Min(int.MaxValue, src.Length - start)).Span;
            for (int i = 0; i + 1 < span.Length; i++)
            {
                if (span[i] == 'E' && span[i + 1] == 'I' &&
                    (i == 0 || PdfLexer.IsWhitespace(span[i - 1])) &&
                    (i + 2 >= span.Length || PdfLexer.IsWhitespace(span[i + 2]) || PdfLexer.IsDelimiter(span[i + 2])))
                {
                    int end = i;
                    if (end > 0 && PdfLexer.IsWhitespace(span[end - 1])) end--;
                    return end;
                }
            }
            return span.Length;
        }

        // ------------------------------------------------------------------ optional content

        private bool IsOptionalContentHidden(PdfObject? oc)
        {
            var hiddenSet = _owner.HiddenOptionalContentGroups;
            if (oc is not PdfDictionary dict || hiddenSet == null || hiddenSet.Count == 0)
                return false;

            if (dict.GetName("Type") == "OCMD")
            {
                var ocgs = new List<PdfDictionary>();
                var ocgsObj = _resolver.Resolve(dict["OCGs"]);
                if (ocgsObj is PdfDictionary single) ocgs.Add(single);
                else if (ocgsObj is PdfArray arr)
                {
                    foreach (var item in arr)
                        if (_resolver.Resolve(item) is PdfDictionary g) ocgs.Add(g);
                }
                if (ocgs.Count == 0)
                    return false;

                int on = 0;
                foreach (var g in ocgs)
                    if (!hiddenSet.Contains(g)) on++;

                // /VE expressions are not evaluated; /P governs (8.11.2.2).
                return (dict.GetName("P") ?? "AnyOn") switch
                {
                    "AllOn" => on != ocgs.Count,
                    "AnyOff" => on == ocgs.Count,
                    "AllOff" => on != 0,
                    _ => on == 0,
                };
            }

            return hiddenSet.Contains(dict);
        }

        // ------------------------------------------------------------------ annotations

        /// <summary>
        /// Paints annotation normal appearances like the PDFium path (FPDF_ANNOT), using the
        /// appearance-to-rectangle algorithm of ISO 32000-2 12.5.5.
        /// </summary>
        private void AppendAnnotationAppearances()
        {
            if (_resolver.Resolve(_page.Dictionary["Annots"]) is not PdfArray annots)
                return;

            foreach (var annotRef in annots)
            {
                if (_resolver.Resolve(annotRef) is not PdfDictionary annot)
                    continue;
                string? subtype = annot.GetName("Subtype");
                if (subtype is "Popup" or "Link")
                    continue;

                long flags = annot.GetInteger("F") ?? 0;
                if ((flags & 2) != 0 || (flags & 32) != 0)
                    continue; // Hidden / NoView

                if (_resolver.Resolve(annot["AP"]) is not PdfDictionary ap)
                    continue;

                var normal = _resolver.Resolve(ap["N"]);
                if (normal is PdfDictionary states && annot.GetName("AS") is string state)
                    normal = _resolver.Resolve(states[state]);
                if (normal is not PdfStream appearance)
                    continue;

                if (ReadRect(annot["Rect"]) is not PdfRect rect || rect.IsEmpty)
                    continue;

                bool hidden = IsOptionalContentHidden(_resolver.Resolve(annot["OC"]));
                var bbox = ReadRect(appearance.Dictionary["BBox"]) ?? rect;
                var matrix = ReadMatrix(appearance.Dictionary["Matrix"]) ?? PdfMatrix.Identity;
                var transformed = matrix.Transform(bbox);
                if (transformed.Width <= 0 || transformed.Height <= 0)
                    continue;

                var a = new PdfMatrix(
                    rect.Width / transformed.Width, 0, 0, rect.Height / transformed.Height,
                    rect.Left - transformed.Left * rect.Width / transformed.Width,
                    rect.Top - transformed.Top * rect.Height / transformed.Height);

                var gs = new GraphicsState { ClipBoundsPage = Crop, CTM = a };
                _commands.Add(new SaveState());
                _commands.Add(new ConcatTransform(a));
                ExecuteForm(appearance, _page.Resources, gs, new ContentContext(PdfMatrix.Identity, 0, 0, false), hidden);
                _commands.Add(new RestoreState());
            }
        }

        // ------------------------------------------------------------------ content streams

        private byte[] ConcatenateContentStreams(IReadOnlyList<PdfStream> streams)
        {
            if (streams.Count == 0)
                return Array.Empty<byte>();

            if (streams.Count == 1)
                return _owner._streamDecoder.DecodeStream(streams[0]);

            // Streams are concatenated with whitespace between them; tokens never span streams (7.8.2).
            using var ms = new MemoryStream();
            foreach (var stream in streams)
            {
                byte[] decoded = _owner._streamDecoder.DecodeStream(stream);
                if (ms.Length + decoded.Length > _limits.MaxDecodedStreamBytes)
                    throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxDecodedStreamBytes), "Page content exceeds the decoded size limit.");
                ms.Write(decoded);
                ms.WriteByte((byte)'\n');
            }

            return ms.ToArray();
        }
    }
}
