using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Redaction;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf redact &lt;dir&gt; [--limit N] [--out report.jsonl]: redacts a band across the middle
/// of page 1 of every PDF and checks the result. It must open in both engines, PDFium must find no
/// word inside the band, and outside the band the page must render as before. Nothing leaves the
/// machine; document text is never written to the report.
/// </summary>
public static class RedactSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var engine = new PdfiumEngine();
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        int ok = 0, skipped = 0, failed = 0, leaked = 0, changedOutside = 0;
        long glyphs = 0;
        var clock = Stopwatch.StartNew();
        using var report = outPath != null ? new StreamWriter(outPath) : null;

        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            string status;
            string? detail = null;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                PdfVectorDocument doc;
                try { doc = await PdfVectorDocument.OpenAsync(bytes); }
                catch (Exception) { skipped++; continue; } // not openable to begin with (or encrypted): not a redaction question
                using (doc)
                {
                    if (doc.PageCount == 0 || doc.IsEncrypted) { skipped++; continue; }
                    var page = doc.PageTree.Pages[0];
                    var crop = page.CropBox;
                    var band = new PdfRect(crop.X, crop.Y + crop.Height * 0.4, crop.Width, crop.Height * 0.2);
                    var result = PdfRedactor.Apply(doc, new[] { new PdfRedactionArea(1, band) }, new PdfRedactionOptions { FillColor = null, JpegDecoder = Jpeg });
                    glyphs += result.GlyphsRemoved;

                    // Opens in both engines.
                    using var after = await PdfVectorDocument.OpenAsync(result.Bytes);
                    await using var pdoc = await engine.OpenDocumentAsync(result.Bytes);

                    // No word left inside the band (inset, so words straddling its edges do not count).
                    int inside = 0;
                    if (page.RotationDegrees == 0)
                    {
                        var words = await engine.TextService.ExtractTextSegmentsAsync(pdoc, 1);
                        var inner = new PdfRect(band.X, band.Y + band.Height * 0.2, band.Width, band.Height * 0.6);
                        foreach (var w in words)
                        {
                            if (string.IsNullOrWhiteSpace(w.Text)) continue;
                            double cx = crop.X + (w.X + w.Width / 2) * crop.Width;
                            double cy = crop.Y + (1 - (w.Y + w.Height / 2)) * crop.Height;
                            if (inner.Contains(cx, cy)) inside++;
                        }
                    }

                    // Outside the band, the page renders as it did.
                    double fraction = await DifferentOutside(doc, after, renderer, band, crop, page.RotationDegrees);
                    if (inside > 0) { leaked++; status = "text-left"; detail = $"{inside} word(s) inside the band"; }
                    else if (fraction > 0.005) { changedOutside++; status = "changed-outside"; detail = $"{fraction:P2} of the page outside the band differs"; }
                    else { ok++; status = "ok"; }
                    detail ??= $"{result.GlyphsRemoved} glyphs, {result.ImagesRedacted} images, {result.PathsCut} paths";
                }
            }
            catch (Exception ex)
            {
                failed++;
                status = "error";
                detail = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            }
            if (status != "ok") Console.WriteLine($"{status,-16} {name}  {detail}");
            report?.WriteLine(JsonSerializer.Serialize(new { file = name, status, detail }));
        }
        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: ok {ok}, skipped {skipped}, text left {leaked}, changed outside {changedOutside}, errors {failed}; {glyphs} glyphs removed");
        return leaked + failed > 0 ? 1 : 0;
    }

    internal static byte[]? Jpeg(PdfEngine.Vector.PdfDecodedImage decoded) => PdfEngine.Vector.Direct2D.WicJpeg.TryDecode(decoded, out _, out _);

    internal static async Task<double> DifferentOutside(PdfVectorDocument before, PdfVectorDocument after, PdfEngine.Vector.Direct2D.Direct2DVectorRenderer renderer, PdfRect band, PdfRect crop, int rotation)
    {
        const double dpi = 36;
        var a = await renderer.RenderAsync(await before.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = dpi }, null, CancellationToken.None);
        var b = await renderer.RenderAsync(await after.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = dpi }, null, CancellationToken.None);
        using var pa = a.Page;
        using var pb = b.Page;
        if (pa.WidthPixels != pb.WidthPixels || pa.HeightPixels != pb.HeightPixels) return 1;
        double scale = dpi / 72.0;
        // The band in output pixels, through the page's /Rotate, grown by 3 px for antialiasing.
        (double X, double Y) Px(double x, double y) => rotation switch
        {
            90 => ((y - crop.Y) * scale, (x - crop.X) * scale),
            180 => ((crop.X + crop.Width - x) * scale, (y - crop.Y) * scale),
            270 => ((crop.Y + crop.Height - y) * scale, (crop.X + crop.Width - x) * scale),
            _ => ((x - crop.X) * scale, (crop.Y + crop.Height - y) * scale),
        };
        var corners = new[] { Px(band.X, band.Y), Px(band.X + band.Width, band.Y), Px(band.X, band.Y + band.Height), Px(band.X + band.Width, band.Y + band.Height) };
        double left = corners.Min(c => c.X) - 3, right = corners.Max(c => c.X) + 3, top = corners.Min(c => c.Y) - 3, bottom = corners.Max(c => c.Y) + 3;
        int differing = 0, total = 0;
        var sa = pa.Pixels.Span; var sb = pb.Pixels.Span;
        for (int y = 0; y < pa.HeightPixels; y++)
        {
            for (int x = 0; x < pa.WidthPixels; x++)
            {
                if (y >= top && y <= bottom && x >= left && x <= right) continue;
                int i = y * pa.Stride + x * 4;
                int d = Math.Abs(sa[i] - sb[i]) + Math.Abs(sa[i + 1] - sb[i + 1]) + Math.Abs(sa[i + 2] - sb[i + 2]);
                total++;
                if (d > 48) differing++;
            }
        }
        return total == 0 ? 0 : (double)differing / total;
    }
}
