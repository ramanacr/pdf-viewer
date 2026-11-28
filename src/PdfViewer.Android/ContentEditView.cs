using Android.Content;
using Android.Graphics;
using Android.Views;
using PdfEngine.Vector.Editing;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// One page of "Edit text and images": the rendered page fitted into the view, with its paragraphs
/// and pictures outlined. A tap reports what is under the finger (or the point, for placing new
/// text or a picture); the selected item can be dragged to move it, and a selected picture has a
/// corner handle that resizes it, keeping its proportions. Coordinates given to and reported by the
/// view are points of the page as shown (from its top-left corner, after its /Rotate).
/// </summary>
internal sealed class ContentEditView : View
{
    /// <summary>A paragraph or picture of the page and its box on the page as shown (points).</summary>
    public sealed record Box(PdfEditTarget Kind, int Id, RectF Shown);

    private enum DragKind { None, Move, Resize }

    private readonly float _density;
    private readonly int _touchSlop;
    private readonly Paint _pagePaint = new(PaintFlags.FilterBitmap);
    private readonly Paint _textOutline = new(PaintFlags.AntiAlias);
    private readonly Paint _imageOutline = new(PaintFlags.AntiAlias);
    private readonly Paint _selectedOutline = new(PaintFlags.AntiAlias);
    private readonly Paint _selectedFill = new();
    private readonly Paint _handle = new(PaintFlags.AntiAlias);
    private readonly Paint _handleRing = new(PaintFlags.AntiAlias);
    private readonly Paint _shadow = new();

    private Bitmap? _page;
    private float _shownWidth = 1, _shownHeight = 1;
    private IReadOnlyList<Box> _boxes = Array.Empty<Box>();

    private DragKind _drag;
    private bool _dragging;
    private float _downX, _downY, _lastX, _lastY;

    public ContentEditView(Context context, Color accent, Color imageColour) : base(context)
    {
        _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? (int)(8 * _density);
        Focusable = true;
        Clickable = true;
        _textOutline.SetStyle(Paint.Style.Stroke);
        _textOutline.StrokeWidth = 1f * _density;
        _textOutline.Color = Color.Argb(150, accent.R, accent.G, accent.B);
        _imageOutline.SetStyle(Paint.Style.Stroke);
        _imageOutline.StrokeWidth = 1.5f * _density;
        _imageOutline.Color = imageColour;
        _imageOutline.SetPathEffect(new DashPathEffect([6 * _density, 4 * _density], 0));
        _selectedOutline.SetStyle(Paint.Style.Stroke);
        _selectedOutline.StrokeWidth = 2.5f * _density;
        _selectedOutline.Color = accent;
        _selectedFill.Color = Color.Argb(40, accent.R, accent.G, accent.B);
        _handle.Color = accent;
        _handleRing.SetStyle(Paint.Style.Stroke);
        _handleRing.StrokeWidth = 2f * _density;
        _handleRing.Color = Color.White;
        _shadow.Color = Color.Argb(60, 0, 0, 0);
    }

    /// <summary>A tap: the box under the finger (null when none) and the point (page as shown, points).</summary>
    public event Action<Box?, PointF>? Tapped;

    /// <summary>The selected box dragged by a distance (page as shown, points).</summary>
    public event Action<Box, float, float>? Moved;

    /// <summary>The selected picture resized to a new box (page as shown, points).</summary>
    public event Action<Box, RectF>? Resized;

    /// <summary>The selected paragraph or picture (outlined strongly; it can be dragged).</summary>
    public Box? Current { get; private set; }

    /// <summary>Placing new text or a picture: taps report a point and nothing is selected by them.</summary>
    public bool Placing { get; set; }

    public IReadOnlyList<Box> Boxes => _boxes;

    /// <summary>Shows a page: its bitmap, its size as shown in points, and its boxes. The old bitmap is recycled.</summary>
    public void SetPage(Bitmap page, float shownWidth, float shownHeight, IReadOnlyList<Box> boxes)
    {
        var old = _page;
        _page = page;
        _shownWidth = Math.Max(1, shownWidth);
        _shownHeight = Math.Max(1, shownHeight);
        _boxes = boxes;
        Current = null;
        CancelDrag();
        Invalidate();
        if (old != null && !ReferenceEquals(old, page)) old.Recycle();
    }

    public void Select(Box? box)
    {
        Current = box;
        CancelDrag();
        Invalidate();
    }

    /// <summary>Releases the bitmap (the screen closed).</summary>
    public void Release()
    {
        var old = _page;
        _page = null;
        _boxes = Array.Empty<Box>();
        Current = null;
        old?.Recycle();
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Pixels per point and the page's top-left corner in the view.</summary>
    private (float Scale, float Left, float Top) Fit()
    {
        float pad = 8 * _density;
        float w = Math.Max(1, Width - 2 * pad), h = Math.Max(1, Height - 2 * pad);
        float scale = Math.Min(w / _shownWidth, h / _shownHeight);
        return (scale, (Width - _shownWidth * scale) / 2, (Height - _shownHeight * scale) / 2);
    }

    private RectF ToView(RectF shown, (float Scale, float Left, float Top) f) =>
        new(f.Left + shown.Left * f.Scale, f.Top + shown.Top * f.Scale, f.Left + shown.Right * f.Scale, f.Top + shown.Bottom * f.Scale);

    /// <summary>The box under a point of the view: the smallest one within reach of the finger (the selected one first, to start a drag).</summary>
    private Box? HitTest(float x, float y, bool selectedFirst)
    {
        var f = Fit();
        float reach = 8 * _density;
        if (selectedFirst && Current != null)
        {
            var s = ToView(Current.Shown, f);
            s.Inset(-reach, -reach);
            if (s.Contains(x, y)) return Current;
        }
        Box? best = null;
        float bestArea = float.MaxValue;
        foreach (var box in _boxes)
        {
            var r = ToView(box.Shown, f);
            r.Inset(-reach, -reach);
            if (!r.Contains(x, y)) continue;
            float area = r.Width() * r.Height();
            if (area < bestArea)
            {
                best = box;
                bestArea = area;
            }
        }
        return best;
    }

    private bool NearHandle(float x, float y)
    {
        if (Current is not { Kind: PdfEditTarget.Image }) return false;
        var r = ToView(Current.Shown, Fit());
        float reach = 24 * _density; // a 48 dp target around the handle
        return Math.Abs(x - r.Right) <= reach && Math.Abs(y - r.Bottom) <= reach;
    }

    /// <summary>The selected picture's box after the resize drag so far (proportions kept, top-left fixed).</summary>
    private RectF ResizedBox(float dxPoints, float dyPoints)
    {
        var r = Current!.Shown;
        float w = Math.Max(1, r.Width()), h = Math.Max(1, r.Height());
        float factor = ((w + dxPoints) / w + (h + dyPoints) / h) / 2;
        factor = Math.Max(factor, Math.Max(8 / w, 8 / h)); // at least 8 points
        return new RectF(r.Left, r.Top, r.Left + w * factor, r.Top + h * factor);
    }

    private void CancelDrag()
    {
        _drag = DragKind.None;
        _dragging = false;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_page == null || _page.IsRecycled) return;
        var f = Fit();
        var dest = new RectF(f.Left, f.Top, f.Left + _shownWidth * f.Scale, f.Top + _shownHeight * f.Scale);
        canvas.DrawRect(dest.Left + 2 * _density, dest.Top + 2 * _density, dest.Right + 2 * _density, dest.Bottom + 2 * _density, _shadow);
        canvas.DrawBitmap(_page, null, dest, _pagePaint);
        foreach (var box in _boxes)
        {
            if (ReferenceEquals(box, Current)) continue;
            canvas.DrawRect(ToView(box.Shown, f), box.Kind == PdfEditTarget.Image ? _imageOutline : _textOutline);
        }
        if (Current is not { } selected) return;
        var r = ToView(selected.Shown, f);
        if (_dragging)
        {
            float dx = (_lastX - _downX) / f.Scale, dy = (_lastY - _downY) / f.Scale;
            var moved = _drag == DragKind.Resize ? ResizedBox(dx, dy) : new RectF(selected.Shown.Left + dx, selected.Shown.Top + dy, selected.Shown.Right + dx, selected.Shown.Bottom + dy);
            canvas.DrawRect(r, _textOutline);
            r = ToView(moved, f);
        }
        canvas.DrawRect(r, _selectedFill);
        canvas.DrawRect(r, _selectedOutline);
        if (selected.Kind == PdfEditTarget.Image)
        {
            canvas.DrawCircle(r.Right, r.Bottom, 10 * _density, _handle);
            canvas.DrawCircle(r.Right, r.Bottom, 10 * _density, _handleRing);
        }
    }

    // ------------------------------------------------------------------ touch

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || _page == null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = _lastX = e.GetX();
                _downY = _lastY = e.GetY();
                _dragging = false;
                _drag = Placing || Current == null ? DragKind.None
                    : NearHandle(_downX, _downY) ? DragKind.Resize
                    : ReferenceEquals(HitTest(_downX, _downY, true), Current) ? DragKind.Move
                    : DragKind.None;
                Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;
            case MotionEventActions.Move:
                _lastX = e.GetX();
                _lastY = e.GetY();
                if (!_dragging && _drag != DragKind.None && Math.Abs(_lastX - _downX) + Math.Abs(_lastY - _downY) > _touchSlop) _dragging = true;
                if (_dragging) Invalidate();
                return true;
            case MotionEventActions.Up:
            {
                _lastX = e.GetX();
                _lastY = e.GetY();
                var f = Fit();
                if (_dragging && Current is { } selected)
                {
                    float dx = (_lastX - _downX) / f.Scale, dy = (_lastY - _downY) / f.Scale;
                    var drag = _drag;
                    CancelDrag();
                    Invalidate();
                    if (drag == DragKind.Resize) Resized?.Invoke(selected, ResizedBox(dx, dy));
                    else Moved?.Invoke(selected, dx, dy);
                    return true;
                }
                CancelDrag();
                if (Math.Abs(_lastX - _downX) + Math.Abs(_lastY - _downY) <= _touchSlop * 2)
                {
                    var point = new PointF((_lastX - f.Left) / f.Scale, (_lastY - f.Top) / f.Scale);
                    bool onPage = point.X >= 0 && point.Y >= 0 && point.X <= _shownWidth && point.Y <= _shownHeight;
                    if (Placing)
                    {
                        if (onPage) Tapped?.Invoke(null, point);
                    }
                    else
                    {
                        Tapped?.Invoke(HitTest(_lastX, _lastY, false), point);
                    }
                    PerformClick();
                }
                Invalidate();
                return true;
            }
            case MotionEventActions.Cancel:
                CancelDrag();
                Invalidate();
                return true;
        }
        return base.OnTouchEvent(e);
    }

    public override bool PerformClick() => base.PerformClick();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
        base.Dispose(disposing);
    }
}
