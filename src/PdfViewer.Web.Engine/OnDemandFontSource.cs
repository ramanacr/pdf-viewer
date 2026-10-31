using PdfEngine.Vector.Skia;
using SkiaSharp;
using PdfEngine.Vector.Fonts;

namespace PdfViewer.Web.Engine;

/// <summary>
/// Font substitutes that a host bundles but fetches only when a page needs them (the web demo's
/// Liberation and Noto fonts, served next to the app). The catalog is described up front, so that
/// substitution chooses a face by name, design and class as with installed fonts. A face that is
/// not fetched yet is recorded and answered as missing; <see cref="ViewerDocument"/> then fetches
/// what was recorded (<see cref="LoadRequestedAsync"/>) and draws the page again. A document that
/// embeds all its fonts asks for no face, so nothing is fetched. Fetched files are kept for the
/// session; Skia's font manager is not asked, so only the bundled faces are used.
/// <para>
/// With a character index (<see cref="Bundled"/>: the Noto fonts' <c>noto/index.json</c>), text
/// that the catalog's faces lack takes each character from the bundled face that has it: Arabic,
/// Hebrew, the main Indic scripts, Sinhala, Thai, symbols, math, and CJK in Unicode slices. The
/// index is fetched the first time such a character is drawn, then only the files that hold the
/// page's characters. A file whose size or SHA-256 differs from the index is refused.
/// </para>
/// </summary>
public sealed class OnDemandFontSource : SkiaFontSource
{
    /// <summary>Where the Noto fonts' index is, relative to the fonts folder.</summary>
    public const string NotoIndexFile = "noto/index.json";

    private readonly Func<string, CancellationToken, Task<byte[]?>> _fetch;
    private readonly HashSet<string> _known;
    private readonly string? _indexFile;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly List<string> _requested = new();
    private BundledFontIndex? _index;

    /// <param name="faces">The faces the host can fetch; <see cref="SystemFontFace.Path"/> is the file name handed to <paramref name="fetch"/>.</param>
    /// <param name="aliases">Families that stand in for the families substitution asks for (<see cref="SystemFontCatalog.WithFamilyAliases"/>).</param>
    /// <param name="fetch">Reads one file by name; null (or an exception) when it cannot be had, after which that file is not asked for again.</param>
    /// <param name="characterIndexFile">A <see cref="BundledFontIndex"/> to fetch for characters the faces lack (its faces' paths are relative to its folder); null for none.</param>
    public OnDemandFontSource(IEnumerable<SystemFontFace> faces, IReadOnlyDictionary<string, string[]> aliases,
        Func<string, CancellationToken, Task<byte[]?>> fetch, string? characterIndexFile = null)
        : this(SystemFontCatalog.FromFaces(faces, aliases), fetch, characterIndexFile)
    {
    }

    private OnDemandFontSource(SystemFontCatalog catalog, Func<string, CancellationToken, Task<byte[]?>> fetch, string? characterIndexFile) : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        _fetch = fetch;
        _known = catalog.Faces.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        _indexFile = characterIndexFile;
        if (_indexFile != null) _known.Add(_indexFile);
    }

    /// <summary>
    /// The Liberation fonts 2.1.5 (SIL Open Font License 1.1): Sans, Serif and Mono, each regular,
    /// bold, italic and bold italic, standing in by <see cref="SystemFontCatalog.LiberationFamilyAliases"/>
    /// for Arial and Helvetica, Times New Roman and Times, Courier New and Courier.
    /// </summary>
    public static OnDemandFontSource Liberation(Func<string, CancellationToken, Task<byte[]?>> fetch) =>
        new(LiberationFaces, SystemFontCatalog.LiberationFamilyAliases, fetch);

    /// <summary>
    /// The web demo's fonts: <see cref="Liberation"/>, then the Noto fonts (SIL Open Font License 1.1),
    /// found by <see cref="NotoIndexFile"/>, for the characters Liberation lacks.
    /// </summary>
    public static OnDemandFontSource Bundled(Func<string, CancellationToken, Task<byte[]?>> fetch) =>
        new(LiberationFaces, SystemFontCatalog.LiberationFamilyAliases, fetch, NotoIndexFile);

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

    /// <summary>Files that substitution asked for and that are not fetched yet (nor failed), in the order asked.</summary>
    public IReadOnlyList<string> PendingFiles
    {
        get { lock (_gate) return _requested.Where(IsPendingFile).ToArray(); }
    }

    /// <summary>Files fetched so far: font files, and the character index once read.</summary>
    public IReadOnlyCollection<string> LoadedFiles
    {
        get { lock (_gate) return _loaded.Keys.ToArray(); }
    }

    public bool HasPendingRequests
    {
        get { lock (_gate) return _requested.Exists(IsPendingFile); }
    }

    /// <summary>The character index once it is fetched and read; null before, without one, or when it could not be had.</summary>
    public BundledFontIndex? CharacterIndex
    {
        get { lock (_gate) return _index; }
    }

    /// <summary>Fetches every file asked for and not fetched yet; returns how many were loaded.</summary>
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
            // A file the index describes must have its size and SHA-256: a truncated or altered
            // file counts as failed, so its text is reported as missing instead of drawn wrongly.
            if (data != null && file != _indexFile && CharacterIndex?.EntryOf(file) is { } described && !described.Matches(data))
                data = null;
            BundledFontIndex? index = null;
            if (file == _indexFile && data != null)
            {
                int slash = file.LastIndexOf('/');
                try
                {
                    index = BundledFontIndex.Parse(data, slash < 0 ? "" : file[..slash]);
                }
                catch (FormatException)
                {
                    data = null;
                }
            }
            lock (_gate)
            {
                if (data is { Length: >= 12 })
                {
                    count++;
                    if (index != null)
                    {
                        // The index is not a face: its entries become faces that can be fetched.
                        _index = index;
                        _loaded[file] = Array.Empty<byte>();
                        foreach (var entry in index.Entries) _known.Add(entry.Face.Path);
                    }
                    else
                    {
                        _loaded[file] = data;
                    }
                }
                else
                {
                    _failed.Add(file);
                }
            }
        }
        return count;
    }

    public override SKTypeface? Load(SystemFontFace face)
    {
        byte[]? data;
        lock (_gate)
        {
            if (face.Path == _indexFile || !_known.Contains(face.Path) || _failed.Contains(face.Path)) return null;
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

    public override bool HasCharacterFaces => _indexFile != null;

    public override SystemFontFace? FindCharacterFace(int codePoint, SkiaTextHint hint)
    {
        BundledFontIndex? index;
        lock (_gate)
        {
            index = _index;
            if (index == null)
            {
                // The index is fetched the first time a character needs it.
                if (_indexFile != null && !_failed.Contains(_indexFile) && !_requested.Contains(_indexFile)) _requested.Add(_indexFile);
                return null;
            }
        }
        return index.Find(codePoint, hint);
    }

    public override bool IsCharacterIndexPending
    {
        get { lock (_gate) return _indexFile != null && IsPendingFile(_indexFile); }
    }

    private bool IsPendingFile(string file) => _known.Contains(file) && !_loaded.ContainsKey(file) && !_failed.Contains(file);
}
