using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfViewer.Models;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The selected annotation's handles over its page: the outline (drag to move, double-click to
/// edit the comment), corner and edge handles to resize, a rotation handle on stamps and a handle
/// on each point of a measurement or a drawing with few points. The look is the content edit
/// layer's. Laid out in unrotated page space at screen scale, so the handles keep their size at
/// any zoom and turn with the page. The geometry is the view model's; this layer passes the pointer.
/// </summary>
public sealed class AnnotationEditLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(AnnotationEditLayer),
        new PropertyMetadata(null, (d, e) => ((AnnotationEditLayer)d).OnPageChanged(e)));

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)));
    private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0x1A, 0x73, 0xE8)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>The handles' size in screen pixels, whatever the zoom.</summary>
    public const double HandleSize = 9;
    private const double RotateHandleGap = 24;

    private MainViewModel? _owner;
    private AnnotationModel? _shown;
    private bool _dragging;
    private Point _downAt;
    private bool _moved;

    public AnnotationEditLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragUp;
        LostMouseCapture += (_, _) => FinishDrag();
    }

    private void OnPageChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PageViewModel old) old.PropertyChanged -= OnPagePropertyChanged;
        if (_owner != null) _owner.PropertyChanged -= OnOwnerChanged;
        _owner = null;
        if (e.NewValue is PageViewModel page)
        {
            page.PropertyChanged += OnPagePropertyChanged;
            _owner = page.Owner;
            if (_owner != null) _owner.PropertyChanged += OnOwnerChanged;
        }
        Show(_owner?.SelectedAnnotation);
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.UnrotatedDisplayWidth) or nameof(PageViewModel.UnrotatedDisplayHeight)
            or nameof(PageViewModel.DisplayScale) or nameof(PageViewModel.OverlayRotationAngle) or nameof(PageViewModel.RotationAngle))
            Rebuild();
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAnnotation)) Show(_owner?.SelectedAnnotation);
    }

    private void Show(AnnotationModel? annot)
    {
        if (_shown != null) _shown.PropertyChanged -= OnAnnotationChanged;
        _shown = annot;
        if (_shown != null) _shown.PropertyChanged += OnAnnotationChanged;
        Rebuild();
    }

    private void OnAnnotationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnnotationModel.X) or nameof(AnnotationModel.Y) or nameof(AnnotationModel.Width) or nameof(AnnotationModel.Height)
            or nameof(AnnotationModel.Vertices) or nameof(AnnotationModel.InkStrokes) or nameof(AnnotationModel.StampRotation) or nameof(AnnotationModel.PageNumber))
            Rebuild();
    }

    private double W => Page?.UnrotatedDisplayWidth ?? 0;
    private double H => Page?.UnrotatedDisplayHeight ?? 0;
    private Point ToScreen(Point n) => new(n.X * W, n.Y * H);
    private Point ToNorm(Point p) => new(W > 0 ? p.X / W : 0, H > 0 ? p.Y / H : 0);

    /// <summary>Quarter turns the page is shown at (the overlay's clockwise turn).</summary>
    private int Turns => Page == null ? 0 : (((int)Math.Round(Page.OverlayRotationAngle / 90.0)) % 4 + 4) % 4;

    /// <summary>The annotation whose handles are shown here: the selected one when it is on this page.</summary>
    private AnnotationModel? Target => _shown is { } a && Page is { } p && a.PageNumber == p.PageNumber ? a : null;

    // Shapes have no automation peers of their own: the layer lists its handles to screen readers.
    protected override AutomationPeer OnCreateAutomationPeer() => new LayerPeer(this);

    private void Rebuild()
    {
        try { RebuildHandles(); }
        finally { (UIElementAutomationPeer.FromElement(this) as LayerPeer)?.ResetChildrenCache(); }
    }

    private void RebuildHandles()
    {
        Children.Clear();
        var a = Target;
        IsHitTestVisible = a != null;
        if (a == null || _owner == null || W <= 0 || H <= 0) return;
        string kind = MainViewModel.AnnotationTypeName(a.Type);

        var box = new Rect(ToScreen(new Point(a.X, a.Y)), ToScreen(new Point(a.X + a.Width, a.Y + a.Height)));
        var body = new Rectangle
        {
            Width = Math.Max(4, box.Width + 4), Height = Math.Max(4, box.Height + 4),
            Stroke = Accent, StrokeThickness = 1.5, Fill = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move; double-click to edit the comment",
        };
        AutomationProperties.SetName(body, $"{kind} annotation, selected");
        AutomationProperties.SetHelpText(body, "Arrow keys move it, Delete removes it, Enter edits the comment, Tab goes to the next annotation.");
        body.MouseEnter += (_, _) => body.Fill = HoverFill;
        body.MouseLeave += (_, _) => body.Fill = Brushes.Transparent;
        body.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                _owner?.EditSelectedAnnotation();
                return;
            }
            Begin(AnnotationHandle.Body, e);
        };
        SetLeft(body, box.X - 2);
        SetTop(body, box.Y - 2);
        Children.Add(body);

        // A turned stamp's own outline, inside the box it covers.
        if (a.Type == AnnotationType.Stamp && Math.Abs(a.StampRotation % 90) > 1e-6)
        {
            var outline = new Polygon
            {
                Points = new PointCollection(_owner.StampOutline(a).Select(ToScreen)),
                Stroke = Accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 2 }, IsHitTestVisible = false,
            };
            Children.Add(outline);
        }

        if (MainViewModel.CanResizeAnnotation(a))
        {
            var handles = new (AnnotationHandle Handle, double X, double Y, string Name)[]
            {
                (AnnotationHandle.TopLeft, box.Left, box.Top, "top left"),
                (AnnotationHandle.Top, box.Left + box.Width / 2, box.Top, "top"),
                (AnnotationHandle.TopRight, box.Right, box.Top, "top right"),
                (AnnotationHandle.Right, box.Right, box.Top + box.Height / 2, "right"),
                (AnnotationHandle.BottomRight, box.Right, box.Bottom, "bottom right"),
                (AnnotationHandle.Bottom, box.Left + box.Width / 2, box.Bottom, "bottom"),
                (AnnotationHandle.BottomLeft, box.Left, box.Bottom, "bottom left"),
                (AnnotationHandle.Left, box.Left, box.Top + box.Height / 2, "left"),
            };
            bool keeps = MainViewModel.KeepsProportions(a, shift: false);
            foreach (var (handle, x, y, name) in handles)
            {
                var square = Square(x, y, CursorFor(handle));
                square.ToolTip = keeps ? "Drag to resize (keeps its shape)" : "Drag to resize (Shift: keep the proportions)";
                AutomationProperties.SetName(square, $"Resize handle, {name}");
                square.MouseLeftButtonDown += (_, e) => Begin(handle, e);
                Children.Add(square);
            }
        }

        if (MainViewModel.CanRotateAnnotation(a))
        {
            // Above the stamp as the page is seen, whatever its rotation.
            double angle = (Page?.OverlayRotationAngle ?? 0) * Math.PI / 180;
            var up = new Vector(-Math.Sin(angle), -Math.Cos(angle));
            var centre = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            double reach = Math.Abs(up.X) * box.Width / 2 + Math.Abs(up.Y) * box.Height / 2;
            var edge = centre + up * reach;
            var knob = centre + up * (reach + RotateHandleGap);
            Children.Add(new Line { X1 = edge.X, Y1 = edge.Y, X2 = knob.X, Y2 = knob.Y, Stroke = Accent, StrokeThickness = 1, IsHitTestVisible = false });
            var rotate = new Ellipse
            {
                Width = HandleSize + 2, Height = HandleSize + 2, Fill = Brushes.White, Stroke = Accent, StrokeThickness = 1.5,
                Cursor = Cursors.Hand, ToolTip = "Drag to rotate (Shift: in steps of 15 degrees)",
            };
            AutomationProperties.SetName(rotate, "Rotate handle");
            rotate.MouseLeftButtonDown += (_, e) => Begin(AnnotationHandle.Rotate, e);
            SetLeft(rotate, knob.X - (HandleSize + 2) / 2);
            SetTop(rotate, knob.Y - (HandleSize + 2) / 2);
            Children.Add(rotate);
        }

        var points = MainViewModel.VertexHandles(a);
        for (int i = 0; i < points.Count; i++)
        {
            int index = i;
            var p = ToScreen(points[i]);
            var dot = new Ellipse
            {
                Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Accent, StrokeThickness = 1.5,
                Cursor = Cursors.Cross,
                ToolTip = a.Type == AnnotationType.Measurement ? "Drag to move the point; it snaps to the page's lines (Shift: no snapping)" : "Drag to move the point",
            };
            AutomationProperties.SetName(dot, $"Point {i + 1} handle");
            dot.MouseLeftButtonDown += (_, e) => Begin(AnnotationHandle.Vertex, e, index);
            SetLeft(dot, p.X - HandleSize / 2);
            SetTop(dot, p.Y - HandleSize / 2);
            Children.Add(dot);
        }
    }

    private static Rectangle Square(double x, double y, Cursor cursor)
    {
        var r = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Accent, StrokeThickness = 1.5, Cursor = cursor };
        SetLeft(r, x - HandleSize / 2);
        SetTop(r, y - HandleSize / 2);
        return r;
    }

    /// <summary>The resize cursor as the handle is seen on screen (a quarter turn swaps them).</summary>
    private Cursor CursorFor(AnnotationHandle h)
    {
        bool odd = Turns % 2 == 1;
        return h switch
        {
            AnnotationHandle.Top or AnnotationHandle.Bottom => odd ? Cursors.SizeWE : Cursors.SizeNS,
            AnnotationHandle.Left or AnnotationHandle.Right => odd ? Cursors.SizeNS : Cursors.SizeWE,
            AnnotationHandle.TopLeft or AnnotationHandle.BottomRight => odd ? Cursors.SizeNESW : Cursors.SizeNWSE,
            _ => odd ? Cursors.SizeNWSE : Cursors.SizeNESW,
        };
    }

    // ------------------------------------------------------------------ dragging

    private void Begin(AnnotationHandle handle, MouseButtonEventArgs e, int vertex = -1)
    {
        if (_owner == null) return;
        e.Handled = true;
        _downAt = e.GetPosition(this);
        if (!_owner.BeginAnnotationDrag(handle, ToNorm(_downAt), vertex)) return;
        _dragging = true;
        _moved = false;
        CaptureMouse(); // the layer, not the handle: handles are drawn again as the annotation moves
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _owner == null || Page == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(); return; }
        var p = e.GetPosition(this);
        if (!_moved && Math.Abs(p.X - _downAt.X) < 2 && Math.Abs(p.Y - _downAt.Y) < 2) return;
        _moved = true;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _owner.UpdateAnnotationDrag(ToNorm(p), shift, MainViewModel.SnapTolerance(Page.DisplayScale));
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        FinishDrag();
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_owner == null) return;
        if (_moved) _owner.EndAnnotationDrag();
        else _owner.CancelAnnotationDrag();
    }

    /// <summary>The layer as a pane of its named handles (thumbs), the selected annotation's outline first.</summary>
    private sealed class LayerPeer : FrameworkElementAutomationPeer
    {
        private readonly Dictionary<FrameworkElement, AutomationPeer> _peers = new();

        public LayerPeer(AnnotationEditLayer owner) : base(owner) { }

        protected override string GetClassNameCore() => nameof(AnnotationEditLayer);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetNameCore() => "Annotation edit handles";

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = ((AnnotationEditLayer)Owner).Children.OfType<FrameworkElement>()
                .Where(c => !string.IsNullOrEmpty(AutomationProperties.GetName(c))).ToList();
            foreach (var gone in _peers.Keys.Except(children).ToList()) _peers.Remove(gone);
            return children.Select(c => _peers.TryGetValue(c, out var p) ? p : _peers[c] = new HandlePeer(c)).ToList();
        }
    }

    private sealed class HandlePeer : FrameworkElementAutomationPeer
    {
        public HandlePeer(FrameworkElement handle) : base(handle) { }
        protected override string GetClassNameCore() => "AnnotationHandle";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override bool IsContentElementCore() => false;
    }
}
