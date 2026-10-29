using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Pdfium.Native;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf text &lt;dir&gt; [--limit N] [--words K] [--out report.json]: the native text page
/// (PdfTextExtractor) against PDFium's (FPDFText_*) on every page of every PDF. Per page: the
/// character count, the page text exactly, after collapsing white space and without white space,
/// the intersection over union of the character boxes (tight and loose) where both sides have
/// the same character at the same index, and the hits of sampled words searched with no options,
/// match case and match whole word. The report holds counts and file names only; document text is
/// never written (word samples are hashed in memory and dropped).
/// </summary>
public static class TextParity
{
    private sealed class Totals
    {
        public int Files, Skipped, Failed, Pages, PagesWithText, CountEqual, TextEqual, CollapsedEqual, NoSpaceEqual;
        public long BoxesCompared, LooseCompared;
        public double IoUSum, LooseIoUSum;
        public long Iou90, LooseIou90;
        public int Searches, SearchEqual;
        public double NativeMs, PdfiumMs;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        int words = int.TryParse(Program.Option(args, "--words"), out int w) ? w : 4;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        // --only previous.json: just the files a previous report listed with differences.
        if (Program.Option(args, "--only") is { } previous)
        {
            using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(previous));
            var names = prior.RootElement.GetProperty("mismatches").EnumerateArray().Select(m => m.GetProperty("file").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => names.Contains(Path.GetRelativePath(dir, f))).ToList();
        }
        var t = new Totals();
        var mismatches = new List<object>();
        var reasons = new Dictionary<string, int>();
        using var engine = new PdfiumEngine();
        var clock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            PdfVectorDocument? vdoc = null;
            PdfiumDocument? pdoc = null;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                try
                {
                    vdoc = await PdfVectorDocument.OpenAsync(bytes, file);
                    pdoc = (PdfiumDocument)await engine.OpenDocumentAsync(bytes);
                }
                catch (Exception)
                {
                    t.Skipped++;
                    continue;
                }
                if (vdoc.PageCount != pdoc.PageCount) { t.Skipped++; continue; }
                t.Files++;
                var badPages = new List<int>();
                for (int p = 1; p <= vdoc.PageCount; p++)
                {
                    var sw = Stopwatch.StartNew();
                    PdfTextPage native;
                    try { native = PdfTextExtractor.LoadPage(vdoc, p); }
                    catch (Exception ex) { Bump(reasons, "native exception " + ex.GetType().Name); badPages.Add(p); t.Pages++; continue; }
                    t.NativeMs += sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    var oracle = Oracle.Read(pdoc, p, out var searcher);
                    t.PdfiumMs += sw.Elapsed.TotalMilliseconds;
                    t.Pages++;
                    using (searcher)
                    {
                        string nText = native.GetAllText();
                        if (oracle.Count > 0 || native.CharCount > 0) t.PagesWithText++;
                        bool countEq = oracle.Count == native.CharCount;
                        bool textEq = oracle.Text == nText;
                        bool collapsedEq = Collapse(oracle.Text) == Collapse(nText);
                        bool noSpaceEq = NoSpace(oracle.Text) == NoSpace(nText);
                        if (countEq) t.CountEqual++;
                        if (textEq) t.TextEqual++;
                        if (collapsedEq) t.CollapsedEqual++;
                        if (noSpaceEq) t.NoSpaceEqual++;
                        if (!collapsedEq)
                        {
                            badPages.Add(p);
                            Bump(reasons, Classify(oracle.Text, nText));
                        }
                        if (countEq)
                        {
                            for (int i = 0; i < oracle.Count; i++)
                            {
                                if (oracle.Unicode[i] != native.GetUnicode(i)) continue;
                                if (IoU(oracle.Boxes[i], native.GetCharBox(i)) is double iou)
                                {
                                    t.BoxesCompared++; t.IoUSum += iou; if (iou >= 0.9) t.Iou90++;
                                }
                                if (IoU(oracle.Loose[i], native.GetLooseCharBox(i)) is double liou)
                                {
                                    t.LooseCompared++; t.LooseIoUSum += liou; if (liou >= 0.9) t.LooseIou90++;
                                }
                            }
                        }
                        foreach (var word in SampleWords(oracle.Text, words))
                        {
                            foreach (var (mc, ww) in new[] { (false, false), (true, false), (false, true) })
                            {
                                var expected = searcher.Find(word, mc, ww);
                                var actual = native.Find(word, mc, ww);
                                t.Searches++;
                                if (expected.SequenceEqual(actual)) t.SearchEqual++;
                                else Bump(reasons, "search hits differ");
                            }
                        }
                    }
                }
                if (badPages.Count > 0) mismatches.Add(new { file = name, pages = badPages.Take(20).ToArray(), count = badPages.Count });
            }
            catch (Exception ex)
            {
                t.Failed++;
                Bump(reasons, "file exception " + ex.GetType().Name);
            }
            finally
            {
                vdoc?.Dispose();
                pdoc?.Dispose();
            }
        }

        double Pct(double a, double b) => b == 0 ? 0 : Math.Round(100.0 * a / b, 3);
        var summary = new
        {
            files = t.Files, skipped = t.Skipped, failed = t.Failed, pages = t.Pages, pagesWithText = t.PagesWithText,
            charCountEqualPct = Pct(t.CountEqual, t.Pages),
            textEqualPct = Pct(t.TextEqual, t.Pages),
            textEqualCollapsedWhitespacePct = Pct(t.CollapsedEqual, t.Pages),
            textEqualIgnoringWhitespacePct = Pct(t.NoSpaceEqual, t.Pages),
            pagesWithTextCollapsedEqualPct = Pct(t.CollapsedEqual - (t.Pages - t.PagesWithText), t.PagesWithText),
            charBoxes = t.BoxesCompared, charBoxMeanIoU = t.BoxesCompared == 0 ? 0 : Math.Round(t.IoUSum / t.BoxesCompared, 4), charBoxIoU90Pct = Pct(t.Iou90, t.BoxesCompared),
            looseBoxes = t.LooseCompared, looseBoxMeanIoU = t.LooseCompared == 0 ? 0 : Math.Round(t.LooseIoUSum / t.LooseCompared, 4), looseBoxIoU90Pct = Pct(t.LooseIou90, t.LooseCompared),
            searches = t.Searches, searchHitsEqualPct = Pct(t.SearchEqual, t.Searches),
            nativeMsPerPage = t.Pages == 0 ? 0 : Math.Round(t.NativeMs / t.Pages, 2),
            pdfiumMsPerPage = t.Pages == 0 ? 0 : Math.Round(t.PdfiumMs / t.Pages, 2),
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            mismatchReasons = reasons.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        var json = JsonSerializer.Serialize(new { summary, mismatches }, new JsonSerializerOptions { WriteIndented = true });
        if (outPath != null) await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void Bump(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string NoSpace(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>What kind of difference a page has, without its text: which side is longer and the class of the first differing character.</summary>
    private static string Classify(string expected, string actual)
    {
        string a = NoSpace(expected), b = NoSpace(actual);
        if (a == b) return "white space only";
        if (a.Length == 0) return "native has text, PDFium none";
        if (b.Length == 0) return "PDFium has text, native none";
        var sa = a.OrderBy(c => c).ToArray();
        var sb = b.OrderBy(c => c).ToArray();
        if (sa.SequenceEqual(sb)) return "same characters, different order";
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string cat(string s) => i < s.Length ? CharUnicodeInfo.GetUnicodeCategory(s[i]).ToString() : "end";
        return $"characters differ ({(a.Length > b.Length ? "native shorter" : a.Length < b.Length ? "native longer" : "same length")}; {cat(a)} vs {cat(b)})";
    }

    private static IEnumerable<string> SampleWords(string text, int max)
    {
        if (max <= 0) return Array.Empty<string>();
        var all = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray()))
            .Where(x => x.Length >= 3).Distinct(StringComparer.Ordinal).ToList();
        if (all.Count <= max) return all;
        var pick = new List<string>();
        for (int k = 0; k < max; k++) pick.Add(all[(int)((long)k * all.Count / max)]);
        return pick;
    }

    private static double? IoU(PdfTextRect a, PdfTextRect b)
    {
        a.Normalize(); b.Normalize();
        double aw = a.Width, ah = a.Height, bw = b.Width, bh = b.Height;
        if (aw <= 0 || ah <= 0 || bw <= 0 || bh <= 0) return null;
        double iw = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double ih = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        double inter = iw > 0 && ih > 0 ? iw * ih : 0;
        return inter / (aw * ah + bw * bh - inter);
    }

    /// <summary>PDFium's text page for one page, read through the native API.</summary>
    internal sealed class Oracle
    {
        public int Count;
        public string Text = string.Empty;
        public char[] Unicode = Array.Empty<char>();
        public PdfTextRect[] Boxes = Array.Empty<PdfTextRect>();
        public PdfTextRect[] Loose = Array.Empty<PdfTextRect>();

        public static Oracle Read(PdfiumDocument doc, int pageNumber, out Searcher searcher)
        {
            var o = new Oracle();
            lock (doc.SyncLock)
            {
                var page = PdfiumNativeBridge.FPDF_LoadPage(doc.Handle, pageNumber - 1);
                var text = PdfiumNativeBridge.FPDFText_LoadPage(page);
                searcher = new Searcher(doc, page, text);
                int n = PdfiumNativeBridge.FPDFText_CountChars(text);
                if (n <= 0) return o;
                o.Count = n;
                var buf = new byte[(n + 1) * 2];
                int written = PdfiumNativeBridge.FPDFText_GetText(text, 0, n, buf);
                o.Text = written > 0 ? PdfiumNativeBridge.Utf16BytesToString(buf, written * 2) : string.Empty;
                o.Unicode = new char[n];
                o.Boxes = new PdfTextRect[n];
                o.Loose = new PdfTextRect[n];
                for (int i = 0; i < n; i++)
                {
                    o.Unicode[i] = (char)PdfiumNativeBridge.FPDFText_GetUnicode(text, i);
                    if (PdfiumNativeBridge.FPDFText_GetCharBox(text, i, out double l, out double r, out double b, out double t) != 0)
                        o.Boxes[i] = new PdfTextRect(l, b, r, t);
                    if (PdfiumNativeBridge.FPDFText_GetLooseCharBox(text, i, out var rect) != 0)
                        o.Loose[i] = new PdfTextRect(rect.left, rect.bottom, rect.right, rect.top);
                }
            }
            return o;
        }
    }

    internal sealed class Searcher : IDisposable
    {
        private readonly PdfiumDocument _doc;
        private readonly SafePageHandle _page;
        private readonly SafeTextPageHandle _text;

        public Searcher(PdfiumDocument doc, SafePageHandle page, SafeTextPageHandle text)
        {
            _doc = doc; _page = page; _text = text;
        }

        public List<(int Start, int Count)> Find(string query, bool matchCase, bool wholeWord)
        {
            var hits = new List<(int, int)>();
            lock (_doc.SyncLock)
            {
                uint flags = (matchCase ? PdfiumNativeBridge.FPDF_MATCHCASE : 0) | (wholeWord ? PdfiumNativeBridge.FPDF_MATCHWHOLEWORD : 0);
                using var handle = PdfiumNativeBridge.FPDFText_FindStart(_text, PdfiumNativeBridge.StringToUtf16NullTerminated(query), flags, 0);
                if (handle == null || handle.IsInvalid) return hits;
                while (PdfiumNativeBridge.FPDFText_FindNext(handle) != 0 && hits.Count < 10000)
                    hits.Add((PdfiumNativeBridge.FPDFText_GetSchResultIndex(handle), PdfiumNativeBridge.FPDFText_GetSchCount(handle)));
            }
            return hits;
        }

        public void Dispose()
        {
            lock (_doc.SyncLock)
            {
                _text.Dispose();
                _page.Dispose();
            }
        }
    }
}
