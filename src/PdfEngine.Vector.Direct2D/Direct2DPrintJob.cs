using PdfEngine.Rendering;
using SharpGen.Runtime;
using Vortice.Direct2D1;

namespace PdfEngine.Vector.Direct2D;

/// <summary>How a page went to the printer.</summary>
public enum PrintedPageKind
{
    /// <summary>Paths, text and images as themselves; only effects are rasterized.</summary>
    Vector,
    /// <summary>One image of the page (blend modes read back pixels a print recording does not have).</summary>
    Raster,
}

/// <summary>
/// One print job through Direct2D's print control: pages are replayed by the screen renderer into
/// command lists and handed to the spooler as XPS, so text stays text and paths stay paths.
/// Settings such as copies, collation, orientation and paper come from the job's print ticket.
/// </summary>
/// <remarks>
/// The job owns its own Direct2D device, so printing never waits for the screen. It joins the
/// multithreaded COM apartment on the thread that starts it and leaves it when disposed there, so
/// a dedicated thread suits it best. Disposing a job that was not closed cancels it.
/// </remarks>
public sealed class Direct2DPrintJob : IDisposable
{
    /// <summary>Largest side, in pixels, of a page's recording space (bounds offscreen group surfaces).</summary>
    internal const int MaxRecordingPixels = 16384;

    private readonly Direct2DVectorRenderer _renderer;
    private readonly ComObject _target;
    private readonly IntPtr _targetPointer;
    private readonly ID2D1PrintControl _control;
    private readonly bool _leaveApartment;
    private readonly int _apartmentThread = Environment.CurrentManagedThreadId;
    private bool _finished, _disposed;

    /// <summary>Resolution of what is rasterized: effects, group surfaces, fallback regions, raster pages.</summary>
    public double RasterDpi { get; }

    /// <summary>Pages added so far.</summary>
    public int PageCount { get; private set; }

    private Direct2DPrintJob(Direct2DVectorRenderer renderer, ComObject target, IntPtr targetPointer, ID2D1PrintControl control, double rasterDpi, bool leaveApartment)
    {
        _renderer = renderer;
        _target = target;
        _targetPointer = targetPointer;
        _control = control;
        RasterDpi = rasterDpi;
        _leaveApartment = leaveApartment;
    }

    /// <summary>
    /// Starts a job on <paramref name="printerName"/>. <paramref name="printTicket"/> is the job's
    /// print ticket (PrintTicket XML). With <paramref name="outputPath"/> the spooler writes the
    /// job's output to that file instead of the printer's port (no file dialog for PDF or XPS
    /// writers). Throws when the printer cannot take an XPS job or Direct2D cannot print.
    /// </summary>
    public static Direct2DPrintJob Start(string printerName, string jobName, ReadOnlySpan<byte> printTicket,
        string? outputPath = null, double rasterDpi = 300)
    {
        ArgumentException.ThrowIfNullOrEmpty(printerName);
        bool leave = PrintInterop.EnterApartment();
        IntPtr ticket = IntPtr.Zero, output = IntPtr.Zero, target = IntPtr.Zero;
        Direct2DVectorRenderer? renderer = null;
        ComObject? targetObject = null;
        try
        {
            if (!printTicket.IsEmpty) ticket = PrintInterop.CreateMemoryStream(printTicket);
            if (outputPath != null) output = PrintInterop.CreateFileStream(outputPath);
            target = PrintInterop.CreateDocumentPackageTarget(printerName, string.IsNullOrEmpty(jobName) ? "Document" : jobName, output, ticket);
            targetObject = new ComObject(target); // owns the reference from here on
            renderer = new Direct2DVectorRenderer();
            var control = renderer.Device.CreatePrintControl(renderer.Wic, targetObject, new PrintControlProperties
            {
                FontSubset = PrintFontSubsetMode.Default,
                RasterDPI = (float)rasterDpi,
                ColorSpace = ColorSpace.Srgb,
            });
            return new Direct2DPrintJob(renderer, targetObject, target, control, rasterDpi, leave);
        }
        catch
        {
            if (targetObject != null) PrintInterop.CancelTarget(target);
            targetObject?.Dispose();
            renderer?.Dispose();
            if (leave) PrintInterop.LeaveApartment();
            throw;
        }
        finally
        {
            // The target keeps its own references to the streams.
            if (ticket != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(ticket);
            if (output != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(output);
        }
    }

    /// <summary>
    /// Adds a page replayed from its display list, turned by <paramref name="rotation"/> and placed
    /// at <paramref name="destination"/> on a sheet of <paramref name="sheetWidth"/> ×
    /// <paramref name="sheetHeight"/> DIPs. Fallback regions come from <paramref name="provider"/>.
    /// </summary>
    public async Task<PrintedPageKind> AddPageAsync(IPdfDisplayList list, PageRotation rotation, PrintRect destination,
        double sheetWidth, double sheetHeight, IPdfFallbackProvider? provider, CancellationToken ct = default)
    {
        ThrowIfFinished();
        var request = new RenderRequest { PageNumber = list.PageNumber, Dpi = RecordingDpi(list, rotation), Rotation = rotation };
        ID2D1CommandList sheet;
        PrintedPageKind kind;
        if (Replayer.NeedsBackdrop(list))
        {
            var result = await _renderer.RenderAsync(list, request, provider, ct).ConfigureAwait(false);
            using var page = result.Page;
            sheet = _renderer.RecordPrintImage(page, destination);
            kind = PrintedPageKind.Raster;
        }
        else
        {
            sheet = await _renderer.RecordPrintPageAsync(list, request, provider, destination, ct).ConfigureAwait(false);
            kind = PrintedPageKind.Vector;
        }
        using (sheet)
        {
            ct.ThrowIfCancellationRequested();
            AddSheet(sheet, sheetWidth, sheetHeight);
        }
        return kind;
    }

    /// <summary>Adds a page that is already an image (BGRA, premultiplied), stretched over <paramref name="destination"/>.</summary>
    public void AddImagePage(RenderedPage image, PrintRect destination, double sheetWidth, double sheetHeight)
    {
        ThrowIfFinished();
        using var sheet = _renderer.RecordPrintImage(image, destination);
        AddSheet(sheet, sheetWidth, sheetHeight);
    }

    /// <summary>Ends the job: the spooler prints what was added.</summary>
    public void Close()
    {
        ThrowIfFinished();
        _control.Close();
        _finished = true;
    }

    /// <summary>Abandons the job: nothing added so far is printed.</summary>
    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        PrintInterop.CancelTarget(_targetPointer);
    }

    /// <summary>The resolution a page is recorded at: <see cref="RasterDpi"/>, lowered for huge pages.</summary>
    internal double RecordingDpi(IPdfDisplayList list, PageRotation rotation)
    {
        var crop = list.CropBox.IsEmpty ? new PdfEngine.Geometry.PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        double longest = Math.Max(crop.Width, crop.Height);
        return longest <= 0 ? RasterDpi : Math.Min(RasterDpi, MaxRecordingPixels * 72.0 / longest);
    }

    private void AddSheet(ID2D1CommandList sheet, double sheetWidth, double sheetHeight)
    {
        _control.AddPage(sheet, new Vortice.Mathematics.Size((float)sheetWidth, (float)sheetHeight), null!, out _, out _);
        PageCount++;
    }

    private void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("The print job has already been closed or cancelled.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _control.Dispose();
        _target.Dispose();
        _renderer.Dispose();
        if (_leaveApartment && Environment.CurrentManagedThreadId == _apartmentThread)
            PrintInterop.LeaveApartment();
    }
}
