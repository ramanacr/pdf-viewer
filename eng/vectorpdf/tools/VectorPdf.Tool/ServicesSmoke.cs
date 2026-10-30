using System.IO;
using System.Diagnostics;
using System.Text.Json;
using PdfEngine.Forms;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Safety;
using PdfEngine.Vector.Services;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf services &lt;dir&gt; [--limit N] [--clean N] [--out report.jsonl]
///
/// The M10 native services against PDFium's adapters on a corpus. Forms: on every file with
/// widgets, the widget list PdfiumFormService reads (count, page, rectangle, partial name, own
/// value, check state) against PdfFormService. Safety: the PdfiumSafetyInspector report
/// against PdfSafetyInspector, kind by kind (the native script count may only be higher).
/// Every Nth file is sanitized natively and the copy checked by PDFium's inspector (clean, same
/// page count). Field values and names are compared, never written; the report holds file names,
/// counts and kinds only.
/// </summary>
public static class ServicesSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        int cleanEvery = int.TryParse(Program.Option(args, "--clean"), out int c) ? c : 10;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var report = outPath != null ? new StreamWriter(outPath) : null;
        string scratch = Path.Combine(Path.GetTempPath(), "vectorpdf-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        int opened = 0, skipped = 0, errors = 0;
        int formFiles = 0, formAgree = 0; long widgets = 0, widgetsAgree = 0;
        int safetyFiles = 0, safetyAgree = 0;
        var kindAgree = new Dictionary<DocumentRiskKind, (int Files, int Agree)>();
        int cleaned = 0, cleanAgree = 0;
        var examples = new List<string>();
        var clock = Stopwatch.StartNew();
        using var engine = new PdfiumEngine();
        var pdfiumInspector = new PdfiumSafetyInspector();

        int index = 0;
        foreach (var file in files)
        {
            index++;
            string name = Path.GetRelativePath(dir, file);
            var record = new Dictionary<string, object?> { ["file"] = name };
            PdfEngine.Documents.IPdfDocument pdoc;
            try { pdoc = await engine.OpenDocumentAsync(file); }
            catch (Exception) { skipped++; continue; }
            await using (pdoc)
            {
                PdfEditableDocument ndoc;
                try { ndoc = await PdfEditableDocument.OpenAsync(file); }
                catch (Exception) { skipped++; continue; }
                await using (ndoc)
                {
                    opened++;
                    try
                    {
                        // Forms.
                        var pf = new List<FormFieldModel>();
                        for (int p = 1; p <= pdoc.PageCount; p++) pf.AddRange(await engine.FormService.GetFormFieldsAsync(pdoc, p));
                        var nf = PdfFormService.ReadAllFields(ndoc.Vector);
                        if (pf.Count > 0 || nf.Count > 0)
                        {
                            formFiles++;
                            int agree = 0;
                            bool countAgree = pf.Count == nf.Count;
                            var reasons = new SortedSet<string>();
                            if (countAgree)
                                for (int i = 0; i < pf.Count; i++)
                                {
                                    // PDFium's adapter divides the unrotated rectangle by the rotated page size from
                                    // the media box corner; the engine normalizes to the unrotated crop box, as the
                                    // viewer draws fields. Rectangles are compared only where both mean the same.
                                    var node = ndoc.Vector.PageTree.Pages[nf[i].PageNumber - 1];
                                    bool comparable = node.RotationDegrees == 0 && node.CropBox.X == 0 && node.CropBox.Y == 0;
                                    if (Differs(pf[i], nf[i], comparable) is { } why) reasons.Add(why); else agree++;
                                }
                            if (reasons.Count > 0) record["widgetDifferences"] = reasons.ToList();
                            widgets += pf.Count;
                            widgetsAgree += agree;
                            bool ok = countAgree && agree == pf.Count;
                            if (ok) formAgree++;
                            else if (examples.Count < 20) examples.Add($"{name}: forms {agree}/{pf.Count} widgets (native {nf.Count}) {string.Join(",", reasons)}");
                            record["widgets"] = pf.Count;
                            record["widgetsAgree"] = agree;
                        }

                        // Safety.
                        var ps = await pdfiumInspector.InspectAsync(pdoc);
                        var ns = PdfSafetyInspector.Inspect(ndoc.Vector);
                        safetyFiles++;
                        // The engine never calls clean what PDFium does not (it may find more).
                        bool safeOk = !(ns.IsClean && !ps.IsClean);
                        foreach (DocumentRiskKind kind in Enum.GetValues<DocumentRiskKind>())
                        {
                            var a = ps.Findings.FirstOrDefault(f => f.Kind == kind);
                            var b = ns.Findings.FirstOrDefault(f => f.Kind == kind);
                            if (a == null && b == null) continue;
                            // Scripts and launch actions: the engine also finds those outside links and the name tree.
                            bool kindOk = kind is DocumentRiskKind.JavaScript or DocumentRiskKind.LaunchAction
                                ? (a == null || (b != null && b.Count >= a.Count))
                                : a != null && b != null && a.Count == b.Count;
                            var (files2, agree2) = kindAgree.GetValueOrDefault(kind);
                            kindAgree[kind] = (files2 + 1, agree2 + (kindOk ? 1 : 0));
                            safeOk &= kindOk;
                            if (!kindOk && examples.Count < 20) examples.Add($"{name}: {kind} pdfium {a?.Count ?? 0} native {b?.Count ?? 0}");
                        }
                        if (safeOk) safetyAgree++;
                        record["safetyAgree"] = safeOk;

                        // Clean copy, checked by PDFium.
                        if (cleanEvery > 0 && index % cleanEvery == 0)
                        {
                            cleaned++;
                            string target = Path.Combine(scratch, index + ".pdf");
                            bool ok;
                            try
                            {
                                var result = await new PdfSanitizer().SanitizeAsync(ndoc, target);
                                await using var written = await engine.OpenDocumentAsync(target);
                                ok = (await pdfiumInspector.InspectAsync(written)).IsClean && written.PageCount == pdoc.PageCount && result.PageCount == pdoc.PageCount;
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                ok = false;
                                record["cleanError"] = ex.GetType().Name;
                            }
                            finally
                            {
                                try { File.Delete(target); } catch (IOException) { }
                            }
                            if (ok) cleanAgree++;
                            else if (examples.Count < 20) examples.Add($"{name}: clean copy");
                            record["cleanCopy"] = ok;
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        errors++;
                        record["error"] = ex.GetType().Name;
                    }
                }
            }
            if (report != null) await report.WriteLineAsync(JsonSerializer.Serialize(record));
            if (index % 200 == 0) Console.Error.WriteLine($"  {index}/{files.Count} files, {clock.Elapsed.TotalSeconds:F0} s");
        }
        try { Directory.Delete(scratch, true); } catch (IOException) { }

        static double Pct(long a, long b) => b > 0 ? Math.Round(100.0 * a / b, 2) : 100;
        var summary = new Dictionary<string, object>
        {
            ["files"] = files.Count,
            ["openedInBoth"] = opened,
            ["skipped"] = skipped,
            ["errors"] = errors,
            ["formFiles"] = formFiles,
            ["formFilesAgreePercent"] = Pct(formAgree, formFiles),
            ["widgets"] = widgets,
            ["widgetsAgreePercent"] = Pct(widgetsAgree, widgets),
            ["safetyFiles"] = safetyFiles,
            ["safetyAgreePercent"] = Pct(safetyAgree, safetyFiles),
            ["safetyKinds"] = kindAgree.ToDictionary(k => k.Key.ToString(), k => (object)new { files = k.Value.Files, agreePercent = Pct(k.Value.Agree, k.Value.Files) }),
            ["cleanCopies"] = cleaned,
            ["cleanCopiesCleanForPdfiumPercent"] = Pct(cleanAgree, cleaned),
            ["examples"] = examples,
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>What differs between a widget as PDFium reads it (its own /T, /V, /AS) and the engine's model of it; null when nothing.</summary>
    private static string? Differs(FormFieldModel p, FormFieldModel n, bool boundsComparable)
    {
        const double tol = 0.01;
        if (p.PageNumber != n.PageNumber) return "page";
        if (boundsComparable && (Math.Abs(p.Bounds.X - n.Bounds.X) > tol || Math.Abs(p.Bounds.Y - n.Bounds.Y) > tol
            || Math.Abs(p.Bounds.Width - n.Bounds.Width) > tol || Math.Abs(p.Bounds.Height - n.Bounds.Height) > tol)) return "bounds";
        if (p.Name.Length > 0 && n.Name != p.Name && !n.Name.EndsWith("." + p.Name, StringComparison.Ordinal)) return "name";
        if (p.Value.Length > 0 && p.Type is FormFieldType.TextField && p.Value != n.Value) return "value";
        if (p.Type == FormFieldType.CheckBox && n.Type is FormFieldType.CheckBox or FormFieldType.RadioButton && p.IsChecked != n.IsChecked) return "checked";
        return null;
    }
}
