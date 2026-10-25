using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Geometry;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PrintProduction;

namespace PdfViewer.ViewModels;

/// <summary>One preflight result in the list.</summary>
public sealed class PreflightFindingItem
{
    public PreflightFindingItem(PdfPreflightFinding finding)
    {
        Finding = finding;
    }

    public PdfPreflightFinding Finding { get; }
    public string RuleId => Finding.RuleId;
    public PdfPreflightSeverity Severity => Finding.Severity;
    public string Message => Finding.Message;
    public string Location => Finding.Location ?? "The whole document";
    public int? Page => Finding.Page;
    public bool HasPage => Finding.Page != null;
    public PdfRect? NormalizedBounds => Finding.NormalizedBounds;
    public string SeverityText => PreflightViewModel.SeverityName(Severity);
    public string Measure => Finding.Measure is { } m ? $"{m:0.##} {Finding.Unit}".Trim() : string.Empty;
    public string AutomationName => $"{SeverityText}: {Message} {Location}.";
}

/// <summary>The results of one severity.</summary>
public sealed partial class PreflightFindingGroup : ObservableObject
{
    public PreflightFindingGroup(PdfPreflightSeverity severity, IReadOnlyList<PreflightFindingItem> findings)
    {
        Severity = severity;
        Findings = findings;
        _isExpanded = severity != PdfPreflightSeverity.Info && findings.Count > 0;
    }

    public PdfPreflightSeverity Severity { get; }
    public IReadOnlyList<PreflightFindingItem> Findings { get; }
    public string Header => $"{PreflightViewModel.SeverityName(Severity)}s: {Findings.Count}";
    public string AutomationName => Header;

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// Preflight (Acrobat Pro's Print Production tool) of the open document against a built-in profile: results
/// grouped by severity; selecting one on a page shows the page with the object highlighted. Reports export as
/// text or JSON with page numbers, object numbers and measures, never page text.
/// </summary>
public sealed partial class PreflightViewModel : ObservableObject
{
    private readonly byte[] _bytes;
    private readonly string _path;
    private readonly string? _password;
    private readonly Action<int, PdfRect?>? _showOnPage;
    private readonly Func<IPdfDisplayListRasterizer>? _rasterizerFactory;

    public PreflightViewModel(byte[] bytes, string path, string? password, Action<int, PdfRect?>? showOnPage,
        Func<IPdfDisplayListRasterizer>? rasterizerFactory = null)
    {
        _bytes = bytes;
        _path = path;
        _password = password;
        _showOnPage = showOnPage;
        _rasterizerFactory = rasterizerFactory;
        _selectedProfile = Profiles[0];
    }

    public string FileName => Path.GetFileName(_path);

    public IReadOnlyList<PdfPreflightProfile> Profiles => PdfPreflightProfile.BuiltIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileDescription))]
    private PdfPreflightProfile _selectedProfile;

    public string ProfileDescription => SelectedProfile.Description + " " + SelectedProfile.Disclaimer;

    public ObservableCollection<PreflightFindingGroup> Groups { get; } = new();

    public IEnumerable<PreflightFindingItem> AllFindings => Groups.SelectMany(g => g.Findings);

    [ObservableProperty]
    private string _summary = "Choose a profile and select Analyze.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isBusy;

    public bool CanRun => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private PdfPreflightReport? _report;

    public bool HasReport => Report != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PreflightFindingItem? _selectedFinding;

    public bool HasSelection => SelectedFinding != null;

    partial void OnSelectedFindingChanged(PreflightFindingItem? value)
    {
        if (value != null) ShowOnPage(value);
    }

    public void ShowOnPage(PreflightFindingItem item)
    {
        if (item.Page is { } page) _showOnPage?.Invoke(page, item.NormalizedBounds);
    }

    public static string SeverityName(PdfPreflightSeverity severity) => severity switch
    {
        PdfPreflightSeverity.Error => "Error",
        PdfPreflightSeverity.Warning => "Warning",
        _ => "Information",
    };

    public async Task RunAsync()
    {
        IsBusy = true;
        SelectedFinding = null;
        var profile = SelectedProfile;
        Summary = $"Checking against {profile.Name}...";
        try
        {
            var report = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
                var rasterizer = _rasterizerFactory?.Invoke() ?? new Direct2DDisplayListRasterizer();
                try
                {
                    return await PdfPreflight.RunAsync(doc, profile, new PdfPreflightOptions { Rasterizer = rasterizer });
                }
                finally
                {
                    (rasterizer as IDisposable)?.Dispose();
                }
            });
            Report = report;
            Groups.Clear();
            foreach (var severity in new[] { PdfPreflightSeverity.Error, PdfPreflightSeverity.Warning, PdfPreflightSeverity.Info })
                Groups.Add(new PreflightFindingGroup(severity, report.Findings.Where(f => f.Severity == severity)
                    .OrderBy(f => f.Page ?? 0).ThenBy(f => f.RuleId, StringComparer.Ordinal).Select(f => new PreflightFindingItem(f)).ToList()));
            Summary = report.Passed
                ? $"{profile.Name}: no errors{(report.Warnings > 0 ? $", {report.Warnings} warning(s)" : string.Empty)} on {report.PagesChecked} page(s)."
                : $"{profile.Name}: {report.Errors} error(s), {report.Warnings} warning(s) on {report.PagesChecked} page(s).";
        }
        catch (Exception ex) when (ex is PdfVectorException or InvalidOperationException or IOException or ArgumentException)
        {
            Summary = $"The document could not be checked: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The report as text or JSON, or null before a check.</summary>
    public string? Export(bool json) => Report == null ? null : json ? Report.ToJson() : Report.ToText();

    public string SuggestedReportName(bool json) =>
        Path.GetFileNameWithoutExtension(_path) + "_preflight" + (json ? ".json" : ".txt");
}
