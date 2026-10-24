using System.Globalization;
using System.Text;

namespace PdfViewer.Web.Engine.Demo;

/// <summary>
/// A small monoline font drawn for the demo document: printable ASCII as stroked lines and
/// elliptical arcs in a 1000-unit em (cap height 700, x-height 480, descender −200). The demo
/// embeds it as a Type 3 font, so the document needs no font file and draws the same in every
/// browser. Written for this project and released with it under the MIT License.
/// </summary>
/// <remarks>
/// Glyph outlines use a four-command notation: <c>M x y</c> starts a stroke, <c>L x y</c> draws a
/// line, <c>R cx cy rx ry a0 a1</c> an elliptical arc from angle <c>a0</c> to <c>a1</c> (degrees,
/// counterclockwise, y up; joined to the stroke in progress), <c>E cx cy rx ry</c> a closed ellipse.
/// </remarks>
internal static class StrokeFont
{
    /// <summary>Stroke width in glyph units.</summary>
    public const int StrokeWidth = 64;

    /// <summary>Advance width and outline of each character.</summary>
    public static readonly IReadOnlyDictionary<char, (int Width, string Outline)> Glyphs = new Dictionary<char, (int, string)>
    {
        [' '] = (280, ""),
        ['!'] = (200, "M100 700 L100 220 M100 0 L100 8"),
        ['"'] = (300, "M90 700 L90 520 M210 700 L210 520"),
        ['%'] = (620, "E160 560 90 130 E460 140 90 130 M500 700 L120 0"),
        ['\''] = (180, "M90 700 L90 520"),
        ['('] = (300, "R330 260 230 450 125 235"),
        [')'] = (300, "R-30 260 230 450 55 -55"),
        ['*'] = (400, "M200 700 L200 450 M90 640 L310 510 M310 640 L90 510"),
        ['+'] = (520, "M260 120 L260 520 M60 320 L460 320"),
        [','] = (200, "M110 20 L60 -110"),
        ['-'] = (380, "M60 260 L320 260"),
        ['.'] = (200, "M100 0 L100 8"),
        ['/'] = (360, "M40 -60 L320 720"),
        ['0'] = (560, "E280 350 210 345"),
        ['1'] = (560, "M150 560 L300 700 L300 0"),
        ['2'] = (560, "R280 500 200 200 160 -40 L70 0 L500 0"),
        ['3'] = (560, "R270 530 190 170 150 -90 R270 180 210 180 90 -150"),
        ['4'] = (560, "M400 0 L400 700 L50 220 L520 220"),
        ['5'] = (560, "M480 700 L110 700 L90 380 R290 220 200 210 127 -150"),
        ['6'] = (560, "E280 215 200 215 M420 700 L100 300"),
        ['7'] = (560, "M60 700 L500 700 L200 0"),
        ['8'] = (560, "E280 540 170 160 E280 190 210 190"),
        ['9'] = (560, "E280 485 200 215 M460 400 L140 0"),
        [':'] = (200, "M100 0 L100 8 M100 380 L100 388"),
        [';'] = (200, "M100 380 L100 388 M110 20 L60 -110"),
        ['<'] = (480, "M420 560 L60 320 L420 80"),
        ['='] = (520, "M70 220 L450 220 M70 400 L450 400"),
        ['>'] = (480, "M60 560 L420 320 L60 80"),
        ['?'] = (480, "R240 530 170 170 160 -60 L240 250 L240 200 M240 0 L240 8"),
        ['A'] = (600, "M40 0 L300 700 L560 0 M125 230 L475 230"),
        ['B'] = (580, "M80 0 L80 700 L320 700 R320 535 175 165 90 -90 L80 370 M320 370 R320 185 190 185 90 -90 L80 0"),
        ['C'] = (620, "R340 350 270 340 45 315"),
        ['D'] = (620, "M80 0 L80 700 L280 700 R280 350 270 350 90 -90 L80 0"),
        ['E'] = (540, "M480 700 L80 700 L80 0 L480 0 M80 360 L400 360"),
        ['F'] = (520, "M480 700 L80 700 L80 0 M80 360 L400 360"),
        ['G'] = (640, "R340 350 270 340 45 360 L400 350"),
        ['H'] = (620, "M80 0 L80 700 M540 0 L540 700 M80 360 L540 360"),
        ['I'] = (200, "M100 0 L100 700"),
        ['J'] = (460, "M380 700 L380 200 R220 200 160 200 0 -180"),
        ['K'] = (580, "M80 0 L80 700 M520 700 L80 260 M235 415 L540 0"),
        ['L'] = (500, "M80 700 L80 0 L460 0"),
        ['M'] = (740, "M80 0 L80 700 L370 160 L660 700 L660 0"),
        ['N'] = (640, "M80 0 L80 700 L560 0 L560 700"),
        ['O'] = (700, "E350 350 290 345"),
        ['P'] = (560, "M80 0 L80 700 L310 700 R310 520 190 180 90 -90 L80 340"),
        ['Q'] = (700, "E350 350 290 345 M400 200 L640 -20"),
        ['R'] = (580, "M80 0 L80 700 L310 700 R310 525 190 175 90 -90 L80 350 M300 350 L520 0"),
        ['S'] = (560, "R280 525 200 175 30 270 R280 175 210 175 90 -150"),
        ['T'] = (560, "M30 700 L530 700 M280 700 L280 0"),
        ['U'] = (620, "M80 700 L80 250 R310 250 230 250 180 360 L540 700"),
        ['V'] = (600, "M40 700 L300 0 L560 700"),
        ['W'] = (860, "M40 700 L230 0 L430 560 L630 0 L820 700"),
        ['X'] = (580, "M50 700 L530 0 M530 700 L50 0"),
        ['Y'] = (580, "M50 700 L290 350 L530 700 M290 350 L290 0"),
        ['Z'] = (560, "M60 700 L500 700 L60 0 L500 0"),
        ['['] = (280, "M220 720 L90 720 L90 -120 L220 -120"),
        [']'] = (280, "M60 720 L190 720 L190 -120 L60 -120"),
        ['_'] = (500, "M40 -100 L460 -100"),
        ['a'] = (540, "E250 240 190 240 M440 480 L440 0"),
        ['b'] = (540, "M80 720 L80 0 E270 240 190 240"),
        ['c'] = (500, "R270 240 210 240 50 310"),
        ['d'] = (540, "E270 240 190 240 M460 720 L460 0"),
        ['e'] = (520, "M60 240 L470 240 R265 240 205 240 0 320"),
        ['f'] = (320, "M300 700 L220 700 R220 600 100 100 90 180 L120 0 M30 470 L290 470"),
        ['g'] = (540, "E270 240 190 240 M460 480 L460 -40 R270 -40 190 160 0 -160"),
        ['h'] = (540, "M80 720 L80 0 M80 290 R270 290 190 190 180 0 L460 0"),
        ['i'] = (200, "M100 0 L100 480 M100 650 L100 658"),
        ['j'] = (240, "M150 480 L150 -80 R50 -80 100 120 0 -90 M150 650 L150 658"),
        ['k'] = (500, "M80 720 L80 0 M440 480 L80 160 M200 270 L460 0"),
        ['l'] = (200, "M100 720 L100 0"),
        ['m'] = (800, "M80 0 L80 480 M80 320 R225 320 145 160 180 0 L370 0 M370 320 R515 320 145 160 180 0 L660 0"),
        ['n'] = (540, "M80 0 L80 480 M80 290 R270 290 190 190 180 0 L460 0"),
        ['o'] = (540, "E270 240 200 240"),
        ['p'] = (540, "M80 480 L80 -200 E270 240 190 240"),
        ['q'] = (540, "E270 240 190 240 M460 480 L460 -200"),
        ['r'] = (360, "M80 0 L80 480 M80 280 R270 280 190 200 180 75"),
        ['s'] = (460, "R230 360 160 120 30 270 R230 120 170 120 90 -150"),
        ['t'] = (340, "M150 680 L150 100 R250 100 100 100 180 270 M40 470 L300 470"),
        ['u'] = (540, "M80 480 L80 190 R270 190 190 190 180 360 M460 480 L460 0"),
        ['v'] = (500, "M40 480 L250 0 L460 480"),
        ['w'] = (740, "M40 480 L200 0 L370 400 L540 0 L700 480"),
        ['x'] = (480, "M50 480 L430 0 M430 480 L50 0"),
        ['y'] = (500, "M40 480 L260 20 M460 480 L160 -200"),
        ['z'] = (460, "M60 480 L400 480 L60 0 L400 0"),
        ['|'] = (200, "M100 720 L100 -150"),
    };

    /// <summary>The character drawn in place of one the font does not have.</summary>
    public const char Replacement = '?';

    /// <summary>Width of <paramref name="text"/> at <paramref name="size"/> points.</summary>
    public static double Measure(string text, double size)
    {
        double units = 0;
        foreach (char c in text) units += Glyphs.TryGetValue(c, out var g) ? g.Width : Glyphs[Replacement].Width;
        return units * size / 1000.0;
    }

    /// <summary>The text with every character the font lacks replaced.</summary>
    public static string Supported(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(Glyphs.ContainsKey(c) ? c : Replacement);
        return sb.ToString();
    }

    /// <summary>The PDF path operators of an outline (stroked by the caller).</summary>
    public static string ToPath(string outline)
    {
        var sb = new StringBuilder();
        var t = outline.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool open = false;
        for (int i = 0; i < t.Length;)
        {
            char op = t[i][0];
            double N(int k) => double.Parse(k == 0 ? t[i].Substring(1) : t[i + k], CultureInfo.InvariantCulture);
            switch (op)
            {
                case 'M':
                    Append(sb, N(0), N(1), "m");
                    open = true;
                    i += 2;
                    break;
                case 'L':
                    Append(sb, N(0), N(1), "l");
                    i += 2;
                    break;
                case 'R':
                    Arc(sb, N(0), N(1), N(2), N(3), N(4), N(5), join: open);
                    open = true;
                    i += 6;
                    break;
                case 'E':
                    Arc(sb, N(0), N(1), N(2), N(3), 0, 360, join: false);
                    sb.Append("h ");
                    open = false;
                    i += 4;
                    break;
                default:
                    throw new FormatException($"Unknown outline command '{op}'.");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>An elliptical arc as cubic Béziers of at most 90° each.</summary>
    private static void Arc(StringBuilder sb, double cx, double cy, double rx, double ry, double a0, double a1, bool join)
    {
        double t0 = a0 * Math.PI / 180, t1 = a1 * Math.PI / 180;
        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(t1 - t0) / (Math.PI / 2) - 1e-9));
        double step = (t1 - t0) / segments;
        double k = 4.0 / 3.0 * Math.Tan(step / 4);
        Append(sb, cx + rx * Math.Cos(t0), cy + ry * Math.Sin(t0), join ? "l" : "m");
        for (int s = 0; s < segments; s++)
        {
            double a = t0 + s * step, b = a + step;
            double x0 = cx + rx * Math.Cos(a), y0 = cy + ry * Math.Sin(a);
            double x3 = cx + rx * Math.Cos(b), y3 = cy + ry * Math.Sin(b);
            double x1 = x0 - k * rx * Math.Sin(a), y1 = y0 + k * ry * Math.Cos(a);
            double x2 = x3 + k * rx * Math.Sin(b), y2 = y3 - k * ry * Math.Cos(b);
            sb.Append(F(x1)).Append(' ').Append(F(y1)).Append(' ').Append(F(x2)).Append(' ').Append(F(y2)).Append(' ')
              .Append(F(x3)).Append(' ').Append(F(y3)).Append(" c ");
        }
    }

    private static void Append(StringBuilder sb, double x, double y, string op) =>
        sb.Append(F(x)).Append(' ').Append(F(y)).Append(' ').Append(op).Append(' ');

    internal static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>PostScript glyph names of the characters, for the font's /Differences (text extraction maps them back to Unicode).</summary>
    public static string GlyphName(char c) => c switch
    {
        ' ' => "space", '!' => "exclam", '"' => "quotedbl", '%' => "percent", '\'' => "quotesingle",
        '(' => "parenleft", ')' => "parenright", '*' => "asterisk", '+' => "plus", ',' => "comma",
        '-' => "hyphen", '.' => "period", '/' => "slash", ':' => "colon", ';' => "semicolon",
        '<' => "less", '=' => "equal", '>' => "greater", '?' => "question", '[' => "bracketleft",
        ']' => "bracketright", '_' => "underscore", '|' => "bar",
        >= '0' and <= '9' => new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" }[c - '0'],
        _ when char.IsAsciiLetter(c) => c.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };
}
