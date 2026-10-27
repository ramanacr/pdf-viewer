using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Parsing;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Range loading in the web viewer: the protocol's fetchRange request and range reply (validated
/// both ways), the reader that turns engine reads into those messages, and a document opened by
/// ranges in <see cref="ViewerDocument"/> (first page before the rest, then complete).
/// </summary>
public class EmbedRangeTests
{
    private const string Envelope = "\"protocol\":\"pdfviewer-embed\",\"version\":1";

    private static EmbedMessage? Parse(string body) => EmbedProtocol.Parse("{" + Envelope + "," + body + "}", EmbedMode.FullWindow);

    private static JsonElement Root(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>A PDF of <paramref name="pages"/> pages, each with text and its own uncompressed image, written page by page.</summary>
    internal static byte[] ImagePdf(int pages, int side = 96, Func<int, string>? mediaBox = null)
    {
        var objects = new List<byte[]>();
        int Add(string head, byte[]? data = null)
        {
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes(head));
            if (data != null)
            {
                ms.Write("\nstream\n"u8);
                ms.Write(data);
                ms.Write("\nendstream"u8);
            }
            objects.Add(ms.ToArray());
            return objects.Count;
        }
        Add(""); Add(""); // 1 catalog, 2 page tree
        int font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var kids = new List<int>();
        var random = new Random(7);
        for (int p = 1; p <= pages; p++)
        {
            string ops = $"BT /F1 24 Tf 72 720 Td (Page {p} of {pages}) Tj ET\nq 300 0 0 300 72 300 cm /Im1 Do Q\n";
            int content = Add($"<< /Length {ops.Length} >>", Encoding.ASCII.GetBytes(ops));
            var pixels = new byte[side * side * 3];
            random.NextBytes(pixels);
            int image = Add($"<< /Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>", pixels);
            kids.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox?.Invoke(p) ?? "[0 0 612 792]"} /Resources << /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >> /Contents {content} 0 R >>"));
        }
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {pages} >>");
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray();
        var pdf = new MemoryStream();
        pdf.Write("%PDF-1.7\n"u8);
        var offsets = new long[objects.Count];
        foreach (int n in Enumerable.Range(3, objects.Count - 2).Append(2).Append(1))
        {
            offsets[n - 1] = pdf.Position;
            pdf.Write(Encoding.ASCII.GetBytes($"{n} 0 obj\n"));
            pdf.Write(objects[n - 1]);
            pdf.Write("\nendobj\n"u8);
        }
        long xref = pdf.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long at in offsets) table.Append($"{at:D10} 00000 n \n");
        table.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        pdf.Write(Encoding.ASCII.GetBytes(table.ToString()));
        return pdf.ToArray();
    }

    // ------------------------------------------------------------------ protocol

    [Fact]
    public void Ready_AnnouncesRangeLoading()
    {
        var features = Root(EmbedProtocol.Ready()).GetProperty("features");
        Assert.Contains("range", features.EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public void FetchRange_IsWrittenOnlyForAValidRange()
    {
        var m = Root(EmbedProtocol.FetchRange(3, 9, 100, 200, 1000));
        Assert.Equal(("fetchRange", 3, 9, 100L, 200L), (m.GetProperty("type").GetString(), m.GetProperty("file").GetInt32(),
            m.GetProperty("requestId").GetInt32(), m.GetProperty("start").GetInt64(), m.GetProperty("end").GetInt64()));
        Assert.Equal(EmbedProtocol.Name, m.GetProperty("protocol").GetString());
        // No URL, nothing but numbers: the host page chooses where the bytes come from.
        Assert.Equal(["protocol", "version", "type", "file", "requestId", "start", "end"], m.EnumerateObject().Select(p => p.Name));

        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(3, 9, 200, 200, 1000));   // empty
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(3, 9, -1, 10, 1000));     // before the file
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(3, 9, 900, 1001, 1000));  // past its end
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(3, 9, 0, EmbedProtocol.MaxRangeBytes + 1L, 100_000_000)); // too long
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(0, 9, 0, 10, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(3, 0, 0, 10, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbedProtocol.FetchRange(3, 9, 0, 10, EmbedProtocol.MaxFileBytes + 1));
        Assert.NotNull(EmbedProtocol.FetchRange(3, 9, 0, EmbedProtocol.MaxRangeBytes, 100_000_000));
    }

    [Fact]
    public void Open_WithRange_IsParsedAndChecked()
    {
        var open = Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":4,\"range\":{\"length\":5000000,\"initialOffset\":3951424}"));
        Assert.Equal(new EmbedRangeOpen(5_000_000, 3_951_424), open.Range);
        Assert.Null(Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":4")).Range);
        Assert.Null(Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":4,\"range\":null")).Range);
        Assert.Equal(0, Assert.IsType<EmbedOpen>(Parse("\"type\":\"open\",\"requestId\":4,\"range\":{\"length\":10}")).Range!.InitialOffset);

        foreach (string bad in new[]
        {
            "{\"length\":0}", "{\"length\":-5}", "{\"length\":1.5}", "{\"length\":\"10\"}", "{}",
            $"{{\"length\":{EmbedProtocol.MaxFileBytes + 1}}}", "{\"length\":10,\"initialOffset\":11}", "{\"length\":10,\"initialOffset\":-1}", "[10]", "10",
        })
        {
            var ex = Assert.Throws<EmbedProtocolException>(() => Parse($"\"type\":\"open\",\"requestId\":4,\"range\":{bad}"));
            Assert.Equal((EmbedErrors.InvalidArgument, 4), (ex.Code, ex.RequestId));
        }
    }

    [Fact]
    public void RangeReply_IsParsedAndChecked()
    {
        var ok = Assert.IsType<EmbedRangeReply>(Parse("\"type\":\"range\",\"requestId\":12,\"file\":4,\"ok\":true"));
        Assert.Equal(new EmbedRangeReply(12, 4, true, null, null), ok);
        var failed = Assert.IsType<EmbedRangeReply>(Parse("\"type\":\"range\",\"requestId\":12,\"file\":4,\"ok\":false,\"error\":{\"code\":\"HTTP_ERROR\",\"message\":\"HTTP 500\"}"));
        Assert.Equal((false, "HTTP_ERROR", "HTTP 500"), (failed.Ok, failed.ErrorCode, failed.ErrorMessage));
        // An error code that is not an SDK code is replaced; a long message is cut.
        var odd = Assert.IsType<EmbedRangeReply>(Parse($"\"type\":\"range\",\"requestId\":12,\"file\":4,\"ok\":false,\"error\":{{\"code\":\"<b>\",\"message\":\"{new string('x', 2000)}\"}}"));
        Assert.Equal(EmbedErrors.NetworkError, odd.ErrorCode);
        Assert.Equal(500, odd.ErrorMessage!.Length);

        Assert.Throws<EmbedProtocolException>(() => Parse("\"type\":\"range\",\"file\":4,\"ok\":true"));                // no requestId
        Assert.Throws<EmbedProtocolException>(() => Parse("\"type\":\"range\",\"requestId\":12,\"ok\":true"));          // no file
        Assert.Throws<EmbedProtocolException>(() => Parse("\"type\":\"range\",\"requestId\":12,\"file\":4,\"ok\":1"));  // ok not a boolean
        Assert.Throws<EmbedProtocolException>(() => Parse("\"type\":\"range\",\"requestId\":12,\"file\":0,\"ok\":true"));
    }

    // ------------------------------------------------------------------ the reader

    /// <summary>A host page in memory: it answers each fetchRange with the file's bytes, or as <see cref="Answer"/> says.</summary>
    private sealed class FakeHost
    {
        private readonly byte[] _file;
        public readonly List<(int Id, long Start, long End)> Requests = new();
        public EmbedRangeReader? Reader;
        public Func<int, long, long, (bool Ok, byte[] Data)?>? Answer;
        public bool Hold;
        public readonly List<(int Id, long Start, long End)> Held = new();

        public FakeHost(byte[] file) => _file = file;

        public void Post(string json)
        {
            var m = Root(json);
            Assert.Equal("fetchRange", m.GetProperty("type").GetString());
            int id = m.GetProperty("requestId").GetInt32();
            long start = m.GetProperty("start").GetInt64(), end = m.GetProperty("end").GetInt64();
            lock (Requests)
            {
                Requests.Add((id, start, end));
                if (Hold)
                {
                    Held.Add((id, start, end));
                    return;
                }
            }
            Reply(id, start, end);
        }

        public void Reply(int id, long start, long end)
        {
            var (ok, data) = Answer?.Invoke(id, start, end) ?? (true, _file[(int)start..(int)end]);
            // As the browser does: the answer arrives later, as its own message.
            _ = Task.Run(() => Reader!.Receive(new EmbedRangeReply(id, Reader.File, ok, ok ? null : "HTTP_ERROR", ok ? null : "failed"), data));
        }
    }

    private static (EmbedRangeReader Reader, FakeHost Host) Connect(byte[] file, int fileId = 4)
    {
        var host = new FakeHost(file);
        var reader = new EmbedRangeReader(fileId, file.Length, host.Post);
        host.Reader = reader;
        return (reader, host);
    }

    [Fact]
    public async Task Reader_ReadsThroughTheHost_InPiecesOfAtMost4MB()
    {
        var file = new byte[10 * 1024 * 1024];
        new Random(1).NextBytes(file);
        var (reader, host) = Connect(file);
        var buffer = new byte[9 * 1024 * 1024];
        await reader.ReadAsync(500_000, buffer, CancellationToken.None);
        Assert.Equal(file.AsSpan(500_000, buffer.Length).ToArray(), buffer);
        Assert.Equal(3, host.Requests.Count);
        Assert.All(host.Requests, r => Assert.True(r.End - r.Start <= EmbedProtocol.MaxRangeBytes));
        Assert.Equal(buffer.Length, reader.BytesReceived);
        Assert.Equal(0, reader.InFlight);
    }

    [Fact]
    public async Task Reader_AcceptsOnlyAWaitingRequestOfItsFile_WithTheBytesAskedFor()
    {
        var file = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var (reader, host) = Connect(file);
        host.Hold = true;
        var read = reader.ReadAsync(100, new byte[50], CancellationToken.None).AsTask();
        var (id, start, end) = Assert.Single(host.Held);

        Assert.False(reader.Receive(new EmbedRangeReply(id + 1, 4, true, null, null), new byte[50]));   // not asked for
        Assert.False(reader.Receive(new EmbedRangeReply(id, 5, true, null, null), new byte[50]));       // another file
        Assert.False(read.IsCompleted);
        Assert.True(reader.Receive(new EmbedRangeReply(id, 4, true, null, null), new byte[49]));        // one byte short: fails
        await Assert.ThrowsAsync<ViewerRangeReadException>(() => read);
        Assert.False(reader.Receive(new EmbedRangeReply(id, 4, true, null, null), new byte[50]));       // answered already
    }

    [Fact]
    public async Task Reader_KeepsAtMostSixRequestsWaiting()
    {
        var file = new byte[100_000];
        var (reader, host) = Connect(file);
        host.Hold = true;
        var reads = Enumerable.Range(0, 9).Select(i => reader.ReadAsync(i * 1000, new byte[100], CancellationToken.None).AsTask()).ToList();
        await Task.Delay(50);
        Assert.Equal(EmbedProtocol.MaxRangeRequestsInFlight, host.Held.Count);
        Assert.Equal(EmbedProtocol.MaxRangeRequestsInFlight, reader.InFlight);
        lock (host.Requests) host.Hold = false;
        foreach (var (id, start, end) in host.Held.ToList()) host.Reply(id, start, end);
        await Task.WhenAll(reads);
        Assert.Equal(9, host.Requests.Count);
    }

    [Fact]
    public async Task Reader_HostErrors_TimeoutsAndClose_FailTheRead()
    {
        var file = new byte[10_000];
        var (reader, host) = Connect(file);
        host.Answer = (_, _, _) => (false, []);
        await Assert.ThrowsAsync<ViewerRangeReadException>(() => reader.ReadAsync(0, new byte[10], CancellationToken.None).AsTask());

        var slow = new EmbedRangeReader(4, file.Length, _ => { }) { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAsync<ViewerRangeReadException>(() => slow.ReadAsync(0, new byte[10], CancellationToken.None).AsTask());

        var closing = new EmbedRangeReader(4, file.Length, _ => { });
        var waiting = closing.ReadAsync(0, new byte[10], CancellationToken.None).AsTask();
        closing.Close();
        await Assert.ThrowsAsync<ViewerRangeReadException>(() => waiting);
        await Assert.ThrowsAsync<ViewerRangeReadException>(() => closing.ReadAsync(0, new byte[10], CancellationToken.None).AsTask());
    }

    // ------------------------------------------------------------------ the document

    [Fact]
    public async Task RangedDocument_DrawsPagesBeforeTheRest_ThenCompletes()
    {
        var file = ImagePdf(80);
        var (reader, host) = Connect(file);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 16 * 1024 });
        Assert.Equal(80, doc.PageCount);
        Assert.False(doc.IsComplete);
        Assert.False(doc.CanAnnotate);
        Assert.False(doc.CanFillForms);
        Assert.Throws<InvalidOperationException>(() => doc.Bytes);

        var first = await doc.RenderPageAsync(1, 0.5);
        var text = await doc.GetTextLayoutAsync(42);
        Assert.True(first.Width > 0);
        Assert.Contains("Page 42 of 80", text.GetText(0, text.Length));
        long beforeRest = reader.BytesReceived;
        Assert.True(beforeRest < file.Length / 5, $"{beforeRest} of {file.Length} bytes before the rest");

        bool completed = false;
        doc.Completed += () => completed = true;
        var metadata = doc.GetMetadataAsync();  // waits for the whole file
        await doc.EnsureCompleteAsync();
        Assert.True(doc.IsComplete);
        Assert.True(completed);
        Assert.Equal(file, doc.Bytes);
        Assert.Equal(80, (await metadata).PageCount);
        Assert.True(doc.CanAnnotate);
        // Nothing fetched twice: the background read copies what pages already fetched.
        Assert.True(reader.BytesReceived < file.Length + beforeRest / 2, $"{reader.BytesReceived} received for {file.Length}");

        // The same pixels as the document opened from memory.
        await using var whole = await ViewerDocument.OpenAsync(file);
        var expected = await whole.RenderPageAsync(1, 0.5);
        Assert.Equal(expected.Pixels.ToArray(), first.Pixels.ToArray());
    }

    [Fact]
    public async Task RangedDocument_PagesNotReadYet_HaveTheFirstPagesSize_UntilTheyAreRead()
    {
        // Page 30 is A4 landscape, the others Letter: until it is read, it is laid out as Letter.
        var file = ImagePdf(60, mediaBox: p => p == 30 ? "[0 0 842 595]" : "[0 0 612 792]");
        var (reader, _) = Connect(file);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 16 * 1024 });
        Assert.Equal(60, doc.PageCount);
        Assert.Equal(new ViewerPage(30, 612, 792), doc.Pages[29]);
        // Opening read the end of the file and page 1's dictionary, not every page's.
        long opened = reader.BytesReceived;
        Assert.True(opened < file.Length / 20, $"{opened} of {file.Length} bytes to open");

        int changes = 0;
        doc.PagesChanged += () => changes++;
        await doc.EnsurePageSizeAsync(30);
        Assert.Equal(new ViewerPage(30, 842, 595), doc.Pages[29]);
        Assert.Equal(842, doc.Geometry[29].MediaBox.Width);
        Assert.Equal(1, changes);
        await doc.RenderPageAsync(30, 0.25);
        await doc.RenderPageAsync(31, 0.25);   // the same size as the first: nothing changes
        Assert.Equal(1, changes);

        await doc.EnsureCompleteAsync();
        Assert.Equal(new ViewerPage(30, 842, 595), doc.Pages[29]);
        await using var whole = await ViewerDocument.OpenAsync(file);
        Assert.Equal(whole.Pages, doc.Pages);
    }

    [Fact]
    public async Task RangedDocument_PageCountFromALyingTree_IsCorrected()
    {
        // The root claims 12 pages and has 10 kids: the tree is read whole at once, and the count is the real one.
        var file = ImagePdf(10);
        string latin = Encoding.Latin1.GetString(file);
        var lying = Encoding.Latin1.GetBytes(latin.Replace("/Count 10 >>", "/Count 12 >>"));
        var (reader, _) = Connect(lying);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 4096 });
        Assert.Equal(10, doc.PageCount);
        Assert.False(doc.IsComplete);
        var text = await doc.GetTextLayoutAsync(10);
        Assert.Contains("Page 10 of 10", text.GetText(0, text.Length));
    }

    /// <summary>
    /// Objects 1..n in number order (1 is the catalog, its /Pages is object 3), each followed by
    /// <paramref name="padding"/> bytes of comment. A body "page:T" is a page whose text is T
    /// (its content stream is written as the next object).
    /// </summary>
    private static byte[] TreePdf(IReadOnlyList<string> objects, int padding = 2048)
    {
        var bodies = new List<string>();
        var numbers = new Dictionary<int, int>();   // given number -> written number
        // Pages take two numbers (page, content): renumber references after them.
        int next = 1;
        for (int i = 0; i < objects.Count; i++)
        {
            numbers[i + 1] = next;
            next += objects[i].StartsWith("page:", StringComparison.Ordinal) ? 2 : 1;
        }
        string Renumber(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"(\d+) 0 R", m => $"{numbers[int.Parse(m.Groups[1].Value)]} 0 R");
        foreach (var o in objects)
        {
            if (o.StartsWith("page:", StringComparison.Ordinal))
            {
                string ops = $"BT /F1 24 Tf 72 720 Td ({o[5..]}) Tj ET";
                bodies.Add($"<< /Type /Page /MediaBox [0 0 612 792] /Resources << /Font << /F1 2 0 R >> >> /Contents {bodies.Count + 2} 0 R >>");
                bodies.Add($"<< /Length {ops.Length} >>\nstream\n{ops}\nendstream");
            }
            else
            {
                bodies.Add(Renumber(o));
            }
        }
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new long[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = pdf.Position;
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n%{new string('x', padding)}\n");
        }
        long xref = pdf.Position;
        W($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long at in offsets) W($"{at:D10} 00000 n \n");
        W($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private const string Catalog = "<< /Type /Catalog /Pages 3 0 R >>";
    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    [Fact]
    public async Task RangedDocument_APageThatShrinksTheCount_ChangesThePages_EvenWhenItFails()
    {
        // The root has 10 kids and /Count 10, but only kids 1 and 2 are pages (the full walk skips
        // the others): page 1 is found lazily, page 5 shows the lie.
        var objects = new List<string> { Catalog, Font, "<< /Type /Pages /Kids [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R] /Count 10 >>", "page:One", "page:Two" };
        for (int i = 6; i <= 13; i++) objects.Add("0");
        var file = TreePdf(objects);
        var (reader, _) = Connect(file);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 4096 });
        Assert.Equal(10, doc.PageCount);
        int changes = 0;
        doc.PagesChanged += () => changes++;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.RenderPageAsync(5, 0.25));
        Assert.Equal(1, changes);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(2, doc.Geometry.Count);
        Assert.False(doc.IsComplete);
    }

    [Fact]
    public async Task RangedDocument_APageDrawnFromAMisleadingTree_IsReportedWhenTheFileIsComplete()
    {
        // Root /Count 3 with 3 kids: X (a node of 2 pages), "Three" and a non-node. The counts add
        // up, but the shortcut (as many kids as pages) takes kid 2 for page 2: it shows "Three"
        // until the whole file is read, where page 2 is X's second page.
        var file = TreePdf([Catalog, Font, "<< /Type /Pages /Kids [4 0 R 7 0 R 8 0 R] /Count 3 >>",
            "<< /Type /Pages /Kids [5 0 R 6 0 R] /Count 2 >>", "page:One", "page:Two", "page:Three", "0"]);
        var (reader, _) = Connect(file);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 4096 });
        var before = await doc.GetTextLayoutAsync(2);
        Assert.Contains("Three", before.GetText(0, before.Length));
        int changes = 0;
        doc.PagesChanged += () => changes++;
        await doc.EnsureCompleteAsync();
        Assert.Equal(1, changes);   // the viewer draws its pages again
        Assert.Equal(3, doc.PageCount);
        var after = await doc.GetTextLayoutAsync(2);
        Assert.Contains("Two", after.GetText(0, after.Length));
    }

    [Fact]
    public async Task RangedDocument_DamagedCrossReference_IsReadWholeAtOnce()
    {
        var file = ImagePdf(10);
        string latin = Encoding.Latin1.GetString(file);
        int at = latin.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        int end = latin.IndexOf('\n', at);
        var broken = Encoding.Latin1.GetBytes(latin[..at] + "777".PadLeft(end - at, '0') + latin[end..]);
        var (reader, _) = Connect(broken);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 4096 });
        Assert.True(doc.IsComplete);
        Assert.NotNull(doc.RangeFallbackReason);
        Assert.True(doc.WasRepaired);
        Assert.Equal(10, doc.PageCount);
        Assert.Equal(broken, doc.Bytes);
    }

    [Fact]
    public async Task RangedDocument_ReaderFailure_IsANetworkError()
    {
        var file = ImagePdf(5);
        var (reader, host) = Connect(file);
        host.Answer = (_, _, _) => (false, []);
        var ex = await Assert.ThrowsAsync<ViewerOpenException>(() => ViewerDocument.OpenRangedAsync(reader));
        Assert.Equal(ViewerOpenFailure.NetworkError, ex.Failure);
        Assert.Equal(EmbedErrors.NetworkError, EmbedErrors.FromOpenFailure(ex.Failure));
    }

    [Fact]
    public async Task RangedDocument_CompletionFailure_IsMapped_AndTheNextTryResumes()
    {
        var file = ImagePdf(40);
        var (reader, host) = Connect(file);
        await using var doc = await ViewerDocument.OpenRangedAsync(reader, rangeOptions: new PdfRangedOptions { TailBytes = 16 * 1024, CompletionRequestBytes = 64 * 1024 });
        bool down = true;
        host.Answer = (_, start, end) => Volatile.Read(ref down) && start >= file.Length / 2 ? (false, []) : (true, file[(int)start..(int)end]);
        var ex = await Assert.ThrowsAsync<ViewerOpenException>(() => doc.EnsureCompleteAsync());
        Assert.Equal(ViewerOpenFailure.NetworkError, ex.Failure);
        Assert.False(doc.IsComplete);
        long first = reader.BytesReceived;

        Volatile.Write(ref down, false);
        await doc.EnsureCompleteAsync();
        Assert.True(doc.IsComplete);
        Assert.Equal(file, doc.Bytes);
        Assert.True(reader.BytesReceived < file.Length + first / 4, $"{reader.BytesReceived} received for {file.Length}");
    }

    [Fact]
    public async Task RangedDocument_TooLarge_IsRefusedBeforeAnyRead()
    {
        var reader = new MemoryRangeReader(new byte[2000]);
        var ex = await Assert.ThrowsAsync<ViewerOpenException>(() => ViewerDocument.OpenRangedAsync(reader, options: new ViewerOptions { MaxFileBytes = 1000 }));
        Assert.Equal(ViewerOpenFailure.TooLarge, ex.Failure);
        Assert.Equal(0, reader.Requests);
    }
}
