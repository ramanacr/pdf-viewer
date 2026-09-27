using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf edit &lt;dir&gt; [--limit N] [--out report.jsonl]: edits page 1 of every PDF. The
/// longest paragraph is first drawn again unchanged (every glyph re-emitted where it was), which
/// must render as before; then one of its words is replaced, which PDFium must read back, with the
/// page unchanged away from the paragraph. Nothing leaves the machine; document text is never
/// written to the report.
/// </summary>
public static class EditSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var engine = new PdfiumEngine();
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        int ok = 0, skipped = 0, failed = 0, redrawDiffers = 0, missing = 0, changedOutside = 0, noText = 0, notFound = 0, readDifferently = 0;
        var clock = Stopwatch.StartNew();
        using var report = outPath != null ? new StreamWriter(outPath) : null;
        var fonts = SystemFontCatalog.Installed;

        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            string status;
            string? detail;
            try
            {
                (status, detail) = await One(file, engine, renderer, fonts);
            }
            catch (Exception ex)
            {
                status = "error";
                detail = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            }
            switch (status)
            {
                case "ok": ok++; break;
                case "skipped": skipped++; continue;
                case "no-paragraph": noText++; continue;
                case "redraw-differs": redrawDiffers++; break;
                case "text-missing": missing++; break;
                case "changed-outside": changedOutside++; break;
                case "text-not-found": notFound++; break;
                case "read-differently": readDifferently++; break;
                default: failed++; break;
            }
            if (status != "ok") Console.WriteLine($"{status,-16} {name}  {detail}");
            report?.WriteLine(JsonSerializer.Serialize(new { file = name, status, detail }));
        }
        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: ok {ok}, skipped {skipped}, no paragraph {noText}, redraw differs {redrawDiffers}, " +
                          $"text missing {missing}, changed outside {changedOutside}, text not found {notFound}, read differently by PDFium {readDifferently}, errors {failed}");
        return failed + missing > 0 ? 1 : 0;
    }

    private static async Task<(string Status, string Detail)> One(string file, PdfiumEngine engine, PdfEngine.Vector.Direct2D.Direct2DVectorRenderer renderer, SystemFontCatalog fonts)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file);
        PdfVectorDocument doc;
        try { doc = await PdfVectorDocument.OpenAsync(bytes); }
        catch (Exception) { return ("skipped", ""); }
        using (doc)
        {
            if (doc.PageCount == 0 || doc.IsEncrypted || doc.WasRepaired) return ("skipped", "");
            var page = doc.PageTree.Pages[0];
            var editable = PdfContentEditor.Read(doc, 1, fonts);
            var block = editable.Texts.Where(t => Words(t.Text).Length >= 3).OrderByDescending(t => t.Text.Length).FirstOrDefault();
            if (block == null)
            {
                // Text PDFium sees that is not offered for editing (in form XObjects, say) is a gap worth knowing about.
                await using var original = await engine.OpenDocumentAsync(bytes);
                int pdfiumWords = (await engine.TextService.ExtractPageTextAsync(original, 1)).Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length >= 3 && w.All(char.IsLetter));
                return pdfiumWords >= 10 ? ("text-not-found", $"PDFium reads {pdfiumWords} words; {editable.Texts.Count} paragraphs found") : ("no-paragraph", "");
            }
            var area = Inflate(block.Bounds, block.FontSize);
            // PDFium must read the paragraph as we do, or reading the edit back proves nothing.
            await using (var original = await engine.OpenDocumentAsync(bytes))
            {
                string before = await engine.TextService.ExtractPageTextAsync(original, 1);
                if (!Words(block.Text).Where(w => w.Length >= 3).Take(3).All(w => before.Contains(w, StringComparison.Ordinal)))
                    return ("read-differently", "PDFium reads the paragraph's text differently");
            }

            // 1. The paragraph drawn again where it was: the page must look the same.
            var redraw = PdfContentEditor.Apply(doc, bytes, new[] { new PdfTransformContent(1, PdfEditTarget.Text, block.Id, PdfMatrix.Identity) });
            using (var again = await PdfVectorDocument.OpenAsync(redraw.Bytes))
            {
                double whole = await RedactSmoke.DifferentOutside(doc, again, renderer, new PdfRect(-1e6, -1e6, 0, 0), page.CropBox, page.RotationDegrees);
                if (whole > 0.002)
                {
                    if (Environment.GetEnvironmentVariable("VECTORPDF_EDIT_DUMP") is { Length: > 0 } dump)
                        await File.WriteAllBytesAsync(Path.Combine(dump, Path.GetFileNameWithoutExtension(file) + ".redraw.pdf"), redraw.Bytes);
                    return ("redraw-differs", $"{whole:P2} of the page differs");
                }
            }

            // 2. A word replaced: read back by PDFium, the rest of the page as it was.
            var words = Words(block.Text);
            string word = words.Where(w => w.Length >= 3).Skip(words.Length / 3).FirstOrDefault() ?? words[1];
            int at = Regex.Match(block.Text, $@"{Regex.Escape(word)}").Index;
            string replaced = block.Text[..at] + "Zebrafish" + block.Text[(at + word.Length)..];
            var edit = PdfContentEditor.Apply(doc, bytes, new[] { new PdfReplaceText(1, block.Id, replaced) });
            using var after = await PdfVectorDocument.OpenAsync(edit.Bytes);
            await using var pdoc = await engine.OpenDocumentAsync(edit.Bytes);
            string text = await engine.TextService.ExtractPageTextAsync(pdoc, 1);
            var newBlock = PdfContentEditor.Read(after, 1).Texts.FirstOrDefault(t => t.Text.Contains("Zebrafish", StringComparison.Ordinal));
            var changedArea = newBlock != null ? Union(area, Inflate(newBlock.Bounds, block.FontSize)) : area;
            double outside = await RedactSmoke.DifferentOutside(doc, after, renderer, changedArea, page.CropBox, page.RotationDegrees);
            if (!text.Contains("Zebrafish", StringComparison.Ordinal))
            {
                if (Environment.GetEnvironmentVariable("VECTORPDF_EDIT_DUMP") is { Length: > 0 } dump)
                    await File.WriteAllBytesAsync(Path.Combine(dump, Path.GetFileNameWithoutExtension(file) + ".edit.pdf"), edit.Bytes);
                // Local debugging only: never on by default, because it prints the page's text.
                if (Environment.GetEnvironmentVariable("VECTORPDF_EDIT_DEBUG") == "1")
                    Console.WriteLine("  pdfium: " + text.Replace("\r", "").Replace("\n", " | "));
                return ("text-missing", $"{edit.Warnings.Count} warning(s)");
            }
            if (outside > 0.005) return ("changed-outside", $"{outside:P2} of the page outside the paragraph differs");
            return ("ok", $"{editable.Texts.Count} paragraphs, {editable.Images.Count} images, fonts embedded: {edit.EmbeddedFonts.Count}");
        }
    }

    private static string[] Words(string text) => text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.All(char.IsLetter)).ToArray();

    private static PdfRect Inflate(PdfRect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private static PdfRect Union(PdfRect a, PdfRect b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }
}
