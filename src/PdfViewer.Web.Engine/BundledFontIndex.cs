using System.Text.Json;
using System.Text.RegularExpressions;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Skia;

namespace PdfViewer.Web.Engine;

/// <summary>
/// Which bundled face has which characters: the index that <c>eng/webdemo/noto_fonts.py</c> writes
/// next to the web demo's Noto fonts (<c>fonts/noto/index.json</c>). Each entry is one font file
/// (a whole face, or one Unicode slice of a CJK face) with the code points its cmap maps. A
/// character is looked up in the faces in index order, script and symbol faces first; the CJK
/// faces share most characters, so the language of the text picks which of them comes first.
/// </summary>
public sealed partial class BundledFontIndex
{
    /// <summary>One file of the index: the face it is (its path relative to the fonts folder) and the code points it covers.</summary>
    public sealed class Entry
    {
        private readonly int[] _starts;
        private readonly int[] _ends;

        internal Entry(SystemFontFace face, string group, int[] starts, int[] ends)
        {
            Face = face;
            Group = group;
            _starts = starts;
            _ends = ends;
        }

        public SystemFontFace Face { get; }

        /// <summary>The source the file comes from: "arabic", "symbols2", … or a CJK language ("sc", "tc", "jp", "kr").</summary>
        public string Group { get; }

        public bool IsCjk => CjkGroups.Contains(Group);

        /// <summary>Number of code points covered.</summary>
        public int CharacterCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _starts.Length; i++) n += _ends[i] - _starts[i] + 1;
                return n;
            }
        }

        public bool Covers(int codePoint)
        {
            int lo = 0, hi = _starts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (codePoint < _starts[mid]) hi = mid - 1;
                else if (codePoint > _ends[mid]) lo = mid + 1;
                else return true;
            }
            return false;
        }
    }

    /// <summary>The CJK languages, in the order tried after the one the text is in.</summary>
    public static IReadOnlyList<string> CjkGroups { get; } = new[] { "sc", "tc", "jp", "kr" };

    private BundledFontIndex(IReadOnlyList<Entry> entries) => Entries = entries;

    public IReadOnlyList<Entry> Entries { get; }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeName();

    /// <summary>
    /// Reads an index (version 1). Each face's "ranges" are pairs [gap, extra]: a run of extra + 1
    /// code points that starts gap code points after the end of the previous run (the first after
    /// -1). Entries with a file name that is not a plain name are left out.
    /// </summary>
    /// <param name="directory">Folder of the files relative to the fonts folder ("noto"); it prefixes each face's path.</param>
    /// <exception cref="FormatException">The data is not an index this version reads.</exception>
    public static BundledFontIndex Parse(ReadOnlySpan<byte> json, string directory)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new FormatException("Unknown font index version.");
            var entries = new List<Entry>();
            foreach (var item in root.GetProperty("faces").EnumerateArray())
            {
                string file = item.GetProperty("file").GetString() ?? "";
                string family = item.GetProperty("family").GetString() ?? "";
                string group = item.GetProperty("group").GetString() ?? "";
                if (!SafeName().IsMatch(file) || family.Length == 0) continue;
                var ranges = item.GetProperty("ranges");
                int count = ranges.GetArrayLength();
                if (count % 2 != 0) throw new FormatException("Odd range list.");
                var starts = new int[count / 2];
                var ends = new int[count / 2];
                long end = -1;
                int n = 0;
                foreach (var value in ranges.EnumerateArray())
                {
                    int v = value.GetInt32();
                    if (v < 0) throw new FormatException("Negative range value.");
                    if (n % 2 == 0)
                    {
                        starts[n / 2] = checked((int)(end + 1 + v));
                    }
                    else
                    {
                        end = (long)starts[n / 2] + v;
                        if (end > 0x10FFFF) throw new FormatException("Code point out of range.");
                        ends[n / 2] = (int)end;
                    }
                    n++;
                }
                var face = new SystemFontFace($"{directory}/{file}", 0, family, "Regular", Path.GetFileNameWithoutExtension(file), 400, false);
                entries.Add(new Entry(face, group, starts, ends));
            }
            return new BundledFontIndex(entries);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Not a font index.", ex);
        }
    }

    /// <summary>
    /// The face for a character: the first script or symbol face that has it, else a CJK face in
    /// the language of the text (<see cref="CjkLanguage"/>), else one of another CJK language.
    /// </summary>
    public SystemFontFace? Find(int codePoint, SkiaTextHint hint)
    {
        foreach (var e in Entries)
            if (!e.IsCjk && e.Covers(codePoint)) return e.Face;
        string language = CjkLanguage(hint);
        foreach (var e in Entries)
            if (e.Group == language && e.Covers(codePoint)) return e.Face;
        foreach (var group in CjkGroups)
        {
            if (group == language) continue;
            foreach (var e in Entries)
                if (e.Group == group && e.Covers(codePoint)) return e.Face;
        }
        return null;
    }

    /// <summary>
    /// The CJK language of a run: the font's character collection when it has one, else kana
    /// (Japanese) or hangul (Korean) in the text, else the font's name (MS Mincho, SimSun, MingLiU,
    /// Batang …); Simplified Chinese when nothing tells (<see cref="CharacterFaces.CjkLanguage"/>).
    /// </summary>
    public static string CjkLanguage(SkiaTextHint hint) => CharacterFaces.CjkLanguage(hint.CidOrdering, hint.FontName, hint.Text);
}
