using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PdfViewer.Web.Engine.Embed;

/// <summary>The error codes of the embed SDK (<c>PdfViewer.View.Enum.ErrorCode</c>).</summary>
public static class EmbedErrors
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string NotAPdf = "NOT_A_PDF";
    public const string TooLarge = "TOO_LARGE";
    public const string PasswordRequired = "PASSWORD_REQUIRED";
    public const string WrongPassword = "WRONG_PASSWORD";
    public const string CertificateRequired = "CERTIFICATE_REQUIRED";
    public const string EncryptionNotSupported = "ENCRYPTION_NOT_SUPPORTED";
    public const string NoDocument = "NO_DOCUMENT";
    public const string NotEnabled = "NOT_ENABLED";
    public const string SearchEnded = "SEARCH_ENDED";

    /// <summary>The code for a document that cannot be opened.</summary>
    public static string FromOpenFailure(ViewerOpenFailure failure) => failure switch
    {
        ViewerOpenFailure.PasswordRequired => PasswordRequired,
        ViewerOpenFailure.WrongPassword => WrongPassword,
        ViewerOpenFailure.CertificateRequired => CertificateRequired,
        ViewerOpenFailure.EncryptionNotSupportedHere => EncryptionNotSupported,
        ViewerOpenFailure.TooLarge => TooLarge,
        _ => NotAPdf,
    };
}

/// <summary>The event types the viewer sends to the host page's callbacks.</summary>
public static class EmbedEvents
{
    public const string DocumentOpen = "DOCUMENT_OPEN";
    public const string PageView = "PAGE_VIEW";
    public const string TextCopy = "TEXT_COPY";
    public const string TextSearch = "TEXT_SEARCH";
    public const string ZoomChange = "ZOOM_CHANGE";
    public const string DocumentDownload = "DOCUMENT_DOWNLOAD";
    public const string DocumentPrint = "DOCUMENT_PRINT";
}

/// <summary>A message the host page sent that is this protocol's but is not valid. The viewer answers it with the code.</summary>
public sealed class EmbedProtocolException(string code, string message, int requestId = 0) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The request to answer, 0 when the message had none.</summary>
    public int RequestId { get; } = requestId;
}

/// <summary>A message from the host page.</summary>
public abstract record EmbedMessage(int RequestId);

/// <summary>Show a file: its bytes come beside the message (a transferred ArrayBuffer).</summary>
public sealed record EmbedOpen(int RequestId, string FileName, string? FileId, bool ReadOnly, string Locale, EmbedConfig Config) : EmbedMessage(RequestId);

/// <summary>The API methods a host page can call (<c>viewer.getAPIs()</c> and the search object).</summary>
public enum EmbedMethod
{
    GetPdfMetadata, GetCurrentPage, GotoLocation, GetSelectedContent,
    ZoomIn, ZoomOut, SetZoomLevel, GetZoomLimits,
    Search, SearchNext, SearchPrevious, SearchClear,
}

/// <summary>
/// An API call, its arguments checked: <see cref="Page"/>, <see cref="X"/> and <see cref="Y"/> for
/// gotoLocation, <see cref="Level"/> for setZoomLevel, <see cref="Term"/> for search,
/// <see cref="SearchId"/> for the search object's next, previous and clear.
/// </summary>
public sealed record EmbedCall(int RequestId, int Document, EmbedMethod Method) : EmbedMessage(RequestId)
{
    public int Page { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Level { get; init; }
    public string? Term { get; init; }
    public int SearchId { get; init; }
}

/// <summary>
/// The postMessage protocol between the SDK script in the host page (pdfviewer-embed.js) and the
/// viewer in its iframe. Every message is an object { protocol: "pdfviewer-embed", version: 1,
/// type, ... }. The viewer's script checks that a message comes from its parent window and from
/// the origin the frame's URL names, then hands it here as JSON; this class decides whether it is
/// valid. Messages of another protocol or version are ignored; ours but malformed are refused with
/// a reason. The replies are built here too, so that both directions are tested in one place.
/// </summary>
public static class EmbedProtocol
{
    public const string Name = "pdfviewer-embed";
    public const int Version = 1;

    /// <summary>Longest search term accepted.</summary>
    public const int MaxTermLength = 1000;

    /// <summary>
    /// The host page's origin from the frame's URL ("https://example.com:8443"), normalized as a
    /// browser serializes it, or null when it is not an http or https origin with nothing after
    /// the host and port. The viewer accepts messages only from that origin and posts only to it.
    /// </summary>
    public static string? ParseOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin.Length > 512) return null;
        // "https://example.com/" is accepted; a path, query, fragment or user name after the host is not.
        string s = origin.EndsWith('/') ? origin[..^1] : origin;
        int start = s.IndexOf("://", StringComparison.Ordinal);
        if (start < 0 || s.IndexOfAny(['/', '?', '#', '@', '\\'], start + 3) >= 0) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Reads a message from the host page. Null when it is not this protocol's (another script's
    /// message; ignore it). Throws <see cref="EmbedProtocolException"/> when it is ours but invalid.
    /// <paramref name="frameMode"/> is the mode of the frame's URL.
    /// </summary>
    public static EmbedMessage? Parse(string json, EmbedMode frameMode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String || protocol.GetString() != Name) return null;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != Version) return null;
            string? type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            int requestId = root.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out int id) && id > 0 ? id : 0;
            return type switch
            {
                "open" => ParseOpen(root, Require(requestId), frameMode),
                "call" => ParseCall(root, Require(requestId)),
                _ => null,
            };
        }

        static int Require(int requestId) =>
            requestId > 0 ? requestId : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "The message has no requestId.");
    }

    private static EmbedOpen ParseOpen(JsonElement root, int requestId, EmbedMode frameMode)
    {
        try
        {
            string fileName = OptionalString(root, "fileName", 1024) is { Length: > 0 } f ? f : "document.pdf";
            string? fileId = OptionalString(root, "fileId", 1024);
            bool readOnly = root.TryGetProperty("readOnly", out var ro) && ro.ValueKind == JsonValueKind.True;
            string locale = OptionalString(root, "locale", 64) ?? "en-US";
            var config = EmbedConfig.FromJson(root.TryGetProperty("config", out var c) ? c : default, frameMode);
            return new EmbedOpen(requestId, fileName, fileId, readOnly, locale, config);
        }
        catch (EmbedProtocolException ex) when (ex.RequestId == 0)
        {
            throw new EmbedProtocolException(ex.Code, ex.Message, requestId);
        }
    }

    private static readonly Dictionary<string, EmbedMethod> Methods = new(StringComparer.Ordinal)
    {
        ["getPDFMetadata"] = EmbedMethod.GetPdfMetadata,
        ["getCurrentPage"] = EmbedMethod.GetCurrentPage,
        ["gotoLocation"] = EmbedMethod.GotoLocation,
        ["getSelectedContent"] = EmbedMethod.GetSelectedContent,
        ["zoomIn"] = EmbedMethod.ZoomIn,
        ["zoomOut"] = EmbedMethod.ZoomOut,
        ["setZoomLevel"] = EmbedMethod.SetZoomLevel,
        ["getZoomLimits"] = EmbedMethod.GetZoomLimits,
        ["search"] = EmbedMethod.Search,
        ["searchNext"] = EmbedMethod.SearchNext,
        ["searchPrevious"] = EmbedMethod.SearchPrevious,
        ["searchClear"] = EmbedMethod.SearchClear,
    };

    private static EmbedCall ParseCall(JsonElement root, int requestId)
    {
        string? name = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (name == null || !Methods.TryGetValue(name, out var method))
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "Unknown method.", requestId);
        int document = root.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int doc) ? doc : 0;
        var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a : default;
        int count = args.ValueKind == JsonValueKind.Array ? args.GetArrayLength() : 0;
        JsonElement Arg(int i) => i < count ? args[i] : default;

        var call = new EmbedCall(requestId, document, method);
        return method switch
        {
            EmbedMethod.GotoLocation => call with
            {
                Page = Integer(Arg(0), "pageNumber", 1, int.MaxValue),
                X = Number(Arg(1), "x", 0, 1e6, fallback: 0),
                Y = Number(Arg(2), "y", 0, 1e6, fallback: 0),
            },
            EmbedMethod.SetZoomLevel => call with { Level = Number(Arg(0), "zoomLevel", 1e-6, 1e6, fallback: null) },
            EmbedMethod.Search => call with { Term = Term(Arg(0)) },
            EmbedMethod.SearchNext or EmbedMethod.SearchPrevious or EmbedMethod.SearchClear => call with { SearchId = Integer(Arg(0), "searchId", 1, int.MaxValue) },
            _ => call,
        };

        int Integer(JsonElement e, string what, int min, int max) =>
            e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double x) && x == Math.Floor(x) && x >= min && x <= max
                ? (int)x
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"{name}: {what} must be a whole number from {min}.", requestId);

        double Number(JsonElement e, string what, double min, double max, double? fallback)
        {
            if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null && fallback is double f) return f;
            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double x) && double.IsFinite(x) && x >= min && x <= max
                ? x
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"{name}: {what} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.", requestId);
        }

        string Term(JsonElement e)
        {
            string? s = e.ValueKind == JsonValueKind.String ? e.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(s) || s.Length > MaxTermLength)
                throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"search: the term must be a string of 1 to {MaxTermLength} characters.", requestId);
            return s;
        }
    }

    private static string? OptionalString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        string? s = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (s == null || s.Length > maxLength) throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"{name} must be a string of at most {maxLength} characters.");
        return s;
    }

    // ------------------------------------------------------------------ replies

    /// <summary>The viewer has started and listens.</summary>
    public static string Ready() => Write("ready", null);

    /// <summary>A request succeeded; <paramref name="value"/> writes its result (one JSON value), or there is none (null).</summary>
    public static string Ok(int requestId, Action<Utf8JsonWriter>? value) => Write("response", w =>
    {
        w.WriteNumber("requestId", requestId);
        w.WriteBoolean("ok", true);
        w.WritePropertyName("value");
        if (value == null) w.WriteNullValue();
        else value(w);
    });

    /// <summary>A request failed with an SDK error code.</summary>
    public static string Error(int requestId, string code, string message) => Write("response", w =>
    {
        w.WriteNumber("requestId", requestId);
        w.WriteBoolean("ok", false);
        w.WriteStartObject("error");
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    /// <summary>An event for the host page's callbacks: <paramref name="data"/> writes the properties of its data object.</summary>
    public static string Event(string type, Action<Utf8JsonWriter>? data) => Write("event", w =>
    {
        w.WriteStartObject("event");
        w.WriteString("type", type);
        w.WriteStartObject("data");
        data?.Invoke(w);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    /// <summary>
    /// Progress of a search started by the API: the current result (page and 1-based index among
    /// all results; page 0 when there is none yet), the results so far, and whether every page is read.
    /// </summary>
    public static string SearchUpdate(int searchId, int currentPage, int currentIndex, int total, bool completed) => Write("search", w =>
    {
        w.WriteNumber("searchId", searchId);
        w.WriteStartObject("result");
        w.WriteStartObject("currentResult");
        w.WriteNumber("pageNumber", currentPage);
        w.WriteNumber("index", currentIndex);
        w.WriteEndObject();
        w.WriteNumber("totalResults", total);
        w.WriteString("status", completed ? "COMPLETED" : "IN_PROGRESS");
        w.WriteEndObject();
    });

    /// <summary>In line: the height of the viewer's content, for the host to size the frame.</summary>
    public static string Size(double height) => Write("size", w => w.WriteNumber("height", Math.Round(height, 1)));

    /// <summary>In line: scroll the host page to this offset from the frame's top (CSS pixels).</summary>
    public static string Scroll(double top) => Write("scroll", w => w.WriteNumber("top", Math.Round(top, 1)));

    /// <summary>Light box: the reader asked to close the viewer (Escape).</summary>
    public static string Close() => Write("close", null);

    /// <summary>The result of getPDFMetadata.</summary>
    public static Action<Utf8JsonWriter> Metadata(ViewerMetadata m, string fileName, string? fileId) => w =>
    {
        w.WriteStartObject();
        w.WriteNumber("numPages", m.PageCount);
        w.WriteString("pdfTitle", m.Title);
        w.WriteString("author", m.Author);
        w.WriteString("subject", m.Subject);
        w.WriteString("keywords", m.Keywords);
        w.WriteString("creator", m.Creator);
        w.WriteString("producer", m.Producer);
        WriteDate(w, "creationDate", m.CreationDate);
        WriteDate(w, "modDate", m.ModificationDate);
        w.WriteString("pdfVersion", m.PdfVersion);
        w.WriteBoolean("isEncrypted", m.IsEncrypted);
        w.WriteBoolean("isTagged", m.IsTagged);
        w.WriteBoolean("canCopy", m.CanCopy);
        w.WriteBoolean("canPrint", m.CanPrint);
        w.WriteBoolean("wasRepaired", m.WasRepaired);
        w.WriteNumber("fileSize", m.FileSizeBytes);
        w.WriteString("fileName", fileName);
        if (fileId == null) w.WriteNull("id");
        else w.WriteString("id", fileId);
        w.WriteEndObject();
    };

    private static void WriteDate(Utf8JsonWriter w, string name, DateTime? date)
    {
        if (date is DateTime d) w.WriteString(name, d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        else w.WriteNull(name);
    }

    private static string Write(string type, Action<Utf8JsonWriter>? body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("protocol", Name);
            w.WriteNumber("version", Version);
            w.WriteString("type", type);
            body?.Invoke(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
