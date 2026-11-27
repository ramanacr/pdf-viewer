using System.Runtime.InteropServices;
using System.Text;
using Android.App;
using Android.Widget;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Optimize;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Skia;
using SkiaSharp;

namespace PdfViewer.Android;

/// <summary>
/// PDF/A (check against a part and level, convert a copy to PDF/A-1b, -2b or -3b) and Reduce file
/// size (the engine's optimizer, with image presets). Both write a new PDF; the open document is
/// not changed. The engine needs two things from the platform here, both made with Skia: a JPEG
/// codec for downsampled images, and a page rasterizer for flattening transparency in PDF/A-1b.
/// </summary>
public sealed partial class MainActivity
{
    private static readonly PdfAFlavor[] CheckFlavours = [PdfAFlavor.A1b, PdfAFlavor.A2b, PdfAFlavor.A2u, PdfAFlavor.A3b, PdfAFlavor.A3u];

    /// <summary>
    /// PDF/A: checks the document against the level it claims (PDF/A-2b when it claims none) and
    /// lists every rule it breaks, by ISO 19005 clause, with how often. From the report the reader
    /// can check another level or convert a copy.
    /// </summary>
    private void PdfACheckAndConvert()
    {
        if (!NeedDocument()) return;
        CheckPdfA(null);
    }

    private async void CheckPdfA(PdfAFlavor? flavour)
    {
        var doc = _doc!;
        byte[] bytes = doc.Bytes;
        var checkedReport = await RunBusyAsync("Checking PDF/A…", token =>
        {
            using var engine = OpenForTool(bytes);
            token.ThrowIfCancellationRequested();
            var claimed = PdfAValidator.ClaimedFlavor(engine);
            return (Claimed: claimed, Report: PdfAValidator.Validate(engine, bytes, flavour ?? claimed ?? PdfAFlavor.A2b));
        });
        if (checkedReport.Report == null || !ReferenceEquals(doc, _doc)) return;
        var (claimedFlavour, report) = checkedReport;

        var text = new StringBuilder();
        text.Append(claimedFlavour == null ? "The document does not claim PDF/A conformance." : $"The document claims {claimedFlavour} conformance.").Append("\n\n");
        if (report.IsCompliant)
        {
            text.Append($"It conforms to {report.Flavor}: no rule is broken.");
        }
        else
        {
            var rules = report.Summary.ToList();
            text.Append($"It does not conform to {report.Flavor}: {rules.Count} rule(s) broken.\n");
            foreach (var (issue, count) in rules)
            {
                text.Append("\n• ").Append(issue.RuleId).Append(": ").Append(issue.Message);
                if (issue.Location != null) text.Append(" (").Append(issue.Location).Append(')');
                if (count > 1) text.Append($" — {count} times");
            }
        }

        var body = new TextView(this) { Text = text.ToString() };
        body.SetTextIsSelectable(true);
        body.SetTextColor(TextColor);
        body.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        body.SetPadding((int)Dp(24), (int)Dp(12), (int)Dp(24), (int)Dp(12));
        var scroll = new ScrollView(this);
        scroll.AddView(body);
        new AlertDialog.Builder(this)
            .SetTitle($"PDF/A check: {report.Flavor}")!
            .SetView(scroll)!
            .SetPositiveButton("Convert a copy…", (_, _) => ChoosePdfAConversion(report.Flavor))!
            .SetNeutralButton("Other level…", (_, _) =>
            {
                string[] names = CheckFlavours.Select(f => f.ToString()).ToArray();
                new AlertDialog.Builder(this).SetTitle("Check against")!
                    .SetItems(names, (_, e) =>
                    {
                        if (ReferenceEquals(doc, _doc)) CheckPdfA(CheckFlavours[e.Which]);
                    })!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show();
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    /// <summary>Which PDF/A level the copy is made for: 1b, 2b (the default, as on Windows) or 3b.</summary>
    private void ChoosePdfAConversion(PdfAFlavor checkedFlavour)
    {
        var doc = _doc;
        if (doc == null) return;
        if (doc.IsEncrypted && !doc.CanCopy)
        {
            // PDF/A files cannot be encrypted: the copy would carry the content without the document's restrictions.
            Toast.MakeText(this, "This document does not allow copying its content, so an unencrypted PDF/A copy is not made.", ToastLength.Long)?.Show();
            return;
        }
        PdfAFlavor[] targets = [PdfAFlavor.A1b, PdfAFlavor.A2b, PdfAFlavor.A3b];
        string[] names =
        [
            "PDF/A-1b: the oldest, most widely accepted (transparency is flattened)",
            "PDF/A-2b: recommended for most archives",
            "PDF/A-3b: as 2b, and allows embedded files",
        ];
        int selected = checkedFlavour.Part switch { 1 => 0, 3 => 2, _ => 1 };
        new AlertDialog.Builder(this)
            .SetTitle("Convert a copy to")!
            .SetSingleChoiceItems(names, selected, (_, e) => selected = e.Which)!
            .SetPositiveButton("Convert", (_, _) =>
            {
                if (ReferenceEquals(doc, _doc)) ConvertToPdfA(doc, targets[selected]);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    /// <summary>Converts a copy and saves it; then says what was changed and what could not be fixed.</summary>
    private void ConvertToPdfA(PdfViewer.Web.Engine.ViewerDocument doc, PdfAFlavor flavour)
    {
        byte[] bytes = doc.Bytes;
        string summary = "";
        SaveNewFile("application/pdf", $"{BaseName}_PDFA.pdf", $"Converting to {flavour}…", token =>
        {
            using var engine = OpenForTool(bytes);
            using var rasterizer = flavour.Part == 1 ? new SkiaPageRasterizer() : null;
            token.ThrowIfCancellationRequested();
            var result = PdfAConverter.Convert(engine, bytes, new PdfAConversionOptions { Flavor = flavour, Rasterizer = rasterizer });
            token.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            if (result.Report.IsCompliant)
            {
                text.Append($"The copy passes the {flavour} check.");
            }
            else
            {
                var rules = result.Report.Summary.ToList();
                text.Append($"The copy was saved, but {rules.Count} rule(s) could not be fixed automatically:");
                foreach (var (issue, count) in rules.Take(20))
                    text.Append("\n• ").Append(issue.RuleId).Append(": ").Append(issue.Message).Append(count > 1 ? $" ({count} times)" : "");
                if (rules.Count > 20) text.Append($"\n…and {rules.Count - 20} more.");
            }
            if (result.Flattening is { } flattening && (flattening.Pages.Count > 0 || flattening.Annotations.Count > 0))
                text.Append("\n\n").Append(flattening.Summary).Append('.');
            if (result.Changes.Count > 0)
            {
                text.Append("\n\nChanged:");
                foreach (var change in result.Changes.Take(20)) text.Append("\n• ").Append(change);
            }
            summary = text.ToString();
            return result.Bytes;
        }, target => OfferOpen(target, summary));
    }

    // ------------------------------------------------------------------ reduce file size

    private sealed record SizePreset(string Title, double? Resolution, int JpegQuality);

    private static readonly SizePreset[] SizePresets =
    [
        new("Keep pictures as they are: remove unused data only", null, 80),
        new("High quality: pictures at 300 ppi", 300, 90),
        new("Standard: pictures at 150 ppi", 150, 80),
        new("Smallest: pictures at 96 ppi", 96, 70),
    ];

    /// <summary>
    /// Reduce file size: the reader picks how far pictures may be reduced; the optimizer then
    /// recompresses streams, merges duplicates, drops thumbnails and private data, packs objects and
    /// downsamples pictures shown above the chosen resolution. The sizes before and after are shown
    /// before the copy is saved; a copy that would not be smaller is not offered.
    /// </summary>
    private void ReduceFileSize()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        int selected = 2;
        new AlertDialog.Builder(this)
            .SetTitle($"Reduce file size ({FormatBytes(doc.Bytes.LongLength)} now)")!
            .SetSingleChoiceItems(SizePresets.Select(p => p.Title).ToArray(), selected, (_, e) => selected = e.Which)!
            .SetPositiveButton("Reduce", (_, _) =>
            {
                if (ReferenceEquals(doc, _doc)) RunReduce(doc, SizePresets[selected], null);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private async void RunReduce(PdfViewer.Web.Engine.ViewerDocument doc, SizePreset preset, string? ownerPassword)
    {
        byte[] bytes = doc.Bytes;
        var outcome = await RunBusyAsync("Reducing the file size…", token =>
        {
            using var engine = OpenForTool(bytes);
            token.ThrowIfCancellationRequested();
            try
            {
                return (Result: PdfOptimizer.Optimize(engine, bytes, new PdfOptimizeOptions
                {
                    ImageResolution = preset.Resolution,
                    JpegQuality = preset.JpegQuality,
                    JpegCodec = preset.Resolution != null ? new SkiaJpegCodec() : null,
                    OwnerPassword = ownerPassword,
                }), NeedsOwner: false);
            }
            catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
            {
                return (Result: (PdfOptimizeResult?)null, NeedsOwner: true);
            }
        });
        if (!ReferenceEquals(doc, _doc)) return;
        if (outcome.NeedsOwner)
        {
            // The permissions forbid changes: the permissions (owner) password allows them. Kept in memory only.
            AskLine("Permissions password", ownerPassword == null
                    ? "This document's permissions do not allow changing it. Type its permissions password to make a smaller copy."
                    : "That password does not allow changes. Try again.",
                "", "Permissions password", global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword, "Continue",
                password =>
                {
                    if (password.Length > 0 && ReferenceEquals(doc, _doc)) RunReduce(doc, preset, password);
                });
            return;
        }
        if (outcome.Result is not { } result) return;
        if (result.Bytes.LongLength >= bytes.LongLength)
        {
            new AlertDialog.Builder(this).SetTitle("Already compact")!
                .SetMessage($"The document is already compact ({FormatBytes(bytes.LongLength)}): a copy would not be smaller, so nothing was saved." +
                            (preset.Resolution == null ? " A preset that reduces pictures may help." : ""))!
                .SetPositiveButton("OK", (_, _) => { })!.Show();
            return;
        }
        double percent = 100.0 * (1 - (double)result.Bytes.LongLength / bytes.LongLength);
        var details = new List<string>();
        if (result.ImagesDownsampled > 0) details.Add($"{result.ImagesDownsampled} picture(s) reduced");
        if (result.DuplicatesMerged > 0) details.Add($"{result.DuplicatesMerged} duplicate object(s) merged");
        if (result.StreamsRecompressed > 0) details.Add($"{result.StreamsRecompressed} stream(s) recompressed");
        string message = $"{FormatBytes(bytes.LongLength)} → {FormatBytes(result.Bytes.LongLength)}, {percent:F0}% smaller" +
                         (details.Count > 0 ? $" ({string.Join(", ", details)})." : ".") +
                         (result.Encrypted ? "\n\nThe copy is encrypted as the original is, with the same passwords and permissions." : "") +
                         (result.Notes.Count > 0 ? "\n\n" + string.Join(" ", result.Notes) : "") +
                         (result.SignaturesInvalidated ? "\n\nThis document is signed. The copy is a new file, so its signatures will not be valid in it." : "");
        byte[] smaller = result.Bytes;
        new AlertDialog.Builder(this)
            .SetTitle("Smaller copy ready")!
            .SetMessage(message)!
            .SetPositiveButton("Save copy", (_, _) =>
                SaveNewPdf($"{BaseName}_reduced.pdf", "Saving…", $"The smaller copy ({FormatBytes(smaller.LongLength)}) was saved.", _ => smaller))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ platform pieces for the engine

    /// <summary>JPEG decoding and encoding for downsampled pictures, with Skia (as WpfJpegCodec on Windows).</summary>
    private sealed class SkiaJpegCodec : IPdfJpegCodec
    {
        public (byte[] Samples, int Width, int Height, int Components)? Decode(byte[] jpeg)
        {
            using var data = SKData.CreateCopy(jpeg);
            using var codec = SKCodec.Create(data);
            if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg) return null;
            // CMYK JPEGs (often Adobe-inverted) are left as they are, as on Windows.
            if (JpegForPdf.Read(jpeg) is not { Components: 1 or 3 }) return null;
            int w = codec.Info.Width, h = codec.Info.Height;
            if (w <= 0 || h <= 0 || (long)w * h > 100_000_000) return null;
            bool gray = codec.Info.ColorType == SKColorType.Gray8;
            if (gray)
            {
                var samples = new byte[w * h];
                var handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
                try
                {
                    var result = codec.GetPixels(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque), handle.AddrOfPinnedObject());
                    return result is SKCodecResult.Success ? (samples, w, h, 1) : null;
                }
                finally
                {
                    handle.Free();
                }
            }
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) is not SKCodecResult.Success) return null;
            var rgba = bitmap.GetPixelSpan();
            int stride = bitmap.RowBytes;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                var row = rgba.Slice(y * stride, w * 4);
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    rgb[o++] = row[x * 4];
                    rgb[o++] = row[x * 4 + 1];
                    rgb[o++] = row[x * 4 + 2];
                }
            }
            return (rgb, w, h, 3);
        }

        public byte[]? Encode(byte[] samples, int width, int height, int components, int quality)
        {
            if (components is not (1 or 3) || samples.Length < width * height * components) return null;
            byte[] pixels = samples;
            var info = new SKImageInfo(width, height, components == 1 ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
            if (components == 3)
            {
                pixels = new byte[width * height * 4];
                for (int i = 0, o = 0; i < width * height * 3; i += 3)
                {
                    pixels[o++] = samples[i];
                    pixels[o++] = samples[i + 1];
                    pixels[o++] = samples[i + 2];
                    pixels[o++] = 255;
                }
            }
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
                using var encoded = pixmap.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
                return encoded?.ToArray();
            }
            finally
            {
                handle.Free();
            }
        }
    }

    /// <summary>
    /// Draws pages for PDF/A-1b transparency flattening with the engine's Skia renderer, as the
    /// Windows app does with its Direct2D renderer: the page as the engine draws it on screen (best
    /// effort), over white, without annotations and form fields, only the window asked for.
    /// </summary>
    private sealed class SkiaPageRasterizer : IPdfPageRasterizer, IDisposable
    {
        private readonly SkiaVectorRenderer _renderer = new(ExportRendererCacheBytes);

        public PdfRasterImage? Render(PdfVectorDocument document, byte[] bytes, int pageNumber, int pageWidthPixels, int pageHeightPixels, PdfPixelWindow window)
        {
            return Task.Run(async () =>
            {
                var list = await document.GetPageDisplayListAsync(pageNumber).ConfigureAwait(false);
                var crop = list.CropBox.IsEmpty ? new PdfEngine.Geometry.PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
                var request = new RenderRequest
                {
                    PageNumber = pageNumber,
                    Dpi = crop.Width > 0 ? pageWidthPixels * 72.0 / crop.Width : 72,
                    TargetWidthPixels = pageWidthPixels,
                    TargetHeightPixels = pageHeightPixels,
                    RenderAnnotations = false,
                    RenderForms = false,
                };
                var result = await _renderer.RenderAsync(list, request, null, new SkiaPixelRegion(window.X, window.Y, window.Width, window.Height),
                    invertColors: false, CancellationToken.None).ConfigureAwait(false);
                using var page = result.Page;
                if (page.WidthPixels != window.Width || page.HeightPixels != window.Height) return null;
                var bgra = new byte[window.Width * window.Height * 4];
                var source = page.Pixels.Span;
                for (int row = 0; row < window.Height; row++)
                    source.Slice(row * page.Stride, window.Width * 4).CopyTo(bgra.AsSpan(row * window.Width * 4));
                return new PdfRasterImage(window.Width, window.Height, bgra, "vector engine (Skia)");
            }).GetAwaiter().GetResult();
        }

        public void Dispose() => _renderer.Dispose();
    }
}
