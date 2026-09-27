using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Annotations;

/// <summary>
/// Draws the normal appearance (/AP /N, ISO 32000-2 12.5.5) of an annotation that has none, the
/// way Acrobat draws it: shapes and lines from their geometry with /C, /IC, /BS or /Border, /BE
/// cloudy borders, /RD and line endings; text markup from the quadrilaterals; icons for notes,
/// attachments and carets; stamps and free text with an embedded font subset. The appearance is a
/// Form XObject whose /BBox is the annotation rectangle moved to the origin, so the 12.5.5
/// algorithm maps it exactly onto /Rect. Its content uses only device colours in the space /C is
/// given in, opacity through /CA and /ca, and the Multiply blend mode for highlights, all of which
/// PDF/A-2 allows.
/// </summary>
public static class PdfAnnotationAppearances
{
    /// <summary>
    /// Builds a normal appearance for an annotation that lacks one, or null when the type is not supported.
    /// newObject is called to add supporting objects (e.g. an embedded font) and returns their object numbers.
    /// </summary>
    public static PdfStream? Generate(PdfDictionary annotation, PdfObjectResolver resolver, Func<PdfObject, int> newObject)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(newObject);

        string? subtype = resolver.Resolve(annotation["Subtype"]) is PdfName n ? n.Value : null;
        if (subtype == null) return null;
        var painter = new Painter(annotation, resolver, newObject);
        if (painter.Width < 0 || painter.Height < 0) return null;

        bool supported = subtype switch
        {
            "Square" => painter.Square(),
            "Circle" => painter.Circle(),
            "Line" => painter.Line(),
            "Polygon" => painter.Poly(closed: true),
            "PolyLine" => painter.Poly(closed: false),
            "Ink" => painter.Ink(),
            "Highlight" => painter.Highlight(),
            "Underline" => painter.TextLine(TextLineKind.Underline),
            "StrikeOut" => painter.TextLine(TextLineKind.StrikeOut),
            "Squiggly" => painter.TextLine(TextLineKind.Squiggly),
            "Text" => painter.Note(),
            "FreeText" => painter.FreeText(),
            "Stamp" => painter.Stamp(),
            "Caret" => painter.Caret(),
            "FileAttachment" => painter.FileAttachment(),
            _ => false,
        };
        return supported ? painter.Finish() : null;
    }

    private enum TextLineKind { Underline, StrikeOut, Squiggly }

    /// <summary>A point or vector in the appearance's coordinates.</summary>
    private readonly record struct Pt(double X, double Y)
    {
        public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
        public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
        public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public Pt Unit => Length > 1e-9 ? this * (1 / Length) : new Pt(1, 0);
        /// <summary>The vector turned a quarter counter-clockwise.</summary>
        public Pt Left => new(-Y, X);
        public double Dot(Pt o) => X * o.X + Y * o.Y;
    }

    /// <summary>The annotation's border: width, style (S, D, B, I, U) and dash pattern.</summary>
    private readonly record struct Border(double Width, string Style, double[]? Dash);

    /// <summary>One glyph of laid-out text: its font, glyph id, the text it stands for and its advance in 1/1000 em.</summary>
    private readonly record struct Glyph(EmbeddedFontBuilder Font, int Gid, string Text, double Advance, bool IsSpace);

    /// <summary>The /DA string: font name, size (0 = auto), fill and stroke colours.</summary>
    private sealed record DefaultAppearance(string? FontName, double Size, double[]? Fill, double[]? Stroke);

    private const double Kappa = 0.5522847498307936;

    private sealed class Painter
    {
        private readonly PdfDictionary _annot;
        private readonly PdfObjectResolver _r;
        private readonly Func<PdfObject, int> _newObject;
        private readonly StringBuilder _c = new();
        private readonly Dictionary<string, PdfObject> _gstates = new();
        private FontSet? _fonts;

        /// <summary>The lower-left corner of /Rect in page space: the appearance's origin.</summary>
        private readonly double _x0, _y0;
        public double Width { get; }
        public double Height { get; }

        public Painter(PdfDictionary annot, PdfObjectResolver resolver, Func<PdfObject, int> newObject)
        {
            _annot = annot;
            _r = resolver;
            _newObject = newObject;
            var rect = Numbers("Rect");
            if (rect is not { Length: 4 })
            {
                Width = Height = -1;
                return;
            }
            _x0 = Math.Min(rect[0], rect[2]);
            _y0 = Math.Min(rect[1], rect[3]);
            Width = Math.Abs(rect[2] - rect[0]);
            Height = Math.Abs(rect[3] - rect[1]);
        }

        private FontSet Fonts => _fonts ??= new FontSet();

        // ------------------------------------------------------------------ reading the annotation

        private PdfObject? Get(string key) => _r.Resolve(_annot[key]);

        private double? Number(string key) => Get(key) is { } o && o.TryGetNumber(out double v) && double.IsFinite(v) ? v : null;

        private string? Name(string key) => Get(key) is PdfName n ? n.Value : null;

        private string? Text(string key) => Get(key) is PdfString s ? s.AsDecodedString() : null;

        private double[]? Numbers(string key) => NumbersOf(Get(key));

        private double[]? NumbersOf(PdfObject? obj)
        {
            if (obj is not PdfArray a) return null;
            var values = new double[a.Count];
            for (int i = 0; i < a.Count; i++)
            {
                if (_r.Resolve(a[i]) is not { } item || !item.TryGetNumber(out values[i]) || !double.IsFinite(values[i]))
                    return null;
            }
            return values;
        }

        /// <summary>A colour array: null when absent or malformed, empty when transparent.</summary>
        private double[]? Colour(string key)
        {
            var c = Numbers(key);
            return c is { Length: 0 or 1 or 3 or 4 } ? c.Select(v => Math.Clamp(v, 0, 1)).ToArray() : null;
        }

        private double Opacity => Math.Clamp(Number("CA") ?? 1, 0, 1);

        private Pt Local(double x, double y) => new(x - _x0, y - _y0);

        /// <summary>/RD: left, bottom, right, top insets of the drawing inside /Rect.</summary>
        private (double L, double B, double R, double T) Differences()
        {
            var rd = Numbers("RD");
            if (rd is not { Length: 4 } || rd.Any(v => v < 0) || rd[0] + rd[2] >= Width || rd[1] + rd[3] >= Height)
                return (0, 0, 0, 0);
            return (rd[0], rd[1], rd[2], rd[3]);
        }

        /// <summary>The border from /BS, else /Border, else the default solid line.</summary>
        private Border ReadBorder(double defaultWidth = 1)
        {
            if (Get("BS") is PdfDictionary bs)
            {
                double w = _r.Resolve(bs["W"]) is { } wo && wo.TryGetNumber(out double wv) && double.IsFinite(wv) ? Math.Max(0, wv) : 1;
                string style = _r.Resolve(bs["S"]) is PdfName s ? s.Value : "S";
                double[]? dash = null;
                if (style == "D")
                    dash = ValidDash(NumbersOf(_r.Resolve(bs["D"]))) ?? new[] { 3.0 };
                return new Border(w, style, dash);
            }
            if (Get("Border") is PdfArray border && border.Count >= 3 && _r.Resolve(border[2]) is { } bw && bw.TryGetNumber(out double width) && double.IsFinite(width))
            {
                var dash = border.Count >= 4 ? ValidDash(NumbersOf(_r.Resolve(border[3]))) : null;
                return new Border(Math.Max(0, width), dash != null ? "D" : "S", dash);
            }
            return new Border(defaultWidth, "S", null);
        }

        private static double[]? ValidDash(double[]? dash) =>
            dash is { Length: > 0 } && dash.All(v => v >= 0) && dash.Sum() > 0 ? dash : null;

        /// <summary>The /BE cloudy intensity (0 when the border is not cloudy).</summary>
        private double CloudIntensity()
        {
            if (Get("BE") is not PdfDictionary be || _r.Resolve(be["S"]) is not PdfName { Value: "C" }) return 0;
            return _r.Resolve(be["I"]) is { } i && i.TryGetNumber(out double v) && double.IsFinite(v) ? Math.Clamp(v, 0, 2) : 0;
        }

        /// <summary>Radius of a cloud's bumps, as Acrobat sizes them for an intensity.</summary>
        private static double CloudRadius(double intensity, double lineWidth) => 4.75 * intensity + 0.5 * lineWidth;

        // ------------------------------------------------------------------ writing content

        private static string F(double v)
        {
            double r = Math.Round(v, 4);
            if (r == 0 || !double.IsFinite(r)) return "0";
            return r.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void Op(string s) => _c.Append(s).Append('\n');
        private void M(Pt p) => _c.Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(" m\n");
        private void L(Pt p) => _c.Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(" l\n");
        private void C(Pt a, Pt b, Pt p) =>
            _c.Append(F(a.X)).Append(' ').Append(F(a.Y)).Append(' ').Append(F(b.X)).Append(' ').Append(F(b.Y)).Append(' ')
              .Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(" c\n");
        private void Re(double x, double y, double w, double h) => Op($"{F(x)} {F(y)} {F(w)} {F(h)} re");

        private static string? ColourOp(double[]? c, bool stroke) => c?.Length switch
        {
            1 => $"{F(c[0])} {(stroke ? "G" : "g")}",
            3 => $"{F(c[0])} {F(c[1])} {F(c[2])} {(stroke ? "RG" : "rg")}",
            4 => $"{F(c[0])} {F(c[1])} {F(c[2])} {F(c[3])} {(stroke ? "K" : "k")}",
            _ => null,
        };

        /// <summary>Sets the fill and stroke colours (outside any path, as 8.5.1 requires).</summary>
        private void Colours(string? fill, string? stroke)
        {
            if (fill != null) Op(fill);
            if (stroke != null) Op(stroke);
        }

        private void Paint(string? fill, string? stroke, bool close = false, bool evenOdd = false)
        {
            if (fill != null && stroke != null) Op((close ? "b" : "B") + (evenOdd ? "*" : string.Empty));
            else if (fill != null) Op(evenOdd ? "f*" : "f");
            else if (stroke != null) Op(close ? "s" : "S");
            else Op("n");
        }

        private void LineStyle(double width, double[]? dash)
        {
            Op($"{F(width)} w");
            if (dash != null) Op($"[{string.Join(' ', dash.Select(F))}] 0 d");
        }

        /// <summary>An ExtGState for the annotation's opacity (and blend mode), selected with gs.</summary>
        private void GraphicsState(double opacity, bool multiply = false)
        {
            if (opacity >= 1 && !multiply) return;
            var entries = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("ExtGState") };
            if (opacity < 1)
            {
                entries["CA"] = new PdfReal(Math.Round(opacity, 4));
                entries["ca"] = new PdfReal(Math.Round(opacity, 4));
            }
            if (multiply) entries["BM"] = new PdfName("Multiply");
            string name = "GS" + _gstates.Count;
            _gstates[name] = new PdfDictionary(entries);
            Op($"/{name} gs");
        }

        private void Ellipse(double cx, double cy, double rx, double ry)
        {
            double kx = rx * Kappa, ky = ry * Kappa;
            M(new Pt(cx + rx, cy));
            C(new Pt(cx + rx, cy + ky), new Pt(cx + kx, cy + ry), new Pt(cx, cy + ry));
            C(new Pt(cx - kx, cy + ry), new Pt(cx - rx, cy + ky), new Pt(cx - rx, cy));
            C(new Pt(cx - rx, cy - ky), new Pt(cx - kx, cy - ry), new Pt(cx, cy - ry));
            C(new Pt(cx + kx, cy - ry), new Pt(cx + rx, cy - ky), new Pt(cx + rx, cy));
            Op("h");
        }

        private void RoundedRect(double x, double y, double w, double h, double radius)
        {
            double r = Math.Min(radius, Math.Min(w, h) / 2), k = r * (1 - Kappa);
            M(new Pt(x + r, y));
            L(new Pt(x + w - r, y));
            C(new Pt(x + w - k, y), new Pt(x + w, y + k), new Pt(x + w, y + r));
            L(new Pt(x + w, y + h - r));
            C(new Pt(x + w, y + h - k), new Pt(x + w - k, y + h), new Pt(x + w - r, y + h));
            L(new Pt(x + r, y + h));
            C(new Pt(x + k, y + h), new Pt(x, y + h - k), new Pt(x, y + h - r));
            L(new Pt(x, y + r));
            C(new Pt(x, y + k), new Pt(x + k, y), new Pt(x + r, y));
            Op("h");
        }

        /// <summary>A circular arc from the current point, counter-clockwise by <paramref name="sweep"/> radians, in Bézier pieces of at most 90 degrees.</summary>
        private void Arc(Pt centre, double radius, double start, double sweep)
        {
            int pieces = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2) - 1e-9));
            double step = sweep / pieces;
            double k = 4.0 / 3.0 * Math.Tan(step / 4) * radius;
            double a = start;
            for (int i = 0; i < pieces; i++)
            {
                double b = a + step;
                var p0 = centre + new Pt(Math.Cos(a), Math.Sin(a)) * radius;
                var p3 = centre + new Pt(Math.Cos(b), Math.Sin(b)) * radius;
                var c1 = p0 + new Pt(-Math.Sin(a), Math.Cos(a)) * k;
                var c2 = p3 + new Pt(Math.Sin(b), -Math.Cos(b)) * k;
                C(c1, c2, p3);
                a = b;
            }
        }

        /// <summary>
        /// A cloudy outline (12.5.4, /BE /S /C) around a closed polygon: overlapping circles
        /// whose centres run along the polygon, joined at their outer intersections, so the
        /// bumps bulge outward and meet in cusps. Corners of a straight-edged shape each get a
        /// bump; curves (<paramref name="byArcLength"/>) get evenly spaced ones.
        /// </summary>
        private void Cloud(IReadOnlyList<Pt> polygon, double radius, bool byArcLength)
        {
            var pts = polygon.ToList();
            double area = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            if (area < 0) pts.Reverse(); // counter-clockwise, so the outside is on the right

            double spacing = radius * 1.5;
            var centres = new List<Pt>();
            if (byArcLength)
            {
                double perimeter = 0;
                for (int i = 0; i < pts.Count; i++) perimeter += (pts[(i + 1) % pts.Count] - pts[i]).Length;
                int count = Math.Max(3, (int)Math.Round(perimeter / spacing));
                double step = perimeter / count, travelled = 0, next = 0;
                for (int i = 0; i < pts.Count && centres.Count < count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    double len = (b - a).Length;
                    while (next <= travelled + len + 1e-9 && centres.Count < count)
                    {
                        centres.Add(a + (b - a) * (len > 0 ? (next - travelled) / len : 0));
                        next += step;
                    }
                    travelled += len;
                }
            }
            else
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    double len = (b - a).Length;
                    if (len < 1e-6) continue;
                    int count = Math.Max(1, (int)Math.Ceiling(len / spacing - 1e-9));
                    for (int k = 0; k < count; k++) centres.Add(a + (b - a) * ((double)k / count));
                }
            }
            for (int i = centres.Count - 1; i > 0 && centres.Count > 3; i--)
                if ((centres[i] - centres[i - 1]).Length < radius * 0.1) centres.RemoveAt(i);

            if (centres.Count < 3)
            {
                M(pts[0]);
                foreach (var p in pts.Skip(1)) L(p);
                Op("h");
                return;
            }

            int n = centres.Count;
            // cusps[i]: where bump i meets bump i + 1, on the outside.
            var cusps = new Pt[n];
            for (int i = 0; i < n; i++)
            {
                var a = centres[i]; var b = centres[(i + 1) % n];
                double d = (b - a).Length;
                var mid = (a + b) * 0.5;
                var right = new Pt((b - a).Unit.Y, -(b - a).Unit.X);
                double h = d < 2 * radius ? Math.Sqrt(radius * radius - d * d / 4) : 0;
                cusps[i] = mid + right * h;
            }
            M(cusps[n - 1]);
            for (int i = 0; i < n; i++)
            {
                var from = cusps[(i + n - 1) % n] - centres[i];
                var to = cusps[i] - centres[i];
                double a0 = Math.Atan2(from.Y, from.X), a1 = Math.Atan2(to.Y, to.X);
                double sweep = a1 - a0;
                while (sweep <= 0) sweep += 2 * Math.PI;
                if (sweep > 1.75 * Math.PI) L(cusps[i]); // swallowed by its neighbours (a concave corner)
                else Arc(centres[i], radius, a0, sweep);
            }
            Op("h");
        }

        /// <summary>The form: /BBox the rectangle at the origin, Flate content, its own resources.</summary>
        public PdfStream Finish()
        {
            var resources = new Dictionary<string, PdfObject>();
            if (_gstates.Count > 0) resources["ExtGState"] = new PdfDictionary(_gstates);
            if (_fonts != null)
            {
                var fonts = new Dictionary<string, PdfObject>();
                foreach (var builder in _fonts.Used)
                    fonts[builder.ResourceName] = new PdfIndirectRef(WriteFont(builder));
                if (fonts.Count > 0) resources["Font"] = new PdfDictionary(fonts);
            }
            var dict = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"),
                ["Subtype"] = new PdfName("Form"),
                ["FormType"] = new PdfInteger(1),
                ["BBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(Math.Round(Width, 4)), new PdfReal(Math.Round(Height, 4)) }),
                ["Resources"] = new PdfDictionary(resources),
                ["Filter"] = new PdfName("FlateDecode"),
            };
            return PdfObjectWriter.NewStream(dict, Redaction.ContentRedactor.Deflate(Encoding.Latin1.GetBytes(_c.ToString())));
        }

        /// <summary>Writes an embedded font's objects through newObject, dependencies first; returns the Type0 font's number.</summary>
        private int WriteFont(EmbeddedFontBuilder builder)
        {
            var local = new Dictionary<int, PdfObject>();
            int next = 0;
            int root = builder.Write(() => ++next, local);
            var numbers = new Dictionary<int, int>();

            int Emit(int n)
            {
                if (numbers.TryGetValue(n, out int done)) return done;
                var obj = local[n];
                foreach (int dependency in References(obj).Distinct())
                    if (local.ContainsKey(dependency)) Emit(dependency);
                int number = _newObject(Renumber(obj, numbers));
                numbers[n] = number;
                return number;
            }
            return Emit(root);
        }

        private static IEnumerable<int> References(PdfObject obj)
        {
            switch (obj)
            {
                case PdfIndirectRef r: yield return r.ObjectNumber; break;
                case PdfArray a: foreach (var item in a) foreach (int n in References(item)) yield return n; break;
                case PdfDictionary d: foreach (var v in d.Entries.Values) foreach (int n in References(v)) yield return n; break;
                case PdfStream s: foreach (int n in References(s.Dictionary)) yield return n; break;
            }
        }

        private static PdfObject Renumber(PdfObject obj, IReadOnlyDictionary<int, int> numbers) => obj switch
        {
            PdfIndirectRef r => numbers.TryGetValue(r.ObjectNumber, out int n) ? new PdfIndirectRef(n) : r,
            PdfArray a => new PdfArray(a.Items.Select(i => Renumber(i, numbers)).ToList()),
            PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(e => e.Key, e => Renumber(e.Value, numbers))),
            PdfStream s => s with { Dictionary = (PdfDictionary)Renumber(s.Dictionary, numbers) },
            _ => obj,
        };

        // ------------------------------------------------------------------ Square, Circle

        /// <summary>The drawing box: /Rect less /RD.</summary>
        private (double X, double Y, double W, double H) InnerBox()
        {
            var rd = Differences();
            return (rd.L, rd.B, Width - rd.L - rd.R, Height - rd.B - rd.T);
        }

        public bool Square()
        {
            var border = ReadBorder();
            var (x, y, w, h) = InnerBox();
            string? fill = ColourOp(Colour("IC"), false);
            string? stroke = border.Width > 0 ? ColourOp(Colour("C") ?? new[] { 0.0 }, true) : null;
            GraphicsState(Opacity);
            double intensity = CloudIntensity();
            if (intensity > 0 && stroke != null)
            {
                DrawCloudyBox(x, y, w, h, border, intensity, fill, stroke, ellipse: false);
                return true;
            }
            DrawBox(x, y, w, h, border, fill, stroke, Colour("IC"));
            return true;
        }

        public bool Circle()
        {
            var border = ReadBorder();
            var (x, y, w, h) = InnerBox();
            string? fill = ColourOp(Colour("IC"), false);
            string? stroke = border.Width > 0 ? ColourOp(Colour("C") ?? new[] { 0.0 }, true) : null;
            GraphicsState(Opacity);
            double intensity = CloudIntensity();
            if (intensity > 0 && stroke != null)
            {
                DrawCloudyBox(x, y, w, h, border, intensity, fill, stroke, ellipse: true);
                return true;
            }
            double half = stroke != null ? border.Width / 2 : 0;
            double rx = Math.Max(0, w / 2 - half), ry = Math.Max(0, h / 2 - half);
            Colours(fill, stroke);
            if (stroke != null) LineStyle(border.Width, border.Dash);
            Ellipse(x + w / 2, y + h / 2, rx, ry);
            Paint(fill, stroke);
            return true;
        }

        /// <summary>A rectangle with its border inside the box (so it fits /Rect), honouring the dash, bevel, inset and underline styles.</summary>
        private void DrawBox(double x, double y, double w, double h, Border border, string? fill, string? stroke, double[]? background)
        {
            double bw = stroke != null ? border.Width : 0;
            if (fill != null)
            {
                Colours(fill, null);
                Re(x + bw / 2, y + bw / 2, Math.Max(0, w - bw), Math.Max(0, h - bw));
                Paint(fill, null);
            }
            if (stroke == null) return;
            Colours(null, stroke);
            LineStyle(bw, border.Dash);
            if (border.Style == "U")
            {
                M(new Pt(x, y + bw / 2));
                L(new Pt(x + w, y + bw / 2));
                Op("S");
                return;
            }
            Re(x + bw / 2, y + bw / 2, Math.Max(0, w - bw), Math.Max(0, h - bw));
            Op("S");
            if (border.Style is "B" or "I" && w > 4 * bw && h > 4 * bw)
            {
                // The 3D band inside the border, as Acrobat draws beveled and inset widgets.
                string light = border.Style == "B" ? "1 g" : "0.5 g";
                string dark = border.Style == "B" ? Darker(background) : "0.75 g";
                Op(light);
                M(new Pt(x + bw, y + bw)); L(new Pt(x + bw, y + h - bw)); L(new Pt(x + w - bw, y + h - bw));
                L(new Pt(x + w - 2 * bw, y + h - 2 * bw)); L(new Pt(x + 2 * bw, y + h - 2 * bw)); L(new Pt(x + 2 * bw, y + 2 * bw));
                Op("h f");
                Op(dark);
                M(new Pt(x + w - bw, y + h - bw)); L(new Pt(x + w - bw, y + bw)); L(new Pt(x + bw, y + bw));
                L(new Pt(x + 2 * bw, y + 2 * bw)); L(new Pt(x + w - 2 * bw, y + 2 * bw)); L(new Pt(x + w - 2 * bw, y + h - 2 * bw));
                Op("h f");
            }
        }

        /// <summary>The shadow colour of a beveled border: the background at half brightness, in its own colour space.</summary>
        private static string Darker(double[]? background) => background?.Length switch
        {
            1 => ColourOp(new[] { background[0] / 2 }, false)!,
            3 => ColourOp(background.Select(v => v / 2).ToArray(), false)!,
            4 => ColourOp(new[] { background[0], background[1], background[2], background[3] + (1 - background[3]) / 2 }, false)!,
            _ => "0.5 g",
        };

        private void DrawCloudyBox(double x, double y, double w, double h, Border border, double intensity, string? fill, string? stroke, bool ellipse)
        {
            double radius = CloudRadius(intensity, border.Width);
            // The bumps reach a radius (and half the line) beyond the base shape: keep them in the box.
            double inset = radius + border.Width / 2;
            double bx = x + inset, by = y + inset, bw = w - 2 * inset, bh = h - 2 * inset;
            if (bw <= 1 || bh <= 1)
            {
                DrawBox(x, y, w, h, border, fill, stroke, null);
                return;
            }
            var polygon = new List<Pt>();
            if (ellipse)
            {
                for (int i = 0; i < 96; i++)
                {
                    double a = 2 * Math.PI * i / 96;
                    polygon.Add(new Pt(bx + bw / 2 + Math.Cos(a) * bw / 2, by + bh / 2 + Math.Sin(a) * bh / 2));
                }
            }
            else
            {
                polygon.AddRange(new[] { new Pt(bx, by), new Pt(bx + bw, by), new Pt(bx + bw, by + bh), new Pt(bx, by + bh) });
            }
            Colours(fill, stroke);
            LineStyle(border.Width, border.Dash);
            Op("1 j");
            Cloud(polygon, radius, byArcLength: ellipse);
            Paint(fill, stroke);
        }

        // ------------------------------------------------------------------ Line, Polygon, PolyLine

        public bool Line()
        {
            var l = Numbers("L");
            if (l is not { Length: 4 }) return false;
            var border = ReadBorder();
            var p1 = Local(l[0], l[1]);
            var p2 = Local(l[2], l[3]);
            var colour = Colour("C") ?? new[] { 0.0 };
            string? stroke = border.Width > 0 ? ColourOp(colour, true) : null;
            var interior = Colour("IC");
            var (startStyle, endStyle) = LineEndings();
            GraphicsState(Opacity);
            if (stroke == null) return true;

            // Leader lines (12.5.6.7): the line moves LL (plus LLO) off its endpoints, to the left.
            double ll = Number("LL") ?? 0, lle = Math.Abs(Number("LLE") ?? 0), llo = Math.Abs(Number("LLO") ?? 0);
            var dir = (p2 - p1).Unit;
            var left = dir.Left;
            double sign = ll < 0 ? -1 : 1;
            var q1 = p1 + left * (ll + sign * llo);
            var q2 = p2 + left * (ll + sign * llo);

            Colours(ColourOp(interior, false), stroke);
            LineStyle(border.Width, border.Dash);
            if (ll != 0)
            {
                M(p1 + left * (sign * llo)); L(q1 + left * (sign * lle));
                M(p2 + left * (sign * llo)); L(q2 + left * (sign * lle));
                Op("S");
            }

            // A caption (/Cap) inside the line or above it.
            string caption = Get("Cap") is PdfBoolean { Value: true } ? (Text("Contents") ?? string.Empty).Trim() : string.Empty;
            bool inline = Name("CP") != "Top";
            var offset = Numbers("CO") is { Length: 2 } co ? co : new[] { 0.0, 0.0 };
            List<Glyph>? captionGlyphs = null;
            EmbeddedFontBuilder? captionFont = null;
            const double captionSize = 9;
            double captionWidth = 0;
            if (caption.Length > 0 && (captionFont = Fonts.Primary("Helvetica", false, false)) != null)
            {
                captionGlyphs = Fonts.Shape(caption.Replace('\r', ' ').Replace('\n', ' '), captionFont, false, false);
                captionWidth = captionGlyphs.Sum(g => g.Advance) / 1000 * captionSize;
            }
            double length = (q2 - q1).Length;
            var middle = (q1 + q2) * 0.5 + dir * offset[0];
            if (captionGlyphs is { Count: > 0 } && inline && captionWidth + 4 < length)
            {
                double gap = captionWidth / 2 + 2;
                M(q1); L(middle - dir * gap);
                M(middle + dir * gap); L(q2);
            }
            else
            {
                M(q1); L(q2);
            }
            Op("S");

            Op("[] 0 d");
            LineEnding(startStyle, q1, dir * -1, border.Width, interior);
            LineEnding(endStyle, q2, dir, border.Width, interior);

            if (captionGlyphs is { Count: > 0 } && captionFont != null)
            {
                double ascent = captionFont.Font.ToThousandths(captionFont.Font.CapHeight) / 1000 * captionSize;
                // Inline: centred on the line; Top: sitting on it.
                double rise = inline ? -ascent / 2 : border.Width / 2 + 1.5;
                var origin = middle + left * (rise + offset[1]) - dir * (captionWidth / 2);
                Op(ColourOp(colour, false) ?? "0 g");
                Op("BT");
                Op($"{F(dir.X)} {F(dir.Y)} {F(-dir.Y)} {F(dir.X)} {F(origin.X)} {F(origin.Y)} Tm");
                ShowGlyphs(captionGlyphs, captionSize);
                Op("ET");
            }
            return true;
        }

        private (string Start, string End) LineEndings()
        {
            if (Get("LE") is PdfArray le && le.Count >= 2)
                return (_r.Resolve(le[0]) is PdfName a ? a.Value : "None", _r.Resolve(le[1]) is PdfName b ? b.Value : "None");
            return ("None", "None");
        }

        /// <summary>
        /// A line ending (Table 179) at <paramref name="tip"/>, <paramref name="outward"/> pointing
        /// away from the line. Sizes follow Acrobat's: shapes three line widths across each way,
        /// arrows nine line widths long at 30 degrees.
        /// </summary>
        private void LineEnding(string style, Pt tip, Pt outward, double width, double[]? interior)
        {
            if (style == "None") return;
            double s = Math.Max(width, 1);
            var d = outward.Unit;
            var n = d.Left;
            string? fill = ColourOp(interior, false);
            double half = 3 * s, arrow = 9 * s;
            double ax = arrow * Math.Cos(Math.PI / 6), ay = arrow * Math.Sin(Math.PI / 6);
            switch (style)
            {
                case "Square":
                    M(tip + d * half + n * half); L(tip - d * half + n * half); L(tip - d * half - n * half); L(tip + d * half - n * half);
                    Op("h"); Paint(fill, "stroke");
                    break;
                case "Circle":
                    Ellipse(tip.X, tip.Y, half, half);
                    Paint(fill, "stroke");
                    break;
                case "Diamond":
                    M(tip + d * half); L(tip + n * half); L(tip - d * half); L(tip - n * half);
                    Op("h"); Paint(fill, "stroke");
                    break;
                case "OpenArrow":
                case "ClosedArrow":
                    M(tip - d * ax + n * ay); L(tip); L(tip - d * ax - n * ay);
                    if (style == "ClosedArrow") { Op("h"); Paint(fill, "stroke"); }
                    else Op("S");
                    break;
                case "ROpenArrow":
                case "RClosedArrow":
                    M(tip + d * ax + n * ay); L(tip); L(tip + d * ax - n * ay);
                    if (style == "RClosedArrow") { Op("h"); Paint(fill, "stroke"); }
                    else Op("S");
                    break;
                case "Butt":
                    M(tip + n * half); L(tip - n * half); Op("S");
                    break;
                case "Slash":
                {
                    var v = d * Math.Cos(Math.PI / 3) + n * Math.Sin(Math.PI / 3);
                    M(tip + v * (arrow / 2)); L(tip - v * (arrow / 2)); Op("S");
                    break;
                }
            }
        }

        public bool Poly(bool closed)
        {
            var border = ReadBorder();
            var v = Numbers("Vertices");
            string? fill = closed ? ColourOp(Colour("IC"), false) : null;
            string? stroke = border.Width > 0 ? ColourOp(Colour("C") ?? new[] { 0.0 }, true) : null;
            GraphicsState(Opacity);
            if (v is not { Length: >= 4 })
            {
                // PDF 2.0 annotations may give a /Path instead of /Vertices.
                if (Get("Path") is not PdfArray path) return false;
                Colours(fill, stroke);
                LineStyle(border.Width, border.Dash);
                Op("1 j");
                if (!DrawPath(path)) return false;
                if (closed) Op("h");
                Paint(fill, stroke);
                return true;
            }
            var points = new List<Pt>();
            for (int i = 0; i + 1 < v.Length; i += 2) points.Add(Local(v[i], v[i + 1]));
            if (fill == null && stroke == null) return true;

            Colours(fill, stroke);
            LineStyle(border.Width, border.Dash);
            double intensity = closed ? CloudIntensity() : 0;
            if (intensity > 0 && points.Count >= 3 && stroke != null)
            {
                Op("1 j");
                Cloud(points, CloudRadius(intensity, border.Width), byArcLength: false);
                Paint(fill, stroke);
                return true;
            }
            Op("1 j");
            M(points[0]);
            foreach (var p in points.Skip(1)) L(p);
            if (closed) Op("h");
            Paint(fill, stroke);

            if (!closed && stroke != null && points.Count >= 2)
            {
                var (startStyle, endStyle) = LineEndings();
                Op("[] 0 d 0 j");
                var interior = Colour("IC");
                Colours(ColourOp(interior, false), null);
                LineEnding(startStyle, points[0], points[0] - points[1], border.Width, interior);
                LineEnding(endStyle, points[^1], points[^1] - points[^2], border.Width, interior);
            }
            return true;
        }

        /// <summary>A PDF 2.0 /Path: arrays of two numbers (move, then line) or six (curve).</summary>
        private bool DrawPath(PdfArray path)
        {
            bool first = true, any = false;
            foreach (var item in path)
            {
                var p = NumbersOf(_r.Resolve(item));
                if (p is { Length: 2 })
                {
                    if (first) M(Local(p[0], p[1])); else L(Local(p[0], p[1]));
                    first = false; any = true;
                }
                else if (p is { Length: 6 } && !first)
                {
                    C(Local(p[0], p[1]), Local(p[2], p[3]), Local(p[4], p[5]));
                }
            }
            return any;
        }

        // ------------------------------------------------------------------ Ink

        public bool Ink()
        {
            var border = ReadBorder();
            string? stroke = ColourOp(Colour("C") ?? new[] { 0.0 }, true);
            var strokes = new List<List<Pt>>();
            if (Get("InkList") is PdfArray inkList)
            {
                foreach (var item in inkList)
                {
                    var v = NumbersOf(_r.Resolve(item));
                    if (v is not { Length: >= 2 }) continue;
                    var pts = new List<Pt>();
                    for (int i = 0; i + 1 < v.Length; i += 2)
                    {
                        var p = Local(v[i], v[i + 1]);
                        if (pts.Count == 0 || (p - pts[^1]).Length > 1e-6) pts.Add(p);
                    }
                    strokes.Add(pts);
                }
            }
            var path = strokes.Count == 0 ? Get("Path") as PdfArray : null;
            if (strokes.Count == 0 && path == null) return false;
            GraphicsState(Opacity);
            if (stroke == null || border.Width <= 0) return true;

            Colours(null, stroke);
            LineStyle(border.Width, border.Dash);
            Op("1 J 1 j");
            if (path != null)
            {
                DrawPath(path);
                Op("S");
                return true;
            }
            foreach (var pts in strokes)
            {
                M(pts[0]);
                if (pts.Count == 1) { L(pts[0]); continue; } // a dot: round caps make it a disc
                if (pts.Count == 2) { L(pts[1]); continue; }
                // A Catmull-Rom spline through the points, as cubic Béziers: smooth like the pen was.
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    var p0 = pts[Math.Max(0, i - 1)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                    C(p1 + (p2 - p0) * (1.0 / 6), p2 - (p3 - p1) * (1.0 / 6), p2);
                }
            }
            Op("S");
            return true;
        }

        // ------------------------------------------------------------------ text markup

        /// <summary>
        /// The quadrilaterals of a text markup annotation in text-frame terms: bottom-left and
        /// bottom-right corners, the unit direction of the text and of "up", and the height.
        /// QuadPoints come in Acrobat's order (top-left, top-right, bottom-left, bottom-right) or
        /// the specification's counter-clockwise order; which one is read from the geometry.
        /// </summary>
        private List<(Pt B0, Pt B1, Pt Up, double H)> Quads()
        {
            var result = new List<(Pt, Pt, Pt, double)>();
            var q = Numbers("QuadPoints");
            if (q == null || q.Length < 8)
                q = new[] { _x0, _y0 + Height, _x0 + Width, _y0 + Height, _x0, _y0, _x0 + Width, _y0 };
            for (int i = 0; i + 7 < q.Length; i += 8)
            {
                var p1 = Local(q[i], q[i + 1]); var p2 = Local(q[i + 2], q[i + 3]);
                var p3 = Local(q[i + 4], q[i + 5]); var p4 = Local(q[i + 6], q[i + 7]);
                Pt t0, t1, b0, b1;
                if ((p2 - p1).Dot(p4 - p3) >= 0) { t0 = p1; t1 = p2; b0 = p3; b1 = p4; }  // Acrobat's Z order
                else { b0 = p1; b1 = p2; t1 = p3; t0 = p4; }                            // counter-clockwise from bottom-left
                var u = (b1 - b0).Unit;
                var up = u.Left;
                double h = (t0 - b0).Dot(up);
                if (h < 0) { up = up * -1; h = -h; }
                if (h < 1e-6 || (b1 - b0).Length < 1e-6) continue;
                result.Add((b0, b1, up, h));
            }
            return result;
        }

        public bool Highlight()
        {
            var quads = Quads();
            string? fill = ColourOp(Colour("C") ?? new[] { 1.0, 1.0, 0.0 }, false);
            GraphicsState(Opacity, multiply: true);
            if (fill == null || quads.Count == 0) return true;
            Colours(fill, null);
            foreach (var (b0, b1, up, h) in quads)
            {
                var u = (b1 - b0).Unit;
                var t0 = b0 + up * h; var t1 = b1 + up * h;
                double bulge = h / 4; // Acrobat's highlight ends are gently rounded
                // Every quad wound the same way, so overlapping quads do not cancel under nonzero fill.
                bool ccw = u.X * up.Y - u.Y * up.X > 0;
                if (ccw)
                {
                    M(b0); L(b1);
                    C(b1 + u * bulge, t1 + u * bulge, t1);
                    L(t0);
                    C(t0 - u * bulge, b0 - u * bulge, b0);
                }
                else
                {
                    M(b1); L(b0);
                    C(b0 - u * bulge, t0 - u * bulge, t0);
                    L(t1);
                    C(t1 + u * bulge, b1 + u * bulge, b1);
                }
                Op("h");
            }
            Op("f");
            return true;
        }

        public bool TextLine(TextLineKind kind)
        {
            var quads = Quads();
            string? stroke = ColourOp(Colour("C") ?? new[] { 0.0 }, true);
            GraphicsState(Opacity);
            if (stroke == null || quads.Count == 0) return true;
            Colours(null, stroke);
            foreach (var (b0, b1, up, h) in quads)
            {
                var u = (b1 - b0).Unit;
                double length = (b1 - b0).Length;
                switch (kind)
                {
                    case TextLineKind.Underline:
                    {
                        double t = Math.Max(0.5, h / 14);
                        Op($"{F(t)} w 0 J");
                        M(b0 + up * t); L(b1 + up * t); Op("S");
                        break;
                    }
                    case TextLineKind.StrikeOut:
                    {
                        double t = Math.Max(0.5, h / 14);
                        Op($"{F(t)} w 0 J");
                        M(b0 + up * (h * 0.42)); L(b1 + up * (h * 0.42)); Op("S");
                        break;
                    }
                    default:
                    {
                        double t = Math.Max(0.5, h / 18);
                        double amplitude = Math.Max(1, h / 10), step = amplitude * 1.5;
                        double baseY = t / 2;
                        Op($"{F(t)} w 1 j 0 J");
                        M(b0 + up * baseY);
                        int k = 1;
                        for (double x = step; ; x += step, k++)
                        {
                            double at = Math.Min(x, length);
                            L(b0 + u * at + up * (baseY + (k % 2 == 1 ? amplitude : 0)));
                            if (x >= length) break;
                        }
                        Op("S");
                        break;
                    }
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ icons

        /// <summary>Draws a 20 by 20 icon at the rectangle's top-left, shrunk only when the rectangle is smaller.</summary>
        private void Icon(Action draw)
        {
            double s = Math.Min(1, Math.Min(Width / 20, Height / 20));
            if (s <= 0) return;
            Op("q");
            Op($"{F(s)} 0 0 {F(s)} 0 {F(Height - 20 * s)} cm");
            draw();
            Op("Q");
        }

        public bool Note()
        {
            var colour = Colour("C") ?? new[] { 1.0, 0.8196, 0.0 };
            string? fill = ColourOp(colour, false);
            GraphicsState(Opacity);
            string name = Name("Name") ?? "Note";
            Icon(() =>
            {
                Op("0 G 0.75 w 1 j");
                Colours(fill, null);
                switch (name)
                {
                    case "Note": NoteIcon(fill); break;
                    case "Help": HelpIcon(fill); break;
                    case "Insert":
                        M(new Pt(10, 18.5)); L(new Pt(18.5, 2)); L(new Pt(1.5, 2)); Op("h");
                        Paint(fill, "0 G");
                        break;
                    case "Key": KeyIcon(fill); break;
                    case "NewParagraph":
                        M(new Pt(10, 18.5)); L(new Pt(16, 10.5)); L(new Pt(4, 10.5)); Op("h");
                        Paint(fill, "0 G");
                        Op("0 g");
                        Pilcrow(7.5, 1.5, 7.5);
                        break;
                    case "Paragraph":
                        Ellipse(10, 10, 8.5, 8.5);
                        Paint(fill, "0 G");
                        Op("0 g");
                        Pilcrow(6.5, 4, 12);
                        break;
                    case "Check":
                        StrokedMark(fill, new[] { new[] { new Pt(3.5, 10), new Pt(8, 4.5), new Pt(16.5, 16) } });
                        break;
                    case "Cross":
                        StrokedMark(fill, new[] { new[] { new Pt(4.5, 4.5), new Pt(15.5, 15.5) }, new[] { new Pt(4.5, 15.5), new Pt(15.5, 4.5) } });
                        break;
                    case "Circle":
                        Ellipse(10, 10, 8, 8);
                        Paint(fill, "0 G");
                        break;
                    case "Star":
                        for (int i = 0; i < 10; i++)
                        {
                            double a = Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? 9 : 3.8;
                            var p = new Pt(10 + r * Math.Cos(a), 9.5 + r * Math.Sin(a));
                            if (i == 0) M(p); else L(p);
                        }
                        Op("h");
                        Paint(fill, "0 G");
                        break;
                    default: CommentIcon(fill); break;
                }
            });
            return true;
        }

        /// <summary>A sheet with a folded corner and ruled lines.</summary>
        private void NoteIcon(string? fill)
        {
            M(new Pt(3.5, 18.5)); L(new Pt(16.5, 18.5)); L(new Pt(16.5, 6.5)); L(new Pt(11.5, 1.5)); L(new Pt(3.5, 1.5)); Op("h");
            Paint(fill, "0 G");
            M(new Pt(16.5, 6.5)); L(new Pt(11.5, 6.5)); L(new Pt(11.5, 1.5)); Op("S");
            M(new Pt(5.5, 15)); L(new Pt(14.5, 15));
            M(new Pt(5.5, 12)); L(new Pt(14.5, 12));
            M(new Pt(5.5, 9)); L(new Pt(14.5, 9));
            M(new Pt(5.5, 6)); L(new Pt(9.5, 6));
            Op("S");
        }

        /// <summary>A speech bubble with a tail at the bottom left and two lines of "text".</summary>
        private void CommentIcon(string? fill)
        {
            M(new Pt(4, 18.5)); L(new Pt(16, 18.5));
            C(new Pt(17.4, 18.5), new Pt(18.5, 17.4), new Pt(18.5, 16));
            L(new Pt(18.5, 8.5));
            C(new Pt(18.5, 7.1), new Pt(17.4, 6), new Pt(16, 6));
            L(new Pt(9, 6)); L(new Pt(4.5, 2)); L(new Pt(6, 6)); L(new Pt(4, 6));
            C(new Pt(2.6, 6), new Pt(1.5, 7.1), new Pt(1.5, 8.5));
            L(new Pt(1.5, 16));
            C(new Pt(1.5, 17.4), new Pt(2.6, 18.5), new Pt(4, 18.5));
            Op("h");
            Paint(fill, "0 G");
            M(new Pt(4.5, 15)); L(new Pt(15.5, 15));
            M(new Pt(4.5, 12)); L(new Pt(15.5, 12));
            M(new Pt(4.5, 9)); L(new Pt(12, 9));
            Op("S");
        }

        private void HelpIcon(string? fill)
        {
            Ellipse(10, 10, 8.5, 8.5);
            Paint(fill, "0 G");
            Op("q 2 w 1 J 0 G");
            M(new Pt(7, 12.5));
            C(new Pt(7, 15.8), new Pt(13, 15.8), new Pt(13, 12.5));
            C(new Pt(13, 10.3), new Pt(10, 10.6), new Pt(10, 8));
            L(new Pt(10, 7.4));
            Op("S Q");
            Op("0 g");
            Ellipse(10, 4.6, 1.2, 1.2);
            Op("f");
        }

        private void KeyIcon(string? fill)
        {
            M(new Pt(10.4, 11.4)); L(new Pt(17.9, 3.9)); L(new Pt(16.1, 2.1)); L(new Pt(14.6, 3.6));
            L(new Pt(15.6, 4.6)); L(new Pt(14.2, 6)); L(new Pt(13.2, 5)); L(new Pt(8.6, 9.6)); Op("h");
            Paint(fill, "0 G");
            Ellipse(7, 13, 4.8, 4.8);
            Paint(fill, "0 G");
            Op("1 g");
            Ellipse(5.8, 14.2, 1.4, 1.4);
            Paint("1 g", "0 G");
        }

        /// <summary>A pilcrow (¶) drawn as a path, <paramref name="size"/> high from its bottom-left corner.</summary>
        private void Pilcrow(double x, double y, double size)
        {
            Pt P(double u, double v) => new(x + u * size, y + v * size);
            M(P(0.45, 1)); L(P(0.45, 0)); L(P(0.35, 0)); L(P(0.35, 0.5));
            C(P(0.1, 0.5), P(0, 0.62), P(0, 0.75));
            C(P(0, 0.88), P(0.1, 1), P(0.35, 1));
            Op("h");
            Re(x + 0.45 * size, y + 0.9 * size, 0.2 * size, 0.1 * size);
            Re(x + 0.55 * size, y, 0.1 * size, size);
            Op("f");
        }

        /// <summary>Strokes in the colour with a dark outline: a wide black stroke under a narrower coloured one.</summary>
        private void StrokedMark(string? fill, Pt[][] lines)
        {
            string? stroke = fill?.Replace(" rg", " RG").Replace(" g", " G").Replace(" k", " K");
            foreach (var (width, colour) in new[] { (4.0, "0 G"), (2.5, stroke) })
            {
                if (colour == null) continue;
                Op($"{F(width)} w 1 J 1 j {colour}");
                foreach (var line in lines)
                {
                    M(line[0]);
                    foreach (var p in line.Skip(1)) L(p);
                }
                Op("S");
            }
        }

        public bool FileAttachment()
        {
            var colour = Colour("C") ?? new[] { 0.0, 0.0, 1.0 };
            string? fill = ColourOp(colour, false);
            string? stroke = ColourOp(colour, true);
            GraphicsState(Opacity);
            string name = Name("Name") ?? "PushPin";
            Icon(() =>
            {
                Op("0 G 0.75 w 1 j");
                Colours(fill, null);
                switch (name)
                {
                    case "Paperclip":
                    case "PaperclipTag":
                        foreach (var (width, c) in new[] { (2.6, "0 G"), (1.4, stroke) })
                        {
                            if (c == null) continue;
                            Op($"{F(width)} w 1 J 1 j {c}");
                            M(new Pt(13, 14)); L(new Pt(13, 4.5));
                            C(new Pt(13, 0.8), new Pt(6.5, 0.8), new Pt(6.5, 4.5));
                            L(new Pt(6.5, 15.5));
                            C(new Pt(6.5, 19.2), new Pt(11, 19.2), new Pt(11, 15.5));
                            L(new Pt(11, 6.5));
                            C(new Pt(11, 4.7), new Pt(8.5, 4.7), new Pt(8.5, 6.5));
                            L(new Pt(8.5, 14));
                            Op("S");
                        }
                        break;
                    case "Graph":
                        Op("0 G");
                        M(new Pt(2.5, 18)); L(new Pt(2.5, 2.5)); L(new Pt(18, 2.5)); Op("S");
                        Re(4.5, 2.5, 3, 8); Re(9, 2.5, 3, 13); Re(13.5, 2.5, 3, 6);
                        Paint(fill, "0 G");
                        break;
                    case "Tag":
                        M(new Pt(2, 10)); L(new Pt(7, 16)); L(new Pt(18, 16)); L(new Pt(18, 4)); L(new Pt(7, 4)); Op("h");
                        Paint(fill, "0 G");
                        Op("1 g");
                        Ellipse(6.8, 10, 1.4, 1.4);
                        Paint("1 g", "0 G");
                        break;
                    default: // PushPin, GraphPushPin
                        M(new Pt(7, 18.5)); L(new Pt(13, 18.5)); L(new Pt(12, 14)); L(new Pt(15.5, 10.5));
                        L(new Pt(4.5, 10.5)); L(new Pt(8, 14)); Op("h");
                        Paint(fill, "0 G");
                        Op("q 1.2 w 1 J 0 G");
                        M(new Pt(10, 10.5)); L(new Pt(10, 1.5)); Op("S Q");
                        break;
                }
            });
            return true;
        }

        public bool Caret()
        {
            var (x, y, w, h) = InnerBox();
            string? fill = ColourOp(Colour("C") ?? new[] { 0.0, 0.0, 1.0 }, false);
            GraphicsState(Opacity);
            if (fill == null) return true;
            Colours(fill, null);
            // Two concave curves meeting in a point at the top centre, with Acrobat's proportions.
            M(new Pt(x, y));
            C(new Pt(x + w * 0.38, y + h * 0.05), new Pt(x + w * 0.46, y + h * 0.37), new Pt(x + w / 2, y + h));
            C(new Pt(x + w * 0.54, y + h * 0.37), new Pt(x + w * 0.62, y + h * 0.05), new Pt(x + w, y));
            Op("h f");
            return true;
        }

        // ------------------------------------------------------------------ text: Stamp, FreeText

        /// <summary>Emits glyphs (in a BT block, after Tm or Td) as runs per font.</summary>
        private void ShowGlyphs(IReadOnlyList<Glyph> glyphs, double size)
        {
            EmbeddedFontBuilder? current = null;
            var hex = new StringBuilder();
            void Flush()
            {
                if (hex.Length > 0) Op($"<{hex}> Tj");
                hex.Clear();
            }
            foreach (var g in glyphs)
            {
                if (!ReferenceEquals(g.Font, current))
                {
                    Flush();
                    current = g.Font;
                    Op($"/{current.ResourceName} {F(size)} Tf");
                }
                hex.Append(Convert.ToHexString(g.Font.Encode(g.Gid, g.Text)));
            }
            Flush();
        }

        public bool Stamp()
        {
            string name = Name("Name") ?? "Draft";
            string label = StampLabel(name, Text("Contents"));
            var colour = Colour("C") ?? StampColour(name);
            string? stroke = ColourOp(colour, true);
            string? textFill = ColourOp(colour, false);
            GraphicsState(Opacity);
            if (stroke == null || Width <= 0 || Height <= 0) return true;

            var (x, y, w, h) = InnerBox();
            double bw = Math.Clamp(Math.Min(w, h) * 0.06, 1, 6);
            double radius = Math.Min(w, h) * 0.2;
            Colours(ColourOp(Tint(colour), false), stroke);
            Op($"{F(bw)} w");
            RoundedRect(x + bw / 2, y + bw / 2, w - bw, h - bw, radius);
            Op("B");

            var font = Fonts.Primary("Helvetica-Bold", true, false);
            if (font == null) return true;
            var glyphs = Fonts.Shape(label, font, true, false);
            double units = glyphs.Sum(g => g.Advance) / 1000;
            if (units <= 0) return true;
            double pad = bw + Math.Min(w, h) * 0.12;
            double size = Math.Min((h - 2 * bw) * 0.6, (w - 2 * pad) / units);
            if (size <= 0.5) return true;
            double cap = font.Font.ToThousandths(font.Font.CapHeight) / 1000 * size;
            double tx = x + (w - units * size) / 2, ty = y + (h - cap) / 2;
            Op(textFill!);
            Op("BT");
            Op($"{F(tx)} {F(ty)} Td");
            ShowGlyphs(glyphs, size);
            Op("ET");
            return true;
        }

        /// <summary>"NotForPublicRelease" is "NOT FOR PUBLIC RELEASE"; Acrobat's SB/SH prefixes are dropped.</summary>
        private static string StampLabel(string name, string? contents)
        {
            if (name.StartsWith('#') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return string.IsNullOrWhiteSpace(contents) || contents.Length > 40 ? "STAMP" : contents.Trim().ToUpperInvariant();
            if (name.Length > 2 && (name.StartsWith("SB", StringComparison.Ordinal) || name.StartsWith("SH", StringComparison.Ordinal)) && char.IsUpper(name[2]))
                name = name[2..];
            if (name == "AsIs") return "AS IS";
            var words = Regex.Replace(name.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");
            return words.ToUpperInvariant().Trim();
        }

        private static double[] StampColour(string name)
        {
            string n = name.StartsWith("SB", StringComparison.Ordinal) || name.StartsWith("SH", StringComparison.Ordinal) ? name[2..] : name;
            return n switch
            {
                "NotApproved" or "Expired" or "TopSecret" or "NotForPublicRelease" or "Confidential" or "Void" or "Rejected" => new[] { 0.78, 0.1, 0.1 },
                "Approved" or "Final" or "Completed" or "Accepted" or "ForPublicRelease" or "Sold" => new[] { 0.13, 0.47, 0.13 },
                _ => new[] { 0.12, 0.2, 0.6 },
            };
        }

        /// <summary>The colour at 12% on white, in its own colour space (a light wash behind stamp text).</summary>
        private static double[] Tint(double[] colour) => colour.Length == 4
            ? colour.Select(v => v * 0.12).ToArray()
            : colour.Select(v => 1 - 0.12 * (1 - v)).ToArray();

        public bool FreeText()
        {
            var da = ParseDA(Text("DA"), Text("DS"));
            string intent = Name("IT") ?? string.Empty;
            var border = ReadBorder(intent == "FreeTextTypewriter" ? 0 : 1);
            var (x, y, w, h) = InnerBox();
            var background = Colour("IC") ?? Colour("C");
            string? fill = ColourOp(background, false);
            var textColour = da.Fill ?? new[] { 0.0 };
            var borderColour = da.Stroke ?? textColour;
            string? stroke = border.Width > 0 ? ColourOp(borderColour, true) : null;
            GraphicsState(Opacity);

            // The callout line (/CL) from the pointed-at spot to the box, with its /LE ending.
            if (Numbers("CL") is { Length: 4 or 6 } cl && stroke != null)
            {
                var pts = new List<Pt>();
                for (int i = 0; i + 1 < cl.Length; i += 2) pts.Add(Local(cl[i], cl[i + 1]));
                Colours(ColourOp(background, false), stroke);
                LineStyle(border.Width, null);
                M(pts[0]);
                foreach (var p in pts.Skip(1)) L(p);
                Op("S");
                string ending = Get("LE") is PdfName le ? le.Value : "None";
                LineEnding(ending, pts[0], pts[0] - pts[1], border.Width, background);
            }

            double intensity = CloudIntensity();
            if (intensity > 0 && stroke != null)
            {
                DrawCloudyBox(x, y, w, h, border, intensity, fill, stroke, ellipse: false);
                double inset = CloudRadius(intensity, border.Width) + border.Width / 2;
                x += inset; y += inset; w -= 2 * inset; h -= 2 * inset;
            }
            else
            {
                DrawBox(x, y, w, h, border, fill, stroke, background);
            }
            string text = Text("Contents") ?? string.Empty;
            if (text.Length == 0 && Text("RC") is { } rc)
                text = System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(rc, @"<\s*(br|/p)\b[^>]*>", "\n", RegexOptions.IgnoreCase), "<[^>]*>", string.Empty)).Trim();
            if (text.Length == 0) return true;

            var (fontName, bold, italic) = FontFor(da.FontName);
            var font = Fonts.Primary(fontName, bold, italic);
            if (font == null) return true;

            double bwidth = stroke != null ? border.Width : 0;
            double pad = bwidth + 2;
            double boxX = x + pad, boxY = y + pad, boxW = w - 2 * pad, boxH = h - 2 * pad;
            if (boxW <= 0 || boxH <= 0) return true;

            double ascent = font.Font.ToThousandths(font.Font.Ascender) / 1000;
            double descent = font.Font.ToThousandths(font.Font.Descender) / 1000;
            double lineFactor = Math.Max(1, ascent - descent);
            var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Split('\n')
                .Select(p => Fonts.Shape(p, font, bold, italic)).ToList();

            double size = da.Size;
            List<List<Glyph>> lines;
            if (size > 0)
            {
                lines = paragraphs.SelectMany(p => Wrap(p, boxW, size)).ToList();
            }
            else
            {
                // Auto size: the largest (up to 12 pt) at which the text fits the box.
                size = 12;
                while (true)
                {
                    lines = paragraphs.SelectMany(p => Wrap(p, boxW, size)).ToList();
                    if (size <= 4 || lines.Count * size * lineFactor <= boxH) break;
                    size -= 0.5;
                }
            }

            int q = (int)(Number("Q") ?? 0);
            double leading = size * lineFactor;
            Op("q");
            Re(boxX, boxY, boxW, boxH);
            Op("W n");
            Op(ColourOp(textColour, false) ?? "0 g");
            Op("BT");
            double baseline = boxY + boxH - ascent * size;
            foreach (var line in lines)
            {
                double lineWidth = line.Sum(g => g.Advance) / 1000 * size;
                double tx = q switch
                {
                    1 => boxX + (boxW - lineWidth) / 2,
                    2 => boxX + boxW - lineWidth,
                    _ => boxX,
                };
                if (line.Count > 0)
                {
                    Op($"1 0 0 1 {F(tx)} {F(baseline)} Tm");
                    ShowGlyphs(line, size);
                }
                baseline -= leading;
                if (baseline < boxY - leading) break;
            }
            Op("ET");
            Op("Q");
            return true;
        }

        /// <summary>Greedy word wrap: words move to the next line whole; a word wider than the line is broken by character.</summary>
        private static List<List<Glyph>> Wrap(List<Glyph> glyphs, double width, double size)
        {
            var lines = new List<List<Glyph>>();
            var line = new List<Glyph>();
            var pendingSpaces = new List<Glyph>();
            double lineWidth = 0, scale = size / 1000;
            int i = 0;
            while (i < glyphs.Count)
            {
                if (glyphs[i].IsSpace)
                {
                    if (line.Count > 0) pendingSpaces.Add(glyphs[i]);
                    i++;
                    continue;
                }
                int end = i;
                while (end < glyphs.Count && !glyphs[end].IsSpace) end++;
                var word = glyphs.GetRange(i, end - i);
                double wordWidth = word.Sum(g => g.Advance) * scale;
                double spaceWidth = pendingSpaces.Sum(g => g.Advance) * scale;
                if (line.Count > 0 && lineWidth + spaceWidth + wordWidth <= width + 1e-6)
                {
                    line.AddRange(pendingSpaces);
                    line.AddRange(word);
                    lineWidth += spaceWidth + wordWidth;
                }
                else
                {
                    if (line.Count > 0) { lines.Add(line); line = new List<Glyph>(); lineWidth = 0; }
                    foreach (var g in word)
                    {
                        double gw = g.Advance * scale;
                        if (line.Count > 0 && lineWidth + gw > width + 1e-6) { lines.Add(line); line = new List<Glyph>(); lineWidth = 0; }
                        line.Add(g);
                        lineWidth += gw;
                    }
                }
                pendingSpaces.Clear();
                i = end;
            }
            lines.Add(line);
            return lines;
        }

        /// <summary>The font a /DA name stands for: the form-field abbreviations (Helv, TiRo, Cour...) or a font name.</summary>
        private static (string Name, bool Bold, bool Italic) FontFor(string? name)
        {
            string n = name ?? "Helv";
            string standard = n switch
            {
                "Helv" => "Helvetica", "HeBo" => "Helvetica-Bold", "HeOb" => "Helvetica-Oblique", "HeBO" => "Helvetica-BoldOblique",
                "TiRo" => "Times-Roman", "TiBo" => "Times-Bold", "TiIt" => "Times-Italic", "TiBI" => "Times-BoldItalic",
                "Cour" => "Courier", "CoBo" => "Courier-Bold", "CoOb" => "Courier-Oblique", "CoBO" => "Courier-BoldOblique",
                _ => n,
            };
            bool bold = standard.Contains("Bold", StringComparison.OrdinalIgnoreCase);
            bool italic = standard.Contains("Italic", StringComparison.OrdinalIgnoreCase) || standard.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            return (standard, bold, italic);
        }

        /// <summary>Parses /DA (font, size, fill and stroke colour), with the /DS style string filling in what it lacks.</summary>
        private static DefaultAppearance ParseDA(string? da, string? ds)
        {
            string? font = null;
            double size = 0;
            double[]? fill = null, stroke = null;
            var tokens = (da ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            double[]? Operands(int at, int count)
            {
                if (at < count) return null;
                var v = new double[count];
                for (int k = 0; k < count; k++)
                    if (!double.TryParse(tokens[at - count + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || !double.IsFinite(v[k])) return null;
                return v.Select(x => Math.Clamp(x, 0, 1)).ToArray();
            }
            for (int i = 0; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case "Tf" when i >= 2 && tokens[i - 2].StartsWith('/'):
                        font = tokens[i - 2][1..];
                        if (double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s is > 0 and < 1000) size = s;
                        break;
                    case "g": fill = Operands(i, 1) ?? fill; break;
                    case "rg": fill = Operands(i, 3) ?? fill; break;
                    case "k": fill = Operands(i, 4) ?? fill; break;
                    case "G": stroke = Operands(i, 1) ?? stroke; break;
                    case "RG": stroke = Operands(i, 3) ?? stroke; break;
                    case "K": stroke = Operands(i, 4) ?? stroke; break;
                }
            }
            if (!string.IsNullOrEmpty(ds))
            {
                if (size <= 0 && Regex.Match(ds, @"font(?:-size)?\s*:[^;]*?([\d.]+)\s*pt", RegexOptions.IgnoreCase) is { Success: true } m
                    && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dsSize) && dsSize is > 0 and < 1000)
                    size = dsSize;
                if (fill == null && Regex.Match(ds, @"(?<![-\w])color\s*:\s*#([0-9a-f]{6})", RegexOptions.IgnoreCase) is { Success: true } c)
                {
                    int rgb = int.Parse(c.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    fill = new[] { (rgb >> 16 & 255) / 255.0, (rgb >> 8 & 255) / 255.0, (rgb & 255) / 255.0 };
                }
            }
            return new DefaultAppearance(font, size, fill, stroke);
        }
    }

    /// <summary>
    /// The installed fonts text is drawn in, each embedded as a subset: a primary face, and for
    /// characters it lacks the catalogue's fallback faces. Characters no face has are left out
    /// rather than drawn as .notdef (which PDF/A forbids).
    /// </summary>
    private sealed class FontSet
    {
        private readonly SystemFontCatalog _catalog = SystemFontCatalog.Installed;
        private readonly Dictionary<(string, int), EmbeddedFontBuilder?> _builders = new();
        private readonly List<EmbeddedFontBuilder> _order = new();

        public IEnumerable<EmbeddedFontBuilder> Used => _order.Where(b => b.IsUsed);

        private EmbeddedFontBuilder? For(SystemFontFace? face)
        {
            if (face == null) return null;
            if (_builders.TryGetValue((face.Path, face.Index), out var existing)) return existing;
            var font = _catalog.Load(face);
            EmbeddedFontBuilder? builder = null;
            if (font != null)
            {
                builder = new EmbeddedFontBuilder(font, "F" + (_order.Count + 1));
                _order.Add(builder);
            }
            _builders[(face.Path, face.Index)] = builder;
            return builder;
        }

        public EmbeddedFontBuilder? Primary(string? name, bool bold, bool italic) =>
            For(_catalog.Match(name, bold, italic, serif: false, fixedPitch: false))
            ?? For(_catalog.FindFamily("Arial", bold, italic))
            ?? _catalog.Fallbacks(bold, italic).Select(For).FirstOrDefault(b => b != null);

        public List<Glyph> Shape(string text, EmbeddedFontBuilder primary, bool bold, bool italic)
        {
            var glyphs = new List<Glyph>();
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if (cp < 0x20 || cp is >= 0x7F and < 0xA0) continue;
                if (cp == 0xA0) cp = 0x20;
                string s = rune.ToString();
                bool space = Rune.IsWhiteSpace(rune);
                var font = primary;
                int gid = primary.Font.GlyphFor(cp);
                if (gid == 0)
                {
                    foreach (var face in _catalog.Fallbacks(bold, italic))
                    {
                        if (For(face) is not { } fallback) continue;
                        int g = fallback.Font.GlyphFor(cp);
                        if (g != 0) { font = fallback; gid = g; break; }
                    }
                }
                if (gid == 0) continue;
                glyphs.Add(new Glyph(font, gid, space ? " " : s, font.Font.Advance(gid), space));
            }
            return glyphs;
        }
    }
}
