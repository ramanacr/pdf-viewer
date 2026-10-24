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
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// The viewer: a toolbar, a search bar and the pages. A document comes from the system document
/// picker (Storage Access Framework, ACTION_OPEN_DOCUMENT) or from another app's "Open with"
/// (ACTION_VIEW); it is read into memory and never written anywhere. The activity is kept across
/// rotation and dark-mode switches so the open document stays open.
/// </summary>
[Activity(Label = "PDF Viewer (preview)", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault], DataScheme = "content", DataMimeType = "application/pdf")]
public sealed class MainActivity : Activity
{
    private const int OpenDocumentRequest = 1;

    /// <summary>A phone's memory is smaller than a desktop's: files up to 256 MB, bitmaps up to 3000 × 3000 pixels.</summary>
    private static readonly ViewerOptions Options = new()
    {
        MaxFileBytes = 256L * 1024 * 1024,
        ImageCacheBytes = 64L * 1024 * 1024,
        MaxBitmapPixels = 3000L * 3000,
    };

    private PageView _pages = null!;
    private TextView _title = null!, _pageIndicator = null!, _empty = null!, _searchCount = null!;
    private TextView _openButton = null!, _searchButton = null!, _nightButton = null!, _prevButton = null!, _nextButton = null!, _closeSearchButton = null!;
    private LinearLayout _toolbar = null!, _searchBar = null!;
    private EditText _searchText = null!;
    private ViewerDocument? _doc;
    private string? _fileName;
    private List<ViewerSearchHit> _hits = new();
    private int _hitIndex = -1;
    private CancellationTokenSource? _search;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(BuildLayout());
        ApplyTheme();
        ShowDocumentState();
        if (Intent is { Action: Intent.ActionView, Data: { } uri }) _ = OpenUriAsync(uri);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is { Action: Intent.ActionView, Data: { } uri }) _ = OpenUriAsync(uri);
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
    }

    // ------------------------------------------------------------------ layout

    private float Dp(float dp) => dp * (Resources?.DisplayMetrics?.Density ?? 1f);

    private View BuildLayout()
    {
        var root = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        root.SetFitsSystemWindows(true);

        _toolbar = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        _toolbar.SetGravity(GravityFlags.CenterVertical);
        _toolbar.SetPadding((int)Dp(4), (int)Dp(4), (int)Dp(4), (int)Dp(4));
        _openButton = ToolButton("Open", "Open a PDF", (_, _) => PickDocument());
        _title = new TextView(this) { Text = "PDF Viewer" };
        _title.SetSingleLine(true);
        _title.Ellipsize = TextUtils.TruncateAt.Middle;
        _title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        _title.SetPadding((int)Dp(8), 0, (int)Dp(8), 0);
        _pageIndicator = ToolButton("", "Go to page", (_, _) => AskPage());
        _searchButton = ToolButton("Search", "Search the document", (_, _) => ShowSearch(true));
        _nightButton = ToolButton("Night", "Night pages: show pages inverted", (_, _) => ToggleNight());
        _toolbar.AddView(_openButton);
        _toolbar.AddView(_title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _toolbar.AddView(_pageIndicator);
        _toolbar.AddView(_searchButton);
        _toolbar.AddView(_nightButton);
        root.AddView(_toolbar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _searchBar = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal, Visibility = ViewStates.Gone };
        _searchBar.SetGravity(GravityFlags.CenterVertical);
        _searchBar.SetPadding((int)Dp(8), 0, (int)Dp(4), (int)Dp(4));
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

        var frame = new FrameLayout(this);
        _pages = new PageView(this);
        _pages.PageChanged += (page, count) => _pageIndicator.Text = count > 0 ? $"{page} / {count}" : "";
        _pages.Problem += message => Toast.MakeText(this, message, ToastLength.Short)?.Show();
        frame.AddView(_pages, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _empty = new TextView(this)
        {
            Text = "Open a PDF to read it.\n\nThis app runs its own C# PDF engine on the device. It has no internet permission: documents never leave the phone.",
            Gravity = GravityFlags.Center,
        };
        _empty.SetPadding((int)Dp(32), 0, (int)Dp(32), 0);
        _empty.Click += (_, _) => PickDocument();
        frame.AddView(_empty, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        root.AddView(frame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return root;
    }

    private TextView ToolButton(string text, string description, EventHandler onClick)
    {
        var button = new TextView(this) { Text = text, ContentDescription = description, Clickable = true, Focusable = true, Gravity = GravityFlags.Center };
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.SetMinWidth((int)Dp(44));
        button.SetMinHeight((int)Dp(44));
        button.SetPadding((int)Dp(10), 0, (int)Dp(10), 0);
        button.Click += onClick;
        return button;
    }

    private bool IsDark => ((Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;

    /// <summary>Colours of the chrome for the system's light or dark theme (views made in code do not follow a theme switch by themselves).</summary>
    private void ApplyTheme()
    {
        bool dark = IsDark;
        var bar = dark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3");
        var text = dark ? Color.ParseColor("#E6E8EB") : Color.ParseColor("#1A1C1E");
        var accent = dark ? Color.ParseColor("#8AB4F8") : Color.ParseColor("#1F5FA8");
        _toolbar.SetBackgroundColor(bar);
        _searchBar.SetBackgroundColor(bar);
        _title.SetTextColor(text);
        _searchCount.SetTextColor(text);
        _searchText.SetTextColor(text);
        _searchText.SetHintTextColor(dark ? Color.ParseColor("#8C9199") : Color.ParseColor("#6C717A"));
        _empty.SetTextColor(text);
        foreach (var b in new[] { _openButton, _pageIndicator, _searchButton, _nightButton, _prevButton, _nextButton, _closeSearchButton })
            b.SetTextColor(accent);
        _nightButton.SetTypeface(null, _pages.NightPages ? TypefaceStyle.Bold : TypefaceStyle.Normal);
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
    }

    private void ShowDocumentState()
    {
        bool open = _doc != null;
        _empty.Visibility = open ? ViewStates.Gone : ViewStates.Visible;
        _pages.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        _searchButton.Enabled = _nightButton.Enabled = _pageIndicator.Enabled = open;
        _searchButton.Alpha = _nightButton.Alpha = open ? 1f : 0.4f;
        _title.Text = !open ? "PDF Viewer" : _doc!.Title ?? _fileName ?? "Document";
        if (!open) _pageIndicator.Text = "";
    }

    // ------------------------------------------------------------------ opening

    private void PickDocument()
    {
        // The system picker grants this app access to the one file chosen; no storage permission is needed.
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/pdf");
        StartActivityForResult(intent, OpenDocumentRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == OpenDocumentRequest && resultCode == Result.Ok && data?.Data is { } uri)
            _ = OpenUriAsync(uri);
    }

    private async Task OpenUriAsync(Uri uri)
    {
        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => ReadAll(uri));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Toast.MakeText(this, ex is FileTooLargeException ? ex.Message : $"The file could not be read ({ex.GetType().Name}).", ToastLength.Long)?.Show();
            return;
        }
        await OpenBytesAsync(bytes, DisplayName(uri), password: null);
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

    /// <summary>The file's name as the document provider shows it, for the title bar only.</summary>
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

    private async Task OpenBytesAsync(byte[] bytes, string? name, string? password)
    {
        ViewerDocument opened;
        try
        {
            opened = await Task.Run(() => ViewerDocument.OpenAsync(bytes, password, Options));
        }
        catch (ViewerOpenException ex) when (ex.Failure is ViewerOpenFailure.PasswordRequired or ViewerOpenFailure.WrongPassword)
        {
            AskPassword(bytes, name, wrong: ex.Failure == ViewerOpenFailure.WrongPassword);
            return;
        }
        catch (ViewerOpenException ex)
        {
            // The message is written for users and never quotes the document.
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            return;
        }

        var previous = _doc;
        _search?.Cancel();
        _hits = new List<ViewerSearchHit>();
        _hitIndex = -1;
        _searchCount.Text = "";
        _doc = opened;
        _fileName = name;
        _pages.SetDocument(opened);
        ShowDocumentState();
        if (previous != null) await previous.DisposeAsync();
    }

    private void AskPassword(byte[] bytes, string? name, bool wrong)
    {
        var input = new EditText(this) { InputType = InputTypes.ClassText | InputTypes.TextVariationPassword, Hint = "Password" };
        new AlertDialog.Builder(this)
            .SetTitle(wrong ? "Wrong password" : "This PDF is protected")!
            .SetMessage(wrong ? "That password does not open this PDF. Try again." : "Enter the password to open it.")!
            .SetView(input)!
            .SetPositiveButton("Open", (_, _) => _ = OpenBytesAsync(bytes, name, input.Text ?? ""))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ navigation, night pages, search

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

    private void ToggleNight()
    {
        _pages.NightPages = !_pages.NightPages;
        ApplyTheme();
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

    public override void OnBackPressed()
    {
        if (_searchBar.Visibility == ViewStates.Visible)
        {
            ShowSearch(false);
            return;
        }
#pragma warning disable CA1422 // the platform's own back handling, still the behaviour on every supported version
        base.OnBackPressed();
#pragma warning restore CA1422
    }

    protected override void OnDestroy()
    {
        _search?.Cancel();
        var doc = _doc;
        _doc = null;
        _pages.SetDocument(null);
        if (doc != null) _ = doc.DisposeAsync().AsTask();
        base.OnDestroy();
    }
}
