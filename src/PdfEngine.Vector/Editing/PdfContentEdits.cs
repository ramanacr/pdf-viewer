using System;
using System.Collections.Generic;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Editing;

/// <summary>A paragraph (or line) of the page's text that can be edited, moved or deleted.</summary>
/// <param name="Bounds">In default user space (points, y up, before /Rotate).</param>
/// <param name="FontSize">The em height in user units.</param>
/// <param name="FontFamily">An installed family that looks like the font (for showing the text while it is edited), when there is one.</param>
/// <param name="Color">Fill colour as RGB, 0..1.</param>
/// <param name="AngleDegrees">Direction of the baseline in user space, counter-clockwise from +x.</param>
public sealed record PdfEditableText(
    int Id, string Text, PdfRect Bounds, IReadOnlyList<PdfRect> LineBounds, string FontName, string? FontFamily, double FontSize,
    bool Bold, bool Italic, IReadOnlyList<double> Color, PdfTextAlignment Alignment, double AngleDegrees, double LineSpacing);

/// <summary>An image the page draws, that can be moved, resized, replaced or deleted.</summary>
/// <param name="Placement">The image's unit square to user space.</param>
public sealed record PdfEditableImage(int Id, PdfRect Bounds, PdfMatrix Placement, int PixelWidth, int PixelHeight);

/// <summary>What of a page can be edited.</summary>
public sealed record PdfEditablePage(int PageNumber, IReadOnlyList<PdfEditableText> Texts, IReadOnlyList<PdfEditableImage> Images);

public enum PdfEditTarget { Text, Image }

/// <summary>A change to a page's content.</summary>
public abstract record PdfContentEdit(int PageNumber);

/// <summary>A paragraph's text replaced; it reflows within its width, keeping its fonts, sizes and colours.</summary>
public sealed record PdfReplaceText(int PageNumber, int TextId, string NewText) : PdfContentEdit(PageNumber);

/// <summary>A paragraph or image moved, resized or turned: <paramref name="Transform"/> maps its current user-space position to the new one.</summary>
public sealed record PdfTransformContent(int PageNumber, PdfEditTarget Target, int Id, PdfMatrix Transform) : PdfContentEdit(PageNumber);

public sealed record PdfDeleteContent(int PageNumber, PdfEditTarget Target, int Id) : PdfContentEdit(PageNumber);

/// <summary>An image replaced by another, fitted into the same place (keeping the new image's proportions).</summary>
public sealed record PdfReplaceImage(int PageNumber, int ImageId, PdfImageContent Image) : PdfContentEdit(PageNumber);

/// <summary>New text: lines separated by '\n', the first line's baseline starting at <paramref name="Baseline"/> (user space).</summary>
public sealed record PdfAddText(int PageNumber, PdfPoint Baseline, string Text, PdfTextFormat Format) : PdfContentEdit(PageNumber);

/// <summary>A new image filling <paramref name="Bounds"/> (user space), turned by <paramref name="AngleDegrees"/> about its centre.</summary>
public sealed record PdfAddImage(int PageNumber, PdfRect Bounds, PdfImageContent Image, double AngleDegrees = 0) : PdfContentEdit(PageNumber);

/// <summary>How new text looks. Colour components are 0..1; the angle is the baseline's direction in user space.</summary>
public sealed record PdfTextFormat(string FontFamily, double Size, bool Bold = false, bool Italic = false,
    double Red = 0, double Green = 0, double Blue = 0, double AngleDegrees = 0);

public enum PdfImageEncoding
{
    /// <summary>A baseline or progressive JPEG with 1 (gray) or 3 (RGB) components, embedded as it is.</summary>
    Jpeg,
    /// <summary>8-bit samples, 3 per pixel.</summary>
    Rgb,
    /// <summary>8-bit samples, 1 per pixel.</summary>
    Gray,
}

/// <summary>Image data to place on a page. <paramref name="Alpha"/>: optional 8-bit opacity per pixel.</summary>
public sealed record PdfImageContent(int Width, int Height, byte[] Data, PdfImageEncoding Encoding, byte[]? Alpha = null, int Components = 3);

public sealed class PdfContentEditOptions
{
    /// <summary>Where fonts for new characters come from (the installed fonts by default).</summary>
    public SystemFontCatalog? Fonts { get; init; }
}

public sealed class PdfContentEditResult
{
    public required byte[] Bytes { get; init; }
    /// <summary>Fonts embedded for characters the document's own fonts do not have.</summary>
    public IReadOnlyList<string> EmbeddedFonts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
