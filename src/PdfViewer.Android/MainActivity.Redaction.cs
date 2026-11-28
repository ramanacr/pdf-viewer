using Android.App;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Documents;
using PdfEngine.Vector;
using PdfEngine.Vector.Redaction;
using PdfViewer.Web.Engine;
using SkiaSharp;
using Color = Android.Graphics.Color;

namespace PdfViewer.Android;

/// <summary>
/// Redaction, as the desktop viewer's: a mode with a bar of its own in which the reader marks what
/// to remove (an area dragged on a page, the text selection with "Redact" in the selection bar, or
/// every place a word or phrase appears), sees the marks as red boxes over the pages, takes one
/// away with a tap or the last ones with undo, then applies them. Applying writes a new PDF through
/// the system's save dialog with the engine's <see cref="PdfRedactor"/>: the text, pictures and
/// drawings under the marks (and the annotations and fields there) are removed from the content,
/// not covered, and the areas are filled black; the result is a new file without the old objects,
/// and the open document is not changed. JPEG pictures are decoded with Skia, so only their pixels
/// under a mark go; a CMYK JPEG under a mark is removed whole. Marks stay in memory only.
/// </summary>
public sealed partial class MainActivity
{
    private bool _redacting;

    /// <summary>The marks (shown-page points) and the step each was made in, for undo (a search's marks are one step).</summary>
    private readonly List<(int Page, ViewerRect Rect, int Step)> _redactMarks = new();
    private int _redactStep;

    private LinearLayout? _redactBar;
    private TextView _redactCount = null!, _redactAreaButton = null!, _redactUndoButton = null!, _redactApplyButton = null!;
    private bool _redactBackRegistered;

    /// <summary>The Tools entry "Redact": redaction mode, with dragging an area on.</summary>
    private void StartRedaction()
    {
        if (!NeedDocument() || _redacting) return;
        ShowAnnotateBar(false);
        _pages.ClearSelection();
        _redactBar ??= BuildRedactBar();
        if (_redactBar.Parent == null && _annotateBar.Parent is ViewGroup parent)
            parent.AddView(_redactBar, parent.IndexOfChild(_annotateBar) + 1, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        ThemeRedactBar();
        if (!_redactBackRegistered)
        {
            _redactBackRegistered = true;
            _backHandlers.Add(() =>
            {
                if (!_redacting) return false;
                LeaveRedaction(null);
                return true;
            });
        }
        _redacting = true;
        _redactBar.Visibility = ViewStates.Visible;
        SetRedactArea(true);
        UpdateRedaction();
        UpdateEditButtons();
        Toast.MakeText(this, "Drag over what to remove. Tap a mark to take it away.", ToastLength.Long)?.Show();
    }

    private void ConnectRedaction()
    {
        _pages.RedactionAreaDrawn += (page, from, to) =>
        {
            if (!_redacting) return;
            var r = new ViewerRect(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
            if (_doc != null && page >= 1 && page <= _doc.PageCount)
            {
                // Kept on the page.
                var g = _doc.Geometry[page - 1];
                double x1 = Math.Clamp(r.X, 0, g.ShownWidth), y1 = Math.Clamp(r.Y, 0, g.ShownHeight);
                double x2 = Math.Clamp(r.X + r.Width, 0, g.ShownWidth), y2 = Math.Clamp(r.Y + r.Height, 0, g.ShownHeight);
                r = new ViewerRect(x1, y1, x2 - x1, y2 - y1);
            }
            if (r.Width < 1 || r.Height < 1) return;
            AddMarks([(page, r)]);
        };
        _pages.RedactionMarkTapped += index =>
        {
            if (!_redacting || index < 0 || index >= _redactMarks.Count) return;
            _redactMarks.RemoveAt(index);
            UpdateRedaction();
            Toast.MakeText(this, "Mark removed.", ToastLength.Short)?.Show();
        };
    }

    /// <summary>The redaction bar: the number of marks, Area (drag to mark; off to scroll and select text), Find, undo, Apply and close.</summary>
    private LinearLayout BuildRedactBar()
    {
        var bar = Bar();
        _redactCount = new TextView(this);
        _redactCount.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        _redactCount.SetPadding((int)Dp(8), 0, (int)Dp(4), 0);
        _redactCount.SetSingleLine(true);
        _redactCount.Ellipsize = TextUtils.TruncateAt.End;
        _redactCount.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _redactAreaButton = ToolButton("Area", "Drag over an area to mark it. Turn off to scroll, or to select text with a long press", (_, _) => SetRedactArea(_pages.Tool != PageView.PageTool.Redact));
        var find = ToolButton("Find", "Mark every place a word or phrase appears", (_, _) => MarkSearchResults());
        _redactUndoButton = ToolButton("↶", "Take back the last marks", (_, _) => UndoRedactMark());
        _redactApplyButton = ToolButton("Apply", "Remove what is under the marks, in a new copy", (_, _) => ApplyRedaction());
        var close = ToolButton("✕", "Stop redacting", (_, _) => LeaveRedaction(null));
        bar.AddView(_redactCount, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        foreach (var b in new[] { _redactAreaButton, find, _redactUndoButton, _redactApplyButton, close }) bar.AddView(b);
        bar.Visibility = ViewStates.Gone;
        return bar;
    }

    private void ThemeRedactBar()
    {
        if (_redactBar == null) return;
        _redactBar.SetBackgroundColor(IsDark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3"));
        _redactCount.SetTextColor(TextColor);
        for (int i = 1; i < _redactBar.ChildCount; i++)
            if (_redactBar.GetChildAt(i) is TextView b) b.SetTextColor(AccentColor);
    }

    /// <summary>Dragging marks an area (on), or the pages scroll and a long press selects text (off).</summary>
    private void SetRedactArea(bool on)
    {
        _pages.Tool = on ? PageView.PageTool.Redact : PageView.PageTool.Browse;
        _redactAreaButton.Selected = on;
        _redactAreaButton.PaintFlags = on ? _redactAreaButton.PaintFlags | PaintFlags.UnderlineText : _redactAreaButton.PaintFlags & ~PaintFlags.UnderlineText;
        if (!on) Toast.MakeText(this, "Scroll freely; a long press selects text to redact.", ToastLength.Short)?.Show();
    }

    private void AddMarks(IReadOnlyList<(int Page, ViewerRect Rect)> marks)
    {
        if (marks.Count == 0) return;
        _redactStep++;
        foreach (var (page, rect) in marks) _redactMarks.Add((page, rect, _redactStep));
        UpdateRedaction();
    }

    private void UndoRedactMark()
    {
        if (_redactMarks.Count == 0) return;
        int step = _redactMarks.Max(m => m.Step);
        _redactMarks.RemoveAll(m => m.Step == step);
        UpdateRedaction();
    }

    /// <summary>The marks over the pages, their number in the bar, and undo and Apply enabled with marks.</summary>
    private void UpdateRedaction()
    {
        _pages.RedactionMarks = _redacting ? _redactMarks.Select(m => (m.Page, m.Rect)).ToList() : Array.Empty<(int, ViewerRect)>();
        if (_redactBar == null) return;
        int n = _redactMarks.Count;
        _redactCount.Text = n == 0 ? "Redact: no marks" : n == 1 ? "1 mark" : $"{n} marks";
        foreach (var b in new[] { _redactUndoButton, _redactApplyButton })
        {
            b.Enabled = n > 0;
            b.Alpha = n > 0 ? 1f : 0.4f;
        }
    }

    /// <summary>"Redact" in the selection bar: the selected lines are marked, one step.</summary>
    private void MarkSelectionForRedaction()
    {
        if (!_redacting || _pages.Selections is not { Count: > 0 } parts) return;
        AddMarks(parts.SelectMany(part => part.Bands.Select(b => (part.Page, b))).ToList());
        _pages.ClearSelection();
    }

    /// <summary>"Find": a word or phrase, then every place it appears is marked (one step); the reader is told how many.</summary>
    private void MarkSearchResults()
    {
        if (!_redacting || _doc is not { } doc) return;
        AskLine("Mark every match", "Every place this word or phrase appears is marked for redaction.", "", "Word or phrase",
            InputTypes.ClassText | InputTypes.TextFlagNoSuggestions, "Mark", query => _ = MarkSearchResultsAsync(doc, query));
    }

    private async Task MarkSearchResultsAsync(ViewerDocument doc, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var hits = await RunBusyAsync("Searching…", token =>
        {
            var found = new List<ViewerSearchHit>();
            // On the worker: the pages are searched one after the other.
            var task = Task.Run(async () =>
            {
                await foreach (var hit in doc.SearchAsync(query, cancellationToken: token)) found.Add(hit);
            }, token);
            task.GetAwaiter().GetResult();
            return found;
        });
        if (hits == null || !ReferenceEquals(doc, _doc) || !_redacting) return;
        if (hits.Count == 0)
        {
            Toast.MakeText(this, "No match found.", ToastLength.Short)?.Show();
            return;
        }
        AddMarks(hits.SelectMany(h => h.Rects.Select(r => (h.Page, r))).ToList());
        int pages = hits.Select(h => h.Page).Distinct().Count();
        Toast.MakeText(this, $"Marked {hits.Count} {(hits.Count == 1 ? "match" : "matches")} on {pages} {(pages == 1 ? "page" : "pages")}.", ToastLength.Short)?.Show();
    }

    /// <summary>Leaves redaction mode; with marks, asks first (they would be lost). <paramref name="then"/> runs once it has ended.</summary>
    private void LeaveRedaction(Action? then)
    {
        if (!_redacting)
        {
            then?.Invoke();
            return;
        }
        if (_redactMarks.Count == 0)
        {
            EndRedaction();
            then?.Invoke();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Stop redacting?")!
            .SetMessage($"The {(_redactMarks.Count == 1 ? "mark" : $"{_redactMarks.Count} marks")} not applied will be cleared. Nothing was removed from the document.")!
            .SetPositiveButton("Stop", (_, _) =>
            {
                EndRedaction();
                then?.Invoke();
            })!
            .SetNegativeButton("Keep marking", (_, _) => { })!
            .Show();
    }

    /// <summary>Ends redaction mode at once: the marks are cleared, the bar goes, the pages browse again.</summary>
    private void EndRedaction()
    {
        _redactMarks.Clear();
        if (!_redacting) return;
        _redacting = false;
        if (_redactBar != null) _redactBar.Visibility = ViewStates.Gone;
        if (_pages.Tool == PageView.PageTool.Redact) _pages.Tool = PageView.PageTool.Browse;
        UpdateRedaction();
        UpdateEditButtons();
    }

    // ------------------------------------------------------------------ applying

    /// <summary>
    /// "Apply": what it does is said first (the content under the marks is removed for good, in a
    /// new copy; the open file stays as it is), then the save dialog, the redaction on a worker
    /// thread, and a summary of what was removed with an offer to open the copy.
    /// </summary>
    private void ApplyRedaction()
    {
        if (!_redacting || _redactMarks.Count == 0 || _doc is not { IsComplete: true } doc) return;
        // In user space (PDF points from the bottom-left corner) for the engine, from the shown pages.
        var areas = _redactMarks.Where(m => m.Page >= 1 && m.Page <= doc.PageCount)
            .Select(m => new PdfRedactionArea(m.Page, doc.Geometry[m.Page - 1].ToUser(m.Rect).ToRect())).ToList();
        int n = areas.Count;
        string message = $"The text, pictures and drawings under the {(n == 1 ? "mark" : $"{n} marks")} are removed for good, with any annotations and form fields there, and the areas are filled black. "
                         + "This is done in a new copy that you save next; the open file is not changed."
                         + (doc.IsSigned ? " The copy's signatures will no longer be valid." : "");
        new AlertDialog.Builder(this)
            .SetTitle("Apply the redaction?")!
            .SetMessage(message)!
            .SetPositiveButton("Save redacted copy", (_, _) => SaveRedacted(doc, areas))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private void SaveRedacted(ViewerDocument doc, List<PdfRedactionArea> areas)
    {
        if (!ReferenceEquals(doc, _doc) || CurrentBytes() is not { } bytes) return;
        PdfRedactionResult? result = null;
        SaveNewFile("application/pdf", BaseName + " (redacted).pdf", "Redacting…", token =>
        {
            using var engine = OpenForTool(bytes);
            if (engine.Security is { } s && !PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanModify)
                throw new UserMessageException("This document's security settings do not allow changes, so it cannot be redacted.");
            token.ThrowIfCancellationRequested();
            result = PdfRedactor.Apply(engine, areas, new PdfRedactionOptions { JpegDecoder = DecodeJpeg });
            return result.Bytes;
        }, target =>
        {
            if (ReferenceEquals(doc, _doc)) EndRedaction();
            var r = result!;
            int images = r.ImagesRedacted + r.ImagesRemoved;
            string summary = $"Removed {r.GlyphsRemoved} {(r.GlyphsRemoved == 1 ? "character" : "characters")}, {images} {(images == 1 ? "picture" : "pictures")} and {r.PathsCut} {(r.PathsCut == 1 ? "drawing" : "drawings")}"
                             + (r.AnnotationsRemoved > 0 ? $", and {r.AnnotationsRemoved} {(r.AnnotationsRemoved == 1 ? "annotation" : "annotations")}" : "")
                             + ". The content was removed from the copy, not covered. The open file is unchanged.";
            OfferOpen(target, summary);
        });
    }

    /// <summary>
    /// JPEG samples for the redactor (straight BGRA of the image's own size, or the raw components
    /// when the core maps them through the image's colour space), through Skia's codec: one or three
    /// components only. Null (the JPEG under a mark is removed whole) for a CMYK JPEG, one that does
    /// not decode or is not the size the PDF says, or one over 64 megapixels.
    /// </summary>
    private static byte[]? DecodeJpeg(PdfDecodedImage image)
    {
        try
        {
            int components = JpegComponents(image.Data.Span);
            if (components is not (1 or 3) || image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > 64_000_000) return null;
            using var data = SKData.CreateCopy(image.Data.Span);
            using var codec = SKCodec.Create(data);
            if (codec == null || codec.Info.Width != image.Width || codec.Info.Height != image.Height) return null;
            bool gray = components == 1 && image.MapJpegComponents != null;
            var info = new SKImageInfo(image.Width, image.Height, gray ? SKColorType.Gray8 : SKColorType.Bgra8888, gray ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            var decoded = codec.GetPixels(info, bitmap.GetPixels());
            if (decoded is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            byte[] pixels = bitmap.GetPixelSpan().ToArray();
            if (image.MapJpegComponents is not { } map) return pixels;
            if (gray) return map(pixels, 1, image.Width, image.Height);
            var rgb = new byte[image.Width * image.Height * 3];
            for (int i = 0, p = 0; i < rgb.Length; i += 3, p += 4)
            {
                rgb[i] = pixels[p + 2];
                rgb[i + 1] = pixels[p + 1];
                rgb[i + 2] = pixels[p];
            }
            return map(rgb, 3, image.Width, image.Height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The number of components in a JPEG's frame header (SOFn), or 0 when there is none.</summary>
    private static int JpegComponents(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return 0;
        int i = 2;
        while (i + 3 < d.Length)
        {
            if (d[i] != 0xFF)
            {
                i++;
                continue;
            }
            byte marker = d[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }
            int length = (d[i + 2] << 8) | d[i + 3];
            // SOF0 to SOF15, less DHT (C4), JPG (C8) and DAC (CC): length, precision, height, width, then the count.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return i + 9 < d.Length ? d[i + 9] : 0;
            if (length < 2) return 0;
            i += 2 + length;
        }
        return 0;
    }
}
