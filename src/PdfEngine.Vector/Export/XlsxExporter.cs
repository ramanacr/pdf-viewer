using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Export;

/// <summary>How the tables of an export to Excel are put on sheets.</summary>
public enum XlsxSheetLayout
{
    /// <summary>Each table on a sheet of its own, named "Page 3 Table 1".</summary>
    SheetPerTable,
    /// <summary>Each page's tables on one sheet, named "Page 3".</summary>
    SheetPerPage,
    /// <summary>Every table on one sheet, one under the other.</summary>
    SingleSheet,
}

/// <summary>How an export to Excel is made.</summary>
public sealed class XlsxExportOptions
{
    public XlsxSheetLayout Layout { get; init; } = XlsxSheetLayout.SheetPerTable;

    /// <summary>
    /// The text outside tables comes over too, a paragraph a row, above and below the tables
    /// it is above and below on the page (pages without tables get a sheet of their own).
    /// </summary>
    public bool IncludeText { get; init; } = true;
}

/// <summary>What an export to Excel produced.</summary>
public sealed class XlsxExportResult
{
    public int Pages { get; init; }
    public int Sheets { get; init; }
    public int Tables { get; init; }
    /// <summary>Cells with something in them.</summary>
    public int Cells { get; init; }
    /// <summary>Cells stored as numbers (currency and percent among them) and as dates.</summary>
    public int Numbers { get; init; }
    public int Dates { get; init; }
    public int MergedCells { get; init; }
    /// <summary>Rows of text from outside the tables.</summary>
    public int TextRows { get; init; }
    /// <summary>What could not be carried over, without the document's text.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Exports a PDF's tables as an Excel workbook (.xlsx): the tables the Word export finds (from
/// tags, ruling lines or aligned text) become cells in a grid with their merged cells, column
/// widths, row heights, fonts, borders and shading; numbers, currency, percentages and dates
/// become values Excel can compute with. The package is written by hand (zip and XML).
/// </summary>
public static class XlsxExporter
{
    /// <summary>Writes the .xlsx to <paramref name="output"/>. Progress reports pages read.</summary>
    public static async Task<XlsxExportResult> ExportAsync(PdfVectorDocument document, Stream output, XlsxExportOptions? options = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new XlsxExportOptions();
        var warnings = new List<string>();
        using var opc = new OpcPackage(output);
        // Pictures and drawings have no place in a sheet: they are not decoded or drawn.
        var reader = ExportSource.Reader(document, NoMedia.Instance, new List<string>(), null, 150, warnings, out _);
        var writer = new XlsxWriter(opc);
        var single = new List<DocItem>();
        for (int n = 1; n <= document.PageCount; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await reader.ReadAsync(n, cancellationToken).ConfigureAwait(false);
            var items = Items(page, options.IncludeText);
            int tables = items.Count(i => i is DocTable);
            switch (options.Layout)
            {
                case XlsxSheetLayout.SingleSheet:
                    single.AddRange(items);
                    break;
                case XlsxSheetLayout.SheetPerPage:
                    if (tables > 0 || items.Count > 0) writer.AddSheet($"Page {n}", items);
                    break;
                default:
                    if (tables == 0)
                    {
                        if (items.Count > 0) writer.AddSheet($"Page {n}", items);
                        break;
                    }
                    // The text before a table goes above it; what follows the last table, below that one.
                    var sheets = new List<List<DocItem>>();
                    var pending = new List<DocItem>();
                    foreach (var item in items)
                    {
                        pending.Add(item);
                        if (item is DocTable) { sheets.Add(pending); pending = new List<DocItem>(); }
                    }
                    sheets[^1].AddRange(pending);
                    for (int t = 0; t < sheets.Count; t++) writer.AddSheet($"Page {n} Table {t + 1}", sheets[t]);
                    break;
            }
            progress?.Report(n);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Layout == XlsxSheetLayout.SingleSheet) writer.AddSheet(options.IncludeText ? "Document" : "Tables", single);
        warnings.AddRange(reader.Warnings);
        warnings.AddRange(writer.Warnings);
        writer.Finish(document.Metadata?.Title);
        return new XlsxExportResult
        {
            Pages = document.PageCount,
            Sheets = writer.SheetCount,
            Tables = writer.Tables,
            Cells = writer.Cells,
            Numbers = writer.Numbers,
            Dates = writer.Dates,
            MergedCells = writer.Merged,
            TextRows = writer.TextRows,
            Warnings = warnings.Distinct().Take(50).ToList(),
        };
    }

    /// <summary>Writes the .xlsx to a file: complete, or not at all (a cancelled or failed export leaves nothing behind).</summary>
    public static Task<XlsxExportResult> ExportAsync(PdfVectorDocument document, string path, XlsxExportOptions? options = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        ExportSource.ToFileAsync(path, stream => ExportAsync(document, stream, options, progress, cancellationToken));

    /// <summary>
    /// The page's tables, top to bottom, and (when wanted) its paragraphs in reading order: each
    /// table comes before the first paragraph below its top that it is above.
    /// </summary>
    internal static List<DocItem> Items(DocPage page, bool text)
    {
        var tables = page.Items.OfType<DocTable>().OrderBy(t => t.Box.T).ThenBy(t => t.Box.L).ToList();
        if (!text) return tables.Cast<DocItem>().ToList();
        var items = page.Items.OfType<DocParagraph>().Where(p => p.Image == null && !p.IsEmpty).Cast<DocItem>().ToList();
        items.AddRange(page.TextBoxes.SelectMany(b => b.Paragraphs).Where(p => !p.IsEmpty));
        foreach (var t in tables)
        {
            int at = items.FindIndex(i => i is DocParagraph && i.Box.T >= t.Box.T - 1 && i.Box.OverlapX(t.Box) > 0);
            // Two tables above the same paragraph keep their order: the second goes in after the first.
            if (at < 0) items.Add(t);
            else items.Insert(at, t);
        }
        return items;
    }
}
