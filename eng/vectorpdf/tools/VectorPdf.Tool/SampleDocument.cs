using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Text;
using PdfEngine.Vector.Windows.Create;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf sampledoc [out.pdf] [--preview dir] [--dpi N]: writes samples/SampleDocument.pdf, the
/// document the app opens as its sample (File → Open Sample Demo Document) and the one the store
/// screenshots show. It is made with the engine's own API: the pages, outline and form with
/// <see cref="PdfDocumentWriter"/>, then the comments with <see cref="PdfAnnotationEditor"/>, which draws
/// their appearances as the app does. Text is set in the standard 14 fonts (nothing is embedded), so
/// the file is small and its text can be searched and selected. --preview renders every page to PNG.
/// </summary>
internal static class SampleDocument
{
    private const double W = 612, H = 792, Left = 54, Right = 558, Body = Right - Left;
    private const int PageCount = 7;

    /// <summary>The date written into the file, so the same code writes the same document.</summary>
    private static readonly DateTime Date = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    // The colours of the app icon (assets/app_icon.svg).
    private static readonly Rgb Crimson = Rgb.Hex(0xc8102e);
    private static readonly Rgb Blush = Rgb.Hex(0xf4c3cb);
    private static readonly Rgb Rose = Rgb.Hex(0xe17c8c);     // crimson at 55 % on white, as the icon's lines
    private static readonly Rgb Ink = Rgb.Hex(0x1f2328);
    private static readonly Rgb Grey = Rgb.Hex(0x8c939d);
    private static readonly Rgb BodyText = Rgb.Hex(0x3a3f46);
    private static readonly Rgb Tint = Rgb.Hex(0xfdf0f2);     // blush, lighter: panels and table stripes
    private static readonly Rgb Hairline = Rgb.Hex(0xe2e4e8);
    private static readonly Rgb White = new(1, 1, 1);

    private static readonly string[] Sections =
    {
        "Cover", "Features at a glance", "Charts and shapes", "Keyboard shortcuts", "Fill in a form", "About PDF Viewer Native", "Comments and markup",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        string root = RepoRoot();
        string output = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[1])
            : Path.Combine(root, "samples", "SampleDocument.pdf");
        string iconPath = Program.Option(args, "--icon") ?? Path.Combine(root, "src", "PdfViewer", "Assets", "app_icon.png");

        PdfCreateImage icon;
        using (var stream = File.OpenRead(iconPath)) icon = WicImageReader.ReadFrames(stream).First();
        if (icon.Alpha == null) throw new InvalidOperationException("The icon has no transparency; expected the 512 px RGBA app icon.");

        var (pages, comments) = WritePages(icon);
        byte[] pdf;
        using (var doc = await PdfVectorDocument.OpenAsync(pages))
            pdf = PdfAnnotationEditor.Apply(doc, pages, editor => { foreach (var c in comments) editor.Add(c); });

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, pdf);
        Console.WriteLine($"{output}: {pdf.Length:N0} bytes");

        using var check = await PdfVectorDocument.OpenAsync(pdf);
        if (check.PageCount != PageCount) throw new InvalidOperationException($"Expected {PageCount} pages, read {check.PageCount}.");
        var text = new PdfVectorTextService();
        for (int p = 1; p <= check.PageCount; p++)
        {
            string t = (await text.ExtractPageTextAsync(check, p)).ReplaceLineEndings(" ");
            Console.WriteLine($"  page {p}: {t.Length} characters: {(t.Length > 90 ? t[..90] + "..." : t)}");
        }

        if (Program.Option(args, "--preview") is { } previewDir)
        {
            double dpi = double.TryParse(Program.Option(args, "--dpi"), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 110;
            Directory.CreateDirectory(previewDir);
            using var renderer = Program.CreateRenderer(args);
            for (int p = 1; p <= check.PageCount; p++)
            {
                var list = await check.GetPageDisplayListAsync(p);
                var fallbacks = await renderer.AnalyzeAsync(list);
                using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = p, Dpi = dpi });
                var bmp = BitmapSource.Create(page.WidthPixels, page.HeightPixels, 96, 96, PixelFormats.Pbgra32, null, page.Pixels.ToArray(), page.Stride);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                string file = Path.Combine(previewDir, $"SampleDocument-page{p}.png");
                using (var fs = File.Create(file)) encoder.Save(fs);
                Console.WriteLine($"  {file} ({page.WidthPixels}x{page.HeightPixels}, {fallbacks.Count} fallback regions)");
            }
        }
        return 0;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }

    // ------------------------------------------------------------------ the pages

    private static (byte[] Pdf, List<PdfAnnotation> Comments) WritePages(PdfCreateImage icon)
    {
        using var ms = new MemoryStream();
        var comments = new List<PdfAnnotation>();
        using (var w = new PdfDocumentWriter(ms))
        {
            int helvetica = w.Add(FontDictionary("Helvetica"));
            int bold = w.Add(FontDictionary("Helvetica-Bold"));
            int italic = w.Add(FontDictionary("Helvetica-Oblique"));
            int zapf = w.Add(new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("Type1"), ["BaseFont"] = new PdfName("ZapfDingbats"),
            }));
            int iconImage = w.AddImage(icon);
            var resources = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Font"] = Dict(("F1", Ref(helvetica)), ("F2", Ref(bold)), ("F3", Ref(italic))),
                ["XObject"] = Dict(("Icon", Ref(iconImage))),
                ["ExtGState"] = Dict(("Half", Dict(("ca", new PdfReal(0.6)), ("CA", new PdfReal(0.6))))),
                ["Shading"] = Dict(("Glow", Gradient())),
            });

            var pageRefs = new List<int>();
            // The cover's links and the form's fields are written once every page has its number.
            var links = Enumerable.Range(0, Sections.Length - 1).Select(_ => w.Reserve()).ToList();
            var cover = Cover(out var linkRects);
            pageRefs.Add(w.AddPage(W, H, cover.ToBytes(), resources, Entries(("Annots", new PdfArray(links.Select(l => (PdfObject)Ref(l)).ToList())))));
            pageRefs.Add(w.AddPage(W, H, Features().ToBytes(), resources));
            pageRefs.Add(w.AddPage(W, H, Charts().ToBytes(), resources));
            pageRefs.Add(w.AddPage(W, H, Shortcuts().ToBytes(), resources));
            var fields = FormFields();
            var widgetNumbers = fields.Select(_ => w.Reserve()).ToList();
            pageRefs.Add(w.AddPage(W, H, FormPage(fields).ToBytes(), resources,
                Entries(("Annots", new PdfArray(widgetNumbers.Select(n => (PdfObject)Ref(n)).ToList())))));
            pageRefs.Add(w.AddPage(W, H, About().ToBytes(), resources));
            pageRefs.Add(w.AddPage(W, H, Markup(comments).ToBytes(), resources));

            for (int i = 0; i < links.Count; i++)
            {
                w.Write(links[i], new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Link"), ["Rect"] = RectArray(linkRects[i]),
                    ["Border"] = Numbers(0, 0, 0), ["F"] = new PdfInteger(4),
                    ["Dest"] = new PdfArray(new PdfObject[] { Ref(pageRefs[i + 1]), new PdfName("Fit") }),
                }));
            }

            int formFont = helvetica;
            for (int i = 0; i < fields.Count; i++)
                w.Write(widgetNumbers[i], Widget(w, fields[i], pageRefs[4], formFont));
            int acroForm = w.Add(new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Fields"] = new PdfArray(widgetNumbers.Select(n => (PdfObject)Ref(n)).ToList()),
                ["DR"] = Dict(("Font", Dict(("Helv", Ref(helvetica)), ("ZaDb", Ref(zapf))))),
                ["DA"] = new PdfString(Encoding.ASCII.GetBytes("/Helv 0 Tf 0 g")),
            }));

            int outlines = WriteOutline(w, pageRefs);
            w.Finish(new PdfCreateInfo
            {
                Title = "PDF Viewer Native — Sample Document",
                Author = "PDF Viewer Native",
                Subject = "A tour of PDF Viewer Native: features, vector graphics, a table, a form, text and comments.",
                Creator = "PDF Viewer Native",
                Producer = "PDF Viewer Native",
                Date = Date,
            }, new Dictionary<string, PdfObject>
            {
                ["Outlines"] = Ref(outlines),
                ["PageMode"] = new PdfName("UseOutlines"),
                ["AcroForm"] = Ref(acroForm),
                ["Lang"] = PdfObjectWriter.TextString("en"),
            });
        }
        return (ms.ToArray(), comments);
    }

    private static int WriteOutline(PdfDocumentWriter w, List<int> pageRefs)
    {
        int root = w.Reserve();
        var items = Sections.Select(_ => w.Reserve()).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var e = new Dictionary<string, PdfObject>
            {
                ["Title"] = PdfObjectWriter.TextString(Sections[i]),
                ["Parent"] = Ref(root),
                ["Dest"] = new PdfArray(new PdfObject[] { Ref(pageRefs[i]), new PdfName("Fit") }),
            };
            if (i > 0) e["Prev"] = Ref(items[i - 1]);
            if (i < items.Count - 1) e["Next"] = Ref(items[i + 1]);
            w.Write(items[i], new PdfDictionary(e));
        }
        w.Write(root, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Outlines"), ["First"] = Ref(items[0]), ["Last"] = Ref(items[^1]), ["Count"] = new PdfInteger(items.Count),
        }));
        return root;
    }

    /// <summary>The header and footer of the pages after the cover, and the section title; returns the baseline below the title.</summary>
    private static double Chrome(Canvas c, int page, string kicker, string title)
    {
        c.Image("Icon", Left, 742, 22);
        c.Text(Font.Bold, 10, Left + 30, 749, "PDF Viewer Native", Ink);
        c.TextRight(Font.Regular, 9, Right, 749, "Sample Document", Grey);
        c.Line(Left, 732, Right, 732, Hairline, 0.75);
        c.Line(Left, 56, Right, 56, Hairline, 0.75);
        c.Text(Font.Regular, 8.5, Left, 40, "PDF Viewer Native — Sample Document", Grey);
        c.TextRight(Font.Regular, 8.5, Right, 40, $"Page {page} of {PageCount}", Grey);

        c.Text(Font.Bold, 9, Left, 700, kicker.ToUpperInvariant(), Crimson);
        c.Text(Font.Bold, 26, Left, 672, title, Ink);
        c.Rect(Left, 659, 40, 3.5, Crimson);
        return 636;
    }

    private static Canvas Cover(out List<PdfRect> linkRects)
    {
        var c = new Canvas();
        c.Rect(0, H - 10, W, 10, Crimson);

        const double iconSize = 168;
        c.Image("Icon", (W - iconSize) / 2, 548, iconSize);
        c.TextCenter(Font.Bold, 40, W / 2, 486, "PDF Viewer Native", Ink);
        double y = 452;
        foreach (var line in Wrap(Font.Regular, 14, "A free, open-source PDF viewer and editor for Windows, Android and the web, with its own PDF engine and no telemetry.", 420))
        {
            c.TextCenter(Font.Regular, 14, W / 2, y, line, BodyText);
            y -= 20;
        }

        // A pill with the document's name.
        const string pill = "SAMPLE DOCUMENT";
        double pw = Width(Font.Bold, pill, 10) + 36;
        c.RoundRect((W - pw) / 2, 372, pw, 24, 12, Crimson, null);
        c.TextCenter(Font.Bold, 10, W / 2, 380.5, pill, White);

        // What is inside, each line a link to its page.
        double boxTop = 344, boxH = 250, boxX = 126, boxW = W - 2 * boxX;
        c.RoundRect(boxX, boxTop - boxH, boxW, boxH, 12, Tint, null);
        c.Text(Font.Bold, 12, boxX + 28, boxTop - 34, "Inside this document", Ink);
        linkRects = new List<PdfRect>();
        double row = boxTop - 64;
        for (int i = 1; i < Sections.Length; i++)
        {
            c.Text(Font.Bold, 11, boxX + 28, row, (i + 1).ToString(CultureInfo.InvariantCulture), Crimson);
            c.Text(Font.Regular, 11, boxX + 50, row, Sections[i], Ink);
            c.TextRight(Font.Regular, 11, boxX + boxW - 28, row, "Page " + (i + 1).ToString(CultureInfo.InvariantCulture), Grey);
            linkRects.Add(new PdfRect(boxX + 20, row - 7, boxW - 40, 22));
            if (i < Sections.Length - 1) c.Line(boxX + 28, row - 10, boxX + boxW - 28, row - 10, Blush, 0.5);
            row -= 30;
        }

        c.TextCenter(Font.Regular, 9, W / 2, 52, "Open source under the MIT licence • No telemetry • No account", Grey);
        c.TextCenter(Font.Italic, 8.5, W / 2, 38, "This document was made with the PDF engine of PDF Viewer Native.", Grey);
        return c;
    }

    private static Canvas Features()
    {
        var c = new Canvas();
        double y = Chrome(c, 2, "Overview", "Features at a glance");
        y = c.Paragraph(Font.Regular, 11, 15, Left, y, Body,
            "One app to read, search, comment on, fill in, sign, edit and convert PDF files. The full list, with a user guide, is in docs/features.md of the source repository.", BodyText) - 10;

        var cards = new (string Title, string[] Items)[]
        {
            ("View", new[] { "Continuous, single-page and facing views", "Zoom from 25 % to 1600 %, sharp at every zoom", "Tabs, bookmarks, thumbnails, night mode and full screen" }),
            ("Read", new[] { "Read mode for screen readers, in tagged reading order", "Read Aloud and a PDF/UA-1 accessibility check", "Make Accessible adds tags to untagged files" }),
            ("Select and search", new[] { "Word-processor text selection", "Search with a list of results", "OCR for scanned pages, on this computer" }),
            ("Comment and measure", new[] { "Highlights, notes, ink and shapes", "Distance, perimeter and area measuring", "Standard, dynamic and custom stamps" }),
            ("Edit", new[] { "Text and images in place", "Organise, merge and split pages", "Headers, footers, watermarks, Bates numbers", "Reduce file size and compare documents" }),
            ("Forms, signatures, security", new[] { "Fill and flatten forms", "Sign (PAdES-B-LT and B-LTA) and verify", "AES-256 passwords and true redaction" }),
            ("Create and convert", new[] { "PDF from images, a scanner or Office files", "PDF/A", "Export to Word, Excel, PowerPoint, text and images" }),
            ("Print production", new[] { "Print with a live preview", "Output Preview: separations, overprint, total ink", "Preflight" }),
            ("Privacy and safety", new[] { "No script engine: active content never runs", "Save Clean Copy removes scripts and attachments", "No telemetry, no account, no cloud" }),
            ("One engine, three platforms", new[] { "Windows app drawn with Direct2D", "Web demo that runs in the browser tab", "Android app (preview) with the same engine" }),
        };

        const double gap = 9, colW = (Body - gap) / 2, pad = 12, size = 9.5, leading = 12.5, indent = 11;
        double Height((string Title, string[] Items) card)
        {
            int lines = card.Items.Sum(i => Wrap(Font.Regular, size, i, colW - 2 * pad - indent).Count);
            return pad + 13 + 8 + lines * leading + (card.Items.Length - 1) * 3 + pad - 4;
        }
        for (int i = 0; i < cards.Length; i += 2)
        {
            double h = Math.Max(Height(cards[i]), Height(cards[i + 1]));
            for (int k = 0; k < 2; k++)
            {
                var card = cards[i + k];
                double x = Left + k * (colW + gap);
                c.RoundRect(x, y - h, colW, h, 8, Tint, null);
                c.Rect(x, y - h + 10, 3, h - 20, Crimson);
                double ty = y - pad - 9;
                c.Text(Font.Bold, 11.5, x + pad, ty, card.Title, Crimson);
                ty -= 8 + leading;
                foreach (var item in card.Items)
                    ty = c.Bullet(size, leading, x + pad, ty, colW - 2 * pad, item, Ink) - 3;
            }
            y -= h + gap;
        }
        y += gap;
        Ensure(y > 64, $"the feature cards run into the footer (y {y:F0})");
        return c;
    }

    private static Canvas Charts()
    {
        var c = new Canvas();
        double y = Chrome(c, 3, "Vector graphics", "Charts and shapes");
        c.Paragraph(Font.Regular, 11, 15, Left, y, Body,
            "Everything on this page is drawn with paths, not pictures. Zoom in as far as you like: the edges, curves and text stay sharp.", BodyText);

        // Bar chart.
        double top = 598, bottom = 372;
        c.RoundRect(Left, bottom, 264, top - bottom, 10, Tint, null);
        c.Text(Font.Bold, 11.5, Left + 14, top - 22, "Pages read per day", Ink);
        c.Text(Font.Regular, 8.5, Left + 14, top - 35, "Sample data for this demonstration", Grey);
        double x0 = Left + 38, x1 = Left + 250, yb = 414, yt = 540;
        for (int v = 0; v <= 40; v += 10)
        {
            double gy = yb + (yt - yb) * v / 40;
            c.Line(x0, gy, x1, gy, v == 0 ? Grey : Hairline, v == 0 ? 0.8 : 0.5);
            c.TextRight(Font.Regular, 7.5, x0 - 6, gy - 2.5, v.ToString(CultureInfo.InvariantCulture), Grey);
        }
        string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        int[] thisWeek = { 12, 18, 9, 24, 33, 16, 21 }, lastWeek = { 8, 14, 15, 19, 22, 11, 13 };
        double group = (x1 - x0) / days.Length, bar = 10.5;
        for (int i = 0; i < days.Length; i++)
        {
            double gx = x0 + i * group + (group - 2 * bar - 2) / 2;
            c.RoundTopBar(gx, yb, bar, (yt - yb) * thisWeek[i] / 40, Crimson);
            c.RoundTopBar(gx + bar + 2, yb, bar, (yt - yb) * lastWeek[i] / 40, Rose);
            c.TextCenter(Font.Regular, 7.5, x0 + (i + 0.5) * group, yb - 12, days[i], BodyText);
        }
        c.Rect(x0, 384, 8, 8, Crimson);
        c.Text(Font.Regular, 8.5, x0 + 12, 385, "This week", BodyText);
        c.Rect(x0 + 72, 384, 8, 8, Rose);
        c.Text(Font.Regular, 8.5, x0 + 84, 385, "Last week", BodyText);

        // Donut chart.
        double dx = Left + 276, dw = Right - dx;
        c.RoundRect(dx, bottom, dw, top - bottom, 10, Tint, null);
        c.Text(Font.Bold, 11.5, dx + 14, top - 22, "Documents opened", Ink);
        c.Text(Font.Regular, 8.5, dx + 14, top - 35, "Sample data for this demonstration", Grey);
        var slices = new (string Label, int Count, Rgb Colour)[] { ("Reports", 48, Crimson), ("Forms", 30, Rose), ("Books", 24, Blush), ("Scans", 18, Ink) };
        double total = slices.Sum(s => s.Count), cx = dx + 70, cy = 476, ro = 58, ri = 34, a = Math.PI / 2;
        foreach (var s in slices)
        {
            double sweep = 2 * Math.PI * s.Count / total;
            c.Wedge(cx, cy, ro, ri, a, a - sweep, s.Colour);
            a -= sweep;
        }
        c.TextCenter(Font.Bold, 16, cx, cy - 1, ((int)total).ToString(CultureInfo.InvariantCulture), Ink);
        c.TextCenter(Font.Regular, 8, cx, cy - 13, "files", Grey);
        double ly = 516;
        foreach (var s in slices)
        {
            c.Rect(dx + 140, ly, 8, 8, s.Colour);
            c.Text(Font.Regular, 9, dx + 153, ly + 0.5, s.Label, BodyText);
            c.TextRight(Font.Bold, 9, Right - 12, ly + 0.5, $"{Math.Round(100 * s.Count / total)} %", Ink);
            ly -= 20;
        }

        // Shapes.
        c.Text(Font.Bold, 13, Left, 340, "Shapes, curves and fills", Ink);
        const double cell = Body / 4;
        double Cx(int i) => Left + cell * (i + 0.5);
        double r1 = 272, r2 = 150;
        c.Circle(Cx(0), r1, 32, Crimson, null, 0);
        c.Caption(Cx(0), r1 - 50, "Circle");
        c.RoundRect(Cx(1) - 44, r1 - 30, 88, 60, 12, Blush, Crimson, 2);
        c.Caption(Cx(1), r1 - 50, "Rounded rectangle");
        c.Polygon(new[] { (Cx(2), r1 + 33), (Cx(2) + 38, r1 - 31), (Cx(2) - 38, r1 - 31) }, Ink, null, 0);
        c.Caption(Cx(2), r1 - 50, "Triangle");
        c.Polygon(Star(Cx(3), r1, 36, 15), Rose, Crimson, 1.5);
        c.Caption(Cx(3), r1 - 50, "Star");

        c.Op("q 1 J 1 j");
        c.StrokeColour(Crimson);
        c.Op($"3 w {F(Cx(0) - 50)} {F(r2)} m {F(Cx(0) - 25)} {F(r2 + 50)} {F(Cx(0) - 10)} {F(r2 + 50)} {F(Cx(0))} {F(r2)} c " +
             $"{F(Cx(0) + 10)} {F(r2 - 50)} {F(Cx(0) + 25)} {F(r2 - 50)} {F(Cx(0) + 50)} {F(r2)} c S Q");
        c.Caption(Cx(0), r2 - 50, "Bezier curve");

        c.Op("q [6 4] 0 d 2 w 1 j");
        c.StrokeColour(Ink);
        c.Op($"{F(Cx(1) - 46)} {F(r2 - 22)} m {F(Cx(1) - 14)} {F(r2 + 20)} l {F(Cx(1) + 8)} {F(r2 - 8)} l {F(Cx(1) + 34)} {F(r2 + 22)} l S Q");
        c.Polygon(new[] { (Cx(1) + 44, r2 + 34), (Cx(1) + 26, r2 + 28), (Cx(1) + 40, r2 + 16) }, Ink, null, 0);
        c.Caption(Cx(1), r2 - 50, "Dashed line and arrow");

        c.Op("q");
        c.RoundRectPath(Cx(2) - 44, r2 - 30, 88, 60, 12);
        c.Op("W n /Glow sh Q");
        c.Caption(Cx(2), r2 - 50, "Gradient");

        c.Op("q /Half gs");
        c.Circle(Cx(3) - 13, r2 + 9, 24, Crimson, null, 0);
        c.Circle(Cx(3) + 13, r2 + 9, 24, Ink, null, 0);
        c.Circle(Cx(3), r2 - 13, 24, Rose, null, 0);
        c.Op("Q");
        c.Caption(Cx(3), r2 - 50, "Transparency");
        return c;
    }

    private static Canvas Shortcuts()
    {
        var c = new Canvas();
        double y = Chrome(c, 4, "Table", "Keyboard shortcuts");
        y = c.Paragraph(Font.Regular, 11, 15, Left, y, Body,
            "A selection of the shortcuts of the Windows app. The full table is in docs/features.md of the source repository.", BodyText) - 12;

        var rows = new[]
        {
            ("Ctrl+O", "Always", "Open a PDF"),
            ("Ctrl+S", "Document open", "Save the document with its annotations"),
            ("Ctrl+P", "Document open", "Print, with preview"),
            ("Ctrl+F", "Document open", "Show or hide the search bar"),
            ("F3 / Shift+F3", "Search active", "Next / previous match"),
            ("Ctrl+C", "Text selected", "Copy the selected text"),
            ("Ctrl+Z / Ctrl+Y", "Document open", "Undo / redo"),
            ("Ctrl+D", "Document open", "Document properties"),
            ("Ctrl+E", "Document open", "Edit text and images"),
            ("Ctrl++ / Ctrl+-", "Document open", "Zoom in / out"),
            ("Ctrl+0", "Document open", "Fit to page"),
            ("Ctrl+1", "Document open", "Fit to width"),
            ("Ctrl+R / Ctrl+Shift+R", "Document open", "Rotate the view clockwise / counter-clockwise"),
            ("Ctrl+B", "Always", "Show or hide the sidebar"),
            ("Ctrl+4", "Document open", "Read mode"),
            ("Ctrl+Shift+N", "Document open", "Night mode (invert pages)"),
            ("Ctrl+Tab / Ctrl+Shift+Tab", "Several documents", "Next / previous tab"),
            ("Alt+Left / Alt+Right", "Document open", "Back / forward through jumps"),
            ("F11", "Document open", "Full screen (Esc or F11 to exit)"),
            ("Home / End", "Document open", "First / last page"),
        };
        double[] cols = { 156, 104, Body - 156 - 104 };
        const double pad = 9, rowH = 21, headH = 25;
        c.RoundTopRect(Left, y - headH, Body, headH, 6, Crimson);
        string[] heads = { "Shortcut", "When", "Action" };
        double cx = Left;
        for (int k = 0; k < 3; k++) { c.Text(Font.Bold, 10, cx + pad, y - 16.5, heads[k], White); cx += cols[k]; }
        y -= headH;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i % 2 == 1) c.Rect(Left, y - rowH, Body, rowH, Tint);
            string[] cells = { rows[i].Item1, rows[i].Item2, rows[i].Item3 };
            cx = Left;
            for (int k = 0; k < 3; k++)
            {
                var font = k == 0 ? Font.Bold : Font.Regular;
                Ensure(Width(font, cells[k], 9.5) <= cols[k] - 2 * pad, $"table cell '{cells[k]}' is too wide");
                c.Text(font, 9.5, cx + pad, y - 14, cells[k], k == 0 ? Ink : BodyText);
                cx += cols[k];
            }
            y -= rowH;
        }
        c.Line(Left, y, Right, y, Crimson, 1);
        Ensure(y > 90, "the table runs into the footer");
        c.Paragraph(Font.Italic, 9, 12, Left, y - 18, Body, "The When column says when a shortcut works: always, while a document is open, or in the state it names.", Grey);
        return c;
    }

    private sealed record Field(string Kind, string Name, string Label, string Tooltip, PdfRect Rect, string Value = "", string[]? Options = null);

    private static List<Field> FormFields()
    {
        const double x0 = Left + 24, x1 = Right - 24, gap = 16, half = (x1 - x0 - gap) / 2;
        string[] features = { "View", "Read", "Select and search", "Comment and measure", "Edit", "Forms and signatures", "Create and convert", "Print production" };
        string[] often = { "Every day", "Every week", "Now and then" };
        return new List<Field>
        {
            new("text", "name", "Your name", "Your name", new PdfRect(x0, 516, half, 24)),
            new("text", "email", "Email address", "Email address", new PdfRect(x0 + half + gap, 516, half, 24)),
            new("combo", "feature", "Favourite feature", "Favourite feature", new PdfRect(x0, 452, half, 24), "Select and search", features),
            new("combo", "often", "How often do you read PDF files?", "How often do you read PDF files", new PdfRect(x0 + half + gap, 452, half, 24), "Every day", often),
            new("multiline", "comments", "Comments", "Comments", new PdfRect(x0, 322, x1 - x0, 92)),
            new("check", "windows", "I use PDF Viewer Native on Windows", "I use PDF Viewer Native on Windows", new PdfRect(x0, 284, 16, 16), "Yes"),
            new("check", "other", "I also use the web demo or the Android app", "I also use the web demo or the Android app", new PdfRect(x0, 258, 16, 16), "Off"),
        };
    }

    private static Canvas FormPage(List<Field> fields)
    {
        var c = new Canvas();
        double y = Chrome(c, 5, "Interactive form", "Fill in a form");
        c.Paragraph(Font.Regular, 11, 15, Left, y, Body,
            "This page has a real form. Click a field and type, pick a value from a list or tick a box, then save the document to keep what you entered.", BodyText);

        c.RoundRect(Left, 236, Body, 604 - 236, 10, Tint, null);
        c.Text(Font.Bold, 13, Left + 24, 579, "Reader survey (sample form)", Ink);
        foreach (var f in fields)
        {
            if (f.Kind == "check")
                c.Text(Font.Regular, 10.5, f.Rect.X + f.Rect.Width + 10, f.Rect.Y + 4, f.Label, Ink);
            else
                c.Text(Font.Bold, 9.5, f.Rect.X, f.Rect.Y + f.Rect.Height + 8, f.Label, Ink);
        }

        double ty = c.Paragraph(Font.Bold, 11, 15, Left, 210, Body, "Good to know", Ink);
        ty = c.Bullet(10, 14, Left, ty - 2, Body, "Nothing you type here is sent anywhere: the form has no script and no submit button.", BodyText) - 4;
        ty = c.Bullet(10, 14, Left, ty, Body, "Flatten the form to turn the fields and their values into ordinary page content.", BodyText) - 4;
        ty = c.Bullet(10, 14, Left, ty, Body, "The Android app (preview) uses the same engine and fills this form too.", BodyText);
        Ensure(ty > 62, "the form notes run into the footer");
        return c;
    }

    private static PdfDictionary Widget(PdfDocumentWriter w, Field f, int page, int font)
    {
        const string ink = "0.122 0.137 0.157";
        var e = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Widget"),
            ["T"] = PdfObjectWriter.TextString(f.Name), ["TU"] = PdfObjectWriter.TextString(f.Tooltip),
            ["Rect"] = RectArray(f.Rect), ["F"] = new PdfInteger(4), ["P"] = Ref(page),
            ["MK"] = Dict(("BC", Numbers(Grey.R, Grey.G, Grey.B)), ("BG", Numbers(1, 1, 1))),
            ["BS"] = Dict(("W", new PdfInteger(1)), ("S", new PdfName("S"))),
        };
        double fw = f.Rect.Width, fh = f.Rect.Height;
        string frame = $"1 g 0 0 {F(fw)} {F(fh)} re f {Grey} RG 1 w 0.5 0.5 {F(fw - 1)} {F(fh - 1)} re S\n";
        int Appearance(string content, bool withFont = true)
        {
            var d = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = Numbers(0, 0, fw, fh),
            };
            if (withFont) d["Resources"] = Dict(("Font", Dict(("Helv", Ref(font)))));
            return w.Add(PdfObjectWriter.NewStream(d, Encoding.Latin1.GetBytes(content)));
        }

        switch (f.Kind)
        {
            case "text":
            case "multiline":
                e["FT"] = new PdfName("Tx");
                e["V"] = PdfObjectWriter.TextString(f.Value);
                e["DA"] = new PdfString(Encoding.ASCII.GetBytes($"/Helv {(f.Kind == "text" ? 11 : 10)} Tf {ink} rg"));
                if (f.Kind == "multiline") e["Ff"] = new PdfInteger(4096);
                e["AP"] = Dict(("N", Ref(Appearance(frame + "/Tx BMC EMC\n"))));
                break;
            case "combo":
                e["FT"] = new PdfName("Ch");
                e["Ff"] = new PdfInteger(131072);
                e["Opt"] = new PdfArray(f.Options!.Select(o => (PdfObject)PdfObjectWriter.TextString(o)).ToList());
                e["V"] = PdfObjectWriter.TextString(f.Value);
                e["DA"] = new PdfString(Encoding.ASCII.GetBytes($"/Helv 11 Tf {ink} rg"));
                string arrow = $"{Hairline} RG 1 w {F(fw - 24)} 4 m {F(fw - 24)} {F(fh - 4)} l S {Crimson} rg {F(fw - 17)} {F(fh / 2 + 2.5)} m {F(fw - 7)} {F(fh / 2 + 2.5)} l {F(fw - 12)} {F(fh / 2 - 3)} l f\n";
                e["AP"] = Dict(("N", Ref(Appearance(frame + arrow + $"/Tx BMC q 1 1 {F(fw - 26)} {F(fh - 2)} re W n BT /Helv 11 Tf {ink} rg 6 {F(fh / 2 - 4)} Td {Lit(f.Value)} Tj ET Q EMC\n"))));
                break;
            case "check":
                e["FT"] = new PdfName("Btn");
                e["V"] = new PdfName(f.Value);
                e["AS"] = new PdfName(f.Value);
                e["DA"] = new PdfString(Encoding.ASCII.GetBytes($"/ZaDb 0 Tf {Crimson} rg"));
                e["MK"] = Dict(("BC", Numbers(Grey.R, Grey.G, Grey.B)), ("BG", Numbers(1, 1, 1)), ("CA", new PdfString(Encoding.ASCII.GetBytes("4"))));
                int on = Appearance($"{Crimson} rg 0 0 {F(fw)} {F(fh)} re f 1 G 2 w 1 J 1 j 3.5 8.2 m 6.6 5 l 12.6 11.4 l S\n", false);
                int off = Appearance(frame, false);
                e["AP"] = Dict(("N", Dict(("Yes", Ref(on)), ("Off", Ref(off)))));
                break;
        }
        return new PdfDictionary(e);
    }

    private static Canvas About()
    {
        var c = new Canvas();
        double y = Chrome(c, 6, "Text", "About PDF Viewer Native");
        const double size = 11, leading = 16;
        var blocks = new (string? Heading, string Text)[]
        {
            (null, "PDF Viewer Native is a free, open-source PDF viewer and editor. It runs on Windows, in a web browser and, as a preview, on Android. Its source code is published under the MIT licence, so anyone can read it, build it and help to improve it."),
            ("Its own PDF engine", "The app does not wrap another PDF library. Its engine, PdfEngine.Vector, is written in C# for .NET 9. The engine reads the file, interprets every page into a display list of paths, text and images, and gives that list to a renderer. On Windows, Direct2D draws the pages; in the browser and on Android, a portable Skia renderer draws the same display list. Because the pages are drawn from vectors, text and lines stay sharp from 25 % to 1600 % zoom."),
            ("A document is data, never a program", "Many vulnerabilities of PDF viewers come from active content: JavaScript, launch actions and embedded files. The engine has no JavaScript interpreter. Active content is parsed, reported and left inert, and a link opens only when you click it. Every document is inspected when it opens, and Save Clean Copy writes a copy without scripts, launch actions and embedded files."),
            ("Private by design", "Nothing about you or your documents is recorded or sent. There is no telemetry, no account and no cloud storage. The update check stays off until you allow it, and optional components are downloaded only when you ask for them."),
            ("Readable by everyone", "Read mode follows the reading order of tagged PDF files and works with screen readers. Read Aloud speaks the text, the PDF/UA-1 check reports accessibility problems, and Make Accessible adds tags to a document that has none."),
        };
        foreach (var (heading, text) in blocks)
        {
            if (heading != null)
            {
                y -= 8;
                c.Text(Font.Bold, 12.5, Left, y, heading, Ink);
                y -= 18;
            }
            y = c.Paragraph(Font.Regular, size, leading, Left, y, Body, text, BodyText) - 4;
        }

        double boxTop = y - 8, boxH = 54;
        c.RoundRect(Left, boxTop - boxH, Body, boxH, 8, Tint, null);
        c.Rect(Left, boxTop - boxH + 10, 3, boxH - 20, Crimson);
        c.Text(Font.Bold, 10.5, Left + 16, boxTop - 20, "Try it", Crimson);
        c.Paragraph(Font.Regular, 10, 14, Left + 16, boxTop - 36, Body - 32, "Press Ctrl+F and search for the word \u201Cengine\u201D. The results list shows every match in this document.", Ink);
        Ensure(boxTop - boxH > 62, "the text page runs into the footer");
        return c;
    }

    private static Canvas Markup(List<PdfAnnotation> comments)
    {
        var c = new Canvas();
        double y = Chrome(c, 7, "Annotations", "Comments and markup");
        c.Paragraph(Font.Regular, 11, 15, Left, y, Body,
            "The notes below carry four comments made with the annotation tools: a highlight, a sticky note, a rectangle and an ellipse. They are ordinary PDF annotations, so other PDF readers show them too.", BodyText);

        double top = 590, bottom = 262;
        c.RoundRect(Left, bottom, Body, top - bottom, 10, White, Hairline, 1);
        c.Text(Font.Bold, 13, Left + 20, top - 26, "Meeting notes (sample)", Ink);
        const double size = 11, leading = 16, x = Left + 20, width = 400;
        double ty = c.Paragraph(Font.Regular, size, leading, x, top - 52, width,
            "The team reviewed the sample document on Thursday. Its seven pages cover the main features, a chart, a table, a form, a page of running text and this page of comments.", BodyText) - 6;

        const string action = "Action: check every page at 400 % zoom before the screenshots are taken. Text, charts and the icon must stay sharp.";
        const string phrase = "check every page at 400 % zoom";
        var lines = Wrap(Font.Regular, size, action, width);
        int lineIndex = lines.FindIndex(l => l.Contains(phrase, StringComparison.Ordinal));
        Ensure(lineIndex >= 0, "the highlighted phrase wraps");
        double hy = ty - lineIndex * leading;
        double hx = x + Width(Font.Regular, lines[lineIndex][..lines[lineIndex].IndexOf(phrase, StringComparison.Ordinal)], size);
        double hw = Width(Font.Regular, phrase, size);
        ty = c.Paragraph(Font.Regular, size, leading, x, ty, width, action, BodyText) - 6;
        ty = c.Paragraph(Font.Regular, size, leading, x, ty, width,
            "Figure 1 shows the plan of the chart page. The rectangle marks the part to check first.", BodyText) - 14;

        // Figure 1: a small plan of page 3.
        double fx = x + 10, fw = 220, fh = 96, fy = ty - fh;
        c.RoundRect(fx, fy, fw, fh, 6, Tint, Hairline, 0.75);
        for (int i = 0; i < 7; i++)
            c.Rect(fx + 18 + i * 14, fy + 16, 9, new[] { 22, 34, 16, 46, 60, 30, 40 }[i], i % 2 == 0 ? Crimson : Rose);
        c.Circle(fx + 168, fy + 50, 30, Rose, null, 0);
        c.Circle(fx + 168, fy + 50, 16, Tint, null, 0);
        c.Text(Font.Bold, 10, fx, fy - 18, "Figure 1", Ink);
        c.Text(Font.Regular, 10, fx + 64, fy - 18, "The chart page, in outline", BodyText);
        Ensure(fy - 27 > bottom + 8, "the figure runs out of the notes panel");

        comments.Add(Comment("Highlight", new PdfRect(hx - 1, hy - 3.5, hw + 2, size + 4), Blush,
            "Please do this before the store screenshots.", q: new[] { hx - 1, hy + size + 0.5, hx + hw + 1, hy + size + 0.5, hx - 1, hy - 3.5, hx + hw + 1, hy - 3.5 }));
        comments.Add(Comment("Text", new PdfRect(Right - 56, hy - 7, 24, 24), Crimson,
            "Check night mode too: press Ctrl+Shift+N.", icon: "Comment"));
        comments.Add(Comment("Square", new PdfRect(fx + 10, fy + 8, 110, 72), Crimson, "Check this part first.", border: 2));
        comments.Add(Comment("Circle", new PdfRect(fx - 9, fy - 27, 62, 26), Crimson, "Figure numbers follow the order of the pages.", border: 1.5));

        double ny = c.Paragraph(Font.Bold, 11, 15, Left, bottom - 24, Body, "Work with the comments", Ink) - 2;
        ny = c.Bullet(10, 14, Left, ny, Body, "Click a comment to select it, then drag it or its handles to move or resize it.", BodyText) - 3;
        ny = c.Bullet(10, 14, Left, ny, Body, "Double-click it, or press Enter, to edit its note. Arrow keys nudge it by 1 pt (Shift: 10 pt).", BodyText) - 3;
        ny = c.Bullet(10, 14, Left, ny, Body, "Press Delete to remove it, and Ctrl+Z to bring it back.", BodyText);
        Ensure(ny > 62, "the comment notes run into the footer");
        return c;
    }

    private static PdfAnnotation Comment(string subtype, PdfRect rect, Rgb colour, string text, double[]? q = null, string? icon = null, double? border = null)
    {
        string date = PdfTextStrings.FormatDate(Date);
        var a = new PdfAnnotation
        {
            PageNumber = 7, Subtype = subtype, Rect = rect, Color = colour.ToArray(), Contents = text,
            Author = "PDF Viewer Native", CreationDate = date, ModifiedDate = date, Icon = icon, BorderWidth = border,
            Name = "sample-" + subtype.ToLowerInvariant(),
        };
        if (q != null) a.QuadPoints.Add(q);
        return a;
    }

    // ------------------------------------------------------------------ helpers

    private static PdfDictionary FontDictionary(string baseFont) => new(new Dictionary<string, PdfObject>
    {
        ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("Type1"), ["BaseFont"] = new PdfName(baseFont),
        ["Encoding"] = new PdfName("WinAnsiEncoding"),
    });

    /// <summary>An axial shading from crimson to blush, across the gradient shape of the charts page (third column).</summary>
    private static PdfDictionary Gradient() => new(new Dictionary<string, PdfObject>
    {
        ["ShadingType"] = new PdfInteger(2), ["ColorSpace"] = new PdfName("DeviceRGB"),
        ["Coords"] = Numbers(Left + Body / 4 * 2.5 - 44, 0, Left + Body / 4 * 2.5 + 44, 0),
        ["Function"] = Dict(("FunctionType", new PdfInteger(2)), ("Domain", Numbers(0, 1)),
            ("C0", Numbers(Crimson.R, Crimson.G, Crimson.B)), ("C1", Numbers(Blush.R, Blush.G, Blush.B)), ("N", new PdfInteger(1))),
        ["Extend"] = new PdfArray(new PdfObject[] { PdfBoolean.True, PdfBoolean.True }),
    });

    private static IReadOnlyList<(double, double)> Star(double cx, double cy, double outer, double inner) =>
        Enumerable.Range(0, 10).Select(i =>
        {
            double r = i % 2 == 0 ? outer : inner, a = Math.PI / 2 + i * Math.PI / 5;
            return (cx + r * Math.Cos(a), cy - 3 + r * Math.Sin(a));
        }).ToList();

    private static void Ensure(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Layout: " + what + ".");
    }

    private static PdfIndirectRef Ref(int n) => new(n);

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Value));

    private static Dictionary<string, PdfObject> Entries(params (string Key, PdfObject Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    private static PdfArray Numbers(params double[] values) =>
        new(values.Select(v => (PdfObject)(Math.Abs(v - Math.Round(v)) < 1e-9 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(Math.Round(v, 4)))).ToList());

    private static PdfArray RectArray(PdfRect r) => Numbers(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 3));

    private enum Font { Regular, Bold, Italic }

    private static string ResourceName(Font f) => f switch { Font.Regular => "F1", Font.Bold => "F2", _ => "F3" };

    private static string BaseFont(Font f) => f switch { Font.Regular => "Helvetica", Font.Bold => "Helvetica-Bold", _ => "Helvetica-Oblique" };

    private static readonly Encoding WinAnsi = CreateWinAnsi();

    private static Encoding CreateWinAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // A character that WinAnsiEncoding cannot show is a mistake in the text above: fail, do not drop it.
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    /// <summary>The width of <paramref name="text"/> in points, from the standard 14 metrics the engine draws with.</summary>
    private static double Width(Font font, string text, double size)
    {
        double total = 0;
        foreach (byte b in WinAnsi.GetBytes(text))
        {
            string? glyph = FontEncodings.WinAnsi[b];
            total += glyph != null && Standard14Fonts.TryGetWidthByName(BaseFont(font), glyph, out double w) ? w : 500;
        }
        return total * size / 1000;
    }

    private static List<string> Wrap(Font font, double size, string text, double width)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Width(font, next, size) > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>A PDF literal string of the text in WinAnsiEncoding, every byte outside printable ASCII escaped.</summary>
    private static string Lit(string text)
    {
        var sb = new StringBuilder("(");
        foreach (byte b in WinAnsi.GetBytes(text))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    private readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb Hex(int v) => new(((v >> 16) & 0xff) / 255.0, ((v >> 8) & 0xff) / 255.0, (v & 0xff) / 255.0);
        public double[] ToArray() => new[] { Math.Round(R, 4), Math.Round(G, 4), Math.Round(B, 4) };
        public override string ToString() => $"{F(R)} {F(G)} {F(B)}";
    }

    /// <summary>A page's content stream, built operator by operator.</summary>
    private sealed class Canvas
    {
        private readonly StringBuilder _s = new();

        public void Op(string op) => _s.Append(op).Append('\n');

        public void FillColour(Rgb c) => Op($"{c} rg");

        public void StrokeColour(Rgb c) => Op($"{c} RG");

        public void Rect(double x, double y, double w, double h, Rgb fill)
        {
            FillColour(fill);
            Op($"{F(x)} {F(y)} {F(w)} {F(h)} re f");
        }

        public void Line(double x1, double y1, double x2, double y2, Rgb colour, double width)
        {
            StrokeColour(colour);
            Op($"{F(width)} w {F(x1)} {F(y1)} m {F(x2)} {F(y2)} l S");
        }

        public void Image(string name, double x, double y, double size) => Op($"q {F(size)} 0 0 {F(size)} {F(x)} {F(y)} cm /{name} Do Q");

        public void RoundRectPath(double x, double y, double w, double h, double r)
        {
            const double k = 0.5523;
            Op($"{F(x + r)} {F(y)} m {F(x + w - r)} {F(y)} l {F(x + w - r + r * k)} {F(y)} {F(x + w)} {F(y + r - r * k)} {F(x + w)} {F(y + r)} c " +
               $"{F(x + w)} {F(y + h - r)} l {F(x + w)} {F(y + h - r + r * k)} {F(x + w - r + r * k)} {F(y + h)} {F(x + w - r)} {F(y + h)} c " +
               $"{F(x + r)} {F(y + h)} l {F(x + r - r * k)} {F(y + h)} {F(x)} {F(y + h - r + r * k)} {F(x)} {F(y + h - r)} c " +
               $"{F(x)} {F(y + r)} l {F(x)} {F(y + r - r * k)} {F(x + r - r * k)} {F(y)} {F(x + r)} {F(y)} c h");
        }

        public void RoundRect(double x, double y, double w, double h, double r, Rgb? fill, Rgb? stroke, double lineWidth = 1)
        {
            if (fill is { } f) FillColour(f);
            if (stroke is { } s) { StrokeColour(s); Op($"{F(lineWidth)} w"); }
            RoundRectPath(x, y, w, h, r);
            Op(fill != null && stroke != null ? "B" : fill != null ? "f" : "S");
        }

        /// <summary>A rectangle with its two top corners rounded (a table head).</summary>
        public void RoundTopRect(double x, double y, double w, double h, double r, Rgb fill)
        {
            const double k = 0.5523;
            FillColour(fill);
            Op($"{F(x)} {F(y)} m {F(x + w)} {F(y)} l {F(x + w)} {F(y + h - r)} l {F(x + w)} {F(y + h - r + r * k)} {F(x + w - r + r * k)} {F(y + h)} {F(x + w - r)} {F(y + h)} c " +
               $"{F(x + r)} {F(y + h)} l {F(x + r - r * k)} {F(y + h)} {F(x)} {F(y + h - r + r * k)} {F(x)} {F(y + h - r)} c h f");
        }

        /// <summary>A chart bar standing on <paramref name="y"/>, its top corners rounded.</summary>
        public void RoundTopBar(double x, double y, double w, double h, Rgb fill) => RoundTopRect(x, y, w, h, Math.Min(3, h / 2), fill);

        public void Circle(double cx, double cy, double r, Rgb? fill, Rgb? stroke, double lineWidth)
        {
            if (fill is { } f) FillColour(f);
            if (stroke is { } s) { StrokeColour(s); Op($"{F(lineWidth)} w"); }
            Arc(cx, cy, r, 0, 2 * Math.PI, true);
            Op("h " + (fill != null && stroke != null ? "B" : fill != null ? "f" : "S"));
        }

        /// <summary>A ring segment from angle <paramref name="a0"/> to <paramref name="a1"/> (radians, counter-clockwise from +x).</summary>
        public void Wedge(double cx, double cy, double outer, double inner, double a0, double a1, Rgb fill)
        {
            FillColour(fill);
            Arc(cx, cy, outer, a0, a1, true);
            Op($"{F(cx + inner * Math.Cos(a1))} {F(cy + inner * Math.Sin(a1))} l");
            Arc(cx, cy, inner, a1, a0, false);
            Op("h f");
            // A hairline of paper between the slices.
            StrokeColour(White);
            Op($"1.5 w {F(cx + inner * Math.Cos(a0))} {F(cy + inner * Math.Sin(a0))} m {F(cx + outer * Math.Cos(a0))} {F(cy + outer * Math.Sin(a0))} l S");
        }

        /// <summary>A circular arc as Bezier curves of at most a quarter turn each.</summary>
        private void Arc(double cx, double cy, double r, double a0, double a1, bool moveTo)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(a1 - a0) / (Math.PI / 2) - 1e-9));
            double step = (a1 - a0) / n, k = 4.0 / 3 * Math.Tan(step / 4);
            if (moveTo) Op($"{F(cx + r * Math.Cos(a0))} {F(cy + r * Math.Sin(a0))} m");
            for (int i = 0; i < n; i++)
            {
                double s = a0 + i * step, e = s + step;
                double x0 = cx + r * Math.Cos(s), y0 = cy + r * Math.Sin(s), x3 = cx + r * Math.Cos(e), y3 = cy + r * Math.Sin(e);
                Op($"{F(x0 - k * r * Math.Sin(s))} {F(y0 + k * r * Math.Cos(s))} {F(x3 + k * r * Math.Sin(e))} {F(y3 - k * r * Math.Cos(e))} {F(x3)} {F(y3)} c");
            }
        }

        public void Polygon(IReadOnlyList<(double X, double Y)> points, Rgb? fill, Rgb? stroke, double lineWidth)
        {
            if (fill is { } f) FillColour(f);
            if (stroke is { } s) { StrokeColour(s); Op($"{F(lineWidth)} w 1 j"); }
            Op($"{F(points[0].X)} {F(points[0].Y)} m " + string.Join(" ", points.Skip(1).Select(p => $"{F(p.X)} {F(p.Y)} l")) + " h " +
               (fill != null && stroke != null ? "B" : fill != null ? "f" : "S"));
        }

        public void Caption(double cx, double y, string text) => TextCenter(Font.Regular, 9, cx, y, text, BodyText);

        public void Text(Font font, double size, double x, double y, string text, Rgb colour) =>
            Op($"BT /{ResourceName(font)} {F(size)} Tf {colour} rg {F(x)} {F(y)} Td {Lit(text)} Tj ET");

        public void TextRight(Font font, double size, double right, double y, string text, Rgb colour) =>
            Text(font, size, right - Width(font, text, size), y, text, colour);

        public void TextCenter(Font font, double size, double cx, double y, string text, Rgb colour) =>
            Text(font, size, cx - Width(font, text, size) / 2, y, text, colour);

        /// <summary>Sets a paragraph from the baseline <paramref name="y"/>; returns the baseline after its last line.</summary>
        public double Paragraph(Font font, double size, double leading, double x, double y, double width, string text, Rgb colour)
        {
            foreach (var line in Wrap(font, size, text, width))
            {
                Text(font, size, x, y, line, colour);
                y -= leading;
            }
            return y;
        }

        /// <summary>A list item with a bullet (WinAnsi 0x95) and a hanging indent; returns the baseline after it.</summary>
        public double Bullet(double size, double leading, double x, double y, double width, string text, Rgb colour)
        {
            const double indent = 11;
            Text(Font.Bold, size, x, y, "•", Crimson);
            return Paragraph(Font.Regular, size, leading, x + indent, y, width - indent, text, colour);
        }

        public byte[] ToBytes() => Encoding.Latin1.GetBytes(_s.ToString());
    }
}
