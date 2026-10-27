using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Edits: form filling (a tap on a field; text with the soft keyboard, check boxes, radio buttons,
/// choices), highlight, underline and strike-out on a text selection, freehand ink, sticky notes,
/// delete, undo and redo, the annotation list, and "Save a copy". The rules and the writing are
/// the shared <see cref="ViewerEditSession"/>'s (the web viewer's rules for permissions, read-only
/// fields, /MaxLen and signed or certified documents); after each change the engine writes the
/// edits into the document as an incremental update and the pages are drawn again from it. A copy
/// is saved with the system's save dialog (ACTION_CREATE_DOCUMENT), which grants access to the
/// one file the reader names: no storage permission. Document JavaScript never runs.
/// </summary>
public sealed partial class MainActivity
{
    private const int SaveCopyRequest = 2;

    private ViewerEditSession? _session;
    private LinearLayout _annotateBar = null!;
    private TextView _annotateButton = null!, _drawButton = null!, _noteButton = null!, _undoButton = null!, _redoButton = null!, _deleteButton = null!, _closeAnnotateButton = null!;
    private TextView _highlightButton = null!, _underlineButton = null!, _strikeButton = null!;
    private bool _noticeShown;

    private IEnumerable<TextView> EditButtons =>
        [_drawButton, _noteButton, _undoButton, _redoButton, _deleteButton, _closeAnnotateButton, _highlightButton, _underlineButton, _strikeButton];

    private bool CanAnnotate => _session?.CanAnnotate == true;

    private View BuildAnnotateBar()
    {
        _annotateBar = Bar();
        _annotateBar.Visibility = ViewStates.Gone;
        _drawButton = ToolButton("Draw", "Draw with one finger (two fingers zoom)", (_, _) => SetTool(PageView.PageTool.Ink));
        _noteButton = ToolButton("Note", "Tap where a note goes", (_, _) => SetTool(PageView.PageTool.Note));
        _undoButton = ToolButton("↶", "Undo", (_, _) => Change(s => s.Undo()));
        _redoButton = ToolButton("↷", "Redo", (_, _) => Change(s => s.Redo()));
        _deleteButton = ToolButton("Delete", "Delete the chosen annotation", (_, _) => DeletePicked());
        _closeAnnotateButton = ToolButton("✕", "Stop annotating", (_, _) => ShowAnnotateBar(false));
        _annotateBar.AddView(_drawButton);
        _annotateBar.AddView(_noteButton);
        _annotateBar.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1f));
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
        if (_pages.Tool == PageView.PageTool.Ink) Toast.MakeText(this, "Draw with one finger; two fingers zoom.", ToastLength.Short)?.Show();
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
        foreach (var (b, tool) in new[] { (_drawButton, PageView.PageTool.Ink), (_noteButton, PageView.PageTool.Note) })
            b.PaintFlags = _pages.Tool == tool ? b.PaintFlags | global::Android.Graphics.PaintFlags.UnderlineText : b.PaintFlags & ~global::Android.Graphics.PaintFlags.UnderlineText;
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
            Toast.MakeText(this, ex.Message, ToastLength.Short)?.Show();
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

    /// <summary>
    /// Share sends the file as opened (its own content URI), without edits no save has written:
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
            .SetMessage("Share sends the file as it was opened, without your changes. To share them, save a copy first and share that file.")!
            .SetPositiveButton("Save a copy", (_, _) => PickSaveCopy())!
            .SetNeutralButton("Share without changes", (_, _) => Share())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }
    /// <summary>Runs <paramref name="then"/> at once, or, with edits no save has written, once the reader chooses to discard them.</summary>
    private void AfterDiscard(Action then)
    {
        if (_session?.IsDirty != true)
        {
            then();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Unsaved changes")!
            .SetMessage("This document has changes that are not saved. Save a copy first, or discard them?")!
            .SetPositiveButton("Save a copy", (_, _) => PickSaveCopy())!
            .SetNeutralButton("Discard", (_, _) => then())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }
}
