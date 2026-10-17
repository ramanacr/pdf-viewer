using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PdfEngine.Vector.Direct2D;

namespace PdfViewer.Services;

/// <summary>
/// Prints pages as images rendered through <see cref="IPdfDocumentService"/> (the vector engine's
/// Direct2D raster in the shipped viewer): the path for "Print as image", for documents that
/// allow only low-quality printing, and for printers Direct2D's print control cannot drive (see
/// <see cref="DocumentPrinter"/>).
/// </summary>
public class PageImagePaginator : DocumentPaginator
{
    private readonly IPdfDocumentService _service;
    private readonly IReadOnlyList<int> _pages;
    private readonly Size _pageSize;
    private readonly PrintRect _printable;
    private readonly PrintScaling _scaling;
    private readonly double _scalePercent;
    private readonly int _rotationAngle;

    /// <summary>Pages <paramref name="startPage"/> to <paramref name="endPage"/>, each fitted to a sheet of the given size.</summary>
    public PageImagePaginator(IPdfDocumentService service, int startPage, int endPage, double pageWidth, double pageHeight, int rotationAngle = 0)
        : this(service, Enumerable.Range(startPage, Math.Max(0, endPage - startPage + 1)).ToArray(), pageWidth, pageHeight,
               new PrintRect(0, 0, pageWidth, pageHeight), PrintScaling.Fit, 100, rotationAngle)
    {
    }

    /// <param name="pages">One-based page numbers in print order.</param>
    /// <param name="sheetWidth">Sheet width in DIPs, in the ticket's orientation.</param>
    /// <param name="printable">The part of the sheet the printer reaches, where pages are placed.</param>
    public PageImagePaginator(IPdfDocumentService service, IReadOnlyList<int> pages, double sheetWidth, double sheetHeight,
        PrintRect printable, PrintScaling scaling, double scalePercent, int rotationAngle)
    {
        _service = service;
        _pages = pages;
        _pageSize = new Size(sheetWidth, sheetHeight);
        _printable = printable;
        _scaling = scaling;
        _scalePercent = scalePercent;
        _rotationAngle = rotationAngle;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages.Count;

    public override Size PageSize
    {
        get => _pageSize;
        set { }
    }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= _pages.Count)
            return DocumentPage.Missing;
        // Documents that allow only low-quality printing are printed at 150 dpi (Table 22, bit 12).
        int dpi = _service.Permissions.CanPrintHighQuality ? DocumentPrinter.PrintDpi : DocumentPrinter.LowQualityDpi;
        var bitmap = _service.RenderPage(_pages[pageNumber], dpi: dpi, rotationAngle: _rotationAngle);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (bitmap != null)
            {
                var r = PrintLayout.Place(bitmap.PixelWidth * 72.0 / dpi, bitmap.PixelHeight * 72.0 / dpi, _printable, _scaling, _scalePercent);
                dc.PushClip(new RectangleGeometry(new Rect(_printable.X, _printable.Y, _printable.Width, _printable.Height)));
                dc.DrawImage(bitmap, new Rect(r.X, r.Y, r.Width, r.Height));
                dc.Pop();
            }
        }

        return new DocumentPage(visual, _pageSize, new Rect(_pageSize), new Rect(_pageSize));
    }
}
