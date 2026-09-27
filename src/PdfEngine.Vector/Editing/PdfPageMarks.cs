using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Acrobat's Edit PDF page marks: headers and footers, Bates numbers, watermarks and backgrounds,
/// added, updated (replaced) and removed as an incremental update.
/// <para>
/// Each mark is a form XObject drawn in a marked-content sequence of its own, the way Acrobat
/// draws them: <c>/Artifact &lt;&lt;/Subtype /Header /Type /Pagination&gt;&gt; BDC … EMC</c> (Footer,
/// Watermark, Background likewise). Marks behind the page go in a content stream put before the
/// page's own; marks in front go in one after it, with the page's content wrapped in q … Q so
/// nothing it leaves set moves them. The page's own streams are not touched, and as artifacts the
/// marks stay out of a tagged document's structure. Marks are placed on the page as it is seen,
/// turned with its /Rotate and inside its crop box, so they read upright.
/// </para>
/// Remove and Update find marks by their marked content, so Acrobat's are found too; a stream
/// holding one is rewritten without that sequence and is otherwise byte for byte the same.
/// </summary>
public static class PdfPageMarks
{
    /// <summary>The stream dictionary key that marks a content stream this editor added around a page's content.</summary>
    internal const string StreamKey = "PdfViewer_Marks";

    public static PdfContentEditResult Add(PdfVectorDocument document, byte[] original, PdfHeaderFooter design, PdfContentEditOptions? options = null) =>
        Apply(document, original, design.Kind, PdfMarkAction.Add, design, null, options);

    public static PdfContentEditResult Update(PdfVectorDocument document, byte[] original, PdfHeaderFooter design, PdfContentEditOptions? options = null) =>
        Apply(document, original, design.Kind, PdfMarkAction.Update, design, null, options);

    public static PdfContentEditResult Add(PdfVectorDocument document, byte[] original, PdfWatermark design, PdfContentEditOptions? options = null) =>
        Apply(document, original, design.Kind, PdfMarkAction.Add, null, design, options);

    public static PdfContentEditResult Update(PdfVectorDocument document, byte[] original, PdfWatermark design, PdfContentEditOptions? options = null) =>
        Apply(document, original, design.Kind, PdfMarkAction.Update, null, design, options);

    /// <summary>Removes every mark of the kind, on every page.</summary>
    public static PdfContentEditResult Remove(PdfVectorDocument document, byte[] original, PdfMarkKind kind) =>
        Apply(document, original, kind, PdfMarkAction.Remove, null, null, null);

    // ------------------------------------------------------------------ reading

    /// <summary>The marks on the document's pages, and the settings this editor made each kind with.</summary>
    public static PdfPageMarksInfo Read(PdfVectorDocument document)
    {
        var r = document.Resolver;
        var marks = new List<PdfPageMark>();
        var settings = new Dictionary<PdfMarkKind, string>();
        for (int p = 1; p <= document.PageTree.Pages.Count; p++)
        {
            var node = document.PageTree.Pages[p - 1];
            var xobjects = r.Resolve(node.Resources["XObject"]) as PdfDictionary;
            var props = Properties(node, r);
            bool anythingBefore = false;
            foreach (var stream in node.Contents)
            {
                byte[] bytes;
                try { bytes = Decode(document, stream); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                string side = stream.Dictionary.GetName(StreamKey) ?? string.Empty;
                int at = 0;
                foreach (var s in PageMarkScanner.Find(bytes, props))
                {
                    var (kind, json) = Refine(s, xobjects, r);
                    if (!anythingBefore && !OnlyStateBetween(bytes, at, s.Start)) anythingBefore = true;
                    at = s.End;
                    marks.Add(new PdfPageMark(p, kind, side == "Open" || (side.Length == 0 && !anythingBefore), json != null));
                    if (json != null) settings.TryAdd(kind, json);
                }
                if (side.Length == 0 && !OnlyStateBetween(bytes, at, bytes.Length)) anythingBefore = true;
            }
        }
        return new PdfPageMarksInfo
        {
            Marks = marks,
            HeaderFooter = Settings<PdfHeaderFooter>(settings, PdfMarkKind.HeaderFooter),
            Bates = Settings<PdfHeaderFooter>(settings, PdfMarkKind.Bates),
            Watermark = Settings<PdfWatermark>(settings, PdfMarkKind.Watermark),
            Background = Settings<PdfWatermark>(settings, PdfMarkKind.Background),
        };
    }

    private static T? Settings<T>(Dictionary<PdfMarkKind, string> settings, PdfMarkKind kind) where T : class
    {
        if (!settings.TryGetValue(kind, out var json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ writing

    private static PdfContentEditResult Apply(PdfVectorDocument document, byte[] original, PdfMarkKind kind, PdfMarkAction action,
        PdfHeaderFooter? headerFooter, PdfWatermark? watermark, PdfContentEditOptions? options)
    {
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (r.Resolve(trailer["Root"]) is not PdfDictionary root) throw new InvalidOperationException("The document has no catalog.");
        var pageObjects = PdfRedactor.PageObjectNumbers(root, r);
        int count = document.PageTree.Pages.Count;
        if (pageObjects.Count != count) throw new InvalidOperationException("The document's page tree cannot be followed, so its pages cannot be marked.");

        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(document);
        int Allocate() => next++;
        var warnings = new List<string>();
        var embedded = new List<string>();
        var now = headerFooter?.Date ?? DateTime.Now;

        PageMarkForms? forms = null;
        PdfVectorDocument? source = null;
        var targets = new Dictionary<int, int>();
        if (action != PdfMarkAction.Remove)
        {
            string json = headerFooter != null ? JsonSerializer.Serialize(headerFooter) : JsonSerializer.Serialize(watermark);
            forms = new PageMarkForms(r, options?.Fonts ?? SystemFontCatalog.Installed, Allocate, objects, warnings, kind, json, now);
            var range = headerFooter?.Pages ?? watermark!.Pages;
            int i = 0;
            foreach (int p in range.Pages(count)) targets[p] = i++;
            if (targets.Count == 0) warnings.Add("The page range has no pages.");
            if (watermark is { Source: PdfMarkSource.Page })
            {
                if (watermark.SourcePdf is not { Length: > 0 } pdf) throw new ArgumentException("Choose the PDF whose page is drawn.");
                source = PdfVectorDocument.Open(new MemoryByteSource(pdf));
            }
            if (watermark is { Source: PdfMarkSource.Image, Image: null }) throw new ArgumentException("Choose the picture to draw.");
        }

        try
        {
            string fileName = headerFooter?.FileName ?? Path.GetFileName(document.FilePath);
            for (int p = 1; p <= count; p++)
            {
                bool adding = targets.TryGetValue(p, out int index);
                if (!adding && action == PdfMarkAction.Add) continue;
                var marks = new List<(PdfMarkKind Kind, bool Behind, string Content, MarkForm Form)>();
                if (adding)
                {
                    var node = document.PageTree.Pages[p - 1];
                    if (headerFooter != null) marks.AddRange(HeaderFooterMarks(headerFooter, forms!, node, p, count, index, now, fileName));
                    else marks.AddRange(WatermarkMarks(watermark!, forms!, node, source));
                }
                MarkPage(document, p, pageObjects[p - 1], action == PdfMarkAction.Add ? null : kind, marks, objects, Allocate);
            }
            forms?.Finish(embedded);
        }
        finally
        {
            source?.Dispose();
        }
        byte[] bytes = objects.Count == 0 ? original : PdfIncrementalWriter.Append(original, document, objects);
        if (action == PdfMarkAction.Remove && objects.Count == 0) warnings.Add("The document has no marks of that kind.");
        return new PdfContentEditResult { Bytes = bytes, Warnings = warnings.Distinct().ToList(), EmbeddedFonts = embedded.Distinct().ToList() };
    }

    private static IEnumerable<(PdfMarkKind, bool, string, MarkForm)> HeaderFooterMarks(PdfHeaderFooter hf, PageMarkForms forms, PdfPageNode node,
        int page, int count, int index, DateTime now, string fileName)
    {
        var (w, h, toUser) = Display(node);
        string? bates = hf.Bates?.Format(hf.Bates.Start + index);
        foreach (var (template, horizontal, top) in hf.Boxes())
        {
            if (string.IsNullOrWhiteSpace(template)) continue;
            string text = Expand(template, page, count, hf, bates, now, fileName);
            if (forms.Text(text, hf.Format, horizontal, 1) is not { } form) continue;
            var b = form.Box;
            double x = horizontal switch
            {
                PdfMarkHorizontal.Left => hf.LeftMargin - b.X,
                PdfMarkHorizontal.Right => w - hf.RightMargin - b.Right,
                _ => (w - b.Width) / 2 - b.X,
            };
            double y = top ? h - hf.TopMargin - b.Bottom : hf.BottomMargin - b.Y;
            var m = PdfMatrix.CreateTranslation(x, y) * toUser;
            yield return (hf.Kind, false, Sequence(top ? "Header" : "Footer", m, form), form);
        }
    }

    private static IEnumerable<(PdfMarkKind, bool, string, MarkForm)> WatermarkMarks(PdfWatermark wm, PageMarkForms forms, PdfPageNode node, PdfVectorDocument? source)
    {
        var (w, h, toUser) = Display(node);
        string subtype = wm.IsBackground ? "Background" : "Watermark";
        if (wm.Source == PdfMarkSource.Color)
        {
            var fill = forms.Color(wm.Color, w, h, wm.Opacity);
            yield return (wm.Kind, true, Sequence(subtype, toUser, fill), fill);
            yield break;
        }
        var form = wm.Source switch
        {
            PdfMarkSource.Image => forms.Image(wm.Image!, wm.Opacity),
            PdfMarkSource.Page => forms.Page(source!, wm.SourcePage, wm.Opacity),
            _ => forms.Text(wm.Text, wm.Format, PdfMarkHorizontal.Center, wm.Opacity),
        };
        if (form == null) yield break;
        var b = form.Box;
        double angle = wm.RotationDegrees * Math.PI / 180, cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
        double turnedW = b.Width * cos + b.Height * sin, turnedH = b.Width * sin + b.Height * cos;
        double scale = wm.RelativeScale is { } rel && turnedW > 0 && turnedH > 0 ? rel * Math.Min(w / turnedW, h / turnedH) : wm.Scale;
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        turnedW *= scale;
        turnedH *= scale;
        double cx = wm.Horizontal switch
        {
            PdfMarkHorizontal.Left => wm.OffsetX + turnedW / 2,
            PdfMarkHorizontal.Right => w - wm.OffsetX - turnedW / 2,
            _ => w / 2 + wm.OffsetX,
        };
        double cy = wm.Vertical switch
        {
            PdfMarkVertical.Bottom => wm.OffsetY + turnedH / 2,
            PdfMarkVertical.Top => h - wm.OffsetY - turnedH / 2,
            _ => h / 2 + wm.OffsetY,
        };
        var m = PdfMatrix.CreateTranslation(-(b.X + b.Width / 2), -(b.Y + b.Height / 2)) * PdfMatrix.CreateScale(scale, scale)
              * PdfMatrix.CreateRotation(angle) * PdfMatrix.CreateTranslation(cx, cy) * toUser;
        yield return (wm.Kind, wm.DrawnBehind, Sequence(subtype, m, form), form);
    }

    private static string Sequence(string subtype, PdfMatrix m, MarkForm form) =>
        $"/Artifact <</Subtype /{subtype} /Type /Pagination >>BDC\nq {PdfContentEditor.Mat(m)} cm /{FormName(form)} Do Q\nEMC\n";

    private static string FormName(MarkForm form) => "PVMk" + form.Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rewrites a page: the marks of <paramref name="strip"/> taken out (this editor's and others'),
    /// the others kept, <paramref name="added"/> drawn behind or in front of the page's own content.
    /// </summary>
    private static void MarkPage(PdfVectorDocument document, int pageNumber, int pageObject, PdfMarkKind? strip,
        List<(PdfMarkKind Kind, bool Behind, string Content, MarkForm Form)> added, Dictionary<int, PdfObject> objects, Func<int> allocate)
    {
        var r = document.Resolver;
        var node = document.PageTree.Pages[pageNumber - 1];
        var pageDict = r.Resolve(pageObject) as PdfDictionary ?? throw new InvalidOperationException($"Page {pageNumber} is not a dictionary.");
        var xobjects = r.Resolve(node.Resources["XObject"]) as PdfDictionary;
        var props = Properties(node, r);

        var behind = new List<(PdfMarkKind Kind, string Content)>();
        var front = new List<(PdfMarkKind Kind, string Content)>();
        var contents = new List<PdfObject>();
        var removedNames = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new StringBuilder();
        bool hit = false;

        foreach (var (reference, stream) in ContentStreams(pageDict["Contents"], r))
        {
            byte[] bytes;
            try { bytes = Decode(document, stream); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"Page {pageNumber}'s content cannot be read ({ex.Message}), so its marks cannot be changed.", ex);
            }
            var sequences = PageMarkScanner.Find(bytes, props).Select(s => s with { Kind = Refine(s, xobjects, r).Kind }).ToList();
            string? side = stream.Dictionary.GetName(StreamKey);
            if (side is "Open" or "Close")
            {
                // A stream this editor added: only its marks are kept, and it is written again.
                foreach (var s in sequences)
                {
                    string text = Encoding.Latin1.GetString(bytes, s.Start, s.End - s.Start);
                    if (s.Kind == strip) { hit = true; removedNames.UnionWith(s.XObjects); continue; }
                    (side == "Open" ? behind : front).Add((s.Kind, text.TrimEnd() + "\n"));
                    remaining.Append(text);
                }
                continue;
            }
            var cut = sequences.Where(s => s.Kind == strip).ToList();
            if (cut.Count == 0)
            {
                contents.Add(reference);
                remaining.Append(Encoding.Latin1.GetString(bytes)).Append('\n');
                continue;
            }
            hit = true;
            var kept = new MemoryStream();
            int at = 0;
            foreach (var s in cut)
            {
                kept.Write(bytes, at, s.Start - at);
                removedNames.UnionWith(s.XObjects);
                at = s.End;
            }
            kept.Write(bytes, at, bytes.Length - at);
            byte[] rest = kept.ToArray();
            remaining.Append(Encoding.Latin1.GetString(rest)).Append('\n');
            int number = allocate();
            objects[number] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, ContentRedactor.Deflate(rest));
            contents.Add(new PdfIndirectRef(number));
        }
        if (!hit && added.Count == 0) return;

        foreach (var a in added) (a.Behind ? behind : front).Add((a.Kind, a.Content));
        // Backgrounds under everything else behind the page.
        behind = behind.OrderBy(b => b.Kind == PdfMarkKind.Background ? 0 : 1).ToList();
        if (behind.Count > 0 || front.Count > 0)
        {
            string open = string.Concat(behind.Select(b => b.Content)) + (front.Count > 0 ? "q\n" : string.Empty);
            contents.Insert(0, NewContent(open, "Open", objects, allocate));
            if (front.Count > 0) contents.Add(NewContent("Q\n" + string.Concat(front.Select(f => f.Content)), "Close", objects, allocate));
        }

        // The page's XObjects: the new marks' forms added, those of removed marks dropped when nothing else draws them.
        var xo = new Dictionary<string, PdfObject>(xobjects?.Entries ?? new Dictionary<string, PdfObject>());
        string all = remaining.ToString();
        foreach (string name in removedNames)
            if (!UsesName(all, name) && xo.TryGetValue(name, out var obj) && IsMarkForm(r.Resolve(obj) as PdfStream, r))
                xo.Remove(name);
        foreach (var a in added) xo[FormName(a.Form)] = new PdfIndirectRef(a.Form.Number);

        var resources = new Dictionary<string, PdfObject>(node.Resources.Entries);
        if (xo.Count > 0) resources["XObject"] = new PdfDictionary(xo);
        else resources.Remove("XObject");
        var entries = new Dictionary<string, PdfObject>(pageDict.Entries) { ["Resources"] = new PdfDictionary(resources) };
        if (contents.Count == 0) entries.Remove("Contents");
        else entries["Contents"] = contents.Count == 1 ? contents[0] : new PdfArray(contents);
        objects[pageObject] = new PdfDictionary(entries);
    }

    private static PdfIndirectRef NewContent(string content, string side, Dictionary<int, PdfObject> objects, Func<int> allocate)
    {
        int number = allocate();
        objects[number] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
        {
            ["Filter"] = new PdfName("FlateDecode"), [StreamKey] = new PdfName(side),
        }, ContentRedactor.Deflate(Encoding.Latin1.GetBytes(content)));
        return new PdfIndirectRef(number);
    }

    // ------------------------------------------------------------------ pages

    /// <summary>
    /// The page as it is seen: its width and height, and the matrix from that space (origin at
    /// the bottom left of the crop box as displayed, y up) to user space, turned by /Rotate.
    /// </summary>
    internal static (double Width, double Height, PdfMatrix ToUser) Display(PdfPageNode node)
    {
        var c = node.CropBox;
        return node.RotationDegrees switch
        {
            90 => (c.Height, c.Width, new PdfMatrix(0, 1, -1, 0, c.X + c.Width, c.Y)),
            180 => (c.Width, c.Height, new PdfMatrix(-1, 0, 0, -1, c.X + c.Width, c.Y + c.Height)),
            270 => (c.Height, c.Width, new PdfMatrix(0, -1, 1, 0, c.X, c.Y + c.Height)),
            _ => (c.Width, c.Height, new PdfMatrix(1, 0, 0, 1, c.X, c.Y)),
        };
    }

    // The page's content streams with the objects that name them (kept as they are when unchanged).
    private static List<(PdfObject Reference, PdfStream Stream)> ContentStreams(PdfObject? contents, PdfObjectResolver r)
    {
        var list = new List<(PdfObject, PdfStream)>();
        var resolved = r.Resolve(contents);
        if (resolved is PdfStream single) list.Add((contents!, single));
        else if (resolved is PdfArray array)
            foreach (var item in array)
                if (r.Resolve(item) is PdfStream s) list.Add((item, s));
        return list;
    }

    private static byte[] Decode(PdfVectorDocument document, PdfStream stream) =>
        new PdfStreamDecoder(null, document.Resolver.Resolve).DecodeStream(stream);

    private static Func<string, PdfDictionary?> Properties(PdfPageNode node, PdfObjectResolver r)
    {
        var properties = r.Resolve(node.Resources["Properties"]) as PdfDictionary;
        return name => r.Resolve(properties?[name]) as PdfDictionary;
    }

    // The kind a sequence's form says it is (its DocSettings, when this editor wrote it), and those settings.
    private static (PdfMarkKind Kind, string? Json) Refine(MarkSequence s, PdfDictionary? xobjects, PdfObjectResolver r)
    {
        foreach (string name in s.XObjects)
        {
            if (r.Resolve(xobjects?[name]) is not PdfStream form) continue;
            var piece = r.Resolve(r.Resolve(form.Dictionary["PieceInfo"]) is PdfDictionary pi ? pi["ADBE_CompoundType"] : null) as PdfDictionary;
            if (r.Resolve(piece?["DocSettings"]) is not PdfDictionary settings) continue;
            if (settings.GetName(PageMarkForms.KindKey) is { } k && Enum.TryParse<PdfMarkKind>(k, out var kind))
                return (kind, (r.Resolve(settings[PageMarkForms.SettingsKey]) as PdfString)?.AsDecodedString());
        }
        return (s.Kind, null);
    }

    // A form Acrobat's or this editor's mark tools made.
    private static bool IsMarkForm(PdfStream? form, PdfObjectResolver r) =>
        form != null && r.Resolve(form.Dictionary["PieceInfo"]) is PdfDictionary piece && piece.ContainsKey("ADBE_CompoundType");

    // Whether the content between the offsets only saves and restores the graphics state.
    private static bool OnlyStateBetween(byte[] bytes, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            byte b = bytes[i];
            if (!PdfLexer.IsWhitespace(b) && b is not ((byte)'q' or (byte)'Q')) return false;
        }
        return true;
    }

    /// <summary>Whether content names the resource (/Name followed by a delimiter).</summary>
    internal static bool UsesName(string content, string name)
    {
        string token = "/" + PdfObjectWriter.EscapeName(name);
        for (int at = content.IndexOf(token, StringComparison.Ordinal); at >= 0; at = content.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            int end = at + token.Length;
            if (end >= content.Length || PdfLexer.IsWhitespace((byte)content[end]) || PdfLexer.IsDelimiter((byte)content[end])) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ tokens

    private static readonly Regex Token = new(@"<<([^<>]*)>>", RegexOptions.Compiled);
    private static readonly Regex PageToken = new(@"^(page\s+)?1(\s*(/|of)\s*n)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DatePattern = new(@"^[dmy]+([/\-. ][dmy]+)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A header or footer's text with its tokens replaced for a page.</summary>
    internal static string Expand(string template, int page, int count, PdfHeaderFooter hf, string? bates, DateTime date, string fileName)
    {
        string number = (page - 1 + hf.StartPageNumber).ToString(CultureInfo.InvariantCulture);
        string total = (count - 1 + hf.StartPageNumber).ToString(CultureInfo.InvariantCulture);
        return Token.Replace(template, m =>
        {
            string t = m.Groups[1].Value.Trim();
            string lower = t.ToLowerInvariant();
            if (lower == "n") return total;
            if (PageToken.IsMatch(t)) return Regex.Replace(Regex.Replace(t, @"\b1\b", number), @"\bn\b", total);
            if (lower == "bates") return bates ?? string.Empty;
            if (lower == "date") return FormatDate(date, hf.DateFormat);
            if (lower is "file" or "filename" or "file name") return fileName;
            if (DatePattern.IsMatch(t)) return FormatDate(date, Regex.Replace(t.ToLowerInvariant(), "m", "M"));
            return m.Value;
        });
    }

    private static string FormatDate(DateTime date, string format)
    {
        try { return date.ToString(format.Length == 1 ? "%" + format : format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return date.ToString("d", CultureInfo.InvariantCulture); }
    }
}
