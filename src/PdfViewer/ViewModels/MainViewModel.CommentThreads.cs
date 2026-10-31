using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PdfEngine.Vector.Annotations;
using PdfViewer.Models;

namespace PdfViewer.ViewModels;

/// <summary>
/// Comment threads (ISO 32000-2 12.5.6.4), as Acrobat makes them. A reply is a note with /IRT
/// to the comment it answers and /RT /R, listed under that comment in the comments panel and
/// not drawn on the page. A review status is a reply with /State and /StateModel /Review; the
/// latest one set is the comment's status. Replies and statuses are annotations like any other:
/// adding, editing and deleting them are steps in the document's undo history, and a save writes
/// them (the engine's annotation editor writes /IRT and the empty appearance of a reply).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The review statuses of Acrobat's Review model; "None" clears the status.</summary>
    public static IReadOnlyList<string> ReviewStates { get; } = new[] { "Accepted", "Rejected", "Cancelled", "Completed", "None" };

    /// <summary>The state model of review statuses.</summary>
    public const string ReviewStateModel = "Review";

    /// <summary>The comments panel: each comment that answers none, with its replies.</summary>
    public ObservableCollection<CommentThreadViewModel> CommentThreads { get; } = new();

    private readonly Dictionary<AnnotationModel, CommentThreadViewModel> _threads = new(ReferenceEqualityComparer.Instance);
    private int _threadsDeferred;

    /// <summary>Holds back bringing the comments panel up to date until the returned scope ends (many changes at once).</summary>
    private IDisposable DeferCommentThreads()
    {
        _threadsDeferred++;
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private MainViewModel? _owner;
        public Scope(MainViewModel owner) => _owner = owner;

        public void Dispose()
        {
            if (_owner is not { } owner) return;
            _owner = null;
            if (--owner._threadsDeferred == 0) owner.RefreshCommentThreads();
        }
    }

    private void OnAnnotationsChangedForThreads()
    {
        if (_threadsDeferred == 0) RefreshCommentThreads();
    }

    partial void OnSelectedAnnotationAuthorChanged(string value) => RefreshCommentThreads();

    // ------------------------------------------------------------------ the threads

    /// <summary>The comment <paramref name="annot"/> answers, or null (it answers none, or that comment is gone).</summary>
    private AnnotationModel? ParentOf(AnnotationModel annot, IReadOnlyDictionary<Guid, AnnotationModel> byId) =>
        annot.InReplyToId is { } id && byId.TryGetValue(id, out var parent) && !ReferenceEquals(parent, annot) ? parent : null;

    private Dictionary<Guid, AnnotationModel> AnnotationsById()
    {
        var byId = new Dictionary<Guid, AnnotationModel>();
        foreach (var a in AllAnnotations) byId.TryAdd(a.Id, a);
        return byId;
    }

    /// <summary>Each annotation's direct replies, in the document's order.</summary>
    private Dictionary<AnnotationModel, List<AnnotationModel>> RepliesByParent(IReadOnlyDictionary<Guid, AnnotationModel> byId)
    {
        var children = new Dictionary<AnnotationModel, List<AnnotationModel>>(ReferenceEqualityComparer.Instance);
        foreach (var a in AllAnnotations)
        {
            if (ParentOf(a, byId) is not { } parent) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<AnnotationModel>();
            list.Add(a);
        }
        return children;
    }

    /// <summary>The replies to <paramref name="annot"/>, and the replies to those, statuses included (a reply chain that loops ends).</summary>
    public IReadOnlyList<AnnotationModel> RepliesOf(AnnotationModel annot)
    {
        var children = RepliesByParent(AnnotationsById());
        var result = new List<AnnotationModel>();
        var seen = new HashSet<AnnotationModel>(ReferenceEqualityComparer.Instance) { annot };
        var pending = new Stack<AnnotationModel>();
        pending.Push(annot);
        while (pending.Count > 0)
        {
            if (!children.TryGetValue(pending.Pop(), out var list)) continue;
            foreach (var reply in list)
            {
                if (!seen.Add(reply)) continue;
                result.Add(reply);
                pending.Push(reply);
            }
        }
        return result;
    }

    /// <summary>The comment's review status: the latest Review status set on it ("None" clears it); null for none.</summary>
    public string? ReviewStateOf(AnnotationModel annot)
    {
        var byId = AnnotationsById();
        return LatestReviewState(RepliesByParent(byId).GetValueOrDefault(annot));
    }

    private static IEnumerable<AnnotationModel> ReviewStatesIn(List<AnnotationModel>? replies) =>
        replies == null ? Enumerable.Empty<AnnotationModel>()
        : replies.Where(r => r.IsStateChange && string.Equals(r.StateModel, ReviewStateModel, StringComparison.OrdinalIgnoreCase))
            .Select((r, i) => (r, i)).OrderBy(x => x.r.CreationDate).ThenBy(x => x.i).Select(x => x.r);

    private static string? LatestReviewState(List<AnnotationModel>? replies)
    {
        var latest = ReviewStatesIn(replies).LastOrDefault();
        return latest == null || string.IsNullOrEmpty(latest.State) || string.Equals(latest.State, "None", StringComparison.OrdinalIgnoreCase)
            ? null : latest.State;
    }

    /// <summary>The comment is the user's own: its author is the name the user comments under.</summary>
    public bool IsOwnComment(AnnotationModel annot) =>
        string.Equals((annot.Author ?? string.Empty).Trim(), (SelectedAnnotationAuthor ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The panel's thread for a comment (or for the comment a reply belongs to), or null.</summary>
    public CommentThreadViewModel? ThreadOf(AnnotationModel annot)
    {
        var byId = AnnotationsById();
        var root = annot;
        for (int i = 0; i <= PdfAnnotationLimits.MaxReplyDepth && ParentOf(root, byId) is { } parent; i++) root = parent;
        return _threads.GetValueOrDefault(root);
    }

    /// <summary>
    /// Brings the comments panel up to date with the annotations: one thread per comment that
    /// answers none (or whose comment is gone), its replies after the one each answers, oldest
    /// first. A thread already shown keeps its object, so what it shows open stays open.
    /// </summary>
    private void RefreshCommentThreads()
    {
        var byId = AnnotationsById();
        var children = RepliesByParent(byId);
        // A status whose comment is gone is no comment of its own: it is kept in the document, not listed.
        var roots = AllAnnotations.Where(a => ParentOf(a, byId) == null && !a.IsStateChange).ToList();
        var placed = new HashSet<AnnotationModel>(ReferenceEqualityComparer.Instance);
        // Replies whose chain never reaches a comment (it loops) are shown as comments of their own.
        foreach (var r in roots) placed.Add(r);

        var wanted = new List<CommentThreadViewModel>();
        void Build(AnnotationModel root)
        {
            if (!_threads.TryGetValue(root, out var thread)) _threads[root] = thread = new CommentThreadViewModel(this, root);
            var replies = new List<(AnnotationModel Model, int Depth)>();
            var pending = new Stack<(AnnotationModel Model, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                var (at, depth) = pending.Pop();
                if (depth > 0) replies.Add((at, depth));
                if (depth >= PdfAnnotationLimits.MaxReplyDepth || !children.TryGetValue(at, out var list)) continue;
                // Oldest first: pushed newest first, so the stack hands them back in order.
                foreach (var reply in list.Select((r, i) => (r, i)).OrderByDescending(x => x.r.CreationDate).ThenByDescending(x => x.i).Select(x => x.r))
                    if (!reply.IsStateChange && placed.Add(reply)) pending.Push((reply, depth + 1));
                    else if (reply.IsStateChange) placed.Add(reply);
            }
            Sync(thread.Replies, replies.Select(r =>
            {
                var vm = thread.Replies.FirstOrDefault(x => ReferenceEquals(x.Model, r.Model)) ?? new CommentReplyViewModel(thread, r.Model);
                vm.Depth = r.Depth;
                vm.IsOwn = IsOwnComment(r.Model);
                vm.ReviewState = LatestReviewState(children.GetValueOrDefault(r.Model));
                vm.RaiseAccessibleName();
                return vm;
            }).ToList());
            var states = ReviewStatesIn(children.GetValueOrDefault(root)).ToList();
            thread.ReviewState = LatestReviewState(children.GetValueOrDefault(root));
            thread.ReviewHistory = string.Join(Environment.NewLine, states.Select(s =>
                $"{s.State} set by {(string.IsNullOrWhiteSpace(s.Author) ? "unknown author" : s.Author)}, {s.CreationDate:g}"));
            thread.RaiseAccessibleName();
            wanted.Add(thread);
        }
        foreach (var root in roots) Build(root);
        // A reply chain that loops has no comment that answers none: its first reply starts a thread.
        foreach (var a in AllAnnotations)
            if (!placed.Contains(a) && !a.IsStateChange) { placed.Add(a); Build(a); }

        var shown = new HashSet<AnnotationModel>(wanted.Select(t => t.Root), ReferenceEqualityComparer.Instance);
        foreach (var gone in _threads.Keys.Where(k => !shown.Contains(k)).ToList()) _threads.Remove(gone);
        Sync(CommentThreads, wanted);
    }

    /// <summary>Makes <paramref name="target"/> hold exactly <paramref name="wanted"/>, in order, moving rather than replacing what both have (so focus and state in the panel stay).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            int at = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], wanted[i])) { at = j; break; }
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, wanted[i]);
        }
    }

    // ------------------------------------------------------------------ replying, editing, statuses

    /// <summary>
    /// Adds a reply to <paramref name="parent"/> (a comment or a reply) from the user, under the
    /// author name the user comments under: one step in the undo history. Null when there is no
    /// text, the parent is not in the document, or the document does not allow comments.
    /// </summary>
    public AnnotationModel? ReplyToComment(AnnotationModel parent, string text)
    {
        if (parent == null || string.IsNullOrWhiteSpace(text) || !AllAnnotations.Contains(parent)) return null;
        if (!Permit(DocumentPermissions.CanAnnotate, "replying to comments")) return null;
        // A thread is at most as deep as the engine reads: past that the reply answers the deepest it may.
        var byId = AnnotationsById();
        var target = parent;
        var chain = new List<AnnotationModel> { parent };
        for (var p = ParentOf(parent, byId); p != null && chain.Count <= PdfAnnotationLimits.MaxReplyDepth + 1; p = ParentOf(p, byId)) chain.Add(p);
        if (chain.Count > PdfAnnotationLimits.MaxReplyDepth) target = chain[^PdfAnnotationLimits.MaxReplyDepth];
        var reply = NewReply(target, text.Trim());
        RecordAnnotationChange("Add Reply", new() { new(reply, null, AnnotationState.Of(reply), AllAnnotations.Count, -1) });
        StatusText = $"Replied to the {AnnotationTypeName(RootOf(target, byId).Type).ToLowerInvariant()} on page {target.PageNumber}.";
        return reply;
    }

    private AnnotationModel RootOf(AnnotationModel annot, IReadOnlyDictionary<Guid, AnnotationModel> byId)
    {
        var root = annot;
        for (int i = 0; i <= PdfAnnotationLimits.MaxReplyDepth && ParentOf(root, byId) is { } parent; i++) root = parent;
        return root;
    }

    /// <summary>A reply note at its parent's place (Acrobat puts it there; it is not drawn).</summary>
    private AnnotationModel NewReply(AnnotationModel parent, string contents)
    {
        var now = DateTime.Now;
        double w = Math.Min(parent.Width, 24.0 / Math.Max(1, CropOf(parent.PageNumber).Width));
        double h = Math.Min(parent.Height, 24.0 / Math.Max(1, CropOf(parent.PageNumber).Height));
        return new AnnotationModel
        {
            PageNumber = parent.PageNumber,
            Type = AnnotationType.Note,
            X = parent.X,
            Y = parent.Y,
            Width = Math.Max(w, 1e-3),
            Height = Math.Max(h, 1e-3),
            ColorHex = "#FFFF00",
            Opacity = 1,
            StrokeThickness = 0,
            Contents = contents,
            Author = SelectedAnnotationAuthor,
            Title = string.Empty,
            CreationDate = now,
            ModifiedDate = now,
            InReplyToId = parent.Id,
        };
    }

    /// <summary>Changes the text of the user's own reply: one step in the undo history. False when it is not the user's, the text is empty or unchanged.</summary>
    public bool EditReply(AnnotationModel reply, string text)
    {
        if (reply == null || !reply.IsReply || reply.IsStateChange || !AllAnnotations.Contains(reply) || !IsOwnComment(reply)) return false;
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == reply.Contents) return false;
        if (!Permit(DocumentPermissions.CanAnnotate, "changing comments")) return false;
        reply.Contents = trimmed;
        bool changed = RecordInPlaceEdit(reply, "Edit Reply");
        if (changed) StatusText = "Edited the reply.";
        return changed;
    }

    /// <summary>
    /// Sets the review status of <paramref name="comment"/> as Acrobat does: a hidden reply with
    /// /State and /StateModel /Review, under the user's author name, saying who set it. "None"
    /// clears it. One step in the undo history. Null when the status is not one of
    /// <see cref="ReviewStates"/>, it is already the comment's status, or comments are not allowed.
    /// </summary>
    public AnnotationModel? SetReviewState(AnnotationModel comment, string state)
    {
        if (comment == null || !AllAnnotations.Contains(comment) || comment.IsStateChange) return null;
        string? known = ReviewStates.FirstOrDefault(s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase));
        if (known == null) return null;
        string? current = ReviewStateOf(comment);
        if (string.Equals(current ?? "None", known, StringComparison.Ordinal)) return null;
        if (!Permit(DocumentPermissions.CanAnnotate, "setting a comment's status")) return null;
        var status = NewReply(comment, $"{known} set by {SelectedAnnotationAuthor}");
        status.State = known;
        status.StateModel = ReviewStateModel;
        RecordAnnotationChange(known == "None" ? "Clear Status" : $"Set Status {known}",
            new() { new(status, null, AnnotationState.Of(status), AllAnnotations.Count, -1) });
        StatusText = known == "None" ? "Cleared the comment's status." : $"Set the comment's status to {known}.";
        return status;
    }

    /// <summary>Opens the comments panel at the comment's thread with its reply box open (the window puts the keyboard focus in it).</summary>
    public void BeginReply(AnnotationModel annot)
    {
        if (annot == null || ThreadOf(annot) is not { } thread) return;
        IsSidebarOpen = true;
        SelectedSidebarTab = AnnotationsTabIndex;
        thread.StartReplyCommand.Execute(null);
    }

    /// <summary>The comments panel's place among the sidebar's tabs.</summary>
    public const int AnnotationsTabIndex = 3;

    /// <summary>Starts a reply to the selected annotation (its context menu on the page).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ReplyToSelectedAnnotation()
    {
        if (SelectedAnnotation is { } a) BeginReply(a);
    }

    /// <summary>Sets the selected annotation's review status (its context menu on the page).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetSelectedAnnotationStatus(string? state)
    {
        if (SelectedAnnotation is { } a && !string.IsNullOrEmpty(state)) SetReviewState(a, state);
    }
}
