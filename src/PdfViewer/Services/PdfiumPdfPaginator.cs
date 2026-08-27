using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PdfViewer.Services;

/// <summary>
/// Custom DocumentPaginator for high-quality printing of PDF pages rendered via IPdfDocumentService.
/// </summary>
public class PdfiumPdfPaginator : DocumentPaginator
{
    private readonly IPdfDocumentService _service;
    private readonly int _startPage;
    private readonly int _endPage;
    private readonly Size _pageSize;
    private readonly int _rotationAngle;

    public PdfiumPdfPaginator(IPdfDocumentService service, int startPage, int endPage, double pageWidth, double pageHeight, int rotationAngle = 0)
    {
        _service = service;
        _startPage = startPage;
        _endPage = endPage;
        _pageSize = new Size(pageWidth, pageHeight);
        _rotationAngle = rotationAngle;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => Math.Max(0, _endPage - _startPage + 1);

    public override Size PageSize
    {
        get => _pageSize;
        set { }
    }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        int actualPageNum = _startPage + pageNumber;
        var bitmap = _service.RenderPage(actualPageNum, dpi: 300, rotationAngle: _rotationAngle);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (bitmap != null)
            {
                // Scale to fit printable area preserving aspect ratio
                double scaleX = _pageSize.Width / bitmap.PixelWidth;
                double scaleY = _pageSize.Height / bitmap.PixelHeight;
                double scale = Math.Min(scaleX, scaleY);

                double drawWidth = bitmap.PixelWidth * scale;
                double drawHeight = bitmap.PixelHeight * scale;
                double offsetX = (_pageSize.Width - drawWidth) / 2.0;
                double offsetY = (_pageSize.Height - drawHeight) / 2.0;

                dc.DrawImage(bitmap, new Rect(offsetX, offsetY, drawWidth, drawHeight));
            }
        }

        return new DocumentPage(visual, _pageSize, new Rect(_pageSize), new Rect(_pageSize));
    }
}
