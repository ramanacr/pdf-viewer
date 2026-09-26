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
    ValueTask<IPdfDisplayList> GetPageDisplayListAsync(
        int pageNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Vector visual presentation model for high-fidelity UI rendering.
/// </summary>
public interface IPdfVectorPageVisual
{
    int PageNumber { get; }
    PdfSize Size { get; }
    IPdfDisplayList DisplayList { get; }
}
