using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Redaction;

namespace PdfEngine.Vector.Export;

/// <summary>What an export to Word produced.</summary>
public sealed class DocxExportResult
{
    public int Pages { get; init; }
    public int Paragraphs { get; init; }
    public int Headings { get; init; }
    public int Tables { get; init; }
    public int Images { get; init; }
    public int Lists { get; init; }
    public int Sections { get; init; }
    public int Hyperlinks { get; init; }
    public bool HasHeaderOrFooter { get; init; }
    /// <summary>The structure tree (Tagged PDF) was used.</summary>
    public bool UsedTags { get; init; }
    /// <summary>What could not be carried over, without the document's text.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Exports a PDF as an editable Word document (.docx): paragraphs whose text flows, with their
/// fonts, sizes, colours, alignment, indents and spacing; headings, lists, tables, images,
/// links, headers and footers, and multi-column pages as Word section columns. Tagged PDFs are
/// read through their structure tree. The package is written by hand (zip and XML).
/// </summary>
public static class DocxExporter
{
    /// <summary>Writes the .docx to <paramref name="output"/>. Progress reports pages read.</summary>
    public static async Task<DocxExportResult> ExportAsync(PdfVectorDocument document, Stream output, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var hyperlinks = new List<string>();
        var warnings = new List<string>();
        StructureTree? tags = null;
        try { tags = StructureTree.Read(document); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add($"The document's tags could not be read ({ex.GetType().Name}); its layout is used instead.");
        }
        List<int>? pageObjects = null;
        if (tags != null && document.XrefTable.Trailer is { } trailer && document.Resolver.Resolve(trailer["Root"]) is PdfDictionary root)
        {
            pageObjects = PdfRedactor.PageObjectNumbers(root, document.Resolver);
            if (pageObjects.Count != document.PageCount) pageObjects = null;
        }

        using var package = new DocxPackage(output);
        var reader = new PageReader(document, pageObjects != null ? tags : null, pageObjects, package, hyperlinks);
        var pages = new List<DocPage>(document.PageCount);
        for (int n = 1; n <= document.PageCount; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(await reader.ReadAsync(n, cancellationToken).ConfigureAwait(false));
            progress?.Report(n);
        }
        warnings.AddRange(reader.Warnings);
        cancellationToken.ThrowIfCancellationRequested();

        var doc = DocumentLayout.Build(pages, hyperlinks, document.Metadata?.Title);
        doc.FontKinds = reader.FontKinds;
        new DocxWriter(package, doc).Write();
        package.Finish(doc.Title);

        var paragraphs = doc.Sections.SelectMany(s => s.Items).OfType<DocParagraph>().Where(p => !p.IsEmpty).ToList();
        return new DocxExportResult
        {
            Pages = pages.Count,
            Paragraphs = paragraphs.Count,
            Headings = paragraphs.Count(p => p.Heading > 0),
            Tables = doc.Sections.Sum(s => s.Items.Count(i => i is DocTable)),
            Images = package.ImageCount,
            Lists = doc.Lists.Count,
            Sections = doc.Sections.Count,
            Hyperlinks = hyperlinks.Count,
            HasHeaderOrFooter = doc.Header.Count + doc.Footer.Count > 0,
            UsedTags = pageObjects != null && tags != null,
            Warnings = warnings.Distinct().Take(50).ToList(),
        };
    }

    /// <summary>Writes the .docx to a file: complete, or not at all (a cancelled or failed export leaves nothing behind).</summary>
    public static async Task<DocxExportResult> ExportAsync(PdfVectorDocument document, string path, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        string temp = path + ".partial";
        try
        {
            DocxExportResult result;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                result = await ExportAsync(document, stream, progress, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return result;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
