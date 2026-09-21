using System.Collections.Concurrent;
using System.Collections.Generic;
using PdfEngine.Vector;

namespace PdfEngine.Diagnostics;

/// <summary>
/// Execution metrics and fallback diagnostics collected per page.
/// Adheres strictly to local diagnostics only (no external telemetry).
/// </summary>
public sealed class PdfPageMetrics
{
    public int PageNumber { get; init; }
    public bool IsFullyVector { get; set; } = true;
    public int DrawCommandsCount { get; set; }
    public int FallbackCommandsCount { get; set; }
    public double FallbackAreaRatio { get; set; }
    public double ParseDurationMs { get; set; }
    public double RenderDurationMs { get; set; }
    public List<PdfFallbackReason> FallbackReasons { get; } = new();
}

/// <summary>
/// Aggregated engine performance and fallback metrics for an opened document or session.
/// </summary>
public sealed class PdfEngineMetrics
{
    private readonly ConcurrentBag<PdfPageMetrics> _pageMetrics = new();
    private readonly ConcurrentDictionary<PdfFallbackReason, int> _reasonCounts = new();

    public int TotalPagesParsed { get; set; }
    public int TotalPagesFullyVector { get; set; }
    public int TotalPagesWithFallback { get; set; }
    public int TotalRecoveries { get; set; }

    public IReadOnlyCollection<PdfPageMetrics> PageMetrics => _pageMetrics;
    public IReadOnlyDictionary<PdfFallbackReason, int> ReasonCounts => _reasonCounts;

    public void RecordPage(PdfPageMetrics metrics)
    {
        _pageMetrics.Add(metrics);
        if (metrics.IsFullyVector)
        {
            TotalPagesFullyVector++;
        }
        else
        {
            TotalPagesWithFallback++;
        }

        foreach (var reason in metrics.FallbackReasons)
        {
            _reasonCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
        }
    }

    public double GetOverallVectorRatio() =>
        TotalPagesParsed > 0 ? (double)TotalPagesFullyVector / TotalPagesParsed : 1.0;
}
