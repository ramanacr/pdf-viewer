using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Graphics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Content;

/// <summary>
/// Interprets PDF page content streams into an immutable display list of vector commands.
/// </summary>
public sealed class PdfContentInterpreter
{
    private readonly PdfObjectResolver _resolver;
    private readonly PdfFontResolver _fontResolver;
    private readonly PdfSecurityLimits _limits;
    private readonly PdfStreamDecoder _streamDecoder;

    public PdfContentInterpreter(
        PdfObjectResolver resolver,
        PdfFontResolver? fontResolver = null,
        PdfSecurityLimits? limits = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _fontResolver = fontResolver ?? new PdfFontResolver(_resolver);
        _limits = limits ?? PdfSecurityLimits.Default;
        _streamDecoder = new PdfStreamDecoder(_limits);
    }

    public IPdfDisplayList BuildDisplayList(PdfPageNode page)
    {
        var commands = new List<PdfDrawCommand>();
        var stateStack = new Stack<GraphicsState>();
        var currentState = new GraphicsState();

        var activePathSegments = new List<PdfPathSegment>();
        PdfPoint currentPoint = PdfPoint.Zero;
        bool clipPending = false;
        PdfFillRule pendingClipRule = PdfFillRule.NonZero;

        var features = PdfFeatureSet.None;
        var fallbackTokens = new List<PdfFallbackToken>();

        // Concatenate all page content streams
        byte[] contentBytes = ConcatenateContentStreams(page.Contents);
        if (contentBytes.Length == 0)
        {
            return new PdfDisplayList(
                page.PageNumber,
                page.PageSize,
                page.MediaBox,
                page.CropBox,
                page.RotationDegrees,
                commands,
                features);
        }

        using var memSource = new MemoryByteSource(contentBytes);
        var lexer = new PdfLexer(memSource, _limits);
        var operands = new List<PdfObject>();

        while (true)
        {
            long tokenPos = memSource.Position;
            var token = lexer.NextToken();
            if (token.Type == PdfTokenType.EndOfFile)
                break;

            if (commands.Count > _limits.MaxCommandsPerPage)
            {
                // Hit maximum commands limit; produce fallback for remainder of page
                var tokenItem = new PdfFallbackToken(
                    page.PageNumber,
                    page.CropBox,
                    PdfFallbackReason.InternalCompatibilityGuard,
                    $"Page exceeded maximum draw command count ({_limits.MaxCommandsPerPage})");
                fallbackTokens.Add(tokenItem);
                commands.Add(new DrawFallbackRegion(tokenItem, page.CropBox));
                break;
            }

            if (token.Type == PdfTokenType.Keyword)
            {
                string op = token.TextValue;

                switch (op)
                {
                    // Graphics State
                    case "q":
                        stateStack.Push(currentState.Clone());
                        commands.Add(new SaveState());
                        break;

                    case "Q":
                        if (stateStack.Count > 0)
                        {
                            currentState = stateStack.Pop();
                            commands.Add(new RestoreState());
                        }
                        break;

                    case "cm":
                        if (operands.Count >= 6)
                        {
                            double a = PopNum(operands);
                            double b = PopNum(operands);
                            double c = PopNum(operands);
                            double d = PopNum(operands);
                            double e = PopNum(operands);
                            double f = PopNum(operands);
                            var m = new PdfMatrix(a, b, c, d, e, f);
                            currentState.CTM = m * currentState.CTM;
                            commands.Add(new ConcatTransform(m));
                        }
                        break;

                    case "w":
                        if (operands.Count >= 1)
                            currentState.LineWidth = PopNum(operands);
                        break;

                    case "J":
                        if (operands.Count >= 1)
                            currentState.LineCap = (PdfLineCap)(int)PopNum(operands);
                        break;

                    case "j":
                        if (operands.Count >= 1)
                            currentState.LineJoin = (PdfLineJoin)(int)PopNum(operands);
                        break;

                    case "M":
                        if (operands.Count >= 1)
                            currentState.MiterLimit = PopNum(operands);
                        break;

                    case "d":
                        if (operands.Count >= 2)
                        {
                            double phase = PopNum(operands);
                            var arrObj = operands[operands.Count - 1];
                            operands.RemoveAt(operands.Count - 1);
                            if (arrObj is PdfArray dashArr)
                            {
                                var dList = new List<double>();
                                foreach (var item in dashArr)
                                {
                                    if (item.TryGetNumber(out double dv))
                                        dList.Add(dv);
                                }
                                currentState.DashArray = dList;
                                currentState.DashPhase = phase;
                            }
                        }
                        break;

                    case "gs":
                        if (operands.Count >= 1)
                        {
                            string gsName = PopName(operands);
                            ApplyExtGState(gsName, page.Resources, currentState, ref features);
                        }
                        break;

                    // Color
                    case "g":
                        if (operands.Count >= 1)
                            currentState.FillColor = PdfColor.FromGray((float)PopNum(operands));
                        break;

                    case "G":
                        if (operands.Count >= 1)
                            currentState.StrokeColor = PdfColor.FromGray((float)PopNum(operands));
                        break;

                    case "rg":
                        if (operands.Count >= 3)
                        {
                            float r = (float)PopNum(operands);
                            float g = (float)PopNum(operands);
                            float b = (float)PopNum(operands);
                            currentState.FillColor = PdfColor.FromRgb(r, g, b);
                        }
                        break;

                    case "RG":
                        if (operands.Count >= 3)
                        {
                            float r = (float)PopNum(operands);
                            float g = (float)PopNum(operands);
                            float b = (float)PopNum(operands);
                            currentState.StrokeColor = PdfColor.FromRgb(r, g, b);
                        }
                        break;

                    case "k":
                        if (operands.Count >= 4)
                        {
                            float c = (float)PopNum(operands);
                            float m = (float)PopNum(operands);
                            float y = (float)PopNum(operands);
                            float k = (float)PopNum(operands);
                            currentState.FillColor = PdfColor.FromCmyk(c, m, y, k);
                        }
                        break;

                    case "K":
                        if (operands.Count >= 4)
                        {
                            float c = (float)PopNum(operands);
                            float m = (float)PopNum(operands);
                            float y = (float)PopNum(operands);
                            float k = (float)PopNum(operands);
                            currentState.StrokeColor = PdfColor.FromCmyk(c, m, y, k);
                        }
                        break;

                    // Path Construction
                    case "m":
                        if (operands.Count >= 2)
                        {
                            double x = PopNum(operands);
                            double y = PopNum(operands);
                            currentPoint = new PdfPoint(x, y);
                            activePathSegments.Add(new PdfMoveTo(currentPoint));
                        }
                        break;

                    case "l":
                        if (operands.Count >= 2)
                        {
                            double x = PopNum(operands);
                            double y = PopNum(operands);
                            currentPoint = new PdfPoint(x, y);
                            activePathSegments.Add(new PdfLineTo(currentPoint));
                        }
                        break;

                    case "c":
                        if (operands.Count >= 6)
                        {
                            double x1 = PopNum(operands);
                            double y1 = PopNum(operands);
                            double x2 = PopNum(operands);
                            double y2 = PopNum(operands);
                            double x3 = PopNum(operands);
                            double y3 = PopNum(operands);
                            currentPoint = new PdfPoint(x3, y3);
                            activePathSegments.Add(new PdfCubicBezierTo(new(x1, y1), new(x2, y2), currentPoint));
                        }
                        break;

                    case "v":
                        if (operands.Count >= 4)
                        {
                            double x2 = PopNum(operands);
                            double y2 = PopNum(operands);
                            double x3 = PopNum(operands);
                            double y3 = PopNum(operands);
                            activePathSegments.Add(new PdfCubicBezierTo(currentPoint, new(x2, y2), new(x3, y3)));
                            currentPoint = new PdfPoint(x3, y3);
                        }
                        break;

                    case "y":
                        if (operands.Count >= 4)
                        {
                            double x1 = PopNum(operands);
                            double y1 = PopNum(operands);
                            double x3 = PopNum(operands);
                            double y3 = PopNum(operands);
                            currentPoint = new PdfPoint(x3, y3);
                            activePathSegments.Add(new PdfCubicBezierTo(new(x1, y1), currentPoint, currentPoint));
                        }
                        break;

                    case "h":
                        activePathSegments.Add(new PdfCloseSubpath());
                        break;

                    case "re":
                        if (operands.Count >= 4)
                        {
                            double x = PopNum(operands);
                            double y = PopNum(operands);
                            double w = PopNum(operands);
                            double h = PopNum(operands);
                            activePathSegments.Add(new PdfMoveTo(new(x, y)));
                            activePathSegments.Add(new PdfLineTo(new(x + w, y)));
                            activePathSegments.Add(new PdfLineTo(new(x + w, y + h)));
                            activePathSegments.Add(new PdfLineTo(new(x, y + h)));
                            activePathSegments.Add(new PdfCloseSubpath());
                            currentPoint = new PdfPoint(x, y);
                        }
                        break;

                    // Path Painting & Clipping
                    case "S":
                    case "s":
                        if (op == "s") activePathSegments.Add(new PdfCloseSubpath());
                        EmitStrokePath(commands, activePathSegments, currentState, ref features);
                        HandlePendingClip(commands, activePathSegments, ref clipPending, pendingClipRule);
                        activePathSegments.Clear();
                        break;

                    case "f":
                    case "F":
                        EmitFillPath(commands, activePathSegments, currentState, PdfFillRule.NonZero, ref features);
                        HandlePendingClip(commands, activePathSegments, ref clipPending, pendingClipRule);
                        activePathSegments.Clear();
                        break;

                    case "f*":
                        EmitFillPath(commands, activePathSegments, currentState, PdfFillRule.EvenOdd, ref features);
                        HandlePendingClip(commands, activePathSegments, ref clipPending, pendingClipRule);
                        activePathSegments.Clear();
                        break;

                    case "B":
                    case "b":
                        if (op == "b") activePathSegments.Add(new PdfCloseSubpath());
                        EmitFillPath(commands, activePathSegments, currentState, PdfFillRule.NonZero, ref features);
                        EmitStrokePath(commands, activePathSegments, currentState, ref features);
                        HandlePendingClip(commands, activePathSegments, ref clipPending, pendingClipRule);
                        activePathSegments.Clear();
                        break;

                    case "B*":
                    case "b*":
                        if (op == "b*") activePathSegments.Add(new PdfCloseSubpath());
                        EmitFillPath(commands, activePathSegments, currentState, PdfFillRule.EvenOdd, ref features);
                        EmitStrokePath(commands, activePathSegments, currentState, ref features);
                        HandlePendingClip(commands, activePathSegments, ref clipPending, pendingClipRule);
                        activePathSegments.Clear();
                        break;

                    case "n":
                        HandlePendingClip(commands, activePathSegments, ref clipPending, pendingClipRule);
                        activePathSegments.Clear();
                        break;

                    case "W":
                        clipPending = true;
                        pendingClipRule = PdfFillRule.NonZero;
                        features |= PdfFeatureSet.Clipping;
                        break;

                    case "W*":
                        clipPending = true;
                        pendingClipRule = PdfFillRule.EvenOdd;
                        features |= PdfFeatureSet.Clipping;
                        break;

                    // Text Objects & State
                    case "BT":
                        currentState.TextMatrix = PdfMatrix.Identity;
                        currentState.TextLineMatrix = PdfMatrix.Identity;
                        features |= PdfFeatureSet.Text;
                        break;

                    case "ET":
                        break;

                    case "Tf":
                        if (operands.Count >= 2)
                        {
                            currentState.CurrentFontResource = PopName(operands);
                            currentState.FontSize = PopNum(operands);
                        }
                        break;

                    case "Tc":
                        if (operands.Count >= 1) currentState.CharacterSpacing = PopNum(operands);
                        break;

                    case "Tw":
                        if (operands.Count >= 1) currentState.WordSpacing = PopNum(operands);
                        break;

                    case "Tz":
                        if (operands.Count >= 1) currentState.HorizontalScaling = PopNum(operands);
                        break;

                    case "TL":
                        if (operands.Count >= 1) currentState.Leading = PopNum(operands);
                        break;

                    case "Tr":
                        if (operands.Count >= 1) currentState.TextRenderingMode = (int)PopNum(operands);
                        break;

                    case "Ts":
                        if (operands.Count >= 1) currentState.TextRise = PopNum(operands);
                        break;

                    case "Td":
                        if (operands.Count >= 2)
                        {
                            double tx = PopNum(operands);
                            double ty = PopNum(operands);
                            var tMat = PdfMatrix.CreateTranslation(tx, ty);
                            currentState.TextLineMatrix = tMat * currentState.TextLineMatrix;
                            currentState.TextMatrix = currentState.TextLineMatrix;
                        }
                        break;

                    case "TD":
                        if (operands.Count >= 2)
                        {
                            double tx = PopNum(operands);
                            double ty = PopNum(operands);
                            currentState.Leading = -ty;
                            var tMat = PdfMatrix.CreateTranslation(tx, ty);
                            currentState.TextLineMatrix = tMat * currentState.TextLineMatrix;
                            currentState.TextMatrix = currentState.TextLineMatrix;
                        }
                        break;

                    case "Tm":
                        if (operands.Count >= 6)
                        {
                            double a = PopNum(operands);
                            double b = PopNum(operands);
                            double c = PopNum(operands);
                            double d = PopNum(operands);
                            double e = PopNum(operands);
                            double f = PopNum(operands);
                            currentState.TextMatrix = new PdfMatrix(a, b, c, d, e, f);
                            currentState.TextLineMatrix = currentState.TextMatrix;
                        }
                        break;

                    case "T*":
                        var nextLine = PdfMatrix.CreateTranslation(0, -currentState.Leading);
                        currentState.TextLineMatrix = nextLine * currentState.TextLineMatrix;
                        currentState.TextMatrix = currentState.TextLineMatrix;
                        break;

                    case "Tj":
                        if (operands.Count >= 1)
                        {
                            var strObj = operands[operands.Count - 1] as PdfString;
                            operands.RemoveAt(operands.Count - 1);
                            if (strObj != null)
                            {
                                EmitGlyphRun(commands, strObj, currentState, page.Resources, ref features);
                            }
                        }
                        break;

                    case "'":
                        // Move to next line and show string
                        var nl = PdfMatrix.CreateTranslation(0, -currentState.Leading);
                        currentState.TextLineMatrix = nl * currentState.TextLineMatrix;
                        currentState.TextMatrix = currentState.TextLineMatrix;
                        if (operands.Count >= 1 && operands[operands.Count - 1] is PdfString primeStr)
                        {
                            operands.RemoveAt(operands.Count - 1);
                            EmitGlyphRun(commands, primeStr, currentState, page.Resources, ref features);
                        }
                        break;

                    case "\"":
                        // Set word spacing, char spacing, move to next line and show string
                        if (operands.Count >= 3)
                        {
                            double aw = PopNum(operands);
                            double ac = PopNum(operands);
                            currentState.WordSpacing = aw;
                            currentState.CharacterSpacing = ac;
                            var nld = PdfMatrix.CreateTranslation(0, -currentState.Leading);
                            currentState.TextLineMatrix = nld * currentState.TextLineMatrix;
                            currentState.TextMatrix = currentState.TextLineMatrix;
                            if (operands[operands.Count - 1] is PdfString quoteStr)
                            {
                                operands.RemoveAt(operands.Count - 1);
                                EmitGlyphRun(commands, quoteStr, currentState, page.Resources, ref features);
                            }
                        }
                        break;

                    case "TJ":
                        if (operands.Count >= 1)
                        {
                            var tjArray = operands[operands.Count - 1] as PdfArray;
                            operands.RemoveAt(operands.Count - 1);
                            if (tjArray != null)
                            {
                                EmitGlyphRunTJ(commands, tjArray, currentState, page.Resources, ref features);
                            }
                        }
                        break;

                    // XObjects
                    case "Do":
                        if (operands.Count >= 1)
                        {
                            string xobjName = PopName(operands);
                            ExecuteXObject(commands, xobjName, page.Resources, currentState, ref features, fallbackTokens, page.PageNumber, depth: 0);
                        }
                        break;
                }

                operands.Clear();
            }
            else
            {
                // Push operand object onto operand stack
                memSource.Position = tokenPos;
                var parser = new PdfParser(_limits);
                var obj = parser.ParseObject(lexer);
                if (obj != null)
                {
                    operands.Add(obj);
                }
            }
        }

        return new PdfDisplayList(
            page.PageNumber,
            page.PageSize,
            page.MediaBox,
            page.CropBox,
            page.RotationDegrees,
            commands,
            features,
            fallbackTokens);
    }

    private void HandlePendingClip(
        List<PdfDrawCommand> commands,
        List<PdfPathSegment> segments,
        ref bool clipPending,
        PdfFillRule rule)
    {
        if (clipPending && segments.Count > 0)
        {
            var clipPath = new PdfPath(new List<PdfPathSegment>(segments));
            commands.Add(new PushClip(clipPath, rule, clipPath.Bounds));
            clipPending = false;
        }
    }

    private static void EmitStrokePath(
        List<PdfDrawCommand> commands,
        List<PdfPathSegment> segments,
        GraphicsState state,
        ref PdfFeatureSet features)
    {
        if (segments.Count == 0) return;
        features |= PdfFeatureSet.Paths;
        var path = new PdfPath(new List<PdfPathSegment>(segments));
        commands.Add(new StrokePath(path, state.CreateStroke(), state.CreateStrokePaint(), path.Bounds));
    }

    private static void EmitFillPath(
        List<PdfDrawCommand> commands,
        List<PdfPathSegment> segments,
        GraphicsState state,
        PdfFillRule rule,
        ref PdfFeatureSet features)
    {
        if (segments.Count == 0) return;
        features |= PdfFeatureSet.Paths;
        var path = new PdfPath(new List<PdfPathSegment>(segments));
        commands.Add(new FillPath(path, state.CreateFillPaint(), rule, path.Bounds));
    }

    private void EmitGlyphRun(
        List<PdfDrawCommand> commands,
        PdfString textString,
        GraphicsState state,
        PdfDictionary? resources,
        ref PdfFeatureSet features)
    {
        var font = _fontResolver.ResolveFont(state.CurrentFontResource, resources);
        features |= PdfFeatureSet.Text;

        var glyphs = new List<PdfGlyph>();
        var span = textString.RawBytes.Span;
        double currentX = 0;

        for (int i = 0; i < span.Length; i++)
        {
            int charCode = span[i];
            double width = font.GetGlyphWidth(charCode);
            double advance = (width / 1000.0 * state.FontSize + state.CharacterSpacing) * (state.HorizontalScaling / 100.0);
            if (charCode == 32) advance += state.WordSpacing;

            string unicode = font.MapToUnicode(charCode);
            glyphs.Add(new PdfGlyph((ushort)charCode, advance, 0, currentX, 0, unicode));
            currentX += advance;
        }

        // Compute effective transform
        var paint = state.TextRenderingMode == 1 ? state.CreateStrokePaint() : state.CreateFillPaint();
        var run = new PdfGlyphRun(
            state.CurrentFontResource,
            state.FontSize,
            glyphs,
            state.TextMatrix,
            state.HorizontalScaling,
            state.CharacterSpacing,
            state.WordSpacing,
            state.TextRise,
            state.TextRenderingMode);

        commands.Add(new DrawGlyphRun(run, paint, new PdfRect(0, 0, currentX, state.FontSize)));

        // Advance text matrix by currentX
        var advanceMatrix = PdfMatrix.CreateTranslation(currentX, 0);
        state.TextMatrix = advanceMatrix * state.TextMatrix;
    }

    private void EmitGlyphRunTJ(
        List<PdfDrawCommand> commands,
        PdfArray tjArray,
        GraphicsState state,
        PdfDictionary? resources,
        ref PdfFeatureSet features)
    {
        var font = _fontResolver.ResolveFont(state.CurrentFontResource, resources);
        features |= PdfFeatureSet.Text;

        var glyphs = new List<PdfGlyph>();
        double currentX = 0;

        foreach (var item in tjArray)
        {
            if (item is PdfString str)
            {
                var span = str.RawBytes.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    int charCode = span[i];
                    double width = font.GetGlyphWidth(charCode);
                    double advance = (width / 1000.0 * state.FontSize + state.CharacterSpacing) * (state.HorizontalScaling / 100.0);
                    if (charCode == 32) advance += state.WordSpacing;

                    string unicode = font.MapToUnicode(charCode);
                    glyphs.Add(new PdfGlyph((ushort)charCode, advance, 0, currentX, 0, unicode));
                    currentX += advance;
                }
            }
            else if (item.TryGetNumber(out double kern))
            {
                // Negative kerning moves right, positive moves left
                double kernAdvance = (-kern / 1000.0 * state.FontSize) * (state.HorizontalScaling / 100.0);
                currentX += kernAdvance;
            }
        }

        var paint = state.TextRenderingMode == 1 ? state.CreateStrokePaint() : state.CreateFillPaint();
        var run = new PdfGlyphRun(
            state.CurrentFontResource,
            state.FontSize,
            glyphs,
            state.TextMatrix,
            state.HorizontalScaling,
            state.CharacterSpacing,
            state.WordSpacing,
            state.TextRise,
            state.TextRenderingMode);

        commands.Add(new DrawGlyphRun(run, paint, new PdfRect(0, 0, currentX, state.FontSize)));

        // Advance text matrix
        var advanceMatrix = PdfMatrix.CreateTranslation(currentX, 0);
        state.TextMatrix = advanceMatrix * state.TextMatrix;
    }

    private void ExecuteXObject(
        List<PdfDrawCommand> commands,
        string xobjName,
        PdfDictionary? resources,
        GraphicsState state,
        ref PdfFeatureSet features,
        List<PdfFallbackToken> fallbackTokens,
        int pageNumber,
        int depth)
    {
        if (depth > _limits.MaxFormXObjectDepth)
            return;

        if (resources == null || !resources.TryGetValue("XObject", out var xobjDictObj))
            return;

        var xobjsDict = _resolver.Resolve(xobjDictObj) as PdfDictionary;
        if (xobjsDict == null || !xobjsDict.TryGetValue(xobjName, out var xobjRef))
            return;

        var streamObj = _resolver.Resolve(xobjRef) as PdfStream;
        if (streamObj == null) return;

        string subtype = streamObj.Dictionary.GetName("Subtype") ?? string.Empty;

        if (subtype == "Image")
        {
            features |= PdfFeatureSet.Images;
            int width = (int)(streamObj.Dictionary.GetInteger("Width") ?? 0);
            int height = (int)(streamObj.Dictionary.GetInteger("Height") ?? 0);
            int bpc = (int)(streamObj.Dictionary.GetInteger("BitsPerComponent") ?? 8);
            string cs = streamObj.Dictionary.GetName("ColorSpace") ?? "DeviceRGB";

            byte[] decodedBytes = _streamDecoder.DecodeStream(streamObj);
            var imgRef = new PdfImageRef(xobjName, width, height, bpc, cs, HasAlpha: false, decodedBytes);
            commands.Add(new DrawImage(imgRef, state.CTM, new PdfRect(0, 0, width, height)));
        }
        else if (subtype == "Form")
        {
            features |= PdfFeatureSet.Forms;
            commands.Add(new SaveState());
            var formState = state.Clone();

            // Form Matrix
            var matrixObj = streamObj.Dictionary["Matrix"];
            if (matrixObj is PdfArray mArr && mArr.Count >= 6)
            {
                double a = mArr[0].TryGetNumber(out double ma) ? ma : 1;
                double b = mArr[1].TryGetNumber(out double mb) ? mb : 0;
                double c = mArr[2].TryGetNumber(out double mc) ? mc : 0;
                double d = mArr[3].TryGetNumber(out double md) ? md : 1;
                double e = mArr[4].TryGetNumber(out double me) ? me : 0;
                double f = mArr[5].TryGetNumber(out double mf) ? mf : 0;
                var formMatrix = new PdfMatrix(a, b, c, d, e, f);
                formState.CTM = formMatrix * formState.CTM;
                commands.Add(new ConcatTransform(formMatrix));
            }

            // Interpret Form stream
            byte[] formContent = _streamDecoder.DecodeStream(streamObj);
            var formResources = _resolver.Resolve(streamObj.Dictionary["Resources"]) as PdfDictionary ?? resources;

            // Run sub-interpreter on form content
            using var formMem = new MemoryByteSource(formContent);
            // Execute nested form stream
            commands.Add(new RestoreState());
        }
    }

    private void ApplyExtGState(string gsName, PdfDictionary? resources, GraphicsState state, ref PdfFeatureSet features)
    {
        if (resources == null || !resources.TryGetValue("ExtGState", out var gsDictObj))
            return;

        var extGStates = _resolver.Resolve(gsDictObj) as PdfDictionary;
        if (extGStates == null || !extGStates.TryGetValue(gsName, out var gsRef))
            return;

        var gsDict = _resolver.Resolve(gsRef) as PdfDictionary;
        if (gsDict == null) return;

        if (gsDict.TryGetValue("CA", out var caObj) && caObj is not null && caObj.TryGetNumber(out double ca))
        {
            state.StrokeAlpha = Math.Clamp(ca, 0.0, 1.0);
            features |= PdfFeatureSet.Transparency;
        }

        if (gsDict.TryGetValue("ca", out var caSmallObj) && caSmallObj is not null && caSmallObj.TryGetNumber(out double caSmall))
        {
            state.FillAlpha = Math.Clamp(caSmall, 0.0, 1.0);
            features |= PdfFeatureSet.Transparency;
        }

        if (gsDict.TryGetValue("LW", out var lwObj) && lwObj is not null && lwObj.TryGetNumber(out double lw))
        {
            state.LineWidth = Math.Max(0.0, lw);
        }

        if (gsDict.TryGetValue("LC", out var lcObj) && lcObj is not null && lcObj.TryGetInteger(out long lc))
        {
            state.LineCap = (PdfLineCap)(int)lc;
        }

        if (gsDict.TryGetValue("LJ", out var ljObj) && ljObj is not null && ljObj.TryGetInteger(out long lj))
        {
            state.LineJoin = (PdfLineJoin)(int)lj;
        }
    }

    private byte[] ConcatenateContentStreams(IReadOnlyList<PdfStream> streams)
    {
        if (streams.Count == 0)
            return Array.Empty<byte>();

        if (streams.Count == 1)
        {
            return _streamDecoder.DecodeStream(streams[0]);
        }

        using var ms = new MemoryStream();
        foreach (var stream in streams)
        {
            byte[] decoded = _streamDecoder.DecodeStream(stream);
            if (decoded.Length > 0)
            {
                ms.Write(decoded);
                ms.WriteByte((byte)'\n');
            }
        }

        return ms.ToArray();
    }

    private static double PopNum(List<PdfObject> operands)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (operands[i].TryGetNumber(out double num))
            {
                operands.RemoveAt(i);
                return num;
            }
        }
        return 0.0;
    }

    private static string PopName(List<PdfObject> operands)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (operands[i].TryGetName(out string name))
            {
                operands.RemoveAt(i);
                return name;
            }
        }
        return string.Empty;
    }
}
