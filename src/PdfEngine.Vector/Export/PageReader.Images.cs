using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PdfEngine.Geometry;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>Images: decoded once each, placed in the flow or anchored on the page.</summary>
internal sealed partial class PageReader
{
    private void PlaceImages(DocPage page, List<(PdfImageRef Ref, PdfMatrix M)> images, bool recognized, CancellationToken ct)
    {
        var textBoxes = page.Items.SelectMany(i => i is DocParagraph p ? p.Lines.Select(l => l.Box) : new[] { i.Box }).ToList();
        double pageArea = _width * _height;
        var pageBox = new Box(0, 0, _width, _height);
        foreach (var (image, m) in images)
        {
            ct.ThrowIfCancellationRequested();
            // Corners of the picture: its top row is at v = 1 of the unit square.
            var tl = Show(m.Transform(0, 1));
            var tr = Show(m.Transform(1, 1));
            var bl = Show(m.Transform(0, 0));
            var br = Show(m.Transform(1, 0));
            var box = new Box(Min(tl.X, tr.X, bl.X, br.X), Min(tl.Y, tr.Y, bl.Y, br.Y), Max(tl.X, tr.X, bl.X, br.X), Max(tl.Y, tr.Y, bl.Y, br.Y));
            if (box.W < 3 || box.H < 3 || box.Overlap(pageBox) < 1) continue;
            // A scan under recognized text: the text is the page now.
            if (recognized && box.Overlap(pageBox) > 0.5 * pageArea) continue;

            var img = new DocImage();
            double ax = tr.X - tl.X, ay = tr.Y - tl.Y, dx = bl.X - tl.X, dy = bl.Y - tl.Y;
            double across = Math.Sqrt(ax * ax + ay * ay), down = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(ay) < 0.02 * across && Math.Abs(dx) < 0.02 * down)
            {
                img.FlipH = ax < 0;
                img.FlipV = dy < 0;
                img.Width = across;
                img.Height = down;
            }
            else if (Math.Abs(ax) < 0.02 * across && Math.Abs(dy) < 0.02 * down)
            {
                // A quarter turn: the top edge runs down (90) or up (270) the page.
                img.Rotation = ay > 0 ? 90 : 270;
                img.FlipV = ay > 0 ? dx > 0 : dx < 0;
                img.Width = across;
                img.Height = down;
            }
            else
            {
                img.Width = box.W;
                img.Height = box.H;
            }

            // Cut to the page (unturned pictures only; the rest keeps its whole area).
            if (img.Rotation == 0 && (box.L < 0 || box.T < 0 || box.R > _width || box.B > _height))
            {
                double l = Math.Max(0, -box.L) / box.W, t = Math.Max(0, -box.T) / box.H;
                double r = Math.Max(0, box.R - _width) / box.W, b = Math.Max(0, box.B - _height) / box.H;
                (img.CropL, img.CropR) = img.FlipH ? (r, l) : (l, r);
                (img.CropT, img.CropB) = img.FlipV ? (b, t) : (t, b);
                box = new Box(Math.Max(0, box.L), Math.Max(0, box.T), Math.Min(_width, box.R), Math.Min(_height, box.B));
                img.Width = box.W;
                img.Height = box.H;
            }
            img.Box = box;
            if (AddImage(image, ct) is not { } rel) continue;
            img.RelId = rel;

            // Text over it: it is behind the text. Text beside it: the text wraps around it. Otherwise it is in the flow.
            bool under = textBoxes.Any(t => t.Overlap(box) > 0.3 * Math.Max(t.W * t.H, 1));
            // In a table's area (a layout table around a logo): held at its place over the table.
            bool beside = textBoxes.Any(t => t.OverlapY(box) > 0.2 * t.H && t.OverlapX(box) <= 0)
                          || page.Items.OfType<DocTable>().Any(t => t.Box.Overlap(box) > 0.5 * box.W * box.H);
            if (under || img.Rotation != 0 || img.FlipH || img.FlipV)
            {
                img.Anchored = true;
                img.Behind = under;
                page.Floating.Add(img);
            }
            else if (beside)
            {
                img.Anchored = true;
                page.Floating.Add(img);
            }
            else page.Items.Add(new DocParagraph { Image = img, Box = box, Align = PdfTextAlignment.Left });
        }
    }

    private static double Min(params double[] v) => v.Min();
    private static double Max(params double[] v) => v.Max();

    /// <summary>The image's part in the package (decoded once per image), or null when it cannot be decoded.</summary>
    private string? AddImage(PdfImageRef image, CancellationToken ct)
    {
        if (!_package.WantsImages) return null;
        string key = image.Source?.CacheKey ?? image.ImageId;
        if (_imagesByKey.TryGetValue(key, out var existing)) return existing.Length > 0 ? existing : null;
        string rel = string.Empty;
        try
        {
            if (image.Source != null)
            {
                var decoded = image.Source.Decode(ct);
                if (decoded.Format == PdfDecodedImageFormat.Jpeg)
                {
                    // Colours only the viewer's codec can map (a Separation JPEG) cannot be written as they are.
                    if (decoded.MapJpegComponents == null || decoded.JpegIsSelfContained) rel = _package.AddImage(decoded.Data.ToArray(), "jpeg");
                }
                else if (decoded.Data.Length >= decoded.Width * decoded.Height * 4)
                    rel = _package.AddImage(PngEncoder.Encode(decoded.Data.Span, decoded.Width, decoded.Height, decoded.Alpha is { } a ? a.Span : default), "png");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Warnings.Add($"An image could not be decoded ({ex.GetType().Name}).");
        }
        _imagesByKey[key] = rel;
        return rel.Length > 0 ? rel : null;
    }
}
