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
    /// The instance of a variable font at <paramref name="weight"/> (100–900), so bold text gets the
    /// real bold design instead of an emboldened regular: Android 12 and later ship Roboto as one
    /// variable font. Null when the face is not a variable font or the platform's font manager has no
    /// such instance; the caller then simulates the weight. SkiaSharp 3.116 has no API to set a
    /// variation axis, so the instance comes from Skia's font manager: Android's reads
    /// <c>/system/etc/fonts.xml</c>, which names an axis value for every weight of a variable family.
    /// Only on Android by default; Windows ships static bold faces for the families substitution uses.
    /// </summary>
    public virtual SKTypeface? LoadInstance(SystemFontFace face, int weight, bool italic) =>
        OperatingSystem.IsAndroid() ? FindInstance(SKFontManager.Default, face, weight, italic) : null;

    private const uint FvarTag = 0x66766172, HeadTag = 0x68656164;

    // (path, index, weight, italic) → the font manager's family and style index of the instance, or null.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, int, bool), (string Family, int Style)?> Instances = new();

    /// <summary>
    /// The style of <paramref name="manager"/> that is <paramref name="face"/>'s file at the weight and
    /// slant asked for: the same font data (its 'head' table) and a weight within 50.
    /// </summary>
    internal static SKTypeface? FindInstance(SKFontManager manager, SystemFontFace face, int weight, bool italic)
    {
        var key = (face.Path, face.Index, weight, italic);
        if (Instances.TryGetValue(key, out var known))
        {
            if (known is not { } where) return null;
            using var set = manager.GetFontStyles(where.Family);
            return where.Style < set.Count ? set.CreateTypeface(where.Style) : null;
        }
        byte[]? head;
        using (var file = SKTypeface.FromFile(face.Path, face.Index))
        {
            if (file == null || !file.GetTableTags().Contains(FvarTag) || !file.TryGetTableData(HeadTag, out head))
            {
                Instances[key] = null;
                return null;
            }
        }
        foreach (string family in manager.GetFontFamilies())
        {
            using var set = manager.GetFontStyles(family);
            for (int i = 0; i < set.Count; i++)
            {
                var style = set[i];
                if (Math.Abs(style.Weight - weight) > 50 || (style.Slant != SKFontStyleSlant.Upright) != italic || style.Width != (int)SKFontStyleWidth.Normal)
                    continue;
                var candidate = set.CreateTypeface(i);
                if (candidate != null && candidate.TryGetTableData(HeadTag, out var h) && h.AsSpan().SequenceEqual(head))
                {
                    Instances[key] = (family, i);
                    return candidate;
                }
                candidate?.Dispose();
            }
        }
        Instances[key] = null;
        return null;
    }

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
        if (Catalog.FindAliasedFamily(family, bold ? 700 : 400, italic) is not { } standIn) return null;
        // A variable stand-in (Roboto on Android 12+) has its bold as an instance, not a file.
        return (bold && !standIn.Bold ? LoadInstance(standIn, 700, italic && standIn.Italic) : null) ?? Load(standIn);
    }

    /// <summary>
    /// True when <see cref="Load"/> answered null for <paramref name="face"/> only because its file is
    /// not here yet: a host that fetches fonts on demand (a browser) loads it and draws the page again.
    /// The text then waits for that face instead of falling through to the next substitute, so
    /// that only the face it would be drawn with is fetched. False by default.
    /// </summary>
    public virtual bool IsPending(SystemFontFace face) => false;

    /// <summary>As <see cref="IsPending(SystemFontFace)"/>, for the face <see cref="MatchFamily"/> would load for the family.</summary>
    public virtual bool IsPending(string family, bool bold, bool italic) => false;

    /// <summary>
    /// True when the source names faces for single characters (<see cref="FindCharacterFace"/>), as a
    /// host that bundles fonts by script does (the web demo's Noto fonts). Text then takes each
    /// character that no substitute of the chain has from the face named for it. False by default,
    /// so the chain alone decides, as on Windows and Android.
    /// </summary>
    public virtual bool HasCharacterFaces => false;

    /// <summary>
    /// The face that has <paramref name="codePoint"/>, for a character that the substitutes of the
    /// chain lack; null when the source has none, or cannot tell yet (<see cref="IsCharacterIndexPending"/>).
    /// <paramref name="hint"/> tells faces that share characters apart (Han in Chinese, Japanese and Korean faces).
    /// </summary>
    public virtual SystemFontFace? FindCharacterFace(int codePoint, SkiaTextHint hint) => null;

    /// <summary>
    /// True when <see cref="FindCharacterFace"/> cannot answer until the host fetched what tells it
    /// which face has a character (an index of the bundled faces). The text then waits, as for a
    /// pending face.
    /// </summary>
    public virtual bool IsCharacterIndexPending => false;
}

/// <summary>What the text that needs a character face is: its font's name, CID ordering (Japan1, GB1, CNS1, Korea1) and characters.</summary>
public readonly record struct SkiaTextHint(string? FontName, string? CidOrdering, string Text);

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

    public SkiaResources(long imageCacheBytes, SkiaFontSource fonts, PdfEngine.Vector.Limits.PdfSecurityLimits? limits = null)
    {
        _imageBudget = Math.Max(0, imageCacheBytes);
        Fonts = fonts;
        Limits = limits ?? PdfEngine.Vector.Limits.PdfSecurityLimits.Default;
    }

    public SkiaFontSource Fonts { get; }

    /// <summary>Image size limits for the JPEG data the backend decodes itself.</summary>
    public PdfEngine.Vector.Limits.PdfSecurityLimits Limits { get; }

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
            SKTypeface? typeface = null;
            // A variable font's bold instance (Roboto on Android 12+) instead of an emboldened regular.
            if (embolden && Fonts.LoadInstance(installed, 700, installed.Italic) is { } instance)
            {
                if (instance.GlyphCount > 0) { typeface = instance; embolden = false; }
                else instance.Dispose();
            }
            typeface ??= Fonts.Load(installed);
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

    private readonly Dictionary<SkiaFace, TrueTypeFontFile?> _shapingFonts = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The substitute's font file as the editing shaper reads it (its cmap, GSUB and GPOS), read once
    /// on first use; null when it is not a TrueType-outline font or its data cannot be read.
    /// </summary>
    public TrueTypeFontFile? GetShapingFont(SkiaFace face)
    {
        lock (_gate)
        {
            if (_shapingFonts.TryGetValue(face, out var cached))
                return cached;
            TrueTypeFontFile? result = null;
            using (var stream = face.Typeface.OpenStream(out int index))
            {
                if (stream != null && stream.Length > 0)
                {
                    var data = new byte[stream.Length];
                    if (stream.Read(data, data.Length) == data.Length)
                        result = TrueTypeFontFile.TryLoad(data, index);
                }
            }
            _shapingFonts[face] = result;
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
            _shapingFonts.Clear();
        }
    }
}
