using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfEngine.Vector.Editing;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The page's paragraphs and images while text and images are edited: outlined, selectable,
/// movable by dragging, images resizable by their corners, paragraphs editable in place (a text
/// box over the paragraph, in its font, size and colour). Laid out in unrotated page space like
/// every overlay; the page's rotation turns it with the page.
/// </summary>
public sealed class ContentEditLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(ContentEditLayer),
        new PropertyMetadata(null, (d, e) => ((ContentEditLayer)d).OnPageChanged(e)));

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)));
    private static readonly Brush Faint = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x70, 0x70, 0x70)));
    private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0x1A, 0x73, 0xE8)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private MainViewModel? _owner;
    private TextBox? _editor;
    private ContentEditItem? _editing;
    private Point? _addAt;

    // Dragging: the item, where the mouse went down, and whether it is a move or a corner resize.
    private ContentEditItem? _dragItem;
    private Point _dragStart;
    private int _dragCorner = -1; // -1 move, 0..3 top-left, top-right, bottom-right, bottom-left
    private Rectangle? _ghost;
    private bool _dragged;

    public ContentEditLayer()
    {
        IsHitTestVisible = false;
        Focusable = true;
        MouseLeftButtonDown += OnBackgroundDown;
    }

    private void OnPageChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PageViewModel old)
        {
            old.EditItems.CollectionChanged -= OnItemsChanged;
            old.PropertyChanged -= OnPagePropertyChanged;
        }
        if (_owner != null) _owner.PropertyChanged -= OnOwnerChanged;
        _owner = null;
        if (e.NewValue is PageViewModel page)
        {
            page.EditItems.CollectionChanged += OnItemsChanged;
            page.PropertyChanged += OnPagePropertyChanged;
            _owner = page.Owner;
            if (_owner != null) _owner.PropertyChanged += OnOwnerChanged;
        }
        UpdateMode();
        Rebuild();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.UnrotatedDisplayWidth) or nameof(PageViewModel.UnrotatedDisplayHeight) or nameof(PageViewModel.DisplayScale))
        {
            CancelEditor();
            Rebuild();
        }
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsEditingContent) or nameof(MainViewModel.IsAddingText))
        {
            UpdateMode();
            if (_owner is { IsEditingContent: false }) CancelEditor();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedEditItem)) Rebuild();
    }

    private void UpdateMode()
    {
        bool editing = _owner?.IsEditingContent == true;
        IsHitTestVisible = editing;
        // Clicks between items fall through to the page, except when placing new text.
        Background = editing && _owner!.IsAddingText ? Brushes.Transparent : null;
        Cursor = editing && _owner!.IsAddingText ? Cursors.IBeam : null;
    }

    private double W => Page?.UnrotatedDisplayWidth ?? 0;
    private double H => Page?.UnrotatedDisplayHeight ?? 0;
    private Rect ToScreen(Rect n) => new(n.X * W, n.Y * H, Math.Max(2, n.Width * W), Math.Max(2, n.Height * H));

    private void Rebuild()
    {
        foreach (var child in Children.OfType<FrameworkElement>().Where(c => !ReferenceEquals(c, _editor)).ToList()) Children.Remove(child);
        if (Page is not { } page) return;
        foreach (var item in page.EditItems)
        {
            if (ReferenceEquals(item, _editing)) continue;
            var r = ToScreen(item.Bounds);
            bool selected = item.IsSelected;
            var box = new Rectangle
            {
                Width = r.Width + 4, Height = r.Height + 4,
                Stroke = selected ? Accent : Faint,
                StrokeThickness = selected ? 1.5 : 0.75,
                StrokeDashArray = selected ? null : new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Tag = item,
                ToolTip = item.Kind == ContentEditKind.Text ? "Double-click to edit the text; drag to move" : "Drag to move; drag a corner to resize",
                ContextMenu = MenuFor(item),
            };
            AutomationProperties.SetName(box, item.AccessibleName);
            box.MouseEnter += (_, _) => { if (!item.IsSelected) box.Fill = HoverFill; };
            box.MouseLeave += (_, _) => box.Fill = Brushes.Transparent;
            box.MouseLeftButtonDown += (s, e) => OnItemDown(item, e, -1);
            box.MouseMove += OnDragMove;
            box.MouseLeftButtonUp += OnDragUp;
            SetLeft(box, r.X - 2);
            SetTop(box, r.Y - 2);
            Children.Add(box);

            if (selected && item.Kind == ContentEditKind.Image)
            {
                var corners = new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom) };
                for (int c = 0; c < 4; c++)
                {
                    int corner = c;
                    var handle = new Rectangle
                    {
                        Width = 9, Height = 9, Fill = Brushes.White, Stroke = Accent, StrokeThickness = 1.5,
                        Cursor = c % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW,
                        ToolTip = "Drag to resize (Shift: without keeping the proportions)",
                    };
                    AutomationProperties.SetName(handle, "Resize handle");
                    handle.MouseLeftButtonDown += (s, e) => OnItemDown(item, e, corner);
                    handle.MouseMove += OnDragMove;
                    handle.MouseLeftButtonUp += OnDragUp;
                    SetLeft(handle, corners[c].X - 4.5);
                    SetTop(handle, corners[c].Y - 4.5);
                    Children.Add(handle);
                }
            }
        }
    }

    private ContextMenu MenuFor(ContentEditItem item)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (item.Kind == ContentEditKind.Text)
        {
            Add("_Edit Text", () => BeginEdit(item));
        }
        else
        {
            Add("_Replace Image...", () => _ = _owner?.ReplaceImageAsync(item));
            Add("Rotate _Clockwise", () => _ = _owner?.RotateEditItemAsync(item, 90));
            Add("Rotate C_ounterclockwise", () => _ = _owner?.RotateEditItemAsync(item, -90));
        }
        menu.Items.Add(new Separator());
        Add("_Delete", () => _ = _owner?.DeleteEditItemAsync(item));
        menu.Opened += (_, _) => { if (_owner != null) _owner.SelectedEditItem = item; };
        return menu;
    }

    // ------------------------------------------------------------------ selecting, moving, resizing

    private void OnItemDown(ContentEditItem item, MouseButtonEventArgs e, int corner)
    {
        if (_owner == null) return;
        e.Handled = true;
        CommitEditor();
        if (_owner.IsAddingText) _owner.IsAddingText = false;
        _owner.SelectedEditItem = item;
        Focus();
        if (e.ClickCount == 2 && item.Kind == ContentEditKind.Text && corner < 0)
        {
            BeginEdit(item);
            return;
        }
        _dragItem = item;
        _dragCorner = corner;
        _dragStart = e.GetPosition(this);
        _dragged = false;
        ((UIElement)e.Source).CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        var d = p - _dragStart;
        if (!_dragged && Math.Abs(d.X) < 3 && Math.Abs(d.Y) < 3) return;
        _dragged = true;
        var r = ToScreen(_dragItem.Bounds);
        var ghost = GhostRect(r, d, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (_ghost == null)
        {
            _ghost = new Rectangle { Stroke = Accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 2 }, Fill = HoverFill, IsHitTestVisible = false };
            Children.Add(_ghost);
        }
        _ghost.Width = ghost.Width;
        _ghost.Height = ghost.Height;
        SetLeft(_ghost, ghost.X);
        SetTop(_ghost, ghost.Y);
    }

    /// <summary>Where the item would go: moved by <paramref name="d"/>, or with the dragged corner moved (proportionally unless <paramref name="free"/>).</summary>
    private Rect GhostRect(Rect r, Vector d, bool free)
    {
        if (_dragCorner < 0) return new Rect(r.X + d.X, r.Y + d.Y, r.Width, r.Height);
        // The corner opposite the dragged one stays put.
        var fixedCorner = _dragCorner switch { 0 => r.BottomRight, 1 => r.BottomLeft, 2 => r.TopLeft, _ => r.TopRight };
        var moving = _dragCorner switch { 0 => r.TopLeft, 1 => r.TopRight, 2 => r.BottomRight, _ => r.BottomLeft } + d;
        double w = Math.Max(4, Math.Abs(moving.X - fixedCorner.X)), h = Math.Max(4, Math.Abs(moving.Y - fixedCorner.Y));
        if (!free)
        {
            double k = Math.Max(w / r.Width, h / r.Height);
            w = r.Width * k;
            h = r.Height * k;
        }
        double x = moving.X < fixedCorner.X ? fixedCorner.X - w : fixedCorner.X;
        double y = moving.Y < fixedCorner.Y ? fixedCorner.Y - h : fixedCorner.Y;
        return new Rect(x, y, w, h);
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem == null) return;
        var item = _dragItem;
        _dragItem = null;
        ((UIElement)sender).ReleaseMouseCapture();
        if (_ghost != null) { Children.Remove(_ghost); _ghost = null; }
        if (!_dragged || _owner == null || W <= 0 || H <= 0) return;
        e.Handled = true;
        var d = e.GetPosition(this) - _dragStart;
        if (_dragCorner < 0)
        {
            _ = _owner.MoveEditItemAsync(item, new Vector(d.X / W, d.Y / H));
        }
        else
        {
            var g = GhostRect(ToScreen(item.Bounds), d, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            _ = _owner.ResizeEditItemAsync(item, new Rect(g.X / W, g.Y / H, g.Width / W, g.Height / H));
        }
    }

    private void OnBackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (_owner == null || !ReferenceEquals(e.OriginalSource, this)) return;
        if (_owner.IsAddingText && W > 0 && H > 0)
        {
            e.Handled = true;
            CommitEditor();
            var p = e.GetPosition(this);
            _addAt = new Point(p.X / W, p.Y / H);
            OpenEditor(null, new Rect(p.X, p.Y, Math.Max(120, W * 0.3), 0));
        }
    }

    // ------------------------------------------------------------------ editing text in place

    public void BeginEdit(ContentEditItem item)
    {
        if (item.Kind != ContentEditKind.Text) return;
        CommitEditor();
        _editing = item;
        Rebuild();
        OpenEditor(item, ToScreen(item.Bounds));
    }

    private void OpenEditor(ContentEditItem? item, Rect r)
    {
        if (Page is not { } page) return;
        double pxPerPt = page.DisplayScale;
        double size = (item?.FontSize ?? _owner?.NewTextFormat.Size ?? 12) * pxPerPt;
        var fmt = _owner?.NewTextFormat;
        var family = item?.FontFamily ?? (item == null ? fmt?.FontFamily : null) ?? "Arial";
        var color = item?.Color ?? (fmt != null ? Color.FromRgb((byte)(fmt.Red * 255), (byte)(fmt.Green * 255), (byte)(fmt.Blue * 255)) : Colors.Black);
        _editor = new TextBox
        {
            Text = item?.Text ?? string.Empty,
            FontFamily = new FontFamily(family),
            FontSize = Math.Max(4, size),
            FontWeight = (item?.Bold ?? fmt?.Bold ?? false) ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = (item?.Italic ?? fmt?.Italic ?? false) ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(color),
            Background = Brushes.White,
            BorderBrush = Accent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = item == null || item.LineCount <= 1 ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextAlignment = (item?.Alignment ?? PdfTextAlignment.Left) switch
            {
                PdfTextAlignment.Center => TextAlignment.Center,
                PdfTextAlignment.Right => TextAlignment.Right,
                PdfTextAlignment.Justified => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            MinWidth = Math.Max(40, r.Width + 6),
            Width = item == null || item.LineCount <= 1 ? double.NaN : r.Width + 6,
            MinHeight = Math.Max(size * 1.3, r.Height + 4),
        };
        if (item is { LineSpacing: > 0 } && item.LineCount > 1)
        {
            _editor.SetValue(System.Windows.Documents.Block.LineHeightProperty, item.LineSpacing * pxPerPt);
            _editor.SetValue(System.Windows.Documents.Block.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        }
        AutomationProperties.SetName(_editor, item == null ? "New text" : "Paragraph text");
        // Rotated text: the editor turns with it (the layer is unrotated page space, y down).
        if (item != null && Math.Abs(item.AngleDegrees) > 0.5)
            _editor.RenderTransform = new RotateTransform(-item.AngleDegrees);
        _editor.PreviewKeyDown += OnEditorKey;
        _editor.LostKeyboardFocus += (_, _) => CommitEditor();
        SetLeft(_editor, r.X - 3);
        SetTop(_editor, r.Y - 2);
        Children.Add(_editor);
        _editor.Loaded += (_, _) =>
        {
            _editor?.Focus();
            if (item == null) return;
            _editor?.SelectAll();
        };
        if (_owner != null) _owner.StatusText = "Editing text: Ctrl+Enter or click elsewhere to keep the change, Esc to cancel.";
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEditor();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            CommitEditor();
        }
    }

    private void CancelEditor()
    {
        if (_editor == null) return;
        var editor = _editor;
        _editor = null;
        _editing = null;
        _addAt = null;
        Children.Remove(editor);
        if (_owner != null) _owner.IsAddingText = false;
        Rebuild();
    }

    private void CommitEditor()
    {
        if (_editor == null || _owner == null || Page == null) return;
        var editor = _editor;
        var item = _editing;
        var at = _addAt;
        _editor = null;
        _editing = null;
        _addAt = null;
        string text = editor.Text.Replace("\r\n", "\n");
        Children.Remove(editor);
        if (item != null) _ = _owner.CommitTextEditAsync(item, text);
        else if (at is { } point) _ = _owner.AddTextAtAsync(Page.PageNumber, point, text);
        Rebuild();
    }
}
