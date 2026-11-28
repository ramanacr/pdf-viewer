using System.Globalization;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Documents;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Page tools that write a new PDF from pages of this document and others: insert the pages of
/// another PDF, extract pages, split into several files, and merge PDFs. All of them use the
/// engine's <see cref="PdfPageAssembler"/> (links, outlines and form fields of the pages taken are
/// kept); the open document is never changed, and edits made in this session are included. Organize
/// pages is in MainActivity.PagesOrganize.cs, headers, footers and watermarks in MainActivity.PagesMarks.cs.
/// <para>
/// A document's own security settings are respected: pages are not taken from a file that does not
/// allow page assembly (and, for extract and split, copying), unless it was opened with its owner
/// password. A new PDF made from an encrypted file has no password, as the engine writes it.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    // ------------------------------------------------------------------ shared

    /// <summary>A PDF the reader picked for a page tool: its bytes, the password it opened with, and what it holds.</summary>
    private sealed record PickedPdf(string Name, byte[] Bytes, string? Password, int PageCount, bool Encrypted);

    /// <summary>What reading a picked file found: the file opened, or it needs a password (<paramref name="WrongPassword"/>: the one given did not open it).</summary>
    private sealed record PdfProbe(string Name, byte[] Bytes, string? Password, int PageCount, bool Encrypted, bool NeedsPassword, bool WrongPassword);

    /// <summary>Opens PDF bytes in the engine (on a worker thread; dispose the result).</summary>
    private static PdfVectorDocument OpenPdfBytes(byte[] bytes, string name, string? password) =>
        PdfVectorDocument.Open(new PdfMemoryByteSource(bytes), name + ".pdf", password: password, certificates: []);

    /// <summary>What the reader may do with a document, as it was opened (everything for an unencrypted one).</summary>
    private static PdfDocumentPermissions PermissionsOf(PdfVectorDocument document) =>
        document.Security is { } s ? PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner) : PdfDocumentPermissions.Unencrypted;

    /// <summary>
    /// Tries to open a picked file: its page count, or that it needs a password. A file that does not
    /// allow its pages to be taken is refused with a message for the reader.
    /// </summary>
    private static PdfProbe ProbePdf(string name, byte[] bytes, string? password)
    {
        try
        {
            using var document = OpenPdfBytes(bytes, name, password);
            if (!PermissionsOf(document).CanAssemble)
                throw new UserMessageException($"The security settings of “{name}” do not allow its pages to be put in another document.");
            return new PdfProbe(name, bytes, password, document.PageCount, document.IsEncrypted, false, false);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
        {
            return new PdfProbe(name, bytes, password, 0, true, true, password != null);
        }
        catch (PdfEncryptedDocumentException)
        {
            throw new UserMessageException($"“{name}” is encrypted in a way this app cannot open (for example with a certificate).");
        }
    }

    /// <summary>
    /// Reads the picked PDFs (one working dialog), asks the password of any that has one, and gives
    /// them in the order picked. Nothing is given when one cannot be read or the reader cancels.
    /// </summary>
    private async void LoadPickedPdfs(IReadOnlyList<Uri> uris, Action<List<PickedPdf>> loaded)
    {
        var names = uris.Select(u => System.IO.Path.GetFileNameWithoutExtension(DisplayName(u) ?? "") is { Length: > 0 } n ? SafeFileName(n) : "Document").ToList();
        var probes = await RunBusyAsync(uris.Count == 1 ? "Reading the PDF…" : $"Reading {uris.Count} PDFs…", token =>
        {
            var list = new List<PdfProbe>();
            for (int i = 0; i < uris.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                list.Add(ProbePdf(names[i], ReadUri(uris[i], Options.MaxFileBytes), null));
            }
            return list;
        });
        if (probes == null) return;
        var picked = new List<PickedPdf>();
        foreach (var first in probes)
        {
            var probe = first;
            while (probe.NeedsPassword)
            {
                string? password = await AskPasswordAsync(probe.Name, probe.WrongPassword);
                if (password == null) return;
                var current = probe;
                probe = await RunBusyAsync("Opening…", _ => ProbePdf(current.Name, current.Bytes, password));
                if (probe == null) return;
            }
            picked.Add(new PickedPdf(probe.Name, probe.Bytes, probe.Password, probe.PageCount, probe.Encrypted));
        }
        if (picked.Count > 0) loaded(picked);
    }

    /// <summary>Asks the password of a picked file; null when the reader cancels. The password stays in memory only.</summary>
    private Task<string?> AskPasswordAsync(string name, bool wrong)
    {
        var answer = new TaskCompletionSource<string?>();
        var input = new EditText(this) { Hint = "Password", InputType = InputTypes.ClassText | InputTypes.TextVariationPassword };
        input.SetSingleLine(true);
        var frame = new FrameLayout(this);
        frame.SetPadding((int)Dp(20), (int)Dp(8), (int)Dp(20), 0);
        frame.AddView(input);
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Password")!
            .SetMessage(wrong ? $"That password does not open “{name}”. Try again." : $"“{name}” is protected by a password.")!
            .SetView(frame)!
            .SetPositiveButton("Open", (_, _) => answer.TrySetResult(input.Text ?? ""))!
            .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(null))!
            .Create()!;
        dialog.CancelEvent += (_, _) => answer.TrySetResult(null);
        dialog.Show();
        input.RequestFocus();
        return answer.Task;
    }

    /// <summary>
    /// Checks the open document's security settings before a tool takes its pages; an unencrypted
    /// document allows everything, so it is only opened again when it is encrypted.
    /// </summary>
    private async Task<bool> DocumentAllowsAsync(Func<PdfDocumentPermissions, bool> test, string refusal)
    {
        if (_doc is not { IsComplete: true } doc || CurrentBytes() is not { } bytes) return false;
        if (!doc.IsEncrypted) return true;
        bool? allowed = await RunBusyAsync<bool?>("Checking the document…", _ =>
        {
            using var engine = OpenForTool(bytes);
            return test(PermissionsOf(engine));
        });
        if (allowed == false) ShowFailure(new UserMessageException(refusal));
        return allowed == true && ReferenceEquals(doc, _doc);
    }

    /// <summary>A form dialog that stays open when <paramref name="confirmed"/> refuses the values (it tells why).</summary>
    private AlertDialog ShowFormDialog(string title, View form, string confirm, Func<bool> confirmed, Action? cancelled = null)
    {
        var scroll = new ScrollView(this);
        scroll.AddView(form);
        var dialog = new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetView(scroll)!
            .SetPositiveButton(confirm, (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => cancelled?.Invoke())!
            .Create()!;
        dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            if (confirmed()) dialog.Dismiss();
        };
        return dialog;
    }

    /// <summary>A vertical form for <see cref="ShowFormDialog"/>, padded like a dialog's text.</summary>
    private LinearLayout FormColumn()
    {
        var form = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        form.SetPadding((int)Dp(24), (int)Dp(8), (int)Dp(24), (int)Dp(8));
        return form;
    }

    /// <summary>A line of explanation in a form.</summary>
    private TextView FormNote(LinearLayout form, string text)
    {
        var note = new TextView(this) { Text = text };
        note.SetTextColor(IsDark ? global::Android.Graphics.Color.ParseColor("#A9AEB6") : global::Android.Graphics.Color.ParseColor("#5A5F66"));
        note.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        note.SetPadding(0, (int)Dp(4), 0, (int)Dp(8));
        form.AddView(note);
        return note;
    }

    /// <summary>A labelled text field in a form.</summary>
    private EditText FormField(LinearLayout form, string label, string value, InputTypes type, string hint = "")
    {
        var caption = new TextView(this) { Text = label };
        caption.SetTextColor(TextColor);
        caption.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        caption.SetPadding(0, (int)Dp(8), 0, 0);
        form.AddView(caption);
        var field = new EditText(this) { Text = value, Hint = hint, InputType = type, ContentDescription = label };
        field.SetSingleLine(true);
        field.SetMinHeight((int)Dp(48));
        caption.LabelFor = field.Id = View.GenerateViewId();
        form.AddView(field);
        return field;
    }

    /// <summary>A whole number typed in a field, within limits; otherwise the reader is told and false is returned.</summary>
    private bool TryWhole(EditText field, string label, int min, int max, out int value)
    {
        if (int.TryParse((field.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= min && value <= max) return true;
        Toast.MakeText(this, $"{label}: type a whole number from {min} to {max}.", ToastLength.Long)?.Show();
        field.RequestFocus();
        return false;
    }

    /// <summary>A number typed in a field (either decimal separator), within limits; otherwise the reader is told and false is returned.</summary>
    private bool TryNumber(EditText field, string label, double min, double max, out double value)
    {
        string text = (field.Text ?? "").Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max) return true;
        Toast.MakeText(this, $"{label}: type a number from {min.ToString(CultureInfo.CurrentCulture)} to {max.ToString(CultureInfo.CurrentCulture)}.", ToastLength.Long)?.Show();
        field.RequestFocus();
        return false;
    }

    /// <summary>Pages as a short text for a file name: "3", "2-5" or "1-3, 7".</summary>
    private static string PagesText(IReadOnlyList<int> pages)
    {
        var parts = new List<string>();
        for (int i = 0; i < pages.Count;)
        {
            int j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1) j++;
            parts.Add(j == i ? pages[i].ToString(CultureInfo.InvariantCulture) : $"{pages[i]}-{pages[j]}");
            i = j + 1;
        }
        string text = string.Join(", ", parts);
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    // ------------------------------------------------------------------ insert

    /// <summary>
    /// Insert pages from a PDF: pick a PDF, say after which page its pages go (the page shown by
    /// default; 0 puts them first), and save the combined document as a new PDF.
    /// </summary>
    private async void InsertPagesFromPdf()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (!await DocumentAllowsAsync(p => p.CanAssemble, "This document's security settings do not allow changing its pages.")) return;
        PickFiles(["application/pdf"], false, uris => LoadPickedPdfs(uris, picked =>
        {
            if (!ReferenceEquals(doc, _doc) || CurrentBytes() is not { } bytes) return;
            var other = picked[0];
            int count = doc.PageCount;
            int current = Math.Clamp(_pages.CurrentPage, 1, count);
            string pages = other.PageCount == 1 ? "The page" : $"The {other.PageCount} pages";
            AskLine("Insert pages", $"{pages} of “{other.Name}” go after which page of this document (1 to {count})? Type 0 to put them before page 1.\n\nThe result is saved as a new PDF; this document is not changed.",
                current.ToString(CultureInfo.InvariantCulture), "Page number", InputTypes.ClassNumber, "Insert", text =>
                {
                    if (!int.TryParse(text.Trim(), out int after) || after < 0 || after > count)
                    {
                        Toast.MakeText(this, $"Type a page number from 0 to {count}.", ToastLength.Long)?.Show();
                        return;
                    }
                    string done = "The pages were inserted in a new PDF." + (doc.IsEncrypted || other.Encrypted ? " It has no password." : "");
                    SaveNewPdf($"{BaseName} with {other.Name}.pdf", "Inserting pages…", done, token =>
                    {
                        using var main = OpenForTool(bytes);
                        using var added = OpenPdfBytes(other.Bytes, other.Name, other.Password);
                        token.ThrowIfCancellationRequested();
                        var list = new List<PdfAssemblyPage>();
                        for (int p = 1; p <= after; p++) list.Add(new PdfAssemblyPage(main, p));
                        for (int p = 1; p <= added.PageCount; p++) list.Add(new PdfAssemblyPage(added, p));
                        for (int p = after + 1; p <= main.PageCount; p++) list.Add(new PdfAssemblyPage(main, p));
                        return PdfPageAssembler.Build(list, new PdfAssemblyOptions { SourceTitles = [BaseName, other.Name] });
                    });
                });
        }));
    }

    // ------------------------------------------------------------------ extract

    /// <summary>Extract pages: the pages typed ("1-3, 5"; the page shown by default), in that order, as a new PDF.</summary>
    private async void ExtractPages()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (!await DocumentAllowsAsync(p => p.CanAssemble && p.CanCopy, "This document's security settings do not allow taking pages out of it.")) return;
        int count = doc.PageCount;
        int current = Math.Clamp(_pages.CurrentPage, 1, count);
        AskLine("Extract pages", $"Which pages (1 to {count})? For example 1-3, 5. They are saved as a new PDF, in the order typed.",
            current.ToString(CultureInfo.InvariantCulture), "Pages, e.g. 1-3, 5", InputTypes.ClassText, "Extract", text =>
            {
                if (!ReferenceEquals(doc, _doc) || CurrentBytes() is not { } bytes) return;
                if (ParsePageRanges(text, count) is not { } pages)
                {
                    Toast.MakeText(this, $"Type pages from 1 to {count}, such as 1-3, 5.", ToastLength.Long)?.Show();
                    return;
                }
                string name = pages.Count == 1 ? $"{BaseName} page {pages[0]}.pdf" : $"{BaseName} pages {PagesText(pages)}.pdf";
                string done = (pages.Count == 1 ? "The page was" : $"{pages.Count} pages were") + " saved as a new PDF." + (doc.IsEncrypted ? " It has no password." : "");
                SaveNewPdf(name, "Extracting pages…", done, token =>
                {
                    using var engine = OpenForTool(bytes);
                    token.ThrowIfCancellationRequested();
                    return PdfPageAssembler.Extract(engine, pages);
                });
            });
    }

    // ------------------------------------------------------------------ split

    /// <summary>
    /// Split: every N pages (1 by default: a file per page) or by ranges typed ("1-3, 4-6, 7-": a file
    /// each), into a folder the reader picks, as "&lt;name&gt; part 1.pdf" and so on.
    /// </summary>
    private async void SplitDocument()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (!await DocumentAllowsAsync(p => p.CanAssemble && p.CanCopy, "This document's security settings do not allow taking pages out of it.")) return;
        int count = doc.PageCount;
        var form = FormColumn();
        FormNote(form, $"This document has {count} pages. Each part is saved as its own PDF in a folder you choose; this document is not changed.");
        var everyN = new RadioButton(this) { Text = "Every N pages", Id = View.GenerateViewId() };
        var byRanges = new RadioButton(this) { Text = "By page ranges", Id = View.GenerateViewId() };
        everyN.SetMinHeight((int)Dp(48));
        byRanges.SetMinHeight((int)Dp(48));
        form.AddView(everyN);
        var size = FormField(form, "Pages in each part", "1", InputTypes.ClassNumber);
        var rangesForm = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        rangesForm.AddView(byRanges);
        var ranges = FormField(rangesForm, "Ranges, a part each (e.g. 1-3, 4-6, 7-)", "", InputTypes.ClassText, "1-3, 4-6, 7-");
        form.AddView(rangesForm);
        // The two buttons sit in different layouts (each above its field), so they are kept exclusive here.
        everyN.Checked = true;
        everyN.CheckedChange += (_, e) => { if (e.IsChecked) byRanges.Checked = false; };
        byRanges.CheckedChange += (_, e) => { if (e.IsChecked) everyN.Checked = false; };
        size.FocusChange += (_, e) => { if (e.HasFocus) everyN.Checked = true; };
        ranges.FocusChange += (_, e) => { if (e.HasFocus) byRanges.Checked = true; };

        ShowFormDialog("Split", form, "Choose folder", () =>
        {
            var parts = new List<List<int>>();
            if (everyN.Checked)
            {
                if (!TryWhole(size, "Pages in each part", 1, count, out int n)) return false;
                for (int start = 1; start <= count; start += n)
                    parts.Add(Enumerable.Range(start, Math.Min(n, count - start + 1)).ToList());
            }
            else
            {
                foreach (string item in (ranges.Text ?? "").Split(',', ';'))
                {
                    if (item.Trim().Length == 0) continue;
                    if (ParsePageRanges(item, count) is not { } pages)
                    {
                        Toast.MakeText(this, $"Type ranges of pages from 1 to {count}, such as 1-3, 4-6, 7-.", ToastLength.Long)?.Show();
                        ranges.RequestFocus();
                        return false;
                    }
                    parts.Add(pages);
                }
                if (parts.Count == 0)
                {
                    Toast.MakeText(this, "Type the ranges, a part each, such as 1-3, 4-6, 7-.", ToastLength.Long)?.Show();
                    ranges.RequestFocus();
                    return false;
                }
            }
            PickFolder(tree => _ = WriteSplitAsync(doc, tree, parts));
            return true;
        });
    }

    /// <summary>Writes the parts into the folder; on a failure or a cancel, the parts already written are removed.</summary>
    private async Task WriteSplitAsync(ViewerDocument doc, Uri tree, List<List<int>> parts)
    {
        if (!ReferenceEquals(doc, _doc) || CurrentBytes() is not { } bytes) return;
        string baseName = BaseName;
        var written = new List<Uri>();
        int? count = await RunBusyAsync<int?>($"Saving {parts.Count} PDFs…", token =>
        {
            using var engine = OpenForTool(bytes);
            for (int i = 0; i < parts.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                byte[] part = PdfPageAssembler.Extract(engine, parts[i]);
                token.ThrowIfCancellationRequested();
                var target = CreateInFolder(tree, SafeFileName($"{baseName} part {i + 1}.pdf"), "application/pdf")
                             ?? throw new UserMessageException("The folder did not accept a new file. Choose another folder.");
                lock (written) written.Add(target);
                WriteUri(target, part);
            }
            return parts.Count;
        }, onFailure: () =>
        {
            lock (written)
                foreach (var target in written) DeleteTarget(target);
        });
        if (count is not { } n) return;
        new AlertDialog.Builder(this)
            .SetTitle("Split")!
            .SetMessage($"Saved {n} PDF{(n == 1 ? "" : "s")} in the folder you chose, from “{SafeFileName(baseName)} part 1.pdf”." + (doc.IsEncrypted ? " They have no password." : ""))!
            .SetPositiveButton("OK", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ merge

    /// <summary>An entry of the merge list: a picked PDF, or the open document (<see cref="Picked"/> null).</summary>
    private sealed record MergeItem(string Name, int PageCount, PickedPdf? Picked);

    /// <summary>
    /// Merge PDFs (no document needed): pick several PDFs, put them in order (the open document can
    /// be one of them, first by default), and save them as one new PDF with a bookmark for each.
    /// </summary>
    private void MergePdfs()
    {
        var items = new List<MergeItem>();
        if (_doc is { IsComplete: true } doc) items.Add(new MergeItem(BaseName + " (open document)", doc.PageCount, null));
        PickFiles(["application/pdf"], true, uris => LoadPickedPdfs(uris, picked =>
        {
            items.AddRange(picked.Select(p => new MergeItem(p.Name, p.PageCount, p)));
            ShowMergeList(items);
        }));
    }

    /// <summary>The merge list: move up, move down, remove, add more; Merge saves the new PDF.</summary>
    private void ShowMergeList(List<MergeItem> items)
    {
        var doc = _doc;
        var form = FormColumn();
        FormNote(form, "The PDFs are joined in this order into a new PDF. Use the arrows to change the order.");
        var rows = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        form.AddView(rows);
        void Fill()
        {
            rows.RemoveAllViews();
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var item = items[i];
                var row = Bar();
                row.SetPadding(0, 0, 0, 0);
                var label = new TextView(this) { Text = $"{i + 1}. {item.Name}\n{item.PageCount} page{(item.PageCount == 1 ? "" : "s")}" };
                label.SetTextColor(TextColor);
                label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
                label.SetMaxLines(3);
                label.Ellipsize = TextUtils.TruncateAt.End;
                row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                var up = ToolButton("↑", $"Move {item.Name} up", (_, _) => { (items[index - 1], items[index]) = (items[index], items[index - 1]); Fill(); });
                var down = ToolButton("↓", $"Move {item.Name} down", (_, _) => { (items[index + 1], items[index]) = (items[index], items[index + 1]); Fill(); });
                var remove = ToolButton("✕", $"Remove {item.Name} from the list", (_, _) => { items.RemoveAt(index); Fill(); });
                foreach (var b in new[] { up, down, remove }) b.SetTextColor(AccentColor);
                up.Enabled = index > 0;
                up.Alpha = index > 0 ? 1f : 0.3f;
                down.Enabled = index < items.Count - 1;
                down.Alpha = index < items.Count - 1 ? 1f : 0.3f;
                row.AddView(up);
                row.AddView(down);
                row.AddView(remove);
                rows.AddView(row);
            }
            if (items.Count == 0) FormNoteInto(rows, "No PDFs in the list. Add some.");
        }
        Fill();
        AlertDialog? dialog = null;
        var add = new Button(this) { Text = "Add PDFs…", ContentDescription = "Add more PDFs to the list" };
        add.SetAllCaps(false);
        add.SetMinHeight((int)Dp(48));
        add.Click += (_, _) => PickFiles(["application/pdf"], true, uris => LoadPickedPdfs(uris, picked =>
        {
            items.AddRange(picked.Select(p => new MergeItem(p.Name, p.PageCount, p)));
            Fill();
            if (dialog is { IsShowing: false }) ShowMergeList(items);
        }));
        form.AddView(add, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = (int)Dp(8) });
        dialog = ShowFormDialog("Merge PDFs", form, "Merge", () =>
        {
            if (items.Count < 2)
            {
                Toast.MakeText(this, "Choose at least two PDFs to merge.", ToastLength.Short)?.Show();
                return false;
            }
            bool withOpen = items.Any(i => i.Picked == null);
            if (withOpen && (doc == null || !ReferenceEquals(doc, _doc)))
            {
                Toast.MakeText(this, "The open document changed. Remove it from the list.", ToastLength.Long)?.Show();
                return false;
            }
            byte[]? current = withOpen ? CurrentBytes() : null;
            var list = items.ToList();
            bool encrypted = list.Any(i => i.Picked?.Encrypted ?? doc?.IsEncrypted ?? false);
            SaveNewPdf("Merged.pdf", "Merging…", $"{list.Count} PDFs were merged into a new PDF." + (encrypted ? " It has no password." : ""), token =>
            {
                var sources = new List<PdfVectorDocument>();
                try
                {
                    foreach (var item in list)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Picked is { } p)
                        {
                            sources.Add(OpenPdfBytes(p.Bytes, p.Name, p.Password));
                        }
                        else
                        {
                            var engine = OpenForTool(current!);
                            sources.Add(engine);
                            if (!PermissionsOf(engine).CanAssemble)
                                throw new UserMessageException("The open document's security settings do not allow its pages to be put in another document.");
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    var titles = list.Select(i => (string?)(i.Picked?.Name ?? BaseNameOf(i))).ToList();
                    return PdfPageAssembler.Merge(sources, new PdfAssemblyOptions { SourceTitles = titles });
                }
                finally
                {
                    foreach (var source in sources) source.Dispose();
                }
            });
            return true;
        });

        static string BaseNameOf(MergeItem item) => item.Name.EndsWith(" (open document)", StringComparison.Ordinal) ? item.Name[..^" (open document)".Length] : item.Name;
    }

    /// <summary>A line of explanation added to any layout.</summary>
    private void FormNoteInto(LinearLayout layout, string text)
    {
        var note = new TextView(this) { Text = text };
        note.SetTextColor(TextColor);
        note.SetPadding(0, (int)Dp(8), 0, (int)Dp(8));
        layout.AddView(note);
    }
}
