using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfViewer.Models;

namespace PdfViewer.ViewModels;

/// <summary>
/// One comment in the comments panel with the replies to it (ISO 32000-2 12.5.6.4), as
/// Acrobat's comment pane shows them: the comment, its review status, its replies (shown or
/// hidden), and a box to write a reply in. The panel keeps the same object for the same comment
/// while the list changes, so a reply being written, or a thread opened, stays as it is.
/// </summary>
public sealed partial class CommentThreadViewModel : ObservableObject
{
    internal CommentThreadViewModel(MainViewModel owner, AnnotationModel root)
    {
        Owner = owner;
        Root = root;
        Replies.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ReplyCount));
            OnPropertyChanged(nameof(HasReplies));
            OnPropertyChanged(nameof(ReplyCountText));
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(ShowsReplies));
            OnPropertyChanged(nameof(AccessibleName));
        };
    }

    /// <summary>The document the comment belongs to.</summary>
    public MainViewModel Owner { get; }

    /// <summary>The comment the thread starts at (it answers none).</summary>
    public AnnotationModel Root { get; }

    /// <summary>The replies, each after the one it answers, oldest first; statuses are not listed (they show as <see cref="ReviewState"/>).</summary>
    public ObservableCollection<CommentReplyViewModel> Replies { get; } = new();

    public int ReplyCount => Replies.Count;
    public bool HasReplies => Replies.Count > 0;
    public string ReplyCountText => Replies.Count == 1 ? "1 reply" : $"{Replies.Count} replies";

    /// <summary>The replies are listed (the thread is open and has some).</summary>
    public bool ShowsReplies => IsExpanded && HasReplies;

    /// <summary>The thread's replies are listed; each thread opens and closes on its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText), nameof(ShowsReplies))]
    private bool _isExpanded = true;

    /// <summary>What the button that shows or hides the replies says.</summary>
    public string ToggleText => (IsExpanded ? "Hide " : "Show ") + ReplyCountText;

    /// <summary>The reply box is open.</summary>
    [ObservableProperty]
    private bool _isReplying;

    /// <summary>The reply being written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostReplyCommand))]
    private string _replyText = string.Empty;

    /// <summary>The comment's review status (the latest one set: "Accepted", "Rejected", "Cancelled" or "Completed"); null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewState), nameof(AccessibleName))]
    private string? _reviewState;

    /// <summary>Who set which status, and when, one per line (the badge's tooltip).</summary>
    [ObservableProperty]
    private string _reviewHistory = string.Empty;

    public bool HasReviewState => ReviewState != null;

    /// <summary>What a screen reader says for the comment.</summary>
    public string AccessibleName
    {
        get
        {
            string text = string.IsNullOrWhiteSpace(Root.Contents) ? "no text" : Root.Contents;
            string author = string.IsNullOrWhiteSpace(Root.Author) ? "unknown author" : Root.Author;
            string status = ReviewState != null ? $", status {ReviewState}" : string.Empty;
            string replies = HasReplies ? $", {ReplyCountText}" : string.Empty;
            return $"{MainViewModel.AnnotationTypeName(Root.Type)} on page {Root.PageNumber} by {author}, {Root.CreationDate.ToString("g", CultureInfo.CurrentCulture)}: {text}{status}{replies}";
        }
    }

    /// <summary>What a screen reader says for the reply box.</summary>
    public string ReplyBoxName => $"Reply to {(string.IsNullOrWhiteSpace(Root.Author) ? "the comment" : Root.Author)}";

    internal void RaiseAccessibleName() => OnPropertyChanged(nameof(AccessibleName));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Opens the reply box (the window puts the keyboard focus in it).</summary>
    [RelayCommand]
    private void StartReply()
    {
        IsExpanded = true;
        IsReplying = true;
    }

    private bool CanPostReply() => !string.IsNullOrWhiteSpace(ReplyText);

    /// <summary>Adds the reply (Enter or Ctrl+Enter in the box) and closes the box.</summary>
    [RelayCommand(CanExecute = nameof(CanPostReply))]
    private void PostReply()
    {
        if (Owner.ReplyToComment(Root, ReplyText) == null) return;
        ReplyText = string.Empty;
        IsReplying = false;
    }

    /// <summary>Closes the reply box (Esc); what was written is thrown away.</summary>
    [RelayCommand]
    private void CancelReply()
    {
        ReplyText = string.Empty;
        IsReplying = false;
    }

    /// <summary>Sets the comment's review status ("Accepted", "Rejected", "Cancelled", "Completed", or "None" to clear it).</summary>
    [RelayCommand]
    private void SetStatus(string? state)
    {
        if (!string.IsNullOrEmpty(state)) Owner.SetReviewState(Root, state);
    }

    [RelayCommand]
    private void Delete() => Owner.DeleteAnnotation(Root);
}

/// <summary>A reply in a comment thread: who wrote it and when, what it says, and (for the user's own) editing and deleting it.</summary>
public sealed partial class CommentReplyViewModel : ObservableObject
{
    internal CommentReplyViewModel(CommentThreadViewModel thread, AnnotationModel model)
    {
        Thread = thread;
        Model = model;
    }

    public CommentThreadViewModel Thread { get; }
    public AnnotationModel Model { get; }

    /// <summary>How many replies lead from the thread's comment to this one (1: it answers the comment).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indent))]
    private int _depth = 1;

    /// <summary>A reply to a reply is indented under it (at most four steps).</summary>
    public Thickness Indent => new(Math.Min(Math.Max(Depth - 1, 0), 4) * 12, 0, 0, 0);

    /// <summary>The user wrote it (its author is the author name the user comments under): only these can be edited and deleted.</summary>
    [ObservableProperty]
    private bool _isOwn;

    /// <summary>The reply's review status (set on it in Acrobat), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewState))]
    private string? _reviewState;

    public bool HasReviewState => ReviewState != null;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private string _editText = string.Empty;

    /// <summary>What a screen reader says for the reply.</summary>
    public string AccessibleName
    {
        get
        {
            string author = string.IsNullOrWhiteSpace(Model.Author) ? "unknown author" : Model.Author;
            return $"Reply by {author}, {Model.CreationDate.ToString("g", CultureInfo.CurrentCulture)}: {Model.Contents}";
        }
    }

    internal void RaiseAccessibleName() => OnPropertyChanged(nameof(AccessibleName));

    [RelayCommand]
    private void StartEdit()
    {
        if (!IsOwn) return;
        EditText = Model.Contents;
        IsEditing = true;
    }

    private bool CanSaveEdit() => !string.IsNullOrWhiteSpace(EditText);

    /// <summary>Keeps the new text (Enter or Ctrl+Enter); one step in the undo history.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private void SaveEdit()
    {
        Thread.Owner.EditReply(Model, EditText);
        IsEditing = false;
    }

    /// <summary>Leaves the reply as it was (Esc).</summary>
    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void Delete()
    {
        if (IsOwn) Thread.Owner.DeleteAnnotation(Model);
    }
}
