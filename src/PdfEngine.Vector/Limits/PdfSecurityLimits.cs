namespace PdfEngine.Vector.Limits;

/// <summary>
/// Hard ceilings and defensive limits for parsing untrusted hostile PDF data.
/// Protects against decompression bombs, cyclic object references, infinite recursion, and OOM.
/// </summary>
public sealed class PdfSecurityLimits
{
    public static readonly PdfSecurityLimits Default = new();

    /// <summary>Maximum indirect object count to resolve per document.</summary>
    public int MaxObjectsCount { get; init; } = 500_000;

    /// <summary>Maximum page tree depth before aborting recursion.</summary>
    public int MaxPageTreeDepth { get; init; } = 64;

    /// <summary>Maximum dictionary or array nesting depth.</summary>
    public int MaxNestingDepth { get; init; } = 64;

    /// <summary>Maximum string or token length in bytes (64 MB).</summary>
    public int MaxTokenLength { get; init; } = 64 * 1024 * 1024;

    /// <summary>Maximum decoded stream expansion size in bytes (128 MB per stream).</summary>
    public long MaxDecodedStreamBytes { get; init; } = 128 * 1024 * 1024;

    /// <summary>Maximum decompression expansion ratio (e.g. 500x).</summary>
    public double MaxDecompressionRatio { get; init; } = 500.0;

    /// <summary>Maximum number of `/Prev` trailer links to follow.</summary>
    public int MaxTrailerChainDepth { get; init; } = 100;

    /// <summary>Maximum Form XObject recursion depth.</summary>
    public int MaxFormXObjectDepth { get; init; } = 16;

    /// <summary>Maximum drawing commands per page display list.</summary>
    public int MaxCommandsPerPage { get; init; } = 500_000;
}
