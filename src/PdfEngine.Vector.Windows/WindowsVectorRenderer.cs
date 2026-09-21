using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
using PdfEngine.Rendering;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// Hardware-accelerated Windows vector renderer implementing IPdfVectorRenderer.
/// Replays immutable PdfDisplayList draw commands onto high-fidelity rendering targets.
/// </summary>
public sealed class WindowsVectorRenderer : IPdfVectorRenderer
{
    private static readonly ThreadLocal<Dictionary<string, FontFamily>> FontCache = new(() => new());

    public ValueTask<RenderedPage> RenderDisplayListAsync(
        IPdfDisplayList displayList,
        RenderRequest request,
        IPdfFallbackProvider? fallbackProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            var result = RenderCore(displayList, request, fallbackProvider, cancellationToken);
            return ValueTask.FromResult(result);
        }

        var tcs = new TaskCompletionSource<RenderedPage>();
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = RenderCore(displayList, request, fallbackProvider, cancellationToken);
                tcs.SetResult(page);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return new ValueTask<RenderedPage>(tcs.Task);
    }

    private static RenderedPage RenderCore(
        IPdfDisplayList displayList,
        RenderRequest request,
        IPdfFallbackProvider? fallbackProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        double dpi = request.Dpi > 0 ? request.Dpi : 96.0;
        double scale = dpi / 72.0;

        double pageWidth = displayList.PageSize.Width;
        double pageHeight = displayList.PageSize.Height;

        int userRotation = ((int)request.Rotation / 90) % 4;
        if (userRotation < 0) userRotation += 4;

        int pixelW = request.TargetWidthPixels;
        int pixelH = request.TargetHeightPixels;

        if (pixelW <= 0 || pixelH <= 0)
        {
            if (userRotation == 1 || userRotation == 3)
            {
                pixelW = (int)Math.Max(1, Math.Round(pageHeight * scale));
                pixelH = (int)Math.Max(1, Math.Round(pageWidth * scale));
            }
            else
            {
                pixelW = (int)Math.Max(1, Math.Round(pageWidth * scale));
                pixelH = (int)Math.Max(1, Math.Round(pageHeight * scale));
            }
        }

        int stride = pixelW * 4;
        long bufferSize = (long)stride * pixelH;
        var memoryOwner = new ManagedMemoryOwner((int)bufferSize);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Clear background to opaque white
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelW, pixelH));

            // Setup viewport scaling and orientation
            var rootTransformGroup = new TransformGroup();

            // Apply user rotation if requested
            if (userRotation != 0)
            {
                rootTransformGroup.Children.Add(new RotateTransform(userRotation * 90));
                switch (userRotation)
                {
                    case 1: rootTransformGroup.Children.Add(new TranslateTransform(pixelW, 0)); break;
                    case 2: rootTransformGroup.Children.Add(new TranslateTransform(pixelW, pixelH)); break;
                    case 3: rootTransformGroup.Children.Add(new TranslateTransform(0, pixelH)); break;
                }
            }

            // PDF origin is bottom-left; WPF origin is top-left.
            // Invert Y axis to correctly render PDF coordinates
            rootTransformGroup.Children.Add(new ScaleTransform(scale, -scale));
            rootTransformGroup.Children.Add(new TranslateTransform(0, -pageHeight));

            dc.PushTransform(rootTransformGroup);

            // Replay draw commands
            int clipDepth = 0;
            int transformDepth = 0;

            foreach (var cmd in displayList.Commands)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (cmd)
                {
                    case SaveState:
                        // Save state marker
                        break;

                    case RestoreState:
                        if (transformDepth > 0)
                        {
                            dc.Pop();
                            transformDepth--;
                        }
                        if (clipDepth > 0)
                        {
                            dc.Pop();
                            clipDepth--;
                        }
                        break;

                    case ConcatTransform ct:
                        var m = ct.Matrix;
                        var matrix = new Matrix(m.A, m.B, m.C, m.D, m.E, m.F);
                        dc.PushTransform(new MatrixTransform(matrix));
                        transformDepth++;
                        break;

                    case FillPath fp:
                        var fillGeom = ConvertPathToStreamGeometry(fp.Path, fp.Rule);
                        if (fillGeom != null)
                        {
                            var brush = CreateBrush(fp.Paint);
                            dc.DrawGeometry(brush, null, fillGeom);
                        }
                        break;

                    case StrokePath sp:
                        var strokeGeom = ConvertPathToStreamGeometry(sp.Path, PdfFillRule.NonZero);
                        if (strokeGeom != null)
                        {
                            var pen = CreatePen(sp.Stroke, sp.Paint);
                            dc.DrawGeometry(null, pen, strokeGeom);
                        }
                        break;

                    case PushClip pc:
                        var clipGeom = ConvertPathToStreamGeometry(pc.Path, pc.Rule);
                        if (clipGeom != null)
                        {
                            dc.PushClip(clipGeom);
                            clipDepth++;
                        }
                        break;

                    case PopClip:
                        if (clipDepth > 0)
                        {
                            dc.Pop();
                            clipDepth--;
                        }
                        break;

                    case DrawGlyphRun dgr:
                        RenderGlyphRun(dc, dgr.Run, dgr.Paint);
                        break;

                    case DrawImage di:
                        RenderImage(dc, di.Image, di.Transform);
                        break;

                    case DrawFallbackRegion dfr:
                        // Draw fallback region bounds or placeholder
                        var fbRect = dfr.Bounds ?? dfr.Token.Bounds;
                        var fbBrush = new SolidColorBrush(Color.FromArgb(40, 255, 165, 0));
                        fbBrush.Freeze();
                        dc.DrawRectangle(fbBrush, new Pen(Brushes.Orange, 1), new Rect(fbRect.X, fbRect.Y, fbRect.Width, fbRect.Height));
                        break;
                }
            }

            // Unwind any remaining open transforms/clips
            while (transformDepth-- > 0) dc.Pop();
            while (clipDepth-- > 0) dc.Pop();

            // Unwind root viewport transform
            dc.Pop();
        }

        // Render visual to RenderTargetBitmap
        var rtb = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // Copy pixels into memory owner
        rtb.CopyPixels(new Int32Rect(0, 0, pixelW, pixelH), memoryOwner.Buffer, stride, 0);

        return new RenderedPage(
            displayList.PageNumber,
            pixelW,
            pixelH,
            stride,
            dpi,
            request.Rotation,
            memoryOwner);
    }

    private static StreamGeometry? ConvertPathToStreamGeometry(PdfPath path, PdfFillRule rule)
    {
        if (path.Segments.Count == 0)
            return null;

        var geom = new StreamGeometry
        {
            FillRule = rule == PdfFillRule.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero
        };

        using (var ctx = geom.Open())
        {
            bool figureStarted = false;
            Point currentPt = new Point(0, 0);

            foreach (var seg in path.Segments)
            {
                switch (seg)
                {
                    case PdfMoveTo m:
                        if (figureStarted)
                        {
                            // Close current figure if open
                        }
                        currentPt = new Point(m.Point.X, m.Point.Y);
                        ctx.BeginFigure(currentPt, isFilled: true, isClosed: false);
                        figureStarted = true;
                        break;

                    case PdfLineTo l:
                        if (!figureStarted)
                        {
                            ctx.BeginFigure(currentPt, isFilled: true, isClosed: false);
                            figureStarted = true;
                        }
                        currentPt = new Point(l.Point.X, l.Point.Y);
                        ctx.LineTo(currentPt, isStroked: true, isSmoothJoin: false);
                        break;

                    case PdfCubicBezierTo c:
                        if (!figureStarted)
                        {
                            ctx.BeginFigure(currentPt, isFilled: true, isClosed: false);
                            figureStarted = true;
                        }
                        currentPt = new Point(c.EndPoint.X, c.EndPoint.Y);
                        ctx.BezierTo(
                            new Point(c.Control1.X, c.Control1.Y),
                            new Point(c.Control2.X, c.Control2.Y),
                            currentPt,
                            isStroked: true,
                            isSmoothJoin: false);
                        break;

                    case PdfCloseSubpath:
                        if (figureStarted)
                        {
                            figureStarted = false;
                        }
                        break;
                }
            }
        }

        geom.Freeze();
        return geom;
    }

    private static SolidColorBrush CreateBrush(PdfPaint paint)
    {
        byte a = (byte)Math.Clamp((int)(paint.Alpha * 255), 0, 255);
        byte r = (byte)Math.Clamp((int)(paint.Color.R * 255), 0, 255);
        byte g = (byte)Math.Clamp((int)(paint.Color.G * 255), 0, 255);
        byte b = (byte)Math.Clamp((int)(paint.Color.B * 255), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(PdfStroke stroke, PdfPaint paint)
    {
        var brush = CreateBrush(paint);
        var pen = new Pen(brush, Math.Max(0.5, stroke.Width));

        pen.StartLineCap = stroke.Cap switch
        {
            PdfLineCap.Round => PenLineCap.Round,
            PdfLineCap.Square => PenLineCap.Square,
            _ => PenLineCap.Flat
        };
        pen.EndLineCap = pen.StartLineCap;

        pen.LineJoin = stroke.Join switch
        {
            PdfLineJoin.Round => PenLineJoin.Round,
            PdfLineJoin.Bevel => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter
        };

        pen.MiterLimit = stroke.MiterLimit;

        if (stroke.DashArray != null && stroke.DashArray.Count > 0)
        {
            var dashes = new DoubleCollection();
            foreach (var d in stroke.DashArray)
            {
                dashes.Add(Math.Max(0.1, d / stroke.Width));
            }
            pen.DashStyle = new DashStyle(dashes, stroke.DashPhase / stroke.Width);
        }

        pen.Freeze();
        return pen;
    }

    private static void RenderGlyphRun(DrawingContext dc, PdfGlyphRun run, PdfPaint paint)
    {
        var textMatrix = run.TextMatrix;
        // Invert Y in text space because PDF text coordinates are Y-up
        var wpMatrix = new Matrix(textMatrix.A, -textMatrix.B, -textMatrix.C, textMatrix.D, textMatrix.E, textMatrix.F);

        dc.PushTransform(new MatrixTransform(wpMatrix));

        var fontFamily = ResolveFontFamily(run.FontResourceName);
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var brush = CreateBrush(paint);

        foreach (var glyph in run.Glyphs)
        {
            if (string.IsNullOrEmpty(glyph.Unicode) || glyph.Unicode == " ")
                continue;

            var ft = new FormattedText(
                glyph.Unicode,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                run.FontSize,
                brush,
                1.0);

            dc.DrawText(ft, new Point(glyph.OffsetX, -run.FontSize));
        }

        dc.Pop();
    }

    private static void RenderImage(DrawingContext dc, PdfImageRef img, PdfMatrix transform)
    {
        if (img.ImageData.IsEmpty || img.Width <= 0 || img.Height <= 0)
            return;

        try
        {
            BitmapSource bmp;
            if (img.ImageData.Span.Length >= img.Width * img.Height * 3)
            {
                // Raw 24-bit RGB
                int stride = img.Width * 3;
                bmp = BitmapSource.Create(
                    img.Width,
                    img.Height,
                    96,
                    96,
                    PixelFormats.Rgb24,
                    null,
                    img.ImageData.ToArray(),
                    stride);
            }
            else
            {
                // Decode from standard image stream (JPEG, PNG, etc.)
                using var stream = new MemoryStream(img.ImageData.ToArray());
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                bmp = decoder.Frames[0];
            }

            dc.PushTransform(new MatrixTransform(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F));
            dc.DrawImage(bmp, new Rect(0, 0, 1, 1)); // In PDF, image XObjects are mapped to a 1x1 unit square
            dc.Pop();
        }
        catch
        {
            // Ignore corrupted images
        }
    }

    private static FontFamily ResolveFontFamily(string fontName)
    {
        if (FontCache.Value!.TryGetValue(fontName, out var cached))
            return cached;

        string clean = fontName;
        int plusIdx = clean.IndexOf('+');
        if (plusIdx >= 0) clean = clean.Substring(plusIdx + 1);

        FontFamily family;
        if (clean.Contains("Courier", StringComparison.OrdinalIgnoreCase))
            family = new FontFamily("Courier New");
        else if (clean.Contains("Times", StringComparison.OrdinalIgnoreCase))
            family = new FontFamily("Times New Roman");
        else if (clean.Contains("Arial", StringComparison.OrdinalIgnoreCase))
            family = new FontFamily("Arial");
        else
            family = new FontFamily("Segoe UI");

        FontCache.Value[fontName] = family;
        return family;
    }

    public void Dispose() { }
}
