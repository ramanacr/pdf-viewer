using System.Text.Json;

namespace PdfViewer.Web.Engine.Embed;

/// <summary>How the host page shows the viewer (the <c>embedMode</c> of the embed SDK).</summary>
public enum EmbedMode
{
    /// <summary>The viewer fills the host's element, with the full toolbar and the side panel.</summary>
    FullWindow,
    /// <summary>The viewer fills a sized element of the page: a compact toolbar, no side panel, an optional full-screen button.</summary>
    SizedContainer,
    /// <summary>All pages in the flow of the host page: the frame grows with the document and the host page scrolls.</summary>
    InLine,
    /// <summary>Over the host page, with a close (or return) button in a bar above the viewer.</summary>
    LightBox,
}

/// <summary>How pages are laid out and fitted when a document opens (<c>defaultViewMode</c>).</summary>
public enum EmbedViewMode { FitWidth, FitPage, TwoColumn, TwoColumnFitPage }

/// <summary>The SDK's <c>config</c> for one file, validated. Defaults follow the Adobe PDF Embed API where it has one.</summary>
public sealed record EmbedConfig
{
    public EmbedMode Mode { get; init; } = EmbedMode.FullWindow;
    public bool ShowDownload { get; init; } = true;
    public bool ShowPrint { get; init; } = true;

    /// <summary>Accepted for compatibility; there are no annotation tools yet (planned, phase 2).</summary>
    public bool ShowAnnotationTools { get; init; } = true;

    public bool ShowThumbnails { get; init; } = true;
    public bool ShowBookmarks { get; init; } = true;
    public bool ShowZoomControl { get; init; } = true;
    public bool ShowFullScreen { get; init; } = true;
    public EmbedViewMode ViewMode { get; init; } = EmbedViewMode.FitWidth;
    public bool FocusOnRendering { get; init; }

    /// <summary>The light box's exit button says "Return" instead of "Close".</summary>
    public bool ExitReturns { get; init; }

    public bool EnableSearchApis { get; init; }

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; init; } = "system";

    /// <summary>The viewer of the web demo: every control, no host page.</summary>
    public static EmbedConfig Demo { get; } = new();

    /// <summary>The modes that have a side panel (outline, thumbnails, search results).</summary>
    public bool HasSidePanel => Mode is EmbedMode.FullWindow or EmbedMode.LightBox;

    public bool TwoColumns => ViewMode is EmbedViewMode.TwoColumn or EmbedViewMode.TwoColumnFitPage;

    public bool FitPage => ViewMode is EmbedViewMode.FitPage or EmbedViewMode.TwoColumnFitPage;

    internal static readonly string[] ModeNames = ["FULL_WINDOW", "SIZED_CONTAINER", "IN_LINE", "LIGHT_BOX"];
    internal static readonly string[] ViewModeNames = ["FIT_WIDTH", "FIT_PAGE", "TWO_COLUMN", "TWO_COLUMN_FIT_PAGE"];

    /// <summary>The mode from its SDK name ("SIZED_CONTAINER"), or null.</summary>
    public static EmbedMode? ParseMode(string? name)
    {
        int i = Array.IndexOf(ModeNames, name);
        return i < 0 ? null : (EmbedMode)i;
    }

    public static string ModeName(EmbedMode mode) => ModeNames[(int)mode];

    /// <summary>
    /// Reads the SDK's config object. Unknown keys are ignored (a newer SDK may send more); a known
    /// key with a wrong type or value throws <see cref="EmbedProtocolException"/> that names it.
    /// <paramref name="frameMode"/> is the mode the frame was opened in (its URL), which wins:
    /// the host page lays the frame out for it.
    /// </summary>
    public static EmbedConfig FromJson(JsonElement config, EmbedMode frameMode)
    {
        if (config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) config = default;
        else if (config.ValueKind != JsonValueKind.Object) throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "config must be an object.");

        if (Text(config, "embedMode", ModeNames) is { } m && ParseMode(m) != frameMode)
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "config.embedMode differs from the frame's mode.");
        bool sized = frameMode is EmbedMode.SizedContainer or EmbedMode.InLine;
        var viewMode = Text(config, "defaultViewMode", ViewModeNames) is { } v ? (EmbedViewMode)Array.IndexOf(ViewModeNames, v) : EmbedViewMode.FitWidth;
        // In line, the host page scrolls: there is no view height to fit a page to.
        if (frameMode == EmbedMode.InLine) viewMode = viewMode is EmbedViewMode.TwoColumn or EmbedViewMode.TwoColumnFitPage ? EmbedViewMode.TwoColumn : EmbedViewMode.FitWidth;

        return new EmbedConfig
        {
            Mode = frameMode,
            ShowDownload = Flag(config, "showDownloadPDF", true),
            ShowPrint = Flag(config, "showPrintPDF", true),
            ShowAnnotationTools = Flag(config, "showAnnotationTools", true),
            ShowThumbnails = Flag(config, "showThumbnails", true),
            ShowBookmarks = Flag(config, "showBookmarks", true),
            ShowZoomControl = Flag(config, "showZoomControl", true),
            ShowFullScreen = Flag(config, "showFullScreen", true),
            ViewMode = viewMode,
            FocusOnRendering = Flag(config, "focusOnRendering", !sized),
            ExitReturns = Text(config, "exitPDFViewerType", ["CLOSE", "RETURN"]) == "RETURN",
            EnableSearchApis = Flag(config, "enableSearchAPIs", false),
            Theme = Text(config, "theme", ["system", "light", "dark"]) ?? "system",
        };
    }

    private static bool Flag(JsonElement config, string name, bool fallback)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"config.{name} must be true or false."),
        };
    }

    private static string? Text(JsonElement config, string name, string[] allowed)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        string? s = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (s == null || Array.IndexOf(allowed, s) < 0)
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"config.{name} must be one of {string.Join(", ", allowed)}.");
        return s;
    }
}
