using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Print;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Vector.Document;
using PdfViewer.Web.Engine;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// The Tools sheet and what the document tools share: a registry of system dialogs that answer with
/// a result (pickers and save dialogs, so a tool does not need a request code of its own), picking
/// files and a folder, writing a new file through the system's save dialog, a "working" dialog that
/// can be cancelled, text reports, and opening the current document again in the engine (with its
/// edits) for a tool that writes a new PDF. It also holds Print (the system print dialog),
/// Document properties and full screen.
/// <para>
/// None of this needs a permission: every file comes from or goes to a location the reader picks in
/// the system's own dialogs (Storage Access Framework), which grant access to that one file or folder.
/// A tool that changes the document writes a new PDF and offers to open it; the opened file is not
/// changed by a tool. Nothing is sent anywhere: the app has no internet permission.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>Request codes of <see cref="StartForResult"/> start here, above the fixed ones (1 to 9).</summary>
    private int _nextRequest = 100;

    private readonly Dictionary<int, Action<Result, Intent?>> _requests = new();

    /// <summary>The password the open document was opened with (memory only; never stored or logged), for tools that read it again.</summary>
    private string? _password;

    private bool _fullScreen;

    /// <summary>
    /// Starts a system dialog that answers with a result; <paramref name="answered"/> gets it. If
    /// Android ends the process while the dialog is open, the answer is dropped (the tool starts again).
    /// </summary>
    private void StartForResult(Intent intent, Action<Result, Intent?> answered)
    {
        int code = _nextRequest++;
        _requests[code] = answered;
        try
        {
            StartActivityForResult(intent, code);
        }
        catch (ActivityNotFoundException)
        {
            _requests.Remove(code);
            Toast.MakeText(this, "No app on this device can do this.", ToastLength.Short)?.Show();
        }
    }

    /// <summary>A result for <see cref="StartForResult"/>: true when it was one of its requests.</summary>
    private bool AnswerRequest(int requestCode, Result resultCode, Intent? data)
    {
        if (!_requests.Remove(requestCode, out var answered)) return requestCode >= 100;
        answered(resultCode, data);
        return true;
    }

    // ------------------------------------------------------------------ overlays and Back

    /// <summary>The frame that holds the pages; tool screens (read mode, page organizer) cover it.</summary>
    private FrameLayout _frame = null!;

    private View? _toolOverlay;
    private Action? _toolOverlayClosed;

    /// <summary>Tools in a mode of their own (redaction marks) that Back ends first: true when one took it.</summary>
    private readonly List<Func<bool>> _backHandlers = new();

    /// <summary>
    /// A tool's own screen over the pages, until Back or <see cref="CloseToolOverlay"/>; <paramref name="closed"/>
    /// runs when it goes. One at a time: a new one replaces the last.
    /// </summary>
    private void ShowToolOverlay(View view, Action? closed = null)
    {
        CloseToolOverlay();
        ShowSearch(false);
        _pages.ClearSelection();
        _toolOverlay = view;
        _toolOverlayClosed = closed;
        view.Elevation = Dp(6);
        _frame.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        view.RequestFocus();
    }

    /// <summary>Closes the tool screen or ends a tool's mode; true when there was one.</summary>
    private bool CloseToolOverlay()
    {
        if (_toolOverlay is { } view)
        {
            _toolOverlay = null;
            _frame.RemoveView(view);
            var closed = _toolOverlayClosed;
            _toolOverlayClosed = null;
            closed?.Invoke();
            return true;
        }
        foreach (var handler in _backHandlers.ToList())
            if (handler()) return true;
        return false;
    }

    /// <summary>A bar at the top of a tool screen: Back, a title, and the tool's own buttons.</summary>
    private LinearLayout ToolScreenBar(string title, out TextView titleView, params TextView[] actions)
    {
        var bar = Bar();
        bar.SetBackgroundColor(IsDark ? Color.ParseColor("#1B1D21") : Color.ParseColor("#ECEFF3"));
        var back = ToolButton("←", "Back", (_, _) => CloseToolOverlay());
        back.SetTextColor(AccentColor);
        bar.AddView(back);
        titleView = new TextView(this) { Text = title };
        titleView.SetTextColor(TextColor);
        titleView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 17);
        titleView.SetSingleLine(true);
        titleView.Ellipsize = TextUtils.TruncateAt.End;
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) titleView.AccessibilityHeading = true;
        bar.AddView(titleView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        foreach (var action in actions)
        {
            action.SetTextColor(AccentColor);
            bar.AddView(action);
        }
        return bar;
    }

    /// <summary>The colour of a tool screen's background.</summary>
    private Color ScreenColor => IsDark ? Color.ParseColor("#121316") : Color.ParseColor("#F7F8FA");

    /// <summary>The start screen's actions: open a PDF, make one from pictures, merge PDFs, all tools.</summary>
    private View BuildStartActions()
    {
        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding(0, (int)Dp(24), 0, 0);
        column.SetGravity(GravityFlags.CenterHorizontal);
        foreach (var (text, description, run) in new (string, string, Action)[]
                 {
                     ("Open a PDF", "Open a PDF from this device or a cloud drive", PickDocument),
                     ("PDF from pictures", "Make a PDF from photos and pictures", CreateFromImages),
                     ("Merge PDFs", "Join several PDFs into one", MergePdfs),
                     ("All tools", "Every tool of this app", ShowTools),
                 })
        {
            var button = new Button(this) { Text = text, ContentDescription = description };
            button.SetAllCaps(false);
            button.SetMinHeight((int)Dp(48));
            button.Click += (_, _) => run();
            column.AddView(button, new LinearLayout.LayoutParams((int)Dp(240), ViewGroup.LayoutParams.WrapContent) { TopMargin = (int)Dp(8) });
        }
        return column;
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// The system picker for files of the given types (several with <paramref name="multiple"/>); the
    /// picker grants read access to the files chosen, so no storage permission is needed.
    /// </summary>
    private void PickFiles(string[] mimeTypes, bool multiple, Action<IReadOnlyList<Uri>> picked)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeTypes.Length == 1 ? mimeTypes[0] : "*/*");
        if (mimeTypes.Length > 1) intent.PutExtra(Intent.ExtraMimeTypes, mimeTypes);
        if (multiple) intent.PutExtra(Intent.ExtraAllowMultiple, true);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartForResult(intent, (result, data) =>
        {
            if (result != Result.Ok || data == null) return;
            var uris = new List<Uri>();
            if (data.ClipData is { } clip)
            {
                for (int i = 0; i < clip.ItemCount; i++)
                    if (clip.GetItemAt(i)?.Uri is { } u) uris.Add(u);
            }
            else if (data.Data is { } one)
            {
                uris.Add(one);
            }
            if (uris.Count > 0) picked(uris);
        });
    }

    /// <summary>The system folder picker (ACTION_OPEN_DOCUMENT_TREE), for tools that write several files.</summary>
    private void PickFolder(Action<Uri> picked)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        StartForResult(intent, (result, data) =>
        {
            if (result == Result.Ok && data?.Data is { } tree) picked(tree);
        });
    }

    /// <summary>Creates a file named <paramref name="name"/> in a folder the reader picked; null when the provider refuses.</summary>
    private Uri? CreateInFolder(Uri tree, string name, string mimeType)
    {
        try
        {
            var parent = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree));
            return parent == null || ContentResolver == null ? null : DocumentsContract.CreateDocument(ContentResolver, parent, mimeType, name);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to a file this app may write (from a save dialog or a picked folder), from the start.</summary>
    private void WriteUri(Uri target, byte[] bytes)
    {
        using var output = ContentResolver?.OpenOutputStream(target, "wt") ?? throw new IOException("No stream for the file.");
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    /// <summary>Reads a picked file, at most <paramref name="maxBytes"/>; a larger one is refused with a message for the reader.</summary>
    private byte[] ReadUri(Uri uri, long maxBytes)
    {
        using var input = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("No stream for the file.");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > maxBytes)
                throw new FileTooLargeException($"This file is larger than {maxBytes / (1024 * 1024)} MB, the most this tool takes.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>
    /// The system's save dialog for a new file, then <paramref name="make"/> on a worker thread (with
    /// a cancellable "working" dialog) and the bytes written. A failure or a cancel removes the
    /// unfinished file. <paramref name="saved"/> runs on the UI thread with the new file.
    /// </summary>
    private void SaveNewFile(string mimeType, string suggestedName, string working, Func<CancellationToken, byte[]> make, Action<Uri>? saved = null)
    {
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeType);
        intent.PutExtra(Intent.ExtraTitle, SafeFileName(suggestedName));
        StartForResult(intent, async (result, data) =>
        {
            if (result != Result.Ok || data?.Data is not { } target) return;
            bool ok = await RunBusyAsync(working, token =>
            {
                byte[] bytes = make(token);
                token.ThrowIfCancellationRequested();
                WriteUri(target, bytes);
                return true;
            }, onFailure: () => DeleteTarget(target));
            if (ok) saved?.Invoke(target);
        });
    }

    /// <summary>
    /// A new PDF made by a tool: the save dialog, the file written, then "Open it now?". The open
    /// document is not changed (with unsaved edits, opening the new file asks first).
    /// </summary>
    private void SaveNewPdf(string suggestedName, string working, string done, Func<CancellationToken, byte[]> make)
    {
        SaveNewFile("application/pdf", suggestedName, working, make, target => OfferOpen(target, done));
    }

    /// <summary>"Saved. Open it now?" for a PDF a tool wrote.</summary>
    private void OfferOpen(Uri target, string message)
    {
        new AlertDialog.Builder(this)
            .SetTitle("Saved")!
            .SetMessage(message)!
            .SetPositiveButton("Open it", (_, _) => AfterDiscard(() =>
            {
                RememberPosition();
                _ = OpenUriAsync(target, null);
            }))!
            .SetNegativeButton("Stay here", (_, _) => { })!
            .Show();
    }

    /// <summary>A file name without characters document providers refuse.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' };
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Document" : clean.Length > 120 ? clean[..120] : clean;
    }

    /// <summary>The open document's file name without ".pdf", for the names tools offer.</summary>
    private string BaseName
    {
        get
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(_fileName ?? "");
            return name.Length > 0 ? name : (_doc?.Title is { Length: > 0 } t ? SafeFileName(t) : "Document");
        }
    }

    // ------------------------------------------------------------------ the open document for tools

    /// <summary>The open document's bytes with the reader's edits written into them (or null without a document).</summary>
    private byte[]? CurrentBytes()
    {
        if (_doc is not { IsComplete: true } doc) return null;
        return doc.Bytes;
    }

    /// <summary>
    /// The open document (with its edits) opened again in the engine, for a tool to read or rewrite;
    /// call it on a worker thread and dispose the result. Encrypted documents open with the password
    /// they were opened with.
    /// </summary>
    private PdfVectorDocument OpenForTool(byte[] bytes) =>
        PdfVectorDocument.Open(new PdfEngine.Vector.Parsing.PdfMemoryByteSource(bytes), (_fileName ?? "Document") + ".pdf", password: _password, certificates: []);

    /// <summary>A document is open (and fully read); otherwise the reader is told to open one.</summary>
    private bool NeedDocument()
    {
        if (_doc is { IsComplete: true }) return true;
        Toast.MakeText(this, "Open a PDF first.", ToastLength.Short)?.Show();
        return false;
    }

    // ------------------------------------------------------------------ working dialog

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread behind a "working" dialog with Cancel. Returns
    /// its result, or default when it was cancelled or failed (the failure is told in a message, and
    /// <paramref name="onFailure"/> runs). The message never quotes document text.
    /// </summary>
    private async Task<T?> RunBusyAsync<T>(string working, Func<CancellationToken, T> work, Action? onFailure = null)
    {
        using var cts = new CancellationTokenSource();
        var dialog = BusyDialog(working, cts);
        try
        {
            return await Task.Run(() => work(cts.Token), cts.Token);
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
            if (!IsFinishing && !IsDestroyed) dialog.Dismiss();
        }
    }

    /// <summary>A message for a tool's failure: the engine's own messages are written for users; others name the exception type.</summary>
    private void ShowFailure(Exception ex)
    {
        string message = ex switch
        {
            OutOfMemoryException => "There is not enough memory for this on this device.",
            FileTooLargeException => ex.Message,
            PdfEngine.Vector.Diagnostics.PdfVectorException v => v.Message,
            UserMessageException u => u.Message,
            _ => $"This did not work ({ex.GetType().Name}).",
        };
        new AlertDialog.Builder(this).SetTitle("Not done")!.SetMessage(message)!.SetPositiveButton("OK", (_, _) => { })!.Show();
    }

    /// <summary>An exception whose message is written for the reader (a tool's refusal).</summary>
    private sealed class UserMessageException(string message) : Exception(message);

    private AlertDialog BusyDialog(string working, CancellationTokenSource cts)
    {
        var row = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding((int)Dp(24), (int)Dp(20), (int)Dp(24), (int)Dp(8));
        row.AddView(new ProgressBar(this) { Indeterminate = true });
        var text = new TextView(this) { Text = working };
        text.SetPadding((int)Dp(16), 0, 0, 0);
        text.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        row.AddView(text);
        var dialog = new AlertDialog.Builder(this)
            .SetView(row)!
            .SetCancelable(false)!
            .SetNegativeButton("Cancel", (_, _) => cts.Cancel())!
            .Create()!;
        dialog.Show();
        return dialog;
    }

    // ------------------------------------------------------------------ reports and small dialogs

    /// <summary>
    /// A long text (a check's report) in a scrolling dialog, with Copy and, when <paramref name="exportName"/>
    /// is given, Save as a text file.
    /// </summary>
    private void ShowReport(string title, string text, string? exportName = null)
    {
        var body = new TextView(this) { Text = text };
        body.SetTextIsSelectable(true);
        body.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        body.SetPadding((int)Dp(24), (int)Dp(12), (int)Dp(24), (int)Dp(12));
        var scroll = new ScrollView(this);
        scroll.AddView(body);
        var builder = new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetView(scroll)!
            .SetPositiveButton("Close", (_, _) => { })!
            .SetNeutralButton("Copy", (_, _) =>
            {
                var clipboard = GetSystemService(ClipboardService) as global::Android.Content.ClipboardManager;
                if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText(title, text);
            })!;
        if (exportName != null)
            builder.SetNegativeButton("Save as text", (_, _) =>
                SaveNewFile("text/plain", exportName + ".txt", "Saving…", _ => System.Text.Encoding.UTF8.GetBytes(text),
                    _ => Toast.MakeText(this, "Report saved.", ToastLength.Short)?.Show()));
        builder.Show();
    }

    /// <summary>A dialog with one line of text input; <paramref name="done"/> gets the text when the reader confirms.</summary>
    private void AskLine(string title, string? message, string initial, string hint, InputTypes type, string confirm, Action<string> done)
    {
        var input = new EditText(this) { Text = initial, Hint = hint, InputType = type };
        input.SetSingleLine(true);
        var frame = new FrameLayout(this);
        frame.SetPadding((int)Dp(20), (int)Dp(8), (int)Dp(20), 0);
        frame.AddView(input);
        var builder = new AlertDialog.Builder(this).SetTitle(title)!.SetView(frame)!
            .SetPositiveButton(confirm, (_, _) => done(input.Text ?? ""))!
            .SetNegativeButton("Cancel", (_, _) => { })!;
        if (message != null) builder.SetMessage(message);
        builder.Show();
        input.RequestFocus();
    }

    /// <summary>
    /// Parses page ranges as the reader types them ("1-3, 5, 8-"), within 1 to <paramref name="pageCount"/>,
    /// in the order given; null when the text is not a page list.
    /// </summary>
    internal static List<int>? ParsePageRanges(string text, int pageCount)
    {
        var pages = new List<int>();
        foreach (string raw in text.Split(',', ';'))
        {
            string part = raw.Trim().Replace('–', '-').Replace('—', '-');
            if (part.Length == 0) continue;
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out int p) || p < 1 || p > pageCount) return null;
                pages.Add(p);
                continue;
            }
            string a = part[..dash].Trim(), b = part[(dash + 1)..].Trim();
            int from = a.Length == 0 ? 1 : int.TryParse(a, out int x) ? x : -1;
            int to = b.Length == 0 ? pageCount : int.TryParse(b, out int y) ? y : -1;
            if (from < 1 || to < 1 || from > pageCount || to > pageCount) return null;
            if (from <= to) for (int p = from; p <= to; p++) pages.Add(p);
            else for (int p = from; p >= to; p--) pages.Add(p);
        }
        return pages.Count == 0 ? null : pages;
    }

    // ------------------------------------------------------------------ the Tools sheet

    private sealed record ToolEntry(string Title, string Description, Action Run, bool NeedsDocument = true);

    private sealed record ToolSection(string Title, ToolEntry[] Entries);

    /// <summary>The tools, by what the reader wants to do. A tool that needs a document is dimmed without one.</summary>
    private ToolSection[] Tools() =>
    [
        new("Pages",
        [
            new("Edit text and images", "Change a page's text, move or replace its pictures, add text and pictures", EditTextAndImages),
            new("Organize pages", "Reorder, rotate, duplicate, delete or add blank pages", OrganizePages),
            new("Insert pages from a PDF", "Add the pages of another PDF after a page", InsertPagesFromPdf),
            new("Extract pages", "Save some pages as a new PDF", ExtractPages),
            new("Split", "Cut the document into several PDFs", SplitDocument),
            new("Merge PDFs", "Join several PDFs into one", MergePdfs, NeedsDocument: false),
            new("Headers, footers and watermarks", "Page numbers, dates, Bates numbers and watermarks", ShowPageMarks),
        ]),
        new("Forms",
        [
            new("Import form data", "Fill the form from an XFDF or FDF file", ImportFormData),
            new("Export form data", "Save the form's values as XFDF or FDF", ExportFormData),
            new("Reset form", "Clear the values of the form fields", ResetForm),
            new("Flatten", "Make form fields and comments part of the page, in a copy", FlattenDocument),
        ]),
        new("Protect and sign",
        [
            new("Protect with password", "Encrypt a copy with AES-256 and set permissions", ProtectWithPassword),
            new("Remove security", "Save a copy without password or restrictions", RemoveSecurity),
            new("Redact", "Remove text and areas for good, in a copy", StartRedaction),
            new("Sign", "Sign with a certificate file (.pfx or .p12)", SignDocument),
            new("Verify signatures", "Check the digital signatures and what changed after", VerifySignatures),
        ]),
        new("Convert and export",
        [
            new("Export to Word", "A .docx file with flowing text, tables and pictures", ExportWord),
            new("Export to Excel", "A .xlsx file with each table on a sheet", ExportExcel),
            new("Export to PowerPoint", "A .pptx file with a slide for each page", ExportPowerPoint),
            new("Export text", "The text of every page as a .txt file", ExportText),
            new("Export pages as images", "PNG or JPEG files, at the resolution you choose", ExportImages),
            new("PDF/A: check and convert", "Check archive conformance, or save a PDF/A copy", PdfACheckAndConvert),
            new("Reduce file size", "Recompress images and remove unused objects, in a copy", ReduceFileSize),
        ]),
        new("Create",
        [
            new("PDF from pictures", "Make a PDF from photos and pictures, a page each", CreateFromImages, NeedsDocument: false),
            new("PDF from a text file", "Lay out a plain text file as a PDF", CreateFromTextFile, NeedsDocument: false),
            new("Combine files", "PDFs and pictures in one PDF, with a bookmark for each", CombineFiles, NeedsDocument: false),
        ]),
        new("Read and accessibility",
        [
            new("Read mode", "The text reflowed to the screen, in a size you choose", ShowReadMode),
            new("Read aloud", "Read the document from this page with the device's voice", ToggleReadAloud),
            new("Accessibility check", "Check PDF/UA-1 and list the issues", AccessibilityCheck),
            new("Make accessible", "Tag the document for screen readers, in a copy", MakeAccessible),
        ]),
        new("Inspect",
        [
            new("Measure", "Distance, perimeter and area at the page's scale or yours", MeasureFromTools),
            new("Document properties", "Title, author, version, security and more", ShowProperties),
            new("Document safety", "Scripts, actions and embedded files found in the document", ShowDocumentSafety),
            new("Save clean copy", "A copy without scripts, launch actions and embedded files", SaveCleanCopy),
            new("Embedded files", "List the attachments and save one", ShowEmbeddedFiles),
            new("Compare with another PDF", "Text that was added, deleted or changed", CompareDocuments),
            new("Preflight", "Check fonts, images, colours and inks for printing", RunPreflight),
        ]),
    ];

    /// <summary>
    /// The Tools sheet: every document tool by section, each with a line that says what it does.
    /// Rows are 56 dp, read by TalkBack as one button with their description; section titles are headings.
    /// </summary>
    private void ShowTools()
    {
        bool open = _doc is { IsComplete: true };
        var list = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        list.SetPadding(0, (int)Dp(8), 0, (int)Dp(8));
        AlertDialog? dialog = null;
        foreach (var section in Tools())
        {
            var heading = new TextView(this) { Text = section.Title };
            heading.SetTextColor(AccentColor);
            heading.SetTypeface(null, TypefaceStyle.Bold);
            heading.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
            heading.SetPadding((int)Dp(24), (int)Dp(16), (int)Dp(24), (int)Dp(4));
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) heading.AccessibilityHeading = true;
            list.AddView(heading);
            foreach (var entry in section.Entries)
            {
                bool enabled = open || !entry.NeedsDocument;
                var row = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical, Clickable = true, Focusable = true };
                row.SetMinimumHeight((int)Dp(56));
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding((int)Dp(24), (int)Dp(6), (int)Dp(24), (int)Dp(6));
                var title = new TextView(this) { Text = entry.Title };
                title.SetTextColor(TextColor);
                title.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
                var line = new TextView(this) { Text = entry.Description };
                line.SetTextColor(IsDark ? Color.ParseColor("#A9AEB6") : Color.ParseColor("#5A5F66"));
                line.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
                row.AddView(title);
                row.AddView(line);
                row.ContentDescription = entry.Title + ". " + entry.Description + (enabled ? "" : ". Open a PDF first.");
                row.Alpha = enabled ? 1f : 0.45f;
                using (var outValue = new global::Android.Util.TypedValue())
                {
                    if (Theme?.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackground, outValue, true) == true)
                        row.SetBackgroundResource(outValue.ResourceId);
                }
                var run = entry.Run;
                row.Click += (_, _) =>
                {
                    if (!enabled)
                    {
                        Toast.MakeText(this, "Open a PDF first.", ToastLength.Short)?.Show();
                        return;
                    }
                    dialog?.Dismiss();
                    run();
                };
                list.AddView(row);
            }
        }
        var scroll = new ScrollView(this);
        scroll.AddView(list);
        dialog = new AlertDialog.Builder(this).SetTitle("Tools")!.SetView(scroll)!.SetNegativeButton("Close", (_, _) => { })!.Create()!;
        dialog.Show();
    }

    // ------------------------------------------------------------------ print

    /// <summary>
    /// Print: the system print dialog (printer, copies, pages, colour, paper), with the document as it
    /// is shown, edits included. An encrypted document is sent without its encryption, as the print
    /// service cannot open it; a document that does not allow printing is not printed.
    /// </summary>
    private void Print()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (!doc.CanPrint)
        {
            Toast.MakeText(this, "This document does not allow printing.", ToastLength.Long)?.Show();
            return;
        }
        if (GetSystemService(PrintService) is not PrintManager printing)
        {
            Toast.MakeText(this, "This device cannot print.", ToastLength.Short)?.Show();
            return;
        }
        byte[] bytes = doc.Bytes;
        bool encrypted = doc.IsEncrypted;
        string name = BaseName;
        printing.Print(name, new PdfPrintAdapter(name, doc.PageCount, () =>
        {
            if (!encrypted) return bytes;
            using var engine = OpenForTool(bytes);
            return PdfDecryptedCopyWriter.Write(engine);
        }), null);
    }

    /// <summary>Hands the print service the PDF's bytes (made on a worker thread when it asks for them).</summary>
    private sealed class PdfPrintAdapter(string name, int pageCount, Func<byte[]> bytes) : PrintDocumentAdapter
    {
        public override void OnLayout(PrintAttributes? oldAttributes, PrintAttributes? newAttributes, CancellationSignal? cancellationSignal,
            LayoutResultCallback? callback, Bundle? extras)
        {
            if (cancellationSignal?.IsCanceled == true)
            {
                callback?.OnLayoutCancelled();
                return;
            }
            var info = new PrintDocumentInfo.Builder(name + ".pdf").SetContentType(PrintContentType.Document)!.SetPageCount(pageCount)!.Build();
            callback?.OnLayoutFinished(info, !Equals(oldAttributes, newAttributes));
        }

        public override void OnWrite(PageRange[]? pages, ParcelFileDescriptor? destination, CancellationSignal? cancellationSignal, WriteResultCallback? callback)
        {
            Task.Run(() =>
            {
                try
                {
                    byte[] data = bytes();
                    if (cancellationSignal?.IsCanceled == true)
                    {
                        callback?.OnWriteCancelled();
                        return;
                    }
                    using var output = new Java.IO.FileOutputStream(destination!.FileDescriptor);
                    output.Write(data);
                    output.Flush();
                    callback?.OnWriteFinished([PageRange.AllPages!]);
                }
                catch (Exception ex)
                {
                    callback?.OnWriteFailed($"The document could not be sent to the printer ({ex.GetType().Name}).");
                }
            });
        }
    }

    // ------------------------------------------------------------------ properties

    /// <summary>Document properties: the information dictionary, version, size, security, tags and signatures.</summary>
    private async void ShowProperties()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        ViewerMetadata m;
        try
        {
            m = await Task.Run(() => doc.GetMetadataAsync());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowFailure(ex);
            return;
        }
        if (!ReferenceEquals(doc, _doc)) return;
        static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
        static string Date(DateTime? d) => d is { } v ? v.ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "—";
        var page = doc.Pages.Count > 0 ? doc.Pages[Math.Max(0, _pages.CurrentPage - 1)] : default;
        var lines = new List<string>
        {
            $"File: {Or(_fileName ?? "")}",
            $"Title: {Or(m.Title)}",
            $"Author: {Or(m.Author)}",
            $"Subject: {Or(m.Subject)}",
            $"Keywords: {Or(m.Keywords)}",
            $"Created: {Date(m.CreationDate)}",
            $"Modified: {Date(m.ModificationDate)}",
            $"Application: {Or(m.Creator)}",
            $"PDF producer: {Or(m.Producer)}",
            "",
            $"PDF version: {Or(m.PdfVersion)}",
            $"Pages: {m.PageCount}",
            $"Page size (this page): {page.Width / 72 * 25.4:0} × {page.Height / 72 * 25.4:0} mm ({page.Width / 72:0.##} × {page.Height / 72:0.##} in)",
            $"File size: {FormatBytes(m.FileSizeBytes)}",
            $"Tagged (accessible structure): {(m.IsTagged ? "Yes" : "No")}",
            $"Encrypted: {(m.IsEncrypted ? "Yes" : "No")}",
            $"Copying text: {(m.CanCopy ? "Allowed" : "Not allowed")}",
            $"Printing: {(m.CanPrint ? "Allowed" : "Not allowed")}",
            $"Signed: {(m.IsSigned ? "Yes" : "No")}" + (m.CertificationLevel is { } level ? $" (certified, level {level})" : ""),
        };
        if (m.WasRepaired) lines.Add("The file was damaged and has been repaired to open it.");
        ShowReport("Document properties", string.Join("\n", lines));
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    // ------------------------------------------------------------------ full screen

    /// <summary>Full screen: the bars and the system bars hide; Back (or a tap on "Exit full screen") brings them back.</summary>
    private void SetFullScreen(bool on)
    {
        if (on && _doc == null) return;
        _fullScreen = on;
        _toolbar.Visibility = on ? ViewStates.Gone : ViewStates.Visible;
        if (on)
        {
            ShowSearch(false);
            ShowAnnotateBar(false);
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window?.InsetsController is { } insets)
        {
            if (on)
            {
                insets.Hide(WindowInsets.Type.SystemBars());
                insets.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            else
            {
                insets.Show(WindowInsets.Type.SystemBars());
            }
        }
        else
        {
#pragma warning disable CA1422 // the way before Android 11
            if (Window?.DecorView is { } decor)
                decor.SystemUiFlags = on
                    ? SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable
                    : SystemUiFlags.Visible;
#pragma warning restore CA1422
        }
        if (on) Toast.MakeText(this, "Full screen. Press Back to leave it.", ToastLength.Short)?.Show();
    }
}
