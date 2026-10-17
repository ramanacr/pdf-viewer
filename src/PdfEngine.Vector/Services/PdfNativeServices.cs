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

namespace PdfEngine.Vector.Services;

/// <summary>
/// The document-level services of <see cref="PdfEngine.IPdfEngine"/> without PDFium: opening,
/// annotations, page organisation, saving, embedded files, forms, signatures, redaction, and the
/// document safety inspector and sanitizer, all on <see cref="PdfNativeDocument"/>.
/// </summary>
public sealed class PdfNativeEngine
{
    public PdfNativeEngine(IPdfRenderer? renderer = null)
    {
        AnnotationService = new PdfNativeAnnotationService();
        PageOrganizer = new PdfNativePageOrganizerService();
        SaveService = new PdfNativeSaveService(renderer);
        AttachmentService = new PdfNativeAttachmentService();
        FormService = new PdfNativeFormService();
        SignatureService = new PdfNativeSignatureService();
        RedactionService = new PdfNativeRedactionService();
        SafetyInspector = new PdfNativeSafetyInspector();
        Sanitizer = new PdfNativeSanitizer(SafetyInspector);
    }

    public string EngineName => "Vector engine (native document services)";

    public PdfNativeAnnotationService AnnotationService { get; }
    public PdfNativePageOrganizerService PageOrganizer { get; }
    public PdfNativeSaveService SaveService { get; }
    public PdfNativeAttachmentService AttachmentService { get; }
    public PdfNativeFormService FormService { get; }
    public PdfNativeSignatureService SignatureService { get; }
    public PdfNativeRedactionService RedactionService { get; }
    public PdfNativeSafetyInspector SafetyInspector { get; }
    public PdfNativeSanitizer Sanitizer { get; }

    public async ValueTask<IPdfDocument> OpenDocumentAsync(string filePath, string? password = null, CancellationToken cancellationToken = default) =>
        await PdfNativeDocument.OpenAsync(filePath, password, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IPdfDocument> OpenDocumentAsync(byte[] pdfBytes, string? password = null, CancellationToken cancellationToken = default) =>
        await PdfNativeDocument.OpenAsync(pdfBytes, string.Empty, password, cancellationToken).ConfigureAwait(false);

    internal static PdfNativeDocument Require(IPdfDocument document)
    {
        if (document is not PdfNativeDocument native)
            throw new ArgumentException("Document must be a PdfNativeDocument instance.", nameof(document));
        if (!native.IsOpen) throw new ObjectDisposedException(nameof(document));
        return native;
    }

    internal static void RefuseOriginal(PdfNativeDocument doc, string targetPath, string what)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        if (!string.IsNullOrEmpty(doc.FilePath) &&
            string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(doc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException($"{what} must be written to a new file; the original is never modified.", targetPath);
    }

    internal static async ValueTask WriteAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new PdfSaveException("Failed writing the document to disk.", ex, targetPath);
        }
    }
}

/// <summary>Annotations in the engine's normalized model: 0..1 from the top-left of the unrotated crop box.</summary>
public sealed class PdfNativeAnnotationService : IPdfAnnotationService
{
    public ValueTask<IReadOnlyList<AnnotationModel>> LoadAnnotationsAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        var crop = doc.Vector.PageTree.Pages[pageNumber - 1].CropBox;
        var result = new List<AnnotationModel>();
        foreach (var a in PdfAnnotationReader.Read(doc.Vector, pageNumber))
        {
            if (Map(a.Subtype) is not AnnotationType type) continue;
            var model = new AnnotationModel
            {
                PageNumber = pageNumber,
                Type = type,
                X = (a.Rect.Left - crop.Left) / crop.Width,
                Y = 1 - (a.Rect.Bottom - crop.Top) / crop.Height,
                Width = a.Rect.Width / crop.Width,
                Height = a.Rect.Height / crop.Height,
                Opacity = a.Opacity,
                Contents = a.Contents ?? string.Empty,
                Author = a.Author ?? string.Empty,
                StrokeThickness = a.BorderWidth ?? 2.0,
            };
            if (PdfAnnotation.ToRgb(a.Color) is var (r, g, b)) model.Color = Hex(r, g, b);
            if (PdfTextStrings.ParseDate(a.CreationDate) is DateTime created) model.CreatedAt = created;
            if (PdfTextStrings.ParseDate(a.ModifiedDate) is DateTime modified) model.ModifiedAt = modified;
            foreach (var q in a.QuadPoints)
            {
                double x0 = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), x1 = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
                double y0 = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), y1 = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
                model.QuadPoints.Add(new PdfRect((x0 - crop.Left) / crop.Width, 1 - (y1 - crop.Top) / crop.Height, (x1 - x0) / crop.Width, (y1 - y0) / crop.Height));
            }
            foreach (var stroke in a.InkList)
            {
                model.InkStrokes.Add(new InkStroke
                {
                    Color = model.Color,
                    Thickness = model.StrokeThickness,
                    Points = stroke.Select(p => new PdfPoint((p.X - crop.Left) / crop.Width, 1 - (p.Y - crop.Top) / crop.Height)).ToList(),
                });
            }
            result.Add(model);
        }
        return ValueTask.FromResult<IReadOnlyList<AnnotationModel>>(result);
    }

    /// <summary>Writes the document with <paramref name="annotations"/> added (and, for Flattened, every annotation drawn into the pages).</summary>
    public async ValueTask SaveAnnotatedDocumentAsync(IPdfDocument document, string targetPath, IReadOnlyList<AnnotationModel> annotations,
        PdfEngine.Annotations.AnnotationSaveMode mode, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        PdfNativeEngine.RefuseOriginal(doc, targetPath, "An annotated copy");
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == PdfEngine.Annotations.AnnotationSaveMode.ExportXfdf)
        {
            await ExportXfdfAsync(doc, targetPath, annotations, cancellationToken).ConfigureAwait(false);
            return;
        }
        byte[] bytes = PdfAnnotationEditor.Apply(doc.Vector, doc.Bytes, editor =>
        {
            foreach (var model in annotations)
            {
                if (model.PageNumber < 1 || model.PageNumber > doc.PageCount) continue;
                editor.Add(ToAnnotation(doc, model));
            }
            if (mode == PdfEngine.Annotations.AnnotationSaveMode.Flattened) editor.FlattenAll();
        });
        await PdfNativeEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static PdfAnnotation ToAnnotation(PdfNativeDocument doc, AnnotationModel m)
    {
        var crop = doc.Vector.PageTree.Pages[m.PageNumber - 1].CropBox;
        double X(double nx) => crop.Left + nx * crop.Width;
        double Y(double ny) => crop.Top + (1 - ny) * crop.Height;
        var a = new PdfAnnotation
        {
            PageNumber = m.PageNumber,
            Subtype = Subtype(m.Type),
            Rect = new PdfRect(X(m.X), Y(m.Y + m.Height), m.Width * crop.Width, m.Height * crop.Height),
            Color = ParseHex(m.Color),
            Opacity = Math.Clamp(m.Opacity, 0, 1),
            Contents = string.IsNullOrEmpty(m.Contents) ? null : m.Contents,
            Author = string.IsNullOrEmpty(m.Author) ? null : m.Author,
            CreationDate = PdfTextStrings.FormatDate(m.CreatedAt),
            ModifiedDate = PdfTextStrings.FormatDate(m.ModifiedAt),
            BorderWidth = m.StrokeThickness > 0 ? m.StrokeThickness : null,
        };
        if (m.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut)
        {
            var boxes = m.QuadPoints.Count > 0 ? m.QuadPoints : new List<PdfRect> { m.Bounds };
            foreach (var q in boxes)
                a.QuadPoints.Add(new[] { X(q.X), Y(q.Y), X(q.X + q.Width), Y(q.Y), X(q.X), Y(q.Y + q.Height), X(q.X + q.Width), Y(q.Y + q.Height) });
        }
        if (m.Type == AnnotationType.Ink)
            foreach (var s in m.InkStrokes.Where(s => s.Points.Count > 1))
                a.InkList.Add(s.Points.Select(p => new PdfPoint(X(p.X), Y(p.Y))).ToList());
        if (m.Type == AnnotationType.FreeText)
            a.DefaultAppearance = $"/Helv {PdfObjects(m.FontSize)} Tf {Rgb(ParseHex(m.FontColor) ?? new[] { 0.0, 0, 0 })} rg";
        return a;
    }

    private static string PdfObjects(double v) => Objects.PdfObjectWriter.FormatReal(Math.Round(v, 2));
    private static string Rgb(double[] c) => string.Join(" ", c.Select(v => Objects.PdfObjectWriter.FormatReal(Math.Round(v, 4))));

    internal static AnnotationType? Map(string subtype) => subtype switch
    {
        "Highlight" => AnnotationType.Highlight,
        "Underline" => AnnotationType.Underline,
        "StrikeOut" => AnnotationType.StrikeOut,
        "Text" => AnnotationType.Note,
        "FreeText" => AnnotationType.FreeText,
        "Square" => AnnotationType.Rectangle,
        "Circle" => AnnotationType.Ellipse,
        "Ink" => AnnotationType.Ink,
        "Stamp" => AnnotationType.Stamp,
        "Redact" => AnnotationType.Redaction,
        _ => null,
    };

    internal static string Subtype(AnnotationType type) => type switch
    {
        AnnotationType.Highlight => "Highlight",
        AnnotationType.Underline => "Underline",
        AnnotationType.StrikeOut => "StrikeOut",
        AnnotationType.Note => "Text",
        AnnotationType.FreeText => "FreeText",
        AnnotationType.Rectangle => "Square",
        AnnotationType.Ellipse => "Circle",
        AnnotationType.Ink => "Ink",
        AnnotationType.Stamp => "Stamp",
        AnnotationType.Redaction => "Redact",
        _ => "Highlight",
    };

    /// <summary>#RRGGBB or #AARRGGBB to RGB components, null when it is neither.</summary>
    public static double[]? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string h = hex.Trim().TrimStart('#');
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        return new[] { ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0 };
    }

    public static string Hex(double r, double g, double b)
    {
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{C(r):X2}{C(g):X2}{C(b):X2}";
    }

    /// <summary>XFDF (ISO 19444-1): rectangles and quadrilaterals in PDF points from the crop box's corner, colours as #RRGGBB.</summary>
    private static async ValueTask ExportXfdfAsync(PdfNativeDocument doc, string targetPath, IReadOnlyList<AnnotationModel> annotations, CancellationToken ct)
    {
        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var elements = new List<XElement>();
        foreach (var m in annotations)
        {
            if (m.PageNumber < 1 || m.PageNumber > doc.PageCount) continue;
            var a = ToAnnotation(doc, m);
            var e = new XElement(ns + Subtype(m.Type).ToLowerInvariant(),
                new XAttribute("page", m.PageNumber - 1),
                new XAttribute("rect", Join(a.Rect.Left, a.Rect.Top, a.Rect.Right, a.Rect.Bottom)),
                new XAttribute("color", Hex(a.Color?[0] ?? 0, a.Color?[1] ?? 0, a.Color?[2] ?? 0)),
                new XAttribute("opacity", m.Opacity.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("title", m.Author ?? string.Empty),
                new XElement(ns + "contents", m.Contents ?? string.Empty));
            if (a.QuadPoints.Count > 0) e.Add(new XAttribute("coords", string.Join(",", a.QuadPoints.Select(q => Join(q)))));
            elements.Add(e);
        }
        var xfdf = new XElement(ns + "xfdf", new XElement(ns + "annots", elements));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await xfdf.SaveAsync(fs, System.Xml.Linq.SaveOptions.None, ct).ConfigureAwait(false);
    }

    private static string Join(params double[] values) => string.Join(",", values.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));
}

/// <summary>Page organisation: in-place edits as incremental updates of the open document; extraction, merging and arranging into new files.</summary>
public sealed class PdfNativePageOrganizerService : IPdfPageOrganizerService
{
    public ValueTask RotatePageAsync(IPdfDocument document, int pageNumber, PageRotation newRotation, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (pageNumber < 1 || pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        cancellationToken.ThrowIfCancellationRequested();
        doc.Apply(u => PdfPageTreeEditor.Rotate(u, pageNumber, (int)newRotation));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeletePageAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (pageNumber < 1 || pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (doc.PageCount <= 1) throw new InvalidOperationException("Cannot delete the only remaining page in a document.");
        cancellationToken.ThrowIfCancellationRequested();
        doc.Apply(u => PdfPageTreeEditor.Delete(u, pageNumber));
        return ValueTask.CompletedTask;
    }

    public ValueTask InsertBlankPageAsync(IPdfDocument document, int targetIndex, double widthPoints = 612, double heightPoints = 792, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
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
        var doc = PdfNativeEngine.Require(document);
        var src = PdfNativeEngine.Require(source);
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
        var doc = PdfNativeEngine.Require(document);
        if (pageNumbers == null || pageNumbers.Count == 0) throw new ArgumentException("Page numbers cannot be empty.", nameof(pageNumbers));
        var valid = pageNumbers.Where(p => p >= 1 && p <= doc.PageCount).ToList();
        if (valid.Count == 0) throw new PdfException($"Failed to extract pages: none of {string.Join(",", pageNumbers)} is in the document.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = PdfPageAssembler.Extract(doc.Vector, valid);
        await PdfNativeEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MergeDocumentsAsync(IReadOnlyList<string> sourceFiles, string targetPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sourceFiles == null || sourceFiles.Count < 2)
            throw new ArgumentException("At least 2 PDF documents are required to merge.", nameof(sourceFiles));
        var opened = new List<PdfNativeDocument>();
        try
        {
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(sourceFiles[i])) throw new FileNotFoundException($"Source PDF file not found: {sourceFiles[i]}", sourceFiles[i]);
                opened.Add(await PdfNativeDocument.OpenAsync(sourceFiles[i], null, cancellationToken).ConfigureAwait(false));
                progress?.Report((i + 1.0) / (sourceFiles.Count + 1));
            }
            byte[] bytes = PdfPageAssembler.Merge(opened.Select(d => d.Vector).ToList(), new PdfAssemblyOptions
            {
                SourceTitles = sourceFiles.Select(f => (string?)Path.GetFileNameWithoutExtension(f)).ToList(),
            });
            await PdfNativeEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            progress?.Report(1.0);
        }
        finally
        {
            foreach (var d in opened) d.Dispose();
        }
    }

    public async ValueTask SplitDocumentAsync(IPdfDocument document, IReadOnlyList<int> pageNumbersPerSplit, string outputDirectory, string filePrefix, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
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
        var doc = PdfNativeEngine.Require(document);
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
        await PdfNativeEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Save as copy: the current revision as it is (incremental), or written whole (FPDF_SaveAsCopy without FPDF_INCREMENTAL).</summary>
public sealed class PdfNativeSaveService : IPdfSaveService
{
    private readonly IPdfRenderer? _renderer;

    public PdfNativeSaveService(IPdfRenderer? renderer = null) => _renderer = renderer;

    public async ValueTask SaveAsync(IPdfDocument document, string targetPath, PdfEngine.Save.SaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        PdfNativeEngine.RefuseOriginal(doc, targetPath, "A copy");
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new PdfEngine.Save.SaveOptions();
        byte[] bytes = options.Mode switch
        {
            SaveMode.Incremental => doc.Bytes,
            SaveMode.Flattened => FlattenedCopy(doc),
            _ => PdfFullWriter.Rewrite(doc.Vector),
        };
        await PdfNativeEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] FlattenedCopy(PdfNativeDocument doc)
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
        var doc = PdfNativeEngine.Require(document);
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

/// <summary>Embedded files: the name tree's, then file attachment annotations'. Extraction writes data only.</summary>
public sealed class PdfNativeAttachmentService : IPdfAttachmentService
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".dll", ".sys",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".jar", ".lnk", ".reg", ".inf", ".scf", ".url", ".chm",
        ".docm", ".xlsm", ".pptm", ".dotm", ".xlam", ".iso", ".img"
    };

    public ValueTask<IReadOnlyList<EmbeddedFileInfo>> GetAttachmentsAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        var list = PdfAttachments.List(doc.Vector).Select(a =>
        {
            string name = string.IsNullOrWhiteSpace(a.Name) ? $"(unnamed file {a.Index + 1})" : a.Name;
            return new EmbeddedFileInfo { Index = a.Index, Name = name, SizeBytes = a.Size, HasExecutableExtension = IsExecutableName(name) };
        }).ToList();
        return ValueTask.FromResult<IReadOnlyList<EmbeddedFileInfo>>(list);
    }

    public async ValueTask<long> ExtractAttachmentAsync(IPdfDocument document, int index, string targetPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload;
        try
        {
            payload = PdfAttachments.Extract(doc.Vector, index);
        }
        catch (Exception ex) when (ex is InvalidDataException or PdfEngine.Vector.Diagnostics.PdfVectorException)
        {
            throw new PdfException($"Embedded file {index + 1} could not be copied out of the document.", ex);
        }
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(targetPath, payload, cancellationToken).ConfigureAwait(false);
        return payload.LongLength;
    }

    /// <summary>Adds a file to the open document (in memory; save to keep it).</summary>
    public ValueTask AddAttachmentAsync(IPdfDocument document, string name, byte[] data, string? description = null, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        doc.Replace(PdfAttachments.Add(doc.Vector, doc.Bytes, name, data, description));
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the file at <paramref name="index"/> from the open document (in memory; save to keep it).</summary>
    public ValueTask DeleteAttachmentAsync(IPdfDocument document, int index, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        doc.Replace(PdfAttachments.Delete(doc.Vector, doc.Bytes, index));
        return ValueTask.CompletedTask;
    }

    /// <summary>The last extension, after Windows strips trailing dots and spaces: "invoice.pdf.exe" is an executable.</summary>
    public static bool IsExecutableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string extension = Path.GetExtension(name.TrimEnd('.', ' '));
        return !string.IsNullOrEmpty(extension) && ExecutableExtensions.Contains(extension);
    }
}
