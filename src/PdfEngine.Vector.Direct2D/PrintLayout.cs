namespace PdfEngine.Vector.Direct2D;

/// <summary>How a page is sized on the sheet.</summary>
public enum PrintScaling
{
    /// <summary>Scaled to fill the printable area, keeping its proportions.</summary>
    Fit,
    /// <summary>At 100 %: one PDF point is 1/72 inch on paper.</summary>
    ActualSize,
    /// <summary>At a given percentage of its actual size.</summary>
    Custom,
}

/// <summary>A rectangle on the sheet, in device-independent pixels (1/96 inch), origin top left.</summary>
public readonly record struct PrintRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Page placement for printing, kept apart from the Direct2D and spooler parts so it can be
/// tested on its own. Sizes are in points for pages and in device-independent pixels for sheets.
/// </summary>
public static class PrintLayout
{
    public const double DipsPerPoint = 96.0 / 72.0;

    /// <summary>A rotation in degrees brought to 0, 90, 180 or 270.</summary>
    public static int NormalizeRotation(int degrees) => (((degrees % 360) + 360) % 360) / 90 * 90;

    /// <summary>Size in points of the page as printed: its crop box turned by <paramref name="rotation"/>.</summary>
    public static (double Width, double Height) RotatedSize(double cropWidth, double cropHeight, int rotation) =>
        NormalizeRotation(rotation) is 90 or 270 ? (cropHeight, cropWidth) : (cropWidth, cropHeight);

    /// <summary>
    /// Where a page of <paramref name="pageWidth"/> × <paramref name="pageHeight"/> points (already
    /// rotated) lands on the sheet: centred in <paramref name="printable"/>, and either scaled to fit
    /// it or drawn at <paramref name="percent"/> of its actual size. A page larger than the area at
    /// actual size is centred too, so the printer clips it evenly on both sides.
    /// </summary>
    public static PrintRect Place(double pageWidth, double pageHeight, PrintRect printable, PrintScaling scaling, double percent = 100)
    {
        if (pageWidth <= 0 || pageHeight <= 0 || printable.Width <= 0 || printable.Height <= 0)
            return new PrintRect(printable.X, printable.Y, 0, 0);
        double w = pageWidth * DipsPerPoint, h = pageHeight * DipsPerPoint;
        double scale = scaling switch
        {
            PrintScaling.Fit => Math.Min(printable.Width / w, printable.Height / h),
            PrintScaling.Custom when percent > 0 => percent / 100.0,
            _ => 1.0,
        };
        w *= scale;
        h *= scale;
        return new PrintRect(printable.X + (printable.Width - w) / 2, printable.Y + (printable.Height - h) / 2, w, h);
    }

    /// <summary>
    /// The printable area in the sheet's orientation. Drivers report it for the portrait sheet; on a
    /// landscape sheet the margins turn with the paper.
    /// </summary>
    public static PrintRect OrientPrintableArea(double sheetWidth, double sheetHeight, PrintRect printable)
    {
        bool sheetLandscape = sheetWidth > sheetHeight, areaLandscape = printable.Width > printable.Height;
        if (sheetLandscape == areaLandscape || printable.Width == printable.Height)
            return printable;
        return new PrintRect(printable.Y, printable.X, printable.Height, printable.Width);
    }
}
