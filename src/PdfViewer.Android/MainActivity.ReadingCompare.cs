using System.Text;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Documents;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Text;
using PdfViewer.Web.Engine;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Compare with another PDF: the text of each page of the open document (with its edits) against
/// the same page of a PDF the reader picks, line by line (a longest-common-subsequence diff of the
/// page's lines, white space ignored). The report gives a summary, then for each page that differs
/// the lines added, deleted and changed, with counts. The lines are shown only when both documents
/// allow copying their text; otherwise only the counts are. From the report, Visual differences shows
/// each page pair drawn at the same scale, as this document, the other, or a heatmap of the pixels
/// that differ, with the share of the page that differs.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The largest page pair the line diff compares in full (lines × lines); larger pairs count the middle as changed.</summary>
    private const long MaxDiffCells = 4_000_000;

    /// <summary>At most this many lines are listed for a page (all are counted).</summary>
    private const int MaxListedLinesPerPage = 30;

    private enum LineEdit { Same, Deleted, Added }

    private sealed record PageComparison(int Page, int Added, int Deleted, int Changed, List<string> Lines, int? OnlyIn, int OnlyLines);

    /// <param name="OtherBytes">The other PDF, kept for the visual comparison (null when it needs a password).</param>
    private sealed record ComparisonResult(bool NeedsPassword, string Report, byte[]? OtherBytes = null);

    private void CompareDocuments()
    {
        if (!NeedDocument()) return;
        PickFiles(["application/pdf"], false, uris => _ = CompareWithAsync(uris[0], null, wrong: false));
    }

    private async Task CompareWithAsync(Uri other, string? password, bool wrong)
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (CurrentBytes() is not { } bytes) return;
        string otherName = DisplayName(other) ?? "the other PDF";
        var result = await RunBusyAsync("Comparing the documents…", token =>
        {
            byte[] otherBytes = ReadUri(other, Options.MaxFileBytes);
            token.ThrowIfCancellationRequested();
            using var mine = OpenForTool(bytes);
            PdfVectorDocument theirs;
            try
            {
                theirs = PdfVectorDocument.Open(new PdfMemoryByteSource(otherBytes), otherName, password: password, certificates: []);
            }
            catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
            {
                return new ComparisonResult(true, "");
            }
            using (theirs)
                return new ComparisonResult(false, CompareText(mine, theirs, otherName, token), otherBytes);
        });
        if (result == null || !ReferenceEquals(doc, _doc)) return;
        if (result.NeedsPassword)
        {
            AskLine(wrong ? "Wrong password" : "This PDF is protected",
                wrong ? "That password does not open it. Try again." : "Enter the password of the PDF to compare with.",
                "", "Password", InputTypes.ClassText | InputTypes.TextVariationPassword, "Compare",
                typed => _ = CompareWithAsync(other, typed, wrong: true));
            return;
        }
        ShowComparisonReport(result.Report, () =>
        {
            if (ReferenceEquals(doc, _doc) && result.OtherBytes is { } otherBytes) ShowVisualDifferences(doc, otherBytes, password, otherName);
        });
    }

    /// <summary>
    /// The text report, as <see cref="ShowReport"/> shows a report (Copy, Save as text), with a button at its top
    /// that opens the visual comparison.
    /// </summary>
    private void ShowComparisonReport(string text, Action visual)
    {
        const string title = "Comparison";
        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(24), (int)Dp(12), (int)Dp(24), (int)Dp(12));
        AlertDialog? dialog = null;
        var button = new Button(this) { Text = "See visual differences", ContentDescription = "See the pages side by side, with the pixels that differ in red" };
        button.SetAllCaps(false);
        button.SetMinHeight((int)Dp(48));
        button.Click += (_, _) =>
        {
            dialog?.Dismiss();
            visual();
        };
        column.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = (int)Dp(8) });
        var body = new TextView(this) { Text = text };
        body.SetTextIsSelectable(true);
        body.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        column.AddView(body);
        var scroll = new ScrollView(this);
        scroll.AddView(column);
        dialog = new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetView(scroll)!
            .SetPositiveButton("Close", (_, _) => { })!
            .SetNeutralButton("Copy", (_, _) =>
            {
                var clipboard = GetSystemService(ClipboardService) as global::Android.Content.ClipboardManager;
                if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText(title, text);
            })!
            .SetNegativeButton("Save as text", (_, _) =>
                SaveNewFile("text/plain", BaseName + "_comparison.txt", "Saving…", _ => System.Text.Encoding.UTF8.GetBytes(text),
                    _ => Toast.MakeText(this, "Report saved.", ToastLength.Short)?.Show()))!
            .Create()!;
        dialog.Show();
    }

    private static bool AllowsCopy(PdfVectorDocument engine) =>
        engine.Security is not { } s || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanCopy;

    /// <summary>The page's text as lines, white space collapsed and empty lines dropped.</summary>
    private static List<string> PageLines(PdfVectorDocument doc, int page, CancellationToken token)
    {
        if (page > doc.PageCount) return [];
        string text = PdfTextExtractor.LoadPage(doc, page, token).GetAllText();
        return text.Split('\n', '\r')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string CompareText(PdfVectorDocument mine, PdfVectorDocument theirs, string otherName, CancellationToken token)
    {
        bool showLines = AllowsCopy(mine) && AllowsCopy(theirs);
        int pages = Math.Max(mine.PageCount, theirs.PageCount);
        var differing = new List<PageComparison>();
        for (int page = 1; page <= pages; page++)
        {
            token.ThrowIfCancellationRequested();
            var a = PageLines(mine, page, token);
            var b = PageLines(theirs, page, token);
            if (page > theirs.PageCount)
            {
                differing.Add(new PageComparison(page, 0, a.Count, 0, [], 1, a.Count));
                continue;
            }
            if (page > mine.PageCount)
            {
                differing.Add(new PageComparison(page, b.Count, 0, 0, [], 2, b.Count));
                continue;
            }
            if (ComparePage(page, a, b, showLines) is { } changed) differing.Add(changed);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"This document: {mine.PageCount} page(s).");
        sb.AppendLine($"Compared with: {otherName}, {theirs.PageCount} page(s).");
        sb.AppendLine("Each page is compared with the page of the same number.");
        sb.AppendLine();
        if (differing.Count == 0)
        {
            sb.AppendLine("The text of the documents is the same.");
            return sb.ToString().TrimEnd();
        }
        int added = differing.Sum(d => d.Added), deleted = differing.Sum(d => d.Deleted), changedLines = differing.Sum(d => d.Changed);
        sb.AppendLine($"Summary: {differing.Count} of {pages} page(s) differ. Lines added: {added}, deleted: {deleted}, changed: {changedLines}.");
        if (!showLines) sb.AppendLine("A document does not allow copying its text, so the lines are counted but not shown.");
        foreach (var d in differing)
        {
            sb.AppendLine();
            if (d.OnlyIn is { } only)
            {
                sb.AppendLine($"Page {d.Page}: only in {(only == 1 ? "this document" : "the other document")} ({d.OnlyLines} line(s))");
                continue;
            }
            var counts = new List<string>(3);
            if (d.Added > 0) counts.Add($"{d.Added} added");
            if (d.Deleted > 0) counts.Add($"{d.Deleted} deleted");
            if (d.Changed > 0) counts.Add($"{d.Changed} changed");
            sb.AppendLine($"Page {d.Page}: {string.Join(", ", counts)}");
            foreach (string line in d.Lines) sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One page pair: null when the lines are the same; else the counts and, with <paramref name="showLines"/>, the lines ("+", "−", "~ old / → new").</summary>
    private static PageComparison? ComparePage(int page, List<string> a, List<string> b, bool showLines)
    {
        var edits = DiffLines(a, b);
        if (edits.All(e => e.Edit == LineEdit.Same)) return null;
        int added = 0, deleted = 0, changed = 0, listed = 0, unlisted = 0;
        var lines = new List<string>();
        void List(string line)
        {
            if (!showLines) return;
            if (listed < MaxListedLinesPerPage)
            {
                lines.Add(line);
                listed++;
            }
            else
            {
                unlisted++;
            }
        }
        int i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Edit == LineEdit.Same)
            {
                i++;
                continue;
            }
            // A run of edits: deleted lines paired with added lines are changes; the rest are added or deleted.
            var gone = new List<string>();
            var come = new List<string>();
            for (; i < edits.Count && edits[i].Edit != LineEdit.Same; i++)
                (edits[i].Edit == LineEdit.Deleted ? gone : come).Add(edits[i].Text);
            int pairs = Math.Min(gone.Count, come.Count);
            for (int k = 0; k < pairs; k++)
            {
                changed++;
                List($"  ~ {gone[k]}");
                List($"    → {come[k]}");
            }
            for (int k = pairs; k < gone.Count; k++)
            {
                deleted++;
                List($"  − {gone[k]}");
            }
            for (int k = pairs; k < come.Count; k++)
            {
                added++;
                List($"  + {come[k]}");
            }
        }
        if (unlisted > 0) lines.Add($"  … and {unlisted} more line(s)");
        return new PageComparison(page, added, deleted, changed, lines, null, 0);
    }

    /// <summary>A line diff: the common start and end are kept, the middle compared by longest common subsequence.</summary>
    private static List<(LineEdit Edit, string Text)> DiffLines(List<string> a, List<string> b)
    {
        int start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
        int endA = a.Count, endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }
        var result = new List<(LineEdit, string)>(Math.Max(a.Count, b.Count));
        for (int k = 0; k < start; k++) result.Add((LineEdit.Same, a[k]));
        int n = endA - start, m = endB - start;
        if ((long)n * m > MaxDiffCells)
        {
            for (int k = start; k < endA; k++) result.Add((LineEdit.Deleted, a[k]));
            for (int k = start; k < endB; k++) result.Add((LineEdit.Added, b[k]));
        }
        else if (n > 0 || m > 0)
        {
            // lcs[i, j]: the longest common subsequence of a[start + i ..] and b[start + j ..].
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y])
                {
                    result.Add((LineEdit.Same, a[start + x]));
                    x++;
                    y++;
                }
                else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
                {
                    result.Add((LineEdit.Added, b[start + y]));
                    y++;
                }
                else
                {
                    result.Add((LineEdit.Deleted, a[start + x]));
                    x++;
                }
            }
        }
        for (int k = endA; k < a.Count; k++) result.Add((LineEdit.Same, a[k]));
        return result;
    }

    // ------------------------------------------------------------------ visual differences

    /// <summary>The page pair is drawn to fit this box (pixels), both pages at the same scale.</summary>
    private const int VisualMaxWidth = 1000, VisualMaxHeight = 1400;

    /// <summary>For "All pages", a smaller box: quicker, and still enough to find the pages that differ.</summary>
    private const int VisualSurveyWidth = 500, VisualSurveyHeight = 700;

    /// <summary>
    /// Visual differences: the open document and the other PDF (opened again with <see cref="ViewerDocument.OpenAsync"/>,
    /// with its password) page by page, both drawn at the same scale, shown as this document, the other one, or the
    /// differences in red over both pages in light grey, with the share of the pixels that differ.
    /// </summary>
    private void ShowVisualDifferences(ViewerDocument doc, byte[] otherBytes, string? password, string otherName)
    {
        var screen = new VisualCompareScreen(this, doc, otherBytes, password, otherName, Math.Clamp(_pages.CurrentPage, 1, doc.PageCount));
        ShowToolOverlay(screen.Build(), screen.Close);
        screen.Start();
    }

    /// <summary>
    /// A page pair drawn at the same scale on canvases of the same size (white where a page is smaller or missing),
    /// as opaque ARGB colours, and the largest colour difference of each pixel (0 to 255).
    /// </summary>
    private sealed record VisualPair(int Page, int Width, int Height, int[] Mine, int[] Theirs, byte[] Difference, bool OnlyMine, bool OnlyTheirs)
    {
        /// <summary>The share of pixels whose colour differs by more than <paramref name="threshold"/> (0 to 255).</summary>
        public double DifferingFraction(int threshold)
        {
            long n = 0;
            foreach (byte d in Difference) if (d > threshold) n++;
            return Difference.Length == 0 ? 0 : n / (double)Difference.Length;
        }
    }

    /// <summary>Draws page <paramref name="page"/> of both documents to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/> and compares the pixels.</summary>
    private static async Task<VisualPair> RenderVisualPairAsync(ViewerDocument mine, ViewerDocument theirs, int page, int maxWidth, int maxHeight, CancellationToken token)
    {
        ViewerPage? a = page <= mine.PageCount ? mine.Pages[page - 1] : null;
        ViewerPage? b = page <= theirs.PageCount ? theirs.Pages[page - 1] : null;
        double w = Math.Max(a?.Width ?? 0, b?.Width ?? 0), h = Math.Max(a?.Height ?? 0, b?.Height ?? 0);
        double scale = Math.Min(maxWidth / Math.Max(1, w), maxHeight / Math.Max(1, h));
        var bitmapA = a != null ? await mine.RenderPageAsync(page, scale, night: false, token).ConfigureAwait(false) : null;
        token.ThrowIfCancellationRequested();
        var bitmapB = b != null ? await theirs.RenderPageAsync(page, scale, night: false, token).ConfigureAwait(false) : null;
        token.ThrowIfCancellationRequested();
        int width = Math.Max(1, Math.Max(bitmapA?.Width ?? 0, bitmapB?.Width ?? 0));
        int height = Math.Max(1, Math.Max(bitmapA?.Height ?? 0, bitmapB?.Height ?? 0));
        var colorsA = PageCanvas(bitmapA, width, height);
        var colorsB = PageCanvas(bitmapB, width, height);
        var difference = new byte[width * height];
        for (int i = 0; i < difference.Length; i++)
        {
            int p = colorsA[i], q = colorsB[i];
            int dr = Math.Abs((p >> 16 & 255) - (q >> 16 & 255));
            int dg = Math.Abs((p >> 8 & 255) - (q >> 8 & 255));
            int db = Math.Abs((p & 255) - (q & 255));
            difference[i] = (byte)Math.Max(dr, Math.Max(dg, db));
        }
        return new VisualPair(page, width, height, colorsA, colorsB, difference, b == null, a == null);
    }

    /// <summary>A rendered page (BGRA, opaque: on a little-endian device each pixel reads as an ARGB colour) at the top left of a white canvas.</summary>
    private static int[] PageCanvas(ViewerBitmap? page, int width, int height)
    {
        var colors = new int[width * height];
        Array.Fill(colors, unchecked((int)0xFFFFFFFF));
        if (page == null) return colors;
        var pixels = page.Pixels;
        int rowBytes = Math.Min(page.Width, width) * 4;
        for (int y = 0; y < Math.Min(page.Height, height); y++)
            Buffer.BlockCopy(pixels.Array!, pixels.Offset + y * page.Width * 4, colors, y * width * 4, rowBytes);
        for (int i = 0; i < colors.Length; i++) colors[i] |= unchecked((int)0xFF000000);
        return colors;
    }

    /// <summary>The heatmap: both pages in light grey, and the pixels that differ by more than <paramref name="threshold"/> in red (deeper the more they differ).</summary>
    private static int[] DifferenceColors(VisualPair pair, int threshold)
    {
        var colors = new int[pair.Difference.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            int lum = Math.Min(Luminance(pair.Mine[i]), Luminance(pair.Theirs[i]));
            int grey = 255 - (255 - lum) * 3 / 10;
            int d = pair.Difference[i];
            if (d <= threshold)
            {
                colors[i] = unchecked((int)0xFF000000) | grey << 16 | grey << 8 | grey;
                continue;
            }
            double k = 0.45 + 0.55 * d / 255.0;
            int r = (int)(grey + (220 - grey) * k), g = (int)(grey * (1 - k)), b = (int)(grey * (1 - k));
            colors[i] = unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
        }
        return colors;
    }

    private static int Luminance(int argb) => ((argb >> 16 & 255) * 299 + (argb >> 8 & 255) * 587 + (argb & 255) * 114) / 1000;

    /// <summary>The Visual differences screen: its views, the page pair shown, and the work on a worker thread.</summary>
    private sealed class VisualCompareScreen(MainActivity a, ViewerDocument mine, byte[] otherBytes, string? password, string otherName, int firstPage)
    {
        private enum Show { Mine, Theirs, Differences }

        private ViewerDocument? _theirs;
        private VisualPair? _pair;
        private int _page = firstPage, _pageCount = mine.PageCount;
        private Show _show = Show.Differences;
        /// <summary>Colour differences up to this percentage are ignored (anti-aliasing, recompressed images).</summary>
        private int _thresholdPercent = 10;
        private CancellationTokenSource? _cts;
        /// <summary>The last piece of work: they run one after another.</summary>
        private Task _work = Task.CompletedTask;
        private int _imageVersion;
        private bool _closed;

        private TextView _title = null!, _summary = null!, _thresholdText = null!, _prev = null!, _next = null!;
        private readonly Dictionary<Show, TextView> _tabs = new();
        private ImageView _image = null!;
        private ProgressBar _progress = null!;

        private int Threshold => _thresholdPercent * 255 / 100;

        public View Build()
        {
            var root = new LinearLayout(a) { Orientation = global::Android.Widget.Orientation.Vertical, Clickable = true };
            root.SetBackgroundColor(a.ScreenColor);
            _prev = a.ToolButton("‹", "Previous page", (_, _) => Go(_page - 1));
            _next = a.ToolButton("›", "Next page", (_, _) => Go(_page + 1));
            var all = a.ToolButton("All pages", "The share of pixels that differ on every page", (_, _) => SurveyAllPages());
            root.AddView(a.ToolScreenBar("Visual differences", out _title, _prev, _next, all));
            _title.Clickable = true;
            _title.Click += (_, _) => AskPage();

            var tabs = a.Bar();
            tabs.SetGravity(GravityFlags.Center);
            foreach (var (show, text, description) in new[]
                     {
                         (Show.Mine, "This PDF", "Show the page of this document"),
                         (Show.Theirs, "Other PDF", "Show the page of the other document"),
                         (Show.Differences, "Differences", "Show the pixels that differ in red"),
                     })
            {
                var tab = a.ToolButton(text, description, (_, _) =>
                {
                    _show = show;
                    ShowImage();
                });
                _tabs[show] = tab;
                tabs.AddView(tab);
            }
            root.AddView(tabs);

            var frame = new FrameLayout(a);
            _image = new ImageView(a) { ContentDescription = "The page" };
            _image.SetScaleType(ImageView.ScaleType.FitCenter!);
            _image.SetPadding((int)a.Dp(8), (int)a.Dp(8), (int)a.Dp(8), (int)a.Dp(8));
            frame.AddView(_image, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            _progress = new ProgressBar(a) { Indeterminate = true };
            frame.AddView(_progress, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
            root.AddView(frame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            _summary = new TextView(a);
            _summary.SetTextColor(a.TextColor);
            _summary.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
            _summary.SetPadding((int)a.Dp(16), (int)a.Dp(4), (int)a.Dp(16), (int)a.Dp(4));
            _summary.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
            root.AddView(_summary);

            var thresholdRow = a.Bar();
            thresholdRow.SetPadding((int)a.Dp(12), 0, (int)a.Dp(12), (int)a.Dp(8));
            _thresholdText = new TextView(a);
            _thresholdText.SetTextColor(a.TextColor);
            thresholdRow.AddView(_thresholdText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            var lower = a.ToolButton("−", "Count smaller colour differences", (_, _) => SetThreshold(_thresholdPercent - 5));
            var higher = a.ToolButton("+", "Ignore larger colour differences", (_, _) => SetThreshold(_thresholdPercent + 5));
            foreach (var b in new[] { lower, higher })
            {
                b.SetTextColor(a.AccentColor);
                thresholdRow.AddView(b);
            }
            root.AddView(thresholdRow);
            ShowThreshold();
            UpdateNavigation();
            ShowTabs();
            return root;
        }

        /// <summary>Opens the other PDF on a worker thread, then draws the first page pair.</summary>
        public void Start()
        {
            _summary.Text = "Opening the other PDF…";
            Run(async token =>
            {
                var theirs = await Task.Run(() => ViewerDocument.OpenAsync(otherBytes, password, Options, token), token);
                if (_closed)
                {
                    await theirs.DisposeAsync();
                    return;
                }
                _theirs = theirs;
                _pageCount = Math.Max(mine.PageCount, theirs.PageCount);
                _page = Math.Clamp(_page, 1, _pageCount);
                UpdateNavigation();
                await LoadPageAsync(token);
            });
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the UI thread after the work before it (which is cancelled), with the
        /// spinner; a failure is told, a cancel is quiet.
        /// </summary>
        private void Run(Func<CancellationToken, Task> work)
        {
            if (_closed) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var previous = _work;
            _progress.Visibility = ViewStates.Visible;
            _work = RunAsync();

            async Task RunAsync()
            {
                await previous; // never throws: each piece handles its own failure
                try
                {
                    if (cts.IsCancellationRequested || _closed) return;
                    if (!ReferenceEquals(mine, a._doc))
                    {
                        a.CloseToolOverlay();
                        return;
                    }
                    await work(cts.Token);
                }
                catch (Exception ex) when (ex is System.OperationCanceledException or ObjectDisposedException && (cts.IsCancellationRequested || _closed))
                {
                }
                catch (Exception ex)
                {
                    if (_closed || cts.IsCancellationRequested) return;
                    _summary.Text = "This page could not be compared.";
                    a.ShowFailure(ex is ViewerOpenException open ? new UserMessageException(open.Message) : ex);
                }
                finally
                {
                    if (ReferenceEquals(_cts, cts) && !_closed) _progress.Visibility = ViewStates.Gone;
                }
            }
        }

        private async Task LoadPageAsync(CancellationToken token)
        {
            if (_theirs is not { } theirs) return;
            int page = _page;
            _summary.Text = $"Comparing page {page}…";
            var pair = await Task.Run(() => RenderVisualPairAsync(mine, theirs, page, VisualMaxWidth, VisualMaxHeight, token), token);
            if (_closed || token.IsCancellationRequested) return;
            _pair = pair;
            ShowSummary();
            await ShowImageAsync();
        }

        private void ShowSummary()
        {
            if (_pair is not { } pair) return;
            double share = pair.DifferingFraction(Threshold) * 100;
            _summary.Text = pair.OnlyMine ? $"Page {pair.Page} is only in this document."
                : pair.OnlyTheirs ? $"Page {pair.Page} is only in {otherName}."
                : share == 0 ? $"Page {pair.Page}: no pixels differ."
                : $"Page {pair.Page}: {share:0.##} % of the pixels differ.";
        }

        private void ShowImage() => _ = ShowImageAsync();

        /// <summary>The page pair as the tab chosen: made into a bitmap on a worker thread.</summary>
        private async Task ShowImageAsync()
        {
            ShowTabs();
            if (_pair is not { } pair || _closed) return;
            int version = ++_imageVersion;
            var show = _show;
            int threshold = Threshold;
            Bitmap? bitmap;
            try
            {
                bitmap = await Task.Run(() =>
                {
                    var colors = show switch
                    {
                        Show.Mine => pair.Mine,
                        Show.Theirs => pair.Theirs,
                        _ => DifferenceColors(pair, threshold),
                    };
                    return Bitmap.CreateBitmap(colors, pair.Width, pair.Height, Bitmap.Config.Argb8888!);
                });
            }
            catch (OutOfMemoryException ex)
            {
                if (!_closed) a.ShowFailure(ex);
                return;
            }
            if (version != _imageVersion || _closed || !ReferenceEquals(pair, _pair))
            {
                bitmap?.Recycle();
                return;
            }
            _image.SetImageBitmap(bitmap);
            _image.ContentDescription = show switch
            {
                Show.Mine => $"Page {pair.Page} of this document",
                Show.Theirs => $"Page {pair.Page} of the other document",
                _ => $"Page {pair.Page}: the pixels that differ, in red",
            };
        }

        private void ShowTabs()
        {
            foreach (var (show, tab) in _tabs)
            {
                bool on = show == _show;
                tab.SetTextColor(on ? a.ScreenColor : a.AccentColor);
                tab.SetBackgroundColor(on ? a.AccentColor : Color.Transparent);
                tab.Selected = on;
            }
        }

        private void SetThreshold(int percent)
        {
            _thresholdPercent = Math.Clamp(percent, 0, 90);
            ShowThreshold();
            ShowSummary();
            if (_show == Show.Differences) ShowImage();
        }

        private void ShowThreshold() =>
            _thresholdText.Text = _thresholdPercent == 0 ? "Every colour difference counts" : $"Colour differences under {_thresholdPercent} % are ignored";

        // ------------------------------------------------------------------ pages

        private void Go(int page)
        {
            page = Math.Clamp(page, 1, _pageCount);
            if (page == _page || _theirs == null) return;
            _page = page;
            UpdateNavigation();
            Run(LoadPageAsync);
        }

        private void AskPage()
        {
            if (_theirs == null) return;
            a.AskLine("Go to page", $"1 to {_pageCount}", $"{_page}", "Page", InputTypes.ClassNumber, "Go", typed =>
            {
                if (int.TryParse(typed.Trim(), out int page) && page >= 1 && page <= _pageCount) Go(page);
                else Toast.MakeText(a, $"Type a page from 1 to {_pageCount}.", ToastLength.Short)?.Show();
            });
        }

        private void UpdateNavigation()
        {
            _title.Text = $"Page {_page} of {_pageCount}";
            _title.ContentDescription = $"Visual differences, page {_page} of {_pageCount}. Tap to go to a page.";
            _prev.Enabled = _page > 1;
            _prev.Alpha = _page > 1 ? 1f : 0.35f;
            _next.Enabled = _page < _pageCount;
            _next.Alpha = _page < _pageCount ? 1f : 0.35f;
        }

        /// <summary>Every page pair compared (smaller, behind a cancellable working dialog): the share of pixels that differ on each.</summary>
        private async void SurveyAllPages()
        {
            if (_theirs is not { } theirs || _closed) return;
            int threshold = Threshold, percent = _thresholdPercent, count = _pageCount;
            var lines = await a.RunBusyAsync("Comparing every page…", token =>
            {
                var result = new List<string>(count + 2);
                int differing = 0;
                for (int page = 1; page <= count; page++)
                {
                    token.ThrowIfCancellationRequested();
                    var pair = RenderVisualPairAsync(mine, theirs, page, VisualSurveyWidth, VisualSurveyHeight, token).GetAwaiter().GetResult();
                    double share = pair.DifferingFraction(threshold) * 100;
                    if (pair.OnlyMine || pair.OnlyTheirs || share > 0) differing++;
                    result.Add(pair.OnlyMine ? $"Page {page}: only in this document"
                        : pair.OnlyTheirs ? $"Page {page}: only in the other document"
                        : $"Page {page}: {share:0.##} %");
                }
                result.Insert(0, $"{differing} of {count} page(s) differ. The share of the pixels that differ on each page"
                                 + (percent > 0 ? $" (colour differences under {percent} % ignored):" : ":"));
                result.Insert(1, "");
                return result;
            });
            if (lines == null || _closed) return;
            a.ShowReport("Visual differences", string.Join("\n", lines), a.BaseName + "_visual_differences");
        }

        /// <summary>The screen closed: the work stops, then the other PDF is closed.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _cts?.Cancel();
            _ = ReleaseAsync(_work);
        }

        private async Task ReleaseAsync(Task work)
        {
            await work;
            _pair = null;
            if (_theirs is { } theirs)
            {
                _theirs = null;
                await theirs.DisposeAsync();
            }
        }
    }
}
