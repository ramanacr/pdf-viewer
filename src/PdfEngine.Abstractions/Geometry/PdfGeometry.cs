namespace PdfEngine.Geometry;

/// <summary>
/// Represents an immutable 2D point in normalized or points space.
/// </summary>
public readonly record struct PdfPoint(double X, double Y)
{
    public static readonly PdfPoint Zero = new(0, 0);
}

/// <summary>
/// Represents a 2D size in width and height.
/// </summary>
public readonly record struct PdfSize(double Width, double Height)
{
    public static readonly PdfSize Empty = new(0, 0);
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Represents a normalized bounding rectangle [0.0, 1.0] or physical point bounds.
/// </summary>
public readonly record struct PdfRect(double X, double Y, double Width, double Height)
{
    public static readonly PdfRect Empty = new(0, 0, 0, 0);

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double px, double py) =>
        px >= X && px <= (X + Width) && py >= Y && py <= (Y + Height);

    public bool Contains(PdfPoint point) => Contains(point.X, point.Y);

    public bool IntersectsWith(PdfRect rect) =>
        rect.X < (X + Width) && X < (rect.X + rect.Width) &&
        rect.Y < (Y + Height) && Y < (rect.Y + rect.Height);
}
