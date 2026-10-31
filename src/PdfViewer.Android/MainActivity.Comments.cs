using System.Globalization;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine.Annotations;
using Color = Android.Graphics.Color;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Comment threads and XFDF. Tapping an annotation (or <b>Reply</b> in the annotation bar for the
/// chosen one, or a line of the annotation list) opens its thread: the comment and its replies with
/// author, date and text, its review status, a box to reply, and Edit and Delete for the reader's own
/// replies. The rules and the writing are the shared <see cref="ViewerEditSession"/>'s, the desktop
/// viewer's structure: a reply is a note with /IRT, /RT /R, the Comment icon and /F 28; a review
/// status (Accepted, Rejected, Cancelled, Completed, None) is a hidden reply (/F 30) with /State and
/// /StateModel /Review. Every change is one undo step. <b>⋮ → Export comments</b> writes the
/// annotations as an .xfdf file through the system's save dialog (and offers to share it);
/// <b>⋮ → Import comments</b> adds the annotations of an .xfdf file the reader picks (Storage
/// Access Framework: no storage permission). The reader's name for replies is the one stamps use.
/// </summary>
public sealed partial class MainActivity
{
    private const int ExportXfdfRequest = 3, ImportXfdfRequest = 4;

    /// <summary>
    /// The largest .xfdf file read, in bytes: the engine's limit in characters
    /// (<see cref="PdfEngine.Vector.Annotations.PdfAnnotationXfdf.MaxCharacters"/>), at up to three
    /// bytes each (UTF-8 or UTF-16). The engine then refuses a text longer than its limit.
    /// </summary>
    private const int MaxXfdfBytes = 3 * PdfEngine.Vector.Annotations.PdfAnnotationXfdf.MaxCharacters;

    private TextView _replyButton = null!;

    /// <summary>The session whose comments the save dialog was opened for (not kept in the saved state).</summary>
    private ViewerEditSession? _exportFor;

    /// <summary>The annotation bar's <b>Reply</b> button: the thread of the chosen annotation.</summary>
    private TextView BuildReplyButton() =>
        _replyButton = ToolButton("Reply", "Comments and replies on the chosen annotation", (_, _) =>
        {
            if (_pages.PickedAnnotation is { } id) ShowThread(id);
        });

    /// <summary>The colours of a panel in the system's light or dark theme (a dialog's own views made in code).</summary>
    private Color PanelColor => IsDark ? Color.ParseColor("#202226") : Color.ParseColor("#FFFFFF");
    private Color MutedColor => IsDark ? Color.ParseColor("#A8ADB5") : Color.ParseColor("#5C616A");

    private static string When(DateTimeOffset date) =>
        date == default ? "" : date.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The thread of annotation <paramref name="id"/> (a reply's thread when a reply is given): the
    /// comment, its replies indented by level, the status, a reply box and the actions the document
    /// and the reader's name allow. The dialog is built again after each change.
    /// </summary>
    private void ShowThread(string id)
    {
        if (_session is not { } session) return;
        var start = session.Store.Find(id);
        // A reply opens the thread it belongs to.
        while (start is { Kind: ViewerAnnotationKind.Reply } && session.Store.Find(start.InReplyTo) is { } parent) start = parent;
        if (start == null) return;
        var items = session.Thread(start.Id);
        if (items.Count == 0) return;
        bool annotate = CanAnnotate;
        Dialog? dialog = null;
        void Then(Action action)
        {
            dialog?.Dismiss();
            action();
        }

        var list = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        int pad = (int)Dp(16);
        list.SetPadding(pad, (int)Dp(8), pad, (int)Dp(8));
        list.SetBackgroundColor(PanelColor);
        var root = items[0];
        if (root.Status is { } status)
            list.AddView(Line($"Status: {status}", bold: true, AccentColor, 0, $"Review status: {status}"));
        foreach (var item in items)
        {
            var a = item.Annotation;
            int indent = (int)Dp(16) * Math.Min(item.Level, 4);
            string author = a.Author.Length > 0 ? a.Author : "Unknown author";
            string when = When(a.Created);
            string head = item.Level == 0 ? $"{item.Label} · p. {a.Page}" : "Reply";
            var header = Line($"{head} · {author}" + (when.Length > 0 ? " · " + when : ""), bold: true, TextColor, indent, null);
            if (item.Level == 0 && OperatingSystem.IsAndroidVersionAtLeast(28)) header.AccessibilityHeading = true;
            list.AddView(header);
            string text = a.Contents.Length > 0 ? a.Contents : item.Level == 0 && item.Summary.Length > 0 ? item.Summary : "(no text)";
            list.AddView(Line(text, bold: false, a.Contents.Length > 0 ? TextColor : MutedColor, indent, null));

            // A comment's text may be changed when the document allows annotations (as before replies);
            // a reply only by the reader who wrote it.
            bool own = item.Level > 0 && session.IsOwn(a);
            if (annotate && (item.Level == 0 || own))
            {
                var row = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
                row.SetPadding(indent, 0, 0, 0);
                string what = item.Level == 0 ? "comment" : "reply";
                var annotation = a;
                row.AddView(ToolButton("Edit", $"Edit this {what}", (_, _) => Then(() => EditComment(annotation))));
                if (own) row.AddView(ToolButton("Delete", "Delete this reply", (_, _) => Then(() => ConfirmDelete(annotation, start.Id))));
                foreach (var b in AllButtons(row)) b.SetTextColor(AccentColor);
                list.AddView(row);
            }
            var gap = new View(this);
            list.AddView(gap, new LinearLayout.LayoutParams(1, (int)Dp(8)));
        }

        EditText? reply = null;
        if (annotate)
        {
            reply = new EditText(this) { Hint = "Write a reply", ContentDescription = "Reply text" };
            reply.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences;
            reply.SetFilters([new InputFilterLengthFilter(ViewerAnnotationStore.MaxContentsLength)]);
            reply.SetMinHeight((int)Dp(48));
            reply.SetTextColor(TextColor);
            reply.SetHintTextColor(MutedColor);
            list.AddView(reply, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
        var scroll = new ScrollView(this);
        scroll.SetBackgroundColor(PanelColor);
        scroll.AddView(list);

        var builder = new AlertDialog.Builder(this)
            .SetTitle(items.Count > 1 ? $"Comments ({items.Count - 1} {(items.Count == 2 ? "reply" : "replies")})" : "Comment")!
            .SetView(scroll)!
            .SetNegativeButton("Close", (_, _) => { })!;
        if (annotate)
        {
            builder.SetPositiveButton("Reply", (_, _) => { });
            builder.SetNeutralButton("Status…", (_, _) => AskStatus(start.Id));
        }
        dialog = builder.Create()!;
        dialog.Show();
        if (reply != null)
        {
            // Reply keeps the dialog open when the box is empty; a sent reply builds the thread again.
            var send = ((AlertDialog)dialog).GetButton((int)DialogButtonType.Positive);
            if (send != null)
            {
                send.ContentDescription = "Send the reply";
                send.Click += (_, _) =>
                {
                    string text = reply.Text?.Trim() ?? "";
                    if (text.Length == 0)
                    {
                        Toast.MakeText(this, "Write the reply first.", ToastLength.Short)?.Show();
                        return;
                    }
                    Then(() => WithName(() => SendReply(start.Id, text)));
                };
            }
        }
    }

    private static IEnumerable<TextView> AllButtons(LinearLayout row)
    {
        for (int i = 0; i < row.ChildCount; i++)
            if (row.GetChildAt(i) is TextView t) yield return t;
    }

    /// <summary>One line of a thread: wraps, selectable for TalkBack in reading order.</summary>
    private TextView Line(string text, bool bold, Color color, int indent, string? description)
    {
        var line = new TextView(this) { Text = text, Focusable = true };
        if (description != null) line.ContentDescription = description;
        line.SetTextColor(color);
        line.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bold ? 14 : 16);
        if (bold) line.SetTypeface(null, TypefaceStyle.Bold);
        line.SetPadding(indent, (int)Dp(4), 0, (int)Dp(4));
        return line;
    }

    /// <summary>Runs <paramref name="then"/> once the reader has a name for their replies (asked once, kept on this device).</summary>
    private void WithName(Action then)
    {
        if (Signer() != null)
        {
            then();
            return;
        }
        AskSigner(then, "Replies and statuses carry your name.");
    }

    private void SendReply(string parentId, string text)
    {
        Change(s => s.AddReply(parentId, text) != null);
        ShowThread(parentId);
    }

    private void EditComment(ViewerAnnotation a)
    {
        string root = a.Kind == ViewerAnnotationKind.Reply ? a.InReplyTo : a.Id;
        AskText(a.Kind == ViewerAnnotationKind.Reply ? "Edit reply" : ViewerEditSession.Label(a.Kind), a.Contents, ViewerAnnotationStore.MaxContentsLength,
            multiline: true, password: false, text =>
            {
                Change(s => s.SetContents(a.Id, text));
                ShowThread(root);
            });
    }

    /// <summary>Asks before deleting a reply, naming the replies under it that go too and how many other people wrote.</summary>
    private void ConfirmDelete(ViewerAnnotation reply, string threadId)
    {
        if (_session is not { } session) return;
        var (replies, byOthers) = session.DeletedWith(reply.Id);
        string message = replies == 0 ? "Undo brings it back."
            : $"This also deletes {replies} {(replies == 1 ? "reply" : "replies")} under it" + (byOthers > 0 ? $", {byOthers} by other people" : "") + ". Undo brings them back.";
        new AlertDialog.Builder(this)
            .SetTitle("Delete this reply?")!
            .SetMessage(message)!
            .SetPositiveButton("Delete", (_, _) =>
            {
                Change(s => s.Delete(reply.Id));
                ShowThread(threadId);
            })!
            .SetNegativeButton("Cancel", (_, _) => ShowThread(threadId))!
            .Show();
    }

    /// <summary>The review statuses, the current one checked; a choice is one undo step.</summary>
    private void AskStatus(string id)
    {
        if (_session is not { } session) return;
        var states = ViewerEditSession.ReviewStates;
        int current = Math.Max(0, states.ToList().IndexOf(session.ReviewStateOf(id) ?? "None"));
        new AlertDialog.Builder(this)
            .SetTitle("Review status")!
            .SetSingleChoiceItems(states.ToArray(), current, (sender, e) =>
            {
                (sender as IDialogInterface)?.Dismiss();
                string state = states[e.Which];
                WithName(() =>
                {
                    Change(s => s.SetReviewState(id, state) != null);
                    ShowThread(id);
                });
            })!
            .SetNegativeButton("Cancel", (_, _) => ShowThread(id))!
            .Show();
    }

    // ------------------------------------------------------------------ XFDF

    /// <summary>"Export comments": the system's save dialog with "<i>name</i>.xfdf".</summary>
    private void PickXfdfExport()
    {
        if (_session is not { } session) return;
        if (session.Store.Current.Count == 0)
        {
            Toast.MakeText(this, "This document has no annotations to export.", ToastLength.Short)?.Show();
            return;
        }
        string name = System.IO.Path.GetFileNameWithoutExtension(_fileName ?? "Document");
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/vnd.adobe.xfdf");
        intent.PutExtra(Intent.ExtraTitle, (name.Length > 0 ? name : "Document") + ".xfdf");
        _exportFor = session;
        StartActivityForResult(intent, ExportXfdfRequest);
    }

    private void XfdfExportAnswered(Uri? target)
    {
        var session = _exportFor;
        _exportFor = null;
        if (target == null) return;
        if (session == null || !ReferenceEquals(session, _session))
        {
            DeleteTarget(target);
            Toast.MakeText(this, "Nothing was exported: the document closed while the save dialog was open. Export again.", ToastLength.Long)?.Show();
            return;
        }
        _ = ExportXfdfAsync(session, target);
    }

    private async Task ExportXfdfAsync(ViewerEditSession session, Uri target)
    {
        string xfdf = session.ExportXfdf(_fileName);
        int count = session.Store.Current.Count;
        try
        {
            await Task.Run(() => WriteCopy(target, System.Text.Encoding.UTF8.GetBytes(xfdf)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bool removed = DeleteTarget(target);
            Toast.MakeText(this, $"The comments could not be exported ({ex.GetType().Name}).{(removed ? " The unfinished file was removed." : "")}", ToastLength.Long)?.Show();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("Comments exported")!
            .SetMessage($"{count} {(count == 1 ? "annotation was" : "annotations were")} written as XFDF. Another PDF app can import them.")!
            .SetPositiveButton("Share", (_, _) => ShareXfdf(target))!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    /// <summary>The system share sheet with the exported file (read access for the chosen app only).</summary>
    private void ShareXfdf(Uri file)
    {
        var send = new Intent(Intent.ActionSend);
        send.SetType("application/vnd.adobe.xfdf");
        send.PutExtra(Intent.ExtraStream, file);
        send.ClipData = ClipData.NewRawUri("XFDF", file);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            StartActivity(Intent.CreateChooser(send, "Share comments"));
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            Toast.MakeText(this, "The exported file cannot be shared from here.", ToastLength.Short)?.Show();
        }
    }

    /// <summary>"Import comments": the system's document picker for an .xfdf file.</summary>
    private void PickXfdfImport()
    {
        if (!CanAnnotate)
        {
            Toast.MakeText(this, _doc?.EditRestriction ?? "This document does not allow annotations.", ToastLength.Long)?.Show();
            return;
        }
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        // Providers name .xfdf files in different ways (often application/octet-stream).
        intent.SetType("*/*");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivityForResult(intent, ImportXfdfRequest);
    }

    private async Task ImportXfdfAsync(Uri source)
    {
        var session = _session;
        if (session == null)
        {
            Toast.MakeText(this, "Open the document again, then import the comments.", ToastLength.Long)?.Show();
            return;
        }
        string xml;
        try
        {
            xml = await Task.Run(() => ReadText(source));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Toast.MakeText(this, ex is FileTooLargeException ? ex.Message : $"The file could not be read ({ex.GetType().Name}).", ToastLength.Long)?.Show();
            return;
        }
        if (!ReferenceEquals(session, _session)) return;
        ViewerXfdfImport result = default;
        Change(s =>
        {
            result = s.ImportXfdf(xml);
            if (result.Added == 0)
                Toast.MakeText(this, result.Skipped > 0 ? $"Nothing was imported: {result.Skipped} {(result.Skipped == 1 ? "annotation does" : "annotations do")} not fit this document." : "The file has no comments to import.", ToastLength.Long)?.Show();
            return result.Added > 0;
        });
        if (result.Added > 0)
            Toast.MakeText(this, $"Imported {result.Added} {(result.Added == 1 ? "annotation" : "annotations")}" + (result.Skipped > 0 ? $"; {result.Skipped} left out." : ". Undo removes them."), ToastLength.Long)?.Show();
    }

    /// <summary>The file as text (UTF-8 unless a byte order mark says otherwise), at most <see cref="MaxXfdfBytes"/>.</summary>
    private string ReadText(Uri uri)
    {
        using var input = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("No stream for the file.");
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > MaxXfdfBytes) throw new FileTooLargeException($"This file is larger than {MaxXfdfBytes / (1024 * 1024)} MB, the most read as XFDF.");
            memory.Write(buffer, 0, read);
        }
        memory.Position = 0;
        using var reader = new StreamReader(memory, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
