using PdfEngine.Vector.Editing;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>
/// Where the Skia backend finds substitutes for fonts a document does not embed. The default reads
/// the installed fonts (<see cref="SystemFontCatalog.Installed"/>, the catalog the Direct2D backend,
/// editing and PDF/A choose from), then asks Skia's font manager by family name. A host without
/// installed fonts, such as a browser, passes a catalog of the fonts it bundles
/// (<see cref="SystemFontCatalog.ForDirectory"/>) or overrides the two loaders. On Android the
/// installed catalog is <c>/system/fonts</c> with Android's stand-ins for the Windows families
/// (<see cref="SystemFontCatalog.ForAndroid"/>), so the default works there as it is.
/// </summary>
public class SkiaFontSource
{
    public SkiaFontSource(SystemFontCatalog? catalog = null)
    {
        _catalog = catalog;
    }

    private SystemFontCatalog? _catalog;

    /// <summary>Faces matched by name, design and class (<see cref="SystemFontCatalog.Match"/>); read on first use.</summary>
    public SystemFontCatalog Catalog => _catalog ??= SystemFontCatalog.Installed;

    /// <summary>Loads a face of <see cref="Catalog"/>; null when it cannot be read.</summary>
    public virtual SKTypeface? Load(SystemFontFace face) => SKTypeface.FromFile(face.Path, face.Index);

    /// <summary>
    /// A face of the named family nearest the style asked for, from Skia's font manager; else the face
    /// that stands in for the family by the catalog's aliases (Android: Roboto for Arial); null when
    /// neither has it.
    /// </summary>
    public virtual SKTypeface? MatchFamily(string family, bool bold, bool italic)
    {
        var typeface = SKFontManager.Default.MatchFamily(family, new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright));
        // The font manager answers with its nearest family, not null, on some platforms: keep only the family asked for.
        if (typeface != null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            return typeface;
        typeface?.Dispose();
        return Catalog.FindAliasedFamily(family, bold ? 700 : 400, italic) is { } standIn ? Load(standIn) : null;
    }
}

/// <summary>A typeface plus the style Skia must simulate because the face lacks it (as DirectWrite's font simulations do).</summary>
internal sealed record SkiaFace(SKTypeface Typeface, bool Embolden, bool Oblique)
{
    /// <summary>Glyph count, read once (0 when the face has no glyphs).</summary>
    public int GlyphCount { get; } = Typeface.GlyphCount;

    /// <summary>A font of this face at <paramref name="size"/>, set up like the Direct2D backend's text (outlines, no hinting).</summary>
    public SKFont CreateFont(float size) => new(Typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        Subpixel = true,
        LinearMetrics = true,
        Embolden = Embolden,
        SkewX = Oblique ? SkiaResources.ObliqueSkew : 0f,
    };
}

/// <summary>Font faces and decoded images shared by every replay of one renderer.</summary>
internal sealed class SkiaResources : IDisposable
{
    /// <summary>Horizontal skew of a simulated oblique (Skia's own fake italic is −1/4).</summary>
    internal const float ObliqueSkew = -0.25f;

    private readonly object _gate = new();
    private readonly Dictionary<string, SkiaFace?> _embeddedFaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkiaFace?> _systemFaces = new(StringComparer.Ordinal);
    private readonly long _imageBudget;
    private readonly LinkedList<(string Key, SKImage Image, long Bytes)> _images = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, SKImage Image, long Bytes)>> _imageIndex = new(StringComparer.Ordinal);
    private long _imageBytes;

    public SkiaResources(long imageCacheBytes, SkiaFontSource fonts)
    {
        _imageBudget = Math.Max(0, imageCacheBytes);
        Fonts = fonts;
    }

    public SkiaFontSource Fonts { get; }

    public long CachedImageBytes
    {
        get { lock (_gate) return _imageBytes; }
    }

    /// <summary>
    /// The embedded font program as a Skia face (sfnt with TrueType or CFF outlines, loaded from
    /// memory: no temporary file); null when Skia cannot read it.
    /// </summary>
    public SkiaFace? GetEmbeddedFace(PdfFontFace face)
    {
        if (face.ProgramData.IsEmpty || face.Format is not (PdfFontProgramFormat.TrueType or PdfFontProgramFormat.OpenTypeCff))
            return null;
        lock (_gate)
        {
            if (_embeddedFaces.TryGetValue(face.Key, out var cached))
                return cached;
            SkiaFace? result = null;
            using (var data = SKData.CreateCopy(face.ProgramData.Span))
            {
                var typeface = SKTypeface.FromData(data);
                if (typeface != null && typeface.GlyphCount > 0)
                    result = new SkiaFace(typeface, false, false);
                else
                    typeface?.Dispose();
            }
            _embeddedFaces[face.Key] = result;
            return result;
        }
    }

    /// <summary>An installed face from the catalog, with bold or oblique simulated when the face lacks the style asked for.</summary>
    public SkiaFace? GetInstalledFace(SystemFontFace installed, bool bold, bool italic)
    {
        bool embolden = bold && !installed.Bold, oblique = italic && !installed.Italic;
        string key = $"file:{installed.Path}|{installed.Index}|{embolden}|{oblique}";
        lock (_gate)
        {
            if (_systemFaces.TryGetValue(key, out var cached))
                return cached;
            var typeface = Fonts.Load(installed);
            var result = typeface != null && typeface.GlyphCount > 0 ? new SkiaFace(typeface, embolden, oblique) : null;
            _systemFaces[key] = result;
            return result;
        }
    }

    /// <summary>A face of the family (a metric-compatible substitute); null when the family is not available.</summary>
    public SkiaFace? GetSystemFace(string family, bool bold, bool italic)
    {
        string key = $"{family}|{bold}|{italic}";
        lock (_gate)
        {
            if (_systemFaces.TryGetValue(key, out var cached))
                return cached;
            SkiaFace? result = null;
            // The source answers only for the family asked for, or a stand-in it chose on purpose.
            var typeface = Fonts.MatchFamily(family, bold, italic);
            if (typeface != null && typeface.GlyphCount > 0)
            {
                var style = typeface.FontStyle;
                result = new SkiaFace(typeface, bold && style.Weight < (int)SKFontStyleWeight.SemiBold,
                    italic && style.Slant == SKFontStyleSlant.Upright);
            }
            else
            {
                typeface?.Dispose();
            }
            _systemFaces[key] = result;
            return result;
        }
    }

    /// <summary>A cached image (moved to the front of the LRU list), or null.</summary>
    public SKImage? GetImage(string key)
    {
        lock (_gate)
        {
            if (!_imageIndex.TryGetValue(key, out var node))
                return null;
            _images.Remove(node);
            _images.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>Caches an image; the least recently used ones are released while the budget is exceeded.</summary>
    /// <remarks>An image larger than the whole budget is not kept: the caller disposes it after drawing.</remarks>
    public bool AddImage(string key, SKImage image, long bytes)
    {
        lock (_gate)
        {
            if (bytes > _imageBudget || _imageIndex.ContainsKey(key))
                return false;
            var node = _images.AddFirst((key, image, bytes));
            _imageIndex[key] = node;
            _imageBytes += bytes;
            while (_imageBytes > _imageBudget && _images.Last is { } last && !ReferenceEquals(last, node))
            {
                _images.RemoveLast();
                _imageIndex.Remove(last.Value.Key);
                _imageBytes -= last.Value.Bytes;
                last.Value.Image.Dispose();
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, image, _) in _images) image.Dispose();
            _images.Clear();
            _imageIndex.Clear();
            _imageBytes = 0;
            foreach (var f in _embeddedFaces.Values) f?.Typeface.Dispose();
            _embeddedFaces.Clear();
            foreach (var f in _systemFaces.Values) f?.Typeface.Dispose();
            _systemFaces.Clear();
        }
    }
}
