using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using PdfEngine.Vector;

namespace PdfEngine.Diagnostics;

/// <summary>
/// Execution metrics and fallback diagnostics collected per page.
/// Adheres strictly to local diagnostics only (no external telemetry, no document text).
/// </summary>
public sealed class PdfPageMetrics
{
    public int PageNumber { get; init; }
    public bool IsFullyVector { get; set; } = true;
    /// <summary>The whole page was delegated to the fallback engine.</summary>
    public bool IsFullPageFallback { get; set; }
    public int DrawCommandsCount { get; set; }
    public int FallbackCommandsCount { get; set; }
    public double FallbackAreaRatio { get; set; }
    public double ParseDurationMs { get; set; }
    public double RenderDurationMs { get; set; }
    public double FallbackRenderDurationMs { get; set; }
    public List<PdfFallbackReason> FallbackReasons { get; } = new();
}

/// <summary>
/// Aggregated engine performance and fallback metrics for an opened document or session
/// (06_PDFIUM_FALLBACK_AND_EXIT_PLAN "Telemetry": local only, never transmitted).
/// </summary>
public sealed class PdfEngineMetrics
{
    private readonly ConcurrentDictionary<int, PdfPageMetrics> _pageMetrics = new();
    private readonly ConcurrentDictionary<PdfFallbackReason, int> _reasonCounts = new();
    private int _pagesParsed;
    private int _pagesFullyVector;
    private int _pagesWithFallback;
    private int _pagesFullFallback;
    private int _recoveries;

    public int TotalPagesParsed { get => _pagesParsed; set => _pagesParsed = value; }
    public int TotalPagesFullyVector { get => _pagesFullyVector; set => _pagesFullyVector = value; }
    public int TotalPagesWithFallback { get => _pagesWithFallback; set => _pagesWithFallback = value; }
    public int TotalPagesFullFallback => _pagesFullFallback;
    public int TotalRecoveries { get => _recoveries; set => _recoveries = value; }

    /// <summary>Why the vector core could not open the document at all, if it could not.</summary>
    public string? DocumentFallbackReason { get; set; }

    public IReadOnlyCollection<PdfPageMetrics> PageMetrics => _pageMetrics.Values.ToList();
    public IReadOnlyDictionary<PdfFallbackReason, int> ReasonCounts => _reasonCounts;

    /// <summary>Records a page once; re-renders of the same page (zoom, scroll) do not double count.</summary>
    public void RecordPage(PdfPageMetrics metrics)
    {
        if (!_pageMetrics.TryAdd(metrics.PageNumber, metrics))
            return;

        Interlocked.Increment(ref _pagesParsed);
        if (metrics.IsFullyVector)
            Interlocked.Increment(ref _pagesFullyVector);
        else
            Interlocked.Increment(ref _pagesWithFallback);
        if (metrics.IsFullPageFallback)
            Interlocked.Increment(ref _pagesFullFallback);

        foreach (var reason in metrics.FallbackReasons.Distinct())
        {
            _reasonCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
        }
    }

    public void RecordRecovery() => Interlocked.Increment(ref _recoveries);

    /// <summary>Page coverage: fraction of recorded pages rendered with no fallback at all.</summary>
    public double GetOverallVectorRatio() =>
        TotalPagesParsed > 0 ? (double)TotalPagesFullyVector / TotalPagesParsed : 1.0;

    /// <summary>Area coverage: mean fraction of page area rendered by the vector path.</summary>
    public double GetVectorAreaRatio()
    {
        var pages = _pageMetrics.Values.ToList();
        return pages.Count == 0 ? 1.0 : 1.0 - pages.Average(p => Math.Clamp(p.FallbackAreaRatio, 0, 1));
    }

    /// <summary>
    /// Deterministic JSON diagnostic report (backlog A5). Contains page numbers, counts, timings and
    /// fallback reason names only — never document text, metadata or file paths.
    /// </summary>
    public string ToJsonReport()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"pagesParsed\":").Append(TotalPagesParsed.ToString(inv));
        sb.Append(",\"pagesFullyVector\":").Append(TotalPagesFullyVector.ToString(inv));
        sb.Append(",\"pagesWithFallback\":").Append(TotalPagesWithFallback.ToString(inv));
        sb.Append(",\"pagesFullPageFallback\":").Append(TotalPagesFullFallback.ToString(inv));
        sb.Append(",\"recoveries\":").Append(TotalRecoveries.ToString(inv));
        sb.Append(",\"pageCoverage\":").Append(GetOverallVectorRatio().ToString("0.####", inv));
        sb.Append(",\"areaCoverage\":").Append(GetVectorAreaRatio().ToString("0.####", inv));
        if (DocumentFallbackReason != null)
            sb.Append(",\"documentFallback\":\"").Append(DocumentFallbackReason).Append('"');
        sb.Append(",\"reasons\":{");
        bool first = true;
        foreach (var (reason, count) in _reasonCounts.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(reason.ToString()).Append("\":").Append(count.ToString(inv));
        }
        sb.Append("},\"pages\":[");
        first = true;
        foreach (var p in _pageMetrics.Values.OrderBy(p => p.PageNumber))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"page\":").Append(p.PageNumber.ToString(inv))
              .Append(",\"vector\":").Append(p.IsFullyVector ? "true" : "false")
              .Append(",\"fullFallback\":").Append(p.IsFullPageFallback ? "true" : "false")
              .Append(",\"commands\":").Append(p.DrawCommandsCount.ToString(inv))
              .Append(",\"fallbackCommands\":").Append(p.FallbackCommandsCount.ToString(inv))
              .Append(",\"fallbackArea\":").Append(p.FallbackAreaRatio.ToString("0.####", inv))
              .Append(",\"buildMs\":").Append(p.ParseDurationMs.ToString("0.#", inv))
              .Append(",\"renderMs\":").Append(p.RenderDurationMs.ToString("0.#", inv))
              .Append(",\"fallbackRenderMs\":").Append(p.FallbackRenderDurationMs.ToString("0.#", inv))
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
