using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using PdfViewer.Core.Commands;
using PdfViewer.Core.Session;
using PdfViewer.Models;

namespace PdfViewer.ViewModels;

/// <summary>
/// One undo history per document, shared by comments and markup and by Edit Text &amp; Images,
/// so Ctrl+Z always takes back the last change whatever kind it was. The history is the
/// document's <see cref="CommandHistory"/>; each change is a command that can be undone and
/// redone. Undoing back to the state last opened or saved clears the unsaved-changes flag.
/// </summary>
public partial class MainViewModel
{
    private const int MaxUndo = 100;

    public bool CanUndo => CommandHistory.CanUndo;
    public bool CanRedo => CommandHistory.CanRedo;

    /// <summary>The Edit menu's Undo item: says what it will undo ("Undo Add Highlight").</summary>
    public string UndoHeader => CommandHistory.NextUndoName is { } name ? $"_Undo {name}" : "_Undo";

    public string RedoHeader => CommandHistory.NextRedoName is { } name ? $"Red_o {name}" : "Red_o";

    /// <summary>The toolbar's Undo button name: the step it takes back, with no access key ("Undo Add Highlight").</summary>
    public string UndoName => CommandHistory.NextUndoName is { } name ? $"Undo {name}" : "Undo";

    public string RedoName => CommandHistory.NextRedoName is { } name ? $"Redo {name}" : "Redo";

    /// <summary>The toolbar's Undo button tooltip: its name and its shortcut.</summary>
    public string UndoToolTip => $"{UndoName} (Ctrl+Z)";

    public string RedoToolTip => $"{RedoName} (Ctrl+Y)";

    // History operations run one at a time, in the order they were asked for, on the UI thread.
    // CommandHistory serializes them too, but a caller that has to wait for it resumes on a pool
    // thread, and these commands change bound collections.
    private Task _historyTail = Task.CompletedTask;

    private Task RunHistoryAsync(Func<Task> operation)
    {
        if (_historyTail.IsCompleted)
            return _historyTail = RunAndRefreshAsync(operation);
        return _historyTail = AfterAsync(_historyTail, operation);
    }

    private async Task AfterAsync(Task previous, Func<Task> operation)
    {
        try { await previous; }
        catch (Exception) { /* the earlier operation reported its own failure */ }
        await RunAndRefreshAsync(operation);
    }

    private async Task RunAndRefreshAsync(Func<Task> operation)
    {
        var history = CommandHistory;
        try
        {
            await operation();
        }
        finally
        {
            // A document opened or closed meanwhile has a new history: this one is gone.
            if (ReferenceEquals(history, CommandHistory)) RefreshUndoState();
        }
    }

    private Task ExecuteAsync(IDocumentCommand command) =>
        RunHistoryAsync(() => CommandHistory.ExecuteCommandAsync(command, Session).AsTask());

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public Task UndoAsync() => RunHistoryAsync(async () =>
    {
        string? name = CommandHistory.NextUndoName;
        if (name == null) return;
        await CommandHistory.UndoAsync(Session);
        StatusText = $"Undid {name}.";
    });

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public Task RedoAsync() => RunHistoryAsync(async () =>
    {
        string? name = CommandHistory.NextRedoName;
        if (name == null) return;
        await CommandHistory.RedoAsync(Session);
        StatusText = $"Redid {name}.";
    });

    /// <summary>A new document (or none): the history starts empty, and what is shown now is the saved state.</summary>
    private void ResetUndoHistory()
    {
        // A fresh history rather than Clear(): an undo still running holds the old one's lock,
        // and Clear() would wait for it here, on the thread that undo needs to finish.
        SelectedAnnotation = null;
        CommandHistory = new CommandHistory(MaxUndo, FeatureGate);
        _historyTail = Task.CompletedTask;
        _knownAnnotationStates.Clear();
        foreach (var a in AllAnnotations) _knownAnnotationStates[a] = AnnotationState.Of(a);
        MarkSavedState();
        RefreshUndoState();
    }

    private void RefreshUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoHeader));
        OnPropertyChanged(nameof(RedoHeader));
        OnPropertyChanged(nameof(UndoName));
        OnPropertyChanged(nameof(RedoName));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        if (IsDocumentLoaded) HasUnsavedChanges = !IsAtSavedState();
    }

    // ------------------------------------------------------------------ the saved state

    // What is on disk, as the document holds it in memory: the revision (form fills, OCR and
    // content edits each make a new one) and every annotation, in order, with its properties.
    private byte[]? _savedBytes;
    private List<(AnnotationModel Model, AnnotationState State)> _savedAnnotations = new();

    private void MarkSavedState()
    {
        _savedBytes = _docService.CurrentBytes;
        _savedAnnotations = AllAnnotations.Select(a => (a, AnnotationState.Of(a))).ToList();
    }

    private bool IsAtSavedState()
    {
        if (!ReferenceEquals(_docService.CurrentBytes, _savedBytes)) return false;
        if (AllAnnotations.Count != _savedAnnotations.Count) return false;
        for (int i = 0; i < AllAnnotations.Count; i++)
        {
            var (model, state) = _savedAnnotations[i];
            if (!ReferenceEquals(AllAnnotations[i], model) || !state.Equals(AnnotationState.Of(model))) return false;
        }
        _formChangedSinceSave = false;
        return true;
    }

    // ------------------------------------------------------------------ annotation changes

    /// <summary>Each annotation as the history last left it: what an in-place edit is undone back to.</summary>
    private readonly Dictionary<AnnotationModel, AnnotationState> _knownAnnotationStates = new(ReferenceEqualityComparer.Instance);

    /// <summary>What the Edit menu calls an annotation of this type.</summary>
    internal static string AnnotationTypeName(AnnotationType type) => type switch
    {
        AnnotationType.StrikeOut => "Strikeout",
        AnnotationType.FreeText => "Text Box",
        AnnotationType.Ink => "Drawing",
        AnnotationType.Measurement => "Measurement",
        AnnotationType.PolyLine => "Connected Lines",
        _ => type.ToString(),
    };

    /// <summary>
    /// The heading of the comment and style editor for an annotation of this type: "Add Text Box"
    /// when it is being created, "Edit Polygon" when it already exists. The window title stays
    /// generic; the heading says what is being edited.
    /// </summary>
    public static string EditDialogHeading(AnnotationType type, bool isNew) =>
        (isNew ? "Add " : "Edit ") + (type == AnnotationType.Note ? "Sticky Note" : AnnotationTypeName(type));

    private void RecordAnnotationChange(string name, List<AnnotationChange> changes)
    {
        if (changes.Count == 0) return;
        _ = ExecuteAsync(new AnnotationCommand(this, name, changes));
    }

    /// <summary>
    /// Changes an annotation in place (move, resize, colour, opacity, thickness, text) as one
    /// step that Undo takes back.
    /// </summary>
    public void ChangeAnnotation(AnnotationModel annot, Action<AnnotationModel> change, string name)
    {
        if (annot == null || !AllAnnotations.Contains(annot)) return;
        if (!Permit(DocumentPermissions.CanAnnotate, "changing comments")) return;
        change(annot);
        RecordInPlaceEdit(annot, name);
    }

    /// <summary>Records an edit already made to the annotation's properties; false when nothing changed.</summary>
    private bool RecordInPlaceEdit(AnnotationModel annot, string name)
    {
        FitShapeBox(annot); // new line endings or a wider line need a bigger box
        var before = _knownAnnotationStates.TryGetValue(annot, out var known) ? known : null;
        var after = AnnotationState.Of(annot);
        if (before != null && before.EqualsIgnoringModified(after)) return false;
        annot.ModifiedDate = DateTime.Now;
        after = AnnotationState.Of(annot);
        int index = AllAnnotations.IndexOf(annot);
        RecordAnnotationChange(name, new List<AnnotationChange> { new(annot, before ?? after, after, index, PageIndexOf(annot)) });
        return true;
    }

    private int PageIndexOf(AnnotationModel annot) =>
        annot.PageNumber >= 1 && annot.PageNumber <= Pages.Count ? Pages[annot.PageNumber - 1].AnnotationsOnPage.IndexOf(annot) : -1;

    /// <summary>
    /// Brings the annotations to one side of a change: removed ones leave the document, restored
    /// ones go back to where they were in the list and on their page (their stacking order), and
    /// changed ones take their properties.
    /// </summary>
    private void ApplyAnnotationChanges(IReadOnlyList<AnnotationChange> changes, bool forward)
    {
        // The comments panel is brought up to date once, after the whole change.
        using (DeferCommentThreads())
            ApplyAnnotationChangesNow(changes, forward);
    }

    private void ApplyAnnotationChangesNow(IReadOnlyList<AnnotationChange> changes, bool forward)
    {
        foreach (var c in changes)
        {
            if (c.Target(forward) != null) continue;
            AllAnnotations.Remove(c.Model);
            RemoveFromPage(c.Model);
            _knownAnnotationStates.Remove(c.Model);
        }

        var kept = changes.Where(c => c.Target(forward) != null).ToList();
        foreach (var c in kept.OrderBy(c => c.Index))
        {
            var state = c.Target(forward)!;
            RemoveFromPage(c.Model);
            state.ApplyTo(c.Model);
            if (!AllAnnotations.Contains(c.Model)) AllAnnotations.Insert(Math.Clamp(c.Index, 0, AllAnnotations.Count), c.Model);
            _knownAnnotationStates[c.Model] = state;
        }

        // Each page's list is its drawing order, so it is put back in its own order.
        foreach (var c in kept.OrderBy(c => c.PageIndex))
        {
            // A reply is not drawn on the page: the comments panel lists it under its parent.
            if (c.Model.IsReply || c.Model.PageNumber < 1 || c.Model.PageNumber > Pages.Count) continue;
            var onPage = Pages[c.Model.PageNumber - 1].AnnotationsOnPage;
            onPage.Insert(c.PageIndex < 0 ? onPage.Count : Math.Min(c.PageIndex, onPage.Count), c.Model);
        }

        SyncAnnotationDrawing();
    }

    private void RemoveFromPage(AnnotationModel annot)
    {
        foreach (var page in Pages)
            if (page.AnnotationsOnPage.Remove(annot)) return;
    }

    /// <summary>One annotation on both sides of a change; a null state means it is not in the document.</summary>
    private sealed record AnnotationChange(AnnotationModel Model, AnnotationState? Before, AnnotationState? After, int Index, int PageIndex)
    {
        public AnnotationState? Target(bool forward) => forward ? After : Before;
    }

    private sealed class AnnotationCommand : IDocumentCommand
    {
        private readonly MainViewModel _owner;
        private readonly IReadOnlyList<AnnotationChange> _changes;

        public AnnotationCommand(MainViewModel owner, string name, IReadOnlyList<AnnotationChange> changes)
        {
            _owner = owner;
            Name = name;
            _changes = changes;
        }

        public string Name { get; }

        public ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            _owner.ApplyAnnotationChanges(_changes, forward: true);
            return ValueTask.CompletedTask;
        }

        public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            _owner.ApplyAnnotationChanges(_changes, forward: false);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Everything about an annotation that a change can touch, copied so later edits do not reach it.</summary>
    internal sealed class AnnotationState : IEquatable<AnnotationState>
    {
        private int PageNumber { get; init; }
        private AnnotationType Type { get; init; }
        private Rect Box { get; init; }
        private Rect[] Quads { get; init; } = Array.Empty<Rect>();
        private Point[][] Ink { get; init; } = Array.Empty<Point[]>();
        private string ColorHex { get; init; } = string.Empty;
        private double Opacity { get; init; }
        private double StrokeThickness { get; init; }
        private string Contents { get; init; } = string.Empty;
        private string Author { get; init; } = string.Empty;
        private string Title { get; init; } = string.Empty;
        private DateTime CreationDate { get; init; }
        private DateTime ModifiedDate { get; init; }
        private Point[] Vertices { get; init; } = Array.Empty<Point>();
        private PdfEngine.Vector.Annotations.PdfMeasurementKind MeasureKind { get; init; }
        private PdfEngine.Vector.Annotations.PdfMeasure? Measure { get; init; }
        private string StampName { get; init; } = string.Empty;
        private string? StampSubtitle { get; init; }
        private double StampRotation { get; init; }
        private Size StampSize { get; init; }
        private string[]? LineEndings { get; init; }
        private string? InteriorColorHex { get; init; }
        private double[]? BorderDash { get; init; }

        public static AnnotationState Of(AnnotationModel a) => new()
        {
            Vertices = a.Vertices.ToArray(),
            MeasureKind = a.MeasureKind,
            Measure = a.Measure,
            StampName = a.StampName,
            StampSubtitle = a.StampSubtitle,
            StampRotation = a.StampRotation,
            StampSize = a.StampSize,
            LineEndings = a.LineEndings?.ToArray(),
            InteriorColorHex = a.InteriorColorHex,
            BorderDash = a.BorderDash?.ToArray(),
            PageNumber = a.PageNumber,
            Type = a.Type,
            Box = new Rect(a.X, a.Y, a.Width, a.Height),
            Quads = a.Quads.ToArray(),
            Ink = a.InkStrokes.Select(s => s.ToArray()).ToArray(),
            ColorHex = a.ColorHex,
            Opacity = a.Opacity,
            StrokeThickness = a.StrokeThickness,
            Contents = a.Contents,
            Author = a.Author,
            Title = a.Title,
            CreationDate = a.CreationDate,
            ModifiedDate = a.ModifiedDate,
        };

        public void ApplyTo(AnnotationModel a)
        {
            a.PageNumber = PageNumber;
            a.Type = Type;
            a.SetGeometry(Box, Quads);
            a.InkStrokes = Ink.Select(s => s.ToList()).ToList();
            a.ColorHex = ColorHex;
            a.Opacity = Opacity;
            a.StrokeThickness = StrokeThickness;
            a.Contents = Contents;
            a.Author = Author;
            a.Title = Title;
            a.CreationDate = CreationDate;
            a.ModifiedDate = ModifiedDate;
            a.MeasureKind = MeasureKind;
            a.Measure = Measure;
            a.Vertices = Vertices.ToList();
            a.StampName = StampName;
            a.StampSubtitle = StampSubtitle;
            a.StampRotation = StampRotation;
            a.StampSize = StampSize;
            a.LineEndings = LineEndings?.ToArray();
            a.InteriorColorHex = InteriorColorHex;
            a.BorderDash = BorderDash?.ToArray();
        }

        public bool EqualsIgnoringModified(AnnotationState other) =>
            PageNumber == other.PageNumber && Type == other.Type && Box == other.Box
            && Quads.AsSpan().SequenceEqual(other.Quads)
            && Ink.Length == other.Ink.Length && Ink.Zip(other.Ink).All(p => p.First.AsSpan().SequenceEqual(p.Second))
            && ColorHex == other.ColorHex && Opacity.Equals(other.Opacity) && StrokeThickness.Equals(other.StrokeThickness)
            && Contents == other.Contents && Author == other.Author && Title == other.Title && CreationDate == other.CreationDate
            && Vertices.AsSpan().SequenceEqual(other.Vertices) && MeasureKind == other.MeasureKind
            && (Measure == null ? other.Measure == null : Measure.SameAs(other.Measure))
            && StampName == other.StampName && StampSubtitle == other.StampSubtitle && StampRotation.Equals(other.StampRotation) && StampSize == other.StampSize
            && (LineEndings ?? Array.Empty<string>()).AsSpan().SequenceEqual(other.LineEndings ?? Array.Empty<string>())
            && InteriorColorHex == other.InteriorColorHex
            && (BorderDash ?? Array.Empty<double>()).AsSpan().SequenceEqual(other.BorderDash ?? Array.Empty<double>());

        public bool Equals(AnnotationState? other) => other != null && EqualsIgnoringModified(other) && ModifiedDate == other.ModifiedDate;
        public override bool Equals(object? obj) => Equals(obj as AnnotationState);
        public override int GetHashCode() => HashCode.Combine(PageNumber, Type, Box, ColorHex, Contents);
    }

    // ------------------------------------------------------------------ content revisions

    /// <summary>
    /// A content edit (Edit Text &amp; Images, header and footer, watermark...): the revision before
    /// and the one after, shown in turn.
    /// </summary>
    private sealed class RevisionCommand : IDocumentCommand
    {
        private readonly MainViewModel _owner;
        private readonly byte[] _before, _after;
        private IReadOnlyList<int>? _pages;

        public RevisionCommand(MainViewModel owner, string name, byte[] before, byte[] after, IReadOnlyList<int>? pages)
        {
            _owner = owner;
            Name = name;
            _before = before;
            _after = after;
            _pages = pages;
        }

        public string Name { get; }

        public async ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
        {
            // The first time only the pages the edit touched are drawn again; a redo shows them all.
            var pages = _pages;
            _pages = null;
            await _owner.ShowRevisionAsync(_after, pages);
        }

        public async ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default) =>
            await _owner.ShowRevisionAsync(_before, null);
    }

    private Task RecordRevisionAsync(string name, byte[] before, byte[] after, IReadOnlyList<int>? pages) =>
        ExecuteAsync(new RevisionCommand(this, name, before, after, pages));
}
