using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PdfA;

namespace PdfViewer.ViewModels;

/// <summary>One rule the document does not meet, with how often.</summary>
public sealed record PdfAIssueItem(string Rule, string Message, string? Location, int Count)
{
    public string CountText => Count > 1 ? $"{Count} times" : string.Empty;
}

/// <summary>
/// PDF/A for the open document: checks it against a chosen part and level (reporting every rule
/// it breaks, by ISO 19005 clause), and converts it to PDF/A-2b or -3b as a new file, reporting
/// what was changed and anything that could not be fixed.
/// </summary>
public sealed partial class PdfAViewModel : ObservableObject
{
    private readonly byte[] _bytes;
    private readonly string _path;
    private readonly string? _password;

    public PdfAViewModel(byte[] bytes, string path, string? password, string? claimed)
    {
        _bytes = bytes;
        _path = path;
        _password = password;
        Claimed = claimed;
        SelectedFlavour = Flavours.FirstOrDefault(f => f.Equals(claimed, StringComparison.OrdinalIgnoreCase)) ?? "PDF/A-2b";
    }

    public string FileName => Path.GetFileName(_path);
    public string? Claimed { get; }
    public string ClaimText => Claimed == null ? "The document does not claim PDF/A conformance." : $"The document claims {Claimed} conformance.";

    public IReadOnlyList<string> Flavours { get; } = new[] { "PDF/A-1b", "PDF/A-2b", "PDF/A-2u", "PDF/A-3b", "PDF/A-3u" };

    [ObservableProperty]
    private string _selectedFlavour;

    public ObservableCollection<PdfAIssueItem> Issues { get; } = new();
    public ObservableCollection<string> Changes { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The converted file, once saved.</summary>
    [ObservableProperty]
    private string? _convertedPath;

    public bool CanConvert => SelectedFlavour is "PDF/A-2b" or "PDF/A-3b";

    partial void OnSelectedFlavourChanged(string value) => OnPropertyChanged(nameof(CanConvert));

    public string SuggestedOutputPath =>
        Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, Path.GetFileNameWithoutExtension(_path) + "_PDFA.pdf");

    public async Task CheckAsync()
    {
        var flavour = PdfAFlavour.Parse(SelectedFlavour) ?? PdfAFlavour.A2b;
        IsBusy = true;
        Summary = $"Checking against {flavour}...";
        try
        {
            var report = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
                return PdfAValidator.Validate(doc, _bytes, flavour);
            });
            Show(report.Summary);
            Changes.Clear();
            Summary = report.IsCompliant
                ? $"The document conforms to {flavour}."
                : $"The document does not conform to {flavour}: {report.Summary.Count()} rule(s) broken.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ArgumentException)
        {
            Summary = $"The document could not be checked: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Converts to the selected PDF/A-2b or -3b and writes the result to <paramref name="outputPath"/> (never over the original).</summary>
    public async Task<bool> ConvertAsync(string outputPath)
    {
        if (!CanConvert) return false;
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            Summary = "Choose a different file: the original is not overwritten.";
            return false;
        }
        var flavour = PdfAFlavour.Parse(SelectedFlavour)!;
        IsBusy = true;
        Summary = $"Converting to {flavour}...";
        try
        {
            var result = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
                return PdfAConverter.Convert(doc, _bytes, new PdfAConversionOptions { Flavour = flavour });
            });
            await File.WriteAllBytesAsync(outputPath, result.Bytes);
            ConvertedPath = outputPath;
            Changes.Clear();
            foreach (var c in result.Changes) Changes.Add(c);
            Show(result.Report.Summary);
            Summary = result.Report.IsCompliant
                ? $"Saved as {flavour}: {Path.GetFileName(outputPath)}. It passes the {flavour} check."
                : $"Saved {Path.GetFileName(outputPath)}, but {result.Report.Summary.Count()} rule(s) could not be fixed automatically (listed below).";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Summary = $"The document could not be converted: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(IEnumerable<(PdfAIssue First, int Count)> summary)
    {
        Issues.Clear();
        foreach (var (issue, count) in summary)
            Issues.Add(new PdfAIssueItem(issue.RuleId, issue.Message, issue.Location, count));
    }
}
