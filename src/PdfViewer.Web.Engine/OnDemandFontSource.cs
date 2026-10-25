using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Skia;
using SkiaSharp;

namespace PdfViewer.Web.Engine;

/// <summary>
/// Font substitutes that a host bundles but fetches only when a page needs them (the web demo's
/// Liberation fonts, served next to the app). The catalog is described up front, so that
/// substitution chooses a face by name, design and class as with installed fonts. A face that is
/// not fetched yet is recorded and answered as missing; <see cref="ViewerDocument"/> then fetches
/// what was recorded (<see cref="LoadRequestedAsync"/>) and draws the page again. A document that
/// embeds all its fonts asks for no face, so nothing is fetched. Fetched files are kept for the
/// session; Skia's font manager is not asked, so only the bundled faces are used.
/// </summary>
public sealed class OnDemandFontSource : SkiaFontSource
{
    private readonly Func<string, CancellationToken, Task<byte[]?>> _fetch;
    private readonly HashSet<string> _known;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly List<string> _requested = new();

    /// <param name="faces">The faces the host can fetch; <see cref="SystemFontFace.Path"/> is the file name handed to <paramref name="fetch"/>.</param>
    /// <param name="aliases">Families that stand in for the families substitution asks for (<see cref="SystemFontCatalog.WithFamilyAliases"/>).</param>
    /// <param name="fetch">Reads one font file by name; null (or an exception) when it cannot be had, after which that file is not asked for again.</param>
    public OnDemandFontSource(IEnumerable<SystemFontFace> faces, IReadOnlyDictionary<string, string[]> aliases,
        Func<string, CancellationToken, Task<byte[]?>> fetch)
        : this(SystemFontCatalog.FromFaces(faces, aliases), fetch)
    {
    }

    private OnDemandFontSource(SystemFontCatalog catalog, Func<string, CancellationToken, Task<byte[]?>> fetch) : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        _fetch = fetch;
        _known = catalog.Faces.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The Liberation fonts 2.1.5 (SIL Open Font License 1.1): Sans, Serif and Mono, each regular,
    /// bold, italic and bold italic, standing in by <see cref="SystemFontCatalog.LiberationFamilyAliases"/>
    /// for Arial and Helvetica, Times New Roman and Times, Courier New and Courier.
    /// </summary>
    public static OnDemandFontSource Liberation(Func<string, CancellationToken, Task<byte[]?>> fetch) =>
        new(LiberationFaces, SystemFontCatalog.LiberationFamilyAliases, fetch);

    /// <summary>The bundled Liberation faces, as their name and OS/2 tables describe them, by file name.</summary>
    public static IReadOnlyList<SystemFontFace> LiberationFaces { get; } = BuildLiberationFaces();

    private static SystemFontFace[] BuildLiberationFaces()
    {
        var faces = new List<SystemFontFace>();
        foreach (var family in new[] { "Sans", "Serif", "Mono" })
            foreach (var (style, subfamily, weight, italic) in new[]
                     { ("Regular", "Regular", 400, false), ("Bold", "Bold", 700, false), ("Italic", "Italic", 400, true), ("BoldItalic", "Bold Italic", 700, true) })
            {
                string postScript = style == "Regular" ? $"Liberation{family}" : $"Liberation{family}-{style}";
                faces.Add(new SystemFontFace($"Liberation{family}-{style}.ttf", 0, $"Liberation {family}", subfamily, postScript, weight, italic));
            }
        return faces.ToArray();
    }

    /// <summary>Faces that substitution asked for and that are not fetched yet (nor failed), in the order asked.</summary>
    public IReadOnlyList<string> PendingFiles
    {
        get { lock (_gate) return _requested.Where(IsPendingFile).ToArray(); }
    }

    /// <summary>Font files fetched so far.</summary>
    public IReadOnlyCollection<string> LoadedFiles
    {
        get { lock (_gate) return _loaded.Keys.ToArray(); }
    }

    public bool HasPendingRequests
    {
        get { lock (_gate) return _requested.Exists(IsPendingFile); }
    }

    /// <summary>Fetches every face asked for and not fetched yet; returns how many were loaded.</summary>
    public async Task<int> LoadRequestedAsync(CancellationToken cancellationToken = default)
    {
        int count = 0;
        foreach (var file in PendingFiles)
        {
            byte[]? data;
            try
            {
                data = await _fetch(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                data = null;
            }
            lock (_gate)
            {
                if (data is { Length: >= 12 }) { _loaded[file] = data; count++; }
                else _failed.Add(file);
            }
        }
        return count;
    }

    public override SKTypeface? Load(SystemFontFace face)
    {
        byte[]? data;
        lock (_gate)
        {
            if (!_known.Contains(face.Path) || _failed.Contains(face.Path)) return null;
            if (!_loaded.TryGetValue(face.Path, out data))
            {
                if (!_requested.Contains(face.Path)) _requested.Add(face.Path);
                return null;
            }
        }
        using var skData = SKData.CreateCopy(data);
        return SKTypeface.FromData(skData, face.Index);
    }

    /// <summary>The bundled face of the family, or of the family that stands in for it; Skia's font manager is not asked.</summary>
    public override SKTypeface? MatchFamily(string family, bool bold, bool italic) =>
        Catalog.FindFamily(family, bold, italic) is { } face ? Load(face) : null;

    public override bool IsPending(SystemFontFace face)
    {
        lock (_gate) return IsPendingFile(face.Path);
    }

    public override bool IsPending(string family, bool bold, bool italic) =>
        Catalog.FindFamily(family, bold, italic) is { } face && IsPending(face);

    private bool IsPendingFile(string file) => _known.Contains(file) && !_loaded.ContainsKey(file) && !_failed.Contains(file);
}
