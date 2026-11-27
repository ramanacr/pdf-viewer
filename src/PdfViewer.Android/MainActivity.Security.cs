using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Safety;
using PdfEngine.Vector.Security;
using PdfEngine.Vector.Services;

namespace PdfViewer.Android;

/// <summary>
/// Protect with a password (AES-256, with permissions), Remove security, Document safety, Save a
/// clean copy and Embedded files. Each writes a new file through the save dialog; the opened file is
/// not changed. Security is only changed with the document's permissions password (asked for when
/// the document was not opened with it), as the engine requires. Passwords live in memory for the
/// one operation and are never stored or logged. Embedded files are only ever saved, never opened.
/// Signing and verifying are in MainActivity.SecuritySigning.cs, the form tools in MainActivity.Forms.cs.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>What a security change needs to know first: whether it is encrypted, whether its owner password is known, its signatures.</summary>
    private sealed record SecurityState(bool Encrypted, bool OwnerKnown, bool PasswordSecurity, int Signatures);

    /// <summary>Reads the document's security on a worker thread; null when it failed (the failure was shown).</summary>
    private Task<SecurityState?> ReadSecurityAsync(byte[] bytes)
    {
        string? password = _password;
        return RunBusyAsync("Reading the document's security…", _ =>
        {
            using var doc = OpenForTool(bytes);
            return new SecurityState(doc.IsEncrypted, PdfEncryptor.IsOwnerKnown(doc, password),
                doc.Security is PdfStandardSecurityHandler, PdfEncryptor.CountSignatures(doc));
        });
    }

    /// <summary>
    /// Makes sure the document's security may be changed: at once when the owner password is known,
    /// else after the reader types it (checked against the document). <paramref name="then"/> gets
    /// the owner password to use (null when the document was opened with full rights).
    /// </summary>
    private void WithOwnerPassword(byte[] bytes, SecurityState state, string title, Action<string?> then)
    {
        if (state.OwnerKnown)
        {
            then(_password);
            return;
        }
        if (!state.PasswordSecurity)
        {
            Tell(title, "This document is encrypted for specific recipients (certificate security); its security cannot be changed here.");
            return;
        }
        var doc = _doc;
        AskLine(title, "Changing this document's security needs its permissions password (owner password).", "", "Permissions password",
            InputTypes.ClassText | InputTypes.TextVariationPassword, "Continue", async typed =>
            {
                if (typed.Length == 0) return;
                bool proven = await RunBusyAsync("Checking the password…", _ =>
                {
                    using var engine = OpenForTool(bytes);
                    return PdfEncryptor.IsOwnerKnown(engine, typed);
                });
                if (!ReferenceEquals(doc, _doc)) return;
                if (proven) then(typed);
                else Toast.MakeText(this, "The permissions password is not correct.", ToastLength.Long)?.Show();
            });
    }

    /// <summary>A rewrite of a signed document breaks its signatures: asked first, when it has any.</summary>
    private void ConfirmSignaturesLost(int signatures, string adjective, string title, Action then)
    {
        if (signatures == 0)
        {
            then();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetMessage($"This document has {signatures} digital signature{(signatures == 1 ? "" : "s")}. The {adjective} copy is a rewrite of the whole file, " +
                        "so its signatures will no longer be valid. The opened file keeps them.")!
            .SetPositiveButton("Continue", (_, _) => then())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>A message with OK.</summary>
    private void Tell(string title, string message) =>
        new AlertDialog.Builder(this).SetTitle(title)!.SetMessage(message)!.SetPositiveButton("OK", (_, _) => { })!.Show();

    /// <summary>The document's edits are written into its bytes; otherwise the reader is asked to wait a moment.</summary>
    private bool EditsWritten()
    {
        if (_session is not { IsCurrent: false }) return true;
        Toast.MakeText(this, "The last change is still being written. Try again in a moment.", ToastLength.Short)?.Show();
        return false;
    }

    // ------------------------------------------------------------------ protect with password

    /// <summary>
    /// Protect with password: an open password (typed twice), and optionally a permissions password
    /// with what readers may do; a copy encrypted with AES-256 is saved. Unsaved edits are in it.
    /// </summary>
    private async void ProtectWithPassword()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        byte[] bytes = CurrentBytes()!;
        var state = await ReadSecurityAsync(bytes);
        if (state == null || !ReferenceEquals(doc, _doc)) return;
        WithOwnerPassword(bytes, state, "Protect with password", owner =>
            ConfirmSignaturesLost(state.Signatures, "protected", "Protect with password", () => ShowProtectDialog(bytes, owner)));
    }

    private void ShowProtectDialog(byte[] bytes, string? currentOwner)
    {
        var column = DialogColumn();
        column.AddView(DialogNote("The copy opens only with the open password. A forgotten password cannot be recovered."));
        var open = PasswordInput("Open password");
        var openAgain = PasswordInput("Open password again");
        column.AddView(open);
        column.AddView(openAgain);
        var restrict = new CheckBox(this) { Text = "Restrict what readers may do", Checked = false };
        restrict.SetMinHeight((int)Dp(48));
        column.AddView(restrict);
        var restrictions = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical, Visibility = ViewStates.Gone };
        restrictions.AddView(DialogNote("The permissions password lifts these limits and allows changing the security. It must differ from the open password."));
        var owner = PasswordInput("Permissions password");
        var ownerAgain = PasswordInput("Permissions password again");
        restrictions.AddView(owner);
        restrictions.AddView(ownerAgain);
        var print = DialogCheck("Allow printing");
        var copy = DialogCheck("Allow copying text and images");
        var edit = DialogCheck("Allow changing the document and its pages");
        var fill = DialogCheck("Allow filling in forms");
        var annotate = DialogCheck("Allow comments");
        foreach (var check in new[] { print, copy, edit, fill, annotate }) restrictions.AddView(check);
        column.AddView(restrictions);
        restrict.CheckedChange += (_, e) => restrictions.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;

        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Protect with password")!
            .SetView(DialogScroll(column))!
            .SetPositiveButton("Save protected copy", (_, _) => { })! // replaced below, so a refused entry keeps the dialog open
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        // The dialog stays open while what was typed is not usable, with the reason under the field.
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            string user = open.Text ?? "", userAgain = openAgain.Text ?? "";
            bool limited = restrict.Checked;
            string ownerPassword = limited ? owner.Text ?? "" : "", ownerPasswordAgain = limited ? ownerAgain.Text ?? "" : "";
            if (user != userAgain)
            {
                openAgain.Error = "The passwords are not the same.";
                return;
            }
            if (ownerPassword != ownerPasswordAgain)
            {
                ownerAgain.Error = "The passwords are not the same.";
                return;
            }
            if (user.Length == 0 && ownerPassword.Length == 0)
            {
                open.Error = limited ? "Type an open password, a permissions password, or both." : "Type an open password.";
                return;
            }
            if (limited && ownerPassword.Length == 0)
            {
                owner.Error = "Restricting readers needs a permissions password.";
                return;
            }
            if (ownerPassword.Length > 0 && ownerPassword == user)
            {
                owner.Error = "Use a password that differs from the open password.";
                return;
            }
            var permissions = !limited ? PdfPermissionSet.All : new PdfPermissionSet
            {
                Printing = print.Checked ? PdfPrintPermission.HighResolution : PdfPrintPermission.None,
                CopyContent = copy.Checked,
                CopyForAccessibility = true, // screen readers keep working
                ChangeDocument = edit.Checked,
                AssemblePages = edit.Checked,
                FillForms = fill.Checked || annotate.Checked, // the comment permission also allows filling forms
                Comment = annotate.Checked,
            };
            var options = new PdfEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = ownerPassword,
                Permissions = permissions,
                Strength = PdfEncryptionStrength.Aes256,
                CurrentOwnerPassword = currentOwner,
                AllowInvalidatingSignatures = true, // confirmed by ConfirmSignaturesLost
            };
            dialog.Dismiss();
            string done = user.Length > 0
                ? "The protected copy is saved. It opens only with its open password."
                : "The protected copy is saved. Anyone can open it; its restrictions need the permissions password to lift.";
            SaveNewPdf(BaseName + " (protected).pdf", "Encrypting…", done, _ =>
            {
                using var engine = OpenForTool(bytes);
                return PdfEncryptor.Encrypt(engine, options).Bytes;
            });
        };
    }

    // ------------------------------------------------------------------ remove security

    /// <summary>Remove security: a copy without password and restrictions, made with the owner password.</summary>
    private async void RemoveSecurity()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        if (!doc.IsEncrypted)
        {
            Tell("Remove security", "This document has no password or restrictions: there is no security to remove.");
            return;
        }
        byte[] bytes = CurrentBytes()!;
        var state = await ReadSecurityAsync(bytes);
        if (state == null || !ReferenceEquals(doc, _doc)) return;
        WithOwnerPassword(bytes, state, "Remove security", owner =>
            new AlertDialog.Builder(this)
                .SetTitle("Remove security?")!
                .SetMessage("A copy without the password and restrictions is saved. Anyone with the copy can open, print, copy and change it. The opened file stays protected." +
                            (state.Signatures > 0 ? $"\n\nThe copy is a rewrite of the whole file: its {state.Signatures} signature{(state.Signatures == 1 ? "" : "s")} will no longer be valid." : ""))!
                .SetPositiveButton("Save unprotected copy", (_, _) =>
                    SaveNewPdf(BaseName + " (unprotected).pdf", "Removing security…", "The copy without security is saved.", _ =>
                    {
                        using var engine = OpenForTool(bytes);
                        return PdfEncryptor.RemoveSecurity(engine, owner, allowInvalidatingSignatures: true).Bytes;
                    }))!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .Show());
    }

    // ------------------------------------------------------------------ document safety

    /// <summary>
    /// Document safety: the scripts, launch actions, embedded files and links the document carries
    /// (none of which this app runs or opens), with Save clean copy.
    /// </summary>
    private async void ShowDocumentSafety()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        byte[] bytes = CurrentBytes()!;
        var report = await RunBusyAsync("Checking the document…", token =>
        {
            using var engine = OpenForTool(bytes);
            return PdfSafetyInspector.Inspect(engine, token);
        });
        if (report == null || !ReferenceEquals(doc, _doc)) return;
        var lines = new List<string>();
        if (report.Findings.Count == 0)
            lines.Add("Nothing active was found: no scripts, launch actions, embedded files or links to other places.");
        else if (report.IsClean)
            lines.Add("Nothing that can act on this device was found. For information:");
        else
            lines.Add("This document carries content that could act on a device in other apps:");
        foreach (var f in report.Findings.OrderByDescending(f => f.Severity))
        {
            lines.Add("");
            lines.Add((f.Severity == RiskSeverity.Elevated ? "⚠ " : "• ") + f.Description);
            foreach (string detail in f.Details.Take(5)) lines.Add("    " + detail);
            if (f.Details.Count > 5) lines.Add($"    (and {f.Details.Count - 5} more)");
        }
        if (report.InspectionWasLimited)
        {
            lines.Add("");
            lines.Add("The check could not cover the whole document, so this is not a guarantee." +
                      (report.LimitationReason.Length > 0 ? " " + report.LimitationReason : ""));
        }
        lines.Add("");
        lines.Add("This app never runs scripts or launch actions and never opens embedded files.");
        string text = string.Join("\n", lines);
        bool active = report.Findings.Any(f => f.Kind is DocumentRiskKind.JavaScript or DocumentRiskKind.LaunchAction or DocumentRiskKind.EmbeddedFile);
        var body = new TextView(this) { Text = text };
        body.SetTextIsSelectable(true);
        body.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        body.SetPadding((int)Dp(24), (int)Dp(12), (int)Dp(24), (int)Dp(12));
        var builder = new AlertDialog.Builder(this)
            .SetTitle(report.IsClean ? "Document safety" : "Document safety: active content")!
            .SetView(DialogScroll(body))!
            .SetPositiveButton("Close", (_, _) => { })!;
        if (active) builder.SetNeutralButton("Save clean copy", (_, _) => SaveCleanCopy());
        if (report.HasKind(DocumentRiskKind.EmbeddedFile)) builder.SetNegativeButton("Embedded files", (_, _) => ShowEmbeddedFiles());
        builder.Show();
    }

    // ------------------------------------------------------------------ clean copy

    /// <summary>
    /// Save clean copy: a copy without scripts, launch actions, embedded files, media and triggered
    /// actions, checked again before it is written; then what was removed (and lost) is told.
    /// </summary>
    private void SaveCleanCopy()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        byte[] bytes = CurrentBytes()!;
        SanitizationResult? result = null;
        SaveNewFile("application/pdf", BaseName + " (clean).pdf", "Removing active content…", token =>
        {
            byte[] clean;
            using (var engine = OpenForTool(bytes))
                (clean, result) = PdfSanitizer.Sanitize(engine, token);
            token.ThrowIfCancellationRequested();
            // A "clean" copy that is not clean is worse than none: it is checked before it is written.
            using (var check = OpenForTool(clean))
                if (!PdfSafetyInspector.Inspect(check, token).IsClean)
                    throw new UserMessageException("The copy still had active content after cleaning, so it was not saved.");
            return clean;
        }, target =>
        {
            var lines = new List<string>();
            if (result is not { RemovedAnything: true })
                lines.Add("The document had no active content to remove; the copy is saved anyway.");
            else
                lines.AddRange(result.Changes.Select(c => "• " + c.Description));
            if (result != null && result.SideEffects.Count > 0)
            {
                lines.Add("");
                lines.AddRange(result.SideEffects.Select(s => "• " + s));
            }
            OfferOpen(target, "The clean copy is saved.\n\n" + string.Join("\n", lines));
        });
    }

    // ------------------------------------------------------------------ embedded files

    /// <summary>
    /// Embedded files: the document's attachments (in its file list and on its pages); one tapped is
    /// saved with the save dialog, never opened. A program or script file is warned about first.
    /// </summary>
    private async void ShowEmbeddedFiles()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        byte[] bytes = CurrentBytes()!;
        var files = await RunBusyAsync("Reading the embedded files…", _ =>
        {
            using var engine = OpenForTool(bytes);
            return PdfAttachments.List(engine);
        });
        if (files == null || !ReferenceEquals(doc, _doc)) return;
        if (files.Count == 0)
        {
            Tell("Embedded files", "This document has no embedded files.");
            return;
        }
        var rows = files.Select(f =>
        {
            string size = f.Size >= 0 ? FormatBytes(f.Size) : "size unknown";
            string where = f.PageNumber > 0 ? $" · on page {f.PageNumber}" : "";
            string warn = PdfAttachmentService.IsExecutableName(f.Name) ? "⚠ " : "";
            return $"{warn}{(f.Name.Length > 0 ? f.Name : "Unnamed file")}\n{size}{where}";
        }).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle($"Embedded files ({files.Count})")!
            .SetItems(rows, (_, e) => SaveAttachment(bytes, files[e.Which]))!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
        Toast.MakeText(this, "Tap a file to save it. Files are never opened here.", ToastLength.Short)?.Show();
    }

    /// <summary>Saves one attachment through the save dialog; a program or script file is warned about first.</summary>
    private void SaveAttachment(byte[] bytes, PdfAttachment file)
    {
        bool executable = PdfAttachmentService.IsExecutableName(file.Name);
        string name = file.Name.Length > 0 ? System.IO.Path.GetFileName(file.Name.Replace('\\', '/')) : "Attachment";
        // A program is saved as plain bytes, so no app offers to run it from the save location.
        string mime = !executable && file.MimeType is { } m && m.Contains('/') && !m.Contains(' ') ? m : "application/octet-stream";
        void Save() => SaveNewFile(mime, name, "Saving the file…", _ =>
        {
            using var engine = OpenForTool(bytes);
            return PdfAttachments.Extract(engine, file.Index);
        }, _ => Toast.MakeText(this, "File saved. It was not opened.", ToastLength.Short)?.Show());
        if (!executable)
        {
            Save();
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle("This is a program or script")!
            .SetMessage("Programs hidden in documents are a common way to spread malware. Save it only if you trust where the document came from, and do not run it unless you are sure.")!
            .SetPositiveButton("Save anyway", (_, _) => Save())!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ dialog parts

    /// <summary>The column of a form dialog, padded as the platform's dialogs are.</summary>
    private LinearLayout DialogColumn()
    {
        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(24), (int)Dp(8), (int)Dp(24), (int)Dp(8));
        return column;
    }

    /// <summary>A dialog's content in a scroll view, so it fits a small screen with the keyboard up.</summary>
    private ScrollView DialogScroll(View content)
    {
        var scroll = new ScrollView(this);
        scroll.AddView(content);
        return scroll;
    }

    /// <summary>A line of explanation in a form dialog.</summary>
    private TextView DialogNote(string text)
    {
        var note = new TextView(this) { Text = text };
        note.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        note.SetPadding(0, (int)Dp(8), 0, (int)Dp(4));
        return note;
    }

    /// <summary>A checked box at a 48 dp touch target.</summary>
    private CheckBox DialogCheck(string text)
    {
        var check = new CheckBox(this) { Text = text, Checked = true };
        check.SetMinHeight((int)Dp(48));
        return check;
    }

    /// <summary>A hidden password field (no suggestions, so the keyboard does not learn it).</summary>
    private EditText PasswordInput(string hint)
    {
        var input = new EditText(this) { Hint = hint, InputType = InputTypes.ClassText | InputTypes.TextVariationPassword };
        input.SetSingleLine(true);
        input.SetMinHeight((int)Dp(48));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) input.ImportantForAutofill = ImportantForAutofill.No;
        return input;
    }
}
