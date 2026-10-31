namespace PdfViewer.Web.Engine.Annotations;

/// <summary>
/// The geometry of a callout (free text with /IT /FreeTextCallout): the line from the spot it points
/// to (its anchor) to its text box, with a knee, as Acrobat draws one, and the /Rect that holds both.
/// The line is laid out on the page as shown (its /Rotate applied), so on a turned page the knee
/// still leaves the box sideways or up and down as the reader sees it.
/// </summary>
public static class ViewerCallout
{
    /// <summary>/IT of a callout.</summary>
    public const string Intent = "FreeTextCallout";

    /// <summary>The ending at the anchor of a new callout (/LE), as Acrobat gives one.</summary>
    public const string DefaultEnding = "OpenArrow";

    /// <summary>The longest straight part of the line next to the box (points of the shown page).</summary>
    public const double KneeLength = 18;

    /// <summary>
    /// The callout line from the anchor (<paramref name="anchorX"/>, <paramref name="anchorY"/>, user
    /// space) to the box <paramref name="box"/>, in user space: the anchor, a knee and the middle of
    /// the box's side that faces the anchor (the side it is farthest past). The knee is
    /// <see cref="KneeLength"/> out from that side, less when the anchor is near; left out when the
    /// anchor is within a point of the side's line, or inside the box (then the line goes to the
    /// nearest side). The anchor is kept exactly as given.
    /// </summary>
    public static double[] Leader(ViewerPageGeometry g, ViewerBox box, double anchorX, double anchorY)
    {
        var a = g.ToShown(anchorX, anchorY);
        var r = g.ToShown(box);
        double left = r.X, right = r.X + r.Width, top = r.Y, bottom = r.Y + r.Height;
        double midX = left + r.Width / 2, midY = top + r.Height / 2;
        double gapX = a.X < left ? left - a.X : a.X > right ? a.X - right : 0;
        double gapY = a.Y < top ? top - a.Y : a.Y > bottom ? a.Y - bottom : 0;
        (double X, double Y) end;
        (double X, double Y)? knee = null;
        if (gapX == 0 && gapY == 0)
        {
            // Inside the box: straight to the nearest side.
            double toLeft = a.X - left, toRight = right - a.X, toTop = a.Y - top, toBottom = bottom - a.Y;
            double least = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
            end = least == toLeft ? (left, midY) : least == toRight ? (right, midY) : least == toTop ? (midX, top) : (midX, bottom);
        }
        else if (gapX >= gapY)
        {
            double side = a.X < left ? left : right, sign = a.X < left ? -1 : 1;
            end = (side, midY);
            double k = Math.Min(KneeLength, gapX / 2);
            if (k >= 1 && Math.Abs(a.Y - midY) >= 1) knee = (side + sign * k, midY);
        }
        else
        {
            double side = a.Y < top ? top : bottom, sign = a.Y < top ? -1 : 1;
            end = (midX, side);
            double k = Math.Min(KneeLength, gapY / 2);
            if (k >= 1 && Math.Abs(a.X - midX) >= 1) knee = (midX, side + sign * k);
        }
        var e = g.ToUser(end.X, end.Y);
        if (knee is not { } kn) return [anchorX, anchorY, e.X, e.Y];
        var k2 = g.ToUser(kn.X, kn.Y);
        return [anchorX, anchorY, k2.X, k2.Y, e.X, e.Y];
    }

    /// <summary>The callout's line laid out again for its box, from its anchor (a callout moved, or its text box resized).</summary>
    public static ViewerAnnotation Relaid(ViewerAnnotation a, ViewerPageGeometry g) =>
        a.Callout.Count >= 2 ? a with { Callout = Leader(g, a.Box, a.Callout[0], a.Callout[1]) } : a;

    /// <summary>
    /// The /Rect of a callout: around its text box and its line, grown by how far the line's width
    /// and its ending reach (<see cref="ViewerAnnotationStore.ShapeMargin(double, string, string)"/>).
    /// </summary>
    public static ViewerBox Outer(ViewerAnnotation a)
    {
        var b = a.Box;
        if (a.Callout.Count < 4) return b;
        var line = ViewerBox.Around(a.Callout, ViewerAnnotationStore.ShapeMargin(Math.Max(a.StrokeWidth, 1), a.StartEnding, "None"));
        return new ViewerBox(Math.Min(b.X1, line.X1), Math.Min(b.Y1, line.Y1), Math.Max(b.X2, line.X2), Math.Max(b.Y2, line.Y2));
    }
}
