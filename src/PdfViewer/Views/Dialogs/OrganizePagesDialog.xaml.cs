using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PdfEngine.Pages;
using PdfEngine.Rendering;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Builds a page arrangement: order, rotation and which pages to keep. The dialog only
/// describes the result; writing it is the caller's job, and always to a new file.
/// </summary>
public partial class OrganizePagesDialog : Window
{
    /// <summary>One row: a source page and what the user has done to it.</summary>
    public sealed class PageEntry : INotifyPropertyChanged
    {
        private int _position;
        private int _quarterTurns;

        public PageEntry(int sourcePageNumber)
        {
            SourcePageNumber = sourcePageNumber;
        }

        public int SourcePageNumber { get; }

        /// <summary>1-based position in the document being built.</summary>
        public int Position
        {
            get => _position;
            set { _position = value; Raise(nameof(Position)); Raise(nameof(PositionLabel)); }
        }

        /// <summary>Clockwise quarter turns to apply, 0-3.</summary>
        public int QuarterTurns
        {
            get => _quarterTurns;
            set { _quarterTurns = ((value % 4) + 4) % 4; Raise(nameof(QuarterTurns)); Raise(nameof(Description)); }
        }

        public string PositionLabel => $"{Position}.";

        public string Description
        {
            get
            {
                string rotation = QuarterTurns switch
                {
                    1 => "rotated 90° right",
                    2 => "rotated 180°",
                    3 => "rotated 90° left",
                    _ => "unchanged"
                };
                return $"Page {SourcePageNumber} — {rotation}";
            }
        }

        public PageRotation Rotation => QuarterTurns switch
        {
            1 => PageRotation.Rotate90,
            2 => PageRotation.Rotate180,
            3 => PageRotation.Rotate270,
            _ => PageRotation.Rotate0
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly int _originalPageCount;

    public ObservableCollection<PageEntry> Entries { get; } = new();

    /// <summary>True when the user chose to save rather than cancel.</summary>
    public bool SaveRequested { get; private set; }

    public OrganizePagesDialog(int pageCount, string documentName)
    {
        InitializeComponent();

        _originalPageCount = pageCount;
        SubtitleText.Text = $"{documentName} — {pageCount} page(s). Reorder, rotate or leave pages out, " +
                            "then save the result as a new document.";

        Reset();
        PageList.ItemsSource = Entries;
        Entries.CollectionChanged += (_, _) => Renumber();
        if (Entries.Count > 0) PageList.SelectedIndex = 0;
    }

    /// <summary>The arrangement the user built, in order.</summary>
    public IReadOnlyList<PageArrangementEntry> BuildArrangement() =>
        Entries.Select(e => new PageArrangementEntry(e.SourcePageNumber, e.Rotation)).ToList();

    private void Reset()
    {
        Entries.Clear();
        for (int page = 1; page <= _originalPageCount; page++)
        {
            Entries.Add(new PageEntry(page));
        }
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            Entries[i].Position = i + 1;
        }

        int removed = _originalPageCount - Entries.Count;
        SummaryText.Text = removed > 0
            ? $"{Entries.Count} page(s), {removed} left out"
            : $"{Entries.Count} page(s)";

        // A document with no pages is not a document, so saving is refused rather than
        // producing a file that cannot be opened.
        SaveButton.IsEnabled = Entries.Count > 0;
    }

    private List<PageEntry> SelectedEntries() =>
        PageList.SelectedItems.Cast<PageEntry>().OrderBy(e => Entries.IndexOf(e)).ToList();

    private void Reselect(IEnumerable<PageEntry> entries)
    {
        PageList.SelectedItems.Clear();
        foreach (var entry in entries)
        {
            PageList.SelectedItems.Add(entry);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0 || Entries.IndexOf(selected[0]) == 0) return;

        foreach (var entry in selected)
        {
            int index = Entries.IndexOf(entry);
            Entries.Move(index, index - 1);
        }

        Reselect(selected);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0 || Entries.IndexOf(selected[^1]) == Entries.Count - 1) return;

        // Move the bottom one first, or each move would land on top of the next.
        for (int i = selected.Count - 1; i >= 0; i--)
        {
            int index = Entries.IndexOf(selected[i]);
            Entries.Move(index, index + 1);
        }

        Reselect(selected);
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-1);

    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(1);

    private void Rotate(int quarterTurns)
    {
        foreach (var entry in SelectedEntries())
        {
            entry.QuarterTurns += quarterTurns;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) return;

        if (selected.Count == Entries.Count)
        {
            MessageBox.Show(this,
                "A document must keep at least one page.",
                "Organize Pages", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int firstIndex = Entries.IndexOf(selected[0]);
        foreach (var entry in selected)
        {
            Entries.Remove(entry);
        }

        if (Entries.Count > 0)
        {
            PageList.SelectedIndex = System.Math.Min(firstIndex, Entries.Count - 1);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Reset();
        if (Entries.Count > 0) PageList.SelectedIndex = 0;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0) return;

        SaveRequested = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SaveRequested = false;
        DialogResult = false;
        Close();
    }
}
