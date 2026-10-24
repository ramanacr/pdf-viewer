using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Document;

namespace PdfViewer.ViewModels;

/// <summary>A checkpoint the tagged copy still fails, as the dialog lists it.</summary>
public sealed record MakeAccessibleIssueItem(string Checkpoint, string Message, int Count, string Kind)
{
    public string CountText => Count > 1 ? $"{Count} times" : string.Empty;

    /// <summary>All of it in one sentence, for screen readers.</summary>
    public string AutomationName => $"{Checkpoint}, {Kind}: {Message}{(Count > 1 ? $", {Count} times" : string.Empty)}";
}

/// <summary>
/// Tools > Accessibility > Make Accessible: tags the open document for assistive technology and
/// saves the tagged copy under a new name (never over the original). The language and whether to
/// replace existing tags are chosen first; afterwards the dialog lists what was tagged, what a
/// person should still review and which PDF/UA checkpoints still fail, and offers to open the copy
/// and run the accessibility check on it.
/// </summary>
public sealed partial class MakeAccessibleViewModel : ObservableObject
{
    private readonly byte[] _bytes;
    private readonly string _path;
    private readonly string? _password;

    public MakeAccessibleViewModel(byte[] bytes, string path, string? password)
    {
        _bytes = bytes;
        _path = path;
        _password = password;
    }

    public string FileName => Path.GetFileName(_path);

    public string SuggestedOutputPath =>
        Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, Path.GetFileNameWithoutExtension(_path) + "_accessible.pdf");

    /// <summary>The document language for the tagged copy (a language tag such as en, en-GB or de-CH).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageError))]
    [NotifyPropertyChangedFor(nameof(CanTag))]
    private string _language = "en";

    /// <summary>Why the language cannot be used, or empty.</summary>
    public string LanguageError => PdfUaValidator.IsValidLanguageTag(Language.Trim())
        ? string.Empty
        : "Enter a language tag such as en, en-GB, fr or de-CH.";

    /// <summary>The document already has tags; they are replaced only when <see cref="Retag"/> is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaggedText))]
    [NotifyPropertyChangedFor(nameof(CanTag))]
    private bool _isTagged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTag))]
    private bool _retag;

    public string TaggedText => IsTagged
        ? "The document is already tagged. Choose to replace its tags to tag it again."
        : "The document is not tagged.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTag))]
    private bool _isBusy;

    public bool CanTag => !IsBusy && LanguageError.Length == 0 && (!IsTagged || Retag);

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>What was tagged: "Headings: 4" and so on.</summary>
    public ObservableCollection<string> Found { get; } = new();

    /// <summary>What a person should still review.</summary>
    public ObservableCollection<string> Review { get; } = new();

    /// <summary>The checkpoints the tagged copy still fails.</summary>
    public ObservableCollection<MakeAccessibleIssueItem> Remaining { get; } = new();

    /// <summary>The tagged copy, once saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _savedPath;

    public bool HasResult => SavedPath != null;

    /// <summary>Reads whether the document is tagged and the language it declares.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var (tagged, language) = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
                return (PdfAutoTagger.IsTagged(doc), PdfAutoTagger.DocumentLanguage(doc));
            });
            IsTagged = tagged;
            if (language != null) Language = language;
            Summary = tagged
                ? "This document already has tags. Replacing them rebuilds the structure from the page layout."
                : "Choose the document language, then save the tagged copy under a new name.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ArgumentException)
        {
            Summary = $"The document could not be read: {ex.Message}";
        }
    }

    /// <summary>Tags the document and writes the copy to <paramref name="outputPath"/> (never over the original).</summary>
    public async Task<bool> SaveAsAsync(string outputPath)
    {
        if (!CanTag) return false;
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Summary = "Choose a different file: the original is not overwritten.";
            return false;
        }
        IsBusy = true;
        Summary = "Tagging the document...";
        Found.Clear();
        Review.Clear();
        Remaining.Clear();
        try
        {
            var options = new PdfAutoTagOptions { Language = Language.Trim(), Retag = Retag };
            var result = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
                return PdfAutoTagger.Tag(doc, options);
            });
            if (!result.Succeeded)
            {
                Summary = result.Message;
                return false;
            }
            await File.WriteAllBytesAsync(outputPath, result.Bytes!);
            SavedPath = outputPath;
            Show(result);
            Summary = $"Saved {Path.GetFileName(outputPath)}. {result.Message}";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Summary = $"The document could not be tagged: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(PdfAutoTagResult result)
    {
        var s = result.Summary;
        Found.Add($"Headings: {s.Headings}");
        Found.Add($"Paragraphs: {s.Paragraphs}");
        Found.Add($"Lists: {s.Lists} ({s.ListItems} items)");
        Found.Add($"Tables: {s.Tables} ({s.TablesWithHeaders} with a header row)");
        Found.Add($"Figures needing alternate text: {result.FiguresNeedingAlt.Count} of {s.Figures}");
        Found.Add($"Links: {s.Links}");
        if (s.OtherAnnotations > 0) Found.Add($"Other annotations and form fields: {s.OtherAnnotations}");
        Found.Add($"Headers, footers and page numbers marked as artifacts: {s.HeadersAndFooters}");
        if (s.MultiColumnPages > 0) Found.Add($"Pages read as columns: {s.MultiColumnPages}");
        foreach (var r in result.Review) Review.Add(r);
        foreach (var r in result.Remaining.OrderBy(r => r.Kind).ThenBy(r => r.Checkpoint, StringComparer.Ordinal))
            Remaining.Add(new MakeAccessibleIssueItem(r.Checkpoint, r.Message, r.Count, KindText(r.Kind)));
    }

    public static string KindText(PdfAutoTagIssueKind kind) => kind switch
    {
        PdfAutoTagIssueKind.HumanReview => "needs a person",
        PdfAutoTagIssueKind.OutsideTagging => "not about tags",
        _ => "not fixed",
    };
}
