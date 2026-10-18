using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf xlsx|pptx &lt;dir&gt; [--limit N] [--out report.jsonl] [--keep dir] [--office] [--background]:
/// exports every PDF to Excel or PowerPoint and checks the result: the export succeeds and the
/// package opens (System.IO.Packaging) with every XML part parsing. With --office the outputs
/// are opened read-only in Excel or PowerPoint (when installed) to find any that need repair.
/// Only counts are reported: no document text. Nothing leaves the machine.
/// </summary>
public static class OfficeSmoke
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    public static async Task<int> RunAsync(string[] args)
    {
        bool excel = args[0] == "xlsx";
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        string? outPath = Program.Option(args, "--out");
        string? keep = Program.Option(args, "--keep");
        if (keep != null) Directory.CreateDirectory(keep);
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var office = args.Contains("--office") ? OfficeCheck.TryStart(excel) : null;
        if (args.Contains("--office") && office == null)
            Console.WriteLine($"{(excel ? "Excel" : "PowerPoint")} is not installed (or PowerPoint is already open): outputs are not opened in it.");
        using var renderer = excel ? null : Program.CreateRenderer(args);
        bool background = args.Contains("--background");
        using var report = outPath != null ? new StreamWriter(outPath) : null;

        int ok = 0, skipped = 0, failed = 0, opened = 0, repaired = 0, notOpened = 0, mismatched = 0;
        long pages = 0, sheets = 0, tables = 0, cells = 0, numbers = 0, dates = 0, merged = 0, textRows = 0;
        long slides = 0, textBoxes = 0, pictures = 0, shapes = 0;
        var clock = Stopwatch.StartNew();
        var slowest = (Name: string.Empty, Seconds: 0.0);
        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            string status = "ok", detail = string.Empty;
            string? officeStatus = null;
            var fileClock = Stopwatch.StartNew();
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                if (doc.IsEncrypted || doc.PageCount == 0) { skipped++; continue; }
                var output = new MemoryStream();
                int expected;
                if (excel)
                {
                    var r = await XlsxExporter.ExportAsync(doc, output);
                    pages += r.Pages; sheets += r.Sheets; tables += r.Tables; cells += r.Cells; numbers += r.Numbers; dates += r.Dates; merged += r.MergedCells; textRows += r.TextRows;
                    expected = r.Sheets;
                    detail = $"pages {r.Pages}, sheets {r.Sheets}, tables {r.Tables}, cells {r.Cells}, numbers {r.Numbers}, dates {r.Dates}, merged {r.MergedCells}, text rows {r.TextRows}";
                }
                else
                {
                    var r = await PptxExporter.ExportAsync(doc, output, new PptxExportOptions { Renderer = renderer, PageAsBackground = background });
                    pages += r.Pages; slides += r.Slides; textBoxes += r.TextBoxes; pictures += r.Pictures; shapes += r.Shapes; tables += r.Tables;
                    expected = r.Slides;
                    detail = $"slides {r.Slides}, text boxes {r.TextBoxes}, pictures {r.Pictures}, shapes {r.Shapes}, tables {r.Tables}";
                }
                byte[] package = output.ToArray();
                int counted = CheckPackage(package, excel);
                if (counted != expected) throw new InvalidDataException($"the package has {counted} of {expected} {(excel ? "sheets" : "slides")}");
                string ext = excel ? ".xlsx" : ".pptx";
                if (keep != null) await File.WriteAllBytesAsync(Path.Combine(keep, Path.ChangeExtension(Path.GetFileName(file), ext)), package);
                if (office != null)
                {
                    var check = office.Open(package, ext, expected);
                    officeStatus = check.ToString().ToLowerInvariant();
                    switch (check)
                    {
                        case OfficeResult.Opened: opened++; break;
                        case OfficeResult.Repaired: repaired++; status = "repaired"; break;
                        case OfficeResult.CountDiffers: mismatched++; status = "count-differs"; break;
                        default: notOpened++; status = "not-opened"; break;
                    }
                }
                ok++;
                detail += $", {fileClock.Elapsed.TotalSeconds:F1} s";
            }
            catch (Exception ex)
            {
                status = "error";
                // The exception's type and first frame only: messages can quote the document.
                detail = ex.GetType().Name + " at " + (ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "?");
                failed++;
            }
            if (fileClock.Elapsed.TotalSeconds > slowest.Seconds) slowest = (name, fileClock.Elapsed.TotalSeconds);
            if (status != "ok") Console.WriteLine($"{status,-14} {name}  {detail}");
            report?.WriteLine(JsonSerializer.Serialize(new { file = name, status, office = officeStatus, detail }));
        }
        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: exported {ok}, skipped (encrypted or empty) {skipped}, errors {failed}; {pages} pages");
        if (excel)
            Console.WriteLine($"sheets {sheets}, tables {tables}, cells {cells}, numbers {numbers}, dates {dates}, merged cells {merged}, text rows {textRows}");
        else
            Console.WriteLine($"slides {slides}, text boxes {textBoxes}, pictures {pictures}, shapes {shapes}, tables {tables}{(background ? " (page as background)" : string.Empty)}");
        if (office != null)
            Console.WriteLine($"{(excel ? "Excel" : "PowerPoint")}: opened without repair {opened}, repaired {repaired}, {(excel ? "sheet" : "slide")} count differs {mismatched}, could not open {notOpened}");
        Console.WriteLine($"slowest {slowest.Seconds:F1} s");
        return failed + repaired + notOpened + mismatched > 0 ? 1 : 0;
    }

    /// <summary>
    /// vectorpdf officecheck &lt;dir&gt;: opens every .xlsx and .pptx under the folder read-only in
    /// Excel or PowerPoint and counts those that open, need repair or do not open (no document text).
    /// </summary>
    public static int Check(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int failed = 0;
        foreach (bool excel in new[] { true, false })
        {
            string ext = excel ? ".xlsx" : ".pptx";
            var files = Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count == 0) continue;
            using var office = OfficeCheck.TryStart(excel);
            if (office == null) { Console.WriteLine($"{(excel ? "Excel" : "PowerPoint")} is not available: {files.Count} {ext} files not checked."); continue; }
            var counts = new Dictionary<OfficeResult, int>();
            foreach (var file in files)
            {
                byte[] bytes = File.ReadAllBytes(file);
                var result = office.Open(bytes, ext, CheckPackageOrZero(bytes, excel));
                counts[result] = counts.GetValueOrDefault(result) + 1;
                if (result != OfficeResult.Opened) { failed++; Console.WriteLine($"{result,-14} {Path.GetRelativePath(dir, file)}"); }
            }
            Console.WriteLine($"{(excel ? "Excel" : "PowerPoint")}: {files.Count} files, " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")));
        }
        return failed > 0 ? 1 : 0;
    }

    private static int CheckPackageOrZero(byte[] bytes, bool excel)
    {
        try { return CheckPackage(bytes, excel); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException) { return 0; }
    }

    /// <summary>Opens the package the way Office's own libraries do and parses every XML part; returns its sheets or slides.</summary>
    private static int CheckPackage(byte[] bytes, bool excel)
    {
        using var package = Package.Open(new MemoryStream(bytes), FileMode.Open, FileAccess.Read);
        XDocument? main = null;
        foreach (var part in package.GetParts())
        {
            if (!part.ContentType.EndsWith("xml", StringComparison.Ordinal)) continue;
            using var s = part.GetStream();
            var xml = XDocument.Load(s);
            if (part.Uri.OriginalString is "/xl/workbook.xml" or "/ppt/presentation.xml") main = xml;
        }
        if (main == null) throw new InvalidDataException("the package has no main part");
        return excel ? main.Descendants(S + "sheet").Count() : main.Descendants(P + "sldId").Count();
    }
}

internal enum OfficeResult { Opened, Repaired, CountDiffers, NotOpened }

/// <summary>
/// Excel or PowerPoint through COM: one hidden instance of its own, each output opened
/// read-only (not added to the recent files) and closed without saving. Settings that Office
/// keeps between sessions are never changed: only this instance's alerts are turned off. A file
/// needs repair when Office writes a repair log for it, cannot open it, or opens it with fewer
/// sheets or slides than were written.
/// </summary>
internal sealed class OfficeCheck : IDisposable
{
    private dynamic? _app;
    private readonly bool _excel;
    /// <summary>The Office processes this check started: closed when it ends, if quitting leaves them running.</summary>
    private readonly List<int> _started;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vectorpdf-office-" + Environment.ProcessId);

    private OfficeCheck(dynamic app, bool excel, List<int> started)
    {
        _app = app;
        _excel = excel;
        _started = started;
        Directory.CreateDirectory(_dir);
    }

    private static HashSet<int> Running(bool excel) => Process.GetProcessesByName(excel ? "EXCEL" : "POWERPNT").Select(p => p.Id).ToHashSet();

    public static OfficeCheck? TryStart(bool excel)
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID(excel ? "Excel.Application" : "PowerPoint.Application") is not { } type) return null;
        // PowerPoint runs once per user: an instance already open is the user's own, and is left alone.
        if (!excel && Process.GetProcessesByName("POWERPNT").Length > 0) return null;
        var before = Running(excel);
        try
        {
            dynamic app = Activator.CreateInstance(type)!;
            if (excel)
            {
                app.Visible = false;
                app.DisplayAlerts = false;
            }
            else app.DisplayAlerts = 1; // ppAlertsNone
            return new OfficeCheck(app, excel, Running(excel).Except(before).ToList());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    public OfficeResult Open(byte[] bytes, string extension, int expected)
    {
        if (_app == null) return OfficeResult.NotOpened;
        string path = Path.Combine(_dir, "check" + extension);
        File.WriteAllBytes(path, bytes);
        var logsBefore = RepairLogs();
        dynamic? doc = null;
        try
        {
            int count;
            if (_excel)
            {
                // UpdateLinks 0, ReadOnly, not in the recent files, xlNormalLoad (no repair asked for).
                doc = _app.Workbooks.Open(Filename: path, UpdateLinks: 0, ReadOnly: true, AddToMru: false, CorruptLoad: 0);
                count = (int)doc.Worksheets.Count;
            }
            else
            {
                // ReadOnly, not untitled, no window.
                doc = _app.Presentations.Open(path, -1, 0, 0);
                count = (int)doc.Slides.Count;
            }
            if (RepairLogs().Except(logsBefore).Any()) return OfficeResult.Repaired;
            return count == expected ? OfficeResult.Opened : OfficeResult.CountDiffers;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return OfficeResult.NotOpened;
        }
        finally
        {
            try { if (doc != null) { if (_excel) doc.Close(false); else doc.Close(); } }
            catch (System.Runtime.InteropServices.COMException) { }
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The repair logs Office writes to the temp folder (error123.xml) when it repairs a file.</summary>
    private static HashSet<string> RepairLogs()
    {
        try { return new HashSet<string>(Directory.EnumerateFiles(Path.GetTempPath(), "error*.xml"), StringComparer.OrdinalIgnoreCase); }
        catch (IOException) { return new HashSet<string>(); }
    }

    public void Dispose()
    {
        try { _app?.Quit(); } catch (System.Runtime.InteropServices.COMException) { }
        _app = null;
        // The COM wrappers hold the instance open until they are collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (int id in _started)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.WaitForExit(15_000)) process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
