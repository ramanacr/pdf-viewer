using System;
using System.Buffers;
using System.Collections.Generic;
using System.Printing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;

namespace PdfViewer.Services;

/// <summary>What to print, where, and how the pages are laid out on the sheet.</summary>
public sealed record PrintRequest
{
    public required PrintQueue Queue { get; init; }

    /// <summary>The job's settings (copies, collation, orientation, paper), as the dialog left them.</summary>
    public required PrintTicket Ticket { get; init; }

    /// <summary>One-based page numbers, in print order.</summary>
    public required IReadOnlyList<int> Pages { get; init; }

    /// <summary>Extra rotation in degrees (the viewer turns pages a quarter for landscape).</summary>
    public int Rotation { get; init; }

    public PrintScaling Scaling { get; init; } = PrintScaling.Fit;

    /// <summary>Percentage of actual size for <see cref="PrintScaling.Custom"/>.</summary>
    public double ScalePercent { get; init; } = 100;

    /// <summary>Send every page as an image, as Acrobat's "Print as image" does (for drivers that mishandle vectors).</summary>
    public bool PrintAsImage { get; init; }

    public string JobName { get; init; } = "Document";

    /// <summary>Tests only: the vector job's output goes to this file instead of the printer's port.</summary>
    public string? OutputPath { get; init; }
}

/// <summary>How a job was printed.</summary>
public enum PrintMethod
{
    /// <summary>Direct2D's print control: pages as vectors (some pages may be images).</summary>
    Vector,
    /// <summary>Page images through WPF's XPS writer.</summary>
    Image,
}

public sealed record PrintOutcome(PrintMethod Method, int VectorPages, int ImagePages, string? Note = null);

public readonly record struct PrintProgress(int Done, int Total, PrintMethod Method);

/// <summary>
/// Prints a document the way the screen draws it: every page the vector engine renders is replayed
/// by the Direct2D renderer into Direct2D's print control, so text and paths reach the printer as
/// themselves; content the engine cannot reproduce exactly prints as the engine draws it. The pages
/// go out on a background thread; the caller only waits.
/// </summary>
/// <remarks>
/// Page images, drawn by the same renderer as the screen, remain the path for documents that allow
/// only low-quality printing (150 dpi, ISO 32000-2 Table 22 bit 12), for "Print as image", for
/// pages that fail to print as vectors, and for printers the print control cannot drive.
/// </remarks>
public static class DocumentPrinter
{
    /// <summary>Resolution of whatever is rasterized in a vector job, and of image pages.</summary>
    public const int PrintDpi = 300;

    /// <summary>Documents that allow only low-quality printing are printed at this resolution.</summary>
    public const int LowQualityDpi = 150;

    /// <summary>The method a job will use before any printer is involved.</summary>
    public static PrintMethod ChooseMethod(IPdfDocumentService service, bool printAsImage) =>
        printAsImage || !service.Permissions.CanPrintHighQuality ? PrintMethod.Image : PrintMethod.Vector;

    /// <summary>One line for the print preview: how the pages will reach the printer.</summary>
    public static string DescribeMethod(IPdfDocumentService service, bool printAsImage)
    {
        if (!service.Permissions.CanPrintHighQuality)
            return $"Prints as images at {LowQualityDpi} dpi: the document allows only low-quality printing.";
        if (printAsImage)
            return $"Prints as images at {PrintDpi} dpi.";
        return "Prints as vectors: text and graphics stay sharp at any printer resolution.";
    }

    /// <summary>
    /// The sheet in the ticket's orientation and the part of it the printer can reach, in DIPs.
    /// Falls back to US Letter without margins when the driver reports nothing.
    /// </summary>
    public static (double Width, double Height, PrintRect Printable) SheetGeometry(PrintQueue queue, PrintTicket ticket)
    {
        double width = 816, height = 1056;
        PrintRect? printable = null;
        try
        {
            var caps = queue.GetPrintCapabilities(ticket);
            if (caps.OrientedPageMediaWidth is > 0 && caps.OrientedPageMediaHeight is > 0)
            {
                width = caps.OrientedPageMediaWidth.Value;
                height = caps.OrientedPageMediaHeight.Value;
            }
            else if (ticket.PageMediaSize?.Width is > 0 && ticket.PageMediaSize.Height is > 0)
            {
                bool landscape = ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;
                width = landscape ? ticket.PageMediaSize.Height.Value : ticket.PageMediaSize.Width.Value;
                height = landscape ? ticket.PageMediaSize.Width.Value : ticket.PageMediaSize.Height.Value;
            }
            if (caps.PageImageableArea is { ExtentWidth: > 0, ExtentHeight: > 0 } area)
                printable = PrintLayout.OrientPrintableArea(width, height, new PrintRect(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight));
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            // Spooler or driver unavailable: the defaults stand.
        }
        return (width, height, printable ?? new PrintRect(0, 0, width, height));
    }

    /// <summary>
    /// Prints <paramref name="request"/>. Call from the UI thread: vector pages are recorded on a
    /// background thread, and image jobs are written asynchronously by WPF, a page at a time.
    /// </summary>
    public static async Task<PrintOutcome> PrintAsync(IPdfDocumentService service, PrintRequest request,
        IProgress<PrintProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        var (sheetWidth, sheetHeight, printable) = SheetGeometry(request.Queue, request.Ticket);
        if (ChooseMethod(service, request.PrintAsImage) == PrintMethod.Vector)
        {
            byte[] ticket;
            using (var stream = request.Ticket.GetXmlStream()) ticket = stream.ToArray();
            var job = new VectorJob(service, request, request.Queue.FullName, ticket, sheetWidth, sheetHeight, printable, progress);
            string note;
            try
            {
                if (await job.RunAsync(ct) is { } outcome)
                    return outcome;
                // A job redirected to a file must never fall through to the printer's port.
                if (request.OutputPath != null)
                    throw new InvalidOperationException($"Direct2D cannot print to {request.Queue.FullName}.");
                note = "The printer does not take vector print jobs; the pages were printed as images.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException && request.OutputPath == null)
            {
                // A failed job was cancelled whole, so nothing printed twice.
                note = $"Vector printing failed ({ex.Message}); the pages were printed as images.";
            }
            return await PrintImagesAsync(service, request, sheetWidth, sheetHeight, printable, progress, ct) with { Note = note };
        }
        if (request.OutputPath != null)
            throw new NotSupportedException("Only vector jobs can be redirected to a file.");
        return await PrintImagesAsync(service, request, sheetWidth, sheetHeight, printable, progress, ct);
    }

    /// <summary>Page images (the vector engine's raster) through WPF's XPS writer (asynchronous, cancellable).</summary>
    private static Task<PrintOutcome> PrintImagesAsync(IPdfDocumentService service, PrintRequest request,
        double sheetWidth, double sheetHeight, PrintRect printable, IProgress<PrintProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var paginator = new PageImagePaginator(service, request.Pages, sheetWidth, sheetHeight, printable, request.Scaling, request.ScalePercent, request.Rotation);
        var writer = PrintQueue.CreateXpsDocumentWriter(request.Queue);
        var done = new TaskCompletionSource<PrintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => writer.CancelAsync());
        writer.WritingProgressChanged += (_, e) =>
        {
            if (e.WritingLevel == WritingProgressChangeLevel.FixedPageWritingProgress)
                progress?.Report(new PrintProgress(Math.Min(e.Number, request.Pages.Count), request.Pages.Count, PrintMethod.Image));
        };
        writer.WritingCompleted += (_, e) =>
        {
            registration.Dispose();
            if (e.Cancelled) done.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));
            else if (e.Error != null) done.TrySetException(e.Error);
            else done.TrySetResult(new PrintOutcome(PrintMethod.Image, 0, request.Pages.Count));
        };
        writer.WriteAsync(paginator, request.Ticket);
        return done.Task;
    }

    /// <summary>A vector job on its own thread (the spooler's COM objects want a multithreaded apartment).</summary>
    private sealed class VectorJob
    {
        private readonly IPdfDocumentService _service;
        private readonly PrintRequest _request;
        private readonly string _printer;
        private readonly byte[] _ticket;
        private readonly double _sheetWidth, _sheetHeight;
        private readonly PrintRect _printable;
        private readonly IProgress<PrintProgress>? _progress;

        public VectorJob(IPdfDocumentService service, PrintRequest request, string printer, byte[] ticket,
            double sheetWidth, double sheetHeight, PrintRect printable, IProgress<PrintProgress>? progress)
        {
            _service = service;
            _request = request;
            _printer = printer;
            _ticket = ticket;
            _sheetWidth = sheetWidth;
            _sheetHeight = sheetHeight;
            _printable = printable;
            _progress = progress;
        }

        /// <summary>The outcome, or null when no print control could be created (nothing was printed).</summary>
        public Task<PrintOutcome?> RunAsync(CancellationToken ct)
        {
            var done = new TaskCompletionSource<PrintOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    done.TrySetResult(Run(ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    done.TrySetCanceled(ct);
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "Vector printing",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return done.Task;
        }

        private PrintOutcome? Run(CancellationToken ct)
        {
            Direct2DPrintJob job;
            try
            {
                job = Direct2DPrintJob.Start(_printer, _request.JobName, _ticket, _request.OutputPath, PrintDpi);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                return null; // XPS-incapable driver, spooler refusal or no Direct2D device
            }

            using (job)
            {
                var hybrid = _service as HybridVectorDocumentService;
                int vector = 0, images = 0;
                var rotation = (PageRotation)PrintLayout.NormalizeRotation(_request.Rotation);
                for (int i = 0; i < _request.Pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int page = _request.Pages[i];
                    _progress?.Report(new PrintProgress(i, _request.Pages.Count, PrintMethod.Vector));
                    var list = hybrid?.GetPrintableDisplayListAsync(page, ct).GetAwaiter().GetResult();
                    if (list != null && TryAddVectorPage(job, hybrid!, list, rotation, ct) is { } kind)
                    {
                        if (kind != PrintedPageKind.Raster) vector++;
                        else images++;
                        continue;
                    }
                    AddImagePage(job, page);
                    images++;
                }
                ct.ThrowIfCancellationRequested();
                job.Close();
                _progress?.Report(new PrintProgress(_request.Pages.Count, _request.Pages.Count, PrintMethod.Vector));
                return new PrintOutcome(PrintMethod.Vector, vector, images);
            }
        }

        /// <summary>How the page was added, or null when it is printed as an image instead.</summary>
        private PrintedPageKind? TryAddVectorPage(Direct2DPrintJob job, HybridVectorDocumentService hybrid, PdfEngine.Vector.IPdfDisplayList list,
            PageRotation rotation, CancellationToken ct)
        {
            var crop = list.CropBox.IsEmpty ? new PdfEngine.Geometry.PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
            var (w, h) = PrintLayout.RotatedSize(crop.Width, crop.Height, list.RotationDegrees + (int)rotation);
            var destination = PrintLayout.Place(w, h, _printable, _request.Scaling, _request.ScalePercent);
            try
            {
                // No fallback provider: regions the engine cannot reproduce exactly print as it draws them.
                return job.AddPageAsync(list, rotation, destination, _sheetWidth, _sheetHeight, null, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !hybrid.IsStrictVector)
            {
                return null; // the page prints as an image of the screen rendering
            }
        }

        /// <summary>The page as an image at print resolution, drawn as on screen (the vector engine; PDFium only in PDFium mode).</summary>
        private void AddImagePage(Direct2DPrintJob job, int page)
        {
            var bitmap = _service.RenderPage(page, PrintDpi, _request.Rotation);
            if (bitmap == null)
                throw new InvalidOperationException($"Page {page} could not be rendered for printing.");
            using var image = ToRenderedPage(bitmap, page);
            var destination = PrintLayout.Place(image.WidthPixels * 72.0 / PrintDpi, image.HeightPixels * 72.0 / PrintDpi,
                _printable, _request.Scaling, _request.ScalePercent);
            job.AddImagePage(image, destination, _sheetWidth, _sheetHeight);
        }
    }

    /// <summary>Premultiplied BGRA pixels of a bitmap, as the print job takes them.</summary>
    internal static RenderedPage ToRenderedPage(BitmapSource bitmap, int pageNumber)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Pbgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        int stride = source.PixelWidth * 4;
        var owner = new ArrayOwner(stride * source.PixelHeight);
        source.CopyPixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), owner.Array, stride, 0);
        return new RenderedPage(pageNumber, source.PixelWidth, source.PixelHeight, stride, bitmap.DpiX, PageRotation.Rotate0, owner);
    }

    private sealed class ArrayOwner : IMemoryOwner<byte>
    {
        public byte[] Array { get; }
        public ArrayOwner(int length) => Array = new byte[length];
        public Memory<byte> Memory => Array;
        public void Dispose() { }
    }
}
