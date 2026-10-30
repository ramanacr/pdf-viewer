using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Oracle;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Annotations;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf docservices &lt;dir&gt; [--limit N] [--pages N] [--roundtrip N] [--out report.jsonl]
///
/// The M9 native document services against PDFium on a corpus. For every file both engines open,
/// it compares page count, page sizes and rotation, the outline (structure, titles, target pages),
/// links (rectangles, action types, targets, URIs), annotations (subtype, rectangle, contents)
/// and embedded files (names and sizes), and reports the share of files on which each agrees.
/// Every Nth file also goes through rotate + delete + insert: the result must open in both
/// engines with the expected pages, and every page must render as the source page it came from.
/// Texts are compared by hash and never written to the report; nothing leaves the machine.
/// </summary>
public static class DocServicesSmoke
{
    private const double Tolerance = 0.05;

    private sealed class Tally
    {
        public int Files, Agree;

        /// <summary>Files that disagree only where the document's own checksum shows ours is right.</summary>
        public int Verified;
        public long Items, ItemsAgree;
        public readonly List<string> Examples = new();

        public void Record(bool agree, string file, string? detail, long items = 0, long itemsAgree = 0)
        {
            Files++;
            if (agree) Agree++;
            else if (Examples.Count < 12) Examples.Add(detail == null ? file : $"{file}: {detail}");
            Items += items;
            ItemsAgree += itemsAgree;
        }

        public object Summary() => new Dictionary<string, object>
        {
            ["files"] = Files,
            ["filesAgree"] = Agree,
            ["agreementPercent"] = Files > 0 ? Math.Round(100.0 * Agree / Files, 2) : 100,
            ["disagreeingVerifiedByChecksum"] = Verified,
            ["items"] = Items,
            ["itemsAgreementPercent"] = Items > 0 ? Math.Round(100.0 * ItemsAgree / Items, 3) : 100,
            ["examples"] = Examples,
        };
    }

    private static string H(string? s) => s == null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    private static bool Near(double a, double b) => Math.Abs(a - b) <= Tolerance;

    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        int pageCap = int.TryParse(Program.Option(args, "--pages"), out int pc) ? pc : 200;
        int roundTripEvery = int.TryParse(Program.Option(args, "--roundtrip"), out int rt) ? rt : 10;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var report = outPath != null ? new StreamWriter(outPath) : null;
        using var renderer = Program.CreateRenderer(args);

        var categories = new[] { "open", "pageCount", "pageGeometry", "metadata", "outline", "links", "annotations", "attachments" }
            .ToDictionary(c => c, _ => new Tally());
        var roundTrip = new Tally();
        var roundTripRender = new Tally();
        var roundTripAnnotate = new Tally();
        int skippedPdfium = 0, skippedEncrypted = 0, errors = 0;
        var clock = Stopwatch.StartNew();

        int index = 0;
        foreach (var file in files)
        {
            index++;
            string name = Path.GetRelativePath(dir, file);
            byte[] bytes = await File.ReadAllBytesAsync(file);
            PdfiumOracle oracle;
            try { oracle = new PdfiumOracle(bytes); }
            catch (Exception) { skippedPdfium++; continue; } // PDFium cannot open it (or needs a password): no reference
            using (oracle)
            {
                PdfVectorDocument doc;
                try
                {
                    doc = await PdfVectorDocument.OpenAsync(bytes, name);
                }
                catch (PdfEngine.Vector.Diagnostics.PdfEncryptedDocumentException)
                {
                    skippedEncrypted++;
                    continue;
                }
                catch (Exception ex)
                {
                    categories["open"].Record(false, name, ex.GetType().Name);
                    continue;
                }
                using (doc)
                {
                    categories["open"].Record(true, name, null);
                    var record = new Dictionary<string, object?> { ["file"] = name };
                    try
                    {
                        Compare(doc, oracle, name, pageCap, categories, record);
                        if (roundTripEvery > 0 && index % roundTripEvery == 0 && doc.PageCount > 0)
                        {
                            await RoundTripAsync(bytes, name, oracle, renderer, roundTrip, roundTripRender, record);
                            AnnotateRoundTrip(doc, bytes, name, oracle, roundTripAnnotate, record);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        errors++;
                        record["error"] = ex.GetType().Name;
                    }
                    if (report != null) await report.WriteLineAsync(JsonSerializer.Serialize(record));
                }
            }
            if (index % 200 == 0) Console.Error.WriteLine($"  {index}/{files.Count} files, {clock.Elapsed.TotalSeconds:F0} s");
        }

        var summary = new Dictionary<string, object>
        {
            ["files"] = files.Count,
            ["skippedPdfiumCannotOpen"] = skippedPdfium,
            ["skippedEncrypted"] = skippedEncrypted,
            ["comparisonErrors"] = errors,
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
        };
        foreach (var (key, tally) in categories) summary[key] = tally.Summary();
        summary["roundTripOpenAndPages"] = roundTrip.Summary();
        summary["roundTripRenders"] = roundTripRender.Summary();
        summary["roundTripAnnotations"] = roundTripAnnotate.Summary();
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (Program.Option(args, "--summary") is string summaryPath) await File.WriteAllTextAsync(summaryPath, json);
        return 0;
    }

    private static void Compare(PdfVectorDocument doc, PdfiumOracle oracle, string name, int pageCap,
        Dictionary<string, Tally> categories, Dictionary<string, object?> record)
    {
        int pages = oracle.PageCount;
        bool countAgrees = pages == doc.PageCount;
        categories["pageCount"].Record(countAgrees, name, $"{doc.PageCount} vs {pages}");
        record["pages"] = pages;
        int n = Math.Min(pages, doc.PageCount);

        // Geometry: every page.
        int geometryAgree = 0;
        string? firstGeometry = null;
        for (int i = 0; i < n; i++)
        {
            var g = PdfDocumentInfo.ReadPageGeometry(doc, i + 1);
            var (w, h) = oracle.PageSize(i);
            bool ok = Near(g.DisplaySize.Width, w) && Near(g.DisplaySize.Height, h) && g.Rotation == oracle.Rotation(i);
            if (ok) geometryAgree++;
            else firstGeometry ??= $"p{i + 1} {g.DisplaySize.Width:F2}x{g.DisplaySize.Height:F2}/{g.Rotation} vs {w:F2}x{h:F2}/{oracle.Rotation(i)}";
        }
        categories["pageGeometry"].Record(countAgrees && geometryAgree == n, name, firstGeometry, n, geometryAgree);

        // Metadata: the standard tags.
        var p = PdfDocumentInfo.ReadProperties(doc);
        var tags = new (string Tag, string Ours)[]
        {
            ("Title", p.Title), ("Author", p.Author), ("Subject", p.Subject), ("Keywords", p.Keywords),
            ("Creator", p.Creator), ("Producer", p.Producer), ("CreationDate", p.CreationDateText), ("ModDate", p.ModDateText),
        };
        var badTags = tags.Where(t => oracle.MetaText(t.Tag) != t.Ours).Select(t => t.Tag).ToList();
        categories["metadata"].Record(badTags.Count == 0, name, string.Join(",", badTags), tags.Length, tags.Length - badTags.Count);

        // Outline.
        var ours = Flatten(PdfDocumentInfo.ReadOutline(doc)).Take(5000).ToList();
        var theirs = oracle.Outline().Take(5000).Select(b => (b.Depth, H(b.Title), b.PageIndex)).ToList();
        int outlineAgree = 0;
        string? firstOutline = null;
        for (int i = 0; i < Math.Max(ours.Count, theirs.Count); i++)
        {
            if (i < ours.Count && i < theirs.Count && ours[i] == theirs[i]) outlineAgree++;
            else firstOutline ??= i < ours.Count && i < theirs.Count
                ? $"item {i}: depth {ours[i].Item1}/{theirs[i].Item1} title {(ours[i].Item2 == theirs[i].Item2 ? "same" : "differs")} page {ours[i].Item3}/{theirs[i].Item3}"
                : $"{ours.Count} items vs {theirs.Count}";
        }
        categories["outline"].Record(firstOutline == null, name, firstOutline, Math.Max(ours.Count, theirs.Count), outlineAgree);
        record["outlineItems"] = theirs.Count;

        // Links, annotations: the first pageCap pages.
        int capped = Math.Min(n, pageCap);
        long linkItems = 0, linkAgree = 0, annotItems = 0, annotAgree = 0;
        string? firstLink = null, firstAnnot = null;
        var nav = new PdfNavigation(doc);
        for (int i = 0; i < capped; i++)
        {
            var ol = oracle.Links(i);
            var ml = PdfDocumentInfo.ReadLinks(doc, i + 1, nav);
            linkItems += Math.Max(ol.Count, ml.Count);
            for (int k = 0; k < Math.Min(ol.Count, ml.Count); k++)
            {
                var a = ml[k];
                var b = ol[k];
                int type = a.Action == null ? -1 : a.Action.Kind switch
                {
                    PdfActionKind.GoTo => 1, PdfActionKind.GoToR => 2, PdfActionKind.Uri => 3, PdfActionKind.Launch => 4, PdfActionKind.GoToE => 5, _ => 0,
                };
                // PDFium gives a remote go-to's page number as if it were a page here; that is not compared.
                int ourPage = a.TargetPageIndex;
                bool ok = Near(a.Rect.Left, b.L) && Near(a.Rect.Top, b.B) && Near(a.Rect.Right, b.R) && Near(a.Rect.Bottom, b.T)
                    && (type == b.ActionType || (type == 5 && b.ActionType is 0 or 5))
                    && (type is 2 or 5 || ourPage == b.PageIndex)
                    && H(a.Action?.Uri) == H(b.Uri);
                if (ok) linkAgree++;
                else firstLink ??= $"p{i + 1} link {k}: type {type}/{b.ActionType} page {ourPage}/{b.PageIndex} rect {(Near(a.Rect.Left, b.L) && Near(a.Rect.Top, b.B) && Near(a.Rect.Right, b.R) && Near(a.Rect.Bottom, b.T) ? "same" : "differs")} uri {(H(a.Action?.Uri) == H(b.Uri) ? "same" : "differs")}";
            }
            if (ol.Count != ml.Count) firstLink ??= $"p{i + 1}: {ml.Count} links vs {ol.Count}";

            var oa = oracle.Annots(i);
            var ma = PdfAnnotationReader.Read(doc, i + 1);
            annotItems += Math.Max(oa.Count, ma.Count);
            for (int k = 0; k < Math.Min(oa.Count, ma.Count); k++)
            {
                var a = ma[k];
                var b = oa[k];
                string subtype = PdfiumOracle.AnnotSubtypes.Contains(a.Subtype) ? a.Subtype : "Unknown";
                bool rect = Near(a.Rect.Left, b.L) && Near(a.Rect.Top, b.B) && Near(a.Rect.Right, b.R) && Near(a.Rect.Bottom, b.T);
                bool ok = subtype == b.Subtype && rect && H(a.Contents ?? string.Empty) == H(b.Contents);
                if (ok) annotAgree++;
                else firstAnnot ??= $"p{i + 1} annot {k}: {subtype}/{b.Subtype} rect {(rect ? "same" : "differs")} contents {(H(a.Contents ?? string.Empty) == H(b.Contents) ? "same" : "differs")}";
            }
            if (oa.Count != ma.Count) firstAnnot ??= $"p{i + 1}: {ma.Count} annotations vs {oa.Count}";
        }
        categories["links"].Record(firstLink == null, name, firstLink, linkItems, linkAgree);
        categories["annotations"].Record(firstAnnot == null, name, firstAnnot, annotItems, annotAgree);
        record["links"] = linkItems;
        record["annotations"] = annotItems;

        // Embedded files of the name tree (PDFium does not list file attachment annotations).
        var mf = PdfAttachments.List(doc, includeAnnotations: false);
        var of = oracle.Attachments();
        int fileAgree = 0, verified = 0;
        for (int k = 0; k < Math.Min(mf.Count, of.Count); k++)
        {
            if (H(mf[k].Name) == H(of[k].Name) && mf[k].Size == of[k].Size) { fileAgree++; continue; }
            // Where the two differ, the file's own /CheckSum (MD5 of the decoded data, 7.11.4) decides.
            if (H(mf[k].Name) == H(of[k].Name) && ChecksumMatches(doc, k)) verified++;
        }
        bool filesOk = mf.Count == of.Count && fileAgree == mf.Count;
        categories["attachments"].Record(filesOk, name, filesOk ? null : $"{mf.Count} files vs {of.Count}, {fileAgree} agree, {verified} differ where the file's checksum confirms ours", Math.Max(mf.Count, of.Count), fileAgree);
        if (!filesOk && mf.Count == of.Count && fileAgree + verified == mf.Count) categories["attachments"].Verified++;
    }

    private static bool ChecksumMatches(PdfVectorDocument doc, int index)
    {
        try
        {
            var spec = PdfAttachments.EmbeddedFileSpec(doc, index);
            var stream = PdfAttachments.FileStream(spec, doc.Resolver);
            if (stream == null || doc.Resolver.Resolve(stream.Dictionary["Params"]) is not PdfEngine.Vector.Objects.PdfDictionary p
                || doc.Resolver.Resolve(p["CheckSum"]) is not PdfEngine.Vector.Objects.PdfString sum) return false;
            return MD5.HashData(PdfAttachments.Extract(doc, index)).AsSpan().SequenceEqual(sum.RawBytes.Span);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static IEnumerable<(int, string, int)> Flatten(IReadOnlyList<PdfOutlineItem> items, int depth = 0)
    {
        foreach (var i in items)
        {
            yield return (depth, H(i.Title), i.PageIndex);
            foreach (var c in Flatten(i.Children, depth + 1)) yield return c;
        }
    }

    /// <summary>
    /// Rotate page 1 by 90 and delete the last page (incremental updates in place), then insert a
    /// copy of the source's page 1 in front (a new document assembled from both). Expected: out[0]
    /// is source page 1, out[1] source page 1 turned 90, out[2..] source pages 2 .. n-1.
    /// </summary>
    private static async Task RoundTripAsync(byte[] source, string name, PdfiumOracle sourceOracle, IPdfVectorRenderer renderer,
        Tally open, Tally renders, Dictionary<string, object?> record)
    {
        int n = sourceOracle.PageCount;
        await using var native = await PdfEditableDocument.OpenAsync(source, name);
        await using var original = await PdfEditableDocument.OpenAsync(source, name);
        var organizer = new PdfPageOrganizerService();
        int rotation = (int)(await native.GetPageInfoAsync(1)).RotationDegrees;
        await organizer.RotatePageAsync(native, 1, (PdfEngine.Rendering.PageRotation)((rotation + 90) % 360));
        if (n > 1) await organizer.DeletePageAsync(native, n);
        await organizer.InsertPagesAsync(native, 0, original, new[] { 1 });

        var expected = new List<(int Source, int Turn)> { (1, 0), (1, 90) };
        for (int p = 2; p <= n - 1; p++) expected.Add((p, 0));
        if (n == 1) expected = new List<(int, int)> { (1, 0), (1, 90) };

        using var outOracle = new PdfiumOracle(native.Bytes);
        using var reopened = await PdfVectorDocument.OpenAsync(native.Bytes, name);
        bool pagesOk = outOracle.PageCount == expected.Count && reopened.PageCount == expected.Count;
        for (int i = 0; pagesOk && i < expected.Count; i++)
        {
            var (sw, sh) = sourceOracle.PageSize(expected[i].Source - 1);
            var (ow, oh) = outOracle.PageSize(i);
            bool turned = expected[i].Turn == 90;
            pagesOk &= Near(turned ? sh : sw, ow) && Near(turned ? sw : sh, oh)
                && outOracle.Rotation(i) == (sourceOracle.Rotation(expected[i].Source - 1) + expected[i].Turn) % 360;
            var g = PdfDocumentInfo.ReadPageGeometry(reopened, i + 1);
            pagesOk &= Near(g.DisplaySize.Width, ow) && Near(g.DisplaySize.Height, oh) && g.Rotation == outOracle.Rotation(i);
        }
        open.Record(pagesOk, name, pagesOk ? null : $"{outOracle.PageCount}/{reopened.PageCount} pages, expected {expected.Count}");
        record["roundTrip"] = pagesOk;
        if (!pagesOk) return;

        // Renders: up to three pages, at 36 dpi, PDFium against PDFium and the vector engine against itself.
        double worst = 0;
        using var sourceVector = await PdfVectorDocument.OpenAsync(source, name);
        foreach (int i in new[] { 0, 1, expected.Count - 1 }.Distinct())
        {
            var (src, turn) = expected[i];
            // PDFium turns the source page itself: the same device transform as the page's new /Rotate.
            var pdfiumSource = sourceOracle.Render(src - 1, 36, turn / 90);
            double pdfiumDiff = PdfiumOracle.MeanDifference(pdfiumSource, outOracle.Render(i, 36));
            record[$"diffPdfium{i}"] = Math.Round(pdfiumDiff, 3);
            worst = Math.Max(worst, pdfiumDiff);
            try
            {
                var vs = await VectorRender(sourceVector, src, renderer);
                var vo = await VectorRender(reopened, i + 1, renderer);
                if (turn == 90) vs = Turn(vs);
                double vectorDiff = PdfiumOracle.MeanDifference(vs, vo);
                record[$"diffVector{i}"] = Math.Round(vectorDiff, 3);
                worst = Math.Max(worst, vectorDiff);
            }
            catch (Exception ex) when (ex is PdfEngine.Vector.Diagnostics.PdfVectorException)
            {
                // A page the vector engine refuses is refused the same way in both documents; PDFium's comparison stands.
            }
        }
        renders.Record(worst <= 1.0, name, $"mean diff {worst:F2}/255");
        record["roundTripWorstDiff"] = Math.Round(worst, 3);
    }

    /// <summary>
    /// Adds a highlight and a square to page 1 as an incremental update: PDFium must list both,
    /// with their rectangles and contents, after the page's own annotations; then flattening page 1
    /// must leave neither of them as an annotation.
    /// </summary>
    private static void AnnotateRoundTrip(PdfVectorDocument doc, byte[] source, string name, PdfiumOracle sourceOracle, Tally tally, Dictionary<string, object?> record)
    {
        var crop = doc.PageTree.Pages[0].CropBox;
        var added = new[]
        {
            new PdfAnnotation { PageNumber = 1, Subtype = "Highlight", Rect = new PdfEngine.Geometry.PdfRect(crop.X + crop.Width * 0.1, crop.Y + crop.Height * 0.8, crop.Width * 0.3, crop.Height * 0.03),
                Color = new[] { 1.0, 1, 0 }, Opacity = 0.5, Contents = "m9 highlight", Author = "m9" },
            new PdfAnnotation { PageNumber = 1, Subtype = "Square", Rect = new PdfEngine.Geometry.PdfRect(crop.X + crop.Width * 0.5, crop.Y + crop.Height * 0.5, crop.Width * 0.2, crop.Height * 0.1),
                Color = new[] { 0.0, 0, 1 }, BorderWidth = 2, Contents = "m9 square", Author = "m9" },
        };
        added[0].QuadPoints.Add(new[] { added[0].Rect.Left, added[0].Rect.Bottom, added[0].Rect.Right, added[0].Rect.Bottom, added[0].Rect.Left, added[0].Rect.Top, added[0].Rect.Right, added[0].Rect.Top });
        int before = sourceOracle.Annots(0).Count;
        var bytes = PdfAnnotationEditor.Apply(doc, source, e => { foreach (var a in added) e.Add(a); });
        using var oracle = new PdfiumOracle(bytes);
        var list = oracle.Annots(0);
        bool ok = list.Count == before + 2;
        for (int k = 0; ok && k < 2; k++)
        {
            var got = list[before + k];
            var want = added[k];
            ok = got.Subtype == want.Subtype && got.Contents == want.Contents
                && Near(got.L, want.Rect.Left) && Near(got.B, want.Rect.Top) && Near(got.R, want.Rect.Right) && Near(got.T, want.Rect.Bottom);
        }
        using var after = PdfVectorDocument.OpenAsync(bytes, name).AsTask().GetAwaiter().GetResult();
        ok &= PdfAnnotationReader.Read(after, 1).Count == before + 2;
        var flat = PdfAnnotationEditor.Apply(after, bytes, e => e.FlattenPage(1));
        using var flatOracle = new PdfiumOracle(flat);
        ok &= !flatOracle.Annots(0).Any(a => a.Contents.StartsWith("m9 ", StringComparison.Ordinal)) && flatOracle.Annots(0).Count <= before;
        tally.Record(ok, name, ok ? null : $"{list.Count} annotations after adding 2 to {before}");
        record["roundTripAnnotations"] = ok;
    }

    private static async Task<(int Width, int Height, byte[] Bgra)> VectorRender(PdfVectorDocument doc, int page, IPdfVectorRenderer renderer)
    {
        var list = await doc.GetPageDisplayListAsync(page);
        using var r = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = page, Dpi = 36 });
        var pixels = new byte[r.WidthPixels * r.HeightPixels * 4];
        for (int y = 0; y < r.HeightPixels; y++) r.Pixels.Span.Slice(y * r.Stride, r.WidthPixels * 4).CopyTo(pixels.AsSpan(y * r.WidthPixels * 4));
        return (r.WidthPixels, r.HeightPixels, pixels);
    }

    /// <summary>The image turned a quarter clockwise, as /Rotate 90 shows a page.</summary>
    private static (int Width, int Height, byte[] Bgra) Turn((int Width, int Height, byte[] Bgra) img)
    {
        int w = img.Height, h = img.Width;
        var outPixels = new byte[w * h * 4];
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int nx = img.Height - 1 - y, ny = x;
                Buffer.BlockCopy(img.Bgra, (y * img.Width + x) * 4, outPixels, (ny * w + nx) * 4, 4);
            }
        return (w, h, outPixels);
    }
}
