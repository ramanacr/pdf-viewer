using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// Edit text and images (the desktop viewer's Ctrl+E, through the engine's <see cref="PdfContentEditor"/>):
/// a screen with one page at a time, its paragraphs and pictures outlined. A tap on a paragraph
/// edits its text (it keeps its fonts, sizes and colours and reflows in its width); a picture can be
/// moved, resized, turned, replaced or deleted; new text and pictures can be added. Each edit is
/// made by the engine on a worker thread as a new revision (an incremental update) and the page is
/// drawn again from it; Undo goes back a revision. Save writes the last revision as a new PDF; the
/// open document is not changed.
/// <para>
/// The document's permissions must allow changing it, a certification that forbids changes is
/// respected, and a signed document is warned about first (the signatures will show the change).
/// Messages never quote the document's text.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The open "Edit text and images" screen, or null.</summary>
    private ContentEditor? _contentEditor;

    /// <summary>A smaller cache than the viewer's: the edit screen draws one page at a time.</summary>
    private static readonly ViewerOptions ContentEditRenderOptions = new()
    {
        MaxFileBytes = 256L * 1024 * 1024,
        ImageCacheBytes = 16L * 1024 * 1024,
        MaxBitmapPixels = 3000L * 3000,
    };

    /// <summary>
    /// The state of the edit screen. The revisions start with the document as it is (with the reader's
    /// other edits); each edit adds one. The engine documents (one to edit, one to draw) are those of
    /// the last revision; they are used on worker threads only, one task at a time (the busy dialog is modal).
    /// </summary>
    private sealed class ContentEditor(ViewerDocument doc) : IDisposable
    {
        public ViewerDocument Doc { get; } = doc;
        public List<byte[]> Revisions { get; } = new();
        /// <summary>What each revision did, for Undo's message (index 0 is unused).</summary>
        public List<string> Descriptions { get; } = new();
        /// <summary>The revision last saved, so leaving does not ask again.</summary>
        public int SavedRevision { get; set; }
        public int Page { get; set; } = 1;
        public int PageCount { get; set; } = 1;
        public PdfEditablePage? Items { get; set; }
        public ViewerPageGeometry Geometry { get; set; }

        public PdfVectorDocument? Engine { get; private set; }
        public ViewerDocument? Renderer { get; private set; }
        private byte[]? _openBytes;

        public byte[] Current => Revisions[^1];
        public bool Unsaved => Revisions.Count > 1 && Revisions.Count - 1 != SavedRevision;

        /// <summary>Opens <paramref name="bytes"/> for editing and drawing, unless it is open already (worker thread).</summary>
        public void Open(byte[] bytes, Func<byte[], PdfVectorDocument> open, string? password, CancellationToken token)
        {
            if (ReferenceEquals(_openBytes, bytes) && Engine != null && Renderer != null) return;
            CloseEngines();
            Engine = open(bytes);
            Renderer = ViewerDocument.OpenAsync(bytes, password, ContentEditRenderOptions, token).GetAwaiter().GetResult();
            _openBytes = bytes;
        }

        private void CloseEngines()
        {
            Engine?.Dispose();
            Renderer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Engine = null;
            Renderer = null;
            _openBytes = null;
        }

        public void Dispose() => CloseEngines();

        // The screen's views.
        public ContentEditView View { get; set; } = null!;
        public View Screen { get; set; } = null!;
        public TextView PageLabel { get; set; } = null!;
        public TextView Hint { get; set; } = null!;
        public TextView Undo { get; set; } = null!;
        public TextView Save { get; set; } = null!;
        public TextView Previous { get; set; } = null!;
        public TextView Next { get; set; } = null!;
        public LinearLayout Actions { get; set; } = null!;

        /// <summary>What the next tap on the page places.</summary>
        public ContentPlacing Placing { get; set; }
        /// <summary>The picture waiting to be placed (Add picture).</summary>
        public PdfImageContent? PendingPicture { get; set; }
        /// <summary>The signed-document warning was accepted.</summary>
        public bool SignedConfirmed { get; set; }
    }

    private enum ContentPlacing { None, Text, Picture }

    /// <summary>A page read from a revision: its picture, how its user space maps to it, and what can be edited.</summary>
    private sealed record ContentPage(Bitmap Bitmap, ViewerPageGeometry Geometry, PdfEditablePage Items, int PageCount);

    // ------------------------------------------------------------------ opening

    /// <summary>Edit text and images: checks what the document allows, then opens the edit screen at the current page.</summary>
    private void EditTextAndImages()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (doc.CertificationLevel is 1 or 2)
        {
            ShowFailure(new UserMessageException("This document is certified, and its certification does not allow changing its pages."));
            return;
        }
        if (doc.IsSigned && doc.EditRestriction is { } restriction)
        {
            ShowFailure(new UserMessageException(restriction));
            return;
        }
        if (CurrentBytes() is not { } bytes) return;
        var editor = new ContentEditor(doc) { Page = Math.Clamp(_pages.CurrentPage, 1, doc.PageCount), PageCount = doc.PageCount };
        editor.Revisions.Add(bytes);
        editor.Descriptions.Add("");
        _contentEditor = editor;
        editor.Screen = BuildContentEditScreen(editor);
        ShowToolOverlay(editor.Screen, () => ContentEditClosed(editor));
        // After the first layout, so the page is drawn at the size it is shown.
        editor.View.Post(() => _ = LoadContentPageAsync(editor, first: true));
    }

    /// <summary>The screen: a bar (Back, title, Undo, Save), the page row (‹ page ›), a hint, the page, and the actions.</summary>
    private View BuildContentEditScreen(ContentEditor editor)
    {
        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical, Clickable = true };
        column.SetBackgroundColor(ScreenColor);

        editor.Undo = ToolButton("Undo", "Undo the last edit", (_, _) => _ = UndoContentEditAsync(editor));
        editor.Save = ToolButton("Save", "Save the edited document as a new PDF", (_, _) => SaveContentEdits(editor));
        column.AddView(ToolScreenBar("Edit text and images", out _, editor.Undo, editor.Save));

        var pageRow = Bar();
        editor.Previous = ToolButton("‹", "Previous page", (_, _) => _ = GoToContentPageAsync(editor, editor.Page - 1));
        editor.Next = ToolButton("›", "Next page", (_, _) => _ = GoToContentPageAsync(editor, editor.Page + 1));
        editor.Previous.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 22);
        editor.Next.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 22);
        editor.Previous.SetTextColor(AccentColor);
        editor.Next.SetTextColor(AccentColor);
        editor.PageLabel = new TextView(this) { Gravity = GravityFlags.Center };
        editor.PageLabel.SetTextColor(TextColor);
        editor.PageLabel.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        pageRow.AddView(editor.Previous);
        pageRow.AddView(editor.PageLabel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        pageRow.AddView(editor.Next);
        column.AddView(pageRow);

        editor.Hint = new TextView(this);
        editor.Hint.SetTextColor(IsDark ? Color.ParseColor("#A9AEB6") : Color.ParseColor("#5A5F66"));
        editor.Hint.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        editor.Hint.SetPadding((int)Dp(16), 0, (int)Dp(16), (int)Dp(4));
        editor.Hint.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        column.AddView(editor.Hint);

        editor.View = new ContentEditView(this, AccentColor, IsDark ? Color.ParseColor("#FFB74D") : Color.ParseColor("#E65100"));
        editor.View.Tapped += (box, point) => ContentTapped(editor, box, point);
        editor.View.Moved += (box, dx, dy) => _ = MoveContentAsync(editor, box, dx, dy);
        editor.View.Resized += (box, to) => _ = ResizeContentAsync(editor, box, to);
        column.AddView(editor.View, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var scroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        scroll.SetBackgroundColor(IsDark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3"));
        editor.Actions = Bar();
        scroll.AddView(editor.Actions);
        column.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        UpdateContentChrome(editor);
        return column;
    }

    /// <summary>The bars as the state is: page label, Undo and Save, the hint and the actions for the selection.</summary>
    private void UpdateContentChrome(ContentEditor editor)
    {
        editor.PageLabel.Text = $"Page {editor.Page} of {editor.PageCount}";
        SetContentEnabled(editor.Previous, editor.Page > 1 && editor.Placing == ContentPlacing.None);
        SetContentEnabled(editor.Next, editor.Page < editor.PageCount && editor.Placing == ContentPlacing.None);
        SetContentEnabled(editor.Undo, editor.Revisions.Count > 1);
        SetContentEnabled(editor.Save, editor.Revisions.Count > 1);
        var selected = editor.View.Current;
        editor.Hint.Text = editor.Placing switch
        {
            ContentPlacing.Text => "Tap where the new text should start.",
            ContentPlacing.Picture => "Tap where the picture should go.",
            _ when selected?.Kind == PdfEditTarget.Text => "Paragraph selected: drag it to move it, or edit or delete it below.",
            _ when selected?.Kind == PdfEditTarget.Image => "Picture selected: drag it to move it, drag the round handle to resize it, or use the buttons below.",
            _ => "Tap a paragraph to edit its text, or a picture to move, resize, turn, replace or delete it.",
        };
        int texts = editor.Items?.Texts.Count ?? 0, images = editor.Items?.Images.Count ?? 0;
        editor.View.ContentDescription = $"Page {editor.Page}: {texts} paragraphs and {images} pictures. Use the List button to choose one.";

        editor.Actions.RemoveAllViews();
        if (editor.Placing != ContentPlacing.None)
        {
            AddContentAction(editor, "Cancel", "Stop placing", () =>
            {
                editor.Placing = ContentPlacing.None;
                editor.PendingPicture = null;
                editor.View.Placing = false;
                UpdateContentChrome(editor);
            });
            return;
        }
        if (selected is { Kind: PdfEditTarget.Text } text)
        {
            AddContentAction(editor, "Edit text", "Edit the paragraph's text", () => EditContentText(editor, text));
            AddContentAction(editor, "Delete", "Delete the paragraph", () => _ = DeleteContentAsync(editor, text));
            AddContentAction(editor, "Done", "Deselect the paragraph", () => SelectContent(editor, null));
            return;
        }
        if (selected is { Kind: PdfEditTarget.Image } image)
        {
            AddContentAction(editor, "Smaller", "Make the picture smaller", () => _ = ScaleContentAsync(editor, image, 0.8));
            AddContentAction(editor, "Bigger", "Make the picture bigger", () => _ = ScaleContentAsync(editor, image, 1.25));
            AddContentAction(editor, "Turn", "Turn the picture a quarter turn clockwise", () => _ = TurnContentAsync(editor, image));
            AddContentAction(editor, "Replace", "Replace the picture with another one", () => ReplaceContentPicture(editor, image));
            AddContentAction(editor, "Delete", "Delete the picture", () => _ = DeleteContentAsync(editor, image));
            AddContentAction(editor, "Done", "Deselect the picture", () => SelectContent(editor, null));
            return;
        }
        AddContentAction(editor, "Add text", "Add new text: tap the page where it should start", () => BeginContentPlacing(editor, ContentPlacing.Text));
        AddContentAction(editor, "Add picture", "Add a picture from this device", () => PickContentPicture(editor));
        AddContentAction(editor, "List", "Choose a paragraph or picture of this page from a list", () => ShowContentList(editor));
    }

    private void AddContentAction(ContentEditor editor, string text, string description, Action run)
    {
        var button = ToolButton(text, description, (_, _) => run());
        button.SetTextColor(AccentColor);
        editor.Actions.AddView(button);
    }

    private static void SetContentEnabled(View view, bool enabled)
    {
        view.Enabled = enabled;
        view.Alpha = enabled ? 1f : 0.4f;
    }

    private void SelectContent(ContentEditor editor, ContentEditView.Box? box)
    {
        editor.View.Select(box);
        UpdateContentChrome(editor);
    }

    // ------------------------------------------------------------------ pages

    /// <summary>The size the page is drawn at: the view's, or the screen's before the first layout.</summary>
    private (int Width, int Height) ContentViewSize(ContentEditor editor)
    {
        int w = editor.View.Width, h = editor.View.Height;
        if (w <= 0 || h <= 0)
        {
            var metrics = Resources?.DisplayMetrics;
            w = metrics?.WidthPixels ?? 1080;
            h = (int)((metrics?.HeightPixels ?? 1920) * 0.7);
        }
        return (Math.Max(1, w - (int)Dp(16)), Math.Max(1, h - (int)Dp(16)));
    }

    /// <summary>
    /// Reads and draws a page of a revision (worker thread): what can be edited on it and its picture
    /// fitted into <paramref name="width"/> × <paramref name="height"/> pixels.
    /// </summary>
    private ContentPage ReadContentPage(ContentEditor editor, byte[] bytes, int page, int width, int height, string? password, CancellationToken token)
    {
        editor.Open(bytes, OpenForTool, password, token);
        var engine = editor.Engine!;
        int count = engine.PageTree.Pages.Count;
        page = Math.Clamp(page, 1, count);
        var node = engine.PageTree.Pages[page - 1];
        var geometry = new ViewerPageGeometry(node.MediaBox, node.CropBox, node.RotationDegrees);
        var items = PdfContentEditor.Read(engine, page, SystemFontCatalog.Installed);
        token.ThrowIfCancellationRequested();
        double scale = Math.Min(width / Math.Max(1, geometry.ShownWidth), height / Math.Max(1, geometry.ShownHeight));
        var rendered = editor.Renderer!.RenderPageAsync(page, Math.Max(0.05, scale), false, token).GetAwaiter().GetResult();
        var colors = new int[rendered.Width * rendered.Height];
        Buffer.BlockCopy(rendered.Pixels.Array!, rendered.Pixels.Offset, colors, 0, colors.Length * 4);
        var bitmap = Bitmap.CreateBitmap(colors, rendered.Width, rendered.Height, Bitmap.Config.Argb8888!)
                     ?? throw new UserMessageException("The page could not be drawn.");
        return new ContentPage(bitmap, geometry, items, count);
    }

    /// <summary>Loads the current page of the last revision; the first time, also checks the document's permissions.</summary>
    private async Task LoadContentPageAsync(ContentEditor editor, bool first = false)
    {
        var bytes = editor.Current;
        int page = editor.Page;
        var (w, h) = ContentViewSize(editor);
        string? password = _password;
        var loaded = await RunBusyAsync(first ? "Reading the page…" : "Drawing the page…", token =>
        {
            if (first)
            {
                editor.Open(bytes, OpenForTool, password, token);
                if (!PermissionsOf(editor.Engine!).CanModify)
                    throw new UserMessageException("This document's security settings do not allow editing its text and images.");
            }
            return ReadContentPage(editor, bytes, page, w, h, password, token);
        });
        if (!ReferenceEquals(editor, _contentEditor))
        {
            loaded?.Bitmap.Recycle();
            return;
        }
        if (loaded == null)
        {
            if (first) CloseContentEditorNow(editor);
            return;
        }
        ShowContentPage(editor, loaded);
    }

    /// <summary>Shows a page that was read: its picture and its boxes (on the page as shown).</summary>
    private void ShowContentPage(ContentEditor editor, ContentPage page, (PdfEditTarget Kind, float X, float Y)? reselect = null)
    {
        editor.Items = page.Items;
        editor.Geometry = page.Geometry;
        editor.PageCount = page.PageCount;
        editor.Page = page.Items.PageNumber;
        var g = page.Geometry;
        var boxes = new List<ContentEditView.Box>();
        foreach (var t in page.Items.Texts) boxes.Add(new ContentEditView.Box(PdfEditTarget.Text, t.Id, ShownRect(g, t.Bounds)));
        foreach (var i in page.Items.Images) boxes.Add(new ContentEditView.Box(PdfEditTarget.Image, i.Id, ShownRect(g, i.Bounds)));
        editor.View.SetPage(page.Bitmap, (float)g.ShownWidth, (float)g.ShownHeight, boxes);
        if (reselect is { } r)
        {
            // The item an edit moved or turned (ids change with each revision): the one of its kind nearest to where it went.
            var near = boxes.Where(b => b.Kind == r.Kind)
                .Select(b => (Box: b, Distance: Math.Abs(b.Shown.CenterX() - r.X) + Math.Abs(b.Shown.CenterY() - r.Y)))
                .Where(c => c.Distance < 24)
                .OrderBy(c => c.Distance)
                .FirstOrDefault();
            if (near.Box != null) editor.View.Select(near.Box);
        }
        UpdateContentChrome(editor);
    }

    /// <summary>A user-space rectangle on the page as shown (points from the top-left).</summary>
    private static RectF ShownRect(ViewerPageGeometry g, PdfEngine.Geometry.PdfRect r)
    {
        var a = g.ToShown(r.X, r.Y);
        var b = g.ToShown(r.X + r.Width, r.Y + r.Height);
        return new RectF((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y));
    }

    /// <summary>A rectangle of the page as shown in user space.</summary>
    private static PdfEngine.Geometry.PdfRect UserRect(ViewerPageGeometry g, RectF r)
    {
        var a = g.ToUser(r.Left, r.Top);
        var b = g.ToUser(r.Right, r.Bottom);
        return new PdfEngine.Geometry.PdfRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private async Task GoToContentPageAsync(ContentEditor editor, int page)
    {
        if (page < 1 || page > editor.PageCount || page == editor.Page) return;
        editor.Page = page;
        await LoadContentPageAsync(editor);
    }

    // ------------------------------------------------------------------ leaving

    /// <summary>The screen went (Back): with unsaved edits, asks first; "Keep editing" brings the screen back.</summary>
    private void ContentEditClosed(ContentEditor editor)
    {
        if (!ReferenceEquals(editor, _contentEditor)) return;
        if (!editor.Unsaved || !ReferenceEquals(editor.Doc, _doc))
        {
            CloseContentEditorNow(editor);
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Leave without saving?")!
            .SetMessage("Your edits to the text and images are not saved. Save writes them to a new PDF; this document is not changed.")!
            .SetCancelable(false)!
            .SetPositiveButton("Keep editing", (_, _) => ReopenContentEditor(editor))!
            .SetNeutralButton("Save…", (_, _) =>
            {
                ReopenContentEditor(editor);
                SaveContentEdits(editor);
            })!
            .SetNegativeButton("Discard", (_, _) => CloseContentEditorNow(editor))!
            .Show();
    }

    private void ReopenContentEditor(ContentEditor editor)
    {
        if (!ReferenceEquals(editor, _contentEditor)) return;
        ShowToolOverlay(editor.Screen, () => ContentEditClosed(editor));
    }

    /// <summary>Ends the edit screen: the overlay goes, the bitmap and the engine documents are released.</summary>
    private void CloseContentEditorNow(ContentEditor editor)
    {
        if (!ReferenceEquals(editor, _contentEditor)) return;
        _contentEditor = null;
        if (ReferenceEquals(_toolOverlay, editor.Screen))
        {
            _toolOverlayClosed = null;
            CloseToolOverlay();
        }
        editor.View.Release();
        // Not on the UI thread: closing may wait for the renderer's lock; no task uses them now.
        Task.Run(editor.Dispose);
    }
}
