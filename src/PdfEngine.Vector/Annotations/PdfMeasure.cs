using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Services;

namespace PdfEngine.Vector.Annotations;

/// <summary>
/// A number format dictionary (ISO 32000-2 12.9, Table 268): how a measured value in one unit is
/// shown. <see cref="Factor"/> (/C) converts from the previous unit of the chain (for the first
/// format: from the units the measure's caller passes in) to this one.
/// </summary>
public sealed record PdfNumberFormat
{
    /// <summary>/U: the unit label, e.g. "ft" or "sq m".</summary>
    public string Unit { get; init; } = string.Empty;
    /// <summary>/C: the conversion factor.</summary>
    public double Factor { get; init; } = 1;
    /// <summary>/F: D (decimal), F (fraction), R (round) or T (truncate).</summary>
    public string Style { get; init; } = "D";
    /// <summary>/D: for decimals a power of ten (100 = two places), for fractions the denominator.</summary>
    public int Precision { get; init; } = 100;
    /// <summary>/FD: a fraction keeps its denominator rather than being reduced.</summary>
    public bool FixedDenominator { get; init; }
    /// <summary>/RT and /RD: the thousands and decimal separators.</summary>
    public string ThousandsSeparator { get; init; } = ",";
    public string DecimalSeparator { get; init; } = ".";
    /// <summary>/PS and /SS: text before and after the unit label.</summary>
    public string Prefix { get; init; } = " ";
    public string Suffix { get; init; } = " ";
    /// <summary>/O: the label follows the value (S) or comes before it (P).</summary>
    public bool LabelFirst { get; init; }

    /// <summary>The value in this format's own style, without the unit label.</summary>
    public string FormatNumber(double value)
    {
        if (!double.IsFinite(value)) return "0";
        bool negative = value < 0;
        value = Math.Abs(value);
        string text;
        switch (Style)
        {
            case "F":
            {
                int denominator = Math.Max(1, Precision);
                long whole = (long)Math.Floor(value);
                long numerator = (long)Math.Round((value - whole) * denominator);
                if (numerator == denominator) { whole++; numerator = 0; }
                if (!FixedDenominator && numerator > 0)
                {
                    long g = Gcd(numerator, denominator);
                    numerator /= g;
                    denominator = (int)(denominator / g);
                }
                text = Group(whole.ToString(CultureInfo.InvariantCulture));
                if (numerator > 0) text = (whole > 0 ? text + " " : string.Empty) + $"{numerator}/{denominator}";
                break;
            }
            case "R":
                text = Group(Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture));
                break;
            case "T":
                text = Group(Math.Truncate(value).ToString("0", CultureInfo.InvariantCulture));
                break;
            default:
            {
                int places = Math.Clamp((int)Math.Round(Math.Log10(Math.Max(1, Precision))), 0, 10);
                double rounded = Math.Round(value, places, MidpointRounding.AwayFromZero);
                string s = rounded.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                int dot = s.IndexOf('.');
                text = dot < 0 ? Group(s) : Group(s[..dot]) + DecimalSeparator + s[(dot + 1)..];
                break;
            }
        }
        return negative && text.Any(c => c is >= '1' and <= '9') ? "-" + text : text;
    }

    /// <summary>The value with its unit label, as /O, /PS and /SS place it.</summary>
    public string FormatWithUnit(double value)
    {
        string number = FormatNumber(value);
        if (Unit.Length == 0) return number;
        return LabelFirst ? (Prefix + Unit + Suffix).TrimStart() + number : number + (Prefix + Unit + Suffix).TrimEnd();
    }

    private string Group(string digits)
    {
        if (ThousandsSeparator.Length == 0 || digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(ThousandsSeparator);
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Max(1, a);
    }

    internal PdfDictionary ToDictionary()
    {
        var e = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("NumberFormat"),
            ["U"] = PdfObjectWriter.TextString(Unit),
            ["C"] = new PdfReal(Math.Round(Factor, 10)),
            ["F"] = new PdfName(Style),
            ["D"] = new PdfInteger(Precision),
        };
        if (FixedDenominator) e["FD"] = PdfBoolean.True;
        if (ThousandsSeparator != ",") e["RT"] = PdfObjectWriter.TextString(ThousandsSeparator);
        if (DecimalSeparator != ".") e["RD"] = PdfObjectWriter.TextString(DecimalSeparator);
        if (Prefix != " ") e["PS"] = PdfObjectWriter.TextString(Prefix);
        if (Suffix != " ") e["SS"] = PdfObjectWriter.TextString(Suffix);
        if (LabelFirst) e["O"] = new PdfName("P");
        return new PdfDictionary(e);
    }

    internal static PdfNumberFormat? FromDictionary(PdfObject? obj, PdfObjectResolver r)
    {
        if (r.Resolve(obj) is not PdfDictionary d) return null;
        string? Text(string key) => PdfTextStrings.Decode(r.Resolve(d[key]));
        double factor = r.Resolve(d["C"]) is { } c && c.TryGetNumber(out double cv) && double.IsFinite(cv) && cv > 0 ? cv : double.NaN;
        if (double.IsNaN(factor)) return null; // /C is required
        int precision = r.Resolve(d["D"]) is { } p && p.TryGetNumber(out double pv) && pv >= 1 && pv <= 1e9 ? (int)pv : 100;
        return new PdfNumberFormat
        {
            Unit = Text("U") ?? string.Empty,
            Factor = factor,
            Style = r.Resolve(d["F"]) is PdfName { Value: "D" or "F" or "R" or "T" } f ? f.Value : "D",
            Precision = precision,
            FixedDenominator = r.Resolve(d["FD"]) is PdfBoolean { Value: true },
            ThousandsSeparator = Text("RT") ?? ",",
            DecimalSeparator = Text("RD") ?? ".",
            Prefix = Text("PS") ?? " ",
            Suffix = Text("SS") ?? " ",
            LabelFirst = r.Resolve(d["O"]) is PdfName { Value: "P" },
        };
    }
}

/// <summary>
/// A rectilinear measure dictionary (ISO 32000-2 12.9, Table 266, /Subtype /RL): the scale and
/// units for measuring in a viewport or in a measurement annotation. Distances and areas are
/// computed from default user space coordinates (points) as 12.9 describes: X (and Y with /CYX)
/// convert to the largest x unit, then /D and /A convert from that unit to theirs.
/// </summary>
public sealed record PdfMeasure
{
    /// <summary>/R: the scale ratio as text, e.g. "1 in = 10 ft".</summary>
    public string Ratio { get; init; } = "1 in = 1 in";
    /// <summary>/X: change along x, and along y too when there is no /Y.</summary>
    public IReadOnlyList<PdfNumberFormat> X { get; init; } = new[] { new PdfNumberFormat { Unit = "in", Factor = 1 / 72.0 } };
    /// <summary>/Y: change along y, or null to use /X.</summary>
    public IReadOnlyList<PdfNumberFormat>? Y { get; init; }
    /// <summary>/D: distance in any direction.</summary>
    public IReadOnlyList<PdfNumberFormat> Distance { get; init; } = new[] { new PdfNumberFormat { Unit = "in" } };
    /// <summary>/A: area.</summary>
    public IReadOnlyList<PdfNumberFormat> Area { get; init; } = new[] { new PdfNumberFormat { Unit = "sq in" } };
    /// <summary>/T: angles; null when the measure gives none (degrees are shown).</summary>
    public IReadOnlyList<PdfNumberFormat>? Angle { get; init; }
    /// <summary>/CYX: converts the largest y unit to the largest x unit (with /Y only).</summary>
    public double? Cyx { get; init; }
    /// <summary>/O: where x and y are measured from, in default user space (relative to the viewport).</summary>
    public PdfPoint Origin { get; init; }

    private double XFactor => X.Count > 0 ? X[0].Factor : 1;
    private double YFactor => Y is { Count: > 0 } y ? y[0].Factor * (Cyx ?? 1) : XFactor;
    private double DistanceFactor => Distance.Count > 0 ? Distance[0].Factor : 1;
    private double AreaFactor => Area.Count > 0 ? Area[0].Factor : 1;

    /// <summary>The distance between two points of default user space, in the measure's distance unit.</summary>
    public double DistanceBetween(PdfPoint a, PdfPoint b)
    {
        double dx = (b.X - a.X) * XFactor, dy = (b.Y - a.Y) * YFactor;
        return Math.Sqrt(dx * dx + dy * dy) * DistanceFactor;
    }

    /// <summary>The length of the path through <paramref name="points"/>, back to the first when <paramref name="closed"/>.</summary>
    public double LengthOf(IReadOnlyList<PdfPoint> points, bool closed = false)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++) total += DistanceBetween(points[i - 1], points[i]);
        if (closed && points.Count > 2) total += DistanceBetween(points[^1], points[0]);
        return total;
    }

    /// <summary>The area the polygon encloses (shoelace formula; a self-crossing polygon counts its signed parts), in the area unit.</summary>
    public double AreaOf(IReadOnlyList<PdfPoint> points)
    {
        if (points.Count < 3) return 0;
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Count];
            sum += p.X * XFactor * (q.Y * YFactor) - q.X * XFactor * (p.Y * YFactor);
        }
        return Math.Abs(sum) / 2 * AreaFactor;
    }

    /// <summary>The angle of the segment from <paramref name="a"/> to <paramref name="b"/>, degrees counter-clockwise from the x axis, 0 to 360.</summary>
    public double AngleOf(PdfPoint a, PdfPoint b)
    {
        double degrees = Math.Atan2((b.Y - a.Y) * YFactor, (b.X - a.X) * XFactor) * 180 / Math.PI;
        if (degrees < 0) degrees += 360;
        return Math.Abs(degrees - 360) < 1e-9 ? 0 : degrees;
    }

    /// <summary>The x and y of a point measured from <see cref="Origin"/>, in the x and y units.</summary>
    public (double X, double Y) CoordinatesOf(PdfPoint p) => ((p.X - Origin.X) * XFactor, (p.Y - Origin.Y) * YFactor);

    public string FormatDistance(double value) => Format(Distance, value);
    public string FormatArea(double value) => Format(Area, value);
    public string FormatX(double value) => Format(X, value);
    public string FormatY(double value) => Format(Y ?? X, value);

    public string FormatAngle(double degrees) =>
        Angle is { Count: > 0 } t ? Format(t, degrees) : degrees.ToString("0.#", CultureInfo.InvariantCulture) + "°";

    /// <summary>
    /// A value through a chain of number formats: each but the last shows the whole part in its
    /// unit and passes the rest on, converted by the next one's factor ("5 ft 3 in").
    /// </summary>
    internal static string Format(IReadOnlyList<PdfNumberFormat> chain, double value)
    {
        if (chain.Count == 0) return value.ToString("0.##", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        bool negative = value < 0;
        double v = Math.Abs(value);
        for (int i = 0; i < chain.Count - 1; i++)
        {
            var next = chain[i + 1];
            double whole = Math.Floor(v);
            double rest = (v - whole) * next.Factor;
            // A rest that shows as a whole unit of this format carries: "6 ft 0 in", not "5 ft 12.00 in".
            if (next.Factor >= 1 && Rounded(next, rest) >= next.Factor - 1e-9) { whole += 1; rest = 0; }
            parts.Add((chain[i] with { Style = "R" }).FormatWithUnit(whole));
            v = rest;
        }
        parts.Add(chain[^1].FormatWithUnit(v));
        string text = string.Join(" ", parts);
        return negative ? "-" + text : text;
    }

    /// <summary>The value as the format rounds it for display.</summary>
    private static double Rounded(PdfNumberFormat f, double v) => f.Style switch
    {
        "R" => Math.Round(v, MidpointRounding.AwayFromZero),
        "T" => Math.Truncate(v),
        "F" => Math.Round(v * Math.Max(1, f.Precision)) / Math.Max(1, f.Precision),
        _ => Math.Round(v, Math.Clamp((int)Math.Round(Math.Log10(Math.Max(1, f.Precision))), 0, 10), MidpointRounding.AwayFromZero),
    };

    // ------------------------------------------------------------------ building from a scale

    /// <summary>The units a scale can be set in, with how many points each is.</summary>
    public static IReadOnlyList<string> Units { get; } = new[] { "pt", "in", "mm", "cm", "m", "ft", "km", "mi" };

    /// <summary>Points in one <paramref name="unit"/> (pt, in, mm, cm, m, ft, yd, km, mi), or null when unknown.</summary>
    public static double? PointsPer(string unit) => unit switch
    {
        "pt" => 1,
        "in" => 72,
        "mm" => 72 / 25.4,
        "cm" => 72 / 2.54,
        "m" => 7200 / 2.54,
        "ft" => 864,
        "yd" => 2592,
        "km" => 7_200_000 / 2.54,
        "mi" => 72.0 * 63360,
        _ => null,
    };

    /// <summary>
    /// The measure for a scale "<paramref name="paperValue"/> <paramref name="paperUnit"/> =
    /// <paramref name="realValue"/> <paramref name="realUnit"/>": one point on the page is
    /// (1/72 in) × real / paper, in the real unit. <paramref name="decimals"/> places are shown.
    /// </summary>
    public static PdfMeasure FromScale(double paperValue, string paperUnit, double realValue, string realUnit, int decimals = 2)
    {
        if (!(paperValue > 0) || !(realValue > 0) || !double.IsFinite(paperValue) || !double.IsFinite(realValue))
            throw new ArgumentOutOfRangeException(nameof(paperValue), "A scale needs two positive numbers.");
        double paper = PointsPer(paperUnit) ?? throw new ArgumentException($"Unknown unit '{paperUnit}'.", nameof(paperUnit));
        if (PointsPer(realUnit) == null) throw new ArgumentException($"Unknown unit '{realUnit}'.", nameof(realUnit));
        // One point of paper is 1/paper paper units, which stand for realValue/paperValue real units each.
        double factor = 1 / paper * (realValue / paperValue);
        int precision = (int)Math.Pow(10, Math.Clamp(decimals, 0, 6));
        return new PdfMeasure
        {
            Ratio = $"{Number(paperValue)} {paperUnit} = {Number(realValue)} {realUnit}",
            X = new[] { new PdfNumberFormat { Unit = realUnit, Factor = factor, Precision = precision } },
            Distance = new[] { new PdfNumberFormat { Unit = realUnit, Factor = 1, Precision = precision } },
            Area = new[] { new PdfNumberFormat { Unit = "sq " + realUnit, Factor = 1, Precision = precision } },
            Angle = new[] { new PdfNumberFormat { Unit = "°", Factor = 1, Precision = 10, Prefix = string.Empty } },
        };
    }

    private static string Number(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the dictionary

    /// <summary>The /Measure dictionary (Type Measure, Subtype RL).</summary>
    public PdfDictionary ToDictionary()
    {
        static PdfArray Chain(IReadOnlyList<PdfNumberFormat> formats) => new(formats.Select(f => (PdfObject)f.ToDictionary()).ToList());
        var e = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Measure"),
            ["Subtype"] = new PdfName("RL"),
            ["R"] = PdfObjectWriter.TextString(Ratio),
            ["X"] = Chain(X),
            ["D"] = Chain(Distance),
            ["A"] = Chain(Area),
        };
        if (Y is { Count: > 0 } y) e["Y"] = Chain(y);
        if (Angle is { Count: > 0 } t) e["T"] = Chain(t);
        if (Cyx is double cyx) e["CYX"] = new PdfReal(cyx);
        if (Origin != default) e["O"] = new PdfArray(new PdfObject[] { new PdfReal(Origin.X), new PdfReal(Origin.Y) });
        return new PdfDictionary(e);
    }

    /// <summary>A rectilinear measure as written; null for a geospatial one (/GEO) or one without the required /X, /D and /A.</summary>
    public static PdfMeasure? FromDictionary(PdfObject? obj, PdfObjectResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (resolver.Resolve(obj) is not PdfDictionary d) return null;
        if (resolver.Resolve(d["Subtype"]) is PdfName { Value: not "RL" }) return null;
        List<PdfNumberFormat>? Chain(string key)
        {
            if (resolver.Resolve(d[key]) is not PdfArray a || a.Count == 0) return null;
            var list = new List<PdfNumberFormat>();
            foreach (var item in a.Take(8))
            {
                if (PdfNumberFormat.FromDictionary(item, resolver) is not { } f) return null;
                list.Add(f);
            }
            return list;
        }
        var x = Chain("X");
        if (x == null) return null;
        var distance = Chain("D") ?? new List<PdfNumberFormat> { new() { Unit = x[0].Unit, Precision = x[0].Precision } };
        var area = Chain("A") ?? new List<PdfNumberFormat> { new() { Unit = "sq " + x[0].Unit, Precision = x[0].Precision } };
        var origin = PdfDocumentInfo.Numbers(resolver.Resolve(d["O"]), resolver) is { Length: 2 } o ? new PdfPoint(o[0], o[1]) : default;
        return new PdfMeasure
        {
            Ratio = PdfTextStrings.Decode(resolver.Resolve(d["R"])) ?? string.Empty,
            X = x,
            Y = Chain("Y"),
            Distance = distance,
            Area = area,
            Angle = Chain("T"),
            Cyx = resolver.Resolve(d["CYX"]) is { } c && c.TryGetNumber(out double cyx) && double.IsFinite(cyx) && cyx > 0 ? cyx : null,
            Origin = origin,
        };
    }

    /// <summary>
    /// The measure is the same scale and units as <paramref name="other"/>: number formats compared
    /// by value, conversion factors to the ten decimal places a file keeps.
    /// </summary>
    public bool SameAs(PdfMeasure? other)
    {
        static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(b));
        static bool Same(IReadOnlyList<PdfNumberFormat>? a, IReadOnlyList<PdfNumberFormat>? b)
        {
            a ??= Array.Empty<PdfNumberFormat>();
            b ??= Array.Empty<PdfNumberFormat>();
            return a.Count == b.Count && a.Zip(b).All(p => Close(p.First.Factor, p.Second.Factor) && p.First with { Factor = p.Second.Factor } == p.Second);
        }
        return other != null && Ratio == other.Ratio && Same(X, other.X) && Same(Y, other.Y) && Same(Distance, other.Distance)
            && Same(Area, other.Area) && Same(Angle, other.Angle) && Cyx == other.Cyx && Origin == other.Origin;
    }
}

/// <summary>A viewport of a page (ISO 32000-2 12.9.2, /VP): a rectangle with its own measure.</summary>
public sealed record PdfViewport(PdfRect Box, string? Name, PdfMeasure? Measure, bool IsGeospatial);

/// <summary>Reads a page's viewports and finds the one that applies to a point.</summary>
public static class PdfViewports
{
    public static IReadOnlyList<PdfViewport> Read(PdfVectorDocument document, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageNumber < 1 || pageNumber > document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var r = document.Resolver;
        var list = new List<PdfViewport>();
        if (r.Resolve(document.PageTree.Pages[pageNumber - 1].Dictionary["VP"]) is not PdfArray vp) return list;
        foreach (var item in vp.Take(256))
        {
            if (r.Resolve(item) is not PdfDictionary d || PdfDocumentInfo.ReadRect(d["BBox"], r) is not { } box) continue;
            var measure = r.Resolve(d["Measure"]) as PdfDictionary;
            bool geo = measure != null && r.Resolve(measure["Subtype"]) is PdfName { Value: "GEO" };
            list.Add(new PdfViewport(box, PdfTextStrings.Decode(r.Resolve(d["Name"])), PdfMeasure.FromDictionary(measure, r), geo));
        }
        return list;
    }

    /// <summary>The viewport a point is measured in: the last one in the array that contains it (12.9.2), or null.</summary>
    public static PdfViewport? At(IReadOnlyList<PdfViewport> viewports, PdfPoint point)
    {
        for (int i = viewports.Count - 1; i >= 0; i--)
        {
            var b = viewports[i].Box;
            if (point.X >= b.Left - 1e-6 && point.X <= b.Right + 1e-6 && point.Y >= b.Top - 1e-6 && point.Y <= b.Bottom + 1e-6)
                return viewports[i];
        }
        return null;
    }
}

/// <summary>What a measurement annotation measures.</summary>
public enum PdfMeasurementKind
{
    /// <summary>A Line annotation, /IT /LineDimension.</summary>
    Distance,
    /// <summary>A PolyLine annotation, /IT /PolyLineDimension.</summary>
    Perimeter,
    /// <summary>A Polygon annotation, /IT /PolygonDimension.</summary>
    Area,
}

/// <summary>
/// Creates measurement annotations as Acrobat writes them (ISO 32000-2 12.5.6.7, 12.5.6.9 and
/// 12.9): a Line with /Measure and a caption for a distance, a PolyLine for a perimeter and a
/// Polygon for an area, each with its intent, its measure and the result as /Contents.
/// </summary>
public static class PdfMeasurements
{
    public const string DistanceIntent = "LineDimension", PerimeterIntent = "PolyLineDimension", AreaIntent = "PolygonDimension";

    /// <summary>The size the label is drawn at, in points.</summary>
    internal const double LabelSize = 9;

    /// <summary>What an annotation of <paramref name="subtype"/> with a /Measure measures, or null for other subtypes.</summary>
    public static PdfMeasurementKind? KindOf(string? subtype) => subtype switch
    {
        "Line" => PdfMeasurementKind.Distance,
        "PolyLine" => PdfMeasurementKind.Perimeter,
        "Polygon" => PdfMeasurementKind.Area,
        _ => null,
    };

    /// <summary>The result as it is labelled: the distance, the perimeter or the area.</summary>
    public static string Label(PdfMeasurementKind kind, IReadOnlyList<PdfPoint> points, PdfMeasure measure) => kind switch
    {
        PdfMeasurementKind.Distance => measure.FormatDistance(points.Count >= 2 ? measure.DistanceBetween(points[0], points[^1]) : 0),
        PdfMeasurementKind.Perimeter => measure.FormatDistance(measure.LengthOf(points)),
        _ => measure.FormatArea(measure.AreaOf(points)),
    };

    /// <summary>
    /// A measurement annotation over <paramref name="points"/> (two for a distance, two or more
    /// for a perimeter, three or more for an area) in default user space.
    /// </summary>
    public static PdfAnnotation Create(PdfMeasurementKind kind, int pageNumber, IReadOnlyList<PdfPoint> points, PdfMeasure measure,
        double[]? color = null, double width = 1)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(measure);
        int needed = kind == PdfMeasurementKind.Area ? 3 : 2;
        if (points.Count < needed) throw new ArgumentException($"A {kind.ToString().ToLowerInvariant()} measurement needs at least {needed} points.", nameof(points));
        var pts = kind == PdfMeasurementKind.Distance ? new[] { points[0], points[^1] } : points.ToArray();
        string label = Label(kind, pts, measure);
        var a = new PdfAnnotation
        {
            PageNumber = pageNumber,
            Subtype = kind switch { PdfMeasurementKind.Distance => "Line", PdfMeasurementKind.Perimeter => "PolyLine", _ => "Polygon" },
            Intent = kind switch { PdfMeasurementKind.Distance => DistanceIntent, PdfMeasurementKind.Perimeter => PerimeterIntent, _ => AreaIntent },
            Measure = measure,
            Contents = label,
            Color = color ?? new[] { 0.0, 0.2, 0.8 },
            BorderWidth = Math.Max(0.25, width),
            Subject = kind switch { PdfMeasurementKind.Distance => "Distance Measurement", PdfMeasurementKind.Perimeter => "Perimeter Measurement", _ => "Area Measurement" },
        };
        if (kind == PdfMeasurementKind.Distance)
        {
            a.Line = new[] { pts[0].X, pts[0].Y, pts[1].X, pts[1].Y };
            a.LineEndings = new[] { "OpenArrow", "OpenArrow" };
            a.Caption = true;
        }
        else
        {
            a.Vertices = pts.ToList();
        }
        a.Rect = Bounds(kind, pts, label, a.BorderWidth ?? 1);
        return a;
    }

    /// <summary>The rectangle the drawing and its label fit in: the points, the arrow heads and the label's box.</summary>
    public static PdfRect Bounds(PdfMeasurementKind kind, IReadOnlyList<PdfPoint> points, string label, double width)
    {
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double margin = kind == PdfMeasurementKind.Distance ? 9 * Math.Max(width, 1) * 0.6 + width : width * 2 + 1;
        minX -= margin; minY -= margin; maxX += margin; maxY += margin;
        var anchor = LabelAnchor(kind, points);
        // The label's box, generously: its glyphs average under 0.6 em.
        double halfW = Math.Max(label.Length, 1) * LabelSize * 0.33 + 3, halfH = LabelSize * 0.9;
        minX = Math.Min(minX, anchor.X - halfW); maxX = Math.Max(maxX, anchor.X + halfW);
        minY = Math.Min(minY, anchor.Y - halfH); maxY = Math.Max(maxY, anchor.Y + halfH);
        return new PdfRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Where the label is centred: mid-line for a distance, the last segment's middle for a perimeter, the centroid for an area.</summary>
    internal static PdfPoint LabelAnchor(PdfMeasurementKind kind, IReadOnlyList<PdfPoint> points)
    {
        if (kind == PdfMeasurementKind.Area && points.Count >= 3)
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i]; var q = points[(i + 1) % points.Count];
                double cross = p.X * q.Y - q.X * p.Y;
                a += cross; cx += (p.X + q.X) * cross; cy += (p.Y + q.Y) * cross;
            }
            if (Math.Abs(a) > 1e-9) return new PdfPoint(cx / (3 * a), cy / (3 * a));
            return new PdfPoint(points.Average(p => p.X), points.Average(p => p.Y));
        }
        var s = points[^2]; var e = points[^1];
        return new PdfPoint((s.X + e.X) / 2, (s.Y + e.Y) / 2);
    }
}
