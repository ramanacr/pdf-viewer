using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf autotag &lt;dir&gt; [--limit N] [--render-pages N] [--out report.jsonl] [--retag]: Make Accessible
/// on up to N untagged files (150 by default), in one process (with --retag: on tagged files, replacing their tags). For each file: whether it was
/// tagged or refused (and why), crashes, whether every page extracts the same text and the first
/// pages render the same pixels (Direct2D, 72 dpi), and which PDF/UA-1 checkpoints still fail, by
/// kind. Nothing leaves the machine; document text is never printed or written to the report.
/// </summary>
public static class AutoTagSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : 150;
        int renderPages = int.TryParse(Program.Option(args, "--render-pages"), out int rp) ? rp : 3;
        string? outPath = Program.Option(args, "--out");
        bool retag = args.Contains("--retag");
        using var report = outPath != null ? new StreamWriter(outPath) : null;
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        var clock = Stopwatch.StartNew();

        int files = 0, notOpened = 0, alreadyTagged = 0, encrypted = 0;
        var statuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int crashes = 0, textChecked = 0, textSame = 0, renderChecked = 0, renderSame = 0, onlyHuman = 0, compliantWithClaim = 0;
        var checkpoints = new SortedDictionary<string, (int Docs, PdfAutoTagIssueKind Kind)>(StringComparer.Ordinal);
        var summaryTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        int pages = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (files >= limit) break;
            string name = Path.GetRelativePath(dir, file);
            byte[] bytes;
            PdfVectorDocument doc;
            try
            {
                bytes = await File.ReadAllBytesAsync(file);
                doc = await PdfVectorDocument.OpenAsync(bytes);
            }
            catch (Exception) { notOpened++; continue; }
            using (doc)
            {
                if (doc.PageCount == 0) { notOpened++; continue; }
                bool wasTagged = PdfAutoTagger.IsTagged(doc);
                if (wasTagged) alreadyTagged++;
                if (wasTagged != retag) continue;
                if (doc.IsEncrypted) encrypted++;
                files++;
                string status, detail = string.Empty;
                PdfAutoTagResult? result = null;
                var one = Stopwatch.StartNew();
                try
                {
                    // The tagger's own text check is off here, so the smoke measures text equality itself.
                    result = PdfAutoTagger.Tag(doc, new PdfAutoTagOptions { VerifyText = false, Retag = retag });
                    status = result.Status.ToString();
                }
                catch (Exception ex)
                {
                    crashes++;
                    status = "crash";
                    detail = ex.GetType().Name + " at " + (ex.StackTrace?.Split('\n').FirstOrDefault(s => s.Contains("Accessibility", StringComparison.Ordinal))?.Trim() ?? "?");
                }
                statuses[status] = statuses.GetValueOrDefault(status) + 1;
                bool? sameText = null, samePixels = null;
                if (result is { Succeeded: true, Bytes: { } output })
                {
                    pages += doc.PageCount;
                    try
                    {
                        using var after = await PdfVectorDocument.OpenAsync(output);
                        sameText = SameText(doc, after);
                        textChecked++;
                        if (sameText == true) textSame++;
                        samePixels = await SamePixels(doc, after, renderer, renderPages);
                        renderChecked++;
                        if (samePixels == true) renderSame++;
                    }
                    catch (Exception ex)
                    {
                        detail = "check failed: " + ex.GetType().Name;
                    }
                    if (result.OnlyHumanChecksRemain) onlyHuman++;
                    foreach (var issue in result.Remaining)
                        checkpoints[issue.Checkpoint] = (checkpoints.TryGetValue(issue.Checkpoint, out var c) ? c.Docs + 1 : 1, issue.Kind);
                    foreach (var (key, value) in Counts(result.Summary)) summaryTotals[key] = summaryTotals.GetValueOrDefault(key) + value;
                    if (result.Remaining.All(r => r.Checkpoint == "06-002")) compliantWithClaim++;
                }
                if (status != "Tagged" || sameText == false || samePixels == false)
                    Console.WriteLine($"{status,-18} {name}  {(sameText == false ? "text differs " : "")}{(samePixels == false ? "pixels differ " : "")}{detail}");
                report?.WriteLine(JsonSerializer.Serialize(new
                {
                    file = name, status, detail, pages = doc.PageCount, seconds = Math.Round(one.Elapsed.TotalSeconds, 2), sameText, samePixels,
                    remaining = result?.Remaining.Select(r => $"{r.Checkpoint}:{r.Kind}").ToArray(),
                    tablePages = result?.Elements.Where(e => e.Type == "Table").Select(e => e.Page).Distinct().ToArray(),
                    listPages = result?.Elements.Where(e => e.Type == "L").Select(e => e.Page).Distinct().Take(5).ToArray(),
                    summary = result?.Summary,
                }));
            }
        }

        int tagged = statuses.GetValueOrDefault("Tagged");
        Console.WriteLine($"\n{files} {(retag ? "tagged files re-tagged" : "untagged files")} ({pages} pages tagged) in {clock.Elapsed.TotalSeconds:F0} s; seen {alreadyTagged} already tagged; {notOpened} that do not open; {encrypted} encrypted");
        Console.WriteLine("status: " + string.Join(", ", statuses.Select(s => $"{s.Key} {s.Value}")));
        Console.WriteLine($"success {tagged}/{files} ({Pct(tagged, files)}), crashes {crashes}");
        Console.WriteLine($"text equal {textSame}/{textChecked} ({Pct(textSame, textChecked)}), pixels equal (first {renderPages} pages) {renderSame}/{renderChecked} ({Pct(renderSame, renderChecked)})");
        Console.WriteLine($"only human-judgement or non-tagging checks left: {onlyHuman}/{tagged} ({Pct(onlyHuman, tagged)}); every machine check passes but the claim: {compliantWithClaim}/{tagged}");
        Console.WriteLine("found: " + string.Join(", ", summaryTotals.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        Console.WriteLine("\nremaining failures (documents per checkpoint):");
        foreach (var kind in Enum.GetValues<PdfAutoTagIssueKind>())
        {
            var list = checkpoints.Where(c => c.Value.Kind == kind).OrderByDescending(c => c.Value.Docs).ToList();
            if (list.Count == 0) continue;
            Console.WriteLine($"  {kind}: " + string.Join(", ", list.Select(c => $"{c.Key} {c.Value.Docs}")));
        }
        return crashes > 0 ? 1 : 0;
    }

    private static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:F1}%";

    private static IEnumerable<(string, int)> Counts(PdfAutoTagSummary s)
    {
        yield return ("headings", s.Headings);
        yield return ("paragraphs", s.Paragraphs);
        yield return ("lists", s.Lists);
        yield return ("tables", s.Tables);
        yield return ("figures", s.Figures);
        yield return ("links", s.Links);
        yield return ("headers/footers", s.HeadersAndFooters);
        yield return ("multi-column pages", s.MultiColumnPages);
    }

    private static bool SameText(PdfVectorDocument before, PdfVectorDocument after)
    {
        for (int p = 1; p <= before.PageCount; p++)
        {
            string a;
            try { a = PdfTextExtractor.LoadPage(before, p).GetAllText(); }
            catch (Exception) { continue; }
            if (a != PdfTextExtractor.LoadPage(after, p).GetAllText()) return false;
        }
        return true;
    }

    private static async Task<bool> SamePixels(PdfVectorDocument before, PdfVectorDocument after, PdfEngine.Vector.Direct2D.Direct2DVectorRenderer renderer, int pages)
    {
        for (int p = 1; p <= Math.Min(pages, before.PageCount); p++)
        {
            var request = new RenderRequest { PageNumber = p, Dpi = 72 };
            var a = await renderer.RenderAsync(await before.GetPageDisplayListAsync(p), request, null, CancellationToken.None);
            var b = await renderer.RenderAsync(await after.GetPageDisplayListAsync(p), request, null, CancellationToken.None);
            using var pa = a.Page;
            using var pb = b.Page;
            if (pa.WidthPixels != pb.WidthPixels || pa.HeightPixels != pb.HeightPixels || !pa.Pixels.Span.SequenceEqual(pb.Pixels.Span)) return false;
        }
        return true;
    }
    /// <summary>
    /// vectorpdf autotag-show &lt;file.pdf&gt; &lt;page&gt; &lt;out.png&gt;: tags the file and draws the page with
    /// each top-level element's box, coloured by type and numbered in reading order (for checking by eye).
    /// </summary>
    public static async Task<int> ShowAsync(string[] args)
    {
        byte[] bytes = await File.ReadAllBytesAsync(args[1]);
        int page = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var result = PdfAutoTagger.Tag(doc, new PdfAutoTagOptions { Retag = true });
        Console.WriteLine(result.Message);
        if (!result.Succeeded) return 1;
        const double dpi = 108, scale = dpi / 72;
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        var rendered = await renderer.RenderAsync(await doc.GetPageDisplayListAsync(page), new RenderRequest { PageNumber = page, Dpi = dpi }, null, CancellationToken.None);
        using var pixels = rendered.Page;
        var node = doc.PageTree.Pages[page - 1];
        var crop = node.CropBox;
        System.Windows.Point Px(double x, double y) => node.RotationDegrees switch
        {
            90 => new((y - crop.Y) * scale, (x - crop.X) * scale),
            180 => new((crop.X + crop.Width - x) * scale, (y - crop.Y) * scale),
            270 => new((crop.Y + crop.Height - y) * scale, (crop.X + crop.Width - x) * scale),
            _ => new((x - crop.X) * scale, (crop.Y + crop.Height - y) * scale),
        };
        var elements = result.Elements.Where(e => e.Page == page).ToList();
        byte[] raw = pixels.Pixels.ToArray();
        int width = pixels.WidthPixels, height = pixels.HeightPixels, stride = pixels.Stride;
        // WPF draws on a single-threaded apartment.
        var thread = new Thread(() =>
        {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, raw, stride);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new System.Windows.Rect(0, 0, width, height));
            int n = 0;
            foreach (var e in elements)
            {
                var color = e.Type switch
                {
                    "Table" => System.Windows.Media.Colors.Red, "L" => System.Windows.Media.Colors.Green, "Figure" => System.Windows.Media.Colors.Orange,
                    "P" => System.Windows.Media.Colors.SlateGray, _ => System.Windows.Media.Colors.Blue,
                };
                var a = Px(e.Bounds.X, e.Bounds.Y);
                var b = Px(e.Bounds.X + e.Bounds.Width, e.Bounds.Y + e.Bounds.Height);
                var pen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(color), 1.5);
                dc.DrawRectangle(null, pen, new System.Windows.Rect(a, b));
                var label = new System.Windows.Media.FormattedText($"{++n} {e.Type}", System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Segoe UI"), 10, new System.Windows.Media.SolidColorBrush(color), 1.0);
                dc.DrawText(label, new System.Windows.Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y) - 11));
            }
        }
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
        using var fs = File.Create(args[3]);
        encoder.Save(fs);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine(string.Join(", ", result.Elements.Where(e => e.Page == page).GroupBy(e => e.Type).Select(g => $"{g.Key} {g.Count()}")));
        return 0;
    }
}