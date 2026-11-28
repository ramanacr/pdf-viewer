using System.Runtime.InteropServices;
using System.Text;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;
using PdfEngine.Vector.Skia;
using PdfViewer.Web.Engine;
using SkiaSharp;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Convert and export: the document as Word, Excel or PowerPoint (the engine's own exporters), its
/// text as a .txt file, and its pages as PNG or JPEG pictures. Each export reads the document with
/// the reader's edits, runs on a worker thread behind a dialog that shows how far it got and can be
/// cancelled, and writes through the system's save dialog (or a folder the reader picks for several
/// pictures). A document that does not allow copying its content is not exported, as on Windows.
/// </summary>
public sealed partial class MainActivity
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PptxMime = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    /// <summary>The image cache of a renderer an export makes for drawings it carries as pictures.</summary>
    private const long ExportRendererCacheBytes = 32L * 1024 * 1024;

    private void ExportWord() => ExportOffice("Word", ".docx", DocxMime, (doc, output, progress, ct) =>
    {
        using var renderer = new SkiaVectorRenderer(ExportRendererCacheBytes);
        var result = DocxExporter.ExportAsync(doc, output, new DocxExportOptions { Renderer = renderer }, progress, ct).GetAwaiter().GetResult();
        return ($"{result.Paragraphs} paragraphs, {result.Tables} tables, {result.Images} pictures", result.Warnings.Count);
    });

    private void ExportExcel() => ExportOffice("Excel", ".xlsx", XlsxMime, (doc, output, progress, ct) =>
    {
        var result = XlsxExporter.ExportAsync(doc, output, new XlsxExportOptions(), progress, ct).GetAwaiter().GetResult();
        return ($"{result.Sheets} sheets, {result.Tables} tables, {result.Numbers + result.Dates} numbers and dates", result.Warnings.Count);
    });

    private void ExportPowerPoint() => ExportOffice("PowerPoint", ".pptx", PptxMime, (doc, output, progress, ct) =>
    {
        using var renderer = new SkiaVectorRenderer(ExportRendererCacheBytes);
        var result = PptxExporter.ExportAsync(doc, output, new PptxExportOptions { Renderer = renderer }, progress, ct).GetAwaiter().GetResult();
        return ($"{result.Slides} slides, {result.TextBoxes} text boxes, {result.Pictures} pictures", result.Warnings.Count);
    });

    /// <summary>
    /// What the exports to Office share: the permission check, the save dialog with the document's
    /// name, the export page by page with progress, and afterwards an offer to open or share the file.
    /// </summary>
    private void ExportOffice(string app, string extension, string mime,
        Func<PdfVectorDocument, Stream, IProgress<int>, CancellationToken, (string Summary, int Warnings)> export)
    {
        if (!NeedDocument() || !MayExport()) return;
        byte[] bytes = _doc!.Bytes;
        int pages = _doc.PageCount;
        (string Summary, int Warnings) result = default;
        SaveNewFileWithProgress(mime, BaseName + extension, $"Exporting to {app}…", (ct, report) =>
        {
            using var engine = OpenForTool(bytes);
            using var output = new MemoryStream();
            result = export(engine, output, new CallbackProgress<int>(n => report($"Exporting to {app}: page {n} of {pages}…")), ct);
            return output.ToArray();
        }, target =>
        {
            string note = result.Warnings > 0 ? $"\n\nSome content could not be carried over ({result.Warnings} note(s))." : "";
            OfferViewOrShare(target, mime, $"Exported to {app}", $"{result.Summary}.{note}");
        });
    }

    /// <summary>The document allows copying its content (ISO 32000-2 Table 22); otherwise the reader is told.</summary>
    private bool MayExport()
    {
        if (_doc?.CanCopy != false) return true;
        Toast.MakeText(this, "This document does not allow copying its content, so it cannot be exported.", ToastLength.Long)?.Show();
        return false;
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// The text of every page, in reading order as the engine extracts it, each page under a
    /// "--- Page n ---" line (the desktop app's format), as a UTF-8 .txt file.
    /// </summary>
    private void ExportText()
    {
        if (!NeedDocument() || !MayExport()) return;
        var doc = _doc!;
        int pages = doc.PageCount, withText = 0;
        SaveNewFileWithProgress("text/plain", BaseName + ".txt", "Exporting text…", (ct, report) =>
        {
            var text = new StringBuilder();
            for (int page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();
                report($"Reading the text: page {page} of {pages}…");
                string pageText = doc.GetPageTextAsync(page, ct).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(pageText)) withText++;
                text.Append("--- Page ").Append(page).Append(" ---\n").Append(pageText.Replace("\r\n", "\n")).Append("\n\n");
            }
            return new UTF8Encoding(false).GetBytes(text.ToString());
        }, target =>
        {
            string message = withText == 0
                ? "No page has a text layer, so the file holds only the page headings. The document is probably scanned."
                : withText < pages
                    ? $"{withText} of {pages} pages had text; the others came out empty."
                    : $"The text of {pages} page(s) was written.";
            OfferViewOrShare(target, "text/plain", "Text exported", message);
        });
    }

    // ------------------------------------------------------------------ pictures of pages

    /// <summary>
    /// Pages as PNG or JPEG pictures: the reader chooses the format (and a JPEG's quality), the
    /// resolution and which pages. One page goes through the save dialog; several go into a folder
    /// the reader picks, named "<c>name</c>_page_0001.png" and so on. Pages are drawn one at a time
    /// as the screen draws them (edits included), each at most the viewer's bitmap limit.
    /// </summary>
    private void ExportImages()
    {
        if (!NeedDocument() || !MayExport()) return;
        var doc = _doc!;
        int current = Math.Clamp(_pages.CurrentPage, 1, doc.PageCount);

        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(24), (int)Dp(8), (int)Dp(24), 0);

        var format = Choices(column, "Format", 0, "PNG (sharp, larger)", "JPEG (smaller, for photos)", "TIFF (lossless, for print)", "BMP");
        var qualityLabel = Label("JPEG quality");
        var quality = Choices(column: null, null, 1, "High (90)", "Medium (75)", "Low (60)");
        column.AddView(qualityLabel);
        column.AddView(quality);
        void ShowQuality()
        {
            var visible = format.CheckedRadioButtonId == format.GetChildAt(1)!.Id ? ViewStates.Visible : ViewStates.Gone;
            qualityLabel.Visibility = visible;
            quality.Visibility = visible;
        }
        format.CheckedChange += (_, _) => ShowQuality();
        ShowQuality();

        var resolution = Choices(column, "Resolution", 1, "72 dpi (screen)", "150 dpi (standard)", "300 dpi (print)", "600 dpi (fine print)");
        var which = Choices(column, "Pages", 0, $"All {doc.PageCount} pages", $"This page ({current})", "Pages…");
        var range = new EditText(this) { Hint = "For example 1-3, 5", InputType = global::Android.Text.InputTypes.ClassText, Visibility = ViewStates.Gone };
        range.SetSingleLine(true);
        column.AddView(range);
        which.CheckedChange += (_, _) =>
        {
            bool custom = which.CheckedRadioButtonId == which.GetChildAt(2)!.Id;
            range.Visibility = custom ? ViewStates.Visible : ViewStates.Gone;
            if (custom) range.RequestFocus();
        };

        var scroll = new ScrollView(this);
        scroll.AddView(column);
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Export pages as images")!
            .SetView(scroll)!
            .SetPositiveButton("Export", (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        // Set here so a wrong page list keeps the dialog open.
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            if (!ReferenceEquals(doc, _doc))
            {
                dialog.Dismiss();
                return;
            }
            List<int>? pages = Index(which) switch
            {
                0 => Enumerable.Range(1, doc.PageCount).ToList(),
                1 => [current],
                _ => ParsePageRanges(range.Text ?? "", doc.PageCount),
            };
            if (pages == null)
            {
                range.Error = $"Type pages from 1 to {doc.PageCount}, such as 1-3, 5.";
                return;
            }
            pages = pages.Distinct().ToList();
            var kind = (PageImageFormat)Index(format);
            int q = Index(quality) switch { 0 => 90, 1 => 75, _ => 60 };
            int dpi = Index(resolution) switch { 0 => 72, 1 => 150, 2 => 300, _ => 600 };
            dialog.Dismiss();
            WritePageImages(doc, pages, kind, q, dpi);
        };
    }

    /// <summary>Renders and writes the chosen pages: one through the save dialog, several into a picked folder.</summary>
    /// <summary>The picture formats of Export pages as images, as Windows offers them.</summary>
    private enum PageImageFormat { Png, Jpeg, Tiff, Bmp }

    private void WritePageImages(ViewerDocument doc, IReadOnlyList<int> pages, PageImageFormat kind, int quality, int dpi)
    {
        var (mime, extension, formatName) = kind switch
        {
            PageImageFormat.Jpeg => ("image/jpeg", ".jpg", "JPEG"),
            PageImageFormat.Tiff => ("image/tiff", ".tif", "TIFF"),
            PageImageFormat.Bmp => ("image/bmp", ".bmp", "BMP"),
            _ => ("image/png", ".png", "PNG"),
        };
        string baseName = BaseName;
        int digits = Math.Max(4, doc.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        string NameOf(int page) => $"{baseName}_page_{page.ToString("D" + digits, System.Globalization.CultureInfo.InvariantCulture)}{extension}";
        bool reduced = false;
        string ReducedNote() => reduced ? $" Large pages were drawn at a lower resolution, to stay within {Options.MaxBitmapPixels / 1_000_000} megapixels." : "";

        if (pages.Count == 1)
        {
            SaveNewFileWithProgress(mime, NameOf(pages[0]), "Drawing the page…", (ct, _) =>
            {
                var (bytes, smaller) = PageImage(doc, pages[0], dpi, kind, quality, ct);
                reduced = smaller;
                return bytes;
            }, target => OfferViewOrShare(target, mime, "Image saved", $"Page {pages[0]} was saved at {dpi} dpi.{ReducedNote()}"));
            return;
        }

        PickFolder(async tree =>
        {
            int written = 0;
            bool ok = await RunProgressAsync($"Exporting {pages.Count} pages…", (ct, report) =>
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    report($"Exporting page {pages[i]} ({i + 1} of {pages.Count})…");
                    var (bytes, smaller) = PageImage(doc, pages[i], dpi, kind, quality, ct);
                    reduced |= smaller;
                    ct.ThrowIfCancellationRequested();
                    var file = CreateInFolder(tree, NameOf(pages[i]), mime)
                               ?? throw new UserMessageException(written == 0
                                   ? "The folder did not accept a new file. Choose another folder."
                                   : $"The folder did not accept a new file after {written} picture(s). Choose another folder for the rest.");
                    try
                    {
                        WriteUri(file, bytes);
                    }
                    catch
                    {
                        DeleteTarget(file);
                        throw;
                    }
                    written++;
                }
                return true;
            });
            if (ok)
                new AlertDialog.Builder(this).SetTitle("Images saved")!
                    .SetMessage($"{written} pages were saved as {formatName} pictures at {dpi} dpi in the folder you chose.{ReducedNote()}")!
                    .SetPositiveButton("OK", (_, _) => { })!.Show();
            else if (written > 0)
                Toast.MakeText(this, $"{written} picture(s) were saved before it stopped.", ToastLength.Long)?.Show();
        });
    }

    /// <summary>
    /// One page drawn at <paramref name="dpi"/> and encoded (with Skia, in native memory). The page is
    /// drawn within the viewer's bitmap limit; <c>Smaller</c> says it had to be drawn at less.
    /// </summary>
    private static (byte[] Bytes, bool Smaller) PageImage(ViewerDocument doc, int page, int dpi, PageImageFormat kind, int quality, CancellationToken ct)
    {
        double scale = dpi / 72.0;
        var bitmap = doc.RenderPageAsync(page, scale, false, ct).GetAwaiter().GetResult();
        bool smaller = bitmap.Scale < scale * 0.99;
        // Skia on Android writes no TIFF or BMP: the engine layer's encoders do, with the resolution the page was drawn at.
        if (kind is PageImageFormat.Tiff or PageImageFormat.Bmp)
        {
            int drawnDpi = Math.Max(1, (int)Math.Round(bitmap.Scale * 72));
            var pixels = bitmap.Pixels.AsSpan();
            return (kind == PageImageFormat.Tiff ? ViewerImageEncoders.Tiff(pixels, bitmap.Width, bitmap.Height, drawnDpi)
                : ViewerImageEncoders.Bmp(pixels, bitmap.Width, bitmap.Height, drawnDpi), smaller);
        }
        bool jpeg = kind == PageImageFormat.Jpeg;
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var segment = bitmap.Pixels;
        var handle = GCHandle.Alloc(segment.Array!, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject() + segment.Offset, bitmap.Width * 4);
            // Pages are drawn over white, so a JPEG (which has no transparency) looks the same.
            using var data = pixmap.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? quality : 100)
                             ?? throw new UserMessageException($"Page {page} could not be encoded as a picture.");
            return (data.ToArray(), smaller);
        }
        finally
        {
            handle.Free();
        }
    }

    // ------------------------------------------------------------------ shared pieces

    /// <summary>A heading line in a tool's dialog.</summary>
    private TextView Label(string text)
    {
        var label = new TextView(this) { Text = text };
        label.SetTextColor(AccentColor);
        label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        label.SetPadding(0, (int)Dp(12), 0, (int)Dp(2));
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) label.AccessibilityHeading = true;
        return label;
    }

    /// <summary>A labelled group of radio buttons (48 dp each), added to <paramref name="column"/> when given, <paramref name="selected"/> checked.</summary>
    private RadioGroup Choices(LinearLayout? column, string? label, int selected, params string[] options)
    {
        var group = new RadioGroup(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        for (int i = 0; i < options.Length; i++)
        {
            var button = new RadioButton(this) { Text = options[i], Id = View.GenerateViewId() };
            button.SetMinHeight((int)Dp(48));
            button.SetTextColor(TextColor);
            group.AddView(button);
            if (i == selected) group.Check(button.Id);
        }
        if (column != null)
        {
            if (label != null) column.AddView(Label(label));
            column.AddView(group);
        }
        return group;
    }

    /// <summary>The index of the checked button of a <see cref="Choices"/> group.</summary>
    private static int Index(RadioGroup group)
    {
        for (int i = 0; i < group.ChildCount; i++)
            if (group.GetChildAt(i)!.Id == group.CheckedRadioButtonId) return i;
        return 0;
    }

    /// <summary>
    /// After a file other than a PDF was written: what was made, and buttons to open it in an app
    /// that opens that kind of file or to share it (read access granted to that app only).
    /// </summary>
    private void OfferViewOrShare(Uri target, string mime, string title, string message)
    {
        new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetMessage(message)!
            .SetPositiveButton("Open", (_, _) =>
            {
                var view = new Intent(Intent.ActionView);
                view.SetDataAndType(target, mime);
                view.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartOther(view);
            })!
            .SetNeutralButton("Share", (_, _) =>
            {
                var send = new Intent(Intent.ActionSend);
                send.SetType(mime);
                send.PutExtra(Intent.ExtraStream, target);
                send.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartOther(Intent.CreateChooser(send, "Share")!);
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    /// <summary>Starts another app's activity; when no app can take it, the reader is told.</summary>
    private void StartOther(Intent intent)
    {
        try
        {
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "No app on this device opens this kind of file.", ToastLength.Long)?.Show();
        }
    }

    /// <summary>
    /// As <c>SaveNewFile</c>, with a working dialog whose line the work updates (page by page): the
    /// save dialog, then <paramref name="make"/> on a worker thread, the bytes written, and the
    /// unfinished file removed after a failure or a cancel.
    /// </summary>
    private void SaveNewFileWithProgress(string mimeType, string suggestedName, string working,
        Func<CancellationToken, Action<string>, byte[]> make, Action<Uri> saved)
    {
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeType);
        intent.PutExtra(Intent.ExtraTitle, SafeFileName(suggestedName));
        StartForResult(intent, async (result, data) =>
        {
            if (result != Result.Ok || data?.Data is not { } target) return;
            bool ok = await RunProgressAsync(working, (token, report) =>
            {
                byte[] bytes = make(token, report);
                token.ThrowIfCancellationRequested();
                report("Writing the file…");
                WriteUri(target, bytes);
                return true;
            }, onFailure: () => DeleteTarget(target));
            if (ok) saved(target);
        });
    }

    /// <summary>
    /// As <c>RunBusyAsync</c>, with a line of progress the work sets from its thread
    /// (<c>report</c>); it returns default when cancelled or failed (the failure is shown).
    /// </summary>
    private async Task<T?> RunProgressAsync<T>(string working, Func<CancellationToken, Action<string>, T> work, Action? onFailure = null)
    {
        using var cts = new CancellationTokenSource();
        var row = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding((int)Dp(24), (int)Dp(20), (int)Dp(24), (int)Dp(8));
        row.AddView(new ProgressBar(this) { Indeterminate = true });
        var text = new TextView(this) { Text = working };
        text.SetPadding((int)Dp(16), 0, 0, 0);
        text.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        text.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var dialog = new AlertDialog.Builder(this)
            .SetView(row)!
            .SetCancelable(false)!
            .SetNegativeButton("Cancel", (_, _) => cts.Cancel())!
            .Create()!;
        dialog.Show();
        string? last = null;
        void Report(string line)
        {
            if (line == last) return;
            last = line;
            RunOnUiThread(() =>
            {
                if (!cts.IsCancellationRequested) text.Text = line;
            });
        }
        try
        {
            return await Task.Run(() => work(cts.Token, Report), cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            onFailure?.Invoke();
            Toast.MakeText(this, "Cancelled.", ToastLength.Short)?.Show();
            return default;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke();
            ShowFailure(ex);
            return default;
        }
        finally
        {
            cts.Cancel(); // late reports from the worker no longer touch the dialog
            if (!IsFinishing && !IsDestroyed) dialog.Dismiss();
        }
    }

    /// <summary>Progress that calls back on the reporting thread (no synchronization context to post to).</summary>
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
