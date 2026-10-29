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

    // ------------------------------------------------------------------ shapes and text boxes (phase 3)

    [Fact]
    public async Task Shapes_FromDrags_AreSavedWithAppearances_WithUndoAndRedo()
    {
        byte[] pdf = TextPage();
        await using var doc = await Open(pdf);
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        var before = await doc.RenderPageAsync(2, 1);

        // Drags on page 2 (shown points, y down), dragged either way.
        var rect = session.AddShape(ViewerAnnotationKind.Square, 2, new ViewerPoint(300, 200), new ViewerPoint(100, 100));
        var ellipse = session.AddShape(ViewerAnnotationKind.Circle, 2, new ViewerPoint(100, 300), new ViewerPoint(250, 400));
        var line = session.AddLine(2, new ViewerPoint(100, 500), new ViewerPoint(300, 500), arrow: false);
        // An arrow dragged past the page's edge ends on the page.
        var arrow = session.AddLine(2, new ViewerPoint(100, 600), new ViewerPoint(700, 700), arrow: true);
        Assert.NotNull(rect);
        Assert.NotNull(ellipse);
        Assert.NotNull(line);
        Assert.NotNull(arrow);
        Assert.Equal(new ViewerRect(100, 100, 200, 100), doc.Geometry[1].ToShown(rect.Box));
        Assert.Equal(ViewerEditSession.ShapeWidth, rect.StrokeWidth);
        Assert.Equal(new double[] { 100, 292, 300, 292 }, line.Vertices);
        Assert.Equal(("None", "None"), (line.StartEnding, line.EndEnding));
        Assert.Equal(("None", "OpenArrow"), (arrow.StartEnding, arrow.EndEnding));
        Assert.Equal(612, arrow.Vertices[2], 6);
        Assert.Equal(792 - 700, arrow.Vertices[3], 6);
        // The line's box holds the line and its width; the arrow's box holds the arrowhead.
        Assert.True(line.Box.Y1 < 292 && line.Box.Y2 > 292);
        Assert.True(arrow.Box.X1 < 100 - ViewerEditSession.ShapeWidth);

        // A drag shorter than a few points adds nothing (a tap with a shape tool, a thin rectangle).
        Assert.Null(session.AddShape(ViewerAnnotationKind.Square, 2, new ViewerPoint(50, 50), new ViewerPoint(52, 51)));
        Assert.Null(session.AddShape(ViewerAnnotationKind.Circle, 2, new ViewerPoint(50, 50), new ViewerPoint(150, 52)));
        Assert.Null(session.AddLine(2, new ViewerPoint(50, 50), new ViewerPoint(52, 52), arrow: true));
        Assert.Null(session.AddLine(2, new ViewerPoint(double.NaN, 50), new ViewerPoint(80, 80), arrow: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AddShape(ViewerAnnotationKind.Line, 2, new ViewerPoint(0, 0), new ViewerPoint(50, 50)));
        Assert.Equal(4, session.Store.Current.Count);

        // A tap finds each; the list names them.
        Assert.Equal(rect.Id, session.AnnotationAt(2, new ViewerPoint(150, 150), slack: 2)?.Id);
        Assert.Equal(line.Id, session.AnnotationAt(2, new ViewerPoint(200, 501), slack: 2)?.Id);
        Assert.Equal(["Rectangle", "Ellipse", "Line", "Line"], session.List().Select(i => i.Label));

        // Written into the document and drawn: the page is no longer blank.
        Assert.True((await session.ApplyAsync()).Written);
        var after = await doc.RenderPageAsync(2, 1);
        Assert.False(before.Pixels.AsSpan().SequenceEqual(after.Pixels));

        // Save a copy: an incremental update with real annotations and their appearance streams.
        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        Assert.True(StartsWith(save.Bytes, pdf));
        using (var vector = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(save.Bytes))
        {
            var read = PdfEngine.Vector.Services.PdfAnnotationReader.Read(vector, 2);
            Assert.Equal(["Square", "Circle", "Line", "Line"], read.Select(a => a.Subtype));
            Assert.All(read, a => Assert.True(a.HasAppearance));
            Assert.All(read, a => Assert.Equal(ViewerEditSession.ShapeWidth, a.BorderWidth));
            Assert.Null(read[2].LineEndings);
            Assert.Equal(["None", "OpenArrow"], read[3].LineEndings!);
            Assert.Equal(new double[] { 100, 292, 300, 292 }, read[2].Line);
        }
        session.EndSave(save, saved: true);
        Assert.False(session.IsDirty);

        // Read back as the app reads a file it opens.
        var back = await ReadBack(save.Bytes);
        Assert.Equal([ViewerAnnotationKind.Square, ViewerAnnotationKind.Circle, ViewerAnnotationKind.Line, ViewerAnnotationKind.Line], back.Select(a => a.Kind));
        Assert.Equal(("None", "OpenArrow"), (back[3].StartEnding, back[3].EndEnding));
        Assert.Equal(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Square), back[0].Color);

        // Undo takes them back one at a time, newest first; redo makes them again.
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Equal([rect.Id, ellipse.Id], session.Store.Current.Select(a => a.Id));
        Assert.True(session.Redo());
        Assert.Equal([rect.Id, ellipse.Id, line.Id], session.Store.Current.Select(a => a.Id));
        // Delete the chosen one (as the Delete button does), then undo the delete.
        Assert.True(session.Delete(ellipse.Id));
        Assert.True((await session.ApplyAsync()).Written);
        Assert.Equal(["Square", "Line"], (await ReadBack(doc.Bytes)).Select(a => ViewerAnnotationPdf.Subtype(a.Kind)));
        Assert.True(session.Undo());
        Assert.True((await session.ApplyAsync()).Written);
        Assert.Equal(3, (await ReadBack(doc.Bytes)).Count);
    }

    [Fact]
    public async Task TextBoxes_FitTheirText_StayOnThePage_AndAreSavedWithAnAppearance()
    {
        byte[] pdf = TextPage();
        await using var doc = await Open(pdf);
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        var g = doc.Geometry[1];

        // No text, no text box.
        Assert.Null(session.AddFreeText(2, new ViewerPoint(100, 100), "   \n "));

        var box = session.AddFreeText(2, new ViewerPoint(100, 100), "Check the total");
        Assert.NotNull(box);
        var shown = g.ToShown(box.Box);
        Assert.Equal(100, shown.X, 6);
        Assert.Equal(100, shown.Y, 6);
        Assert.Equal(ViewerEditSession.FreeTextWidth, shown.Width, 6);
        Assert.Equal(ViewerEditSession.FreeTextSize, box.FontSize);
        Assert.Equal("#000000", box.Color);
        Assert.Equal(0, box.StrokeWidth);

        // Near the bottom-right corner it stays on the page; a longer text makes a taller box.
        var corner = session.AddFreeText(2, new ViewerPoint(600, 790), string.Join("\n", Enumerable.Repeat("A line of the text", 4)));
        Assert.NotNull(corner);
        var c = g.ToShown(corner.Box);
        Assert.True(c.X + c.Width <= 612 + 1e-6 && c.Y + c.Height <= 792 + 1e-6 && c.X >= 0 && c.Y >= 0);
        Assert.True(c.Height > shown.Height * 3);

        // A tap finds it; new text resizes it; empty text is refused (delete it instead).
        Assert.Equal(box.Id, session.AnnotationAt(2, new ViewerPoint(110, 105), slack: 2)?.Id);
        Assert.True(session.SetContents(box.Id, new string('x', 200)));
        Assert.True(g.ToShown(session.Store.Find(box.Id)!.Box).Height > shown.Height);
        Assert.Equal(100, g.ToShown(session.Store.Find(box.Id)!.Box).Y, 6);
        Assert.Throws<ViewerAnnotationException>(() => session.SetContents(box.Id, ""));
        Assert.True(session.Undo());
        Assert.Equal("Check the total", session.Store.Find(box.Id)!.Contents);

        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        Assert.True(StartsWith(save.Bytes, pdf));
        using (var vector = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(save.Bytes))
        {
            var read = PdfEngine.Vector.Services.PdfAnnotationReader.Read(vector, 2);
            Assert.Equal(["FreeText", "FreeText"], read.Select(a => a.Subtype));
            Assert.All(read, a => Assert.True(a.HasAppearance));
            Assert.Equal("Check the total", read[0].Contents);
            Assert.Contains("12 Tf", read[0].DefaultAppearance);
        }
        session.EndSave(save, saved: true);

        var back = await ReadBack(save.Bytes);
        Assert.Equal([ViewerAnnotationKind.FreeText, ViewerAnnotationKind.FreeText], back.Select(a => a.Kind));
        Assert.Equal(("Check the total", "#000000", 12.0), (back[0].Contents, back[0].Color, back[0].FontSize));
        Assert.Equal(["Text box", "Text box"], session.List().Select(i => i.Label));

        // Undo removes the newest; redo puts it back.
        Assert.True(session.Undo());
        Assert.Single(session.Store.Current);
        Assert.True(session.Redo());
        Assert.Equal(2, session.Store.Current.Count);
    }

    /// <summary>A Letter page turned by <paramref name="rotate"/> (/Rotate).</summary>
    private static byte[] TurnedPage(int rotate)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: $"/Rotate {rotate}");
        return b.Build();
    }

    /// <summary>Where the glyphs of page <paramref name="page"/>'s annotations start, in shown-page points.</summary>
    private static async Task<List<(double X, double Y)>> GlyphOrigins(byte[] pdf, int page, ViewerPageGeometry g)
    {
        using var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(page);
        return list.Commands.OfType<PdfEngine.Vector.DrawGlyphRun>()
            .SelectMany(c => c.Run.Glyphs.Select(gl => c.Run.TextToPage.Transform(gl.OffsetX, gl.OffsetY)))
            .Select(p => g.ToShown(p.X, p.Y)).ToList();
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TextBoxes_OnTurnedPages_ReadUprightAsShown(int rotate)
    {
        byte[] pdf = TurnedPage(rotate);
        await using var doc = await Open(pdf);
        var session = await ViewerEditSession.StartAsync(doc);
        var g = doc.Geometry[0];
        Assert.Equal(rotate, g.Rotation);

        var box = session.AddFreeText(1, new ViewerPoint(100, 100), "Please review the totals");
        Assert.NotNull(box);
        var shown = g.ToShown(box.Box);
        Assert.Equal(ViewerEditSession.FreeTextWidth, shown.Width, 6);
        Assert.True(shown.Height < 30, $"one line, {shown.Height} pt tall");

        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        // The text is one line on the shown page: left to right, on one baseline, inside the box.
        var origins = await GlyphOrigins(save.Bytes, 1, g);
        Assert.True(origins.Count >= 20, $"{origins.Count} glyphs");
        Assert.All(origins, p => Assert.InRange(p.Y, shown.Y, shown.Y + shown.Height));
        Assert.True(origins.Max(p => p.Y) - origins.Min(p => p.Y) < 0.5, "one baseline");
        Assert.All(origins, p => Assert.InRange(p.X, shown.X, shown.X + shown.Width));
        Assert.Equal(origins.OrderBy(p => p.X).ToList(), origins);
        using (var vector = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(save.Bytes))
            Assert.Equal(rotate, PdfEngine.Vector.Services.PdfAnnotationReader.Read(vector, 1)[0].AppearanceRotation, 3);
        session.EndSave(save, saved: true);
    }

    [Fact]
    public async Task TextBoxes_FromTheWebViewersStore_ReadUprightOnATurnedPage()
    {
        // The web viewer (and the embed API) add annotations to the store and write them with the same engine.
        await using var doc = await Open(TurnedPage(90));
        var g = doc.Geometry[0];
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        var rect = g.ToUser(new ViewerRect(100, 100, 200, 40));
        store.Add([new ViewerAnnotation { Id = "t1", Page = 1, Kind = ViewerAnnotationKind.FreeText, Box = rect, Color = "#000000", StrokeWidth = 0, Contents = "Upright on the screen" }]);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, []);
        var origins = await GlyphOrigins(doc.Bytes, 1, g);
        Assert.NotEmpty(origins);
        Assert.True(origins.Max(p => p.Y) - origins.Min(p => p.Y) < 0.5, "one baseline as shown");
        Assert.Equal(origins.OrderBy(p => p.X).ToList(), origins);
    }

    [Fact]
    public async Task TextBoxes_AreAsTallAsTheirDrawnLines_AndRefuseCharactersNoFontHas()
    {
        await using var doc = await Open(TextPage());
        var session = await ViewerEditSession.StartAsync(doc);
        var g = doc.Geometry[1];
        // 25 Chinese characters wrap onto 2 lines at 220 points; 64 capital Ms onto 4 lines.
        foreach (var (text, lines) in new[] { (string.Concat(Enumerable.Repeat("一二三四五", 5)), 2), (new string('M', 64), 4) })
        {
            var a = session.AddFreeText(2, new ViewerPoint(50, 50), text);
            Assert.NotNull(a);
            var layout = PdfEngine.Vector.Annotations.PdfAnnotationAppearances.MeasureFreeText(text, "Helv", 12, 220)!.Value;
            Assert.Equal(lines, layout.Lines);
            Assert.Equal(layout.Height + 0.5, g.ToShown(a.Box).Height, 6);
        }

        // A character no font on the device has would be left out of the box: it is refused, nothing is added.
        int before = session.Store.Current.Count;
        var ex = Assert.Throws<ViewerAnnotationException>(() => session.AddFreeText(2, new ViewerPoint(50, 300), "Total \U000F0000"));
        Assert.Contains("no font on this device", ex.Message);
        Assert.Equal(before, session.Store.Current.Count);
        var first = session.Store.Current[0];
        Assert.Throws<ViewerAnnotationException>(() => session.SetContents(first.Id, "\U000F0000"));
        Assert.NotEqual("\U000F0000", session.Store.Find(first.Id)!.Contents);
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
        // One save at a time: the app does not open the save dialog while one runs.
        Assert.True(session.IsSaving);
        Assert.Null(await session.BeginSaveAsync());
        session.EndSave(save, saved: false);
        Assert.True(session.IsDirty); // the copy was not written
        Assert.False(session.IsSaving);
        var again = await session.BeginSaveAsync();
        session.EndSave(again!, saved: true);
        Assert.False(session.IsDirty);
    }
}
