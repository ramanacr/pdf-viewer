using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The Android reader's edits (src/PdfViewer.Android, phase 2), run on the desktop: touches mapped
/// to page points, the form value rules (/MaxLen, read-only, options, states), the signed and
/// certified document rules, annotations made from touches and written into the file, undo and
/// redo, the annotation list, and a saved copy that is an incremental update.
/// </summary>
public class AndroidEditTests
{
    private static async Task<ViewerDocument> Open(byte[] pdf) => await ViewerDocument.OpenAsync(pdf);

    /// <summary>A Letter page with one line of Helvetica text.</summary>
    private static byte[] TextPage()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 24 Tf 72 700 Td (Annotate this line) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 612 792]");
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix) => bytes.Length > prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    // ------------------------------------------------------------------ touches to page points

    [Fact]
    public void Touches_MapToPagePoints_AndGapsAreOnNoPage()
    {
        var layout = new ContinuousPageLayout([new ViewerPage(1, 612, 792), new ViewerPage(2, 612, 792)], gap: 24, margin: 16);
        layout.Resize(1080, 1920);
        double s = layout.PixelsPerPoint;
        var page1 = layout.PageRectInView(1);

        // The middle of page 1, and its top-left corner.
        var mid = layout.PageAt(page1.X + 306 * s, page1.Y + 396 * s);
        Assert.NotNull(mid);
        Assert.Equal(1, mid.Value.Page);
        Assert.Equal(306, mid.Value.Point.X, 6);
        Assert.Equal(396, mid.Value.Point.Y, 6);
        Assert.Equal(new ViewerPoint(0, 0), layout.PageAt(page1.X, page1.Y)!.Value.Point);

        // The margin beside a page and the gap between pages are on no page.
        Assert.Null(layout.PageAt(page1.X - 4, page1.Y + 10));
        Assert.Null(layout.PageAt(page1.X + 10, page1.Bottom + 12));

        // Zoomed and scrolled: a point on page 2 maps back to the view point that ToView gives.
        layout.ZoomAt(3, 540, 960);
        layout.GoTo(2, 300);
        var target = new ViewerRect(100, 300, 0, 0);
        var v = layout.ToView(2, target);
        var hit = layout.PageAt(v.X, v.Y);
        Assert.Equal(2, hit!.Value.Page);
        Assert.Equal(100, hit.Value.Point.X, 6);
        Assert.Equal(300, hit.Value.Point.Y, 6);
        // Outside the page, ToPagePoint still maps (a selection handle dragged past the edge).
        Assert.True(layout.ToPagePoint(2, v.X - 1000, v.Y).X < 0);
    }

    // ------------------------------------------------------------------ form value rules

    private static ViewerFormWidget Widget(ViewerFormKind kind, int maxLength = 0, bool multiline = false, bool readOnly = false,
        bool editable = false, bool multiSelect = false, string? onState = null) =>
        new("f", kind, 1, new ViewerRect(10, 10, 100, 20), "", [""], [("A", "Apple"), ("B", "Banana")], onState, false, readOnly,
            multiline, false, maxLength, multiSelect, editable, "");

    [Fact]
    public void FieldValues_FollowMaxLen_ReadOnly_OptionsAndStates()
    {
        string? why;
        Assert.Equal(["12345"], ViewerEditSession.Normalize(Widget(ViewerFormKind.Text, maxLength: 5), ["123456789"], out why));
        Assert.Null(why);
        // One line loses its line breaks; several lines keep them.
        Assert.Equal(["a b c"], ViewerEditSession.Normalize(Widget(ViewerFormKind.Text), ["a\r\nb\nc"], out _));
        Assert.Equal(["a\nb"], ViewerEditSession.Normalize(Widget(ViewerFormKind.Text, multiline: true), ["a\nb"], out _));
        // Without a /MaxLen, the viewer's own limit.
        Assert.Equal(ViewerEditSession.MaxFieldValueLength, ViewerEditSession.Normalize(Widget(ViewerFormKind.Text), [new string('x', 20000)], out _)![0].Length);

        Assert.Null(ViewerEditSession.Normalize(Widget(ViewerFormKind.Text, readOnly: true), ["x"], out why));
        Assert.Equal("This field is read-only.", why);

        Assert.Equal(["Yes"], ViewerEditSession.Normalize(Widget(ViewerFormKind.CheckBox, onState: "Yes"), ["Yes"], out _));
        Assert.Equal(["Off"], ViewerEditSession.Normalize(Widget(ViewerFormKind.CheckBox, onState: "Yes"), ["Off"], out _));
        Assert.Null(ViewerEditSession.Normalize(Widget(ViewerFormKind.Radio, onState: "Red"), ["Blue"], out why));
        Assert.NotNull(why);

        Assert.Equal(["B"], ViewerEditSession.Normalize(Widget(ViewerFormKind.ComboBox), ["B"], out _));
        Assert.Null(ViewerEditSession.Normalize(Widget(ViewerFormKind.ComboBox), ["Cherry"], out why));
        Assert.Contains("options", why);
        Assert.Equal(["Cherry"], ViewerEditSession.Normalize(Widget(ViewerFormKind.ComboBox, editable: true), ["Cherry"], out _));

        Assert.Null(ViewerEditSession.Normalize(Widget(ViewerFormKind.ListBox), ["A", "B"], out why));
        Assert.Contains("one choice", why);
        Assert.Equal(["A", "B"], ViewerEditSession.Normalize(Widget(ViewerFormKind.ListBox, multiSelect: true), ["A", "B", "A"], out _));
        Assert.Null(ViewerEditSession.Normalize(Widget(ViewerFormKind.ListBox, multiSelect: true), ["A", "Z"], out _));
    }

    [Fact]
    public async Task FilledFields_AreWritten_Drawn_AndSavedAsAnIncrementalUpdate()
    {
        byte[] pdf = FormPdfFixture.Build();
        await using var doc = await Open(pdf);
        var session = await ViewerEditSession.StartAsync(doc);
        Assert.True(session.CanFillForms);
        var before = await doc.RenderPageAsync(1, 1);

        // A tap on the name field finds its widget (shown points: the field is 20 pt below the top).
        var name = session.WidgetAt(1, new ViewerPoint(100, 30));
        Assert.Equal("person.name", name?.Name);
        Assert.Null(session.WidgetAt(1, new ViewerPoint(290, 395)));
        Assert.True(session.SetFieldValue(name!, ["Ada Lovelace"], out _));
        Assert.False(session.SetFieldValue(name!, ["Ada Lovelace"], out var none)); // the same again: nothing to write
        Assert.Null(none);

        var zip = session.Widgets.Single(w => w.Name == "zip");
        Assert.True(session.SetFieldValue(zip, ["123456789"], out _));
        var agree = session.Widgets.Single(w => w.Name == "agree");
        Assert.True(session.SetFieldValue(agree, [agree.ToggleValue], out _));
        Assert.True(session.IsDirty);

        var result = await session.ApplyAsync();
        Assert.True(result.Written);
        Assert.Null(result.Refused ?? result.Failed);
        Assert.Equal("Ada Lovelace", session.Widgets.First(w => w.Name == "person.name").Value);
        Assert.Equal("12345", session.Widgets.Single(w => w.Name == "zip").Value);
        Assert.True(session.Widgets.Single(w => w.Name == "agree").IsOn);

        // The field's new appearance is drawn into the page.
        var after = await doc.RenderPageAsync(1, 1);
        Assert.False(before.Pixels.AsSpan().SequenceEqual(after.Pixels.AsSpan()));

        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        Assert.True(StartsWith(save.Bytes, pdf)); // the file as opened, then the update
        session.EndSave(save, saved: true);
        Assert.False(session.IsDirty);
        using var back = await PdfVectorDocument.OpenAsync(save.Bytes);
        var form = PdfAcroForm.Read(back)!;
        Assert.Equal("Ada Lovelace", form["person.name"]!.Value);
        Assert.Equal("12345", form["zip"]!.Value);
        Assert.True(form["agree"]!.IsChecked);
    }

    [Fact]
    public async Task ValuesRefusedByTheFieldsRules_GoBack_WithoutJavaScript()
    {
        await using var doc = await Open(ScriptedFormFixture.Build());
        var session = await ViewerEditSession.StartAsync(doc);
        var price = session.Widgets.Single(w => w.Name == "price");
        Assert.True(session.SetFieldValue(price, ["5000"], out _));
        var result = await session.ApplyAsync();
        Assert.NotNull(result.Refused);
        Assert.Null(session.ValueOf("price"));
        Assert.True(session.SetFieldValue(price, ["2.5"], out _));
        Assert.True(session.SetFieldValue(session.Widgets.Single(w => w.Name == "qty"), ["3"], out _));
        Assert.True((await session.ApplyAsync()).Written);
        Assert.Equal("7.5", session.Widgets.Single(w => w.Name == "total").Value);
    }

    // ------------------------------------------------------------------ signed and certified files

    private static async Task<byte[]> Signed(int? certificationLevel)
    {
        byte[] pdf = FormPdfFixture.Build();
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Author", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Author", CertificationLevel = certificationLevel });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
    }

    [Fact]
    public async Task CertifiedDocuments_KeepTheirRules()
    {
        // Level 1: no changes at all; no fields are offered and nothing can be added.
        await using (var locked = await Open(await Signed(1)))
        {
            var session = await ViewerEditSession.StartAsync(locked);
            Assert.False(session.CanFillForms || session.CanAnnotate);
            Assert.Empty(session.Widgets);
            var any = (await locked.GetFormWidgetsAsync()).First();
            Assert.False(session.SetFieldValue(any, ["x"], out var why));
            Assert.Contains("does not allow changes", why);
            Assert.Throws<ViewerAnnotationException>(() => session.AddNote(1, new ViewerPoint(50, 50), "n"));
        }

        // Level 2: form filling only, as a new revision; the reader is told once.
        byte[] signed = await Signed(2);
        await using var doc = await Open(signed);
        var s2 = await ViewerEditSession.StartAsync(doc);
        Assert.True(s2.CanFillForms);
        Assert.False(s2.CanAnnotate);
        Assert.Throws<ViewerAnnotationException>(() => s2.AddInk(1, [new ViewerPoint(10, 10), new ViewerPoint(50, 50)]));
        Assert.Null(s2.TakeNotice()); // nothing changed yet
        Assert.True(s2.SetFieldValue(s2.Widgets.First(w => w.Name == "person.name"), ["Grace"], out _));
        Assert.NotNull(s2.TakeNotice());
        Assert.Null(s2.TakeNotice());
        Assert.True((await s2.ApplyAsync()).Written);
        Assert.True(StartsWith(doc.Bytes, signed));
    }

    // ------------------------------------------------------------------ annotations

    [Fact]
    public async Task Annotations_FromTouches_RoundTripThroughTheFile_WithUndoAndRedo()
    {
        byte[] pdf = TextPage();
        await using var doc = await Open(pdf);
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        Assert.True(session.CanAnnotate);

        // A note near the page's right edge stays on the page.
        var note = session.AddNote(1, new ViewerPoint(605, 10), "Check this");
        var shown = doc.Geometry[0].ToShown(note.Box);
        Assert.Equal(612 - ViewerEditSession.NoteSize, shown.X, 6);
        Assert.Equal(ViewerEditSession.NoteSize, shown.Width, 6);
        Assert.Equal("Reader", note.Author);

        // A stroke: repeats closer than 0.75 pt are dropped; a dot is not a drawing.
        var ink = session.AddInk(2, [new ViewerPoint(100, 100), new ViewerPoint(100.2, 100.1), new ViewerPoint(150, 120), new ViewerPoint(200, 180)]);
        Assert.NotNull(ink);
        Assert.Equal(6, ink.InkList[0].Count);
        Assert.Null(session.AddInk(2, [new ViewerPoint(10, 10), new ViewerPoint(10.1, 10.1)]));

        // Highlight the line of text (selected as a long press and "All" would).
        var selection = new ViewerTextSelection(1, await doc.GetTextLayoutAsync(1));
        selection.SelectAll();
        var mark = session.MarkText(ViewerAnnotationKind.Highlight, selection);
        Assert.NotNull(mark);
        Assert.Equal(8, mark.QuadPoints.Count);

        // A tap finds the note (the smaller box under the finger) and the highlight.
        Assert.Equal(note.Id, session.AnnotationAt(1, new ViewerPoint(600, 15), slack: 2)?.Id);
        var band = selection.Bands[0];
        Assert.Equal(mark.Id, session.AnnotationAt(1, new ViewerPoint(band.X + 5, band.Y + band.Height / 2), slack: 2)?.Id);
        Assert.Null(session.AnnotationAt(1, new ViewerPoint(300, 600), slack: 2));

        Assert.True((await session.ApplyAsync()).Written);
        Assert.True(StartsWith(doc.Bytes, pdf));

        // The list: page order, labels and text.
        var list = session.List();
        Assert.Equal(["Note", "Highlight", "Drawing"], list.Select(i => i.Label));
        Assert.Equal("Check this", list[0].Summary);

        // The saved file has them, read back as the app reads a file it opens.
        await using (var back = await Open(doc.Bytes))
        {
            var again = await ViewerEditSession.StartAsync(back);
            Assert.Equal([ViewerAnnotationKind.Note, ViewerAnnotationKind.Highlight, ViewerAnnotationKind.Ink], again.Store.Current.Select(a => a.Kind));
            Assert.Equal("Check this", again.Store.Current[0].Contents);
            // A file's own annotation can be edited and deleted.
            Assert.True(again.SetContents(again.Store.Current[0].Id, "Checked"));
            Assert.True(again.Delete(again.Store.Current[2].Id));
            Assert.True((await again.ApplyAsync()).Written);
            await using var third = await Open(back.Bytes);
            var kept = await third.ReadAnnotationsAsync();
            Assert.Equal([ViewerAnnotationKind.Note, ViewerAnnotationKind.Highlight], kept.Select(a => a.Kind));
            Assert.Equal("Checked", kept[0].Contents);
        }

        // Undo takes back the last change, redo makes it again.
        Assert.True(session.Delete(note.Id));
        Assert.Equal(2, session.Store.Current.Count);
        Assert.True(session.Undo());
        Assert.Equal(3, session.Store.Current.Count);
        Assert.True(session.Redo());
        Assert.Equal(2, session.Store.Current.Count);
        Assert.True((await session.ApplyAsync()).Written);
        Assert.Equal(2, (await ReadBack(doc.Bytes)).Count);
    }

    private static async Task<IReadOnlyList<ViewerAnnotation>> ReadBack(byte[] bytes)
    {
        await using var doc = await Open(bytes);
        return await doc.ReadAnnotationsAsync();
    }

    [Fact]
    public async Task ASave_WaitsForTheLatestEdit_AndAFailedOneSavesNothing()
    {
        await using var doc = await Open(TextPage());
        var session = await ViewerEditSession.StartAsync(doc);
        Assert.False(session.IsDirty);
        // Not written yet: the save writes it first.
        session.AddNote(1, new ViewerPoint(50, 50), "Later");
        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        Assert.Single(await ReadBack(save.Bytes));
        // One save at a time.
        Assert.Null(await session.BeginSaveAsync());
        session.EndSave(save, saved: false);
        Assert.True(session.IsDirty); // the copy was not written
        var again = await session.BeginSaveAsync();
        session.EndSave(again!, saved: true);
        Assert.False(session.IsDirty);
    }
}
