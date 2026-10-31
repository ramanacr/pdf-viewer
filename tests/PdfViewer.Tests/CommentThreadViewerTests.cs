using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Services;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Comment threads in the desktop viewer (ISO 32000-2 12.5.6.4): replies listed under the
/// comment they answer and never drawn on the page, editing and deleting them, review statuses
/// written as Acrobat writes them, the undo history over all of it, and saving and reopening.
/// Every PDF is built here; the "Acrobat" file is written with the engine the way Acrobat
/// writes replies and statuses (/IRT, /RT /R, /State and /StateModel, the flags it uses).
/// </summary>
public class CommentThreadViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CommentThreads_" + Guid.NewGuid().ToString("N"));

    public CommentThreadViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string NewPdf() => TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, $"threads_{Guid.NewGuid():N}.pdf"), 1, "ThreadToken");

    private static async Task<MainViewModel> Open(string path, string author = "Ann")
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        vm.SelectedAnnotationAuthor = author;
        return vm;
    }

    private static AnnotationModel Note(string text, string author = "Ann") => new()
    {
        PageNumber = 1,
        Type = AnnotationType.Note,
        X = 0.1, Y = 0.1, Width = 0.04, Height = 0.03,
        ColorHex = "#FFFF00",
        Opacity = 1,
        Contents = text,
        Author = author,
        Title = "Sticky Note",
    };

    private static System.Collections.Generic.List<PdfAnnotation> ReadPage(string path)
    {
        using var doc = PdfVectorDocument.OpenAsync(File.ReadAllBytes(path)).GetAwaiter().GetResult();
        return PdfAnnotationReader.Read(doc, 1).ToList();
    }

    // ------------------------------------------------------------------ replying

    [Fact]
    public async Task AReply_IsListedUnderItsComment_IsNotDrawnOnThePage_AndUndoRedoTakeItBackAndForth()
    {
        using var vm = await Open(NewPdf());
        var note = Note("Please check the figures.");
        vm.AddAnnotation(note);
        var thread = Assert.Single(vm.CommentThreads);
        Assert.Same(note, thread.Root);
        Assert.False(thread.HasReplies);

        vm.SelectedAnnotationAuthor = "Bob";
        var reply = vm.ReplyToComment(note, "  Checked, they add up.  ");
        Assert.NotNull(reply);
        Assert.Equal("Checked, they add up.", reply!.Contents);
        Assert.Equal("Bob", reply.Author);
        Assert.Equal(note.Id, reply.InReplyToId);
        Assert.Equal(AnnotationType.Note, reply.Type);
        Assert.Equal(1, reply.PageNumber);
        Assert.Contains(reply, vm.AllAnnotations);
        Assert.Equal(new[] { note }, vm.Pages[0].AnnotationsOnPage); // not drawn on the page
        Assert.Same(thread, Assert.Single(vm.CommentThreads));       // the same thread, with its reply
        var listed = Assert.Single(thread.Replies);
        Assert.Same(reply, listed.Model);
        Assert.True(listed.IsOwn);
        Assert.Equal("1 reply", thread.ReplyCountText);
        Assert.Equal("_Undo Add Reply", vm.UndoHeader);
        Assert.True(vm.HasUnsavedChanges);

        await vm.UndoAsync();
        Assert.DoesNotContain(reply, vm.AllAnnotations);
        Assert.Empty(Assert.Single(vm.CommentThreads).Replies);

        await vm.RedoAsync();
        Assert.Same(reply, Assert.Single(Assert.Single(vm.CommentThreads).Replies).Model);
        Assert.Equal(new[] { note }, vm.Pages[0].AnnotationsOnPage);

        Assert.Null(vm.ReplyToComment(note, "   "));  // nothing to say
        Assert.Null(vm.ReplyToComment(Note("not in the document"), "hello"));
    }

    [Fact]
    public async Task ThePanelsReplyBox_PostsAReply_AndCancelThrowsTheTextAway()
    {
        using var vm = await Open(NewPdf());
        var note = Note("Shorten this paragraph?");
        vm.AddAnnotation(note);
        var thread = Assert.Single(vm.CommentThreads);
        thread.IsExpanded = false;

        thread.StartReplyCommand.Execute(null);
        Assert.True(thread.IsReplying);
        Assert.True(thread.IsExpanded); // replying opens the thread
        Assert.False(thread.PostReplyCommand.CanExecute(null));
        thread.ReplyText = "Yes, by half.";
        Assert.True(thread.PostReplyCommand.CanExecute(null));
        thread.PostReplyCommand.Execute(null);
        Assert.False(thread.IsReplying);
        Assert.Equal(string.Empty, thread.ReplyText);
        Assert.Equal("Yes, by half.", Assert.Single(thread.Replies).Model.Contents);

        thread.StartReplyCommand.Execute(null);
        thread.ReplyText = "never mind";
        thread.CancelReplyCommand.Execute(null);
        Assert.False(thread.IsReplying);
        Assert.Single(thread.Replies);

        // The page's menu starts a reply in the panel: the comments tab opens at the thread.
        vm.SelectedSidebarTab = 0;
        vm.SelectAnnotation(note);
        vm.ReplyToSelectedAnnotationCommand.Execute(null);
        Assert.True(vm.IsSidebarOpen);
        Assert.Equal(MainViewModel.AnnotationsTabIndex, vm.SelectedSidebarTab);
        Assert.True(thread.IsReplying);

        thread.IsExpanded = false;
        Assert.False(thread.ShowsReplies);
        Assert.Equal("Show 1 reply", thread.ToggleText);
        thread.ToggleExpandedCommand.Execute(null);
        Assert.True(thread.ShowsReplies);
        Assert.Equal("Hide 1 reply", thread.ToggleText);
    }

    [Fact]
    public async Task OwnRepliesCanBeEdited_OthersCannot_AndUndoPutsTheTextBack()
    {
        using var vm = await Open(NewPdf(), author: "Bob");
        var note = Note("Wrong date on page 1.");
        vm.AddAnnotation(note);
        var reply = vm.ReplyToComment(note, "Fixed.")!;

        Assert.True(vm.EditReply(reply, "Fixed in the second draft."));
        Assert.Equal("Fixed in the second draft.", reply.Contents);
        Assert.Equal("_Undo Edit Reply", vm.UndoHeader);
        Assert.False(vm.EditReply(reply, "Fixed in the second draft.")); // unchanged
        Assert.False(vm.EditReply(reply, "  "));

        await vm.UndoAsync();
        Assert.Equal("Fixed.", reply.Contents);
        await vm.RedoAsync();
        Assert.Equal("Fixed in the second draft.", reply.Contents);

        // Through the panel: Edit, new text, Save.
        var listed = Assert.Single(Assert.Single(vm.CommentThreads).Replies);
        listed.StartEditCommand.Execute(null);
        Assert.True(listed.IsEditing);
        Assert.Equal("Fixed in the second draft.", listed.EditText);
        listed.EditText = "Fixed in draft 3.";
        listed.SaveEditCommand.Execute(null);
        Assert.False(listed.IsEditing);
        Assert.Equal("Fixed in draft 3.", reply.Contents);

        // Someone else's reply is not the user's to change.
        vm.SelectedAnnotationAuthor = "Carol";
        var asCarol = Assert.Single(Assert.Single(vm.CommentThreads).Replies);
        Assert.False(asCarol.IsOwn);
        Assert.False(vm.EditReply(reply, "Carol's words"));
        asCarol.StartEditCommand.Execute(null);
        Assert.False(asCarol.IsEditing);
        asCarol.DeleteCommand.Execute(null);
        Assert.Contains(reply, vm.AllAnnotations);
    }

    [Fact]
    public async Task DeletingAComment_AsksFirst_TakesItsRepliesAndStatusesWithIt_AndUndoBringsThemAllBack()
    {
        using var vm = await Open(NewPdf());
        var note = Note("Is this figure current?");
        vm.AddAnnotation(note);
        var first = vm.ReplyToComment(note, "It is from 2024.")!;
        var nested = vm.ReplyToComment(first, "Then it needs updating.")!;
        var status = vm.SetReviewState(note, "Accepted")!;

        string? asked = null;
        vm.ConfirmFunc = (message, _) => { asked = message; return false; };
        vm.DeleteAnnotation(note);
        Assert.Equal("Delete this comment and its 2 replies?", asked);
        Assert.Equal(4, vm.AllAnnotations.Count); // the user said no

        vm.ConfirmFunc = (_, _) => true;
        vm.DeleteAnnotation(note);
        Assert.Empty(vm.AllAnnotations);
        Assert.Empty(vm.CommentThreads);
        Assert.Empty(vm.Pages[0].AnnotationsOnPage);
        Assert.Equal("_Undo Delete Note and Replies", vm.UndoHeader);

        await vm.UndoAsync();
        Assert.Equal(new[] { note, first, nested, status }, vm.AllAnnotations);
        Assert.Equal(new[] { note }, vm.Pages[0].AnnotationsOnPage);
        var thread = Assert.Single(vm.CommentThreads);
        Assert.Equal(new[] { first, nested }, thread.Replies.Select(r => r.Model));
        Assert.Equal(new[] { 1, 2 }, thread.Replies.Select(r => r.Depth));
        Assert.Equal("Accepted", thread.ReviewState);

        await vm.RedoAsync();
        Assert.Empty(vm.AllAnnotations);
        await vm.UndoAsync();

        // A reply with no replies of its own goes without asking.
        asked = null;
        vm.ConfirmFunc = (message, _) => { asked = message; return false; };
        Assert.Single(vm.CommentThreads).Replies[1].DeleteCommand.Execute(null);
        Assert.Null(asked);
        Assert.DoesNotContain(nested, vm.AllAnnotations);
        Assert.Equal("_Undo Delete Reply", vm.UndoHeader);
    }

    [Fact]
    public async Task ReviewStatus_IsTheLatestSet_IsNotCountedAsAReply_AndNoneClearsIt()
    {
        using var vm = await Open(NewPdf(), author: "Bob");
        var note = Note("Approve the new logo?");
        vm.AddAnnotation(note);
        var thread = Assert.Single(vm.CommentThreads);
        Assert.Null(thread.ReviewState);

        Assert.Null(vm.SetReviewState(note, "Approved")); // not a Review state
        var accepted = vm.SetReviewState(note, "accepted");
        Assert.NotNull(accepted);
        Assert.Equal("Accepted", accepted!.State);
        Assert.Equal("Review", accepted.StateModel);
        Assert.True(accepted.IsStateChange);
        Assert.Equal(note.Id, accepted.InReplyToId);
        Assert.Equal("Accepted set by Bob", accepted.Contents);
        Assert.Equal("Bob", accepted.Author);
        Assert.Equal("Accepted", thread.ReviewState);
        Assert.Equal("Accepted", vm.ReviewStateOf(note));
        Assert.Contains("Accepted set by Bob", thread.ReviewHistory);
        Assert.Empty(thread.Replies);                 // a status is not a reply
        Assert.DoesNotContain(accepted, vm.Pages[0].AnnotationsOnPage);
        Assert.Equal("_Undo Set Status Accepted", vm.UndoHeader);
        Assert.Null(vm.SetReviewState(note, "Accepted")); // already its status

        thread.SetStatusCommand.Execute("Rejected");
        Assert.Equal("Rejected", thread.ReviewState);
        vm.SelectAnnotation(note);
        vm.SetSelectedAnnotationStatusCommand.Execute("Completed");
        Assert.Equal("Completed", thread.ReviewState);
        vm.SetReviewState(note, "None");
        Assert.Null(thread.ReviewState);
        Assert.Equal("_Undo Clear Status", vm.UndoHeader);

        await vm.UndoAsync();
        Assert.Equal("Completed", thread.ReviewState);
        await vm.UndoAsync();
        await vm.UndoAsync();
        Assert.Equal("Accepted", thread.ReviewState);
        await vm.UndoAsync();
        Assert.Null(thread.ReviewState);
        Assert.Single(vm.AllAnnotations);
    }

    // ------------------------------------------------------------------ saving and reopening

    [Fact]
    public async Task Saving_WritesRepliesAsStandardPdfReplies_AndReopeningShowsTheSameThread()
    {
        string path = NewPdf();
        using (var vm = await Open(path))
        {
            var note = Note("Check the totals.");
            vm.AddAnnotation(note);
            vm.SelectedAnnotationAuthor = "Bob";
            var reply = vm.ReplyToComment(note, "Totals checked.")!;
            vm.SetReviewState(note, "Accepted");
            vm.SelectedAnnotationAuthor = "Ann";
            vm.ReplyToComment(reply, "Thanks.");
            await vm.SaveAsync();
            Assert.False(vm.HasUnsavedChanges);
        }

        var saved = ReadPage(path);
        var parent = Assert.Single(saved, a => a.Subtype == "Text" && a.InReplyTo == 0);
        Assert.Equal("Check the totals.", parent.Contents);
        var reply1 = Assert.Single(saved, a => a.InReplyTo == parent.ObjectNumber && a.State == null);
        Assert.Equal("Text", reply1.Subtype);
        Assert.Equal("R", reply1.ReplyType);
        Assert.Equal("Totals checked.", reply1.Contents);
        Assert.Equal("Bob", reply1.Author);
        Assert.Equal(NativeDocumentServices.ReplyFlags, reply1.Flags); // Print, NoZoom, NoRotate: shown in lists, as Acrobat writes it
        Assert.Equal("Comment", reply1.Icon);
        Assert.True(reply1.HasAppearance); // an empty one: no second note icon over the comment
        var reply2 = Assert.Single(saved, a => a.InReplyTo == reply1.ObjectNumber);
        Assert.Equal("Thanks.", reply2.Contents);
        var status = Assert.Single(saved, a => a.InReplyTo == parent.ObjectNumber && a.State != null);
        Assert.Equal("Accepted", status.State);
        Assert.Equal("Review", status.StateModel);
        Assert.Equal("R", status.ReplyType);
        Assert.NotEqual(0, status.Flags & 2); // hidden, as Acrobat writes statuses
        Assert.Equal("Accepted set by Bob", status.Contents);
        // Readers find threads by /IRT (the web viewer with this same engine call).
        var roots = PdfAnnotationThreads.Roots(saved);
        Assert.Equal(parent.ObjectNumber, roots[reply1.ObjectNumber]);
        Assert.Equal(parent.ObjectNumber, roots[reply2.ObjectNumber]);
        Assert.Equal(parent.ObjectNumber, roots[status.ObjectNumber]);

        using (var reopened = await Open(path, author: "Bob"))
        {
            Assert.False(reopened.HasUnsavedChanges);
            var thread = Assert.Single(reopened.CommentThreads);
            Assert.Equal("Check the totals.", thread.Root.Contents);
            Assert.Equal(new[] { "Totals checked.", "Thanks." }, thread.Replies.Select(r => r.Model.Contents));
            Assert.Equal(new[] { 1, 2 }, thread.Replies.Select(r => r.Depth));
            Assert.Equal(new[] { "Bob", "Ann" }, thread.Replies.Select(r => r.Model.Author));
            Assert.Equal("Accepted", thread.ReviewState);
            Assert.Equal(new[] { thread.Root }, reopened.Pages[0].AnnotationsOnPage);

            // Editing a reply that was read from the file keeps it a reply.
            Assert.True(reopened.EditReply(thread.Replies[0].Model, "Totals checked twice."));
            await reopened.SaveAsync();
        }

        var edited = ReadPage(path);
        var editedParent = Assert.Single(edited, a => a.Subtype == "Text" && a.InReplyTo == 0);
        var editedReply = Assert.Single(edited, a => a.InReplyTo == editedParent.ObjectNumber && a.State == null);
        Assert.Equal("Totals checked twice.", editedReply.Contents);
        Assert.Equal(NativeDocumentServices.ReplyFlags, editedReply.Flags);
        Assert.Single(edited, a => a.InReplyTo == editedReply.ObjectNumber);

        using (var again = await Open(path))
        {
            again.ConfirmFunc = (_, _) => true;
            again.DeleteAnnotation(Assert.Single(again.CommentThreads).Root);
            await again.SaveAsync();
        }
        Assert.DoesNotContain(ReadPage(path), a => a.Subtype is "Text" or "Popup");
    }

    [Fact]
    public async Task ASignedDocument_GetsItsRepliesInAnIncrementalUpdate_AndReopensWithTheSameThread()
    {
        byte[] signed;
        using (var pki = new PdfEngine.Vector.Tests.Fixtures.TestPki())
        {
            byte[] pdf = PdfEngine.Vector.Tests.Fixtures.FormPdfFixture.Build();
            using var doc = await PdfVectorDocument.OpenAsync(pdf);
            var prepared = PdfEngine.Vector.Signatures.PdfSigner.Prepare(doc, pdf,
                new PdfEngine.Vector.Signatures.PdfSignatureRequest { SignerName = "Grace Hopper", Rect = new PdfEngine.Geometry.PdfRect(150, 20, 140, 40) });
            signed = prepared.Complete(await PdfEngine.Vector.Signatures.PdfCmsSigner.SignAsync(prepared.SignedBytes(), pki.Signer));
        }
        string path = Path.Combine(_dir, "signed.pdf");
        await File.WriteAllBytesAsync(path, signed);

        using (var vm = await Open(path))
        {
            var note = Note("Sign-off pending legal review.");
            vm.AddAnnotation(note);
            vm.ReplyToComment(note, "Legal has reviewed it.");
            vm.SetReviewState(note, "Completed");
            await vm.SaveAsync();
        }
        byte[] saved = await File.ReadAllBytesAsync(path);
        Assert.Equal(signed, saved.AsSpan(0, signed.Length).ToArray()); // the signed revision is untouched

        var page = ReadPage(path);
        var parent = Assert.Single(page, a => a.Subtype == "Text" && a.InReplyTo == 0);
        Assert.Equal("Legal has reviewed it.", Assert.Single(page, a => a.InReplyTo == parent.ObjectNumber && a.State == null).Contents);
        Assert.Equal("Completed", Assert.Single(page, a => a.InReplyTo == parent.ObjectNumber && a.State != null).State);

        using var reopened = await Open(path);
        var thread = Assert.Single(reopened.CommentThreads, t => t.Root.Type == AnnotationType.Note);
        Assert.Equal("Legal has reviewed it.", Assert.Single(thread.Replies).Model.Contents);
        Assert.Equal("Completed", thread.ReviewState);
    }

    [Fact]
    public async Task RepliesAndStatusesMadeInAcrobat_LoadIntoTheirThreads_AndASaveKeepsThem()
    {
        string path = NewPdf();
        byte[] original = await File.ReadAllBytesAsync(path);
        byte[] acrobat;
        using (var doc = await PdfVectorDocument.OpenAsync(original))
        {
            acrobat = PdfAnnotationEditor.Apply(doc, original, e =>
            {
                e.Add(new PdfAnnotation
                {
                    PageNumber = 1, Subtype = "Text", Name = "acro-parent", Rect = new PdfEngine.Geometry.PdfRect(100, 600, 20, 20),
                    Contents = "Typo in the heading.", Author = "Reviewer A", Icon = "Comment", CreationDate = "D:20261001100000Z",
                });
                e.Add(new PdfAnnotation
                {
                    PageNumber = 1, Subtype = "Text", Name = "acro-reply", InReplyToName = "acro-parent", ReplyType = "R", Flags = 28,
                    Rect = new PdfEngine.Geometry.PdfRect(100, 600, 20, 20), Contents = "Corrected.", Author = "Author B", Icon = "Comment",
                    CreationDate = "D:20261001110000Z",
                });
                e.Add(new PdfAnnotation
                {
                    PageNumber = 1, Subtype = "Text", Name = "acro-status", InReplyToName = "acro-parent", Flags = 30,
                    State = "Completed", StateModel = "Review", Rect = new PdfEngine.Geometry.PdfRect(100, 600, 20, 20),
                    Contents = "Completed set by Reviewer A", Author = "Reviewer A", CreationDate = "D:20261001120000Z",
                });
                e.Add(new PdfAnnotation
                {
                    PageNumber = 1, Subtype = "Text", Name = "acro-mark", InReplyToName = "acro-parent", Flags = 30,
                    State = "Marked", StateModel = "Marked", Rect = new PdfEngine.Geometry.PdfRect(100, 600, 20, 20),
                    Contents = "Marked set by Reviewer A", Author = "Reviewer A",
                });
                e.Add(new PdfAnnotation
                {
                    PageNumber = 1, Subtype = "Highlight", Name = "acro-highlight", Rect = new PdfEngine.Geometry.PdfRect(50, 690, 200, 20),
                    QuadPoints = { new double[] { 50, 710, 250, 710, 50, 690, 250, 690 } }, Color = new[] { 1.0, 1.0, 0.0 }, Author = "Reviewer A",
                });
                e.Add(new PdfAnnotation
                {
                    PageNumber = 1, Subtype = "Text", InReplyToName = "acro-highlight", Flags = 28,
                    Rect = new PdfEngine.Geometry.PdfRect(50, 690, 20, 20), Contents = "Why highlighted?", Author = "Author B",
                });
            });
        }
        await File.WriteAllBytesAsync(path, acrobat);

        using (var vm = await Open(path, author: "Author B"))
        {
            Assert.Equal(6, vm.AllAnnotations.Count);
            Assert.Equal(2, vm.CommentThreads.Count);
            var noteThread = Assert.Single(vm.CommentThreads, t => t.Root.Type == AnnotationType.Note);
            Assert.Equal("Typo in the heading.", noteThread.Root.Contents);
            var reply = Assert.Single(noteThread.Replies);
            Assert.Equal("Corrected.", reply.Model.Contents);
            Assert.True(reply.IsOwn);
            Assert.Equal("Completed", noteThread.ReviewState); // the Marked state is kept but is no review status
            var highlightThread = Assert.Single(vm.CommentThreads, t => t.Root.Type == AnnotationType.Highlight);
            Assert.Equal("Why highlighted?", Assert.Single(highlightThread.Replies).Model.Contents);
            // Only the comments themselves are on the page.
            Assert.Equal(2, vm.Pages[0].AnnotationsOnPage.Count);
            Assert.DoesNotContain(vm.Pages[0].AnnotationsOnPage, a => a.IsReply);

            // An unrelated change and a save keep every reply and status as Acrobat wrote it.
            vm.AddAnnotation(Note("Another point.", "Author B"));
            await vm.SaveAsync();
        }

        var saved = ReadPage(path);
        var parent = Assert.Single(saved, a => a.Name == "acro-parent");
        Assert.Equal(parent.ObjectNumber, Assert.Single(saved, a => a.Name == "acro-reply").InReplyTo);
        var status = Assert.Single(saved, a => a.Name == "acro-status");
        Assert.Equal(parent.ObjectNumber, status.InReplyTo);
        Assert.Equal(("Completed", "Review", 30), (status.State, status.StateModel, status.Flags));
        Assert.Equal("Marked", Assert.Single(saved, a => a.Name == "acro-mark").StateModel);
        var highlight = Assert.Single(saved, a => a.Name == "acro-highlight");
        Assert.Single(saved, a => a.InReplyTo == highlight.ObjectNumber);
    }

    // ------------------------------------------------------------------ the panel itself

    /// <summary>
    /// The comments panel as MainWindow.xaml has it (its event handlers left out), built over a
    /// document with a thread, in the light and the dark theme: it lays out, every button and
    /// box a keyboard user needs is there with a name, and the reply box keys are bound. With
    /// VIEWER_SNAPSHOT_DIR set it is also written out as a PNG, to look at.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task TheCommentsPanel_ShowsTheThread_WithNamedControls_InBothThemes(string theme)
    {
        // Disposed on the thread its views were bound on (closing the document clears the panel's lists).
        var vm = await Open(NewPdf(), author: "Bob");
        var note = Note("Please confirm the delivery date.");
        vm.AddAnnotation(note);
        vm.ReplyToComment(note, "Confirmed for 12 November.");
        vm.SetReviewState(note, "Accepted");
        Assert.Single(vm.CommentThreads).StartReplyCommand.Execute(null);

        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                string root = AppContext.BaseDirectory;
                while (!Directory.Exists(Path.Combine(root, "src", "PdfViewer"))) root = Path.GetDirectoryName(root)!;
                string Read(string relative) => File.ReadAllText(Path.Combine(root, "src", "PdfViewer", relative));
                string window = Read(Path.Combine("Views", "MainWindow.xaml"));
                int start = window.IndexOf("<ScrollViewer x:Name=\"CommentsPanel\"", StringComparison.Ordinal);
                Assert.True(start >= 0, "MainWindow.xaml has no comments panel");
                int end = window.IndexOf("</ScrollViewer>", start, StringComparison.Ordinal) + "</ScrollViewer>".Length;
                string panelXaml = window[start..end];
                // Code-behind handlers cannot be wired by XamlReader; the converters come from App.xaml.
                panelXaml = System.Text.RegularExpressions.Regex.Replace(panelXaml, @"\s(Click|MouseDoubleClick|IsVisibleChanged)=""[^""]*""", "");
                panelXaml = panelXaml.Replace("<ScrollViewer x:Name=\"CommentsPanel\"",
                    "<ScrollViewer xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
                    + "xmlns:vm=\"clr-namespace:PdfViewer.ViewModels;assembly=PdfViewer\" xmlns:conv=\"clr-namespace:PdfViewer.Converters;assembly=PdfViewer\" "
                    + "xmlns:themes=\"clr-namespace:PdfViewer.Themes;assembly=PdfViewer\" x:Name=\"CommentsPanel\"");
                panelXaml = panelXaml.Replace("<ScrollViewer.Resources>",
                    "<ScrollViewer.Resources><conv:BoolToVisibilityConverter x:Key=\"BoolToVis\" /><conv:BoolToVisibilityConverter x:Key=\"InverseBoolToVis\" Invert=\"True\" />"
                    + "<conv:HexToBrushConverter x:Key=\"HexToBrushConverter\" /><Style x:Key=\"MenuGlyph\" TargetType=\"TextBlock\" />");
                var panel = (System.Windows.Controls.ScrollViewer)System.Windows.Markup.XamlReader.Parse(panelXaml);

                var host = new System.Windows.Controls.Border { Width = 300, Height = 420, Child = panel, DataContext = vm };
                host.Resources.MergedDictionaries.Add((System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Read(Path.Combine("Themes", theme + "Theme.xaml"))));
                if (theme == "Dark")
                    host.Resources.MergedDictionaries.Add((System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Read(Path.Combine("Themes", "DarkControls.xaml"))));
                host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SidebarBgBrush");
                host.Measure(new System.Windows.Size(300, 420));
                host.Arrange(new System.Windows.Rect(0, 0, 300, 420));
                host.UpdateLayout();

                var all = Descendants(host).ToList();
                var buttons = all.OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => Shown(b, host)).ToList();
                string Name(System.Windows.DependencyObject d) =>
                    System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement((System.Windows.UIElement)d)?.GetName() ?? string.Empty;
                var names = buttons.Select(Name).ToList();
                Assert.Contains(names, n => n.StartsWith("Note on page 1 by Ann", StringComparison.Ordinal) && n.Contains("status Accepted") && n.Contains("1 reply"));
                foreach (string expected in new[] { "Reply", "Set status", "Edit", "Delete", "1 reply", "Post", "Cancel" })
                    Assert.Contains(expected, names);
                Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(Name(b)), "a button without a name"));
                var texts = all.OfType<System.Windows.Controls.TextBlock>().Where(t => Shown(t, host)).Select(t => t.Text).ToList();
                Assert.Contains("Confirmed for 12 November.", texts);
                Assert.Contains("Accepted", texts);
                var box = Assert.Single(all.OfType<System.Windows.Controls.TextBox>(), t => Shown(t, host));
                Assert.Equal("Reply to Ann", Name(box));
                // Enter and Ctrl+Enter post, Esc cancels; typing reaches the thread (the posting itself is tested above).
                var keys = box.InputBindings.OfType<System.Windows.Input.KeyBinding>().ToList();
                var threadVm = vm.CommentThreads[0];
                Assert.Contains(keys, k => k.Key == System.Windows.Input.Key.Enter && k.Modifiers == System.Windows.Input.ModifierKeys.None && ReferenceEquals(k.Command, threadVm.PostReplyCommand));
                Assert.Contains(keys, k => k.Key == System.Windows.Input.Key.Enter && k.Modifiers == System.Windows.Input.ModifierKeys.Control && ReferenceEquals(k.Command, threadVm.PostReplyCommand));
                Assert.Contains(keys, k => k.Key == System.Windows.Input.Key.Escape && ReferenceEquals(k.Command, threadVm.CancelReplyCommand));
                box.Text = "Thanks!";
                Assert.Equal("Thanks!", threadVm.ReplyText);
                Assert.True(threadVm.PostReplyCommand.CanExecute(null));

                if (Environment.GetEnvironmentVariable("VIEWER_SNAPSHOT_DIR") is { Length: > 0 } dir)
                {
                    host.UpdateLayout();
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 420, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(dir, $"comments-panel-{theme}.png"));
                    png.Save(fs);
                }
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                vm.Dispose();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "test timed out");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>The element and everything it is in, up to <paramref name="host"/>, are visible (IsVisible needs a window on screen).</summary>
    private static bool Shown(System.Windows.DependencyObject element, System.Windows.DependencyObject host)
    {
        for (var d = element; d != null && !ReferenceEquals(d, host); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.UIElement { Visibility: not System.Windows.Visibility.Visible }) return false;
        return true;
    }

    private static System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [Fact]
    public async Task ExportedXfdf_NamesRepliesAndTheCommentsTheyAnswer()
    {
        using var vm = await Open(NewPdf());
        var note = Note("Export me.");
        vm.AddAnnotation(note);
        var reply = vm.ReplyToComment(note, "With my reply.")!;
        var status = vm.SetReviewState(note, "Rejected")!;
        string xfdf = Path.Combine(_dir, "threads.xfdf");
        AnnotationExchange.ExportAnnotationsToXfdf(xfdf, vm.AllAnnotations);

        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var texts = XDocument.Load(xfdf).Descendants(ns + "text").ToList();
        Assert.Equal(3, texts.Count);
        string parentName = (string)texts[0].Attribute("name")!;
        Assert.Equal(note.Id.ToString("D"), parentName);
        Assert.Equal(parentName, (string?)texts[1].Attribute("inreplyto"));
        Assert.Equal("reply", (string?)texts[1].Attribute("replyType"));
        Assert.Equal(reply.Id.ToString("D"), (string?)texts[1].Attribute("name"));
        Assert.Null(texts[1].Attribute("state"));
        Assert.Equal(parentName, (string?)texts[2].Attribute("inreplyto"));
        Assert.Equal(("Rejected", "Review"), ((string?)texts[2].Attribute("state"), (string?)texts[2].Attribute("statemodel")));
        Assert.Equal(status.Id.ToString("D"), (string?)texts[2].Attribute("name"));
    }
}
