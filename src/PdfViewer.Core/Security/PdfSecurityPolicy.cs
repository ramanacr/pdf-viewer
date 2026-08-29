namespace PdfViewer.Core.Security;

/// <summary>
/// Configurable security policy defining allowed actions and document boundaries.
/// </summary>
public record PdfSecurityPolicy
{
    public bool AllowJavaScript { get; init; } = false;
    public bool AllowExternalLinks { get; init; } = true;
    public bool AllowLaunchActions { get; init; } = false;
    public bool ConfirmAttachmentExtraction { get; init; } = true;
    public bool AllowNetworkAccess { get; init; } = false;
    public long MaxDocumentSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024; // 2 GB ceiling
    public int MaxRenderDimensionPixels { get; init; } = 8192;

    public static readonly PdfSecurityPolicy DefaultStrict = new();
    public static readonly PdfSecurityPolicy Permissive = new()
    {
        AllowJavaScript = true,
        AllowExternalLinks = true,
        AllowLaunchActions = true,
        ConfirmAttachmentExtraction = false,
        AllowNetworkAccess = true
    };
}
