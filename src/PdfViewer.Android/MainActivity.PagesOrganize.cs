using System.Globalization;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Organize pages: a tool screen with the document's pages as a grid of thumbnails. Tap pages to
/// select them (several at once), then turn them left or right, move them earlier, later or to a
/// place, duplicate or delete them, or add a blank page after them. Every change can be undone.
/// "Save as…" writes the new arrangement as a new PDF (<see cref="PdfPageAssembler.Build"/>, with
/// the session's edits); the open document is not changed. Thumbnails are rendered by the engine on
/// a worker thread, the pages in view first, and a bounded number are kept.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>Organize pages: opens the tool screen for the open document.</summary>
    private async void OrganizePages()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (!await DocumentAllowsAsync(p => p.CanAssemble, "This document's security settings do not allow changing its pages.")) return;
        if (!ReferenceEquals(doc, _doc) || CurrentBytes() is not { } bytes) return;
        new PageOrganizer(this, doc, bytes).Show();
    }

    /// <summary>A page of the new arrangement: page <see cref="Source"/> of the document (0: a blank page of <see cref="Width"/> × <see cref="Height"/> points), turned a further <see cref="Rotation"/> degrees clockwise.</summary>
    private sealed record OrganizedPage(long Id, int Source, int Rotation, double Width, double Height);

    /// <summary>The organizer screen and its arrangement, selection and undo steps.</summary>
    private sealed class PageOrganizer
    {
        private const int MaxThumbnails = 160;
        private const int MaxUndo = 100;

        private readonly MainActivity _a;
        private readonly ViewerDocument _doc;
        private readonly byte[] _bytes;
        private List<OrganizedPage> _pages;
        private readonly HashSet<long> _selected = new();
        private readonly Stack<List<OrganizedPage>> _undo = new();
        private readonly Dictionary<int, Bitmap> _thumbnails = new();
        private readonly LinkedList<int> _thumbnailOrder = new();
        private readonly Dictionary<(int, int), Bitmap> _blanks = new();
        private readonly Stack<int> _wanted = new();
        private readonly HashSet<int> _failed = new();
        private long _nextId;
        private bool _rendering, _saved, _closed;
        private LinearLayout _screen = null!;
        private GridView _grid = null!;
        private TextView _title = null!, _undoButton = null!;
        private readonly List<TextView> _selectionButtons = new();
        private PageAdapter _adapter = null!;

        public PageOrganizer(MainActivity activity, ViewerDocument doc, byte[] bytes)
        {
            _a = activity;
            _doc = doc;
            _bytes = bytes;
            _pages = doc.Pages.Select(p => new OrganizedPage(_nextId++, p.Number, 0, p.Width, p.Height)).ToList();
        }

        private float Dp(float dp) => _a.Dp(dp);

        /// <summary>True when the arrangement differs from the document's.</summary>
        private bool Changed => _pages.Count != _doc.PageCount || _pages.Where((p, i) => p.Source != i + 1 || p.Rotation != 0).Any();

        public void Show()
        {
            _screen = new LinearLayout(_a) { Orientation = global::Android.Widget.Orientation.Vertical };
            _screen.SetBackgroundColor(_a.ScreenColor);
            _undoButton = _a.ToolButton("Undo", "Undo the last change", (_, _) => Undo());
            var save = _a.ToolButton("Save as…", "Save the new arrangement as a new PDF", (_, _) => Save());
            _screen.AddView(_a.ToolScreenBar("Organize pages", out _title, _undoButton, save));

            var hint = new TextView(_a) { Text = "Tap pages to select them. Save as… writes a new PDF; this document is not changed." };
            hint.SetTextColor(_a.IsDark ? Color.ParseColor("#A9AEB6") : Color.ParseColor("#5A5F66"));
            hint.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
            hint.SetPadding((int)Dp(16), (int)Dp(6), (int)Dp(16), (int)Dp(6));
            _screen.AddView(hint);

            _grid = new GridView(_a)
            {
                StretchMode = StretchMode.StretchColumnWidth,
                ChoiceMode = ChoiceMode.None,
                ContentDescription = "Pages",
            };
            _grid.SetNumColumns(-1); // AUTO_FIT: as many columns as fit
            _grid.SetColumnWidth((int)Dp(104));
            _grid.SetHorizontalSpacing((int)Dp(8));
            _grid.SetVerticalSpacing((int)Dp(8));
            _grid.SetPadding((int)Dp(8), (int)Dp(8), (int)Dp(8), (int)Dp(8));
            _grid.SetClipToPadding(false);
            _adapter = new PageAdapter(this);
            _grid.Adapter = _adapter;
            _grid.ItemClick += (_, e) => Toggle(e.Position);
            _grid.ItemLongClick += (_, e) => SelectThrough(e.Position);
            _screen.AddView(_grid, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            var actions = _a.Bar();
            actions.SetBackgroundColor(_a.IsDark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3"));
            void Add(string text, string description, Action run, bool needsSelection = true)
            {
                var button = _a.ToolButton(text, description, (_, _) =>
                {
                    if (needsSelection && _selected.Count == 0)
                    {
                        Toast.MakeText(_a, "Tap pages to select them first.", ToastLength.Short)?.Show();
                        return;
                    }
                    run();
                });
                button.SetTextColor(_a.AccentColor);
                if (needsSelection) _selectionButtons.Add(button);
                actions.AddView(button);
            }
            Add("⟲ Left", "Turn the selected pages left", () => Rotate(-90));
            Add("⟳ Right", "Turn the selected pages right", () => Rotate(90));
            Add("◀ Earlier", "Move the selected pages one place earlier", () => Shift(-1));
            Add("Later ▶", "Move the selected pages one place later", () => Shift(1));
            Add("Move to…", "Move the selected pages after a page you choose", MoveTo);
            Add("Duplicate", "Add a copy of each selected page after it", Duplicate);
            Add("Blank after", "Add a blank page after the selected pages, or at the end", AddBlank, needsSelection: false);
            Add("Delete", "Delete the selected pages", Delete);
            Add("All / none", "Select all pages, or none", SelectAllOrNone, needsSelection: false);
            var scroll = new HorizontalScrollView(_a);
            scroll.AddView(actions);
            _screen.AddView(scroll);

            Refresh();
            _a.ShowToolOverlay(_screen, Closed);
        }

        // ------------------------------------------------------------------ closing

        /// <summary>The screen went (Back): an arrangement not saved may be kept or thrown away.</summary>
        private void Closed()
        {
            if (_saved || !Changed)
            {
                Release();
                return;
            }
            new AlertDialog.Builder(_a)
                .SetTitle("Discard the new arrangement?")!
                .SetMessage("The pages' new order and turns have not been saved as a PDF.")!
                .SetPositiveButton("Discard", (_, _) => Release())!
                .SetNegativeButton("Keep organizing", (_, _) => Reopen())!
                .SetOnCancelListener(new CancelListener(Reopen))!
                .Show();
        }

        private void Reopen()
        {
            if (_closed || !ReferenceEquals(_doc, _a._doc))
            {
                Release();
                return;
            }
            _a.ShowToolOverlay(_screen, Closed);
            RequestRender();
        }

        /// <summary>Lets go of the thumbnails (the screen is gone for good).</summary>
        private void Release()
        {
            _closed = true;
            _wanted.Clear();
            _grid.Adapter = null;
            foreach (var bitmap in _thumbnails.Values) bitmap.Recycle();
            foreach (var bitmap in _blanks.Values) bitmap.Recycle();
            _thumbnails.Clear();
            _thumbnailOrder.Clear();
            _blanks.Clear();
        }

        private sealed class CancelListener(Action cancelled) : Java.Lang.Object, IDialogInterfaceOnCancelListener
        {
            public void OnCancel(IDialogInterface? dialog) => cancelled();
        }

        // ------------------------------------------------------------------ changes

        /// <summary>Keeps the arrangement before a change, for Undo.</summary>
        private void Remember()
        {
            _undo.Push(_pages.ToList());
            if (_undo.Count > MaxUndo)
            {
                var keep = _undo.Take(MaxUndo).Reverse().ToList();
                _undo.Clear();
                foreach (var step in keep) _undo.Push(step);
            }
        }

        private void Undo()
        {
            if (_undo.Count == 0)
            {
                Toast.MakeText(_a, "Nothing to undo.", ToastLength.Short)?.Show();
                return;
            }
            _pages = _undo.Pop();
            _selected.IntersectWith(_pages.Select(p => p.Id));
            _saved = false;
            Refresh();
        }

        private void AfterChange(string announcement)
        {
            _saved = false;
            Refresh();
            Announce(announcement);
        }

        /// <summary>Tells TalkBack what a change did (nothing is shown).</summary>
        private void Announce(string text)
        {
#pragma warning disable CA1422 // Android 16 obsoletes announcements; older versions, most devices, still speak them
            _grid.AnnounceForAccessibility(text);
#pragma warning restore CA1422
        }

        private void Rotate(int degrees)
        {
            Remember();
            _pages = _pages.Select(p => _selected.Contains(p.Id) ? p with { Rotation = ((p.Rotation + degrees) % 360 + 360) % 360 } : p).ToList();
            AfterChange(degrees < 0 ? "Turned left." : "Turned right.");
        }

        /// <summary>Moves each selected page one place earlier (<paramref name="step"/> -1) or later (1), past a page that is not selected.</summary>
        private void Shift(int step)
        {
            var indices = Enumerable.Range(0, _pages.Count).Where(i => _selected.Contains(_pages[i].Id)).ToList();
            if (step > 0) indices.Reverse();
            bool moved = false;
            var list = _pages.ToList();
            foreach (int i in indices)
            {
                int j = i + step;
                if (j < 0 || j >= list.Count || _selected.Contains(list[j].Id)) continue;
                (list[i], list[j]) = (list[j], list[i]);
                moved = true;
            }
            if (!moved)
            {
                Toast.MakeText(_a, step < 0 ? "The selected pages are already first." : "The selected pages are already last.", ToastLength.Short)?.Show();
                return;
            }
            Remember();
            _pages = list;
            AfterChange(step < 0 ? "Moved earlier." : "Moved later.");
            int first = _pages.FindIndex(p => _selected.Contains(p.Id));
            if (first >= 0) _grid.SmoothScrollToPosition(first);
        }

        private void MoveTo()
        {
            int count = _pages.Count;
            _a.AskLine("Move pages", $"Put the selected pages after which page (1 to {count})? Type 0 to put them first. Pages are numbered as they are now.",
                "0", "Page number", InputTypes.ClassNumber, "Move", text =>
                {
                    if (_closed) return;
                    if (!int.TryParse(text.Trim(), out int after) || after < 0 || after > _pages.Count)
                    {
                        Toast.MakeText(_a, $"Type a page number from 0 to {_pages.Count}.", ToastLength.Long)?.Show();
                        return;
                    }
                    var moving = _pages.Where(p => _selected.Contains(p.Id)).ToList();
                    var rest = _pages.Where(p => !_selected.Contains(p.Id)).ToList();
                    int at = _pages.Take(after).Count(p => !_selected.Contains(p.Id));
                    rest.InsertRange(at, moving);
                    Remember();
                    _pages = rest;
                    AfterChange("Moved.");
                    _grid.SmoothScrollToPosition(at);
                });
        }

        private void Duplicate()
        {
            Remember();
            var list = new List<OrganizedPage>();
            foreach (var p in _pages)
            {
                list.Add(p);
                if (_selected.Contains(p.Id)) list.Add(p with { Id = _nextId++ });
            }
            int added = list.Count - _pages.Count;
            _pages = list;
            AfterChange(added == 1 ? "1 page duplicated." : $"{added} pages duplicated.");
        }

        /// <summary>A blank page the size of the page before it (as seen), after the last selected page or at the end.</summary>
        private void AddBlank()
        {
            int after = _pages.FindLastIndex(p => _selected.Contains(p.Id));
            if (after < 0) after = _pages.Count - 1;
            var before = _pages[after];
            bool turned = before.Rotation % 180 != 0;
            var blank = new OrganizedPage(_nextId++, 0, 0, turned ? before.Height : before.Width, turned ? before.Width : before.Height);
            Remember();
            _pages.Insert(after + 1, blank);
            AfterChange($"Blank page added as page {after + 2}.");
            _grid.SmoothScrollToPosition(after + 1);
        }

        private void Delete()
        {
            int deleting = _pages.Count(p => _selected.Contains(p.Id));
            if (deleting >= _pages.Count)
            {
                Toast.MakeText(_a, "A PDF needs at least one page: keep one or more.", ToastLength.Long)?.Show();
                return;
            }
            Remember();
            _pages = _pages.Where(p => !_selected.Contains(p.Id)).ToList();
            _selected.Clear();
            AfterChange(deleting == 1 ? "1 page deleted. Undo brings it back." : $"{deleting} pages deleted. Undo brings them back.");
        }

        private void SelectAllOrNone()
        {
            if (_selected.Count == _pages.Count) _selected.Clear();
            else _selected.UnionWith(_pages.Select(p => p.Id));
            Refresh();
        }

        private void Toggle(int position)
        {
            if (position < 0 || position >= _pages.Count) return;
            long id = _pages[position].Id;
            if (!_selected.Remove(id)) _selected.Add(id);
            Refresh();
        }

        /// <summary>A long press selects every page from the nearest selected one before it (or the first page) to this one.</summary>
        private void SelectThrough(int position)
        {
            if (position < 0 || position >= _pages.Count) return;
            int from = _pages.FindLastIndex(position, p => _selected.Contains(p.Id));
            if (from < 0) from = position;
            for (int i = from; i <= position; i++) _selected.Add(_pages[i].Id);
            Refresh();
            Announce($"Pages {from + 1} to {position + 1} selected.");
        }

        /// <summary>The title, the buttons' state and the grid, after a change.</summary>
        private void Refresh()
        {
            int n = _selected.Count;
            _title.Text = n == 0 ? $"Organize pages · {_pages.Count}" : $"{n} of {_pages.Count} selected";
            _undoButton.Alpha = _undo.Count > 0 ? 1f : 0.4f;
            foreach (var button in _selectionButtons) button.Alpha = n > 0 ? 1f : 0.4f;
            _adapter.NotifyDataSetChanged();
        }

        // ------------------------------------------------------------------ save

        private void Save()
        {
            if (!ReferenceEquals(_doc, _a._doc))
            {
                Toast.MakeText(_a, "Another document is open now. Close this screen and start again.", ToastLength.Long)?.Show();
                return;
            }
            if (!Changed)
            {
                Toast.MakeText(_a, "Nothing changed yet: the pages are as in the document.", ToastLength.Short)?.Show();
                return;
            }
            var pages = _pages.ToList();
            var bytes = _bytes;
            string done = $"The {pages.Count} pages were saved in their new arrangement as a new PDF." + (_doc.IsEncrypted ? " It has no password." : "");
            _a.SaveNewFile("application/pdf", $"{_a.BaseName} organized.pdf", "Saving the pages…", token =>
            {
                using var engine = _a.OpenForTool(bytes);
                token.ThrowIfCancellationRequested();
                var list = pages.Select(p => p.Source == 0
                    ? PdfAssemblyPage.Blank(p.Width, p.Height) with { Rotation = p.Rotation }
                    : new PdfAssemblyPage(engine, p.Source, p.Rotation)).ToList();
                return PdfPageAssembler.Build(list);
            }, target =>
            {
                _saved = true;
                OfferOpen(target, done);
            });
        }

        /// <summary>"Saved. Open it now?": opening it closes this screen; staying keeps organizing.</summary>
        private void OfferOpen(Uri target, string message)
        {
            new AlertDialog.Builder(_a)
                .SetTitle("Saved")!
                .SetMessage(message)!
                .SetPositiveButton("Open it", (_, _) =>
                {
                    if (!_closed) _a.CloseToolOverlay();
                    _a.AfterDiscard(() =>
                    {
                        _a.RememberPosition();
                        _ = _a.OpenUriAsync(target, null);
                    });
                })!
                .SetNegativeButton("Keep organizing", (_, _) => { })!
                .Show();
        }

        // ------------------------------------------------------------------ thumbnails

        /// <summary>The thumbnail of a page of the document, or null while it is rendered (it is asked for).</summary>
        private Bitmap? Thumbnail(OrganizedPage page)
        {
            if (page.Source == 0)
            {
                // A blank page: white paper of its proportions.
                double longest = Math.Max(page.Width, page.Height);
                var key = ((int)Math.Round(page.Width / longest * 90), (int)Math.Round(page.Height / longest * 90));
                if (!_blanks.TryGetValue(key, out var blank))
                {
                    blank = Bitmap.CreateBitmap(Math.Max(1, key.Item1), Math.Max(1, key.Item2), Bitmap.Config.Argb8888!)!;
                    blank.EraseColor(Color.White);
                    _blanks[key] = blank;
                }
                return blank;
            }
            if (_thumbnails.TryGetValue(page.Source, out var bitmap))
            {
                _thumbnailOrder.Remove(page.Source);
                _thumbnailOrder.AddLast(page.Source);
                return bitmap;
            }
            if (!_failed.Contains(page.Source))
            {
                _wanted.Push(page.Source);
                // After layout: the grid asks for a cell before it attaches it, and the loop skips pages not in view.
                _grid.Post(RequestRender);
            }
            return null;
        }

        private void RequestRender()
        {
            if (_rendering || _closed) return;
            _ = RenderLoopAsync();
        }

        /// <summary>Renders the pages asked for, the latest first, skipping those no longer in view.</summary>
        private async Task RenderLoopAsync()
        {
            _rendering = true;
            try
            {
                while (!_closed && _wanted.Count > 0 && ReferenceEquals(_doc, _a._doc))
                {
                    int source = _wanted.Pop();
                    if (_thumbnails.ContainsKey(source) || _failed.Contains(source) || !InView(source)) continue;
                    var size = _doc.Pages[source - 1];
                    double scale = Dp(150) / Math.Max(1, Math.Max(size.Width, size.Height));
                    var doc = _doc;
                    Bitmap? bitmap;
                    try
                    {
                        bitmap = await Task.Run(async () => ToBitmap(await doc.RenderPageAsync(source, scale)));
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _failed.Add(source);
                        continue;
                    }
                    if (bitmap == null)
                    {
                        _failed.Add(source);
                        continue;
                    }
                    if (_closed)
                    {
                        bitmap.Recycle();
                        break;
                    }
                    _thumbnails[source] = bitmap;
                    _thumbnailOrder.AddLast(source);
                    while (_thumbnailOrder.Count > MaxThumbnails && _thumbnailOrder.First is { } oldest)
                    {
                        // Not recycled: a cell may still show it until it is bound again.
                        _thumbnails.Remove(oldest.Value);
                        _thumbnailOrder.RemoveFirst();
                    }
                    ShowThumbnail(source, bitmap);
                }
            }
            finally
            {
                _rendering = false;
            }
        }

        /// <summary>A rendered page (BGRA, opaque) as an Android bitmap: on a little-endian device each BGRA pixel reads as an ARGB colour.</summary>
        private static Bitmap? ToBitmap(ViewerBitmap page)
        {
            var colors = new int[page.Width * page.Height];
            Buffer.BlockCopy(page.Pixels.Array!, page.Pixels.Offset, colors, 0, colors.Length * 4);
            return Bitmap.CreateBitmap(colors, page.Width, page.Height, Bitmap.Config.Argb8888!);
        }

        private bool InView(int source)
        {
            for (int i = 0; i < _grid.ChildCount; i++)
                if (_grid.GetChildAt(i)?.Tag is Cell { Source: var s } && s == source) return true;
            return false;
        }

        private void ShowThumbnail(int source, Bitmap bitmap)
        {
            for (int i = 0; i < _grid.ChildCount; i++)
                if (_grid.GetChildAt(i)?.Tag is Cell cell && cell.Source == source) cell.Image.SetImageBitmap(bitmap);
        }

        // ------------------------------------------------------------------ cells

        /// <summary>The views of a grid cell, kept in its tag.</summary>
        private sealed class Cell(ImageView image, TextView label, TextView check, FrameLayout frame) : Java.Lang.Object
        {
            public ImageView Image { get; } = image;
            public TextView Label { get; } = label;
            public TextView Check { get; } = check;
            public FrameLayout Frame { get; } = frame;
            public int Source { get; set; }
        }

        private View MakeCell()
        {
            var column = new LinearLayout(_a) { Orientation = global::Android.Widget.Orientation.Vertical };
            var frame = new SquareFrame(_a);
            frame.SetPadding((int)Dp(4), (int)Dp(4), (int)Dp(4), (int)Dp(4));
            var image = new ImageView(_a);
            image.SetScaleType(ImageView.ScaleType.FitCenter);
            frame.AddView(image, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            var check = new TextView(_a) { Text = "✓", Gravity = GravityFlags.Center };
            check.SetTextColor(Color.White);
            check.SetTypeface(null, TypefaceStyle.Bold);
            var badge = new global::Android.Graphics.Drawables.GradientDrawable();
            badge.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
            badge.SetColor(_a.AccentColor);
            check.Background = badge;
            frame.AddView(check, new FrameLayout.LayoutParams((int)Dp(24), (int)Dp(24), GravityFlags.Top | GravityFlags.End));
            column.AddView(frame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            var label = new TextView(_a) { Gravity = GravityFlags.Center };
            label.SetTextColor(_a.TextColor);
            label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
            label.SetSingleLine(true);
            label.Ellipsize = TextUtils.TruncateAt.End;
            column.AddView(label);
            column.Tag = new Cell(image, label, check, frame);
            return column;
        }

        private void Bind(View view, int position)
        {
            var cell = (Cell)view.Tag!;
            var page = _pages[position];
            bool selected = _selected.Contains(page.Id);
            cell.Source = page.Source;
            var bitmap = Thumbnail(page);
            if (bitmap != null) cell.Image.SetImageBitmap(bitmap);
            else cell.Image.SetImageDrawable(new global::Android.Graphics.Drawables.ColorDrawable(_a.IsDark ? Color.ParseColor("#2A2D33") : Color.ParseColor("#DDE1E6")));
            cell.Image.Rotation = page.Rotation;
            cell.Frame.SetBackgroundColor(selected ? _a.AccentColor : Color.Transparent);
            cell.Image.Alpha = selected ? 0.85f : 1f;
            cell.Check.Visibility = selected ? ViewStates.Visible : ViewStates.Gone;
            string number = (position + 1).ToString(CultureInfo.InvariantCulture);
            cell.Label.Text = page.Source == 0 ? $"{number} · blank" : page.Source == position + 1 ? number : $"{number} (was {page.Source})";
            string turned = page.Rotation == 0 ? "" : $", turned {page.Rotation} degrees";
            string what = page.Source == 0 ? "blank page" : $"page {page.Source} of the document";
            view.ContentDescription = $"Page {number}, {what}{turned}{(selected ? ", selected" : "")}";
        }

        private sealed class PageAdapter(PageOrganizer organizer) : BaseAdapter
        {
            public override int Count => organizer._closed ? 0 : organizer._pages.Count;

            public override Java.Lang.Object? GetItem(int position) => null;

            public override long GetItemId(int position) => organizer._pages[position].Id;

            public override bool HasStableIds => true;

            public override View GetView(int position, View? convertView, ViewGroup? parent)
            {
                var view = convertView?.Tag is Cell ? convertView : organizer.MakeCell();
                organizer.Bind(view, position);
                return view;
            }
        }

        /// <summary>A frame as tall as it is wide, so a page turned a quarter still fits its cell.</summary>
        private sealed class SquareFrame : FrameLayout
        {
            public SquareFrame(Context context) : base(context) { }

            private SquareFrame(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

            protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) => base.OnMeasure(widthMeasureSpec, widthMeasureSpec);
        }
    }
}
