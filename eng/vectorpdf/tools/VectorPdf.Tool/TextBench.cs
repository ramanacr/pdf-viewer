using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Pdfium.Native;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Text;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf textbench &lt;dir&gt; [--limit N] [--profile] [--out report.json]: how long the first
/// text load of each page takes, native (PdfTextExtractor.LoadPage) against PDFium
/// (FPDF_LoadPage + FPDFText_LoadPage + FPDFText_CountChars), cold (a fresh document, so the first
/// page of a file also pays for its fonts) and warm (the same pages again with the page cache
/// dropped). --profile also splits the native time into content decoding, the content walk and the
/// text page build, with font set-up counted on its own.
///
/// vectorpdf textsearch &lt;file.pdf&gt; [--query word] [--runs N]: a whole-document search, every
/// page loaded and searched in turn on a freshly opened document, native against PDFium.
///
/// Reports hold timings and file names only, never document text.
/// </summary>
public static class TextBench
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        bool profile = args.Contains("--profile");
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();

        var nativeCold = new List<double>();
        var pdfiumCold = new List<double>();
        var nativeWarm = new List<double>();
        var pdfiumWarm = new List<double>();
        var firstPageNative = new List<double>();
        var firstPagePdfium = new List<double>();
        var phases = new Dictionary<string, double>();
        int pages = 0;
        using var engine = new PdfiumEngine();
        using var allocations = StartAllocations(args);

        // JIT both sides on the first file before timing anything.
        if (files.Count > 0) await RunFile(engine, files[0], null, null, null, null, null, null, null);

        foreach (var file in files)
        {
            try
            {
                pages += await RunFile(engine, file, nativeCold, pdfiumCold, nativeWarm, pdfiumWarm, firstPageNative, firstPagePdfium, profile ? phases : null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"{Path.GetFileName(file)}: {ex.GetType().Name}");
            }
        }

        var summary = new
        {
            files = files.Count,
            pages,
            cold = new { native = Stats(nativeCold), pdfium = Stats(pdfiumCold) },
            warm = new { native = Stats(nativeWarm), pdfium = Stats(pdfiumWarm) },
            firstPageOfFile = new { native = Stats(firstPageNative), pdfium = Stats(firstPagePdfium) },
            gc = new
            {
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                pauseMs = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
                allocatedMB = GC.GetTotalAllocatedBytes() / (1024 * 1024),
            },
            allocatedMBByType = AllocationReport(allocations),
            profileMsTotal = profile ? phases.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 1)) : null,
        };
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        if (outPath != null) await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine(json);
        return 0;
    }

    private static async Task<int> RunFile(PdfiumEngine engine, string file,
        List<double>? nativeCold, List<double>? pdfiumCold, List<double>? nativeWarm, List<double>? pdfiumWarm,
        List<double>? firstNative, List<double>? firstPdfium, Dictionary<string, double>? phases)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file);
        PdfVectorDocument vdoc;
        PdfiumDocument pdoc;
        try
        {
            vdoc = await PdfVectorDocument.OpenAsync(bytes, file);
            pdoc = (PdfiumDocument)await engine.OpenDocumentAsync(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
        using var _v = vdoc;
        using var _p = pdoc;
        if (vdoc.PageCount != pdoc.PageCount) return 0;
        int n = vdoc.PageCount;

        for (int p = 1; p <= n; p++)
        {
            double native = TimeNative(vdoc, p);
            double pdfium = TimePdfium(pdoc, p);
            nativeCold?.Add(native);
            pdfiumCold?.Add(pdfium);
            if (p == 1) { firstNative?.Add(native); firstPdfium?.Add(pdfium); }
        }
        for (int p = 1; p <= n; p++)
        {
            PdfTextExtractor.Invalidate(vdoc);
            nativeWarm?.Add(TimeNative(vdoc, p));
            pdfiumWarm?.Add(TimePdfium(pdoc, p));
        }

        if (phases != null)
        {
            // A second document, so that font set-up is paid again and can be told apart.
            using var fresh = await PdfVectorDocument.OpenAsync(bytes, file);
            Profile(fresh, phases);
        }
        return n;
    }

    private static double TimeNative(PdfVectorDocument doc, int page)
    {
        var sw = Stopwatch.StartNew();
        try { _ = PdfTextExtractor.LoadPage(doc, page).CharCount; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double TimePdfium(PdfiumDocument doc, int page)
    {
        var sw = Stopwatch.StartNew();
        lock (doc.SyncLock)
        {
            using var handle = PdfiumNativeBridge.FPDF_LoadPage(doc.Handle, page - 1);
            using var text = PdfiumNativeBridge.FPDFText_LoadPage(handle);
            _ = PdfiumNativeBridge.FPDFText_CountChars(text);
        }
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>The phases of PdfTextExtractor.Build, timed apart.</summary>
    private static void Profile(PdfVectorDocument doc, Dictionary<string, double> phases)
    {
        var fonts = new PdfFontResolver(doc.Resolver, PdfSecurityLimits.Default);
        var infos = new Dictionary<PdfFont, TextFontInfo>(ReferenceEqualityComparer.Instance);
        var fontClock = new Stopwatch();
        TextFontInfo Info(PdfFont font, PdfEngine.Vector.Objects.PdfDictionary? dict)
        {
            if (!infos.TryGetValue(font, out var info))
            {
                fontClock.Start();
                infos[font] = info = new TextFontInfo(font, dict, doc.Resolver);
                fontClock.Stop();
            }
            return info;
        }
        void Add(string key, double ms) => phases[key] = phases.TryGetValue(key, out double v) ? v + ms : ms;

        for (int p = 1; p <= doc.PageCount; p++)
        {
            var node = doc.PageTree.Pages[p - 1];
            var sw = Stopwatch.StartNew();
            var decoder = new PdfStreamDecoder(null, doc.Resolver.Resolve);
            var content = new MemoryStream();
            foreach (var stream in node.Contents)
            {
                try { content.Write(decoder.DecodeStream(stream)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                content.WriteByte((byte)'\n');
            }
            byte[] data = content.ToArray();
            Add("1 decode page content", sw.Elapsed.TotalMilliseconds);

            fontClock.Reset();
            sw.Restart();
            TextHolder holder;
            try { holder = new TextContentReader(doc.Resolver, fonts, Info).Read(data, node.Resources); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { holder = new TextHolder(); }
            double read = sw.Elapsed.TotalMilliseconds;
            Add("2 content walk (fonts excluded)", read - fontClock.Elapsed.TotalMilliseconds);
            Add("2a TextFontInfo set-up", fontClock.Elapsed.TotalMilliseconds);

            sw.Restart();
            try { _ = new PdfTextPage(p, PdfTextExtractor.PageBox(node), node.RotationDegrees, holder); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            Add("3 text page build", sw.Elapsed.TotalMilliseconds);
        }
    }

    public static async Task<int> SearchAsync(string[] args)
    {
        if (args.Length < 2) return 2;
        string file = args[1];
        string query = Program.Option(args, "--query") ?? "the";
        int runs = int.TryParse(Program.Option(args, "--runs"), out int r) ? r : 3;
        byte[] bytes = await File.ReadAllBytesAsync(file);
        using var engine = new PdfiumEngine();
        var native = new List<double>();
        var nativeAgain = new List<double>();
        var pdfium = new List<double>();
        int pages = 0, nativeHits = 0, pdfiumHits = 0;
        for (int run = 0; run <= runs; run++)
        {
            // Run 0 warms the JIT and is not counted.
            using (var doc = await PdfVectorDocument.OpenAsync(bytes, file))
            {
                pages = doc.PageCount;
                var sw = Stopwatch.StartNew();
                int hits = 0;
                for (int p = 1; p <= doc.PageCount; p++)
                    hits += PdfTextExtractor.LoadPage(doc, p).Find(query).Count;
                if (run > 0) native.Add(sw.Elapsed.TotalMilliseconds);
                nativeHits = hits;
                // The same search again on the open document: the pages the cache kept are not rebuilt.
                sw.Restart();
                for (int p = 1; p <= doc.PageCount; p++)
                    _ = PdfTextExtractor.LoadPage(doc, p).Find(query).Count;
                if (run > 0) nativeAgain.Add(sw.Elapsed.TotalMilliseconds);
            }
            using (var doc = (PdfiumDocument)await engine.OpenDocumentAsync(bytes))
            {
                var sw = Stopwatch.StartNew();
                int hits = 0;
                lock (doc.SyncLock)
                {
                    var q = PdfiumNativeBridge.StringToUtf16NullTerminated(query);
                    for (int p = 0; p < doc.PageCount; p++)
                    {
                        using var page = PdfiumNativeBridge.FPDF_LoadPage(doc.Handle, p);
                        using var text = PdfiumNativeBridge.FPDFText_LoadPage(page);
                        using var find = PdfiumNativeBridge.FPDFText_FindStart(text, q, 0, 0);
                        if (find == null || find.IsInvalid) continue;
                        while (PdfiumNativeBridge.FPDFText_FindNext(find) != 0) hits++;
                    }
                }
                if (run > 0) pdfium.Add(sw.Elapsed.TotalMilliseconds);
                pdfiumHits = hits;
            }
        }
        var summary = new
        {
            file = Path.GetFileName(file), pages, runs,
            nativeMs = Stats(native), nativeRepeatMs = Stats(nativeAgain), pdfiumMs = Stats(pdfium),
            hitsEqual = nativeHits == pdfiumHits,
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// vectorpdf texthash &lt;dir&gt; --out hashes.txt: one line per page, the file, the page and a
    /// SHA-256 over everything the native text page gives (text, every character's code, type,
    /// boxes, origin, font size and MCID, and the hits of a few fixed searches), so two builds can
    /// be shown to give the same text pages bit for bit. The text itself is never written.
    /// </summary>
    public static async Task<int> HashAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        string outPath = Program.Option(args, "--out") ?? "texthash.txt";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var output = new StreamWriter(outPath);
        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            PdfVectorDocument doc;
            try { doc = await PdfVectorDocument.OpenAsync(await File.ReadAllBytesAsync(file), file); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { await output.WriteLineAsync($"{name}\topen failed"); continue; }
            using (doc)
            {
                for (int p = 1; p <= doc.PageCount; p++)
                {
                    string hash;
                    try { hash = Hash(PdfTextExtractor.LoadPage(doc, p)); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { hash = "exception " + ex.GetType().Name; }
                    await output.WriteLineAsync($"{name}\t{p}\t{hash}");
                }
            }
        }
        return 0;
    }

    private static string Hash(PdfTextPage page)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buf = new byte[8];
        void D(double v) { BitConverter.TryWriteBytes(buf, v); sha.AppendData(buf); }
        void I(long v) { BitConverter.TryWriteBytes(buf, v); sha.AppendData(buf); }
        void R(PdfTextRect r) { D(r.Left); D(r.Bottom); D(r.Right); D(r.Top); }
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(page.GetAllText()));
        I(page.CharCount);
        for (int i = 0; i < page.CharCount; i++)
        {
            I(page.GetUnicode(i));
            I((int)page.GetCharType(i));
            R(page.GetCharBox(i));
            R(page.GetLooseCharBox(i));
            var o = page.GetOrigin(i);
            D(o.X); D(o.Y);
            D(page.GetFontSize(i));
            I(page.GetMarkedContentId(i));
            I(page.HasTextObject(i) ? 1 : 0);
            I(page.TextIndexFromCharIndex(i));
        }
        foreach (string q in new[] { "the", "e", "a b", "1" })
            foreach (var (mc, ww) in new[] { (false, false), (true, false), (false, true) })
                foreach (var (s, c) in page.Find(q, mc, ww)) { I(s); I(c); }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Allocation ticks (about one per 100 KB allocated) by type, from the runtime's own events.</summary>
    private sealed class AllocationListener : System.Diagnostics.Tracing.EventListener
    {
        public readonly Dictionary<string, long> Bytes = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload == null) return;
            int t = e.PayloadNames!.IndexOf("TypeName");
            int a = e.PayloadNames.IndexOf("AllocationAmount64");
            if (t < 0 || a < 0) return;
            string type = e.Payload[t] as string ?? "?";
            long amount = Convert.ToInt64(e.Payload[a]);
            lock (Bytes) Bytes[type] = Bytes.TryGetValue(type, out long v) ? v + amount : amount;
        }
    }

    internal static System.Diagnostics.Tracing.EventListener? StartAllocations(string[] args) => args.Contains("--alloc") ? new AllocationListener() : null;

    internal static object? AllocationReport(System.Diagnostics.Tracing.EventListener? listener)
    {
        if (listener is not AllocationListener l) return null;
        lock (l.Bytes) return l.Bytes.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value / (1024 * 1024));
    }

    private static object Stats(List<double> values)
    {
        if (values.Count == 0) return new { count = 0 };
        var sorted = values.OrderBy(v => v).ToArray();
        double Pct(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return new
        {
            count = sorted.Length,
            median = Math.Round(Pct(0.5), 2),
            p95 = Math.Round(Pct(0.95), 2),
            p99 = Math.Round(Pct(0.99), 2),
            mean = Math.Round(sorted.Average(), 2),
            total = Math.Round(sorted.Sum(), 1),
        };
    }
}
