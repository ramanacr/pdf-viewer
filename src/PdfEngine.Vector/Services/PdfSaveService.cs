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

/// <summary>Save as copy: the current revision as it is (incremental), or written whole (FPDF_SaveAsCopy without FPDF_INCREMENTAL).</summary>
public sealed class PdfSaveService : IPdfSaveService
{
    private readonly IPdfRenderer? _renderer;

    public PdfSaveService(IPdfRenderer? renderer = null) => _renderer = renderer;

    public async ValueTask SaveAsync(IPdfDocument document, string targetPath, PdfEngine.Save.SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        PdfVectorEngine.RefuseOriginal(doc, targetPath, "A copy");
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new PdfEngine.Save.SaveOptions();
        byte[] bytes = options.Mode switch
        {
            SaveMode.Incremental => doc.Bytes,
            SaveMode.Flattened => FlattenedCopy(doc),
            _ => PdfFullWriter.Rewrite(doc.Vector),
        };
        await PdfVectorEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] FlattenedCopy(PdfEditableDocument doc)
    {
        var update = new PdfUpdate(doc.Vector, doc.Bytes);
        var editor = new PdfAnnotationEditor(update);
        editor.FlattenAll();
        editor.Finish();
        return update.Commit(forceFullRewrite: true);
    }

    private static byte[] Png(RenderedPage page)
    {
        int row = page.WidthPixels * 4;
        var src = page.Pixels.Span;
        if (page.Stride == row) return Export.PngEncoder.Encode(src[..(row * page.HeightPixels)], page.WidthPixels, page.HeightPixels);
        var packed = new byte[row * page.HeightPixels];
        for (int y = 0; y < page.HeightPixels; y++) src.Slice(y * page.Stride, row).CopyTo(packed.AsSpan(y * row));
        return Export.PngEncoder.Encode(packed, page.WidthPixels, page.HeightPixels);
    }

    public async ValueTask ExportPagesToImagesAsync(IPdfDocument document, string outputDirectory, string filePrefix, int startPage, int endPage,
        string format = "png", int dpi = 300, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_renderer == null) throw new NotSupportedException("Image export needs a renderer; the native save service was created without one.");
        var doc = PdfVectorEngine.Require(document);
        Directory.CreateDirectory(outputDirectory);
        string ext = (format ?? "png").Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "png" => "png",
            _ => throw new NotSupportedException($"Image export format '{format}' is not supported by the native save service. Supported formats: png."),
        };
        startPage = Math.Clamp(startPage, 1, doc.PageCount);
        endPage = Math.Clamp(endPage, startPage, doc.PageCount);
        for (int p = startPage; p <= endPage; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rendered = await _renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = p, Dpi = dpi, HighQuality = true }, cancellationToken).ConfigureAwait(false);
            string path = Path.Combine(outputDirectory, $"{filePrefix}_page_{p:D4}.{ext}");
            await File.WriteAllBytesAsync(path, Png(rendered), cancellationToken).ConfigureAwait(false);
            progress?.Report((p - startPage + 1.0) / (endPage - startPage + 1));
        }
    }
}
