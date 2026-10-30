using System.Runtime.CompilerServices;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Redaction;
using PdfEngine.Signatures;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Signatures;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Redaction in the engine-neutral model, on <see cref="PdfRedactor"/>: what lies under each area
/// (glyphs, image pixels, path segments, annotations and form fields) is removed from the content,
/// not covered, and the result is written to a new file. Areas are normalized to the unrotated crop
/// box from its top-left corner, like annotations.
/// </summary>
public sealed class PdfRedactionService : IPdfRedactionService
{
    private readonly ConditionalWeakTable<IPdfDocument, List<RedactionArea>> _pending = new();

    /// <summary>Decodes JPEG samples so that only the pixels under an area are removed (else a JPEG under one goes whole).</summary>
    public Func<PdfEngine.Vector.PdfDecodedImage, byte[]?>? JpegDecoder { get; init; }

    public ValueTask<IReadOnlyList<RedactionArea>> GetPendingRedactionsAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        PdfVectorEngine.Require(document);
        lock (_pending)
            return ValueTask.FromResult<IReadOnlyList<RedactionArea>>(_pending.TryGetValue(document, out var list) ? list.ToList() : Array.Empty<RedactionArea>());
    }

    public ValueTask AddPendingRedactionAsync(IPdfDocument document, RedactionArea redaction, CancellationToken cancellationToken = default)
    {
        PdfVectorEngine.Require(document);
        ArgumentNullException.ThrowIfNull(redaction);
        lock (_pending) _pending.GetOrCreateValue(document).Add(redaction);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ApplyRedactionsAsync(IPdfDocument document, string targetPath, IReadOnlyList<RedactionArea> redactions, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        if (!string.IsNullOrEmpty(doc.FilePath) && string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(doc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException("Target path cannot overwrite currently open document directly.", targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        var result = Apply(doc.Vector, redactions ?? Array.Empty<RedactionArea>(), JpegDecoder);
        await PdfVectorEngine.WriteAsync(targetPath, result.Bytes, cancellationToken).ConfigureAwait(false);
        lock (_pending) _pending.Remove(document);
    }

    /// <summary>The redacted document. Fill colour and overlay text come from the first area that sets them.</summary>
    public static PdfRedactionResult Apply(PdfVectorDocument document, IReadOnlyList<RedactionArea> redactions,
        Func<PdfEngine.Vector.PdfDecodedImage, byte[]?>? jpegDecoder = null)
    {
        var areas = new List<PdfRedactionArea>();
        foreach (var a in redactions)
        {
            if (a.PageNumber < 1 || a.PageNumber > document.PageCount) continue;
            var crop = document.PageTree.Pages[a.PageNumber - 1].CropBox;
            double x = crop.Left + a.Bounds.X * crop.Width;
            double top = crop.Top + (1 - a.Bounds.Y) * crop.Height;
            double h = a.Bounds.Height * crop.Height;
            areas.Add(new PdfRedactionArea(a.PageNumber, new PdfRect(x, top - h, a.Bounds.Width * crop.Width, h)));
        }
        var first = redactions.FirstOrDefault();
        return PdfRedactor.Apply(document, areas, new PdfRedactionOptions
        {
            FillColor = PdfAnnotationService.ParseHex(first?.FillColor) ?? new double[] { 0, 0, 0 },
            OverlayText = redactions.Select(r => r.OverlayText).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            JpegDecoder = jpegDecoder,
        });
    }
}
