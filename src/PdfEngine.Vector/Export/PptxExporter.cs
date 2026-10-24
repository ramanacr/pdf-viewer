using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Export;

/// <summary>How an export to PowerPoint is made.</summary>
public sealed class PptxExportOptions
{
    /// <summary>
    /// Draws groups of drawings that are not rectangles or lines (charts, maps, curves) as
    /// pictures, and the page in <see cref="PageAsBackground"/>. Without one those drawings are left out.
    /// </summary>
    public IPdfVectorRenderer? Renderer { get; init; }

    /// <summary>Resolution of those pictures, in dots per inch (72 to 300).</summary>
    public double GraphicsDpi { get; init; } = 150;

    /// <summary>
    /// Each slide's background is a picture of its page without the text, with the text laid
    /// over it as editable text boxes: it looks as the page does, though its drawings, pictures
    /// and tables cannot be edited. Needs <see cref="Renderer"/>.
    /// </summary>
    public bool PageAsBackground { get; init; }
}

/// <summary>What an export to PowerPoint produced.</summary>
public sealed class PptxExportResult
{
    public int Pages { get; init; }
    public int Slides { get; init; }
    public int TextBoxes { get; init; }
    /// <summary>Pictures: the PDF's images, drawings drawn as pictures, and pages drawn as backgrounds.</summary>
    public int Pictures { get; init; }
    /// <summary>Rectangles and lines.</summary>
    public int Shapes { get; init; }
    public int Tables { get; init; }
    /// <summary>The slides' size, in points.</summary>
    public double SlideWidth { get; init; }
    public double SlideHeight { get; init; }
    /// <summary>What could not be carried over, without the document's text.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Exports a PDF as a PowerPoint presentation (.pptx): a slide a page, at the page's size, with
/// its text as editable text boxes where the page has it (fonts, sizes, colours, line spacing,
/// turned text turned), its tables as tables, its images as pictures, rectangles and lines as
/// shapes, and other drawings as pictures drawn by the renderer. The package is written by hand.
/// </summary>
public static class PptxExporter
{
    /// <summary>Writes the .pptx to <paramref name="output"/>. Progress reports pages read.</summary>
    public static async Task<PptxExportResult> ExportAsync(PdfVectorDocument document, Stream output, PptxExportOptions? options = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new PptxExportOptions();
        var warnings = new List<string>();
        bool background = options.PageAsBackground;
        if (background && options.Renderer == null)
        {
            warnings.Add("A picture of the page as the background needs a renderer: the slides are made of shapes instead.");
            background = false;
        }
        double dpi = Math.Clamp(options.GraphicsDpi, 72, 300);
        var hyperlinks = new List<string>();
        using var opc = new OpcPackage(output);
        var media = new PptxMedia(opc, wanted: !background);
        // Over a picture of the page, the drawings and images are in the picture: the reader leaves them be.
        var reader = ExportSource.Reader(document, media, hyperlinks, background ? null : options.Renderer, dpi, warnings, out _);
        var first = document.PageTree.Pages[0].PageSize;
        var writer = new PptxWriter(opc, hyperlinks, first.Width, first.Height);
        for (int n = 1; n <= document.PageCount; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await reader.ReadAsync(n, cancellationToken).ConfigureAwait(false);
            string? picture = null;
            if (background)
            {
                picture = await BackgroundAsync(document, n, options.Renderer!, dpi, opc, n, warnings, cancellationToken).ConfigureAwait(false);
            }
            writer.AddSlide(page, picture, invisibleText: picture != null && page.Recognized);
            progress?.Report(n);
        }
        cancellationToken.ThrowIfCancellationRequested();
        warnings.AddRange(reader.Warnings);
        writer.Finish(document.Metadata?.Title);
        return new PptxExportResult
        {
            Pages = document.PageCount,
            Slides = document.PageCount,
            TextBoxes = writer.TextBoxes,
            Pictures = writer.Pictures,
            Shapes = writer.Shapes,
            Tables = writer.Tables,
            SlideWidth = writer.SlideWidth,
            SlideHeight = writer.SlideHeight,
            Warnings = warnings.Distinct().Take(50).ToList(),
        };
    }

    /// <summary>Writes the .pptx to a file: complete, or not at all (a cancelled or failed export leaves nothing behind).</summary>
    public static Task<PptxExportResult> ExportAsync(PdfVectorDocument document, string path, PptxExportOptions? options = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        ExportSource.ToFileAsync(path, stream => ExportAsync(document, stream, options, progress, cancellationToken));

    /// <summary>A picture of the page without its text (the text goes over it, editable); its path from a slide, or null when it cannot be drawn.</summary>
    private static async Task<string?> BackgroundAsync(PdfVectorDocument document, int pageNumber, IPdfVectorRenderer renderer, double dpi, OpcPackage opc, int index,
        List<string> warnings, CancellationToken ct)
    {
        try
        {
            var list = await document.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
            var commands = list.Commands.Where(c => c is not DrawGlyphRun).ToList();
            var drawings = new PdfDisplayList(list.PageNumber, list.PageSize, list.MediaBox, list.CropBox, list.RotationDegrees, commands, list.Features);
            using var rendered = await renderer.RenderDisplayListAsync(drawings, new RenderRequest { PageNumber = pageNumber, Dpi = dpi, RenderAnnotations = false, RenderForms = false }, null, ct)
                .ConfigureAwait(false);
            int w = rendered.WidthPixels, h = rendered.HeightPixels;
            if (w < 1 || h < 1) return null;
            var pixels = rendered.Pixels.Span;
            byte[] packed;
            if (rendered.Stride == w * 4) packed = pixels[..(w * h * 4)].ToArray();
            else
            {
                packed = new byte[w * h * 4];
                for (int y = 0; y < h; y++) pixels.Slice(y * rendered.Stride, w * 4).CopyTo(packed.AsSpan(y * w * 4));
            }
            string name = $"page{index}.png";
            opc.WriteMedia("ppt/media/" + name, PngEncoder.Encode(packed, w, h));
            return "../media/" + name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            warnings.Add($"Page {pageNumber}: it could not be drawn as the slide's background ({ex.GetType().Name}).");
            return null;
        }
    }
}
