using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Forms;

/// <summary>A new value for a field: text (text fields, combo boxes), on/off, a radio choice, or list selections.</summary>
public sealed record PdfFieldChange(string FullName, string? Text = null, bool? Checked = null, string? Choice = null, IReadOnlyList<string>? Selections = null);

/// <summary>
/// Fills form fields and writes the result as an incremental update: each changed field gets its
/// new /V, each of its widgets a regenerated appearance (text and choice fields: the value laid
/// out in the field's /DA font, size and colour, with its alignment, multi-line wrapping, comb
/// cells and /MK background and border; buttons: their /AS state). Viewers that trust appearance
/// streams (most do) show exactly this; the original bytes are untouched, so signatures on
/// earlier revisions stay valid.
/// </summary>
public static class PdfFormFiller
{
    public static byte[] Apply(PdfVectorDocument document, byte[] original, IEnumerable<PdfFieldChange> changes)
    {
        var form = PdfAcroForm.Read(document) ?? throw new InvalidOperationException("The document has no form.");
        var r = document.Resolver;
        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(document);

        // Objects changed so far, so that a field whose widget is its own dictionary is edited once.
        PdfDictionary Current(int number) =>
            objects.TryGetValue(number, out var o) ? (PdfDictionary)o : r.Resolve(number) as PdfDictionary
            ?? throw new InvalidOperationException($"Object {number} is not a dictionary.");
        void Set(int number, string key, PdfObject? value)
        {
            var entries = new Dictionary<string, PdfObject>(Current(number).Entries);
            if (value == null) entries.Remove(key); else entries[key] = value;
            objects[number] = new PdfDictionary(entries);
        }

        foreach (var change in changes)
        {
            var field = form[change.FullName] ?? throw new KeyNotFoundException($"No field named '{change.FullName}'.");
            if (field.IsReadOnly)
                throw new InvalidOperationException($"Field '{field.FullName}' is read-only.");

            switch (field.Kind)
            {
                case PdfFormFieldKind.Text:
                case PdfFormFieldKind.ComboBox:
                {
                    string text = change.Text ?? change.Choice ?? string.Empty;
                    if (field.Kind == PdfFormFieldKind.Text && field.MaxLength > 0 && text.Length > field.MaxLength)
                        text = text[..field.MaxLength];
                    Set(field.ObjectNumber, "V", PdfObjectWriter.TextString(text));
                    string shown = field.Kind == PdfFormFieldKind.ComboBox
                        ? field.Options.FirstOrDefault(o => o.Export == text).Display ?? text
                        : field.IsPassword ? new string('*', text.Length) : text;
                    foreach (var w in field.Widgets)
                        next = SetAppearance(w, field, form, r, objects, Current, Set, next, stream => TextAppearance(stream, field, form, r, w, shown));
                    break;
                }
                case PdfFormFieldKind.ListBox:
                {
                    var selected = change.Selections ?? (change.Choice != null ? new[] { change.Choice } : Array.Empty<string>());
                    Set(field.ObjectNumber, "V", selected.Count == 1 ? PdfObjectWriter.TextString(selected[0])
                        : new PdfArray(selected.Select(s => (PdfObject)PdfObjectWriter.TextString(s)).ToList()));
                    var indices = selected.Select(s => field.Options.ToList().FindIndex(o => o.Export == s)).Where(i => i >= 0).OrderBy(i => i).ToList();
                    Set(field.ObjectNumber, "I", indices.Count > 0 ? new PdfArray(indices.Select(i => (PdfObject)new PdfInteger(i)).ToList()) : null);
                    foreach (var w in field.Widgets)
                        next = SetAppearance(w, field, form, r, objects, Current, Set, next, stream => ListAppearance(stream, field, form, r, w, indices));
                    break;
                }
                case PdfFormFieldKind.CheckBox:
                {
                    bool on = change.Checked ?? false;
                    string onState = field.Widgets.Select(w => w.OnState).FirstOrDefault(s => s != null) ?? "Yes";
                    var v = new PdfName(on ? onState : "Off");
                    Set(field.ObjectNumber, "V", v);
                    foreach (var w in field.Widgets)
                        Set(w.ObjectNumber, "AS", new PdfName(on && w.OnState != null ? w.OnState : "Off"));
                    break;
                }
                case PdfFormFieldKind.RadioGroup:
                {
                    string choice = change.Choice ?? "Off";
                    if (choice != "Off" && field.Widgets.All(w => w.OnState != choice))
                        throw new ArgumentException($"'{choice}' is not an option of '{field.FullName}'.");
                    Set(field.ObjectNumber, "V", new PdfName(choice));
                    foreach (var w in field.Widgets)
                        Set(w.ObjectNumber, "AS", new PdfName(w.OnState == choice ? choice : "Off"));
                    break;
                }
                default:
                    throw new NotSupportedException($"Fields of kind {field.Kind} are not filled here.");
            }
        }
        return PdfIncrementalWriter.Append(original, document, objects);
    }

    private delegate void Setter(int number, string key, PdfObject? value);

    /// <summary>A new /AP /N stream for a widget; returns the next free object number.</summary>
    private static int SetAppearance(PdfFormWidget w, PdfFormField field, PdfAcroForm form, Parsing.PdfObjectResolver r,
        Dictionary<int, PdfObject> objects, Func<int, PdfDictionary> current, Action<int, string, PdfObject?> set, int next,
        Func<StringBuilder, PdfDictionary?> draw)
    {
        var content = new StringBuilder();
        var fontResources = draw(content);
        double width = w.Rect.Width, height = w.Rect.Height;
        if (w.Rotation is 90 or 270) (width, height) = (height, width);
        var entries = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Form"),
            ["BBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height) }),
        };
        if (w.Rotation != 0)
            entries["Matrix"] = RotationMatrix(w.Rotation, w.Rect.Width, w.Rect.Height);
        if (fontResources != null)
            entries["Resources"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Font"] = fontResources });
        int number = next++;
        objects[number] = PdfObjectWriter.NewStream(entries, Encoding.Latin1.GetBytes(content.ToString()));

        var ap = r.Resolve(current(w.ObjectNumber)["AP"]) as PdfDictionary;
        var apEntries = ap != null ? new Dictionary<string, PdfObject>(ap.Entries) : new Dictionary<string, PdfObject>();
        apEntries["N"] = new PdfIndirectRef(number);
        apEntries.Remove("D"); // a stale "down" appearance would show the old value while pressed
        set(w.ObjectNumber, "AP", new PdfDictionary(apEntries));
        return next;
    }

    private static PdfArray RotationMatrix(int rotation, double w, double h) => rotation switch
    {
        90 => new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(1), new PdfInteger(-1), new PdfInteger(0), new PdfReal(w), new PdfInteger(0) }),
        180 => new PdfArray(new PdfObject[] { new PdfInteger(-1), new PdfInteger(0), new PdfInteger(0), new PdfInteger(-1), new PdfReal(w), new PdfReal(h) }),
        _ => new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(-1), new PdfInteger(1), new PdfInteger(0), new PdfInteger(0), new PdfReal(h) }),
    };

    // ------------------------------------------------------------------ text layout

    /// <summary>The /DA string parsed: font resource name, size (0 = auto) and the colour operators.</summary>
    internal sealed record Appearance(string FontName, double Size, string ColourOps);

    internal static Appearance ParseDA(string? da)
    {
        string font = "Helv";
        double size = 0;
        string colour = "0 g";
        if (string.IsNullOrWhiteSpace(da))
            return new Appearance(font, size, colour);
        var tokens = da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "Tf" when i >= 2:
                    font = tokens[i - 2].TrimStart('/');
                    double.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out size);
                    break;
                case "g" when i >= 1:
                    colour = $"{tokens[i - 1]} g";
                    break;
                case "rg" when i >= 3:
                    colour = $"{tokens[i - 3]} {tokens[i - 2]} {tokens[i - 1]} rg";
                    break;
                case "k" when i >= 4:
                    colour = $"{tokens[i - 4]} {tokens[i - 3]} {tokens[i - 2]} {tokens[i - 1]} k";
                    break;
            }
        }
        return new Appearance(font, size, colour);
    }

    /// <summary>Glyph widths (1/1000 em) and byte encoding of a simple font, for layout and the content stream.</summary>
    internal sealed class FontMetrics
    {
        private readonly Func<int, double> _width;
        public double Ascent { get; }
        public double Descent { get; }
        public FontMetrics(Func<int, double> width, double ascent, double descent)
        {
            _width = width;
            Ascent = ascent;
            Descent = descent;
        }
        /// <summary>WinAnsi byte for a character; '?' when the font cannot show it.</summary>
        public byte Encode(char c) => c < 0x80 ? (byte)c : WinAnsi.TryGetValue(c, out byte b) ? b : (byte)'?';
        public double Width(string text, double size) => text.Sum(c => _width(Encode(c))) * size / 1000.0;

        private static readonly Dictionary<char, byte> WinAnsi = BuildWinAnsi();
        private static Dictionary<char, byte> BuildWinAnsi()
        {
            // WinAnsiEncoding (Annex D.2): Latin-1 from 0xA0, and this block below it.
            var map = new Dictionary<char, byte>();
            for (int b = 0xA0; b <= 0xFF; b++) map[(char)b] = (byte)b;
            (char C, byte B)[] low =
            {
                ('€', 0x80), ('‚', 0x82), ('ƒ', 0x83), ('„', 0x84), ('…', 0x85), ('†', 0x86),
                ('‡', 0x87), ('ˆ', 0x88), ('‰', 0x89), ('Š', 0x8A), ('‹', 0x8B), ('Œ', 0x8C),
                ('Ž', 0x8E), ('‘', 0x91), ('’', 0x92), ('“', 0x93), ('”', 0x94), ('•', 0x95),
                ('–', 0x96), ('—', 0x97), ('˜', 0x98), ('™', 0x99), ('š', 0x9A), ('›', 0x9B),
                ('œ', 0x9C), ('ž', 0x9E), ('Ÿ', 0x9F),
            };
            foreach (var (c, v) in low) map[c] = v;
            return map;
        }
    }

    internal static FontMetrics Metrics(PdfDictionary? fontDict, Parsing.PdfObjectResolver r)
    {
        string baseFont = fontDict?.GetName("BaseFont") ?? "Helvetica";
        int first = (int)(r.Resolve(fontDict?["FirstChar"]) is PdfInteger fc ? fc.Value : 0);
        var widths = r.Resolve(fontDict?["Widths"]) as PdfArray;
        var descriptor = r.Resolve(fontDict?["FontDescriptor"]) as PdfDictionary;
        double asc = descriptor?.GetNumber("Ascent") is double a && a > 0 ? a : 800;
        double desc = descriptor?.GetNumber("Descent") is double d && d < 0 ? d : -200;
        return new FontMetrics(code =>
        {
            if (widths != null && code >= first && code - first < widths.Count && r.Resolve(widths[code - first]) is { } wo && wo.TryGetNumber(out double w) && w > 0)
                return w;
            return Standard14Fonts.TryGetWidth(baseFont, code, out double sw) ? sw : 500;
        }, asc, desc);
    }

    private static (PdfDictionary? Resources, PdfDictionary? FontDict, Appearance Da) Font(PdfFormField field, PdfAcroForm form, Parsing.PdfObjectResolver r)
    {
        var da = ParseDA(field.DefaultAppearance ?? form.DefaultAppearance);
        var fonts = r.Resolve(form.DefaultResources?["Font"]) as PdfDictionary;
        var fontRef = fonts?[da.FontName];
        var fontDict = r.Resolve(fontRef) as PdfDictionary;
        PdfDictionary? resources = null;
        if (fontRef != null)
        {
            resources = new PdfDictionary(new Dictionary<string, PdfObject> { [da.FontName] = fontRef });
        }
        else
        {
            // No such resource: Helvetica, the form default, embedded as a standard font.
            da = da with { FontName = "Helv" };
            resources = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Helv"] = new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("Type1"),
                    ["BaseFont"] = new PdfName("Helvetica"), ["Encoding"] = new PdfName("WinAnsiEncoding"),
                }),
            });
        }
        return (resources, fontDict, da);
    }

    private static void Frame(StringBuilder s, PdfFormWidget w, double width, double height)
    {
        if (w.Background is { Length: > 0 } bg)
            s.Append(Colour(bg, fill: true)).Append($" 0 0 {F(width)} {F(height)} re f\n");
        if (w.Border is { Length: > 0 } bc && w.BorderWidth > 0)
        {
            double bw = w.BorderWidth;
            s.Append(Colour(bc, fill: false)).Append($" {F(bw)} w {F(bw / 2)} {F(bw / 2)} {F(width - bw)} {F(height - bw)} re S\n");
        }
    }

    private static PdfDictionary? TextAppearance(StringBuilder s, PdfFormField field, PdfAcroForm form, Parsing.PdfObjectResolver r, PdfFormWidget w, string text)
    {
        var (resources, fontDict, da) = Font(field, form, r);
        var metrics = Metrics(fontDict, r);
        double width = w.Rect.Width, height = w.Rect.Height;
        if (w.Rotation is 90 or 270) (width, height) = (height, width);
        double pad = Math.Max(1, w.Border != null ? w.BorderWidth : 0) + 1;
        double innerW = Math.Max(1, width - 2 * pad), innerH = Math.Max(1, height - 2 * pad);
        double em = (metrics.Ascent - metrics.Descent) / 1000.0;

        Frame(s, w, width, height);
        s.Append("/Tx BMC\nq\n");
        s.Append($"{F(pad / 2)} {F(pad / 2)} {F(width - pad)} {F(height - pad)} re W n\n");
        s.Append("BT\n");

        if (field.IsMultiline && !field.IsComb)
        {
            double size = da.Size > 0 ? da.Size : 12;
            var lines = Wrap(text, metrics, size, innerW);
            if (da.Size <= 0)
            {
                // Auto size: shrink until every line fits (Acrobat's behaviour for size 0).
                while (size > 4 && lines.Count * size * 1.15 > innerH) { size -= 0.5; lines = Wrap(text, metrics, size, innerW); }
            }
            double leading = size * 1.15;
            s.Append($"/{da.FontName} {F(size)} Tf {da.ColourOps}\n");
            double y = height - pad - metrics.Ascent / 1000.0 * size;
            foreach (var line in lines)
            {
                double x = Align(field.Quadding, pad, innerW, metrics.Width(line, size));
                s.Append($"1 0 0 1 {F(x)} {F(y)} Tm ({Escape(line, metrics)}) Tj\n");
                y -= leading;
            }
        }
        else
        {
            double size = da.Size > 0 ? da.Size : Math.Min(innerH / Math.Max(em, 0.5), 12 * innerH / 10);
            if (da.Size <= 0)
            {
                size = Math.Clamp(innerH / Math.Max(em, 0.5) * 0.9, 4, 12 * Math.Max(1, innerH / 12));
                double tw = metrics.Width(text, size);
                if (tw > innerW && tw > 0) size = Math.Max(4, size * innerW / tw);
            }
            double baseline = pad + (innerH - em * size) / 2 - metrics.Descent / 1000.0 * size;
            s.Append($"/{da.FontName} {F(size)} Tf {da.ColourOps}\n");
            if (field.IsComb)
            {
                double cell = width / field.MaxLength;
                for (int i = 0; i < text.Length && i < field.MaxLength; i++)
                {
                    string ch = text[i].ToString();
                    double x = i * cell + (cell - metrics.Width(ch, size)) / 2;
                    s.Append($"1 0 0 1 {F(x)} {F(baseline)} Tm ({Escape(ch, metrics)}) Tj\n");
                }
            }
            else
            {
                double x = Align(field.Quadding, pad, innerW, metrics.Width(text, size));
                s.Append($"1 0 0 1 {F(x)} {F(baseline)} Tm ({Escape(text, metrics)}) Tj\n");
            }
        }
        s.Append("ET\nQ\nEMC\n");
        return resources;
    }

    private static PdfDictionary? ListAppearance(StringBuilder s, PdfFormField field, PdfAcroForm form, Parsing.PdfObjectResolver r, PdfFormWidget w, IReadOnlyList<int> selected)
    {
        var (resources, fontDict, da) = Font(field, form, r);
        var metrics = Metrics(fontDict, r);
        double width = w.Rect.Width, height = w.Rect.Height;
        double size = da.Size > 0 ? da.Size : 12;
        double leading = size * 1.15, pad = 2;
        Frame(s, w, width, height);
        s.Append("/Tx BMC\nq\n");
        s.Append($"1 1 {F(width - 2)} {F(height - 2)} re W n\n");
        double top = height - pad;
        for (int i = 0; i < field.Options.Count; i++)
        {
            double y0 = top - (i + 1) * leading;
            if (y0 + leading < 0) break;
            if (selected.Contains(i))
                s.Append($"0.6 0.75 0.86 rg 1 {F(y0)} {F(width - 2)} {F(leading)} re f\n");
        }
        s.Append($"BT /{da.FontName} {F(size)} Tf {da.ColourOps}\n");
        for (int i = 0; i < field.Options.Count; i++)
        {
            double baseline = top - (i + 1) * leading - metrics.Descent / 1000.0 * size + (leading - size) / 2;
            if (baseline < -leading) break;
            s.Append($"1 0 0 1 {F(pad)} {F(baseline)} Tm ({Escape(field.Options[i].Display, metrics)}) Tj\n");
        }
        s.Append("ET\nQ\nEMC\n");
        return resources;
    }

    private static double Align(int quadding, double pad, double inner, double textWidth) => quadding switch
    {
        1 => pad + (inner - textWidth) / 2,
        2 => pad + inner - textWidth,
        _ => pad,
    };

    /// <summary>Word wrap to a width; explicit line breaks kept; a word longer than a line is broken.</summary>
    internal static List<string> Wrap(string text, FontMetrics metrics, double size, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (metrics.Width(candidate, size) <= width || line.Length == 0 && metrics.Width(word, size) <= width)
                {
                    line.Clear().Append(candidate);
                    continue;
                }
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                // Break an over-long word by characters.
                var piece = new StringBuilder();
                foreach (char c in word)
                {
                    if (piece.Length > 0 && metrics.Width(piece.ToString() + c, size) > width) { lines.Add(piece.ToString()); piece.Clear(); }
                    piece.Append(c);
                }
                line.Append(piece);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static string Escape(string text, FontMetrics metrics)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            byte b = metrics.Encode(c);
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b < 0x20 || b > 0x7E) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static string Colour(double[] c, bool fill) => c.Length switch
    {
        1 => $"{F(c[0])} {(fill ? "g" : "G")}",
        3 => $"{F(c[0])} {F(c[1])} {F(c[2])} {(fill ? "rg" : "RG")}",
        4 => $"{F(c[0])} {F(c[1])} {F(c[2])} {F(c[3])} {(fill ? "k" : "K")}",
        _ => fill ? "1 g" : "0 G",
    };

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));
}
