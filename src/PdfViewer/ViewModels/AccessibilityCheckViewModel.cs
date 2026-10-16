using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Geometry;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Document;

namespace PdfViewer.ViewModels;

/// <summary>One failure condition the document meets: what is wrong, how to fix it, and where.</summary>
public sealed class AccessibilityIssueItem
{
    public AccessibilityIssueItem(PdfUaIssue issue)
    {
        Checkpoint = issue.Checkpoint;
        Clause = issue.Clause;
        Category = issue.Category;
        IsWarning = issue.Severity == PdfUaSeverity.Warning;
        Message = issue.Message;
        Fix = issue.Fix;
        Location = issue.Location ?? "The whole document";
        Page = issue.Page;
        NormalizedBounds = issue.NormalizedBounds;
    }

    public string Checkpoint { get; }
    public string Clause { get; }
    public PdfUaCategory Category { get; }
    public bool IsWarning { get; }
    public string SeverityText => IsWarning ? "Needs review" : "Failed";
    public string Message { get; }
    public string Fix { get; }
    public string Location { get; }
    public int? Page { get; }
    public bool HasPage => Page != null;
    /// <summary>The element's bounds as fractions of the page (from its top-left), when known.</summary>
    public PdfRect? NormalizedBounds { get; }
    public string Reference => $"Matterhorn checkpoint {Checkpoint}, ISO 14289-1 clause {Clause}";
    /// <summary>What a screen reader announces for the item.</summary>
    public string AutomationName => $"{SeverityText}: {Message} {Location}.";
}

/// <summary>The issues of one category, as Acrobat's checker groups them.</summary>
public sealed partial class AccessibilityIssueGroup : ObservableObject
{
    public AccessibilityIssueGroup(PdfUaCategory category, IReadOnlyList<AccessibilityIssueItem> issues)
    {
        Category = category;
        Issues = issues;
        _isExpanded = issues.Any(i => !i.IsWarning);
    }

    public PdfUaCategory Category { get; }
    public string Name => AccessibilityCheckViewModel.CategoryName(Category);
    public IReadOnlyList<AccessibilityIssueItem> Issues { get; }
    public int Errors => Issues.Count(i => !i.IsWarning);
    public int Warnings => Issues.Count(i => i.IsWarning);

    public string Header => (Errors, Warnings) switch
    {
        (0, 0) => $"{Name}: passed",
        (_, 0) => $"{Name}: {Errors} failed",
        (0, _) => $"{Name}: {Warnings} to review",
        _ => $"{Name}: {Errors} failed, {Warnings} to review",
    };

    public string AutomationName => Header;

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// The accessibility check (Acrobat's Full Check) of the open document against PDF/UA-1 and the
/// Matterhorn Protocol: issues grouped by category, each saying what is wrong and how to fix it.
/// Selecting an issue on a page shows that page and highlights the element when its bounds are known.
/// </summary>
public sealed partial class AccessibilityCheckViewModel : ObservableObject
{
    private readonly byte[] _bytes;
    private readonly string _path;
    private readonly string? _password;
    private readonly Action<int, PdfRect?>? _showOnPage;

    public AccessibilityCheckViewModel(byte[] bytes, string path, string? password, Action<int, PdfRect?>? showOnPage)
    {
        _bytes = bytes;
        _path = path;
        _password = password;
        _showOnPage = showOnPage;
    }

    public string FileName => Path.GetFileName(_path);

    public ObservableCollection<AccessibilityIssueGroup> Groups { get; } = new();

    public IEnumerable<AccessibilityIssueItem> AllIssues => Groups.SelectMany(g => g.Issues);

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _claimText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _isBusy;

    public bool CanCheck => !IsBusy;

    [ObservableProperty]
    private bool _isCompliant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    private AccessibilityIssueItem? _selectedIssue;

    public bool HasSelection => SelectedIssue != null;
    public bool HasNoSelection => SelectedIssue == null;

    partial void OnSelectedIssueChanged(AccessibilityIssueItem? value)
    {
        if (value != null) ShowOnPage(value);
    }

    /// <summary>Goes to the issue's page and highlights the element, when it is on one.</summary>
    public void ShowOnPage(AccessibilityIssueItem issue)
    {
        if (issue.Page is { } page) _showOnPage?.Invoke(page, issue.NormalizedBounds);
    }

    public static string CategoryName(PdfUaCategory category) => category switch
    {
        PdfUaCategory.PageContent => "Page content",
        PdfUaCategory.AlternateText => "Alternate text",
        _ => category.ToString(),
    };

    public async Task CheckAsync()
    {
        IsBusy = true;
        Summary = "Checking accessibility...";
        SelectedIssue = null;
        try
        {
            var report = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
                return PdfUaValidator.Validate(doc, _bytes);
            });
            Groups.Clear();
            var items = report.Issues.Select(i => new AccessibilityIssueItem(i)).ToList();
            foreach (var category in Enum.GetValues<PdfUaCategory>())
                Groups.Add(new AccessibilityIssueGroup(category, items.Where(i => i.Category == category).OrderBy(i => i.IsWarning).ThenBy(i => i.Page ?? 0).ToList()));
            IsCompliant = report.IsCompliant;
            ClaimText = report.ClaimsPdfUa ? "The document claims PDF/UA-1 conformance." : "The document does not claim PDF/UA conformance.";
            int errors = items.Count(i => !i.IsWarning), warnings = items.Count - errors;
            int failedGroups = Groups.Count(g => g.Errors > 0);
            Summary = report.IsCompliant
                ? $"No accessibility failures found{(warnings > 0 ? $"; {warnings} item(s) to review" : string.Empty)}. Reading order, colour contrast and whether alternate text is meaningful still need a person to check."
                : $"The document fails the accessibility check: {errors} issue(s) in {failedGroups} categor{(failedGroups == 1 ? "y" : "ies")}{(warnings > 0 ? $", and {warnings} item(s) to review" : string.Empty)}.";
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
}
