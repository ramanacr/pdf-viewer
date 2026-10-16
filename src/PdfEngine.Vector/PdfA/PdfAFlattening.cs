using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.PdfA;

/// <summary>
/// Draws a page to opaque pixels for transparency flattening. The core has no rasterizer of its
/// own; the composition root supplies one (the Direct2D renderer, with PDFium for pages the
/// vector engine cannot draw by itself).
/// </summary>
public interface IPdfPageRasterizer
{
    /// <summary>
    /// The pixels of <paramref name="window"/> of the page drawn over white, unrotated, its crop
    /// box filling exactly <paramref name="pageWidthPixels"/> × <paramref name="pageHeightPixels"/>,
    /// without annotations; or null when it cannot be drawn faithfully.
    /// </summary>
    /// <param name="bytes">The file <paramref name="document"/> was opened from (for a renderer that reads it itself).</param>
    PdfRasterImage? Render(PdfVectorDocument document, byte[] bytes, int pageNumber, int pageWidthPixels, int pageHeightPixels, PdfPixelWindow window);
}

/// <summary>A rectangle of a page's pixels, from its top-left corner.</summary>
public readonly record struct PdfPixelWindow(int X, int Y, int Width, int Height);

/// <summary>Pixels as 8-bit BGRA rows (stride = width × 4), and which renderer drew them.</summary>
public sealed record PdfRasterImage(int Width, int Height, byte[] Bgra, string Renderer);

/// <summary>How transparency is flattened for PDF/A-1.</summary>
public sealed class PdfFlatteningOptions
{
    /// <summary>Resolution of flattened areas without text, in dots per inch.</summary>
    public double Dpi { get; init; } = 300;

    /// <summary>Resolution of flattened areas that show text, which needs more to stay sharp.</summary>
    public double TextDpi { get; init; } = 600;

    /// <summary>When the flattened areas cover at least this share of the page, the whole page is flattened as one image.</summary>
    public double WholePageCoverage { get; init; } = 0.6;

    /// <summary>Most pixels one flattened area may have; a larger one is drawn at a lower resolution.</summary>
    public long MaxPixelsPerRegion { get; init; } = 40_000_000;
}

/// <summary>An area of a page replaced by an image, in default user space.</summary>
public sealed record PdfFlattenedRegion(PdfRect Bounds, double Dpi, int WidthPixels, int HeightPixels, bool ContainsText);

/// <summary>A page whose transparency was flattened.</summary>
public sealed record PdfFlattenedPage(int PageNumber, IReadOnlyList<PdfFlattenedRegion> Regions, bool WholePage, string Renderer);

/// <summary>What transparency flattening did to a document.</summary>
public sealed class PdfFlatteningReport
{
    /// <summary>Pages where transparent content was replaced by images, with the areas.</summary>
    public required IReadOnlyList<PdfFlattenedPage> Pages { get; init; }

    /// <summary>Pages whose transparency could be removed without changing how they look (opaque groups, unused alpha).</summary>
    public required int PagesCleanedOnly { get; init; }

    /// <summary>Transparency groups removed from pages and forms.</summary>
    public required int GroupsRemoved { get; init; }

    /// <summary>Text-showing operators kept as invisible text where their transparent drawing was flattened.</summary>
    public required int InvisibleTextOperators { get; init; }

    public int RegionCount => Pages.Sum(p => p.Regions.Count);

    /// <summary>One line for the user: pages, areas and resolutions (never any document text).</summary>
    public string Summary
    {
        get
        {
            if (Pages.Count == 0)
                return $"Removed transparency that did not change the look of {PagesCleanedOnly} page(s)";
            var dpis = Pages.SelectMany(p => p.Regions).Select(r => Math.Round(r.Dpi)).Distinct().OrderBy(d => d).ToList();
            string dpi = dpis.Count == 1
                ? $"{dpis[0].ToString(CultureInfo.InvariantCulture)} dpi"
                : $"{dpis[0].ToString(CultureInfo.InvariantCulture)}-{dpis[^1].ToString(CultureInfo.InvariantCulture)} dpi";
            string pages = string.Join(", ", Pages.Select(p => p.PageNumber.ToString(CultureInfo.InvariantCulture)));
            int whole = Pages.Count(p => p.WholePage);
            string what = whole == Pages.Count ? "whole page(s)" : $"{RegionCount} area(s)";
            return $"Flattened transparency on page(s) {pages} ({what} drawn as images at {dpi}); everything else stays vector and text stays searchable";
        }
    }
}
