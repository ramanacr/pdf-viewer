using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// The reader: a toolbar with a menu, a search bar, a selection bar, the pages and the page grid.
/// A document comes from the system document picker (Storage Access Framework,
/// ACTION_OPEN_DOCUMENT), from another app's "Open with" (ACTION_VIEW) or from the recent list;
/// it is read into memory. Edits (annotations, form values; MainActivity.Edits.cs) are saved back into
/// that file when Android grants write access to it, or as a copy the reader picks with the system's
/// save dialog. The activity is kept across rotation and
/// dark-mode switches; when Android ends the process in the background, the saved state reopens
/// the document at the same place, with its unsaved edits (MainActivity.KeptEdits.cs).
/// </summary>
[Activity(Label = "PDF Viewer (preview)", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault], DataScheme = "content", DataMimeType = "application/pdf")]
public sealed partial class MainActivity : Activity
{
    private const int OpenDocumentRequest = 1;

    /// <summary>A phone's memory is smaller than a desktop's: files up to 256 MB, bitmaps up to 3000 × 3000 pixels.</summary>
    private static readonly ViewerOptions Options = new()
    {
        MaxFileBytes = 256L * 1024 * 1024,
        ImageCacheBytes = 64L * 1024 * 1024,
        MaxBitmapPixels = 3000L * 3000,
    };

    // Saved-state keys: the document's location and the view, never its text or a password.
    private const string StateUri = "doc.uri", StatePage = "view.page", StateU = "view.u", StateV = "view.v", StateZoom = "view.zoom", StateNight = "view.night";

    private PageView _pages = null!;
    private ThumbnailView _thumbnails = null!;
    private TextView _title = null!, _pageIndicator = null!, _searchCount = null!, _emptyText = null!, _selectionLabel = null!;
    private TextView _openButton = null!, _searchButton = null!, _menuButton = null!, _prevButton = null!, _nextButton = null!, _closeSearchButton = null!;
    private TextView _copyButton = null!, _selectPageButton = null!, _closeSelectionButton = null!;
    private LinearLayout _toolbar = null!, _searchBar = null!, _selectionBar = null!, _empty = null!, _recentList = null!;
    private EditText _searchText = null!;
    private ViewerDocument? _doc;
    private string? _fileName;
    private Uri? _uri;
    private List<ViewerSearchHit> _hits = new();
    private int _hitIndex = -1;
    private CancellationTokenSource? _search;
    private RecentDocuments _recent = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _recent = new RecentDocuments(System.IO.Path.Combine(FilesDir!.AbsolutePath, "recent.json"));
        SetContentView(BuildLayout());
        ApplyTheme();
        ShowDocumentState();

        // After Android ended the process: the document saved in the state, at the saved place.
        if (savedInstanceState?.GetString(StateUri) is { } saved && Uri.Parse(saved) is { } savedUri)
        {
            _pages.NightPages = savedInstanceState.GetBoolean(StateNight);
            var view = (new PageAnchor(savedInstanceState.GetInt(StatePage, 1), savedInstanceState.GetDouble(StateU), savedInstanceState.GetDouble(StateV)),
                savedInstanceState.GetDouble(StateZoom, 1));
            // With the unsaved edits kept for it (MainActivity.KeptEdits.cs), read on a worker thread.
            _ = ReopenAfterProcessDeathAsync(savedUri, saved, view);
        }
        else if (Intent is { Action: Intent.ActionView, Data: { } uri })
        {
            // A new start keeps edits an earlier process kept: they are offered when their document opens.
            _ = OpenUriAsync(uri, null);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is { Action: Intent.ActionView, Data: { } uri }) AfterDiscard(() => _ = OpenUriAsync(uri, null));
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        // During a save into the file, a new instance must not read it half-written: no document is kept.
        if (_uri == null || _doc == null || _savingBack) return;
        KeepEdits();
        outState.PutString(StateUri, _uri.ToString());
        outState.PutBoolean(StateNight, _pages.NightPages);
        if (_pages.SavedView is { } view)
        {
            outState.PutInt(StatePage, view.TopLeft.Page);
            outState.PutDouble(StateU, view.TopLeft.U);
            outState.PutDouble(StateV, view.TopLeft.V);
            outState.PutDouble(StateZoom, view.Zoom);
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        RememberPosition();
    }

    /// <summary>In the background Android may end the process: unsaved edits are kept (leaving with Back has saved or discarded them).</summary>
    protected override void OnStop()
    {
        if (IsFinishing) ForgetOurKeptEdits();
        else KeepEdits();
        base.OnStop();
    }

    /// <summary>The page and zoom of the open document, for the recent list.</summary>
    private void RememberPosition()
    {
        if (_uri != null && _doc != null && _pages.CurrentPage > 0)
            _recent.Moved(_uri.ToString()!, _pages.CurrentPage, Math.Round(_pages.Zoom, 3));
    }

    // ------------------------------------------------------------------ layout

    private float Dp(float dp) => dp * (Resources?.DisplayMetrics?.Density ?? 1f);

    private View BuildLayout()
    {
        var root = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        root.SetFitsSystemWindows(true);

        _toolbar = Bar();
        _openButton = ToolButton("Open", "Open a PDF", (_, _) => PickDocument());
        _title = new TextView(this) { Text = "PDF Viewer" };
        _title.SetSingleLine(true);
        _title.Ellipsize = TextUtils.TruncateAt.Middle;
        _title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        _title.SetPadding((int)Dp(8), 0, (int)Dp(8), 0);
        _pageIndicator = ToolButton("", "Go to page", (_, _) => AskPage());
        _searchButton = ToolButton("Search", "Search the document", (_, _) => ShowSearch(true));
        _annotateButton = ToolButton("✎", "Annotate: draw, shapes, notes, text boxes, undo and redo", (_, _) => ShowAnnotateBar(_annotateBar.Visibility != ViewStates.Visible));
        _menuButton = ToolButton("⋮", "More: outline, pages, annotations, save, save a copy, share, night pages, recent files", (_, _) => ShowMenu());
        _toolbar.AddView(_openButton);
        _toolbar.AddView(_title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _toolbar.AddView(_pageIndicator);
        _toolbar.AddView(_searchButton);
        _toolbar.AddView(_annotateButton);
        _toolbar.AddView(_menuButton);
        root.AddView(_toolbar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _searchBar = Bar();
        _searchBar.Visibility = ViewStates.Gone;
        _searchText = new EditText(this) { Hint = "Find in document", ImeOptions = ImeAction.Search };
        _searchText.SetSingleLine(true);
        _searchText.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        _searchText.EditorAction += (_, e) =>
        {
            e.Handled = e.ActionId == ImeAction.Search || e.Event?.KeyCode == Keycode.Enter;
            if (e.Handled) _ = SearchAsync(_searchText.Text ?? "");
        };
        _searchCount = new TextView(this);
        _searchCount.SetPadding((int)Dp(8), 0, (int)Dp(8), 0);
        _prevButton = ToolButton("‹", "Previous match", (_, _) => StepHit(-1));
        _nextButton = ToolButton("›", "Next match", (_, _) => StepHit(1));
        _closeSearchButton = ToolButton("✕", "Close search", (_, _) => ShowSearch(false));
        _searchBar.AddView(_searchText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _searchBar.AddView(_searchCount);
        _searchBar.AddView(_prevButton);
        _searchBar.AddView(_nextButton);
        _searchBar.AddView(_closeSearchButton);
        root.AddView(_searchBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(BuildAnnotateBar(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _selectionBar = Bar();
        _selectionBar.Visibility = ViewStates.Gone;
        _selectionLabel = new TextView(this) { Text = "Text selected" };
        _selectionLabel.SetPadding((int)Dp(8), 0, (int)Dp(8), 0);
        _copyButton = ToolButton("Copy", "Copy the selected text", (_, _) => CopySelection());
        _selectPageButton = ToolButton("All", "Select all text of this page", (_, _) => _pages.SelectPage());
        _closeSelectionButton = ToolButton("✕", "End the selection", (_, _) => _pages.ClearSelection());
        _selectionBar.AddView(_selectionLabel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _selectionBar.AddView(_copyButton);
        _selectionBar.AddView(_selectPageButton);
        AddMarkupButtons(_selectionBar);
        _selectionBar.AddView(_closeSelectionButton);

        var frame = new FrameLayout(this);
        _pages = new PageView(this);
        _pages.PageChanged += (page, count) => _pageIndicator.Text = count > 0 ? $"{page} / {count}" : "";
        _pages.Problem += message => Toast.MakeText(this, message, ToastLength.Short)?.Show();
        _pages.LinkTapped += OnLinkTapped;
        _pages.SelectionChanged += active => _selectionBar.Visibility = active ? ViewStates.Visible : ViewStates.Gone;
        ConnectEdits();
        frame.AddView(_pages, Fill());
        _thumbnails = new ThumbnailView(this) { Visibility = ViewStates.Gone };
        _thumbnails.PageChosen += page =>
        {
            ShowThumbnails(false);
            _pages.GoToPage(page);
        };
        frame.AddView(_thumbnails, Fill());

        // The empty state: what the app is, and the recent files.
        _empty = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        _empty.SetGravity(GravityFlags.CenterHorizontal);
        _empty.SetPadding((int)Dp(32), (int)Dp(48), (int)Dp(32), (int)Dp(16));
        _emptyText = new TextView(this)
        {
            Text = "Open a PDF to read it.\n\nThis app runs its own C# PDF engine on the device. It has no internet permission: documents never leave the phone.",
            Gravity = GravityFlags.Center,
        };
        _emptyText.Click += (_, _) => PickDocument();
        _empty.AddView(_emptyText);
        _recentList = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        _recentList.SetPadding(0, (int)Dp(24), 0, 0);
        var scroll = new ScrollView(this);
        scroll.AddView(_recentList);
        _empty.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        frame.AddView(_empty, Fill());
        // Over the pages, so that they do not move when a selection starts.
        _selectionBar.Elevation = Dp(4);
        frame.AddView(_selectionBar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));
        root.AddView(frame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return root;

        static FrameLayout.LayoutParams Fill() => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
    }

    private LinearLayout Bar()
    {
        var bar = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding((int)Dp(4), (int)Dp(4), (int)Dp(4), (int)Dp(4));
        return bar;
    }

    private TextView ToolButton(string text, string description, EventHandler onClick)
    {
        var button = new TextView(this) { Text = text, ContentDescription = description, Clickable = true, Focusable = true, Gravity = GravityFlags.Center };
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        // 48 dp: the touch target size Android's accessibility guidelines ask for.
        button.SetMinWidth((int)Dp(48));
        button.SetMinHeight((int)Dp(48));
        button.SetPadding((int)Dp(10), 0, (int)Dp(10), 0);
        button.Click += onClick;
        return button;
    }

    private bool IsDark => ((Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;

    private Color TextColor => IsDark ? Color.ParseColor("#E6E8EB") : Color.ParseColor("#1A1C1E");
    private Color AccentColor => IsDark ? Color.ParseColor("#8AB4F8") : Color.ParseColor("#1F5FA8");

    /// <summary>Colours of the chrome for the system's light or dark theme (views made in code do not follow a theme switch by themselves).</summary>
    private void ApplyTheme()
    {
        bool dark = IsDark;
        var bar = dark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3");
        var text = TextColor;
        var accent = AccentColor;
        foreach (var b in new[] { _toolbar, _searchBar, _selectionBar, _annotateBar }) b.SetBackgroundColor(bar);
        _empty.SetBackgroundColor(dark ? Color.ParseColor("#121316") : Color.ParseColor("#F7F8FA"));
        foreach (var t in new[] { _title, _searchCount, _searchText, _emptyText, _selectionLabel }) t.SetTextColor(text);
        _searchText.SetHintTextColor(dark ? Color.ParseColor("#8C9199") : Color.ParseColor("#6C717A"));
        foreach (var b in new[] { _openButton, _pageIndicator, _searchButton, _annotateButton, _menuButton, _prevButton, _nextButton, _closeSearchButton, _copyButton, _selectPageButton, _closeSelectionButton }.Concat(EditButtons))
            b.SetTextColor(accent);
        ShowRecent();
        // Android 15 draws apps edge to edge and ignores bar colours; earlier versions take them.
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            Window?.SetStatusBarColor(bar);
            Window?.SetNavigationBarColor(bar);
        }
        // Dark icons on the light bars, light icons on the dark ones (the theme set them only at start).
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            int light = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
            Window?.InsetsController?.SetSystemBarsAppearance(dark ? 0 : light, light);
        }
        _pages.SetDark(dark);
        _thumbnails.SetDark(dark);
    }

    private void ShowDocumentState()
    {
        bool open = _doc != null;
        _empty.Visibility = open ? ViewStates.Gone : ViewStates.Visible;
        _pages.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        if (!open) ShowThumbnails(false);
        _searchButton.Enabled = _pageIndicator.Enabled = _annotateButton.Enabled = open;
        _searchButton.Alpha = _annotateButton.Alpha = open ? 1f : 0.4f;
        if (!open) ShowAnnotateBar(false);
        _title.Text = !open ? "PDF Viewer" : _doc!.Title ?? _fileName ?? "Document";
        if (!open)
        {
            _pageIndicator.Text = "";
            ShowRecent();
        }
    }

    /// <summary>The recent files under the empty state: tap to open, long press to remove.</summary>
    private void ShowRecent()
    {
        if (_recentList == null) return;
        _recentList.RemoveAllViews();
        if (_recent.Items.Count == 0) return;
        var heading = new TextView(this) { Text = "Recent files" };
        heading.SetTextColor(TextColor);
        heading.SetTypeface(null, TypefaceStyle.Bold);
        heading.SetPadding(0, 0, 0, (int)Dp(8));
        _recentList.AddView(heading);
        foreach (var item in _recent.Items)
        {
            var entry = item;
            var row = new TextView(this)
            {
                Text = $"{entry.Name}  ·  page {entry.Page}",
                Clickable = true,
                Focusable = true,
                ContentDescription = $"Open {entry.Name} at page {entry.Page}",
            };
            row.SetTextColor(AccentColor);
            row.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
            row.SetSingleLine(true);
            row.Ellipsize = TextUtils.TruncateAt.Middle;
            row.SetMinHeight((int)Dp(44));
            row.Gravity = GravityFlags.CenterVertical;
            row.Click += (_, _) => AfterDiscard(() => OpenRecent(entry));
            row.LongClick += (_, _) => ForgetRecent(entry.Location);
            _recentList.AddView(row);
        }
    }

    // ------------------------------------------------------------------ menu

    private void ShowMenu()
    {
        var menu = new PopupMenu(this, _menuButton);
        bool open = _doc != null;
        var items = menu.Menu!;
        items.Add(0, 1, 1, "Outline")!.SetEnabled(open);
        items.Add(0, 2, 2, "Pages")!.SetEnabled(open);
        items.Add(0, 3, 3, "Share")!.SetEnabled(open && _uri != null);
        var night = items.Add(0, 4, 4, "Night pages")!;
        night.SetCheckable(true);
        night.SetChecked(_pages.NightPages);
        night.SetEnabled(open);
        items.Add(0, 5, 5, "Recent files")!.SetEnabled(_recent.Items.Count > 0);
        items.Add(0, 6, 3, "Annotations")!.SetEnabled(open && _session != null);
        // Save writes the opened file (it offers a copy when the file is read-only); enabled with unsaved edits.
        var save = items.Add(0, 8, 3, "Save")!;
        save.SetEnabled(open && _session?.IsDirty == true);
        var copy = items.Add(0, 7, 3, "Save a copy")!;
        copy.SetEnabled(open);
        var export = items.Add(0, 9, 3, "Export comments (XFDF)")!;
        export.SetEnabled(open && _session != null);
        var import = items.Add(0, 10, 3, "Import comments (XFDF)")!;
        import.SetEnabled(open && CanAnnotate);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            save.SetContentDescription("Save the changes into the opened file");
            copy.SetContentDescription("Save the document with the changes as a new file");
            export.SetContentDescription("Export the annotations and replies as an XFDF file");
            import.SetContentDescription("Add the annotations of an XFDF file to this document");
        }
        menu.MenuItemClick += (_, e) =>
        {
            switch (e.Item?.ItemId)
            {
                case 1: _ = ShowOutlineAsync(); break;
                case 2: ShowThumbnails(true); break;
                case 3: ShareChecked(); break;
                case 4: _pages.NightPages = !_pages.NightPages; break;
                case 5: AskRecent(); break;
                case 6: ShowAnnotationList(); break;
                case 7: PickSaveCopy(); break;
                case 8: Save(); break;
                case 9: PickXfdfExport(); break;
                case 10: PickXfdfImport(); break;
            }
        };
        menu.Show();
    }

    private async Task ShowOutlineAsync()
    {
        var doc = _doc;
        if (doc == null) return;
        IReadOnlyList<ViewerOutlineLine> lines;
        try
        {
            lines = ViewerOutline.Flatten(await Task.Run(() => doc.GetOutlineAsync()));
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Toast.MakeText(this, $"The outline could not be read ({ex.GetType().Name}).", ToastLength.Short)?.Show();
            return;
        }
        if (!ReferenceEquals(doc, _doc)) return;
        if (lines.Count == 0)
        {
            Toast.MakeText(this, "This document has no outline.", ToastLength.Short)?.Show();
            return;
        }
        // Indented by depth; an entry that goes nowhere in this document says so.
        var items = lines.Select(l => new string(' ', Math.Min(l.Depth, 6)) + l.Title + (l.Page is { } p ? $"  ({p})" : "")).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle("Outline")!
            .SetItems(items, (_, e) =>
            {
                if (lines[e.Which].Page is { } page) _pages.GoToPage(page);
                else Toast.MakeText(this, "This entry does not go to a page of this document.", ToastLength.Short)?.Show();
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    private void ShowThumbnails(bool show)
    {
        if (show && _doc == null) return;
        _thumbnails.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (show)
        {
            _pages.ClearSelection();
            _thumbnails.Show(_doc!, _pages.CurrentPage, _pages.NightPages);
        }
    }

    /// <summary>
    /// Shares the open PDF with the system share sheet: the document's own content URI, with read
    /// access for the app the user chooses. The app needs no permission and no file of its own for this.
    /// </summary>
    private void Share()
    {
        if (_uri == null) return;
        var send = new Intent(Intent.ActionSend);
        send.SetType("application/pdf");
        send.PutExtra(Intent.ExtraStream, _uri);
        send.ClipData = ClipData.NewRawUri(_fileName ?? "PDF", _uri);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            StartActivity(Intent.CreateChooser(send, "Share PDF"));
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "No app on this device can receive a PDF.", ToastLength.Short)?.Show();
        }
    }

    private void AskRecent()
    {
        var items = _recent.Items.ToList();
        if (items.Count == 0) return;
        new AlertDialog.Builder(this)
            .SetTitle("Recent files")!
            .SetItems(items.Select(d => $"{d.Name}  ·  page {d.Page}").ToArray(), (_, e) => AfterDiscard(() => OpenRecent(items[e.Which])))!
            .SetNeutralButton("Clear list", (_, _) =>
            {
                foreach (var d in items) ReleasePermission(d.Location);
                _recent.Clear();
                ShowRecent();
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    private void OpenRecent(RecentDocument entry)
    {
        if (Uri.Parse(entry.Location) is not { } uri) return;
        RememberPosition();
        _ = OpenUriAsync(uri, (new PageAnchor(entry.Page, 0, 0), entry.Zoom), fromRecent: true);
    }

    private void ForgetRecent(string location)
    {
        ReleasePermission(location);
        _recent.Remove(location);
        ShowRecent();
    }

    // ------------------------------------------------------------------ selection

    /// <summary>Copies the selection to the clipboard; the document's copy permission was checked when it was made.</summary>
    private void CopySelection()
    {
        if (_doc is not { CanCopy: true } || !_pages.HasSelection) return;
        string text = _pages.SelectedText;
        if (text.Length == 0) return;
        var clipboard = GetSystemService(ClipboardService)?.JavaCast<global::Android.Content.ClipboardManager>();
        if (clipboard == null) return;
        clipboard.PrimaryClip = ClipData.NewPlainText("Text from a PDF", text);
        // Android 13 and later show their own confirmation.
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            Toast.MakeText(this, "Copied", ToastLength.Short)?.Show();
        _pages.ClearSelection();
    }

    // ------------------------------------------------------------------ opening

    private void PickDocument() => AfterDiscard(PickDocumentNow);

    private void PickDocumentNow()
    {
        // The system picker grants this app access to the one file chosen (read, and write where the
        // provider allows it, so Save can write the edits back); no storage permission is needed.
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/pdf");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, OpenDocumentRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == SaveCopyRequest)
        {
            SaveDialogAnswered(resultCode == Result.Ok ? data?.Data : null);
            return;
        }
        if (requestCode == ExportXfdfRequest)
        {
            XfdfExportAnswered(resultCode == Result.Ok ? data?.Data : null);
            return;
        }
        if (requestCode == ImportXfdfRequest)
        {
            if (resultCode == Result.Ok && data?.Data is { } source) _ = ImportXfdfAsync(source);
            return;
        }
        if (requestCode == OpenDocumentRequest && resultCode == Result.Ok && data?.Data is { } uri)
        {
            RememberPosition();
            // Keep access across restarts (the picker offers a persistable grant), so the recent
            // list and a restore after Android ended the process can open the file again. Write access
            // too, when the picker granted it, so Save works on a file reopened from the recent list.
            var granted = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            try
            {
                ContentResolver?.TakePersistableUriPermission(uri, granted | ActivityFlags.GrantReadUriPermission);
            }
            catch (Java.Lang.SecurityException)
            {
                try
                {
                    ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
                }
                catch (Java.Lang.SecurityException)
                {
                    // Not persistable: the file opens now, but is not kept in the recent list.
                }
            }
            _ = OpenUriAsync(uri, null);
        }
    }

    private bool HasPersistedPermission(Uri uri) =>
        ContentResolver?.PersistedUriPermissions?.Any(p => p.IsReadPermission && p.Uri?.Equals(uri) == true) == true;

    private void ReleasePermission(string location)
    {
        try
        {
            if (Uri.Parse(location) is not { } uri) return;
            var held = ContentResolver?.PersistedUriPermissions?.FirstOrDefault(p => p.Uri?.Equals(uri) == true);
            if (held == null) return;
            // Both grants when both were taken.
            var flags = (held.IsReadPermission ? ActivityFlags.GrantReadUriPermission : 0)
                        | (held.IsWritePermission ? ActivityFlags.GrantWriteUriPermission : 0);
            if (flags != 0) ContentResolver?.ReleasePersistableUriPermission(uri, flags);
        }
        catch (Java.Lang.SecurityException)
        {
        }
    }

    /// <summary>
    /// Opens a document; <paramref name="view"/> is the place to show (a saved state or the recent
    /// list); <paramref name="restore"/> holds unsaved edits kept while Android ended the app.
    /// </summary>
    private async Task OpenUriAsync(Uri uri, (PageAnchor TopLeft, double Zoom)? view, bool fromRecent = false, ViewerEditSnapshot? restore = null)
    {
        byte[] bytes;
        ViewerFileStamp stamp;
        byte[]? original = null;
        ViewerEditSnapshot? kept = null;
        bool afterDeath = restore != null;
        try
        {
            // The size and time that later tell whether another app changed the file: taken with the read.
            // Kept edits (from the saved state, or any on storage) and the bytes they fit, hashed here too.
            (bytes, stamp, kept, original) = await Task.Run(() =>
            {
                var file = ReadAll(uri);
                var found = restore ?? ReadKeptEditsNow();
                return (file, Stamp(uri), found, found?.OriginalOf(file));
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The kept edits were for this file, which cannot be read any more (a revoked grant, a deleted file).
            if (afterDeath) ForgetKeptEdits();
            bool gone = ex is Java.Lang.SecurityException or Java.IO.FileNotFoundException or FileNotFoundException;
            if (gone && _recent.Find(uri.ToString()!) != null)
            {
                ForgetRecent(uri.ToString()!);
                Toast.MakeText(this, "This file can no longer be opened here, so it was taken off the recent list.", ToastLength.Long)?.Show();
                return;
            }
            Toast.MakeText(this, ex is FileTooLargeException ? ex.Message : $"The file could not be read ({ex.GetType().Name}).", ToastLength.Long)?.Show();
            return;
        }
        string? name = fromRecent && _recent.Find(uri.ToString()!) is { } r ? r.Name : DisplayName(uri);
        if (afterDeath && original == null)
        {
            ForgetKeptEdits();
            Toast.MakeText(this, "The file changed while the app was closed, so your unsaved changes could not be restored.", ToastLength.Long)?.Show();
            await OpenBytesAsync(bytes, uri, name, password: null, view, stamp);
            return;
        }
        if (afterDeath)
        {
            // With kept edits, the bytes they were made on (the file may have a saved update after them).
            await OpenBytesAsync(original!, uri, name, password: null, view, stamp, restore, onDisk: bytes);
            return;
        }
        if (kept != null && original != null)
        {
            // Opened anew (not from the saved state), with edits kept for these bytes: the reader chooses.
            OfferKeptEdits(kept,
                restore: () => _ = OpenBytesAsync(original, uri, name, password: null, view, stamp, kept, onDisk: bytes),
                discard: () => _ = OpenBytesAsync(bytes, uri, name, password: null, view, stamp));
            return;
        }
        await OpenBytesAsync(bytes, uri, name, password: null, view, stamp);
    }

    private sealed class FileTooLargeException(string message) : IOException(message);

    private byte[] ReadAll(Uri uri)
    {
        using var input = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("No stream for the document.");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > Options.MaxFileBytes)
                throw new FileTooLargeException($"This file is larger than {Options.MaxFileBytes / (1024 * 1024)} MB, the most this viewer opens.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>The file's name as the document provider shows it, for the title bar and the recent list only.</summary>
    private string? DisplayName(Uri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task OpenBytesAsync(byte[] bytes, Uri uri, string? name, string? password, (PageAnchor TopLeft, double Zoom)? view, ViewerFileStamp stamp,
        ViewerEditSnapshot? restore = null, byte[]? onDisk = null)
    {
        ViewerDocument opened;
        try
        {
            opened = await Task.Run(() => ViewerDocument.OpenAsync(bytes, password, Options));
        }
        catch (ViewerOpenException ex) when (ex.Failure is ViewerOpenFailure.PasswordRequired or ViewerOpenFailure.WrongPassword)
        {
            // An encrypted document's edits are never kept: the file as it is now is opened.
            AskPassword(onDisk ?? bytes, uri, name, wrong: ex.Failure == ViewerOpenFailure.WrongPassword, view, stamp);
            return;
        }
        catch (ViewerOpenException ex)
        {
            // The message is written for users and never quotes the document. Kept edits for it cannot be given back.
            if (restore != null) ForgetKeptEdits();
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            return;
        }

        // The reader's edits: the file's annotations and, when it allows form filling, its fields.
        ViewerEditSession? session = null;
        try
        {
            session = await Task.Run(async () =>
            {
                var started = await ViewerEditSession.StartAsync(opened);
                // Annotations made on this device earlier: the reader's own (MainActivity.KeptEdits.cs).
                foreach (string id in ReadMadeHere()) started.MadeHere.Add(id);
                return started;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Toast.MakeText(this, $"This document cannot be edited here ({ex.GetType().Name}).", ToastLength.Short)?.Show();
        }
        if (session?.Problem is { } problem) Toast.MakeText(this, problem, ToastLength.Short)?.Show();

        var previous = _doc;
        _search?.Cancel();
        _hits = new List<ViewerSearchHit>();
        _hitIndex = -1;
        _searchCount.Text = "";
        ShowSearch(false);
        _thumbnails.Release();
        _doc = opened;
        _fileName = name;
        _uri = uri;
        // To tell, at a save, whether another app changed the file since; and what to write back if a save fails part-way.
        _openedStamp = stamp;
        _onDisk = onDisk ?? bytes;
        // What kept edits are tied to (MainActivity.KeptEdits.cs): the same bytes as the snapshot's length.
        _openedHash = Task.Run(() => ViewerEditSnapshot.Hash(opened.OriginalBytes));
        _pages.CopyAllowed = opened.CanCopy;
        _pages.SetDocument(opened);
        _linkBack.Clear();
        // Kept edits on storage are this document's only once restored; another document's stay until replaced.
        _keptOurs = false;
        _keptVersion = -1;
        StartEdits(session);
        if (restore != null && session != null && !opened.IsEncrypted) RestoreKeptEdits(session, restore);
        if (view is { } v) _pages.RestoreView(v.TopLeft, v.Zoom);
        // Files the app may open again (a persisted grant) go to the top of the recent list.
        if (HasPersistedPermission(uri))
            _recent.Opened(uri.ToString()!, name ?? "Document", DateTimeOffset.Now);
        ReleaseUnlistedPermissions();
        ShowDocumentState();
        if (previous != null) await previous.DisposeAsync();
    }

    /// <summary>Gives back persisted grants for files that are no longer on the recent list (the system keeps a limited number).</summary>
    private void ReleaseUnlistedPermissions()
    {
        var permissions = ContentResolver?.PersistedUriPermissions;
        if (permissions == null) return;
        foreach (var p in permissions.ToList())
        {
            string? location = p.Uri?.ToString();
            if (location != null && _recent.Find(location) == null && p.Uri?.Equals(_uri) != true)
                ReleasePermission(location);
        }
    }

    private void AskPassword(byte[] bytes, Uri uri, string? name, bool wrong, (PageAnchor TopLeft, double Zoom)? view, ViewerFileStamp stamp)
    {
        var input = new EditText(this) { InputType = InputTypes.ClassText | InputTypes.TextVariationPassword, Hint = "Password" };
        new AlertDialog.Builder(this)
            .SetTitle(wrong ? "Wrong password" : "This PDF is protected")!
            .SetMessage(wrong ? "That password does not open this PDF. Try again." : "Enter the password to open it.")!
            .SetView(input)!
            .SetPositiveButton("Open", (_, _) => _ = OpenBytesAsync(bytes, uri, name, input.Text ?? "", view, stamp))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ navigation, search

    private void AskPage()
    {
        if (_doc == null) return;
        var input = new EditText(this) { InputType = InputTypes.ClassNumber, Hint = $"1 to {_doc.PageCount}" };
        new AlertDialog.Builder(this)
            .SetTitle("Go to page")!
            .SetView(input)!
            .SetPositiveButton("Go", (_, _) =>
            {
                if (int.TryParse(input.Text, out int page)) _pages.GoToPage(Math.Clamp(page, 1, _doc.PageCount));
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>The soft keyboard (input_method) service; a Java object, so JavaCast gives its managed wrapper.</summary>
    private InputMethodManager? Keyboard() => GetSystemService(InputMethodService)?.JavaCast<InputMethodManager>();

    private void ShowSearch(bool show)
    {
        if (show && _doc == null) return;
        _searchBar.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        var keyboard = Keyboard();
        if (show)
        {
            ShowThumbnails(false);
            _searchText.RequestFocus();
            _searchText.SelectAll(); // typing replaces the last query
            keyboard?.ShowSoftInput(_searchText, ShowFlags.Implicit);
        }
        else
        {
            _search?.Cancel();
            keyboard?.HideSoftInputFromWindow(_searchText.WindowToken, HideSoftInputFlags.None);
            _hits = new List<ViewerSearchHit>();
            _hitIndex = -1;
            _searchCount.Text = "";
            _pages.SetSearchHits(_hits, -1);
        }
    }

    private async Task SearchAsync(string query)
    {
        var doc = _doc;
        if (doc == null || string.IsNullOrWhiteSpace(query)) return;
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var keyboard = Keyboard();
        keyboard?.HideSoftInputFromWindow(_searchText.WindowToken, HideSoftInputFlags.None);
        _searchCount.Text = "…";
        List<ViewerSearchHit> hits;
        try
        {
            // Text extraction runs on a worker thread, page by page.
            hits = await Task.Run(async () =>
            {
                var found = new List<ViewerSearchHit>();
                await foreach (var hit in doc.SearchAsync(query, cancellationToken: cts.Token)) found.Add(hit);
                return found;
            }, cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(doc, _doc)) return;
        _hits = hits;
        // The first match at or after the page in view.
        _hitIndex = hits.Count == 0 ? -1 : Math.Max(0, hits.FindIndex(h => h.Page >= _pages.CurrentPage));
        ShowHit();
    }

    private void StepHit(int step)
    {
        if (_hits.Count == 0) return;
        _hitIndex = (_hitIndex + step + _hits.Count) % _hits.Count;
        ShowHit();
    }

    private void ShowHit()
    {
        _searchCount.Text = _hits.Count == 0 ? "No matches" : $"{_hitIndex + 1} / {_hits.Count}";
        _pages.SetSearchHits(_hits, _hitIndex);
    }

    /// <summary>A hardware keyboard (or a Chromebook): + and − zoom by steps, 0 goes back to fit width.</summary>
    public override bool OnKeyDown([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        if (_doc != null && _thumbnails.Visibility != ViewStates.Visible && !_searchText.HasFocus)
        {
            switch (keyCode)
            {
                case Keycode.Plus or Keycode.Equals or Keycode.NumpadAdd:
                    _pages.ZoomTo(_pages.Zoom * 1.5);
                    return true;
                case Keycode.Minus or Keycode.NumpadSubtract:
                    _pages.ZoomTo(_pages.Zoom / 1.5);
                    return true;
                case Keycode.Num0 or Keycode.Numpad0:
                    _pages.ZoomTo(1);
                    return true;
            }
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override void OnBackPressed()
    {
        if (_thumbnails.Visibility == ViewStates.Visible)
        {
            ShowThumbnails(false);
            return;
        }
        if (_pages.HasSelection)
        {
            _pages.ClearSelection();
            return;
        }
        if (_searchBar.Visibility == ViewStates.Visible)
        {
            ShowSearch(false);
            return;
        }
        if (_annotateBar.Visibility == ViewStates.Visible)
        {
            ShowAnnotateBar(false);
            return;
        }
        // Back to where a link was followed from, before leaving the document.
        if (GoBackByLink()) return;
        if (_session?.IsDirty == true)
        {
            AfterDiscard(Finish);
            return;
        }
#pragma warning disable CA1422 // the platform's own back handling, still the behaviour on every supported version
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    protected override void OnDestroy()
    {
        _search?.Cancel();
        if (IsFinishing) ForgetOurKeptEdits();
        var doc = _doc;
        _doc = null;
        _pages.SetDocument(null);
        StartEdits(null);
        _thumbnails.Release();
        if (doc != null) _ = doc.DisposeAsync().AsTask();
        base.OnDestroy();
    }
}
