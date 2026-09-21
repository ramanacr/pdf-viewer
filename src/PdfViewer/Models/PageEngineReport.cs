using System;
using System.Collections.Generic;
using PdfEngine.Vector;

namespace PdfViewer.Models;

/// <summary>
/// Diagnostic report for a rendered page, specifying which engine was used and whether fallback occurred.
/// </summary>
public sealed record PageEngineReport(
    int PageNumber,
    PdfEngineMode Engine,
    bool IsFallback,
    IReadOnlyList<string> FallbackReasons,
    string BadgeText,
    string Tooltip);
