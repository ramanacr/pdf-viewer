using System;
using System.Collections.Generic;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Graphics;

/// <summary>
/// Mutable graphics state tracked on the interpreter state stack.
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

    public PdfColor StrokeColor { get; set; } = PdfColor.Black;
    public PdfColor FillColor { get; set; } = PdfColor.Black;
    public PdfEngine.Vector.Color.PdfColorSpace StrokeColorSpace { get; set; } = PdfEngine.Vector.Color.PdfColorSpace.DeviceGray;
    public PdfEngine.Vector.Color.PdfColorSpace FillColorSpace { get; set; } = PdfEngine.Vector.Color.PdfColorSpace.DeviceGray;
    public double StrokeAlpha { get; set; } = 1.0;
    public double FillAlpha { get; set; } = 1.0;
    public string BlendMode { get; set; } = "Normal";

    public PdfPath? ClipPath { get; set; }
    public PdfFillRule ClipRule { get; set; } = PdfFillRule.NonZero;

    // Text State
    public string CurrentFontResource { get; set; } = string.Empty;
    public double FontSize { get; set; } = 12.0;
    public double CharacterSpacing { get; set; } = 0.0;
    public double WordSpacing { get; set; } = 0.0;
    public double HorizontalScaling { get; set; } = 100.0; // percent
    public double Leading { get; set; } = 0.0;
    public int TextRenderingMode { get; set; } = 0;
    public double TextRise { get; set; } = 0.0;

    public PdfMatrix TextMatrix { get; set; } = PdfMatrix.Identity;
    public PdfMatrix TextLineMatrix { get; set; } = PdfMatrix.Identity;

    public GraphicsState Clone()
    {
        return new GraphicsState
        {
            CTM = CTM,
            LineWidth = LineWidth,
            LineCap = LineCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit,
            DashArray = DashArray != null ? new List<double>(DashArray) : null,
            DashPhase = DashPhase,
            StrokeColor = StrokeColor,
            FillColor = FillColor,
            StrokeColorSpace = StrokeColorSpace,
            FillColorSpace = FillColorSpace,
            StrokeAlpha = StrokeAlpha,
            FillAlpha = FillAlpha,
            BlendMode = BlendMode,
            ClipPath = ClipPath,
            ClipRule = ClipRule,
            CurrentFontResource = CurrentFontResource,
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
