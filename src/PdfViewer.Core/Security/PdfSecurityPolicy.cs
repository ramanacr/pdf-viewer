using PdfEngine.Exceptions;

namespace PdfViewer.Core.Security;

/// <summary>
/// A document-originated action that a malicious PDF might try to trigger.
/// </summary>
public enum PdfDocumentAction
{
    /// <summary>Embedded JavaScript (document-level, field or annotation triggered).</summary>
    JavaScript,

    /// <summary>A URI action opening an external address in a browser.</summary>
    ExternalLink,

    /// <summary>A /Launch action starting an external program or file.</summary>
    LaunchProgram,

    /// <summary>Any action that would cause the document to reach the network.</summary>
    NetworkAccess,

    /// <summary>Extracting an embedded file attachment to disk.</summary>
    AttachmentExtraction
}

/// <summary>
/// Configurable security policy defining allowed actions and document boundaries.
/// Enforced via the Ensure* guards below - see each member for where it is applied.
/// </summary>
public record PdfSecurityPolicy
{
    /// <summary>
    /// Blocks embedded JavaScript. Enforced by <see cref="EnsureActionAllowed"/>.
    /// Note the viewer additionally never creates a PDFium form-fill environment, which is
    /// the only thing that can execute document JavaScript, so this is defence in depth.
    /// </summary>
    public bool AllowJavaScript { get; init; } = false;

    /// <summary>Allows URI actions to be followed. Enforced by <see cref="EnsureActionAllowed"/>.</summary>
    public bool AllowExternalLinks { get; init; } = true;

    /// <summary>Blocks /Launch actions. Enforced by <see cref="EnsureActionAllowed"/>.</summary>
    public bool AllowLaunchActions { get; init; } = false;

    /// <summary>Requires confirmation before extracting attachments. Consulted by <see cref="IsConfirmationRequired"/>.</summary>
    public bool ConfirmAttachmentExtraction { get; init; } = true;

    /// <summary>Blocks document-initiated network access. Enforced by <see cref="EnsureActionAllowed"/>.</summary>
    public bool AllowNetworkAccess { get; init; } = false;

    /// <summary>
    /// Upper bound on the size of a document that may be opened.
    /// Enforced by <see cref="EnsureDocumentSizeAllowed"/> at every document-open boundary.
    /// </summary>
    public long MaxDocumentSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024; // 2 GB ceiling

    /// <summary>
    /// Upper bound on either dimension of a single rasterized page.
    /// Enforced by <see cref="EnsureRenderDimensionsAllowed"/> at every render boundary,
    /// bounding both memory use and decode time for hostile page geometry.
    /// </summary>
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

    /// <summary>
    /// Throws <see cref="PdfSecurityPolicyException"/> if the document exceeds the size ceiling.
    /// </summary>
    public void EnsureDocumentSizeAllowed(long sizeBytes, string? filePath = null)
    {
        if (sizeBytes > MaxDocumentSizeBytes)
        {
            string where = string.IsNullOrEmpty(filePath) ? "The document" : $"'{Path.GetFileName(filePath)}'";
            throw new PdfSecurityPolicyException(
                nameof(MaxDocumentSizeBytes),
                $"{where} is {sizeBytes:N0} bytes, exceeding the {MaxDocumentSizeBytes:N0} byte security limit.");
        }
    }

    /// <summary>
    /// Throws <see cref="PdfSecurityPolicyException"/> if a requested raster exceeds the
    /// per-dimension ceiling.
    /// </summary>
    public void EnsureRenderDimensionsAllowed(int widthPixels, int heightPixels)
    {
        if (widthPixels > MaxRenderDimensionPixels || heightPixels > MaxRenderDimensionPixels)
        {
            throw new PdfSecurityPolicyException(
                nameof(MaxRenderDimensionPixels),
                $"Requested render of {widthPixels}x{heightPixels} px exceeds the " +
                $"{MaxRenderDimensionPixels} px per-dimension security limit.");
        }
    }

    /// <summary>
    /// Throws <see cref="PdfSecurityPolicyException"/> if the document-originated action is
    /// not permitted. Call this before acting on anything the DOCUMENT asked for - following
    /// a link, running script, launching a program - never for app-initiated navigation.
    /// </summary>
    public void EnsureActionAllowed(PdfDocumentAction action)
    {
        bool allowed = action switch
        {
            PdfDocumentAction.JavaScript => AllowJavaScript,
            PdfDocumentAction.ExternalLink => AllowExternalLinks,
            PdfDocumentAction.LaunchProgram => AllowLaunchActions,
            PdfDocumentAction.NetworkAccess => AllowNetworkAccess,
            PdfDocumentAction.AttachmentExtraction => true, // gated by confirmation, not blocked
            _ => false
        };

        if (!allowed)
        {
            throw new PdfSecurityPolicyException(
                action.ToString(),
                $"The document requested a {action} action, which is blocked by the active security policy.");
        }
    }

    /// <summary>
    /// True when the action requires explicit user confirmation before proceeding.
    /// </summary>
    public bool IsConfirmationRequired(PdfDocumentAction action) =>
        action == PdfDocumentAction.AttachmentExtraction && ConfirmAttachmentExtraction;
}
