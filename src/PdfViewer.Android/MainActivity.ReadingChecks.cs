using System.Text;
using Android.App;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.PrintProduction;

namespace PdfViewer.Android;

/// <summary>
/// The checks of the document: the accessibility check (PDF/UA-1, the engine's Matterhorn
/// Protocol checks), Make accessible (the engine's auto-tagger, writing a tagged copy) and print
/// preflight (the engine's built-in profiles). Reports name checkpoints, rules, pages and objects,
/// never text of the pages, and can be copied or saved as text.
/// </summary>
public sealed partial class MainActivity
{
    // ------------------------------------------------------------------ accessibility check

    /// <summary>Accessibility check: PDF/UA-1 issues by category, then by checkpoint with how often, on which pages, and how to fix them.</summary>
    private async void AccessibilityCheck()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (CurrentBytes() is not { } bytes) return;
        var report = await RunBusyAsync("Checking accessibility…", token =>
        {
            using var engine = OpenForTool(bytes);
            token.ThrowIfCancellationRequested();
            return PdfUaValidator.Validate(engine, bytes);
        });
        if (report == null || !ReferenceEquals(doc, _doc)) return;
        ShowReport("Accessibility check", AccessibilityReportText(report), BaseName + "_accessibility");
    }

    internal static string AccessibilityReportText(PdfUaReport report)
    {
        var sb = new StringBuilder();
        int errors = report.Errors.Count(), warnings = report.Warnings.Count();
        var failedCategories = report.Errors.Select(i => i.Category).Distinct().Count();
        sb.AppendLine(report.IsCompliant
            ? $"No accessibility failures found{(warnings > 0 ? $"; {warnings} item(s) to review" : "")}. Reading order, colour contrast and whether alternate text is meaningful still need a person to check."
            : $"The document fails the accessibility check: {errors} issue(s) in {failedCategories} categor{(failedCategories == 1 ? "y" : "ies")}{(warnings > 0 ? $", and {warnings} item(s) to review" : "")}.");
        sb.AppendLine(report.ClaimsPdfUa ? "The document claims PDF/UA-1 conformance." : "The document does not claim PDF/UA conformance.");
        foreach (var category in Enum.GetValues<PdfUaCategory>())
        {
            var issues = report.Issues.Where(i => i.Category == category).ToList();
            if (issues.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"{CategoryName(category)} ({issues.Count})");
            foreach (var group in issues.GroupBy(i => (i.Checkpoint, i.Severity)).OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Checkpoint, StringComparer.Ordinal))
            {
                var first = group.First();
                string kind = group.Key.Severity == PdfUaSeverity.Error ? "error" : "to review";
                sb.Append($"  {group.Key.Checkpoint} ({kind}): {first.Message}");
                if (group.Count() > 1) sb.Append($" ×{group.Count()}");
                sb.AppendLine();
                var pages = group.Where(i => i.Page != null).Select(i => i.Page!.Value).Distinct().Order().ToList();
                if (pages.Count > 0) sb.AppendLine($"    {(pages.Count == 1 ? "Page" : "Pages")} {PageList(pages)}");
                else if (first.Location is { } where) sb.AppendLine($"    {where}");
                if (first.Fix.Length > 0) sb.AppendLine($"    Fix: {first.Fix}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string CategoryName(PdfUaCategory category) => category switch
    {
        PdfUaCategory.PageContent => "Page content",
        PdfUaCategory.AlternateText => "Alternate text",
        _ => category.ToString(),
    };

    /// <summary>Pages as ranges: "1–3, 5, 8".</summary>
    internal static string PageList(IReadOnlyList<int> sorted)
    {
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? $"{sorted[i]}" : j == i + 1 ? $"{sorted[i]}, {sorted[j]}" : $"{sorted[i]}–{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ make accessible

    /// <summary>
    /// Make accessible: tags the document for screen readers in a new PDF (the auto-tagger: reading
    /// order, headings, paragraphs, lists, simple tables, figures with placeholder alternate text,
    /// headers and footers as artifacts). It asks for the document language first, and whether to
    /// replace the tags of a document that has them. The result tells what was found and what still
    /// needs a person.
    /// </summary>
    private async void MakeAccessible()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (CurrentBytes() is not { } bytes) return;
        // Null when the check failed (already told).
        var read = await RunBusyAsync<(bool Tagged, string? Language)?>("Reading the document…", token =>
        {
            using var engine = OpenForTool(bytes);
            token.ThrowIfCancellationRequested();
            if (engine.Security is { } s && !s.IsOwner && (s.Permissions & 8) == 0)
                throw new UserMessageException("This document's security settings do not allow changing it, so it cannot be tagged.");
            return (PdfAutoTagger.IsTagged(engine), PdfAutoTagger.DocumentLanguage(engine));
        });
        if (read is not { } facts || !ReferenceEquals(doc, _doc)) return;

        string language = facts.Language ?? Java.Util.Locale.Default?.ToLanguageTag() ?? "en";
        if (!PdfUaValidator.IsValidLanguageTag(language)) language = "en";

        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(24), (int)Dp(8), (int)Dp(24), 0);
        var intro = new TextView(this)
        {
            Text = "A tagged copy is saved: reading order, headings, paragraphs, lists, tables and figures are marked for screen readers. The pages look the same.",
        };
        intro.SetTextColor(TextColor);
        column.AddView(intro);
        var input = new EditText(this) { Text = language, Hint = "Language, for example en or en-GB", InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions };
        input.SetSingleLine(true);
        input.ContentDescription = "Document language";
        var label = new TextView(this) { Text = "Document language" };
        label.SetTextColor(TextColor);
        label.SetPadding(0, (int)Dp(12), 0, 0);
        column.AddView(label);
        column.AddView(input);
        CheckBox? retag = null;
        if (facts.Tagged)
        {
            retag = new CheckBox(this) { Text = "Replace the tags it has (it is already tagged)" };
            retag.SetMinHeight((int)Dp(48));
            column.AddView(retag);
        }
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Make accessible")!
            .SetView(column)!
            .SetPositiveButton("Tag and save", (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        // Checked before the dialog closes, so that a mistyped language can be corrected.
        dialog.GetButton((int)global::Android.Content.DialogButtonType.Positive)!.Click += (_, _) =>
        {
            string lang = (input.Text ?? "").Trim();
            if (!PdfUaValidator.IsValidLanguageTag(lang))
            {
                input.Error = "Not a language tag. Use for example en, en-GB or de-CH.";
                return;
            }
            if (facts.Tagged && retag?.Checked != true)
            {
                Toast.MakeText(this, "The document is already tagged. Choose to replace its tags to tag it again.", ToastLength.Long)?.Show();
                return;
            }
            dialog.Dismiss();
            if (!ReferenceEquals(doc, _doc)) return;
            SaveTaggedCopy(bytes, new PdfAutoTagOptions { Language = lang, Retag = facts.Tagged });
        };
    }

    private void SaveTaggedCopy(byte[] bytes, PdfAutoTagOptions options)
    {
        PdfAutoTagResult? result = null;
        SaveNewFile("application/pdf", BaseName + " (accessible).pdf", "Tagging the document…", token =>
        {
            using var engine = OpenForTool(bytes);
            var tagged = PdfAutoTagger.Tag(engine, options, token);
            if (!tagged.Succeeded || tagged.Bytes == null) throw new UserMessageException(tagged.Message);
            result = tagged;
            return tagged.Bytes;
        }, target =>
        {
            if (result != null) OfferOpen(target, MakeAccessibleSummary(result));
        });
    }

    internal static string MakeAccessibleSummary(PdfAutoTagResult result)
    {
        var s = result.Summary;
        var sb = new StringBuilder();
        sb.AppendLine(result.Message);
        sb.AppendLine();
        sb.AppendLine("Found and tagged:");
        sb.AppendLine($"  Headings: {s.Headings}");
        sb.AppendLine($"  Paragraphs: {s.Paragraphs}");
        sb.AppendLine($"  Lists: {s.Lists} ({s.ListItems} items)");
        sb.AppendLine($"  Tables: {s.Tables} ({s.TablesWithHeaders} with a header row)");
        sb.AppendLine($"  Figures needing alternate text: {result.FiguresNeedingAlt.Count} of {s.Figures}");
        sb.AppendLine($"  Links: {s.Links}");
        if (s.OtherAnnotations > 0) sb.AppendLine($"  Other annotations and form fields: {s.OtherAnnotations}");
        sb.AppendLine($"  Headers, footers and page numbers marked as artifacts: {s.HeadersAndFooters}");
        if (s.MultiColumnPages > 0) sb.AppendLine($"  Pages read as columns: {s.MultiColumnPages}");
        if (result.Review.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Still needs a person:");
            foreach (string r in result.Review) sb.AppendLine($"  • {r}");
        }
        if (result.Remaining.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Checks the copy still fails:");
            foreach (var r in result.Remaining.OrderBy(r => r.Kind).ThenBy(r => r.Checkpoint, StringComparer.Ordinal))
            {
                string kind = r.Kind switch
                {
                    PdfAutoTagIssueKind.HumanReview => "needs a person",
                    PdfAutoTagIssueKind.OutsideTagging => "not about tags",
                    _ => "not fixed",
                };
                sb.AppendLine($"  {r.Checkpoint} ({kind}): {r.Message}{(r.Count > 1 ? $" ×{r.Count}" : "")}");
            }
        }
        sb.AppendLine();
        sb.Append("Open it now?");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ preflight

    /// <summary>Preflight: choose a built-in profile, then the engine checks fonts, images, colours, inks and page boxes; the report can be saved as text.</summary>
    private void RunPreflight()
    {
        if (!NeedDocument()) return;
        var profiles = PdfPreflightProfile.BuiltIn;
        var column = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(20), (int)Dp(8), (int)Dp(20), 0);
        var group = new RadioGroup(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        var buttons = new List<RadioButton>();
        foreach (var profile in profiles)
        {
            var radio = new RadioButton(this) { Text = profile.Name, Id = View.GenerateViewId() };
            radio.SetMinHeight((int)Dp(48));
            radio.SetTextColor(TextColor);
            group.AddView(radio);
            var description = new TextView(this) { Text = profile.Description };
            description.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
            description.SetTextColor(TextColor);
            description.Alpha = 0.75f;
            description.SetPadding((int)Dp(32), 0, 0, (int)Dp(8));
            group.AddView(description);
            buttons.Add(radio);
        }
        group.Check(buttons[0].Id);
        column.AddView(group);
        var note = new TextView(this) { Text = "Preflight checks the file; it changes nothing." };
        note.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 13);
        note.SetTextColor(TextColor);
        note.SetPadding(0, (int)Dp(4), 0, 0);
        column.AddView(note);
        var scroll = new ScrollView(this);
        scroll.AddView(column);
        new AlertDialog.Builder(this)
            .SetTitle("Preflight")!
            .SetView(scroll)!
            .SetPositiveButton("Check", (_, _) =>
            {
                int index = Math.Max(0, buttons.FindIndex(b => b.Id == group.CheckedRadioButtonId));
                _ = PreflightAsync(profiles[index]);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private async Task PreflightAsync(PdfPreflightProfile profile)
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        if (CurrentBytes() is not { } bytes) return;
        var report = await RunBusyAsync($"Checking for {profile.Name}…", token =>
        {
            using var engine = OpenForTool(bytes);
            // No plate rasterizer on Android: total ink is measured per painted colour.
            return PdfPreflight.RunAsync(engine, profile, new PdfPreflightOptions(), token).GetAwaiter().GetResult();
        });
        if (report == null || !ReferenceEquals(doc, _doc)) return;
        var sb = new StringBuilder();
        sb.AppendLine(report.Passed
            ? $"{profile.Name}: no errors{(report.Warnings > 0 ? $", {report.Warnings} warning(s)" : "")} on {report.PagesChecked} page(s)."
            : $"{profile.Name}: {report.Errors} error(s), {report.Warnings} warning(s) on {report.PagesChecked} page(s).");
        if (profile.InkLimitPercent != null)
            sb.AppendLine("Total ink is measured per painted colour on this device: images and objects printed over each other are not added up.");
        sb.AppendLine();
        sb.Append(report.ToText());
        ShowReport("Preflight", sb.ToString().TrimEnd(), BaseName + "_preflight");
    }
}
