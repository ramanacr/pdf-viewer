using System;
using System.Collections.Generic;
using PdfEngine.Geometry;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Graphics;

/// <summary>
/// Mutable graphics state tracked on the interpreter state stack (ISO 32000-2 8.4, Tables 51–52).
/// </summary>
public sealed class GraphicsState
{
    public PdfMatrix CTM { get; set; } = PdfMatrix.Identity;
    public double LineWidth { get; set; } = 1.0;
    public PdfLineCap LineCap { get; set; } = PdfLineCap.Butt;
    public PdfLineJoin LineJoin { get; set; } = PdfLineJoin.Miter;
    public double MiterLimit { get; set; } = 10.0;
    public IReadOnlyList<double>? DashArray { get; set; }
    public double DashPhase { get; set; } = 0.0;

    /// <summary>Stroke colour, always opaque; transparency lives in <see cref="StrokeAlpha"/>.</summary>
    public PdfColor StrokeColor { get; set; } = PdfColor.Black;
    /// <summary>Fill colour, always opaque; transparency lives in <see cref="FillAlpha"/>.</summary>
    public PdfColor FillColor { get; set; } = PdfColor.Black;
    public PdfEngine.Vector.Color.PdfColorSpace StrokeColorSpace { get; set; } = PdfEngine.Vector.Color.PdfColorSpace.DeviceGray;
    public PdfEngine.Vector.Color.PdfColorSpace FillColorSpace { get; set; } = PdfEngine.Vector.Color.PdfColorSpace.DeviceGray;

    /// <summary>Resolved pattern (dictionary or stream) when the fill colour space is /Pattern.</summary>
    public PdfObject? FillPattern { get; set; }
    public PdfObject? StrokePattern { get; set; }

    public double StrokeAlpha { get; set; } = 1.0;
    public double FillAlpha { get; set; } = 1.0;
    public string BlendMode { get; set; } = "Normal";

    /// <summary>An ExtGState /SMask other than /None is in effect.</summary>
    public bool SoftMaskActive { get; set; }

    /// <summary>ExtGState /AIS — alpha constants are shape, not opacity.</summary>
    public bool AlphaIsShape { get; set; }

    public PdfPath? ClipPath { get; set; }
    public PdfFillRule ClipRule { get; set; } = PdfFillRule.NonZero;

    /// <summary>
    /// Conservative bounds of the current clip in default user space. Every emitted command's
    /// bounds are intersected with it, and fallback regions default to it.
    /// </summary>
    public PdfRect ClipBoundsPage { get; set; }

    // Text State
    public string CurrentFontResource { get; set; } = string.Empty;
    public PdfFont? Font { get; set; }
    public double FontSize { get; set; } = 12.0;
    public double CharacterSpacing { get; set; } = 0.0;
    public double WordSpacing { get; set; } = 0.0;
    public double HorizontalScaling { get; set; } = 100.0; // percent
    public double Leading { get; set; } = 0.0;
    public int TextRenderingMode { get; set; } = 0;
    public double TextRise { get; set; } = 0.0;

    public PdfMatrix TextMatrix { get; set; } = PdfMatrix.Identity;
    public PdfMatrix TextLineMatrix { get; set; } = PdfMatrix.Identity;

    public bool IsNormalBlend => BlendMode is "Normal" or "Compatible";

    public GraphicsState Clone()
    {
        return new GraphicsState
        {
            CTM = CTM,
            LineWidth = LineWidth,
            LineCap = LineCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit,
            DashArray = DashArray,
            DashPhase = DashPhase,
            StrokeColor = StrokeColor,
            FillColor = FillColor,
            StrokeColorSpace = StrokeColorSpace,
            FillColorSpace = FillColorSpace,
            FillPattern = FillPattern,
            StrokePattern = StrokePattern,
            StrokeAlpha = StrokeAlpha,
            FillAlpha = FillAlpha,
            BlendMode = BlendMode,
            SoftMaskActive = SoftMaskActive,
            AlphaIsShape = AlphaIsShape,
            ClipPath = ClipPath,
            ClipRule = ClipRule,
            ClipBoundsPage = ClipBoundsPage,
            CurrentFontResource = CurrentFontResource,
            Font = Font,
            FontSize = FontSize,
            CharacterSpacing = CharacterSpacing,
            WordSpacing = WordSpacing,
            HorizontalScaling = HorizontalScaling,
            Leading = Leading,
            TextRenderingMode = TextRenderingMode,
            TextRise = TextRise,
            TextMatrix = TextMatrix,
            TextLineMatrix = TextLineMatrix
        };
    }

    public PdfStroke CreateStroke() =>
        new(LineWidth, LineCap, LineJoin, MiterLimit, DashArray, DashPhase);

    public PdfPaint CreateStrokePaint() =>
        new(StrokeColor, StrokeAlpha);

    public PdfPaint CreateFillPaint() =>
        new(FillColor, FillAlpha);
}
