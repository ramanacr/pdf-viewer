using Android.App;
using Android.Widget;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// The form tools: Import form data (XFDF or FDF) and Reset form change the fields through the edit
/// session (<see cref="ViewerEditSession.SetFieldValue"/>), so they are ordinary unsaved edits, with
/// the session's rules for permissions, signatures and read-only fields; Export form data writes the
/// values as XFDF or FDF; Flatten writes a copy with the fields (and, if chosen, the comments) drawn
/// into the pages. Form scripts never run. Messages do not quote field names or values.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The largest form data file Import takes.</summary>
    private const long MaxFormDataBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The edit session when the document has fillable fields and allows filling them; otherwise
    /// the reader is told why and null is returned.
    /// </summary>
    private ViewerEditSession? FillableSession(string title)
    {
        if (!NeedDocument() || !EditsWritten()) return null;
        if (_session is not { } session)
        {
            Tell(title, "This document cannot be changed here.");
            return null;
        }
        if (!session.CanFillForms)
        {
            Tell(title, _doc?.EditRestriction ?? "This document does not allow filling in its form.");
            return null;
        }
        if (session.Widgets.Count == 0)
        {
            Tell(title, "This document has no form fields to fill.");
            return null;
        }
        return session;
    }

    /// <summary>Counts the form fields that hold values (not buttons or signatures) on a worker thread; null when it failed.</summary>
    private Task<FormCount?> CountFormFieldsAsync(byte[] bytes) => RunBusyAsync("Reading the form…", _ =>
    {
        using var engine = OpenForTool(bytes);
        var form = PdfAcroForm.Read(engine);
        int fields = form?.Fields.Count(f => f.Kind is not (PdfFormFieldKind.PushButton or PdfFormFieldKind.Signature)) ?? 0;
        return new FormCount(fields, form?.Fields.Count ?? 0);
    });

    private sealed record FormCount(int ValueFields, int AllFields);

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Import form data: an XFDF or FDF file's values go into the matching fields as unsaved edits;
    /// then how many were filled, and how many names the form does not have, is told.
    /// </summary>
    private void ImportFormData()
    {
        if (FillableSession("Import form data") is not { } session) return;
        var doc = _doc;
        PickFiles(["application/vnd.adobe.xfdf", "application/vnd.fdf", "application/xml", "text/xml", "*/*"], false, async uris =>
        {
            var values = await RunBusyAsync("Reading the form data…", _ =>
            {
                byte[] data = ReadUri(uris[0], MaxFormDataBytes);
                try
                {
                    return LooksLikeFdf(data) ? PdfFormService.ReadFdf(data) : PdfFormService.ReadXfdf(data);
                }
                catch (InvalidDataException)
                {
                    throw new UserMessageException("This file is not form data this app can read (XFDF or FDF).");
                }
            });
            if (values == null || !ReferenceEquals(doc, _doc) || !ReferenceEquals(session, _session)) return;
            ApplyFormData(session, values);
        });
    }

    /// <summary>FDF starts with "%FDF-"; anything else is read as XFDF.</summary>
    private static bool LooksLikeFdf(byte[] data)
    {
        int n = Math.Min(data.Length, 1024);
        return System.Text.Encoding.Latin1.GetString(data, 0, n).Contains("%FDF-", StringComparison.Ordinal);
    }

    private void ApplyFormData(ViewerEditSession session, List<(string Name, IReadOnlyList<string> Values)> values)
    {
        int filled = 0, same = 0, unknown = 0, readOnly = 0, refused = 0;
        // The last value given for a field wins, as in the engine's own import.
        var last = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, v) in values) last[name] = v;
        foreach (var (name, v) in last)
        {
            var widgets = session.Widgets.Where(w => w.Name == name).ToList();
            if (widgets.Count == 0)
            {
                unknown++;
                continue;
            }
            if (widgets[0].ReadOnly)
            {
                readOnly++;
                continue;
            }
            var (widget, taken) = ValueFor(widgets, v);
            if (session.SetFieldValue(widget, taken, out var refusal)) filled++;
            else if (refusal == null) same++;
            else refused++;
        }
        if (filled > 0) _ = WriteAsync(session);
        var lines = new List<string>
        {
            filled == 0 && same == 0 ? "No field was filled." : $"{filled} field{(filled == 1 ? "" : "s")} filled" + (same > 0 ? $", {same} already had the value." : "."),
        };
        if (unknown > 0) lines.Add($"{unknown} name{(unknown == 1 ? " in the file is not a field" : "s in the file are not fields")} of this form.");
        if (readOnly > 0) lines.Add($"{readOnly} read-only field{(readOnly == 1 ? " was" : "s were")} left as {(readOnly == 1 ? "it was" : "they were")}.");
        if (refused > 0) lines.Add($"{refused} value{(refused == 1 ? " does" : "s do")} not fit {(refused == 1 ? "its field" : "their fields")} (not one of the choices) and {(refused == 1 ? "was" : "were")} left out.");
        if (filled > 0) lines.Add("The changes are not saved yet: Save or Save a copy keeps them.");
        Tell("Import form data", string.Join("\n\n", lines));
    }

    /// <summary>
    /// The widget to set and the values to give it: a check box or radio button takes the widget
    /// whose on-state the value names (a check box also takes Yes, On, 1 or true for its only state),
    /// or Off; other fields take the values as they are.
    /// </summary>
    private static (ViewerFormWidget Widget, IReadOnlyList<string> Values) ValueFor(List<ViewerFormWidget> widgets, IReadOnlyList<string> values)
    {
        var first = widgets[0];
        if (first.Kind is not (ViewerFormKind.CheckBox or ViewerFormKind.Radio)) return (first, values);
        string value = values.Count > 0 ? values[0].Trim() : "";
        if (widgets.FirstOrDefault(w => w.OnState == value) is { } named) return (named, [value]);
        bool on = value.Length > 0 && value != "Off";
        if (first.Kind == ViewerFormKind.CheckBox && on && widgets.Select(w => w.OnState).Distinct().Count() == 1
            && value.ToLowerInvariant() is "yes" or "on" or "1" or "true" or "x")
            return (first, [first.OnState ?? "Yes"]);
        // Off, or a state the field does not have (refused by the session as not one of its states).
        return (first, [on ? value : "Off"]);
    }

    // ------------------------------------------------------------------ export

    /// <summary>Export form data: the fields' values (with unsaved edits) as XFDF or FDF, through the save dialog.</summary>
    private async void ExportFormData()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        byte[] bytes = CurrentBytes()!;
        var count = await CountFormFieldsAsync(bytes);
        if (count == null || !ReferenceEquals(doc, _doc)) return;
        if (count.ValueFields == 0)
        {
            Tell("Export form data", "This document has no form fields to export.");
            return;
        }
        string href = _fileName ?? BaseName + ".pdf";
        new AlertDialog.Builder(this)
            .SetTitle("Export form data as")!
            .SetItems(["XFDF (XML; most apps read it)", "FDF (older format, for Acrobat)"], (_, e) =>
            {
                bool xfdf = e.Which == 0;
                SaveNewFile(xfdf ? "application/vnd.adobe.xfdf" : "application/vnd.fdf", BaseName + (xfdf ? ".xfdf" : ".fdf"), "Exporting the form data…", _ =>
                {
                    using var engine = OpenForTool(bytes);
                    return xfdf ? System.Text.Encoding.UTF8.GetBytes(PdfFormService.ExportXfdf(engine, href)) : PdfFormService.ExportFdf(engine, href);
                }, _ => Toast.MakeText(this, $"Form data saved ({count.ValueFields} field{(count.ValueFields == 1 ? "" : "s")}).", ToastLength.Short)?.Show());
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ reset

    /// <summary>
    /// Reset form: after a confirmation, every field that is not read-only goes back to its default
    /// value (/DV), or empty or off when it has none, as unsaved edits.
    /// </summary>
    private void ResetForm()
    {
        if (FillableSession("Reset form") is not { } session) return;
        new AlertDialog.Builder(this)
            .SetTitle("Reset the form?")!
            .SetMessage("Every field goes back to its default value, or is emptied. Read-only fields stay as they are. Nothing is saved until you save.")!
            .SetPositiveButton("Reset", (_, _) => _ = ResetFormAsync(session))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private async Task ResetFormAsync(ViewerEditSession session)
    {
        var doc = _doc;
        byte[]? bytes = CurrentBytes();
        if (bytes == null) return;
        var defaults = await RunBusyAsync("Reading the form…", _ =>
        {
            using var engine = OpenForTool(bytes);
            return PdfAcroForm.Read(engine)?.Fields.GroupBy(f => f.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().DefaultValue, StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        });
        if (defaults == null || !ReferenceEquals(doc, _doc) || !ReferenceEquals(session, _session)) return;
        int changed = 0, kept = 0;
        foreach (var group in session.Widgets.GroupBy(w => w.Name, StringComparer.Ordinal).ToList())
        {
            var widgets = group.ToList();
            var first = widgets[0];
            if (first.ReadOnly) continue;
            string dv = defaults.GetValueOrDefault(group.Key, "");
            var widget = first;
            IReadOnlyList<string> values;
            switch (first.Kind)
            {
                case ViewerFormKind.CheckBox:
                    values = [dv.Length > 0 && dv != "Off" ? first.OnState ?? "Yes" : "Off"];
                    break;
                case ViewerFormKind.Radio:
                    var on = dv.Length > 0 ? widgets.FirstOrDefault(w => w.OnState == dv) : null;
                    widget = on ?? first;
                    values = [on != null ? dv : "Off"];
                    break;
                case ViewerFormKind.ListBox:
                    values = dv.Length > 0 ? [dv] : [];
                    break;
                default:
                    values = [dv];
                    break;
            }
            if (session.SetFieldValue(widget, values, out var refusal)) changed++;
            else if (refusal != null) kept++; // a choice without a default that cannot be emptied
        }
        if (changed > 0) _ = WriteAsync(session);
        string message = changed == 0 ? "The form already has its default values." : $"{changed} field{(changed == 1 ? "" : "s")} reset. Save to keep the change.";
        if (kept > 0) message += $" {kept} choice field{(kept == 1 ? " has" : "s have")} no default and kept {(kept == 1 ? "its" : "their")} value.";
        Toast.MakeText(this, message, ToastLength.Long)?.Show();
    }

    // ------------------------------------------------------------------ flatten

    /// <summary>
    /// Flatten: a copy with the form fields (and, if chosen, the comments) drawn into the page
    /// content, so they show everywhere and can no longer be changed. Written whole, which breaks
    /// any signature: asked first for a signed document.
    /// </summary>
    private async void FlattenDocument()
    {
        if (!NeedDocument() || !EditsWritten()) return;
        var doc = _doc!;
        byte[] bytes = CurrentBytes()!;
        var count = await CountFormFieldsAsync(bytes);
        if (count == null || !ReferenceEquals(doc, _doc)) return;
        bool hasForm = count.AllFields > 0;
        // Without an edit session the comments are not known: flattening them is offered anyway.
        bool hasComments = _session?.List().Count is not 0;
        if (!hasForm && !hasComments)
        {
            Tell("Flatten", "This document has no form fields or comments to flatten.");
            return;
        }
        var choices = new List<(string Label, bool Comments)>();
        if (hasForm && hasComments) choices.Add(("Form fields and comments", true));
        if (hasForm) choices.Add(("Form fields only", false));
        if (!hasForm) choices.Add(("Comments", true));
        void Flatten(bool comments)
        {
            void Go() => SaveNewPdf(BaseName + " (flattened).pdf", "Flattening…",
                "The flattened copy is saved. Its " + (comments ? hasForm ? "fields and comments are" : "comments are" : "fields are") +
                " now part of the pages and can no longer be changed.", token =>
                {
                    byte[] flat = bytes;
                    if (hasForm)
                    {
                        using var engine = OpenForTool(flat);
                        flat = PdfFormService.Flatten(engine, flat);
                    }
                    token.ThrowIfCancellationRequested();
                    if (comments)
                    {
                        using var engine = OpenForTool(flat);
                        var update = new PdfUpdate(engine, flat);
                        var editor = new PdfAnnotationEditor(update);
                        editor.FlattenAll();
                        editor.Finish();
                        flat = update.Commit(forceFullRewrite: true);
                    }
                    return flat;
                });
            if (!doc.IsSigned)
            {
                Go();
                return;
            }
            new AlertDialog.Builder(this)
                .SetTitle("Flatten a signed document?")!
                .SetMessage("The flattened copy is a rewrite of the whole file, so its signatures will no longer be valid. The opened file keeps them.")!
                .SetPositiveButton("Continue", (_, _) => Go())!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .Show();
        }
        new AlertDialog.Builder(this)
            .SetTitle("Flatten into a copy")!
            .SetItems(choices.Select(c => c.Label).ToArray(), (_, e) => Flatten(choices[e.Which].Comments))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }
}
