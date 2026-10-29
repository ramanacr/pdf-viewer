using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Edits: form filling (a tap on a field; text with the soft keyboard, check boxes, radio buttons,
/// choices), highlight, underline and strike-out on a text selection, freehand ink, rectangles,
/// ellipses, lines and arrows (a drag), sticky notes and text boxes (a tap), delete, undo and
/// redo, the annotation list, "Save" and "Save a copy". The rules and the writing are the shared <see cref="ViewerEditSession"/>'s (the web viewer's rules for permissions, read-only
/// fields, /MaxLen and signed or certified documents); after each change the engine writes the
/// edits into the document as an incremental update and the pages are drawn again from it. "Save"
/// writes them back into the opened file when Android granted write access to it
/// (<see cref="ViewerSaveBack"/>); a copy is saved with the system's save dialog
/// (ACTION_CREATE_DOCUMENT), which grants access to the one file the reader names. Neither needs a
/// storage permission. After a save the undo history stays, as in the desktop and web viewers.
/// Document JavaScript never runs.
/// </summary>
public sealed partial class MainActivity
{
    private const int SaveCopyRequest = 2;

    private ViewerEditSession? _session;
    private LinearLayout _annotateBar = null!;
    private TextView _annotateButton = null!, _undoButton = null!, _redoButton = null!, _deleteButton = null!, _closeAnnotateButton = null!;
    private TextView _highlightButton = null!, _underlineButton = null!, _strikeButton = null!;
    private (TextView Button, PageView.PageTool Tool)[] _toolButtons = [];
    private bool _noticeShown;

    private IEnumerable<TextView> EditButtons =>
        _toolButtons.Select(t => t.Button).Concat([_undoButton, _redoButton, _deleteButton, _closeAnnotateButton, _highlightButton, _underlineButton, _strikeButton]);

    private bool CanAnnotate => _session?.CanAnnotate == true;

    /// <summary>
    /// The annotate bar: the tools in a strip that scrolls sideways (a phone is too narrow for
    /// all of them), then undo, redo, delete and close, which stay in place.
    /// </summary>
    private View BuildAnnotateBar()
    {
        _annotateBar = Bar();
        _annotateBar.Visibility = ViewStates.Gone;
        _toolButtons =
        [
            (ToolButton("Draw", "Draw with one finger (two fingers zoom)", (_, _) => SetTool(PageView.PageTool.Ink)), PageView.PageTool.Ink),
            (ToolButton("Note", "Tap where a note goes", (_, _) => SetTool(PageView.PageTool.Note)), PageView.PageTool.Note),
            (ToolButton("Text", "Text box: tap where the text goes", (_, _) => SetTool(PageView.PageTool.Text)), PageView.PageTool.Text),
            (ToolButton("Rectangle", "Rectangle: drag from one corner to the other", (_, _) => SetTool(PageView.PageTool.Rectangle)), PageView.PageTool.Rectangle),
            (ToolButton("Ellipse", "Ellipse: drag across it from corner to corner", (_, _) => SetTool(PageView.PageTool.Ellipse)), PageView.PageTool.Ellipse),
            (ToolButton("Line", "Line: drag from one end to the other", (_, _) => SetTool(PageView.PageTool.Line)), PageView.PageTool.Line),
            (ToolButton("Arrow", "Arrow: drag from its tail to its point", (_, _) => SetTool(PageView.PageTool.Arrow)), PageView.PageTool.Arrow),
        ];
        _undoButton = ToolButton("↶", "Undo", (_, _) => Change(s => s.Undo()));
        _redoButton = ToolButton("↷", "Redo", (_, _) => Change(s => s.Redo()));
        _deleteButton = ToolButton("Delete", "Delete the chosen annotation", (_, _) => DeletePicked());
        _closeAnnotateButton = ToolButton("✕", "Stop annotating", (_, _) => ShowAnnotateBar(false));
        var tools = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        foreach (var (button, _) in _toolButtons) tools.AddView(button);
        var strip = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false, FillViewport = false };
        strip.AddView(tools);
        _annotateBar.AddView(strip, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _annotateBar.AddView(_undoButton);
        _annotateBar.AddView(_redoButton);
        _annotateBar.AddView(_deleteButton);
        _annotateBar.AddView(_closeAnnotateButton);
        return _annotateBar;
    }

    /// <summary>Highlight, underline and strike-out in the selection bar (shown when the document allows annotations).</summary>
    private void AddMarkupButtons(LinearLayout bar)
    {
        _highlightButton = ToolButton("Highlight", "Highlight the selected text", (_, _) => MarkSelection(ViewerAnnotationKind.Highlight));
        _underlineButton = ToolButton("Underline", "Underline the selected text", (_, _) => MarkSelection(ViewerAnnotationKind.Underline));
        _strikeButton = ToolButton("Strike", "Strike through the selected text", (_, _) => MarkSelection(ViewerAnnotationKind.StrikeOut));
        foreach (var b in new[] { _highlightButton, _underlineButton, _strikeButton }) bar.AddView(b);
    }

    private void ConnectEdits()
    {
        _pages.FieldTapped += OnFieldTapped;
        _pages.AnnotationTapped += OnAnnotationTapped;
        _pages.EmptyTapped += () => Pick(null);
        _pages.InkDrawn += (page, stroke) => Change(s => s.AddInk(page, stroke) != null, unchanged: () => _pages.DropPendingInk(stroke));
        _pages.NoteRequested += (page, point) =>
        {
            SetTool(PageView.PageTool.Browse);
            AskText("New note", "", ViewerAnnotationStore.MaxContentsLength, multiline: true, password: false,
                text => Change(s => s.AddNote(page, point, text) is { } a && Picked(a.Id)));
        };
        _pages.TextRequested += (page, point) =>
        {
            SetTool(PageView.PageTool.Browse);
            AskText("New text box", "", ViewerAnnotationStore.MaxContentsLength, multiline: true, password: false,
                text => Change(s => s.AddFreeText(page, point, text) is { } a && Picked(a.Id)));
        };
        // A drag shorter than ViewerEditSession.MinShapeSize adds nothing: its outline goes.
        _pages.ShapeDrawn += (tool, page, from, to, outline) => Change(s => tool switch
        {
            PageView.PageTool.Rectangle => s.AddShape(ViewerAnnotationKind.Square, page, from, to),
            PageView.PageTool.Ellipse => s.AddShape(ViewerAnnotationKind.Circle, page, from, to),
            PageView.PageTool.Line => s.AddLine(page, from, to, arrow: false),
            _ => s.AddLine(page, from, to, arrow: true),
        } != null, unchanged: () => _pages.DropPendingInk(outline));
    }

    /// <summary>A new document (or none): its edit session, nothing chosen, the browse tool.</summary>
    private void StartEdits(ViewerEditSession? session)
    {
        _session = session;
        _noticeShown = false;
        _pages.Session = session;
        ShowAnnotateBar(false);
        UpdateEditButtons();
    }

    private void ShowAnnotateBar(bool show)
    {
        if (show && !CanAnnotate)
        {
            Toast.MakeText(this, _doc?.EditRestriction ?? "This document does not allow annotations.", ToastLength.Long)?.Show();
            return;
        }
        _annotateBar.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (!show)
        {
            _pages.Tool = PageView.PageTool.Browse;
            Pick(null);
        }
        UpdateEditButtons();
    }

    private void SetTool(PageView.PageTool tool)
    {
        _pages.Tool = _pages.Tool == tool ? PageView.PageTool.Browse : tool;
        Pick(null);
        if (PageView.Drags(_pages.Tool)) Toast.MakeText(this, "Draw with one finger; two fingers zoom.", ToastLength.Short)?.Show();
        UpdateEditButtons();
    }

    private void UpdateEditButtons()
    {
        bool annotate = CanAnnotate;
        static void Enable(TextView b, bool on)
        {
            b.Enabled = on;
            b.Alpha = on ? 1f : 0.4f;
        }
        Enable(_undoButton, _session?.CanUndo == true);
        Enable(_redoButton, _session?.CanRedo == true);
        Enable(_deleteButton, annotate && _pages.PickedAnnotation != null);
        // The chosen tool is underlined.
        foreach (var (b, tool) in _toolButtons)
        {
            b.PaintFlags = _pages.Tool == tool ? b.PaintFlags | global::Android.Graphics.PaintFlags.UnderlineText : b.PaintFlags & ~global::Android.Graphics.PaintFlags.UnderlineText;
            b.Selected = _pages.Tool == tool; // TalkBack says "selected"
        }
        foreach (var b in new[] { _highlightButton, _underlineButton, _strikeButton })
            b.Visibility = annotate ? ViewStates.Visible : ViewStates.Gone;
        // The selection bar is short of room on a phone when it has the markup buttons.
        _selectionLabel.Visibility = annotate ? ViewStates.Gone : ViewStates.Visible;
    }

    private void Pick(string? id)
    {
        _pages.PickedAnnotation = id;
        UpdateEditButtons();
    }

    private bool Picked(string id)
    {
        Pick(id);
        return true;
    }

    // ------------------------------------------------------------------ changes

    /// <summary>
    /// Runs a change of the reader's (true when something changed), then has the engine write it
    /// into the document and the pages drawn again. A refusal is shown as a short message.
    /// </summary>
    private void Change(Func<ViewerEditSession, bool> change, Action? unchanged = null)
    {
        if (_session is not { } session) return;
        bool changed;
        try
        {
            changed = change(session);
        }
        catch (ViewerAnnotationException ex)
        {
            // A longer reason (a text box's missing font) stays up long enough to read.
            Toast.MakeText(this, ex.Message, ex.Message.Length > 60 ? ToastLength.Long : ToastLength.Short)?.Show();
            changed = false;
        }
        if (!changed)
        {
            unchanged?.Invoke();
            UpdateEditButtons();
            return;
        }
        if (_pages.PickedAnnotation is { } picked && session.Store.Find(picked) == null) Pick(null);
        _ = WriteAsync(session);
    }

    private async Task WriteAsync(ViewerEditSession session)
    {
        UpdateEditButtons();
        if (!_noticeShown && session.TakeNotice() is { } notice)
        {
            _noticeShown = true;
            new AlertDialog.Builder(this).SetTitle("Signed document")!.SetMessage(notice)!.SetPositiveButton("OK", (_, _) => { })!.Show();
        }
        var result = await session.ApplyAsync();
        if (!ReferenceEquals(session, _session)) return;
        if (result.Written)
        {
            _pages.Refresh();
            _thumbnails.Release();
        }
        else if (result.Failed != null)
        {
            _pages.DropPendingInk();
        }
        if ((result.Refused ?? result.Failed) is { } message) Toast.MakeText(this, message, ToastLength.Long)?.Show();
        UpdateEditButtons();
    }

    private void MarkSelection(ViewerAnnotationKind kind)
    {
        if (_pages.Selection is not { } selection) return;
        Change(s => s.MarkText(kind, selection) != null);
        _pages.ClearSelection();
    }

    private void DeletePicked()
    {
        if (_pages.PickedAnnotation is not { } id) return;
        Change(s => s.Delete(id));
        Pick(null);
    }

    private void OnAnnotationTapped(ViewerAnnotation a)
    {
        Pick(a.Id);
        if (CanAnnotate && _annotateBar.Visibility != ViewStates.Visible) ShowAnnotateBar(true);
        if (a.Kind == ViewerAnnotationKind.Note || a.Contents.Length > 0) ShowComment(a);
    }

    /// <summary>A note's comment (any annotation's text): edited when the document allows it, else read.</summary>
    private void ShowComment(ViewerAnnotation a)
    {
        string title = ViewerEditSession.Label(a.Kind) + (a.Author.Length > 0 ? " · " + a.Author : "");
        if (!CanAnnotate)
        {
            new AlertDialog.Builder(this).SetTitle(title)!.SetMessage(a.Contents.Length > 0 ? a.Contents : "(no text)")!
                .SetPositiveButton("Close", (_, _) => { })!.Show();
            return;
        }
        AskText(title, a.Contents, ViewerAnnotationStore.MaxContentsLength, multiline: true, password: false,
            text => Change(s => s.SetContents(a.Id, text)), delete: () => DeletePicked());
    }

    // ------------------------------------------------------------------ form fields

    private void OnFieldTapped(ViewerFormWidget w)
    {
        if (_session is not { } session) return;
        if (w.ReadOnly)
        {
            Toast.MakeText(this, "This field is read-only.", ToastLength.Short)?.Show();
            return;
        }
        var current = session.ValueOf(w.Name) ?? w.Values;
        string label = w.Tooltip.Length > 0 ? w.Tooltip : w.Name;
        switch (w.Kind)
        {
            case ViewerFormKind.Text:
                AskText(label, current.FirstOrDefault() ?? "", w.MaxLength > 0 ? w.MaxLength : ViewerEditSession.MaxFieldValueLength, w.Multiline, w.Password,
                    text => SetField(w, [text]));
                break;
            case ViewerFormKind.CheckBox or ViewerFormKind.Radio:
                SetField(w, [w.ToggleValue]);
                break;
            case ViewerFormKind.ComboBox:
            {
                var exports = w.Options.Select(o => o.Export).ToList();
                var builder = new AlertDialog.Builder(this).SetTitle(label)!
                    .SetSingleChoiceItems(w.Options.Select(o => o.Display).ToArray(), exports.IndexOf(current.FirstOrDefault() ?? ""), (sender, e) =>
                    {
                        (sender as IDialogInterface)?.Dismiss();
                        SetField(w, [exports[e.Which]]);
                    })!
                    .SetNegativeButton("Cancel", (_, _) => { })!;
                // An editable combo box also takes a value of the reader's own.
                if (w.Editable)
                    builder.SetNeutralButton("Type…", (_, _) => AskText(label, current.FirstOrDefault() ?? "", ViewerEditSession.MaxFieldValueLength, false, false, text => SetField(w, [text])));
                builder.Show();
                break;
            }
            default:
            {
                var exports = w.Options.Select(o => o.Export).ToList();
                var shown = w.Options.Select(o => o.Display).ToArray();
                if (w.MultiSelect)
                {
                    var on = exports.Select(current.Contains).ToArray();
                    new AlertDialog.Builder(this).SetTitle(label)!
                        .SetMultiChoiceItems(shown, on, (_, e) => on[e.Which] = e.IsChecked)!
                        .SetPositiveButton("OK", (_, _) => SetField(w, exports.Where((_, i) => on[i]).ToList()))!
                        .SetNegativeButton("Cancel", (_, _) => { })!
                        .Show();
                }
                else
                {
                    new AlertDialog.Builder(this).SetTitle(label)!
                        .SetSingleChoiceItems(shown, exports.IndexOf(current.FirstOrDefault() ?? ""), (sender, e) =>
                        {
                            (sender as IDialogInterface)?.Dismiss();
                            SetField(w, [exports[e.Which]]);
                        })!
                        .SetNegativeButton("Cancel", (_, _) => { })!
                        .Show();
                }
                break;
            }
        }
    }

    private void SetField(ViewerFormWidget w, IReadOnlyList<string> values)
    {
        if (_session is not { } session) return;
        if (session.SetFieldValue(w, values, out var refusal))
            _ = WriteAsync(session);
        else if (refusal != null)
            Toast.MakeText(this, refusal, ToastLength.Short)?.Show();
    }

    /// <summary>
    /// A text dialog with the soft keyboard up: at most <paramref name="maxLength"/> characters
    /// (a field's /MaxLen), one line or several, hidden for a password field.
    /// </summary>
    private void AskText(string title, string text, int maxLength, bool multiline, bool password, Action<string> done, Action? delete = null)
    {
        var input = new EditText(this) { Text = text };
        input.InputType = password ? InputTypes.ClassText | InputTypes.TextVariationPassword
            : multiline ? InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences
            : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        if (!multiline) input.SetSingleLine(true);
        input.SetFilters([new InputFilterLengthFilter(maxLength)]);
        input.SetSelection(input.Text?.Length ?? 0);
        var builder = new AlertDialog.Builder(this).SetTitle(title)!.SetView(input)!
            .SetPositiveButton("OK", (_, _) => done(input.Text ?? ""))!
            .SetNegativeButton("Cancel", (_, _) => { })!;
        if (delete != null) builder.SetNeutralButton("Delete", (_, _) => delete());
        var dialog = builder.Create()!;
        dialog.Window?.SetSoftInputMode(SoftInput.StateVisible);
        dialog.Show();
        input.RequestFocus();
    }

    // ------------------------------------------------------------------ annotation list

    private void ShowAnnotationList()
    {
        if (_session is not { } session) return;
        var items = session.List();
        if (items.Count == 0)
        {
            Toast.MakeText(this, "This document has no annotations.", ToastLength.Short)?.Show();
            return;
        }
        // "p. 3 · Highlight · text", replies indented under the annotation they answer.
        var lines = items.Select(i => new string(' ', 2 * Math.Min(i.Level, 4)) + $"p. {i.Annotation.Page} · {i.Label}" + (i.Summary.Length > 0 ? " · " + i.Summary : "")).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle($"Annotations ({items.Count})")!
            .SetItems(lines, (_, e) =>
            {
                var a = items[e.Which].Annotation;
                ShowThumbnails(false);
                _pages.GoToAnnotation(a);
                Pick(a.Kind == ViewerAnnotationKind.Reply ? a.InReplyTo : a.Id);
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ saving

    /// <summary>
    /// The document whose copy the save dialog was opened for. It is not kept in the saved state:
    /// after Android ended the process while the dialog was open, the edits are gone, and the
    /// file the dialog created is removed instead of being written with other bytes.
    /// </summary>
    private ViewerDocument? _saveFor;

    /// <summary>"Save a copy": the system's save dialog, with the file's name and "(edited)" offered.</summary>
    private void PickSaveCopy()
    {
        if (_doc == null) return;
        if (_session?.IsSaving == true || _saveFor != null)
        {
            Toast.MakeText(this, "A copy is being saved already.", ToastLength.Short)?.Show();
            return;
        }
        string name = System.IO.Path.GetFileNameWithoutExtension(_fileName ?? "Document");
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/pdf");
        intent.PutExtra(Intent.ExtraTitle, (name.Length > 0 ? name : "Document") + " (edited).pdf");
        _saveFor = _doc;
        StartActivityForResult(intent, SaveCopyRequest);
    }

    /// <summary>The save dialog answered: a file to write, or null when the reader cancelled.</summary>
    private void SaveDialogAnswered(Uri? target)
    {
        var doc = _saveFor;
        _saveFor = null;
        if (target == null) return;
        if (doc == null || !ReferenceEquals(doc, _doc))
        {
            // The process was ended (or the document changed) while the dialog was open.
            DeleteTarget(target);
            new AlertDialog.Builder(this).SetTitle("Nothing was saved")!
                .SetMessage("The changes were lost while the save dialog was open (Android closed the app to free memory). Make them again and save a copy.")!
                .SetPositiveButton("OK", (_, _) => { })!.Show();
            return;
        }
        _ = SaveCopyAsync(doc, target);
    }

    private async Task SaveCopyAsync(ViewerDocument doc, Uri target)
    {
        var session = _session;
        ViewerSave? save = session != null ? await session.BeginSaveAsync() : null;
        byte[]? bytes = session == null ? doc.Bytes : save?.Bytes;
        if (bytes == null || !ReferenceEquals(doc, _doc))
        {
            if (save != null) session!.EndSave(save, saved: false);
            DeleteTarget(target);
            Toast.MakeText(this, session?.IsSaving == true ? "A copy is being saved already." : "The changes could not be written into the document, so nothing was saved.", ToastLength.Long)?.Show();
            return;
        }
        try
        {
            await Task.Run(() =>
            {
                // "wt": the file the reader named is written from the start (truncated).
                using var output = ContentResolver?.OpenOutputStream(target, "wt") ?? throw new IOException("No stream for the file.");
                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (save != null) session!.EndSave(save, saved: false);
            // A part-written PDF is worse than none.
            bool removed = DeleteTarget(target);
            Toast.MakeText(this, $"The copy could not be saved ({ex.GetType().Name}).{(removed ? " The unfinished file was removed." : "")}", ToastLength.Long)?.Show();
            return;
        }
        if (save != null) session!.EndSave(save, saved: true);
        Toast.MakeText(this, "Copy saved.", ToastLength.Short)?.Show();
    }

    /// <summary>Removes the file the save dialog created (best effort; the provider may not allow it).</summary>
    private bool DeleteTarget(Uri target)
    {
        try
        {
            return ContentResolver != null && global::Android.Provider.DocumentsContract.DeleteDocument(ContentResolver, target);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ save into the opened file

    /// <summary>The provider's size and last-modified time of the open file when it was opened or last saved here.</summary>
    private ViewerFileStamp _openedStamp;

    /// <summary>
    /// "Save": the edits written back to the file that was opened, when Android lets this app write
    /// it; otherwise the reader is told why and offered "Save a copy". A file that changed since it
    /// was opened is replaced only when the reader agrees. <paramref name="after"/> runs once the
    /// file is saved (or when there is nothing to save).
    /// </summary>
    private void Save(Action? after = null, bool overwrite = false)
    {
        if (_doc == null || _uri is not { } uri) return;
        if (_saveFor != null)
        {
            Toast.MakeText(this, ViewerSaveBack.BusyMessage, ToastLength.Short)?.Show();
            return;
        }
        bool canWrite = CanWriteBack(uri);
        var now = canWrite && _session?.IsDirty == true ? Stamp(uri) : default;
        switch (ViewerSaveBack.Check(_session, canWrite, _openedStamp, now, overwrite))
        {
            case ViewerSaveBackStep.NothingToSave:
                if (after != null) after();
                else Toast.MakeText(this, ViewerSaveBack.NothingToSaveMessage, ToastLength.Short)?.Show();
                break;
            case ViewerSaveBackStep.Busy:
                Toast.MakeText(this, ViewerSaveBack.BusyMessage, ToastLength.Short)?.Show();
                break;
            case ViewerSaveBackStep.CannotWrite:
                new AlertDialog.Builder(this)
                    .SetTitle("Save a copy instead?")!
                    .SetMessage(ViewerSaveBack.CannotWriteMessage)!
                    .SetPositiveButton("Save a copy", (_, _) => PickSaveCopy())!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show();
                break;
            case ViewerSaveBackStep.ChangedOnDisk:
                new AlertDialog.Builder(this)
                    .SetTitle("The file has changed")!
                    .SetMessage(ViewerSaveBack.ChangedOnDiskMessage)!
                    .SetPositiveButton("Replace", (_, _) => Save(after, overwrite: true))!
                    .SetNeutralButton("Save a copy", (_, _) => PickSaveCopy())!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show();
                break;
            default:
                _ = SaveBackAsync(_session!, uri, after);
                break;
        }
    }

    private async Task SaveBackAsync(ViewerEditSession session, Uri uri, Action? after)
    {
        // The complete bytes (the file as opened plus the edits as an incremental update) are made
        // first; only then is the file written, on a worker thread.
        var result = await ViewerSaveBack.SaveAsync(session, bytes => Task.Run(() => WriteBack(uri, bytes)));
        if (!ReferenceEquals(session, _session)) return;
        UpdateEditButtons();
        if (result.Saved)
        {
            _openedStamp = Stamp(uri);
            Toast.MakeText(this, "Saved.", ToastLength.Short)?.Show();
            after?.Invoke();
            return;
        }
        if (result.FileMayBeDamaged)
        {
            // The edits are still in memory: a copy keeps them.
            new AlertDialog.Builder(this)
                .SetTitle("Not saved")!
                .SetMessage(result.Problem!)!
                .SetPositiveButton("Save a copy", (_, _) => PickSaveCopy())!
                .SetNegativeButton("Close", (_, _) => { })!
                .Show();
            return;
        }
        if (result.Problem is { } problem) Toast.MakeText(this, problem, ToastLength.Long)?.Show();
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> over the file in one go ("wt": from the start, truncated) and
    /// returns the length the file has afterwards, or null when the provider cannot tell.
    /// </summary>
    private long? WriteBack(Uri uri, byte[] bytes)
    {
        var resolver = ContentResolver ?? throw new IOException("No content resolver.");
        var descriptor = resolver.OpenFileDescriptor(uri, "wt") ?? throw new IOException("No file descriptor for the file.");
        long? length = null;
        // Closing the stream closes the descriptor.
        var output = new ParcelFileDescriptor.AutoCloseOutputStream(descriptor);
        try
        {
            output.Write(bytes);
            output.Flush();
            // Some providers ignore the "t" of "wt": an older, longer file would keep its end.
            if (descriptor.StatSize > bytes.Length) output.Channel!.Truncate(bytes.Length);
            try
            {
                output.FD?.Sync();
            }
            catch (Java.IO.SyncFailedException)
            {
                // Not a file on this device's storage (a pipe to the provider): nothing to sync.
            }
            if (descriptor.StatSize is >= 0 and var size) length = size;
        }
        finally
        {
            output.Close();
        }
        // A provider that serves the file through a pipe reports the size it now has.
        return length ?? QueryLong(uri, IOpenableColumns.Size);
    }

    /// <summary>
    /// The app may write the file: Android granted write access to it (the document picker, a
    /// persisted grant or "Open with" with write access), and a document provider does not say
    /// that the file is read-only. Never throws.
    /// </summary>
    private bool CanWriteBack(Uri uri)
    {
        try
        {
            if (CheckCallingOrSelfUriPermission(uri, ActivityFlags.GrantWriteUriPermission) != Permission.Granted) return false;
            if (DocumentsContract.IsDocumentUri(this, uri) && QueryLong(uri, DocumentsContract.Document.ColumnFlags) is { } flags)
                return (flags & (long)DocumentContractFlags.SupportsWrite) != 0;
            return true;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The provider's size and (for a document provider) last-modified time of the file; unknown values are null.</summary>
    private ViewerFileStamp Stamp(Uri uri) =>
        new(QueryLong(uri, IOpenableColumns.Size),
            DocumentsContract.IsDocumentUri(this, uri) ? QueryLong(uri, DocumentsContract.Document.ColumnLastModified) : null);

    private long? QueryLong(Uri uri, string column)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, [column], null, null, null);
            if (cursor == null || !cursor.MoveToFirst()) return null;
            int i = cursor.GetColumnIndex(column);
            return i < 0 || cursor.IsNull(i) ? null : cursor.GetLong(i);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Share sends the file as saved (its own content URI), without edits no save has written:
    /// with such edits the reader chooses first.
    /// </summary>
    private void ShareChecked()
    {
        if (_session?.IsDirty != true)
        {
            Share();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Unsaved changes")!
            .SetMessage("Share sends the file as it is saved, without your unsaved changes. Save them first, or share without them?")!
            .SetPositiveButton("Save", (_, _) => Save(after: Share))!
            .SetNeutralButton("Share without changes", (_, _) => Share())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>
    /// Runs <paramref name="then"/> at once, or, with edits no save has written, once the reader
    /// saves them (Save; where the file cannot be written, Save offers a copy) or discards them.
    /// </summary>
    private void AfterDiscard(Action then)
    {
        if (_session?.IsDirty != true)
        {
            then();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Save changes?")!
            .SetMessage("This document has changes that are not saved. Save them, or discard them?")!
            .SetPositiveButton("Save", (_, _) => Save(after: then))!
            .SetNeutralButton("Discard", (_, _) => then())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }
}
