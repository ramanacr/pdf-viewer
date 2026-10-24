using System.Globalization;
using System.Text;

namespace PdfViewer.Web.Engine.Demo;

/// <summary>
/// The demo viewer's built-in sample: a four-page tour of what the engine draws (paths, strokes,
/// clips, shadings, transparency and blend modes, DeviceCMYK, an image with a soft mask, a tiling
/// pattern, transformed text), with an outline. It is written by this code when asked for, so the
/// repository ships no binary sample, and its text uses <see cref="StrokeFont"/> as a Type 3 font,
/// so it renders without any installed or downloaded font and its text can be searched.
/// </summary>
public static class DemoDocument
{
    /// <summary>The words a viewer can offer to search for: each is on the pages.</summary>
    public static readonly IReadOnlyList<string> SearchExamples = ["Skia", "shading", "WebAssembly"];

    /// <summary>Page size: US Letter.</summary>
    private const double PageWidth = 612, PageHeight = 792;

    public static byte[] Create()
    {
        var w = new MiniPdfWriter();
        int catalog = w.Reserve(), pages = w.Reserve(), outlines = w.Reserve();
        int font = AddFont(w);
        int resources = AddResources(w, font);

        var contents = new[] { TourPage(), PathsPage(), ColourPage(), ImagesPage() };
        var titles = new[] { "Engine tour", "Paths, strokes and clipping", "Colour, shadings and transparency", "Images, patterns and transforms" };
        var pageIds = new int[contents.Length];
        for (int i = 0; i < contents.Length; i++)
        {
            int content = w.AddStream("", contents[i]);
            pageIds[i] = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources {resources} 0 R /Contents {content} 0 R >>");
        }
        w.Set(pages, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Length} >>");

        var items = new int[titles.Length];
        for (int i = 0; i < items.Length; i++) items[i] = w.Reserve();
        for (int i = 0; i < items.Length; i++)
        {
            var sb = new StringBuilder($"<< /Title {MiniPdfWriter.Literal(titles[i])} /Parent {outlines} 0 R /Dest [{pageIds[i]} 0 R /Fit]");
            if (i > 0) sb.Append($" /Prev {items[i - 1]} 0 R");
            if (i < items.Length - 1) sb.Append($" /Next {items[i + 1]} 0 R");
            w.Set(items[i], sb.Append(" >>").ToString());
        }
        w.Set(outlines, $"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {items.Length} >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R /PageMode /UseOutlines >>");
        int info = w.Add("<< /Title (PDF Viewer Native: engine tour) /Producer (PdfViewer.Web.Engine DemoDocument) >>");
        return w.Build(catalog, info);
    }

    // ---------------------------------------------------------------- resources

    private static int AddFont(MiniPdfWriter w)
    {
        var chars = StrokeFont.Glyphs.Keys.OrderBy(c => c).ToArray();
        var procs = new StringBuilder("<<");
        foreach (char c in chars)
        {
            var (width, outline) = StrokeFont.Glyphs[c];
            string path = outline.Length == 0 ? "" : $" 1 J 1 j {StrokeFont.StrokeWidth} w {StrokeFont.ToPath(outline)} S";
            int proc = w.AddStream("", $"{width} 0 d0{path}", flate: false);
            procs.Append($" /{StrokeFont.GlyphName(c)} {proc} 0 R");
        }
        procs.Append(" >>");
        int first = chars[0], last = chars[^1];
        var widths = new StringBuilder();
        var differences = new StringBuilder();
        int previous = -2;
        for (int code = first; code <= last; code++)
        {
            bool has = StrokeFont.Glyphs.TryGetValue((char)code, out var g);
            widths.Append(has ? g.Width : 0).Append(' ');
            if (!has) continue;
            if (code != previous + 1) differences.Append(code).Append(' ');
            differences.Append('/').Append(StrokeFont.GlyphName((char)code)).Append(' ');
            previous = code;
        }
        int toUnicode = w.AddStream("", """
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /Adobe-Identity-UCS def
            /CMapType 2 def
            1 begincodespacerange
            <00> <FF>
            endcodespacerange
            1 beginbfrange
            <20> <7E> <0020>
            endbfrange
            endcmap
            CMapName currentdict /CMap defineresource pop
            end
            end
            """);
        return w.Add($"<< /Type /Font /Subtype /Type3 /Name /DemoStroke /FontBBox [-100 -250 900 760] /FontMatrix [0.001 0 0 0.001 0 0] " +
                     $"/CharProcs {w.Add(procs.ToString())} 0 R /Encoding << /Type /Encoding /Differences [{differences.ToString().TrimEnd()}] >> " +
                     $"/FirstChar {first} /LastChar {last} /Widths [{widths.ToString().TrimEnd()}] /ToUnicode {toUnicode} 0 R /Resources << >> >>");
    }

    private static readonly string[] BlendModes = ["Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "Difference", "Exclusion"];

    private static int AddResources(MiniPdfWriter w, int font)
    {
        // Shadings: the header band, a multi-stop axial ramp and a radial highlight.
        int band = w.Add("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 612 0] /Extend [true true] /Function " +
                         Stitch(w, ["0.07 0.16 0.38", "0.00 0.47 0.56", "0.33 0.20 0.58"]) + " >>");
        int ramp = w.Add("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] /Extend [true true] /Function " +
                         Stitch(w, ["0.90 0.20 0.25", "0.98 0.70 0.10", "0.20 0.70 0.35", "0.15 0.45 0.90", "0.55 0.25 0.80"]) + " >>");
        int radial = w.Add("<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [-0.3 0.35 0.05 0 0 1] /Extend [false true] /Function " +
                           Stitch(w, ["1 1 1", "0.25 0.55 0.95", "0.05 0.12 0.35"]) + " >>");

        // A coloured tiling pattern: a checker with a dot in each light cell.
        int pattern = w.AddStream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 24 24] /XStep 24 /YStep 24 /Resources << >>",
            "0.99 0.95 0.86 rg 0 0 24 24 re f 0.93 0.45 0.12 rg 0 0 12 12 re f 12 12 12 12 re f 0.13 0.36 0.70 rg " + Circle(18, 6, 3.2) + " f " + Circle(6, 18, 3.2) + " f");

        // An image with a soft mask: smooth colour waves inside a soft-edged disc.
        const int size = 160;
        var rgb = new byte[size * size * 3];
        var alpha = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                rgb[i * 3] = (byte)(128 + 127 * Math.Sin(x * 0.07 + 0.5));
                rgb[i * 3 + 1] = (byte)(128 + 127 * Math.Sin(y * 0.06 + 2.0));
                rgb[i * 3 + 2] = (byte)(128 + 127 * Math.Sin((x + y) * 0.05 + 4.0));
                double d = Math.Sqrt((x - 79.5) * (x - 79.5) + (y - 79.5) * (y - 79.5));
                alpha[i] = (byte)Math.Clamp((78 - d) / 8 * 255, 0, 255);
            }
        int mask = w.AddStream($"/Type /XObject /Subtype /Image /Width {size} /Height {size} /ColorSpace /DeviceGray /BitsPerComponent 8", alpha);
        int image = w.AddStream($"/Type /XObject /Subtype /Image /Width {size} /Height {size} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R", rgb);

        var states = new StringBuilder("/Half << /ca 0.55 /CA 0.55 >> /Venn << /BM /Multiply /ca 0.9 >>");
        foreach (var mode in BlendModes) states.Append($" /BM{mode} << /BM /{mode} >>");
        return w.Add($"<< /Font << /F1 {font} 0 R >> /ExtGState << {states} >> /Shading << /Band {band} 0 R /Ramp {ramp} 0 R /Radial {radial} 0 R >> " +
                     $"/Pattern << /Checker {pattern} 0 R >> /XObject << /Waves {image} 0 R >> /ProcSet [/PDF /ImageC] >>");
    }

    /// <summary>A stitching function through evenly spaced RGB stops.</summary>
    private static string Stitch(MiniPdfWriter w, string[] stops)
    {
        var parts = new List<string>();
        for (int i = 0; i + 1 < stops.Length; i++)
            parts.Add($"{w.Add($"<< /FunctionType 2 /Domain [0 1] /C0 [{stops[i]}] /C1 [{stops[i + 1]}] /N 1 >>")} 0 R");
        int n = parts.Count;
        string bounds = string.Join(" ", Enumerable.Range(1, n - 1).Select(i => F((double)i / n)));
        string encode = string.Join(" ", Enumerable.Repeat("0 1", n));
        return $"<< /FunctionType 3 /Domain [0 1] /Functions [{string.Join(" ", parts)}] /Bounds [{bounds}] /Encode [{encode}] >>";
    }

    // ---------------------------------------------------------------- pages

    private static string TourPage()
    {
        var c = new Content();
        c.Raw("q 0 652 612 140 re W n /Band sh Q");
        c.Text(54, 82, 30, "PDF Viewer Native", "1 1 1");
        c.Text(54, 112, 13, "An engine tour, written and drawn in your browser", "0.86 0.93 1");

        double y = 180;
        y = c.Paragraph(54, y, 504, 11.5, 17,
            "This document was written a moment ago by the engine's own code, running in your browser as WebAssembly. " +
            "No server produced it and nothing has been uploaded: the PDF bytes, the parser, the renderer and the text search all live in this tab.");
        y = c.Paragraph(54, y + 10, 504, 11.5, 17,
            "Every page is drawn by PdfEngine.Vector, the viewer's own PDF engine, through its portable Skia backend. " +
            "The text you are reading uses a Type 3 font whose letters are small vector drawings inside the file, so it needs no font from your computer.");
        y = c.Paragraph(54, y + 10, 504, 11.5, 17,
            "Try it: search for Skia or shading, zoom in with Ctrl and the plus key, switch to dark mode, or open the outline. " +
            "Then open one of your own PDF files: drop it anywhere on the page.");

        y += 30;
        c.Text(54, y, 15, "On the next pages", "0.07 0.16 0.38");
        string[] entries =
        [
            "2   Paths, strokes and clipping: caps, joins, dashes, curves, fill rules, clips, hairlines",
            "3   Colour, shadings and transparency: axial and radial shadings, DeviceCMYK, blend modes",
            "4   Images, patterns and transforms: a soft-masked image, a tiling pattern, rotated text",
        ];
        foreach (var e in entries)
        {
            y += 26;
            c.Raw($"0.00 0.47 0.56 rg {F(54)} {F(PageHeight - y - 1)} 6 6 re f");
            c.Text(70, y + 5, 10.5, e, "0.15 0.15 0.2");
        }

        // A small drawing: concentric rounded squares in the band colours.
        for (int i = 0; i < 6; i++)
        {
            double s = 150 - i * 22, x = 306 - s / 2, top = 560 + i * 11;
            c.Raw($"q {F(0.07 + i * 0.05)} {F(0.30 + i * 0.07)} {F(0.56 - i * 0.03)} RG 3 w {RoundRect(x, PageHeight - top - s / 1.6, s, s / 1.6, 14)} S Q");
        }
        c.Text(54, 750, 8.5, "Generated by PdfViewer.Web.Engine.DemoDocument. MIT License.", "0.45 0.45 0.5");
        return c.ToString();
    }

    private static string PathsPage()
    {
        var c = new Content();
        PageTitle(c, "Paths, strokes and clipping");

        // Line caps.
        var (x, top) = Cell(0, 0, c, "Line caps: butt, round, square");
        string[] caps = ["0", "1", "2"];
        for (int i = 0; i < 3; i++)
        {
            double yy = PageHeight - (top + 50 + i * 42);
            c.Raw($"q 0.13 0.36 0.70 RG 16 w {caps[i]} J {F(x + 30)} {F(yy)} m {F(x + 200)} {F(yy)} l S Q");
            c.Raw($"q 0.9 0.2 0.25 RG 0.6 w {F(x + 30)} {F(yy - 14)} m {F(x + 30)} {F(yy + 14)} l {F(x + 200)} {F(yy - 14)} m {F(x + 200)} {F(yy + 14)} l S Q");
        }

        // Line joins.
        (x, top) = Cell(0, 1, c, "Line joins: miter, round, bevel");
        for (int i = 0; i < 3; i++)
        {
            double bx = x + 15 + i * 78, by = PageHeight - (top + 150);
            c.Raw($"q 0.00 0.47 0.56 RG 13 w {i} j 0 J {F(bx)} {F(by)} m {F(bx + 30)} {F(by + 100)} l {F(bx + 60)} {F(by)} l S Q");
        }

        // Dash patterns.
        (x, top) = Cell(1, 0, c, "Dash patterns");
        string[] dashes = ["[6 3] 0 d 0 J", "[14 4 3 4] 0 d 0 J", "[0.1 7] 0 d 1 J", "[22 6] 9 d 2 J"];
        for (int i = 0; i < dashes.Length; i++)
        {
            double yy = PageHeight - (top + 45 + i * 34);
            c.Raw($"q 0.20 0.20 0.28 RG {(i == 2 ? 5 : 3)} w {dashes[i]} {F(x + 20)} {F(yy)} m {F(x + 220)} {F(yy)} l S Q");
        }

        // Bezier curves with their control polygons.
        (x, top) = Cell(1, 1, c, "Cubic Bezier curves");
        double b0 = PageHeight - (top + 140);
        var pts = new[] { (x + 10, b0), (x + 50, b0 + 120), (x + 110, b0 - 20), (x + 130, b0 + 60), (x + 150, b0 + 140), (x + 200, b0 + 110), (x + 230, b0 + 10) };
        c.Raw($"q 0.6 0.6 0.65 RG 0.7 w [3 2] 0 d {string.Join(" ", pts.Select((p, i) => $"{F(p.Item1)} {F(p.Item2)} {(i == 0 ? "m" : "l")}"))} S Q");
        c.Raw($"q 0.55 0.25 0.80 RG 3.5 w {F(pts[0].Item1)} {F(pts[0].Item2)} m " +
              $"{F(pts[1].Item1)} {F(pts[1].Item2)} {F(pts[2].Item1)} {F(pts[2].Item2)} {F(pts[3].Item1)} {F(pts[3].Item2)} c " +
              $"{F(pts[4].Item1)} {F(pts[4].Item2)} {F(pts[5].Item1)} {F(pts[5].Item2)} {F(pts[6].Item1)} {F(pts[6].Item2)} c S Q");
        foreach (var p in pts) c.Raw($"q 0.9 0.2 0.25 rg {Circle(p.Item1, p.Item2, 2.6)} f Q");

        // Fill rules.
        (x, top) = Cell(2, 0, c, "Fill rules: nonzero and even-odd");
        for (int i = 0; i < 2; i++)
        {
            double cx = x + 62 + i * 122, cy = PageHeight - (top + 100);
            var star = new StringBuilder();
            for (int k = 0; k < 5; k++)
            {
                double a = Math.PI / 2 + k * 4 * Math.PI / 5;
                star.Append($"{F(cx + 58 * Math.Cos(a))} {F(cy + 58 * Math.Sin(a))} {(k == 0 ? "m" : "l")} ");
            }
            c.Raw($"q 0.98 0.70 0.10 rg 0.35 0.25 0.05 RG 1.2 w {star}h {(i == 0 ? "B" : "B*")} Q");
        }

        // Clipping and hairlines.
        (x, top) = Cell(2, 1, c, "Clipping and hairlines");
        double ccx = x + 70, ccy = PageHeight - (top + 100);
        c.Raw($"q {Circle(ccx, ccy, 58)} W n");
        string[] stripe = ["0.90 0.20 0.25", "0.98 0.70 0.10", "0.20 0.70 0.35", "0.15 0.45 0.90"];
        for (int i = -8; i < 8; i++)
            c.Raw($"{stripe[(i + 8) % 4]} rg {F(ccx + i * 14)} {F(ccy - 70)} m {F(ccx + i * 14 + 14)} {F(ccy - 70)} l {F(ccx + i * 14 + 64)} {F(ccy + 70)} l {F(ccx + i * 14 + 50)} {F(ccy + 70)} l f");
        c.Raw("Q");
        double hx = x + 150, hy = PageHeight - (top + 160);
        for (int i = 0; i <= 12; i++)
        {
            double a = i * Math.PI / 24;
            c.Raw($"q 0.2 0.2 0.28 RG 0 w {F(hx)} {F(hy)} m {F(hx + 90 * Math.Cos(a))} {F(hy + 120 * Math.Sin(a))} l S Q");
        }

        c.Paragraph(54, 742, 504, 9.5, 14,
            "Strokes have their width in user space. A stroke narrower than one device pixel, such as the fan of zero-width lines above, is drawn as a one-pixel hairline at every zoom.");
        return c.ToString();
    }

    private static string ColourPage()
    {
        var c = new Content();
        PageTitle(c, "Colour, shadings and transparency");

        var (x, top) = Cell(0, 0, c, "Axial shading, five stops");
        c.Raw($"q {F(x + 10)} {F(PageHeight - top - 160)} 220 120 re W n 220 0 0 1 {F(x + 10)} 0 cm /Ramp sh Q");

        (x, top) = Cell(0, 1, c, "Radial shading");
        double rcx = x + 120, rcy = PageHeight - (top + 100);
        c.Raw($"q {Circle(rcx, rcy, 64)} W n 64 0 0 64 {F(rcx)} {F(rcy)} cm /Radial sh Q");

        (x, top) = Cell(1, 0, c, "Transparency: Multiply");
        string[] venn = ["0 0.68 0.94", "0.93 0.0 0.55", "1.0 0.9 0.0"];
        double vx = x + 120, vy = PageHeight - (top + 105);
        (double, double)[] at = [(vx - 32, vy + 20), (vx + 32, vy + 20), (vx, vy - 34)];
        for (int i = 0; i < 3; i++) c.Raw($"q /Venn gs {venn[i]} rg {Circle(at[i].Item1, at[i].Item2, 48)} f Q");

        (x, top) = Cell(1, 1, c, "DeviceCMYK, built-in SWOP model");
        string[] cmyk = ["1 0 0 0", "0 1 0 0", "0 0 1 0", "0 0 0 1", "0 1 1 0", "1 0 1 0", "1 1 0 0", "0.6 0.4 0.4 1"];
        for (int i = 0; i < cmyk.Length; i++)
        {
            double sx = x + 12 + (i % 4) * 56, sy = PageHeight - (top + 82 + (i / 4) * 62);
            c.Raw($"q {cmyk[i]} k {F(sx)} {F(sy)} 46 46 re f Q");
        }

        // Blend modes over a ramp: one square per mode.
        double row = 552;
        c.Text(54, row - 8, 12, "Blend modes", "0.07 0.16 0.38");
        c.Raw($"q 54 {F(PageHeight - row - 110)} 504 80 re W n 504 0 0 1 54 0 cm /Ramp sh Q");
        for (int i = 0; i < BlendModes.Length; i++)
        {
            double bx = 58 + i * 63;
            c.Raw($"q /BM{BlendModes[i]} gs 0.10 0.30 0.75 rg {F(bx)} {F(PageHeight - row - 92)} 55 44 re f Q");
            c.Text(bx, row + 128, 7.5, BlendModes[i], "0.2 0.2 0.28");
        }

        c.Paragraph(54, 712, 504, 9.5, 14,
            "Shadings are evaluated per device pixel where Skia has no matching gradient. Groups, soft masks and the sixteen PDF blend modes are composited as ISO 32000-2 defines them.");
        return c.ToString();
    }

    private static string ImagesPage()
    {
        var c = new Content();
        PageTitle(c, "Images, patterns and transforms");

        var (x, top) = Cell(0, 0, c, "Image with a soft mask");
        c.Raw($"q 150 0 0 150 {F(x + 40)} {F(PageHeight - top - 175)} cm /Waves Do Q");

        (x, top) = Cell(0, 1, c, "The same image, rotated and skewed");
        double ia = 25 * Math.PI / 180;
        c.Raw($"q 1 0 0 1 {F(x + 110)} {F(PageHeight - top - 175)} cm {F(Math.Cos(ia))} {F(Math.Sin(ia))} {F(-Math.Sin(ia))} {F(Math.Cos(ia))} 0 0 cm 1 0 0.35 1 0 0 cm 110 0 0 110 0 0 cm /Waves Do Q");

        (x, top) = Cell(1, 0, c, "Tiling pattern");
        c.Raw($"q /Pattern cs /Checker scn 0.35 0.25 0.05 RG 1 w {RoundRect(x + 10, PageHeight - top - 175, 220, 140, 18)} B Q");

        (x, top) = Cell(1, 1, c, "Transformed text");
        // Spokes: each line starts a little way out from one pivot, turned by its angle.
        double px = x + 22, py = PageHeight - top - 104;
        for (int i = 0; i < 5; i++)
        {
            int degrees = -40 + i * 20;
            double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            c.Raw($"BT 0.13 0.36 0.70 rg 0.13 0.36 0.70 RG /F1 11 Tf {F(cos)} {F(sin)} {F(-sin)} {F(cos)} {F(px + 44 * cos)} {F(py + 44 * sin)} Tm (turned {degrees} degrees) Tj ET");
        }
        c.Raw($"q 0.9 0.2 0.25 rg {Circle(px, py, 3)} f Q");

        double y = c.Paragraph(54, 590, 504, 11, 16,
            "Open one of your own PDF files to see how far the engine gets with real documents. It runs here exactly as in the desktop viewer, " +
            "with two differences: a browser has no installed fonts, so text in fonts that a PDF does not embed is not drawn unless you allow this page to use your device's fonts, " +
            "and four-component (CMYK) JPEG images are not decoded yet.");
        c.Paragraph(54, y + 10, 504, 11, 16,
            "Nothing you open is uploaded, stored or measured. Closing the tab forgets it.");
        return c.ToString();
    }

    private static void PageTitle(Content c, string title)
    {
        c.Raw("q 0 770 612 22 re W n /Band sh Q");
        c.Text(54, 62, 20, title, "0.07 0.16 0.38");
        c.Raw($"q 0.00 0.47 0.56 RG 1.5 w 54 {F(PageHeight - 74)} m 558 {F(PageHeight - 74)} l S Q");
    }

    /// <summary>Draws a cell's label and returns its left edge and top (distance from the page top).</summary>
    private static (double X, double Top) Cell(int row, int column, Content c, string label)
    {
        double x = 54 + column * 264, top = 100 + row * 205;
        c.Text(x, top + 12, 11, label, "0.07 0.16 0.38");
        c.Raw($"q 0.85 0.87 0.9 RG 0.75 w {F(x)} {F(PageHeight - top - 190)} 240 172 re S Q");
        return (x, top);
    }

    private static string Circle(double cx, double cy, double r)
    {
        const double k = 0.5523;
        double d = r * k;
        return $"{F(cx + r)} {F(cy)} m {F(cx + r)} {F(cy + d)} {F(cx + d)} {F(cy + r)} {F(cx)} {F(cy + r)} c " +
               $"{F(cx - d)} {F(cy + r)} {F(cx - r)} {F(cy + d)} {F(cx - r)} {F(cy)} c " +
               $"{F(cx - r)} {F(cy - d)} {F(cx - d)} {F(cy - r)} {F(cx)} {F(cy - r)} c " +
               $"{F(cx + d)} {F(cy - r)} {F(cx + r)} {F(cy - d)} {F(cx + r)} {F(cy)} c h";
    }

    private static string RoundRect(double x, double y, double w, double h, double r)
    {
        double d = r * 0.5523;
        return $"{F(x + r)} {F(y)} m {F(x + w - r)} {F(y)} l {F(x + w - r + d)} {F(y)} {F(x + w)} {F(y + r - d)} {F(x + w)} {F(y + r)} c " +
               $"{F(x + w)} {F(y + h - r)} l {F(x + w)} {F(y + h - r + d)} {F(x + w - r + d)} {F(y + h)} {F(x + w - r)} {F(y + h)} c " +
               $"{F(x + r)} {F(y + h)} l {F(x + r - d)} {F(y + h)} {F(x)} {F(y + h - r + d)} {F(x)} {F(y + h - r)} c " +
               $"{F(x)} {F(y + r)} l {F(x)} {F(y + r - d)} {F(x + r - d)} {F(y)} {F(x + r)} {F(y)} c h";
    }

    private static string F(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Page content with text placed from the page top.</summary>
    private sealed class Content
    {
        private readonly StringBuilder _sb = new();

        public void Raw(string operators) => _sb.Append(operators).Append('\n');

        /// <summary>One line of text; <paramref name="top"/> is the baseline's distance from the page top.</summary>
        public void Text(double x, double top, double size, string text, string rgb) =>
            Raw($"BT {rgb} rg {rgb} RG /F1 {F(size)} Tf {F(x)} {F(PageHeight - top)} Td {MiniPdfWriter.Literal(StrokeFont.Supported(text))} Tj ET");

        /// <summary>Wrapped text; returns the distance from the page top below its last line.</summary>
        public double Paragraph(double x, double top, double width, double size, double leading, string text)
        {
            var line = new StringBuilder();
            double y = top + size;
            foreach (var word in text.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && StrokeFont.Measure(candidate, size) > width)
                {
                    Text(x, y, size, line.ToString(), "0.15 0.15 0.2");
                    line.Clear().Append(word);
                    y += leading;
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }
            if (line.Length > 0) Text(x, y, size, line.ToString(), "0.15 0.15 0.2");
            return y + leading - size;
        }

        public override string ToString() => _sb.ToString();
    }
}
