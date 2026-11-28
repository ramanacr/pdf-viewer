using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PdfEngine.Documents;
using PdfEngine.Vector.Document;
using PdfViewer.Web.Engine;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// Read mode: the document's text reflowed to the phone's width as headings, paragraphs, lists and
/// figure descriptions (from its tags when it is tagged; otherwise inferred from the text layout,
/// MainActivity.ReadingContent.cs), page by page, loaded as the reader scrolls. The text size is
/// kept in the app's preferences; the colours follow dark mode and night pages. Each page starts
/// with a "Page 3 of 20" marker that goes back to that page in the viewer. As on Windows, read mode
/// needs the document's permission to extract text for accessibility, and the text can be selected
/// only when the document allows copying. Every block is its own view, so TalkBack moves by
/// paragraph and by heading.
/// </summary>
public sealed partial class MainActivity
{
    private const string ReadingPreferences = "reading", ReadTextSizeKey = "read.textSize";
    private const float DefaultReadTextSize = 18f, MinReadTextSize = 12f, MaxReadTextSize = 40f;

    /// <summary>What read mode is showing: its own engine copy of the document and the pages loaded so far.</summary>
    private sealed class ReadModeState(ViewerDocument doc, PdfVectorDocument engine, int pageCount, bool selectable)
    {
        public ViewerDocument Doc { get; } = doc;
        public PdfVectorDocument Engine { get; } = engine;
        public int PageCount { get; } = pageCount;
        public bool Selectable { get; } = selectable;
        public CancellationTokenSource Cts { get; } = new();

        /// <summary>One page is read at a time from <see cref="Engine"/>; closing waits for it before disposing.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>The pages shown are <see cref="First"/> to <see cref="Last"/>.</summary>
        public int First, Last;
        public bool LoadingNext, LoadingPrevious, Closed;
        public View Root = null!;
        public ScrollView Scroll = null!;
        public LinearLayout Content = null!;
        public TextView Footer = null!, Earlier = null!;

        /// <summary>Every text view with its size relative to body text, for A− and A+.</summary>
        public List<(TextView View, float Scale)> Texts { get; } = new();
    }

    private float ReadTextSize
    {
        get
        {
            try
            {
                return Math.Clamp(GetSharedPreferences(ReadingPreferences, FileCreationMode.Private)?.GetFloat(ReadTextSizeKey, DefaultReadTextSize) ?? DefaultReadTextSize,
                    MinReadTextSize, MaxReadTextSize);
            }
            catch (Java.Lang.ClassCastException)
            {
                return DefaultReadTextSize;
            }
        }
        set => GetSharedPreferences(ReadingPreferences, FileCreationMode.Private)?.Edit()?.PutFloat(ReadTextSizeKey, value)?.Apply();
    }

    /// <summary>Whether the document lets assistive technology extract its text (ISO 32000-2 Table 22, bit 10, or copying).</summary>
    private static bool AllowsAccessibility(PdfVectorDocument engine) =>
        engine.Security is not { } s || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanExtractForAccessibility;

    private const string NoAccessibilityMessage = "This document's security settings do not allow its text to be extracted for reading.";

    private async void ShowReadMode()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (CurrentBytes() is not { } bytes) return;
        var engine = await RunBusyAsync("Preparing read mode…", token =>
        {
            var opened = OpenForTool(bytes);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!AllowsAccessibility(opened)) throw new UserMessageException(NoAccessibilityMessage);
                return opened;
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        });
        if (engine == null) return;
        if (!ReferenceEquals(doc, _doc))
        {
            engine.Dispose();
            return;
        }
        var state = new ReadModeState(doc, engine, Math.Min(engine.PageCount, doc.PageCount), doc.CanCopy);
        int start = Math.Clamp(_pages.CurrentPage, 1, Math.Max(1, state.PageCount));
        state.First = start;
        state.Last = start - 1;
        BuildReadMode(state);
        ShowToolOverlay(state.Root, () => CloseReadMode(state));
        _ = LoadNextReadPageAsync(state);
    }

    private (Color Background, Color Text, Color Muted) ReadColors =>
        IsDark || _pages.NightPages
            ? (Color.ParseColor("#1E1E1E"), Color.ParseColor("#E8E6E3"), Color.ParseColor("#9A9A9A"))
            : (Color.ParseColor("#FBF8F1"), Color.ParseColor("#222222"), Color.ParseColor("#6E6E6E"));

    private void BuildReadMode(ReadModeState state)
    {
        var (background, _, muted) = ReadColors;
        var root = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical, Clickable = true };
        root.SetBackgroundColor(background);
        var smaller = ToolButton("A−", "Smaller text", (_, _) => SetReadTextSize(state, ReadTextSize - 2));
        var larger = ToolButton("A+", "Larger text", (_, _) => SetReadTextSize(state, ReadTextSize + 2));
        root.AddView(ToolScreenBar("Read mode", out _, smaller, larger));

        var scroll = new ScrollView(this) { FillViewport = true };
        var content = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        // A comfortable line length on a tablet: at most about 680 dp of text.
        float width = Resources?.DisplayMetrics?.WidthPixels ?? 0;
        int side = (int)Math.Max(Dp(20), (width - Dp(680)) / 2);
        content.SetPadding(side, (int)Dp(12), side, (int)Dp(96));
        scroll.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var earlier = ToolButton($"Show earlier pages (1–{state.First - 1})", "Show the pages before this one", (_, _) => _ = LoadPreviousReadPagesAsync(state));
        earlier.SetTextColor(AccentColor);
        earlier.Visibility = state.First > 1 ? ViewStates.Visible : ViewStates.Gone;
        content.AddView(earlier, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var footer = new TextView(this) { Text = "Loading…", Gravity = GravityFlags.Center };
        footer.SetTextColor(muted);
        footer.SetPadding(0, (int)Dp(24), 0, (int)Dp(24));
        content.AddView(footer);

        // More pages as the reader scrolls, and whenever the text's height changes (a page added, another text size).
        scroll.ScrollChange += (_, _) => _ = LoadNextReadPageAsync(state);
        content.LayoutChange += (_, _) => _ = LoadNextReadPageAsync(state);
        state.Root = root;
        state.Scroll = scroll;
        state.Content = content;
        state.Footer = footer;
        state.Earlier = earlier;
    }

    /// <summary>Ends read mode: stops reading pages and disposes the engine copy once the page being read is done.</summary>
    private static void CloseReadMode(ReadModeState state)
    {
        state.Closed = true;
        state.Cts.Cancel();
        _ = Task.Run(async () =>
        {
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                state.Engine.Dispose();
            }
            finally
            {
                state.Gate.Release();
            }
        });
    }

    /// <summary>True while read mode is open and still about the open document (it closes when the document changed).</summary>
    private bool ReadModeCurrent(ReadModeState state)
    {
        if (state.Closed) return false;
        if (ReferenceEquals(state.Doc, _doc)) return true;
        if (ReferenceEquals(_toolOverlay, state.Root)) CloseToolOverlay();
        return false;
    }

    /// <summary>One page's blocks, read on a worker thread; a page that cannot be read says so.</summary>
    private static async Task<List<ReadBlock>?> ReadPageBlocksAsync(ReadModeState state, int page)
    {
        var token = state.Cts.Token;
        try
        {
            return await Task.Run(async () =>
            {
                await state.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    return ReadContent.Page(state.Engine, page, token);
                }
                finally
                {
                    state.Gate.Release();
                }
            }, token);
        }
        catch (Exception ex) when (ex is System.OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [new ReadBlock(ReadRole.Unreadable, $"The text of this page could not be read ({ex.GetType().Name}).")];
        }
    }

    /// <summary>
    /// Loads the next page when the end of what is shown is within two screens. The new page changes
    /// the text's height once laid out, which calls this again, until two screens are ahead.
    /// </summary>
    private async Task LoadNextReadPageAsync(ReadModeState state)
    {
        if (state.LoadingNext || !ReadModeCurrent(state)) return;
        if (state.Last >= state.PageCount)
        {
            state.Footer.Text = "End of the document";
            return;
        }
        var scroll = state.Scroll;
        int viewport = scroll.Height;
        if (state.Last >= state.First)
        {
            // Not laid out yet: the layout change asks again.
            if (viewport == 0 || state.Content.Height == 0) return;
            if (state.Content.Height - (scroll.ScrollY + viewport) > viewport * 2) return;
        }
        state.LoadingNext = true;
        int page = state.Last + 1;
        var blocks = await ReadPageBlocksAsync(state, page);
        state.LoadingNext = false;
        if (blocks == null || !ReadModeCurrent(state)) return;
        state.Content.AddView(BuildReadPage(state, page, blocks), state.Content.IndexOfChild(state.Footer));
        state.Last = page;
        if (page >= state.PageCount) state.Footer.Text = "End of the document";
    }

    /// <summary>"Show earlier pages": up to three pages before the first shown, keeping the text in view where it is.</summary>
    private async Task LoadPreviousReadPagesAsync(ReadModeState state)
    {
        if (state.LoadingPrevious || !ReadModeCurrent(state)) return;
        state.LoadingPrevious = true;
        state.Earlier.Text = "Loading…";
        for (int n = 0; n < 3 && state.First > 1; n++)
        {
            int page = state.First - 1;
            var blocks = await ReadPageBlocksAsync(state, page);
            if (blocks == null || !ReadModeCurrent(state)) return;
            var section = BuildReadPage(state, page, blocks);
            // Keep what the reader was looking at in place: once laid out, scroll down by the height added above it.
            EventHandler<View.LayoutChangeEventArgs>? placed = null;
            placed = (_, _) =>
            {
                section.LayoutChange -= placed;
                section.Post(() => state.Scroll.ScrollBy(0, section.Height));
            };
            section.LayoutChange += placed;
            state.Content.AddView(section, 1);
            state.First = page;
        }
        state.LoadingPrevious = false;
        state.Earlier.Text = $"Show earlier pages (1–{state.First - 1})";
        state.Earlier.Visibility = state.First > 1 ? ViewStates.Visible : ViewStates.Gone;
    }

    private void SetReadTextSize(ReadModeState state, float size)
    {
        size = Math.Clamp(size, MinReadTextSize, MaxReadTextSize);
        ReadTextSize = size;
        foreach (var (view, scale) in state.Texts)
            view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, size * scale);
        Toast.MakeText(this, $"Text size {size:0}", ToastLength.Short)?.Show();
    }

    /// <summary>A page of read mode: its marker (tap to show the page in the viewer), then its blocks.</summary>
    private LinearLayout BuildReadPage(ReadModeState state, int page, List<ReadBlock> blocks)
    {
        var (_, text, muted) = ReadColors;
        float size = ReadTextSize;
        var section = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };

        var marker = new TextView(this)
        {
            Text = $"Page {page} of {state.PageCount}",
            Clickable = true,
            Focusable = true,
            ContentDescription = $"Page {page} of {state.PageCount}. Show this page in the viewer",
        };
        marker.SetTextColor(AccentColor);
        marker.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        marker.SetMinHeight((int)Dp(48));
        marker.Gravity = GravityFlags.CenterVertical;
        marker.SetPadding(0, (int)Dp(page == state.First ? 0 : 16), 0, (int)Dp(4));
        marker.Click += (_, _) =>
        {
            bool current = ReferenceEquals(state.Doc, _doc);
            CloseToolOverlay();
            if (current) _pages.GoToPage(page);
        };
        section.AddView(marker);
        var rule = new View(this);
        rule.SetBackgroundColor(muted);
        rule.Alpha = 0.35f;
        section.AddView(rule, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Max(1, (int)Dp(1))) { BottomMargin = (int)Dp(12) });

        if (blocks.Count == 0)
            blocks = [new ReadBlock(ReadRole.Unreadable, "No text on this page. It may be a scanned picture of text.")];
        foreach (var block in blocks)
        {
            var view = new TextView(this);
            view.SetTextColor(text);
            view.SetLineSpacing(0, 1.35f);
            if (state.Selectable) view.SetTextIsSelectable(true);
            float scale = 1f;
            int top = 0, bottom = (int)Dp(12), left = 0;
            switch (block.Role)
            {
                case ReadRole.Heading:
                    scale = block.Level switch { 1 => 1.6f, 2 => 1.4f, 3 => 1.25f, 4 => 1.15f, _ => 1.05f };
                    view.Text = block.Text;
                    view.SetTypeface(null, TypefaceStyle.Bold);
                    if (OperatingSystem.IsAndroidVersionAtLeast(28)) view.AccessibilityHeading = true;
                    top = (int)Dp(8);
                    bottom = (int)Dp(8);
                    break;
                case ReadRole.ListItem:
                    view.Text = ReadContent.StartsWithListMarker(block.Text) ? block.Text : "• " + block.Text;
                    left = (int)Dp(16);
                    bottom = (int)Dp(6);
                    break;
                case ReadRole.Figure:
                    view.Text = string.IsNullOrWhiteSpace(block.Text) ? "[Figure]" : $"[Figure: {block.Text}]";
                    view.SetTypeface(null, TypefaceStyle.Italic);
                    view.SetTextColor(muted);
                    break;
                case ReadRole.Caption:
                    scale = 0.9f;
                    view.Text = block.Text;
                    view.SetTypeface(null, TypefaceStyle.Italic);
                    break;
                case ReadRole.Quote:
                    view.Text = block.Text;
                    view.SetTypeface(null, TypefaceStyle.Italic);
                    left = (int)Dp(24);
                    break;
                case ReadRole.Code:
                    scale = 0.9f;
                    view.Text = block.Text;
                    view.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
                    break;
                case ReadRole.TableHeader:
                    view.Text = block.Text;
                    view.SetTypeface(null, TypefaceStyle.Bold);
                    bottom = (int)Dp(4);
                    break;
                case ReadRole.TableCell:
                    view.Text = block.Text;
                    bottom = (int)Dp(4);
                    break;
                case ReadRole.Note:
                    scale = 0.85f;
                    view.Text = block.Text;
                    break;
                case ReadRole.Unreadable:
                    view.Text = block.Text;
                    view.SetTypeface(null, TypefaceStyle.Italic);
                    view.SetTextColor(muted);
                    view.SetTextIsSelectable(false);
                    break;
                default:
                    view.Text = block.Text;
                    break;
            }
            view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, size * scale);
            state.Texts.Add((view, scale));
            section.AddView(view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = top,
                BottomMargin = bottom,
                LeftMargin = left,
            });
        }
        return section;
    }
}
