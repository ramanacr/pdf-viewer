using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Redaction;
using PdfViewer.Models;

namespace PdfViewer.ViewModels;

/// <summary>What the Apply Redactions dialog collected.</summary>
public sealed record RedactionApplyOptions(string OutputPath, string? OverlayText, bool RemoveMetadata);

public partial class MainViewModel
{
    /// <summary>Shows the Apply Redactions dialog (number of marks, default output path); null when cancelled.</summary>
    public Func<int, string, RedactionApplyOptions?>? ShowApplyRedactionsFunc { get; set; }

    /// <summary>Waiting for the user to drag an area to mark for redaction.</summary>
    [ObservableProperty]
    private bool _isMarkingRedaction;

    partial void OnIsMarkingRedactionChanged(bool value)
    {
        if (value) { ActiveAnnotationTool = null; IsPlacingSignature = false; }
        OnPropertyChanged(nameof(RedactionMarkCount));
    }

    /// <summary>Marks waiting to be applied, over all pages.</summary>
    public int RedactionMarkCount => Pages.Sum(p => p.RedactionMarks.Count);

    private string? WhyCannotRedact()
    {
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return "Open a document first.";
        if (!DocumentPermissions.CanModify) return "This document's security settings do not allow editing, so it cannot be redacted.";
        if (_docService.IsDecryptedCopy) return "This document is encrypted for specific recipients; save an unencrypted copy to redact it.";
        return null;
    }

    /// <summary>Marks the selected text, one box per word, following the glyphs.</summary>
    [RelayCommand]
    private void MarkSelectionForRedaction()
    {
        int added = 0;
        foreach (var page in Pages)
            foreach (var segment in page.SelectedSegments)
            {
                page.RedactionMarks.Add(segment.NormalizedBounds);
                added++;
            }
        if (added == 0)
        {
            ShowAlert("Select the text you want to redact first.", "Redact", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ClearSelection();
        OnPropertyChanged(nameof(RedactionMarkCount));
        StatusText = $"Marked {added} area(s) for redaction. Apply Redactions removes them in a new copy.";
    }

    /// <summary>Marks every current search hit (for example a name or an account number throughout the document).</summary>
    [RelayCommand]
    private void MarkSearchResultsForRedaction()
    {
        if (SearchMatches.Count == 0)
        {
            ShowAlert("Search for the text to redact first; every match found is marked.", "Redact", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var match in SearchMatches)
            if (match.PageNumber >= 1 && match.PageNumber <= Pages.Count)
                Pages[match.PageNumber - 1].RedactionMarks.Add(new Rect(match.X, match.Y, match.Width, match.Height));
        OnPropertyChanged(nameof(RedactionMarkCount));
        StatusText = $"Marked {SearchMatches.Count} search match(es) for redaction.";
    }

    [RelayCommand]
    private void BeginMarkRedactionArea()
    {
        if (IsMarkingRedaction) { IsMarkingRedaction = false; StatusText = "Marking cancelled."; return; }
        if (WhyCannotRedact() is { } reason)
        {
            ShowAlert(reason, "Redact", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IsMarkingRedaction = true;
        StatusText = "Drag over the area to redact (Esc stops). Marks stay until applied or cleared.";
    }

    /// <summary>An area dragged on a page, normalized to the unrotated page (top-left origin).</summary>
    public void AddRedactionMark(int pageNumber, Rect bounds)
    {
        if (pageNumber < 1 || pageNumber > Pages.Count || bounds.Width <= 0 || bounds.Height <= 0) return;
        Pages[pageNumber - 1].RedactionMarks.Add(bounds);
        OnPropertyChanged(nameof(RedactionMarkCount));
        StatusText = $"{RedactionMarkCount} area(s) marked for redaction.";
    }

    public void RemoveRedactionMark(int pageNumber, Rect bounds)
    {
        if (pageNumber < 1 || pageNumber > Pages.Count) return;
        Pages[pageNumber - 1].RedactionMarks.Remove(bounds);
        OnPropertyChanged(nameof(RedactionMarkCount));
    }

    [RelayCommand]
    private void ClearRedactionMarks()
    {
        foreach (var page in Pages) page.RedactionMarks.Clear();
        IsMarkingRedaction = false;
        OnPropertyChanged(nameof(RedactionMarkCount));
        StatusText = "Redaction marks cleared.";
    }

    /// <summary>
    /// Applies every mark: the content under them is removed (not covered) in a new file, which
    /// is then opened. The open document is never overwritten, so the unredacted original stays.
    /// </summary>
    [RelayCommand]
    public async Task ApplyRedactionsAsync()
    {
        if (WhyCannotRedact() is { } reason)
        {
            ShowAlert(reason, "Redact", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int count = RedactionMarkCount;
        if (count == 0)
        {
            ShowAlert("Mark something to redact first: select text, drag an area, or mark search results.", "Redact",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string source = _docService.CurrentFilePath;
        string suggested = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, Path.GetFileNameWithoutExtension(source) + "_redacted.pdf");
        if (ShowApplyRedactionsFunc?.Invoke(count, suggested) is not { } options) return;
        if (string.Equals(Path.GetFullPath(options.OutputPath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            ShowAlert("Save the redacted copy under a new name: the original stays as it is.", "Redact", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ApplyRedactionsToAsync(options);
    }

    /// <summary>The work of <see cref="ApplyRedactionsAsync"/> without the dialog (tests call this).</summary>
    public async Task<PdfRedactionResult?> ApplyRedactionsToAsync(RedactionApplyOptions options)
    {
        byte[] bytes = _docService.CurrentBytes!;
        StatusText = "Redacting...";
        PdfRedactionResult result;
        try
        {
            using var doc = await PdfVectorDocument.OpenAsync(bytes, _docService.CurrentFilePath, password: _openPassword);
            var areas = new List<PdfRedactionArea>();
            foreach (var page in Pages)
                foreach (var mark in page.RedactionMarks)
                    areas.Add(new PdfRedactionArea(page.PageNumber, ToUserSpace(doc, page.PageNumber, mark)));
            var redactionOptions = new PdfRedactionOptions
            {
                OverlayText = string.IsNullOrWhiteSpace(options.OverlayText) ? null : options.OverlayText,
                RemoveMetadata = options.RemoveMetadata,
                JpegDecoder = decoded => PdfEngine.Vector.Direct2D.WicJpeg.TryDecode(decoded, out _, out _),
            };
            result = await Task.Run(() => PdfRedactor.Apply(doc, areas, redactionOptions));
            string output = Path.GetFullPath(options.OutputPath);
            string temporary = Path.Combine(Path.GetDirectoryName(output) ?? string.Empty, $".{Path.GetFileName(output)}.redacting");
            await File.WriteAllBytesAsync(temporary, result.Bytes);
            File.Move(temporary, output, overwrite: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException
                                       or PdfEngine.Vector.Diagnostics.PdfSyntaxException)
        {
            StatusText = "The document was not redacted.";
            ShowAlert($"The document was not redacted:\n\n{ex.Message}\n\nThe original is unchanged.", "Redact", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        string summary = $"Removed {result.GlyphsRemoved} character(s), {result.ImagesRedacted + result.ImagesRemoved} image(s) and {result.PathsCut} drawing(s)"
                         + (result.AnnotationsRemoved > 0 ? $", and {result.AnnotationsRemoved} annotation(s)" : string.Empty) + ".";
        string warnings = result.Warnings.Count > 0 ? "\n\n" + string.Join("\n", result.Warnings.Distinct()) : string.Empty;
        await LoadDocumentAsync(options.OutputPath, _openPassword);
        StatusText = $"Redacted copy saved: {Path.GetFileName(options.OutputPath)}. {summary}";
        ShowAlert($"The redacted copy is saved and open:\n{options.OutputPath}\n\n{summary}\nThe content was removed from the file, not covered.{warnings}",
            "Redact", MessageBoxButton.OK, MessageBoxImage.Information);
        return result;
    }
}
