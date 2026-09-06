using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// Owns the open documents and which one is on screen.
///
/// Each tab is a whole <see cref="MainViewModel"/> - its own document, pages, annotations,
/// search, history and unsaved state - because those things are per-document and sharing any
/// of them between tabs is what makes tabbed viewers leak state across documents.
///
/// The consequence is memory: every open document keeps its own rendered-page cache, so the
/// shared budget is re-divided whenever a tab opens or closes rather than each tab helping
/// itself to the whole thing.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    /// <summary>
    /// Beyond this, opening another document replaces the least recently used tab instead of
    /// adding one. Each open document holds its file in memory and a share of the page cache;
    /// an unbounded tab count is an unbounded memory commitment made one click at a time.
    /// </summary>
    public const int MaxOpenDocuments = 12;

    public ObservableCollection<MainViewModel> Documents { get; } = new();

    [ObservableProperty]
    private MainViewModel? _activeDocument;

    /// <summary>Shown instead of the document area when nothing is open.</summary>
    public bool HasOpenDocument => ActiveDocument != null;

    /// <summary>The tab strip only earns its space once there is a choice to make.</summary>
    public bool ShowTabStrip => Documents.Count > 1;

    public ShellViewModel()
    {
        Documents.CollectionChanged += OnDocumentsChanged;

        // There is always at least one tab, even with nothing open. The whole document UI -
        // menus, toolbar and the welcome screen included - binds through the active tab, so a
        // null one would leave the window with nothing to bind to. An empty tab is exactly
        // what the application looked like before tabs existed.
        Documents.Add(CreateTab());
        ActiveDocument = Documents[0];
    }

    /// <summary>
    /// A tab, wired so that anything it opens comes back here to be placed rather than
    /// replacing the document it is already showing.
    /// </summary>
    private MainViewModel CreateTab()
    {
        var document = new MainViewModel();
        document.RequestOpenDocumentAsync = async path => await OpenDocumentAsync(path);
        return document;
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowTabStrip));
        RedivideMemoryBudget();
        CloseActiveDocumentCommand.NotifyCanExecuteChanged();
        NextDocumentCommand.NotifyCanExecuteChanged();
        PreviousDocumentCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveDocumentChanged(MainViewModel? oldValue, MainViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasOpenDocument));
        ActiveDocumentChanged?.Invoke(oldValue, newValue);
    }

    /// <summary>
    /// Raised when the document on screen changes, so the view can put away the outgoing
    /// document's scroll position and restore the incoming one's.
    /// </summary>
    public event Action<MainViewModel?, MainViewModel?>? ActiveDocumentChanged;

    /// <summary>
    /// Splits the rendered-page budget across the open documents. Called whenever the number
    /// of them changes, so an existing tab gives memory back as soon as another opens rather
    /// than only bounding what it takes in future.
    /// </summary>
    private void RedivideMemoryBudget()
    {
        long share = LruPageCache.ShareOfTotal(Documents.Count);
        foreach (var document in Documents)
        {
            document.SetPageCacheCeiling(share);
        }
    }

    /// <summary>
    /// Opens a file, focusing the tab that already has it rather than opening it twice.
    /// Returns the tab the file ended up in, or null if it could not be opened.
    /// </summary>
    public async Task<MainViewModel?> OpenDocumentAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        // Already open: show it rather than loading a second copy.
        var existing = FindOpenDocument(filePath);
        if (existing != null)
        {
            ActiveDocument = existing;
            return existing;
        }

        // An empty tab is a place to put this, not something to open another tab beside.
        var emptyTab = ActiveDocument is { IsDocumentLoaded: false }
            ? ActiveDocument
            : Documents.FirstOrDefault(d => !d.IsDocumentLoaded);

        if (emptyTab != null)
        {
            ActiveDocument = emptyTab;
            await emptyTab.LoadDocumentAsync(filePath);
            return emptyTab.IsDocumentLoaded ? emptyTab : null;
        }

        // At the limit, the least recently used tab makes way. Its unsaved work is not
        // silently discarded - the same prompt a manual close would raise is asked first.
        if (Documents.Count >= MaxOpenDocuments)
        {
            var oldest = Documents.FirstOrDefault(d => d != ActiveDocument) ?? Documents[0];
            if (!await CloseDocumentAsync(oldest)) return null;
        }

        var tab = CreateTab();
        Documents.Add(tab);
        ActiveDocument = tab;

        await tab.LoadDocumentAsync(filePath);

        // A document that failed to open would leave an empty tab behind next to the one the
        // user was already reading.
        if (!tab.IsDocumentLoaded && Documents.Count > 1)
        {
            int index = Documents.IndexOf(tab);
            Documents.Remove(tab);
            ActiveDocument = Documents[Math.Min(index, Documents.Count - 1)];
            return null;
        }

        return tab.IsDocumentLoaded ? tab : null;
    }

    public MainViewModel? FindOpenDocument(string filePath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return Documents.FirstOrDefault(d =>
            d.Metadata != null &&
            string.Equals(SafeFullPath(d.Metadata.FilePath), full, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Closes a tab, asking about unsaved work first. Returns false if the user cancelled, in
    /// which case the tab is still open.
    /// </summary>
    public async Task<bool> CloseDocumentAsync(MainViewModel document)
    {
        if (document == null || !Documents.Contains(document)) return true;

        if (!await document.ConfirmDiscardChangesAsync()) return false;

        int index = Documents.IndexOf(document);

        document.ShutdownReadAloud();
        document.CloseDocument();

        // The last tab is emptied rather than removed, so the window always has something to
        // bind to and the user lands back on the welcome screen.
        if (Documents.Count == 1)
        {
            ActiveDocument = document;
            return true;
        }

        Documents.Remove(document);

        if (ActiveDocument == document)
        {
            // Focus the neighbour, the way every tabbed application does.
            ActiveDocument = Documents[Math.Min(index, Documents.Count - 1)];
        }

        return true;
    }

    public bool CanCloseActiveDocument => ActiveDocument != null;

    [RelayCommand(CanExecute = nameof(CanCloseActiveDocument))]
    public async Task CloseActiveDocumentAsync()
    {
        if (ActiveDocument != null) await CloseDocumentAsync(ActiveDocument);
    }

    public bool CanSwitchDocument => Documents.Count > 1;

    [RelayCommand(CanExecute = nameof(CanSwitchDocument))]
    public void NextDocument() => StepDocument(1);

    [RelayCommand(CanExecute = nameof(CanSwitchDocument))]
    public void PreviousDocument() => StepDocument(-1);

    private void StepDocument(int direction)
    {
        if (Documents.Count == 0 || ActiveDocument == null) return;

        int index = Documents.IndexOf(ActiveDocument);
        if (index < 0) return;

        // Wraps, so Ctrl+Tab from the last tab returns to the first.
        int next = ((index + direction) % Documents.Count + Documents.Count) % Documents.Count;
        ActiveDocument = Documents[next];
    }

    /// <summary>
    /// Asks every tab about its unsaved work. Returns false as soon as one is cancelled, so
    /// closing the window stops rather than continuing to prompt for the rest.
    /// </summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var document in Documents.ToList())
        {
            if (document.HasUnsavedChanges)
            {
                ActiveDocument = document;   // show what is being asked about
                if (!await document.ConfirmDiscardChangesAsync()) return false;
            }
        }

        return true;
    }

    public void ShutdownAll()
    {
        foreach (var document in Documents.ToList())
        {
            document.ShutdownReadAloud();
            document.CloseDocument();
        }
    }
}
