using Android.Content;
using Android.Graphics;
using Android.Views;
using PdfEngine.Vector.Editing;

namespace PdfViewer.Android;

/// <summary>
/// A pad to sign on with a finger or a stylus, for the picture of a visible signature: strokes in
/// dark ink on a light pad. <see cref="ToPicture"/> gives the strokes cut to their extent on a
/// transparent background, as the picture a desktop user would scan.
/// </summary>
internal sealed class SignaturePadView : View
{
    private readonly List<List<PointF>> _strokes = new();
    private readonly Paint _ink = new() { Color = Color.ParseColor("#10204A"), StrokeWidth = 6, AntiAlias = true, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
    private readonly Paint _line = new() { Color = Color.ParseColor("#9AA0A6"), StrokeWidth = 2 };

    public SignaturePadView(Context context) : base(context)
    {
        _ink.SetStyle(Paint.Style.Stroke);
        SetBackgroundColor(Color.ParseColor("#FAFAF7"));
        ContentDescription = "Signature pad: sign here with your finger";
        _ink.StrokeWidth = 3 * (Resources?.DisplayMetrics?.Density ?? 2);
    }

    /// <summary>True once something was drawn.</summary>
    public bool HasInk => _strokes.Any(s => s.Count > 0);

    public event Action? Changed;

    public void Clear()
    {
        _strokes.Clear();
        Invalidate();
        Changed?.Invoke();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                Parent?.RequestDisallowInterceptTouchEvent(true);
                _strokes.Add([new PointF(e.GetX(), e.GetY())]);
                break;
            case MotionEventActions.Move when _strokes.Count > 0:
                // The points between frames too, so a quick line stays smooth.
                for (int h = 0; h < e.HistorySize; h++) _strokes[^1].Add(new PointF(e.GetHistoricalX(h), e.GetHistoricalY(h)));
                _strokes[^1].Add(new PointF(e.GetX(), e.GetY()));
                break;
            case MotionEventActions.Up:
                Changed?.Invoke();
                break;
        }
        Invalidate();
        return true;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // The line to sign on.
        float y = Height * 0.75f;
        canvas.DrawLine(Width * 0.06f, y, Width * 0.94f, y, _line);
        foreach (var stroke in _strokes) DrawStroke(canvas, stroke, _ink);
    }

    private static void DrawStroke(Canvas canvas, List<PointF> stroke, Paint ink)
    {
        if (stroke.Count == 0) return;
        if (stroke.Count == 1)
        {
            canvas.DrawPoint(stroke[0].X, stroke[0].Y, ink);
            return;
        }
        using var path = new global::Android.Graphics.Path();
        path.MoveTo(stroke[0].X, stroke[0].Y);
        for (int i = 1; i < stroke.Count; i++) path.LineTo(stroke[i].X, stroke[i].Y);
        canvas.DrawPath(path, ink);
    }

    /// <summary>The signature as a picture: the strokes' extent plus a margin, dark ink with transparency; null when nothing was drawn.</summary>
    public PdfImageContent? ToPicture()
    {
        if (!HasInk) return null;
        float pad = _ink.StrokeWidth * 2;
        float x0 = _strokes.SelectMany(s => s).Min(p => p.X) - pad, y0 = _strokes.SelectMany(s => s).Min(p => p.Y) - pad;
        float x1 = _strokes.SelectMany(s => s).Max(p => p.X) + pad, y1 = _strokes.SelectMany(s => s).Max(p => p.Y) + pad;
        int w = Math.Max(1, (int)Math.Ceiling(x1 - x0)), h = Math.Max(1, (int)Math.Ceiling(y1 - y0));
        using var bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
        using (var canvas = new Canvas(bitmap))
        {
            canvas.Translate(-x0, -y0);
            foreach (var stroke in _strokes) DrawStroke(canvas, stroke, _ink);
        }
        var argb = new int[w * h];
        bitmap.GetPixels(argb, 0, w, 0, 0, w, h);
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (int i = 0; i < argb.Length; i++)
        {
            int c = argb[i];
            rgb[3 * i] = (byte)(c >> 16);
            rgb[3 * i + 1] = (byte)(c >> 8);
            rgb[3 * i + 2] = (byte)c;
            alpha[i] = (byte)(c >>> 24);
        }
        return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, alpha, 3);
    }
}
