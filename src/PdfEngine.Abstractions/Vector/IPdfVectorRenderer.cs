using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Rendering;

namespace PdfEngine.Vector;

/// <summary>
/// Renderer capable of replaying an immutable vector display list to raster or native surfaces.
/// </summary>
public interface IPdfVectorRenderer : IDisposable
{
    /// <summary>Draws <paramref name="displayList"/> into a new BGRA pixel buffer.</summary>
    /// <param name="displayList">The page to draw, from <see cref="IPdfVectorDocument.GetPageDisplayListAsync"/>.</param>
    /// <param name="request">The resolution, output size and viewer rotation.</param>
    /// <param name="fallbackProvider">Draws the regions this renderer cannot draw faithfully, or null to draw them by best effort.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The pixels. Dispose the result to release its buffer.</returns>
    ValueTask<RenderedPage> RenderDisplayListAsync(
        IPdfDisplayList displayList,
        RenderRequest request,
        IPdfFallbackProvider? fallbackProvider = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every region needing fallback for this backend: the display list's own tokens plus what only
    /// the backend can detect (font programs it cannot load, glyphs a substitute lacks, images it
    /// cannot decode).
    /// </summary>
    Task<IReadOnlyList<PdfFallbackToken>> AnalyzeAsync(IPdfDisplayList displayList, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended document interface exposing vector display lists.
/// </summary>
public interface IPdfVectorDocument : IPdfDocument
{
    /// <summary>Interprets the content of a page and returns its display list.</summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The display list. It is immutable; one list can be rendered many times, at any resolution.</returns>
    ValueTask<IPdfDisplayList> GetPageDisplayListAsync(
        int pageNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Vector visual presentation model for high-fidelity UI rendering.
/// </summary>
public interface IPdfVectorPageVisual
{
    /// <summary>The page number, starting at 1.</summary>
    int PageNumber { get; }
    /// <summary>The page size in points.</summary>
    PdfSize Size { get; }
    /// <summary>The display list of the page.</summary>
    IPdfDisplayList DisplayList { get; }
}
