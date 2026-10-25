using System;
using System.Collections.Generic;
using PdfEngine.Geometry;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Document;

/// <summary>
/// Resolved representation of a single PDF page and its inherited resources/boxes.
/// </summary>
public sealed class PdfPageNode
{
    public int PageNumber { get; }
    public PdfDictionary Dictionary { get; }
    public PdfRect MediaBox { get; }
    public PdfRect CropBox { get; }
    public int RotationDegrees { get; }
    public PdfDictionary Resources { get; }

    /// <summary>The page's content streams (read when first asked for, for a document opened by ranges).</summary>
    public IReadOnlyList<PdfStream> Contents
    {
        get
        {
            if (_contents != null) return _contents;
            lock (_sync)
            {
                // Not Lazy<T>: a failure (bytes not loaded yet) must not be remembered.
                return _contents ??= _resolveContents!() ?? Array.Empty<PdfStream>();
            }
        }
    }

    private IReadOnlyList<PdfStream>? _contents;
    private readonly Func<IReadOnlyList<PdfStream>>? _resolveContents;
    private readonly object _sync = new();

    public PdfSize PageSize =>
        (RotationDegrees == 90 || RotationDegrees == 270)
            ? new PdfSize(CropBox.Height, CropBox.Width)
            : new PdfSize(CropBox.Width, CropBox.Height);

    public PdfPageNode(
        int pageNumber,
        PdfDictionary dictionary,
        PdfRect mediaBox,
        PdfRect cropBox,
        int rotationDegrees,
        PdfDictionary? resources,
        IReadOnlyList<PdfStream> contents)
    {
        PageNumber = pageNumber;
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        MediaBox = mediaBox.IsEmpty ? new PdfRect(0, 0, 612, 792) : mediaBox; // Default 8.5 x 11 in points
        CropBox = cropBox.IsEmpty ? MediaBox : cropBox;
        RotationDegrees = ((rotationDegrees % 360) + 360) % 360;
        Resources = resources ?? new PdfDictionary(new Dictionary<string, PdfObject>());
        _contents = contents ?? Array.Empty<PdfStream>();
    }

    /// <summary>A page whose content streams are resolved when <see cref="Contents"/> is first read.</summary>
    internal PdfPageNode(
        int pageNumber,
        PdfDictionary dictionary,
        PdfRect mediaBox,
        PdfRect cropBox,
        int rotationDegrees,
        PdfDictionary? resources,
        Func<IReadOnlyList<PdfStream>> contents)
        : this(pageNumber, dictionary, mediaBox, cropBox, rotationDegrees, resources, Array.Empty<PdfStream>())
    {
        _contents = null;
        _resolveContents = contents ?? throw new ArgumentNullException(nameof(contents));
    }
}
