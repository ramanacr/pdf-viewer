using PdfEngine.Comparison;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfEngine.Text;

namespace PdfViewer.Core.Comparison;

/// <summary>
/// Subsystem comparing two PDF documents textually and visually with pixel heatmap generation.
/// </summary>
public sealed class PdfComparisonService : IPdfComparisonService
{
    private readonly IPdfRenderer _renderer;
    private readonly IPdfTextService _textService;

    public PdfComparisonService(IPdfRenderer renderer, IPdfTextService textService)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
    }

    public async ValueTask<DocumentComparisonResult> CompareDocumentsAsync(
        IPdfDocument documentA,
        IPdfDocument documentB,
        CancellationToken cancellationToken = default)
    {
        if (documentA == null) throw new ArgumentNullException(nameof(documentA));
        if (documentB == null) throw new ArgumentNullException(nameof(documentB));

        cancellationToken.ThrowIfCancellationRequested();

        var textDiffs = new List<TextDiffChunk>();
        var visualDiffPages = new List<int>();
        int maxPages = Math.Max(documentA.PageCount, documentB.PageCount);
        double totalSimilarity = 0.0;

        for (int p = 1; p <= maxPages; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string textA = p <= documentA.PageCount ? await _textService.ExtractPageTextAsync(documentA, p, cancellationToken) : string.Empty;
            string textB = p <= documentB.PageCount ? await _textService.ExtractPageTextAsync(documentB, p, cancellationToken) : string.Empty;

            if (textA != textB)
            {
                if (string.IsNullOrEmpty(textA))
                {
                    textDiffs.Add(new TextDiffChunk { Type = DiffType.Added, Text = textB, PageNumber = p });
                }
                else if (string.IsNullOrEmpty(textB))
                {
                    textDiffs.Add(new TextDiffChunk { Type = DiffType.Deleted, Text = textA, PageNumber = p });
                }
                else
                {
                    textDiffs.Add(new TextDiffChunk { Type = DiffType.Modified, Text = $"From: '{textA}'\nTo: '{textB}'", PageNumber = p });
                }
            }

            if (p <= documentA.PageCount && p <= documentB.PageCount)
            {
                var req = new RenderRequest { PageNumber = p, Dpi = 72.0 };
                using var pageA = await _renderer.RenderPageAsync(documentA, req, cancellationToken);
                using var pageB = await _renderer.RenderPageAsync(documentB, req, cancellationToken);

                double sim = ComputePixelSimilarity(pageA, pageB);
                totalSimilarity += sim;
                if (sim < 0.999)
                {
                    visualDiffPages.Add(p);
                }
            }
            else
            {
                visualDiffPages.Add(p);
            }
        }

        double avgSimilarity = maxPages > 0 ? totalSimilarity / maxPages : 1.0;

        return new DocumentComparisonResult
        {
            PageCountA = documentA.PageCount,
            PageCountB = documentB.PageCount,
            VisualSimilarityScore = avgSimilarity,
            TextDifferences = textDiffs,
            PagesWithVisualDifferences = visualDiffPages
        };
    }

    public async ValueTask<RenderedPage> GenerateVisualDiffPageAsync(
        IPdfDocument documentA,
        IPdfDocument documentB,
        int pageNumber,
        double dpi = 150.0,
        CancellationToken cancellationToken = default)
    {
        if (documentA == null) throw new ArgumentNullException(nameof(documentA));
        if (documentB == null) throw new ArgumentNullException(nameof(documentB));

        cancellationToken.ThrowIfCancellationRequested();

        var req = new RenderRequest { PageNumber = pageNumber, Dpi = dpi };
        using var pageA = await _renderer.RenderPageAsync(documentA, req, cancellationToken);
        using var pageB = await _renderer.RenderPageAsync(documentB, req, cancellationToken);

        int width = Math.Min(pageA.WidthPixels, pageB.WidthPixels);
        int height = Math.Min(pageA.HeightPixels, pageB.HeightPixels);
        int stride = width * 4;
        int byteLength = stride * height;
        var memoryOwner = new Rendering.ManagedMemoryOwner(byteLength);

        var spanA = pageA.Pixels.Span;
        var spanB = pageB.Pixels.Span;
        var spanDiff = memoryOwner.Memory.Span;

        for (int y = 0; y < height; y++)
        {
            int rowStartA = y * pageA.Stride;
            int rowStartB = y * pageB.Stride;
            int rowStartDiff = y * stride;

            for (int x = 0; x < width; x++)
            {
                int idxA = rowStartA + (x * 4);
                int idxB = rowStartB + (x * 4);
                int idxDiff = rowStartDiff + (x * 4);

                byte bA = spanA[idxA], gA = spanA[idxA + 1], rA = spanA[idxA + 2];
                byte bB = spanB[idxB], gB = spanB[idxB + 1], rB = spanB[idxB + 2];

                int diff = Math.Abs(rA - rB) + Math.Abs(gA - gB) + Math.Abs(bA - bB);

                if (diff > 15)
                {
                    // Magenta highlight for visual difference
                    spanDiff[idxDiff] = 255;     // B
                    spanDiff[idxDiff + 1] = 0;   // G
                    spanDiff[idxDiff + 2] = 255; // R
                    spanDiff[idxDiff + 3] = 255; // A
                }
                else
                {
                    // Dimmed background
                    spanDiff[idxDiff] = (byte)(bA * 0.85);
                    spanDiff[idxDiff + 1] = (byte)(gA * 0.85);
                    spanDiff[idxDiff + 2] = (byte)(rA * 0.85);
                    spanDiff[idxDiff + 3] = 255;
                }
            }
        }

        return new RenderedPage(pageNumber, width, height, stride, dpi, PageRotation.Rotate0, memoryOwner);
    }

    private static double ComputePixelSimilarity(RenderedPage a, RenderedPage b)
    {
        if (a.WidthPixels != b.WidthPixels || a.HeightPixels != b.HeightPixels) return 0.0;

        var spanA = a.Pixels.Span;
        var spanB = b.Pixels.Span;
        int count = Math.Min(spanA.Length, spanB.Length);
        if (count == 0) return 1.0;

        long matchingBytes = 0;
        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(spanA[i] - spanB[i]) < 10) matchingBytes++;
        }

        return (double)matchingBytes / count;
    }
}
