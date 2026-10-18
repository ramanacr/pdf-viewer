using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Ocr;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Ocr;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Text;
using PdfEngine.Vector.Windows.Create;

namespace PdfViewer.Services;

/// <summary>How new documents are made: page layout, image compression and colour, recognition and PDF/A.</summary>
public sealed record CreatePdfSettings
{
    public PdfImagePageOptions Page { get; init; } = new();
    public ImageConversionOptions Image { get; init; } = new();
    /// <summary>Recognize the text of pages that have none (Windows OCR) and add it as an invisible layer.</summary>
    public bool MakeSearchable { get; init; }
    /// <summary>Convert the result to PDF/A-2b.</summary>
    public bool PdfA { get; init; }
    /// <summary>The paper plain text is set on.</summary>
    public PdfTextLayoutOptions Text { get; init; } = new();
}

/// <summary>What was made.</summary>
public sealed record CreatePdfReport(int Pages, int JpegPassThrough, int RecognizedPages, int Words, bool PdfA, int PdfAIssues);

/// <summary>Progress: what is happening, and how far (0 to 1), or a negative fraction when it cannot be told.</summary>
public readonly record struct CreatePdfProgress(string Message, double Fraction);

/// <summary>
/// Create PDF, all of it on this machine: images to pages, Office files through Office's own
/// export, plain text through our layout, files combined into one with an outline item each,
/// then (when asked) the scanned pages made searchable with Windows OCR and the whole converted
/// to PDF/A-2b. The result is written beside the target and moved into place when complete, so a
/// stopped or failed run leaves no file behind. Nothing here sends anything anywhere.
/// </summary>
public sealed class CreatePdfService
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".bmp", ".dib", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".ico",
    };

    /// <summary>Recognizes scanned pages; null when Windows OCR has no language installed.</summary>
    public IOcrEngine? Ocr { get; init; }

    public IOfficeConverter Office { get; init; } = new OfficeConverter();

    /// <summary>Asks for the password of a protected PDF being combined (by file name); null skips asking.</summary>
    public Func<string, Task<string?>>? RequestPassword { get; init; }

    public string Producer { get; init; } = "PDF Viewer Native";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsText(string path) => string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file Create PDF can take: an image, a PDF, plain text, or a file Office opens.</summary>
    public bool CanTake(string path) => IsImage(path) || IsPdf(path) || IsText(path) || Office.AppFor(Path.GetExtension(path)) != null;

    private PdfCreateInfo Info(string? title) => new() { Title = title, Creator = "PDF Viewer Native (Create PDF)", Producer = Producer };

    // ------------------------------------------------------------------ the three ways in

    public async Task<CreatePdfReport> FromImagesAsync(IReadOnlyList<ImageInput> images, string target, CreatePdfSettings settings,
        IProgress<CreatePdfProgress>? progress = null, CancellationToken ct = default)
    {
        var options = new ImagesToPdfOptions { Page = settings.Page, Image = settings.Image, Info = Info(Path.GetFileNameWithoutExtension(target)) };
        var imageProgress = progress == null ? null : new Progress<ImagesToPdfProgress>(p =>
            progress.Report(new CreatePdfProgress($"Adding image {Math.Min(p.Images + 1, p.TotalImages)} of {p.TotalImages} (page {p.Pages})...",
                0.9 * p.Images / Math.Max(1, p.TotalImages) * (settings.MakeSearchable || settings.PdfA ? 0.5 : 1))));
        if (!settings.MakeSearchable && !settings.PdfA)
        {
            // Straight to the file, a page at a time: nothing is held in memory.
            ImagesToPdfResult? written = null;
            await WriteAtomicallyAsync(target, stream => Task.Run(() => written = ImagesToPdf.Write(images, stream, options, imageProgress, ct), ct), ct);
            return new CreatePdfReport(written!.Pages, written.JpegPassThrough, 0, 0, false, 0);
        }
        using var ms = new MemoryStream();
        var result = await Task.Run(() => ImagesToPdf.Write(images, ms, options, imageProgress, ct), ct);
        var (bytes, report) = await FinishAsync(ms.ToArray(), settings, progress, ct);
        await WriteAtomicallyAsync(target, s => s.WriteAsync(bytes, ct).AsTask(), ct);
        return report with { JpegPassThrough = result.JpegPassThrough };
    }

    /// <summary>One file: an image, plain text, or anything Office opens.</summary>
    public async Task<CreatePdfReport> FromFileAsync(string source, string target, CreatePdfSettings settings,
        IProgress<CreatePdfProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsImage(source)) return await FromImagesAsync(new[] { ImageInput.FromFile(source) }, target, settings, progress, ct);
        byte[] pdf = await ConvertToPdfAsync(source, settings, progress, ct);
        var (bytes, report) = await FinishAsync(pdf, settings, progress, ct);
        await WriteAtomicallyAsync(target, s => s.WriteAsync(bytes, ct).AsTask(), ct);
        return report;
    }

    /// <summary>
    /// PDFs, images and Office files into one document, in the order given, each source an
    /// outline item named after its file (holding the source's own outline).
    /// </summary>
    public async Task<CreatePdfReport> CombineAsync(IReadOnlyList<string> sources, string target, CreatePdfSettings settings,
        IProgress<CreatePdfProgress>? progress = null, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Choose at least one file.", nameof(sources));
        var docs = new List<PdfVectorDocument>();
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string source = sources[i], name = Path.GetFileName(source);
                progress?.Report(new CreatePdfProgress($"Reading {name} ({i + 1} of {sources.Count})...", 0.6 * i / sources.Count));
                if (IsPdf(source))
                {
                    docs.Add(await OpenPdfAsync(source, ct));
                    continue;
                }
                byte[] pdf = IsImage(source)
                    ? await Task.Run(() => ImagesToPdf.ToBytes(new[] { ImageInput.FromFile(source) }, new ImagesToPdfOptions { Page = settings.Page, Image = settings.Image, Info = Info(null) }, ct), ct)
                    : await ConvertToPdfAsync(source, settings, null, ct);
                docs.Add(await PdfVectorDocument.OpenAsync(pdf, name, cancellationToken: ct));
            }
            progress?.Report(new CreatePdfProgress("Combining...", 0.65));
            var options = new PdfAssemblyOptions
            {
                SourceTitles = sources.Select(Path.GetFileNameWithoutExtension).ToList(),
                Info = Info(Path.GetFileNameWithoutExtension(target)),
            };
            byte[] combined = await Task.Run(() => PdfPageAssembler.Merge(docs, options), ct);
            foreach (var d in docs) d.Dispose();
            docs.Clear();
            var (bytes, report) = await FinishAsync(combined, settings, progress, ct);
            await WriteAtomicallyAsync(target, s => s.WriteAsync(bytes, ct).AsTask(), ct);
            return report;
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>
    /// A non-image file as PDF bytes: Office's export when the application is installed, our own
    /// layout for plain text without Word; anything else needs its Office application.
    /// </summary>
    private async Task<byte[]> ConvertToPdfAsync(string source, CreatePdfSettings settings, IProgress<CreatePdfProgress>? progress, CancellationToken ct)
    {
        string name = Path.GetFileName(source);
        var app = Office.AppFor(Path.GetExtension(source));
        if (app is { } a && Office.IsInstalled(a))
        {
            progress?.Report(new CreatePdfProgress($"Converting {name} with Microsoft {a}...", -1));
            string dir = Path.Combine(Path.GetTempPath(), "PdfViewerCreate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string pdf = Path.Combine(dir, "converted.pdf");
                await Office.ConvertAsync(source, pdf, ct);
                return await File.ReadAllBytesAsync(pdf, ct);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        if (IsText(source))
        {
            progress?.Report(new CreatePdfProgress($"Laying out {name}...", -1));
            byte[] raw = await File.ReadAllBytesAsync(source, ct);
            return await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                PdfTextDocument.Write(PdfTextDocument.Decode(raw), ms, settings.Text, Info(Path.GetFileNameWithoutExtension(source)), cancellationToken: ct);
                return ms.ToArray();
            }, ct);
        }
        if (app is { } needed)
            throw new OfficeConversionException($"'{name}' needs Microsoft {needed} to be converted, and it is not installed on this computer. " +
                "Open the file in an application that can print it and choose \"Microsoft Print to PDF\", or save it as PDF there.");
        throw new OfficeConversionException($"'{name}' is not a kind of file Create PDF can convert.");
    }

    private async Task<PdfVectorDocument> OpenPdfAsync(string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            return await PdfVectorDocument.OpenAsync(bytes, path, cancellationToken: ct);
        }
        catch (PdfEncryptedDocumentException) when (RequestPassword != null)
        {
            string? password = await RequestPassword(Path.GetFileName(path));
            if (string.IsNullOrEmpty(password)) throw new OperationCanceledException();
            return await PdfVectorDocument.OpenAsync(bytes, path, password: password, cancellationToken: ct);
        }
    }

    /// <summary>Recognition of the pages without text, then PDF/A, as asked.</summary>
    private async Task<(byte[] Bytes, CreatePdfReport Report)> FinishAsync(byte[] pdf, CreatePdfSettings settings,
        IProgress<CreatePdfProgress>? progress, CancellationToken ct)
    {
        int pages, recognizedPages = 0, words = 0, issues = 0;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf, cancellationToken: ct)) pages = doc.PageCount;
        if (settings.MakeSearchable)
        {
            if (Ocr == null) throw new InvalidOperationException("Windows text recognition is not installed. Add an OCR language in Windows Settings > Time & language > Language & region, or create the PDF without Make Searchable.");
            (pdf, recognizedPages, words) = await RecognizeAsync(pdf, Ocr, progress, ct);
        }
        if (settings.PdfA)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CreatePdfProgress("Converting to PDF/A-2b...", 0.95));
            var converted = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(pdf, cancellationToken: ct);
                return PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavour = PdfAFlavour.A2b });
            }, ct);
            pdf = converted.Bytes;
            issues = converted.Report.Issues.Count;
        }
        return (pdf, new CreatePdfReport(pages, 0, recognizedPages, words, settings.PdfA, issues));
    }

    /// <summary>
    /// The Make Searchable path: each page without text is drawn and recognized, and the words are
    /// added as invisible text (<see cref="PdfTextLayerWriter"/>); the update is then folded into a
    /// compact file.
    /// </summary>
    public static async Task<(byte[] Bytes, int Pages, int Words)> RecognizeAsync(byte[] pdf, IOcrEngine ocr,
        IProgress<CreatePdfProgress>? progress, CancellationToken ct)
    {
        var recognized = new Dictionary<int, IReadOnlyList<PdfRecognizedWord>>();
        int words = 0;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf, cancellationToken: ct))
        {
            for (int page = 1; page <= doc.PageCount; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (PdfTextExtractor.LoadPage(doc, page).Text.Trim().Length > 0) continue; // its own text is exact
                progress?.Report(new CreatePdfProgress($"Recognizing text: page {page} of {doc.PageCount}...", 0.5 + 0.45 * (page - 1) / doc.PageCount));
                var result = await ocr.RecognizePageAsync(doc, page, cancellationToken: ct);
                var found = result.Words
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Bounds.Width > 0 && w.Bounds.Height > 0)
                    .Select(w => new PdfRecognizedWord(w.Text, w.Bounds.X, w.Bounds.Y, w.Bounds.Width, w.Bounds.Height))
                    .ToList();
                if (found.Count == 0) continue;
                recognized[page] = found;
                words += found.Count;
            }
        }
        if (recognized.Count == 0) return (pdf, 0, 0);
        byte[] layered = await Task.Run(async () =>
        {
            using var doc = await PdfVectorDocument.OpenAsync(pdf, cancellationToken: ct);
            byte[] update = PdfTextLayerWriter.Apply(doc, pdf, recognized);
            using var updated = await PdfVectorDocument.OpenAsync(update, cancellationToken: ct);
            return PdfFullWriter.Rewrite(updated);
        }, ct);
        return (layered, recognized.Count, words);
    }

    /// <summary>Writes beside the target and moves the file into place once complete; a failure or a stop leaves nothing.</summary>
    public static async Task WriteAtomicallyAsync(string target, Func<Stream, Task> write, CancellationToken ct)
    {
        string full = Path.GetFullPath(target);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, "~" + Path.GetFileNameWithoutExtension(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                await write(stream);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
