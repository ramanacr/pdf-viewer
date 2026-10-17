using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf docx &lt;dir&gt; [--limit N] [--out report.jsonl] [--keep dir]: exports every PDF to Word
/// and checks the result: the export succeeds, the package opens (System.IO.Packaging) with a
/// document part that parses, and the words PDFium reads from the PDF are in the .docx (text
/// coverage). Nothing leaves the machine; document text is never written to the report.
/// </summary>
public static class DocxSmoke
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        string? outPath = Program.Option(args, "--out");
        string? keep = Program.Option(args, "--keep");
        if (keep != null) Directory.CreateDirectory(keep);
        using var word = args.Contains("--word") ? WordPages.TryStart() : null;
        if (args.Contains("--word") && word == null) Console.WriteLine("Word is not installed: page counts are not compared.");
        var ratios = new List<double>();
        int pdfPagesTotal = 0, wordPagesTotal = 0, samePages = 0;
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var engine = new PdfiumEngine();
        // Drawings other than rectangles and lines come over as pictures drawn by the vector renderer.
        using var renderer = Program.CreateRenderer(args);
        var options = new DocxExportOptions { Renderer = renderer };
        using var report = outPath != null ? new StreamWriter(outPath) : null;
        int ok = 0, skipped = 0, failed = 0, noText = 0;
        var coverages = new List<double>();
        var textCoverages = new List<double>();
        long wordsPdf = 0, wordsFound = 0;
        int tables = 0, images = 0, lists = 0, headings = 0, withHeader = 0, multiColumn = 0;
        var clock = Stopwatch.StartNew();
        var slowest = (Name: string.Empty, Seconds: 0.0);
        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            string status = "ok";
            string detail = string.Empty;
            double? coverage = null, inOrder = null;
            int? wordPages = null;
            var fileClock = Stopwatch.StartNew();
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                if (doc.IsEncrypted || doc.PageCount == 0) { skipped++; continue; }
                var output = new MemoryStream();
                var result = await DocxExporter.ExportAsync(doc, output, options);
                if (keep != null) await File.WriteAllBytesAsync(Path.Combine(keep, Path.ChangeExtension(Path.GetFileName(file), ".docx")), output.ToArray());
                if (word != null && word.Count(output.ToArray()) is int wp)
                {
                    wordPages = wp;
                    ratios.Add((double)wp / result.Pages);
                    pdfPagesTotal += result.Pages;
                    wordPagesTotal += wp;
                    if (wp == result.Pages) samePages++;
                }
                output.Position = 0;
                var (body, repeated, columns) = ReadPackage(output);
                tables += result.Tables;
                images += result.Images;
                lists += result.Lists;
                headings += result.Headings;
                if (result.HasHeaderOrFooter) withHeader++;
                if (columns) multiColumn++;

                // Words PDFium reads, and how many of them the document has (headers once per page).
                var pdf = new Dictionary<string, int>(StringComparer.Ordinal);
                var pdfText = new System.Text.StringBuilder();
                await using (var pd = await engine.OpenDocumentAsync(bytes))
                    for (int page = 1; page <= pd.PageCount; page++)
                    {
                        string pageText = await engine.TextService.ExtractPageTextAsync(pd, page);
                        pdfText.AppendLine(pageText);
                        foreach (var w in Words(pageText)) pdf[w] = pdf.GetValueOrDefault(w) + 1;
                    }
                // With --keep, what PDFium reads is kept next to the .docx, to compare them.
                if (keep != null) await File.WriteAllTextAsync(Path.Combine(keep, Path.GetFileNameWithoutExtension(file) + ".pdfium.txt"), pdfText.ToString());
                var docx = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var w in Words(body)) docx[w] = docx.GetValueOrDefault(w) + 1;
                foreach (var w in Words(repeated)) docx[w] = docx.GetValueOrDefault(w) + doc.PageCount;
                long total = pdf.Values.Sum(), found = pdf.Sum(kv => Math.Min(kv.Value, docx.GetValueOrDefault(kv.Key)));
                if (total < 20) { noText++; status = "no-text"; }
                else
                {
                    coverage = (double)found / total;
                    coverages.Add(coverage.Value);
                    // PDFium splits letter-spaced words ("U nited"): in order, as parts of the document's text, they are there too.
                    inOrder = InOrder(pdfText.ToString(), body + "\n" + repeated);
                    textCoverages.Add(inOrder.Value);
                    wordsPdf += total;
                    wordsFound += found;
                    if (inOrder < 0.9) status = "low-coverage";
                }
                ok++;
                detail = $"pages {result.Pages}, paragraphs {result.Paragraphs}, tables {result.Tables}, images {result.Images}, lists {result.Lists}, sections {result.Sections}"
                    + (coverage is double c ? $", words {c:P1}, in order {inOrder:P1}" : string.Empty)
                    + (wordPages is int wpages ? $", word pages {wpages}" : string.Empty) + $", {fileClock.Elapsed.TotalSeconds:F1} s";
            }
            catch (Exception ex)
            {
                status = "error";
                // The exception's type and first frame only: messages can quote the document.
                detail = ex.GetType().Name + " at " + (ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "?");
                failed++;
            }
            if (fileClock.Elapsed.TotalSeconds > slowest.Seconds) slowest = (name, fileClock.Elapsed.TotalSeconds);
            if (status != "ok" && status != "no-text") Console.WriteLine($"{status,-14} {name}  {detail}");
            report?.WriteLine(JsonSerializer.Serialize(new { file = name, status, coverage, inOrder, wordPages, detail }));
        }
        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: exported {ok}, skipped (encrypted or empty) {skipped}, errors {failed}; {noText} without a text layer");
        if (coverages.Count > 0)
        {
            var sorted = coverages.OrderBy(c => c).ToList();
            Console.WriteLine($"text coverage over {coverages.Count} files: words {100.0 * wordsFound / wordsPdf:F2}% overall, mean {100 * coverages.Average():F2}%, median {100 * sorted[sorted.Count / 2]:F2}%, "
                + $"worst {100 * sorted[0]:F2}%, files >= 99% {coverages.Count(c => c >= 0.99)}, >= 95% {coverages.Count(c => c >= 0.95)}, < 90% {coverages.Count(c => c < 0.9)}");
            var text = textCoverages.OrderBy(c => c).ToList();
            Console.WriteLine($"words found in order in the text (letter-spaced words PDFium splits count): mean {100 * text.Average():F2}%, median {100 * text[text.Count / 2]:F2}%, "
                + $"worst {100 * text[0]:F2}%, files >= 99% {text.Count(c => c >= 0.99)}, < 90% {text.Count(c => c < 0.9)}");
        }
        if (ratios.Count > 0)
        {
            var r = ratios.OrderBy(v => v).ToList();
            Console.WriteLine($"pages in Word over {r.Count} files: {wordPagesTotal} for {pdfPagesTotal} PDF pages ({(double)wordPagesTotal / pdfPagesTotal:F3}x); per file median {r[r.Count / 2]:F3}x, "
                + $"mean {r.Average():F3}x, worst {r[^1]:F2}x; same count {samePages} ({100.0 * samePages / r.Count:F1}%), within 5% {r.Count(v => v <= 1.05)}, more than 1.5x {r.Count(v => v > 1.5)}");
        }
        Console.WriteLine($"tables {tables}, images {images}, lists {lists}, headings {headings}; files with a header or footer {withHeader}, with section columns {multiColumn}; slowest {slowest.Seconds:F1} s");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>Opens the package the way Office's own libraries do and returns the text of the body and of the headers and footers.</summary>
    private static (string Body, string Repeated, bool Columns) ReadPackage(Stream stream)
    {
        using var package = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var main = package.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument").Single();
        var docUri = PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), main.TargetUri);
        var docPart = package.GetPart(docUri);
        XDocument document;
        using (var s = docPart.GetStream()) document = XDocument.Load(s);
        string body = Text(document);
        bool columns = document.Descendants(W + "cols").Any(c => (int?)c.Attribute(W + "num") > 1);
        var repeated = new List<string>();
        foreach (var rel in docPart.GetRelationships().Where(r => r.RelationshipType.EndsWith("/header", StringComparison.Ordinal) || r.RelationshipType.EndsWith("/footer", StringComparison.Ordinal)))
        {
            // The first page's copy repeats the default one: counted once.
            var part = package.GetPart(PackUriHelper.ResolvePartUri(docUri, rel.TargetUri));
            using var s = part.GetStream();
            repeated.Add(Text(XDocument.Load(s)));
        }
        return (body, string.Join("\n", repeated.Distinct()), columns);
    }

    private static string Text(XDocument doc) => string.Join("\n", doc.Descendants(W + "p").Select(p =>
        string.Concat(p.Descendants().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? " " : string.Empty))));

    /// <summary>The share of <paramref name="expected"/>'s words found in <paramref name="actual"/>'s letters and digits, in order (near where the last was found) or as a word anywhere.</summary>
    private static double InOrder(string expected, string actual)
    {
        string stream = string.Concat(Words(actual));
        var tokens = new HashSet<string>(Words(actual), StringComparer.Ordinal);
        int pos = 0, found = 0, total = 0;
        foreach (var w in Words(expected))
        {
            total++;
            int from = Math.Max(0, pos - 2000), length = Math.Min(stream.Length - from, 6000);
            int at = length > 0 ? stream.IndexOf(w, from, length, StringComparison.Ordinal) : -1;
            if (at >= 0) { found++; pos = at + w.Length; }
            else if (tokens.Contains(w)) found++;
        }
        return total == 0 ? 1 : (double)found / total;
    }

    private static IEnumerable<string> Words(string text) => Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => m.Value.ToLowerInvariant());
}

/// <summary>
/// Word through COM, for page counts: one hidden instance, each document opened read-only (not
/// added to the recent files, no repair), paginated and closed without saving. Word's own
/// settings are never changed.
/// </summary>
internal sealed class WordPages : IDisposable
{
    private dynamic? _word;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vectorpdf-word-" + Environment.ProcessId);

    private WordPages(dynamic word)
    {
        _word = word;
        Directory.CreateDirectory(_dir);
    }

    public static WordPages? TryStart()
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("Word.Application") is not { } type) return null;
        try
        {
            dynamic word = Activator.CreateInstance(type)!;
            word.Visible = false;
            word.DisplayAlerts = 0;
            return new WordPages(word);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    /// <summary>The pages Word lays the package out on, or null when Word cannot open it.</summary>
    public int? Count(byte[] docx)
    {
        if (_word == null) return null;
        string path = Path.Combine(_dir, "check.docx");
        File.WriteAllBytes(path, docx);
        dynamic? document = null;
        try
        {
            // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, passwords, Revert, write
            // passwords, Format, Encoding, Visible, OpenAndRepair.
            document = _word.Documents.Open(path, false, true, false, "", "", true, "", "", 0, 0, false, false);
            return (int)document.ComputeStatistics(2);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
        finally
        {
            try { document?.Close(0); } catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    public void Dispose()
    {
        try { _word?.Quit(0); } catch (System.Runtime.InteropServices.COMException) { }
        _word = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}