using System.Numerics;
using PdfEngine.Geometry;
using PdfEngine.Vector.Editing;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Glyph runs: the embedded font program by glyph index, else a substitute by Unicode fitted to the
/// document's widths; text clips and stroking render modes from the glyph outlines. The same
/// resolution chain, glyph frame and width fitting as the Direct2D backend's replayer.
/// </summary>
internal sealed partial class SkiaReplayer
{
    private string _lastTextProblem = string.Empty;

    private readonly Dictionary<PdfGlyphRun, ResolvedRun?> _resolved = new(ReferenceEqualityComparer.Instance);

    /// <summary>A run's face, glyph indices and offsets (glyph space, ascender up), and the horizontal scale of each glyph (null when all are 1).</summary>
    private sealed record ResolvedRun(SkiaFace Face, ushort[] Indices, Vector2[] Offsets, float[]? Scales);

    /// <summary>Font face and glyph indices for a run; null when the backend cannot draw it faithfully.</summary>
    private ResolvedRun? ResolveText(PdfGlyphRun run)
    {
        if (_resolved.TryGetValue(run, out var cached))
            return cached;
        var resolved = ResolveTextCore(run);
        _resolved[run] = resolved;
        return resolved;
    }

    private ResolvedRun? ResolveTextCore(PdfGlyphRun run)
    {
        var face = run.Face;
        SkiaFace? embedded = null;
        // An embedded program Skia cannot load, or one in a format the engine does not read, is
        // replaced by a system substitute for the font's name, as PDFium and the Direct2D backend replace it.
        if (face != null && !face.ProgramData.IsEmpty && face.Format is PdfFontProgramFormat.TrueType or PdfFontProgramFormat.OpenTypeCff)
            embedded = _res.GetEmbeddedFace(face);

        double th = run.HorizontalScaling / 100.0;
        double sign = run.FontSize < 0 ? -1 : 1;

        if (embedded != null)
        {
            // Embedded program: glyphs by index.
            var ids = new ushort[run.Glyphs.Count];
            var offs = new Vector2[run.Glyphs.Count];
            int glyphCount = embedded.GlyphCount;
            for (int i = 0; i < run.Glyphs.Count; i++)
            {
                var g = run.Glyphs[i];
                ids[i] = g.GlyphId < glyphCount ? g.GlyphId : (ushort)0;
                offs[i] = new Vector2((float)(g.OffsetX / th * sign), (float)(g.OffsetY * sign));
            }
            return new ResolvedRun(embedded, ids, offs, null);
        }

        // Not embedded: a substitute. Every glyph of the run must be found in one face, so the chain
        // is tried in order: the installed font of that name or design in the style asked for
        // (SystemFontCatalog.Match), the class family (Symbol by its symbol code first), then faces
        // that cover the text's Unicode; the first that has them all wins. Glyphs stay where /Widths put them.
        var (installed, family, bold, italic, isSymbol) = SubstituteFamily(face, run.FontFamilyName ?? run.FontResourceName);
        var chain = new List<(SystemFontFace? Installed, string Family, bool Bold, bool Italic, bool SymbolCodes)>();
        if (installed != null) chain.Add((installed, installed.Family, bold, italic, false));
        if (family != null) chain.Add((null, family, bold, italic, isSymbol));
        if (isSymbol) chain.Add((null, family!, bold, italic, false));
        foreach (var extra in UnicodeFallbackFamilies)
            if (!chain.Exists(c => c.Installed == null && c.Family == extra && !c.SymbolCodes)) chain.Add((null, extra, bold, italic, false));

        (SkiaFace Face, ResolvedRun Mapped, int Missing)? best = null;
        foreach (var (inst, fam, b, it, symbolCodes) in chain)
        {
            var candidate = inst != null ? _res.GetInstalledFace(inst, b, it) : _res.GetSystemFace(fam, b, it);
            if (candidate == null)
                continue;
            var mapped = MapByUnicode(candidate, run, symbolCodes, th, sign, allowMissing: false, out _);
            if (mapped != null)
                return mapped;
            // Best effort: the face that has the most of the run's glyphs draws it, leaving out the
            // ones none of them has (PDFium draws what its substitute has too).
            var partial = MapByUnicode(candidate, run, symbolCodes, th, sign, allowMissing: true, out int missing);
            if (partial != null && (best == null || missing < best.Value.Missing))
                best = (candidate, partial, missing);
        }
        if (best is { } found)
            return found.Mapped;
        _lastTextProblem = family == null ? "No system substitute for font" : "Substitute font lacks a glyph used by this text";
        return null;
    }

    /// <summary>Faces tried after the metric-compatible substitute, for glyphs it lacks (symbols, dingbats, other scripts).</summary>
    private static readonly string[] UnicodeFallbackFamilies =
        { "Segoe UI Symbol", "Arial", "Segoe UI", "Cambria Math", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Ebrima" };

    /// <summary>
    /// Glyph indices for every drawable glyph of a run in <paramref name="face"/>, or null if one is
    /// missing; with the horizontal scale of each (null when all are 1). A substitute's glyph is
    /// fitted to the document's /Widths: one wider than its width is narrowed so it does not run
    /// into the next, and one narrower is widened by up to a tenth and centred in the rest.
    /// </summary>
    /// <param name="allowMissing">Leave out glyphs the face lacks (counted in <paramref name="missing"/>) instead of failing.</param>
    private static ResolvedRun? MapByUnicode(SkiaFace face, PdfGlyphRun run, bool symbolCodes, double th, double sign,
        bool allowMissing, out int missing)
    {
        missing = 0;
        var indices = new List<ushort>(run.Glyphs.Count);
        var offsets = new List<Vector2>(run.Glyphs.Count);
        var widths = new List<double>(run.Glyphs.Count);
        using var font = new SKFont(face.Typeface, 1f) { LinearMetrics = true, Hinting = SKFontHinting.None };
        foreach (var g in run.Glyphs)
        {
            string? text = g.Unicode;
            if (string.IsNullOrEmpty(text) || text == " " || text == " ")
                continue;
            int unicode = char.ConvertToUtf32(text, 0);
            int cp = symbolCodes && g.CharCode >= 0 ? 0xF000 + g.CharCode : unicode;
            ushort index = font.GetGlyph(cp);
            // A re-encoded Symbol font shows its characters by codes of its own: find them by what they are.
            if (index == 0 && symbolCodes && Fonts.SymbolFontCodes.CodeFor(unicode) is int symbolCode)
                index = font.GetGlyph(0xF000 + symbolCode);
            // Adobe's private-use forms (the serif registered sign, pieces of tall brackets) by their standard character.
            if (index == 0 && !symbolCodes && Fonts.SymbolFontCodes.StandardFor(unicode) is int standard)
                index = font.GetGlyph(standard);
            if (index == 0)
            {
                if (char.IsWhiteSpace(text, 0) || char.IsControl(text, 0))
                    continue;
                if (!allowMissing)
                    return null;
                missing++;
                continue;
            }
            indices.Add(index);
            offsets.Add(new Vector2((float)(g.OffsetX / th * sign), (float)(g.OffsetY * sign)));
            widths.Add(g.Width);
        }
        var ids = indices.ToArray();
        float[]? scales = null;
        if (ids.Length > 0 && widths.Exists(w => w > 0))
        {
            // Advances of a 1-unit font are the design advances in em.
            var advances = font.GetGlyphWidths(ids);
            double em = Math.Abs(run.FontSize);
            for (int i = 0; i < ids.Length; i++)
            {
                double own = advances[i];
                if (widths[i] <= 0 || own <= 0 || Math.Abs(widths[i] - own) <= own * 0.01) continue;
                double scale = Math.Min(widths[i] / own, 1.10);
                scales ??= Enumerable.Repeat(1f, ids.Length).ToArray();
                scales[i] = (float)scale;
                // Centred in what is left of its width (glyph space: text space without Tz).
                double gap = (widths[i] - own * scale) * em / 2;
                if (gap > 0) offsets[i] = offsets[i] with { X = offsets[i].X + (float)(gap * sign) };
            }
        }
        return new ResolvedRun(face, ids, offsets.ToArray(), scales);
    }

    /// <summary>
    /// Substitutes for a non-embedded font: the installed face <see cref="SystemFontCatalog.Match"/>
    /// chooses (null for Symbol and Dingbats, which go by their own codes), and the class family
    /// tried after it. The Direct2D backend's rule.
    /// </summary>
    private (SystemFontFace? Installed, string? Family, bool Bold, bool Italic, bool Symbol) SubstituteFamily(PdfFontFace? face, string? fontName)
    {
        string name = face?.PostScriptName ?? fontName ?? string.Empty;
        int plus = name.IndexOf('+');
        if (plus >= 0 && plus < name.Length - 1) name = name[(plus + 1)..];
        bool bold = face?.IsBold == true || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = face?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        if (name.Contains("Dingbat", StringComparison.OrdinalIgnoreCase))
            return (null, "Segoe UI Symbol", false, false, false); // the Dingbats block, by the encoding's Unicode
        if (name.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase))
            return (null, "Symbol", false, false, true);
        var installed = _res.Fonts.Catalog.Match(name, bold, italic, face?.IsSerif == true, face?.IsFixedPitch == true, face?.IsScript == true);
        if (face?.IsFixedPitch == true || name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase))
            return (installed, "Courier New", bold, italic, false);
        if (face?.IsSerif == true || name.Contains("Times", StringComparison.OrdinalIgnoreCase) || (name.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !name.Contains("Sans", StringComparison.OrdinalIgnoreCase)))
            return (installed, "Times New Roman", bold, italic, false);
        return (installed, "Arial", bold, italic, false); // metric-compatible with Helvetica
    }

    /// <summary>
    /// The face's glyph matrix (the part of a CFF FontMatrix beyond the 1/unitsPerEm scale: an
    /// oblique's skew, a narrow face's x scale) in Skia glyph space (y down, em = size), and the
    /// glyph positions pre-compensated so each glyph still lands at its PDF position.
    /// </summary>
    private static (Matrix3x2 Glyph, SKPoint[] Positions) GlyphFrame(PdfFontFace? face, float emSize, Vector2[] offsets)
    {
        // Offsets are (advance, ascender-up); in y-down glyph space a position is (x, -up).
        if (face?.GlyphMatrix is not { } g)
            return (Matrix3x2.Identity, offsets.Select(o => new SKPoint(o.X, -o.Y)).ToArray());
        var glyph = new Matrix3x2((float)g.A, (float)-g.B, (float)-g.C, (float)g.D, (float)(g.E * emSize), (float)(-g.F * emSize));
        var linear = glyph with { M31 = 0, M32 = 0 };
        if (!Matrix3x2.Invert(linear, out var inverse))
            return (Matrix3x2.Identity, offsets.Select(o => new SKPoint(o.X, -o.Y)).ToArray());
        return (glyph, offsets.Select(o =>
        {
            var q = Vector2.Transform(new Vector2(o.X, -o.Y), inverse);
            return new SKPoint(q.X, q.Y);
        }).ToArray());
    }

    /// <summary>A resolved run in pieces of one horizontal glyph scale: each piece's glyphs and positions, in its scaled glyph space.</summary>
    private static IEnumerable<(ushort[] Indices, SKPoint[] Positions, float Scale)> ScaledPieces(ushort[] indices, SKPoint[] positions, float[]? scales)
    {
        if (scales == null)
        {
            yield return (indices, positions, 1f);
            yield break;
        }
        int i = 0;
        while (i < indices.Length)
        {
            int j = i + 1;
            while (j < indices.Length && scales[j] == scales[i]) j++;
            float s = scales[i];
            // The piece is drawn scaled by s across: its positions are divided by s so they land where they were.
            var piece = positions[i..j].Select(p => new SKPoint(p.X / s, p.Y)).ToArray();
            yield return (indices[i..j], piece, s);
            i = j;
        }
    }

    /// <summary>Glyph space (y-down, baseline at the origin, em = |Tfs|) → text space.</summary>
    private static Matrix3x2 GlyphToText(PdfGlyphRun run, Matrix3x2 glyphMatrix)
    {
        double th = run.HorizontalScaling / 100.0;
        float sign = run.FontSize < 0 ? -1 : 1;
        return glyphMatrix * new Matrix3x2((float)(sign * th), 0, 0, -sign, 0, (float)run.TextRise);
    }

    /// <summary>The outlines of glyphs at their positions, in the glyph space of <paramref name="font"/>.</summary>
    private static SKPath GlyphOutlines(SKFont font, ushort[] indices, SKPoint[] positions)
    {
        var outline = new SKPath { FillType = SKPathFillType.Winding };
        for (int i = 0; i < indices.Length; i++)
        {
            using var glyph = font.GetGlyphPath(indices[i]);
            if (glyph == null || glyph.IsEmpty) continue;
            outline.AddPath(glyph, positions[i].X, positions[i].Y);
        }
        return outline;
    }

    // Glyph outlines per run, in pieces of one horizontal scale: built once, reused by every render.
    private readonly Dictionary<DrawGlyphRun, (SKPath Outline, Matrix3x2 PieceToText)[]> _glyphOutlines = new(ReferenceEqualityComparer.Instance);

    /// <summary>The run's glyph outlines, each piece with its glyph space → text space transform.</summary>
    private (SKPath Outline, Matrix3x2 PieceToText)[] RunOutlines(DrawGlyphRun cmd, ResolvedRun r)
    {
        if (_glyphOutlines.TryGetValue(cmd, out var cached))
            return cached;
        var run = cmd.Run;
        float em = (float)Math.Abs(run.FontSize);
        var (glyphMatrix, positions) = GlyphFrame(run.Face, em, r.Offsets);
        var glyphToText = GlyphToText(run, glyphMatrix);
        using var font = r.Face.CreateFont(em);
        var pieces = ScaledPieces(r.Indices, positions, r.Scales)
            .Select(p => (GlyphOutlines(font, p.Indices, p.Positions), Matrix3x2.CreateScale(p.Scale, 1) * glyphToText))
            .ToArray();
        _glyphOutlines[cmd] = pieces;
        return pieces;
    }

    /// <summary>
    /// Draws a glyph run as filled outlines, as the Direct2D backend's outline rendering mode does
    /// (no hinting, no glyph-cache gamma): text then has the same weight and position as there.
    /// </summary>
    private void DrawText(SKCanvas canvas, DrawGlyphRun cmd, Matrix3x2 full)
    {
        var run = cmd.Run;
        if (ResolveText(run) is not { } r || r.Indices.Length == 0 || run.FontSize == 0)
            return;
        var textMatrix = M(run.TextMatrix);
        int mode = run.RenderingMode;
        bool fill = mode is 0 or 2 or 4 or 6, stroke = mode is 1 or 2 or 5 or 6;
        foreach (var (outline, pieceToText) in RunOutlines(cmd, r))
        {
            var pieceTransform = pieceToText * textMatrix * full;
            if (fill)
            {
                using var paint = FillPaint(cmd.Paint.Color, cmd.Paint.Alpha);
                canvas.SetMatrix(ToSk(pieceTransform));
                canvas.DrawPath(outline, paint);
            }
            if (stroke && run.StrokePaint is PdfPaint sp && run.Stroke is PdfStroke st)
            {
                // Stroke width is user space: express it in glyph space.
                float textScale = MathF.Sqrt(MathF.Abs((pieceToText * textMatrix).GetDeterminant()));
                Stroke(canvas, outline, Color(sp.Color, sp.Alpha), st with { Width = st.Width / Math.Max(textScale, 1e-6), DashArray = null }, pieceTransform);
            }
        }
    }

    private readonly Dictionary<PushTextClip, SKPath> _textOutlines = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Union of the runs' glyph outlines in user space (nonzero). A run whose font cannot be
    /// outlined contributes its box instead (the region is classified by <see cref="Analyze"/>).
    /// </summary>
    private SKPath TextOutline(PushTextClip cmd, Matrix3x2 ctm)
    {
        if (_textOutlines.TryGetValue(cmd, out var cached))
            return cached;
        var result = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var run in cmd.Runs)
        {
            if (run.FontSize == 0 || run.Glyphs.Count == 0)
                continue;
            if (ResolveText(run) is not { } r)
            {
                if (cmd.Bounds is PdfRect b && Matrix3x2.Invert(ctm, out var pageToUser))
                {
                    using var box = Polygon(pageToUser, b);
                    result.AddPath(box);
                }
                continue;
            }
            float em = (float)Math.Abs(run.FontSize);
            var (glyphMatrix, positions) = GlyphFrame(run.Face, em, r.Offsets);
            var glyphToUser = GlyphToText(run, glyphMatrix) * M(run.TextMatrix);
            using var font = r.Face.CreateFont(em);
            foreach (var (indices, piecePositions, scale) in ScaledPieces(r.Indices, positions, r.Scales))
            {
                using var outline = GlyphOutlines(font, indices, piecePositions);
                var toUser = ToSk(Matrix3x2.CreateScale(scale, 1) * glyphToUser);
                result.AddPath(outline, in toUser);
            }
        }
        _textOutlines[cmd] = result;
        return result;
    }
}
