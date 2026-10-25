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
    public const string NotAllowed = "NOT_ALLOWED";
    public const string Closed = "CLOSED";
    public const string ViewerError = "VIEWER_ERROR";
    public const string NetworkError = "NETWORK_ERROR";

    /// <summary>The code for a document that cannot be opened.</summary>
    public static string FromOpenFailure(ViewerOpenFailure failure) => failure switch
    {
        ViewerOpenFailure.PasswordRequired => PasswordRequired,
        ViewerOpenFailure.WrongPassword => WrongPassword,
        ViewerOpenFailure.CertificateRequired => CertificateRequired,
        ViewerOpenFailure.EncryptionNotSupportedHere => EncryptionNotSupported,
        ViewerOpenFailure.TooLarge => TooLarge,
        ViewerOpenFailure.NetworkError => NetworkError,
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
    /// <summary>The first page of a file is drawn (Adobe's APP_RENDERING_DONE); for a file read by ranges, also how much of it was read by then.</summary>
    public const string AppRenderingDone = "APP_RENDERING_DONE";

    // For the annotation manager's listeners (annotationManager.registerEventListener).
    public const string AnnotationAdded = "ANNOTATION_ADDED";
    public const string AnnotationUpdated = "ANNOTATION_UPDATED";
    public const string AnnotationDeleted = "ANNOTATION_DELETED";
    public const string AnnotationSelected = "ANNOTATION_SELECTED";
    public const string AnnotationUnselected = "ANNOTATION_UNSELECTED";
    public const string AnnotationClicked = "ANNOTATION_CLICKED";
}

/// <summary>A message the host page sent that is this protocol's but is not valid. The viewer answers it with the code.</summary>
public sealed class EmbedProtocolException(string code, string message, int requestId = 0) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The request to answer, 0 when the message had none.</summary>
    public int RequestId { get; } = requestId;
}

/// <summary>
/// What the frame's URL says (index.html?embed=1&amp;mode=SIZED_CONTAINER&amp;origin=https%3A%2F%2Fexample.com):
/// the mode the host page lays the frame out for, and the host's origin, null when it is missing or
/// not a valid http or https origin (the viewer then refuses to listen).
/// </summary>
public sealed record EmbedFrame(EmbedMode Mode, string? Origin)
{
    /// <summary>The frame's settings from a URL query ("?embed=1&amp;…"), or null when the page is not embedded.</summary>
    public static EmbedFrame? FromQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        string? embed = null, mode = null, origin = null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = eq < 0 ? part : part[..eq];
            string value;
            try
            {
                value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                continue;
            }
            switch (key)
            {
                case "embed": embed ??= value; break;
                case "mode": mode ??= value; break;
                case "origin": origin ??= value; break;
            }
        }
        if (embed == null) return null;
        return new EmbedFrame(EmbedConfig.ParseMode(mode) ?? EmbedMode.FullWindow,
            embed == EmbedProtocol.Version.ToString(CultureInfo.InvariantCulture) ? EmbedProtocol.ParseOrigin(origin) : null);
    }
}

/// <summary>A message from the host page.</summary>
public abstract record EmbedMessage(int RequestId);

/// <summary>
/// Show a file: its bytes come beside the message (a transferred ArrayBuffer). With
/// <see cref="Range"/>, they are only the part of the file the host page already has, and the
/// viewer asks for the rest by ranges (<see cref="EmbedProtocol.FetchRange"/>).
/// </summary>
public sealed record EmbedOpen(int RequestId, string FileName, string? FileId, bool ReadOnly, string Locale, EmbedConfig Config) : EmbedMessage(RequestId)
{
    /// <summary>The host page's SAVE_API callback, when it registered one.</summary>
    public EmbedSaveOptions Save { get; init; } = EmbedSaveOptions.None;

    /// <summary>The file is read by ranges: its length, and where the bytes that came with the message start.</summary>
    public EmbedRangeOpen? Range { get; init; }
}

/// <summary>A file read by ranges: its <see cref="Length"/>, and the offset of the bytes that came with the open message.</summary>
public sealed record EmbedRangeOpen(long Length, long InitialOffset);

/// <summary>
/// The host page's answer to a fetchRange request: the bytes come beside it (a transferred
/// ArrayBuffer) when <see cref="Ok"/>, else <see cref="ErrorCode"/> says why. <see cref="File"/> is
/// the open request of the file; <see cref="EmbedMessage.RequestId"/> the fetchRange request.
/// </summary>
public sealed record EmbedRangeReply(int RequestId, int File, bool Ok, string? ErrorCode, string? ErrorMessage) : EmbedMessage(RequestId);

/// <summary>The API methods a host page can call (<c>viewer.getAPIs()</c> and the search object).</summary>
public enum EmbedMethod
{
    GetPdfMetadata, GetCurrentPage, GotoLocation, GetSelectedContent,
    ZoomIn, ZoomOut, SetZoomLevel, GetZoomLimits,
    Search, SearchNext, SearchPrevious, SearchClear,
    GetAnnotationManager, AddAnnotations, GetAnnotations, UpdateAnnotation, DeleteAnnotations,
    SelectAnnotation, UnselectAnnotation, SetAnnotationConfig, ExportXfdf, ImportXfdf,
    SetSaveOptions, SaveResult,
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

    /// <summary>addAnnotations: the annotations; updateAnnotation: the one annotation.</summary>
    public IReadOnlyList<Annotations.ViewerAnnotation> Annotations { get; init; } = [];

    /// <summary>getAnnotations and deleteAnnotations.</summary>
    public Annotations.ViewerAnnotationFilter? Filter { get; init; }

    /// <summary>selectAnnotation.</summary>
    public string? AnnotationId { get; init; }

    /// <summary>setAnnotationConfig.</summary>
    public EmbedAnnotationConfig? AnnotationConfig { get; init; }

    /// <summary>importXfdf: the XFDF document.</summary>
    public string? Xfdf { get; init; }

    /// <summary>setSaveOptions.</summary>
    public EmbedSaveOptions? SaveOptions { get; init; }

    /// <summary>saveResult: the save it answers, whether the host page saved it, and the file name it gave.</summary>
    public int SaveId { get; init; }
    public bool Saved { get; init; }
    public string? SavedFileName { get; init; }
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

    /// <summary>Largest file accepted (512 MB), the same as the SDK script's limit.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>Most bytes in one fetchRange request (4 MB).</summary>
    public const int MaxRangeBytes = 4 * 1024 * 1024;

    /// <summary>Most fetchRange requests waiting for an answer at the same time; the host page refuses more.</summary>
    public const int MaxRangeRequestsInFlight = 6;

    /// <summary>What this viewer can do beyond protocol version 1's first messages, sent in its ready message.</summary>
    public static readonly IReadOnlyList<string> Features = ["range"];

    /// <summary>
    /// Longest message accepted, in characters of its JSON (2 MB). The file's bytes travel beside
    /// the message, so a real message is a few kilobytes; a longer one is refused before it is parsed.
    /// </summary>
    public const int MaxMessageLength = 2 * 1024 * 1024;

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
    /// message; ignore it). Throws <see cref="EmbedProtocolException"/> when it is ours but invalid,
    /// and when it is longer than <see cref="MaxMessageLength"/> (before it is parsed).
    /// <paramref name="frameMode"/> is the mode of the frame's URL.
    /// </summary>
    public static EmbedMessage? Parse(string json, EmbedMode frameMode)
    {
        if (json.Length > MaxMessageLength)
        {
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument,
                $"The message is longer than {MaxMessageLength / (1024 * 1024)} MB.", LeadingRequestId(json));
        }
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
                "range" => ParseRangeReply(root, Require(requestId)),
                _ => null,
            };
        }

        static int Require(int requestId) =>
            requestId > 0 ? requestId : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "The message has no requestId.");
    }

    /// <summary>
    /// The requestId of a message too long to parse, read from its first characters only (the SDK
    /// writes it near the start), so that the refusal can answer the request. 0 when it is not there.
    /// </summary>
    private static int LeadingRequestId(string json)
    {
        byte[] head = Encoding.UTF8.GetBytes(json, 0, Math.Min(json.Length, 512));
        var reader = new Utf8JsonReader(head, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { MaxDepth = 16 }));
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("requestId")) continue;
                return reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int id) && id > 0 ? id : 0;
            }
        }
        catch (JsonException)
        {
        }
        return 0;
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
            var save = EmbedAnnotationJson.ParseSaveOptions(root.TryGetProperty("save", out var sv) ? sv : default, requestId);
            return new EmbedOpen(requestId, fileName, fileId, readOnly, locale, config) { Save = save, Range = ParseRangeOpen(root, requestId) };
        }
        catch (EmbedProtocolException ex) when (ex.RequestId == 0)
        {
            throw new EmbedProtocolException(ex.Code, ex.Message, requestId);
        }
    }

    /// <summary>open's "range": { length, initialOffset }, or null when the file's bytes came whole.</summary>
    private static EmbedRangeOpen? ParseRangeOpen(JsonElement root, int requestId)
    {
        if (!root.TryGetProperty("range", out var range) || range.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (range.ValueKind != JsonValueKind.Object)
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "range must be an object { length, initialOffset }.", requestId);
        long length = WholeNumber(range, "length", 1, MaxFileBytes, requestId, "range.length");
        long initial = range.TryGetProperty("initialOffset", out _) ? WholeNumber(range, "initialOffset", 0, length, requestId, "range.initialOffset") : 0;
        return new EmbedRangeOpen(length, initial);
    }

    private static long WholeNumber(JsonElement parent, string name, long min, long max, int requestId, string what)
    {
        if (parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long x) && x >= min && x <= max)
            return x;
        throw new EmbedProtocolException(EmbedErrors.InvalidArgument,
            $"{what} must be a whole number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.", requestId);
    }

    /// <summary>The host page's answer to fetchRange: { requestId, file, ok, error? } (the bytes come beside it).</summary>
    private static EmbedRangeReply ParseRangeReply(JsonElement root, int requestId)
    {
        int file = (int)WholeNumber(root, "file", 1, int.MaxValue, requestId, "range reply: file");
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "range reply: ok must be true or false.", requestId);
        if (ok.ValueKind == JsonValueKind.True) return new EmbedRangeReply(requestId, file, true, null, null);
        string code = EmbedErrors.NetworkError, message = "The host page could not fetch the range.";
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 and <= 64 } cs && cs.All(ch => ch is >= 'A' and <= 'Z' or '_'))
                code = cs;
            if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } ms)
                message = ms.Length > 500 ? ms[..500] : ms;
        }
        return new EmbedRangeReply(requestId, file, false, code, message);
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
        ["getAnnotationManager"] = EmbedMethod.GetAnnotationManager,
        ["addAnnotations"] = EmbedMethod.AddAnnotations,
        ["getAnnotations"] = EmbedMethod.GetAnnotations,
        ["updateAnnotation"] = EmbedMethod.UpdateAnnotation,
        ["deleteAnnotations"] = EmbedMethod.DeleteAnnotations,
        ["selectAnnotation"] = EmbedMethod.SelectAnnotation,
        ["unselectAnnotation"] = EmbedMethod.UnselectAnnotation,
        ["setAnnotationConfig"] = EmbedMethod.SetAnnotationConfig,
        ["exportXfdf"] = EmbedMethod.ExportXfdf,
        ["importXfdf"] = EmbedMethod.ImportXfdf,
        ["setSaveOptions"] = EmbedMethod.SetSaveOptions,
        ["saveResult"] = EmbedMethod.SaveResult,
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
            EmbedMethod.AddAnnotations => call with { Annotations = EmbedAnnotationJson.ParseList(Arg(0), requestId, "", default) },
            EmbedMethod.UpdateAnnotation => call with { Annotations = [EmbedAnnotationJson.Parse(Arg(0), requestId, "", default, requireId: true)] },
            EmbedMethod.GetAnnotations => call with { Filter = EmbedAnnotationJson.ParseFilter(Arg(0), requestId, name) },
            EmbedMethod.DeleteAnnotations => Arg(0).ValueKind == JsonValueKind.Object
                ? call with { Filter = EmbedAnnotationJson.ParseFilter(Arg(0), requestId, name) }
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "deleteAnnotations: give a filter ({ annotationIds, pageRange }; {} deletes every annotation).", requestId),
            EmbedMethod.SelectAnnotation => call with { AnnotationId = Id(Arg(0)) },
            EmbedMethod.SetAnnotationConfig => call with { AnnotationConfig = EmbedAnnotationJson.ParseConfig(Arg(0), requestId) },
            EmbedMethod.ImportXfdf => call with { Xfdf = Xfdf(Arg(0)) },
            EmbedMethod.SetSaveOptions => Arg(0).ValueKind == JsonValueKind.Object
                ? call with { SaveOptions = EmbedAnnotationJson.ParseSaveOptions(Arg(0), requestId) }
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "setSaveOptions: give an object.", requestId),
            EmbedMethod.SaveResult => call with
            {
                SaveId = Integer(Arg(0), "saveId", 1, int.MaxValue),
                Saved = SaveCode(Arg(1)),
                SavedFileName = SaveName(Arg(2)),
            },
            _ => call,
        };

        string Id(JsonElement e) =>
            e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= Annotations.ViewerAnnotationStore.MaxIdLength } s
                ? s
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, $"{name}: the annotation id must be a string of 1 to {Annotations.ViewerAnnotationStore.MaxIdLength} characters.", requestId);

        string Xfdf(JsonElement e) =>
            e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s
                ? s
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "importXfdf: give the XFDF document as a string.", requestId);

        bool SaveCode(JsonElement e) =>
            e.ValueKind == JsonValueKind.String && e.GetString() is "SUCCESS" or "FAIL"
                ? e.GetString() == "SUCCESS"
                : throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "saveResult: the code must be SUCCESS or FAIL.", requestId);

        string? SaveName(JsonElement e)
        {
            if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 1024 } fn && !fn.Any(char.IsControl)) return fn;
            throw new EmbedProtocolException(EmbedErrors.InvalidArgument, "saveResult: metaData.fileName must be a string of 1 to 1024 characters.", requestId);
        }

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

    /// <summary>The viewer has started and listens; <see cref="Features"/> tell the SDK script what it may send (range loading).</summary>
    public static string Ready() => Write("ready", w =>
    {
        w.WriteStartArray("features");
        foreach (var f in Features) w.WriteStringValue(f);
        w.WriteEndArray();
    });

    /// <summary>
    /// Asks the host page for bytes [<paramref name="start"/>, <paramref name="end"/>) of the file of
    /// open request <paramref name="file"/>; the host fetches them from the URL (or the
    /// linearizationInfo) it was given, never from another, and answers with a "range" message.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for a range that is empty, longer than
    /// <see cref="MaxRangeBytes"/> or not inside a file of <paramref name="length"/> bytes.
    /// </summary>
    public static string FetchRange(int file, int requestId, long start, long end, long length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(file, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestId, 1);
        if (length < 1 || length > MaxFileBytes) throw new ArgumentOutOfRangeException(nameof(length));
        if (start < 0 || end <= start || end > length || end - start > MaxRangeBytes) throw new ArgumentOutOfRangeException(nameof(end), $"The range [{start}, {end}) is not a valid range of the file.");
        return Write("fetchRange", w =>
        {
            w.WriteNumber("file", file);
            w.WriteNumber("requestId", requestId);
            w.WriteNumber("start", start);
            w.WriteNumber("end", end);
        });
    }

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

    /// <summary>
    /// The reader saved: the host page's SAVE_API callback gets the file, whose bytes travel beside
    /// this message as a transferred ArrayBuffer (<c>data</c>). The host answers with saveResult.
    /// </summary>
    public static string SaveRequest(int saveId, string fileName, string? fileId, string reason) => Write("save", w =>
    {
        w.WriteNumber("saveId", saveId);
        w.WriteString("fileName", fileName);
        if (fileId == null) w.WriteNull("fileId");
        else w.WriteString("fileId", fileId);
        w.WriteString("reason", reason);
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
        w.WriteBoolean("isSigned", m.IsSigned);
        if (m.CertificationLevel is int level) w.WriteNumber("certificationLevel", level);
        else w.WriteNull("certificationLevel");
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
