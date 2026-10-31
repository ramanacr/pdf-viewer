using System.Text.Json;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Demo;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The engine side of the web embed SDK (src/PdfViewer.Web.Engine/Embed): the postMessage
/// protocol's validation of what a host page sends, the replies, the config, and the document
/// facts the APIs need (metadata, mouse selection).
/// </summary>
public class EmbedSdkTests
{
    private const string Envelope = "\"protocol\":\"pdfviewer-embed\",\"version\":1";

    private static EmbedMessage? Parse(string body, EmbedMode mode = EmbedMode.FullWindow) =>
        EmbedProtocol.Parse("{" + Envelope + "," + body + "}", mode);

    private static EmbedCall Call(string method, string args = "[]") =>
        Assert.IsType<EmbedCall>(Parse($"\"type\":\"call\",\"requestId\":7,\"document\":3,\"method\":\"{method}\",\"args\":{args}"));

    private static EmbedProtocolException Refused(string body, EmbedMode mode = EmbedMode.FullWindow) =>
        Assert.Throws<EmbedProtocolException>(() => Parse(body, mode));

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://Example.COM:8443", "https://example.com:8443")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("https://example.com:443", "https://example.com")]
    public void Origin_IsNormalizedAsABrowserSerializesIt(string given, string expected) =>
        Assert.Equal(expected, EmbedProtocol.ParseOrigin(given));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("file:///C:/page.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.com/page")]
    [InlineData("https://example.com?x=1")]
    [InlineData("https://example.com#top")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com\\@evil.test")]
    [InlineData("example.com")]
    public void Origin_RefusesAnythingButAnHttpOrigin(string? given) => Assert.Null(EmbedProtocol.ParseOrigin(given));

    [Fact]
    public void FrameUrl_GivesTheModeAndTheHostOrigin()
    {
        Assert.Null(EmbedFrame.FromQuery(null));
        Assert.Null(EmbedFrame.FromQuery(""));
        Assert.Null(EmbedFrame.FromQuery("?mode=IN_LINE"));
        Assert.Equal(new EmbedFrame(EmbedMode.SizedContainer, "https://example.com"),
            EmbedFrame.FromQuery("?embed=1&mode=SIZED_CONTAINER&origin=https%3A%2F%2Fexample.com"));
        Assert.Equal(new EmbedFrame(EmbedMode.FullWindow, "http://127.0.0.1:8081"), EmbedFrame.FromQuery("embed=1&origin=http://127.0.0.1:8081"));
        // The first value counts, so an appended parameter cannot replace the origin.
        Assert.Equal("https://a.test", EmbedFrame.FromQuery("?embed=1&origin=https%3A%2F%2Fa.test&origin=https%3A%2F%2Fb.test")!.Origin);
        // Embedded, but nothing to listen to: no origin, a bad one, or another protocol version.
        Assert.Null(EmbedFrame.FromQuery("?embed=1&mode=LIGHT_BOX")!.Origin);
        Assert.Equal(EmbedMode.LightBox, EmbedFrame.FromQuery("?embed=1&mode=LIGHT_BOX")!.Mode);
        Assert.Null(EmbedFrame.FromQuery("?embed=1&origin=null")!.Origin);
        Assert.Null(EmbedFrame.FromQuery("?embed=1&origin=%")!.Origin);
        Assert.Null(EmbedFrame.FromQuery("?embed=2&origin=https%3A%2F%2Fexample.com")!.Origin);
        Assert.Equal(EmbedMode.FullWindow, EmbedFrame.FromQuery("?embed=1&mode=POPUP")!.Mode);
    }

    [Fact]
    public void FrameUrl_GivesTheFontBase_OnlyAsAPlainPathOnTheViewersOrigin()
    {
        // The SDK's fontBaseUrl arrives as an absolute path (pdfviewer-embed.js, fontBasePath).
        Assert.Null(EmbedFrame.FromQuery("?embed=1&origin=https%3A%2F%2Fa.test")!.FontBase);
        Assert.Equal("/static/pdf-fonts/", EmbedFrame.FromQuery("?embed=1&origin=https%3A%2F%2Fa.test&fonts=%2Fstatic%2Fpdf-fonts%2F")!.FontBase);
        Assert.Equal("/a/", EmbedFrame.FromQuery("?embed=1&fonts=/a/&fonts=/b/")!.FontBase);
        Assert.Null(EmbedFrame.FromQuery("?embed=1&fonts=https%3A%2F%2Ffonts.googleapis.com%2F")!.FontBase);
        Assert.Null(EmbedFrame.ParseFontBase(null));
        Assert.Null(EmbedFrame.ParseFontBase("/" + new string('a', 600) + "/"));
    }

    /// <summary>
    /// The shared case list (tests/sdk/font-base-cases.json), which tests/sdk/font-base.test.mjs runs
    /// against the viewer's JavaScript rule (font-base.js), so the C# and JavaScript checks agree.
    /// </summary>
    [Fact]
    public void FontBase_FollowsTheSharedCaseList()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var cases = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir!.FullName, "tests", "sdk", "font-base-cases.json")));
        var valid = cases.RootElement.GetProperty("valid").EnumerateArray().Select(e => e.GetString()!).ToList();
        var invalid = cases.RootElement.GetProperty("invalid").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(valid);
        Assert.True(invalid.Count > 20);
        foreach (var value in valid) Assert.Equal(value, EmbedFrame.ParseFontBase(value));
        foreach (var value in invalid) Assert.Null(EmbedFrame.ParseFontBase(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"open\",\"requestId\":1}")]
    [InlineData("{\"protocol\":\"other\",\"version\":1,\"type\":\"open\",\"requestId\":1}")]
    [InlineData("{\"protocol\":\"pdfviewer-embed\",\"version\":2,\"type\":\"open\",\"requestId\":1}")]
    [InlineData("{\"protocol\":\"pdfviewer-embed\",\"version\":\"1\",\"type\":\"open\",\"requestId\":1}")]
    [InlineData("{\"protocol\":\"pdfviewer-embed\",\"version\":1,\"type\":\"unknown\",\"requestId\":1}")]
    [InlineData("{\"protocol\":\"pdfviewer-embed\",\"version\":1}")]
    public void ForeignOrUnknownMessages_AreIgnored(string json) => Assert.Null(EmbedProtocol.Parse(json, EmbedMode.FullWindow));

    [Fact]
    public void DeeplyNestedJson_IsIgnored()
    {
        string json = "{" + Envelope + ",\"type\":\"open\",\"requestId\":1,\"config\":" + new string('[', 40) + new string(']', 40) + "}";
        Assert.Null(EmbedProtocol.Parse(json, EmbedMode.FullWindow));
    }

    [Fact]
    public void Open_ReadsTheFileAndTheConfig()
    {
        var open = Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":4,\"fileName\":\"Report.pdf\",\"fileId\":\"r-1\",\"readOnly\":true,\"locale\":\"de-DE\"," +
            "\"config\":{\"embedMode\":\"SIZED_CONTAINER\",\"showDownloadPDF\":false,\"showPrintPDF\":false,\"showZoomControl\":false,\"defaultViewMode\":\"FIT_PAGE\"," +
            "\"enableSearchAPIs\":true,\"theme\":\"dark\",\"newerOption\":{\"x\":1}}", EmbedMode.SizedContainer));
        Assert.Equal((4, "Report.pdf", "r-1", true, "de-DE"), (open.RequestId, open.FileName, open.FileId, open.ReadOnly, open.Locale));
        var c = open.Config;
        Assert.Equal(EmbedMode.SizedContainer, c.Mode);
        Assert.False(c.ShowDownload);
        Assert.False(c.ShowPrint);
        Assert.False(c.ShowZoomControl);
        Assert.True(c.ShowThumbnails);
        Assert.Equal(EmbedViewMode.FitPage, c.ViewMode);
        Assert.True(c.FitPage);
        Assert.False(c.TwoColumns);
        Assert.True(c.EnableSearchApis);
        Assert.False(c.FocusOnRendering); // a sized container does not take the focus unless asked
        Assert.False(c.HasSidePanel);
        Assert.Equal("dark", c.Theme);
    }

    [Fact]
    public void Open_DefaultsFollowTheMode()
    {
        var full = Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":1")).Config;
        Assert.Equal(EmbedMode.FullWindow, full.Mode);
        Assert.True(full.FocusOnRendering);
        Assert.True(full.HasSidePanel);
        Assert.True(full.ShowDownload && full.ShowPrint && full.ShowBookmarks && full.ShowThumbnails);
        Assert.False(full.EnableSearchApis);
        Assert.Equal(EmbedViewMode.FitWidth, full.ViewMode);
        Assert.Equal("system", full.Theme);
        Assert.Equal("document.pdf", Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":1,\"fileName\":\"\"")).FileName);

        // In line, the host page scrolls: fit page falls back to fit width, two columns stay.
        var inline = Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":1,\"config\":{\"defaultViewMode\":\"FIT_PAGE\"}", EmbedMode.InLine)).Config;
        Assert.Equal(EmbedViewMode.FitWidth, inline.ViewMode);
        var inlineTwo = Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":1,\"config\":{\"defaultViewMode\":\"TWO_COLUMN_FIT_PAGE\"}", EmbedMode.InLine)).Config;
        Assert.Equal(EmbedViewMode.TwoColumn, inlineTwo.ViewMode);

        var box = Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":1,\"config\":{\"exitPDFViewerType\":\"RETURN\"}", EmbedMode.LightBox)).Config;
        Assert.True(box.ExitReturns);
        Assert.True(box.HasSidePanel);
    }

    [Theory]
    [InlineData("{\"showDownloadPDF\":\"no\"}", "showDownloadPDF")]
    [InlineData("{\"showPrintPDF\":1}", "showPrintPDF")]
    [InlineData("{\"defaultViewMode\":\"CONTINUOUS\"}", "defaultViewMode")]
    [InlineData("{\"theme\":\"blue\"}", "theme")]
    [InlineData("{\"exitPDFViewerType\":\"EXIT\"}", "exitPDFViewerType")]
    [InlineData("{\"embedMode\":\"LIGHT_BOX\"}", "embedMode")]
    [InlineData("[]", "config")]
    public void Open_RefusesAWrongConfigValue_NamingIt(string config, string field)
    {
        var ex = Refused($"\"type\":\"open\",\"requestId\":9,\"config\":{config}");
        Assert.Equal(EmbedErrors.InvalidArgument, ex.Code);
        Assert.Equal(9, ex.RequestId);
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Requests_NeedAnId_AndSaneStrings()
    {
        Assert.Equal(0, Refused("\"type\":\"open\"").RequestId);
        Assert.Equal(0, Refused("\"type\":\"call\",\"requestId\":0,\"method\":\"zoomIn\"").RequestId);
        Assert.Equal(2, Refused("\"type\":\"open\",\"requestId\":2,\"fileName\":42").RequestId);
        Assert.Equal(2, Refused($"\"type\":\"open\",\"requestId\":2,\"fileName\":\"{new string('a', 2000)}\"").RequestId);
    }

    [Fact]
    public void Calls_AreCheckedMethodByMethod()
    {
        Assert.Equal((7, 3, EmbedMethod.GetPdfMetadata), (Call("getPDFMetadata").RequestId, Call("getPDFMetadata").Document, Call("getPDFMetadata").Method));
        Assert.Equal(EmbedMethod.GetZoomLimits, Call("getZoomLimits").Method);

        var go = Call("gotoLocation", "[3, 10.5, 20]");
        Assert.Equal((3, 10.5, 20.0), (go.Page, go.X, go.Y));
        var goPage = Call("gotoLocation", "[2]");
        Assert.Equal((2, 0.0, 0.0), (goPage.Page, goPage.X, goPage.Y));

        Assert.Equal(1.5, Call("setZoomLevel", "[1.5]").Level);
        Assert.Equal("annual report", Call("search", "[\"  annual report \"]").Term);
        Assert.Equal(5, Call("searchNext", "[5]").SearchId);
        Assert.Equal(EmbedMethod.SearchClear, Call("searchClear", "[5]").Method);
    }

    [Theory]
    [InlineData("eval", "[]")]
    [InlineData("constructor", "[]")]
    [InlineData("gotoLocation", "[]")]
    [InlineData("gotoLocation", "[0]")]
    [InlineData("gotoLocation", "[1.5]")]
    [InlineData("gotoLocation", "[\"2\"]")]
    [InlineData("gotoLocation", "[2, -1, 0]")]
    [InlineData("gotoLocation", "[2, 0, \"top\"]")]
    [InlineData("setZoomLevel", "[]")]
    [InlineData("setZoomLevel", "[0]")]
    [InlineData("setZoomLevel", "[\"150%\"]")]
    [InlineData("search", "[]")]
    [InlineData("search", "[\"   \"]")]
    [InlineData("search", "[42]")]
    [InlineData("searchNext", "[]")]
    [InlineData("searchPrevious", "[0]")]
    public void Calls_WithBadArguments_AreRefusedWithTheirRequestId(string method, string args)
    {
        var ex = Refused($"\"type\":\"call\",\"requestId\":11,\"method\":\"{method}\",\"args\":{args}");
        Assert.Equal(EmbedErrors.InvalidArgument, ex.Code);
        Assert.Equal(11, ex.RequestId);
    }

    [Fact]
    public void Search_RefusesAnOverlongTerm() =>
        Assert.Equal(11, Refused($"\"type\":\"call\",\"requestId\":11,\"method\":\"search\",\"args\":[\"{new string('x', EmbedProtocol.MaxTermLength + 1)}\"]").RequestId);

    [Fact]
    public void OverlongMessages_AreRefusedBeforeParsing_WithTheirRequestId()
    {
        string padding = new('x', EmbedProtocol.MaxMessageLength);
        var call = Refused($"\"type\":\"call\",\"requestId\":12,\"document\":3,\"method\":\"search\",\"args\":[\"{padding}\"]");
        Assert.Equal((EmbedErrors.InvalidArgument, 12), (call.Code, call.RequestId));
        // Not even valid JSON: refused on its length alone, never parsed.
        var open = Refused($"\"type\":\"open\",\"requestId\":13,\"fileName\":\"{padding}");
        Assert.Equal((EmbedErrors.InvalidArgument, 13), (open.Code, open.RequestId));
        // No requestId near the start: refused, with nothing to answer.
        var late = Assert.Throws<EmbedProtocolException>(() => EmbedProtocol.Parse("{\"pad\":\"" + padding + "\"," + Envelope + ",\"type\":\"call\",\"requestId\":14}", EmbedMode.FullWindow));
        Assert.Equal((EmbedErrors.InvalidArgument, 0), (late.Code, late.RequestId));
    }

    [Fact]
    public void MessagesUpToTheLimit_AreParsed()
    {
        string prefix = "{" + Envelope + ",\"type\":\"call\",\"requestId\":15,\"document\":3,\"method\":\"getCurrentPage\",\"args\":[],\"pad\":\"";
        string json = prefix + new string('x', EmbedProtocol.MaxMessageLength - prefix.Length - 2) + "\"}";
        Assert.Equal(EmbedProtocol.MaxMessageLength, json.Length);
        Assert.Equal(15, Assert.IsType<EmbedCall>(EmbedProtocol.Parse(json, EmbedMode.FullWindow)).RequestId);
    }

    private static JsonElement Root(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        Assert.Equal("pdfviewer-embed", root.GetProperty("protocol").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        return root;
    }

    [Fact]
    public void Replies_CarryTheEnvelope_AndTheirPayload()
    {
        Assert.Equal("ready", Root(EmbedProtocol.Ready()).GetProperty("type").GetString());

        var ok = Root(EmbedProtocol.Ok(5, w => w.WriteNumberValue(3)));
        Assert.Equal(("response", 5, true, 3), (ok.GetProperty("type").GetString(), ok.GetProperty("requestId").GetInt32(), ok.GetProperty("ok").GetBoolean(), ok.GetProperty("value").GetInt32()));
        Assert.Equal(JsonValueKind.Null, Root(EmbedProtocol.Ok(5, null)).GetProperty("value").ValueKind);

        var error = Root(EmbedProtocol.Error(6, EmbedErrors.PasswordRequired, "Say \"please\" <b>"));
        Assert.False(error.GetProperty("ok").GetBoolean());
        Assert.Equal(EmbedErrors.PasswordRequired, error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Say \"please\" <b>", error.GetProperty("error").GetProperty("message").GetString());

        var page = Root(EmbedProtocol.Event(EmbedEvents.PageView, w => w.WriteNumber("pageNumber", 4))).GetProperty("event");
        Assert.Equal("PAGE_VIEW", page.GetProperty("type").GetString());
        Assert.Equal(4, page.GetProperty("data").GetProperty("pageNumber").GetInt32());
        Assert.Equal(JsonValueKind.Object, Root(EmbedProtocol.Event(EmbedEvents.DocumentPrint, null)).GetProperty("event").GetProperty("data").ValueKind);

        var search = Root(EmbedProtocol.SearchUpdate(2, 3, 1, 8, completed: true));
        Assert.Equal(2, search.GetProperty("searchId").GetInt32());
        var result = search.GetProperty("result");
        Assert.Equal((3, 1, 8, "COMPLETED"), (result.GetProperty("currentResult").GetProperty("pageNumber").GetInt32(),
            result.GetProperty("currentResult").GetProperty("index").GetInt32(), result.GetProperty("totalResults").GetInt32(), result.GetProperty("status").GetString()));
        Assert.Equal("IN_PROGRESS", Root(EmbedProtocol.SearchUpdate(2, 0, 0, 0, completed: false)).GetProperty("result").GetProperty("status").GetString());

        Assert.Equal(1234.5, Root(EmbedProtocol.Size(1234.5)).GetProperty("height").GetDouble());
        Assert.Equal(80, Root(EmbedProtocol.Scroll(80)).GetProperty("top").GetDouble());
        Assert.Equal("close", Root(EmbedProtocol.Close()).GetProperty("type").GetString());
    }

    [Fact]
    public void OpenFailures_MapToSdkCodes()
    {
        Assert.Equal("NOT_A_PDF", EmbedErrors.FromOpenFailure(ViewerOpenFailure.NotReadable));
        Assert.Equal("PASSWORD_REQUIRED", EmbedErrors.FromOpenFailure(ViewerOpenFailure.PasswordRequired));
        Assert.Equal("WRONG_PASSWORD", EmbedErrors.FromOpenFailure(ViewerOpenFailure.WrongPassword));
        Assert.Equal("CERTIFICATE_REQUIRED", EmbedErrors.FromOpenFailure(ViewerOpenFailure.CertificateRequired));
        Assert.Equal("ENCRYPTION_NOT_SUPPORTED", EmbedErrors.FromOpenFailure(ViewerOpenFailure.EncryptionNotSupportedHere));
        Assert.Equal("TOO_LARGE", EmbedErrors.FromOpenFailure(ViewerOpenFailure.TooLarge));
    }

    private static byte[] DocumentWithInfo(bool tagged)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BT /F1 24 Tf 20 100 Td (Find me here) Tj ET",
            "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >>", mediaBox: "[0 0 300 200]");
        int info = b.Add("<< /Title (Annual report) /Author (Finance team) /Subject (Results) /Keywords (money) /Producer (Test builder) /CreationDate (D:20260102030405Z) >>");
        b.WithTrailer($"/Info {info} 0 R");
        if (tagged) b.WithCatalog($"/StructTreeRoot {b.Add("<< /Type /StructTreeRoot >>")} 0 R /MarkInfo << /Marked true >>");
        return b.Build();
    }

    [Fact]
    public async Task Metadata_ReportsInformationSecurityAndTags()
    {
        byte[] bytes = DocumentWithInfo(tagged: true);
        await using var doc = await ViewerDocument.OpenAsync(bytes);
        var m = await doc.GetMetadataAsync();
        Assert.Equal((1, "Annual report", "Finance team", "Results", "money", "Test builder"), (m.PageCount, m.Title, m.Author, m.Subject, m.Keywords, m.Producer));
        Assert.Equal(new DateTime(2026, 1, 2), m.CreationDate?.Date);
        Assert.Equal("1.7", m.PdfVersion);
        Assert.True(m.IsTagged);
        Assert.False(m.IsEncrypted);
        Assert.True(m.CanCopy && m.CanPrint);
        Assert.Equal(bytes.LongLength, m.FileSizeBytes);

        await using var untagged = await ViewerDocument.OpenAsync(DocumentWithInfo(tagged: false));
        Assert.False((await untagged.GetMetadataAsync()).IsTagged);

        var json = Root(EmbedProtocol.Ok(1, EmbedProtocol.Metadata(m, "report.pdf", null))).GetProperty("value");
        Assert.Equal(1, json.GetProperty("numPages").GetInt32());
        Assert.Equal("Annual report", json.GetProperty("pdfTitle").GetString());
        Assert.Equal("2026-01-02", json.GetProperty("creationDate").GetString()![..10]);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("modDate").ValueKind);
        Assert.True(json.GetProperty("isTagged").GetBoolean());
        Assert.Equal("report.pdf", json.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task Metadata_OfAnEncryptedDocument()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BT /F1 12 Tf 20 100 Td (Secret) Tj ET", "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        await using var doc = await ViewerDocument.OpenAsync(b.Build(new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner")), "user");
        Assert.True((await doc.GetMetadataAsync()).IsEncrypted);
    }

    [Fact]
    public async Task MouseSelection_SelectsBetweenTheCarets_InEitherDirection()
    {
        await using var doc = await ViewerDocument.OpenAsync(DocumentWithInfo(tagged: false));
        var me = Assert.Single(Assert.Single(await doc.SearchPageAsync(1, "me")).Rects);
        double middle = me.Y + me.Height / 2;
        var before = new ViewerPoint(me.X + 0.5, middle);
        var after = new ViewerPoint(me.X + me.Width - 0.5, middle);

        var selection = new ViewerTextSelection(1, await doc.GetTextLayoutAsync(1));
        selection.SelectBetween(before, after);
        Assert.Equal("me", selection.Text);
        Assert.Single(selection.Bands);
        selection.SelectBetween(after, before);
        Assert.Equal("me", selection.Text);

        // A click without a drag selects nothing; a drag across the line selects it all.
        selection.SelectBetween(before, before);
        Assert.True(selection.IsEmpty);
        selection.SelectBetween(new ViewerPoint(0, middle), new ViewerPoint(300, middle));
        Assert.Equal("Find me here", selection.Text);
        selection.Clear();
        Assert.True(selection.IsEmpty);

        // A page without text selects nothing.
        await using var tour = await ViewerDocument.OpenAsync(HelveticaFree());
        var empty = new ViewerTextSelection(1, await tour.GetTextLayoutAsync(1));
        empty.SelectBetween(new ViewerPoint(0, 0), new ViewerPoint(100, 100));
        Assert.True(empty.IsEmpty);
    }

    private static byte[] HelveticaFree()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 10 10 100 100 re f");
        return b.Build();
    }

    [Fact]
    public async Task DemoDocument_MetadataHasItsTitle()
    {
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create());
        var m = await doc.GetMetadataAsync();
        Assert.Equal("PDF Viewer Native: engine tour", m.Title);
        Assert.Equal(4, m.PageCount);
    }
}
