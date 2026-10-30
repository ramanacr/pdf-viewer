using System.Globalization;
using System.Xml.Linq;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using PdfEngine.Save;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>Page organisation: in-place edits as incremental updates of the open document; extraction, merging and arranging into new files.</summary>
public sealed class PdfPageOrganizerService : IPdfPageOrganizerService
{
    public ValueTask RotatePageAsync(IPdfDocument document, int pageNumber, PageRotation newRotation, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        if (pageNumber < 1 || pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        cancellationToken.ThrowIfCancellationRequested();
        doc.Apply(u => PdfPageTreeEditor.Rotate(u, pageNumber, (int)newRotation));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeletePageAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        if (pageNumber < 1 || pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (doc.PageCount <= 1) throw new InvalidOperationException("Cannot delete the only remaining page in a document.");
        cancellationToken.ThrowIfCancellationRequested();
        doc.Apply(u => PdfPageTreeEditor.Delete(u, pageNumber));
        return ValueTask.CompletedTask;
    }

    public ValueTask InsertBlankPageAsync(IPdfDocument document, int targetIndex, double widthPoints = 612, double heightPoints = 792, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        int index = Math.Clamp(targetIndex, 0, doc.PageCount);
        doc.Apply(u => PdfPageTreeEditor.InsertBlank(u, index, widthPoints, heightPoints));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Inserts pages of another document before page <paramref name="targetIndex"/> + 1
    /// (FPDF_ImportPages into an open document). The result is written whole, so the open
    /// document becomes that new file in memory.
    /// </summary>
    public ValueTask InsertPagesAsync(IPdfDocument document, int targetIndex, IPdfDocument source, IReadOnlyList<int> sourcePages, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        var src = PdfVectorEngine.Require(source);
        cancellationToken.ThrowIfCancellationRequested();
        int index = Math.Clamp(targetIndex, 0, doc.PageCount);
        var pages = new List<PdfAssemblyPage>();
        for (int i = 1; i <= index; i++) pages.Add(new PdfAssemblyPage(doc.Vector, i));
        pages.AddRange(sourcePages.Select(p => new PdfAssemblyPage(src.Vector, p)));
        for (int i = index + 1; i <= doc.PageCount; i++) pages.Add(new PdfAssemblyPage(doc.Vector, i));
        doc.Replace(PdfPageAssembler.Build(pages, new PdfAssemblyOptions { SourceTitles = null }));
        return ValueTask.CompletedTask;
    }

    public async ValueTask ExtractPagesAsync(IPdfDocument document, IReadOnlyList<int> pageNumbers, string targetPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        if (pageNumbers == null || pageNumbers.Count == 0) throw new ArgumentException("Page numbers cannot be empty.", nameof(pageNumbers));
        var valid = pageNumbers.Where(p => p >= 1 && p <= doc.PageCount).ToList();
        if (valid.Count == 0) throw new PdfException($"Failed to extract pages: none of {string.Join(",", pageNumbers)} is in the document.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = PdfPageAssembler.Extract(doc.Vector, valid);
        await PdfVectorEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MergeDocumentsAsync(IReadOnlyList<string> sourceFiles, string targetPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sourceFiles == null || sourceFiles.Count < 2)
            throw new ArgumentException("At least 2 PDF documents are required to merge.", nameof(sourceFiles));
        var opened = new List<PdfEditableDocument>();
        try
        {
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(sourceFiles[i])) throw new FileNotFoundException($"Source PDF file not found: {sourceFiles[i]}", sourceFiles[i]);
                opened.Add(await PdfEditableDocument.OpenAsync(sourceFiles[i], null, cancellationToken).ConfigureAwait(false));
                progress?.Report((i + 1.0) / (sourceFiles.Count + 1));
            }
            byte[] bytes = PdfPageAssembler.Merge(opened.Select(d => d.Vector).ToList(), new PdfAssemblyOptions
            {
                SourceTitles = sourceFiles.Select(f => (string?)Path.GetFileNameWithoutExtension(f)).ToList(),
            });
            await PdfVectorEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            progress?.Report(1.0);
        }
        finally
        {
            foreach (var d in opened) d.Dispose();
        }
    }

    public async ValueTask SplitDocumentAsync(IPdfDocument document, IReadOnlyList<int> pageNumbersPerSplit, string outputDirectory, string filePrefix, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        Directory.CreateDirectory(outputDirectory);
        int current = 1, part = 1;
        foreach (int count in pageNumbersPerSplit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current > doc.PageCount) break;
            int end = Math.Min(doc.PageCount, current + count - 1);
            await ExtractPagesAsync(doc, Enumerable.Range(current, end - current + 1).ToList(),
                Path.Combine(outputDirectory, $"{filePrefix}_part{part:D3}.pdf"), cancellationToken).ConfigureAwait(false);
            current = end + 1;
            part++;
        }
    }

    public async ValueTask ArrangePagesAsync(IPdfDocument document, IReadOnlyList<PageArrangementEntry> arrangement, string targetPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        if (arrangement == null || arrangement.Count == 0) throw new ArgumentException("A document must keep at least one page.", nameof(arrangement));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        if (!string.IsNullOrEmpty(doc.FilePath) && string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(doc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfException("Arranged pages must be written to a new file; the original is never modified.");
        foreach (var entry in arrangement)
        {
            if (entry.SourcePageNumber < 1 || entry.SourcePageNumber > doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(arrangement), $"Page {entry.SourcePageNumber} is outside the document's {doc.PageCount} pages.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = PdfPageAssembler.Build(arrangement.Select(e => new PdfAssemblyPage(doc.Vector, e.SourcePageNumber, (int)e.Rotation)).ToList());
        await PdfVectorEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }
}
