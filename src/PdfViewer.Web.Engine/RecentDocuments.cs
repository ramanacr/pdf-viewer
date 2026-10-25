using System.Globalization;
using System.Text.Json;

namespace PdfViewer.Web.Engine;

/// <summary>A recently opened document: where it is (a content URI on Android), its file name, and where the reader was.</summary>
public sealed record RecentDocument(string Location, string Name, int Page, double Zoom, DateTimeOffset Opened);

/// <summary>
/// The recently opened documents, newest first, kept in one small JSON file in the app's private
/// storage. It holds locations, file names and reading positions only, never document text or
/// passwords. A damaged or unreadable file is an empty list; writes go through a temporary file,
/// so a crash leaves the old list or the new one.
/// </summary>
/// <remarks>Not thread-safe: one viewer uses it from its UI thread.</remarks>
public sealed class RecentDocuments
{
    private readonly string _path;
    private readonly List<RecentDocument> _items = new();

    public RecentDocuments(string path, int capacity = 12)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _path = path;
        Capacity = capacity;
        Load();
    }

    public int Capacity { get; }

    public IReadOnlyList<RecentDocument> Items => _items;

    public RecentDocument? Find(string location) => _items.Find(d => d.Location == location);

    /// <summary>The document was opened: it moves to the top, keeping its last position unless one is given.</summary>
    public RecentDocument Opened(string location, string name, DateTimeOffset now, int? page = null, double? zoom = null)
    {
        var old = Find(location);
        var entry = new RecentDocument(location, string.IsNullOrWhiteSpace(name) ? "Document" : name.Trim(),
            page ?? old?.Page ?? 1, zoom ?? old?.Zoom ?? 1, now);
        _items.RemoveAll(d => d.Location == location);
        _items.Insert(0, entry);
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
        Save();
        return entry;
    }

    /// <summary>The reader moved: remembers the page and zoom of a listed document.</summary>
    public void Moved(string location, int page, double zoom)
    {
        int i = _items.FindIndex(d => d.Location == location);
        if (i < 0 || (_items[i].Page == page && _items[i].Zoom.Equals(zoom))) return;
        _items[i] = _items[i] with { Page = Math.Max(1, page), Zoom = zoom };
        Save();
    }

    /// <summary>Takes a document off the list (it can no longer be opened, or the user removed it).</summary>
    public bool Remove(string location)
    {
        if (_items.RemoveAll(d => d.Location == location) == 0) return false;
        Save();
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Load()
    {
        _items.Clear();
        try
        {
            if (!File.Exists(_path)) return;
            using var json = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (!json.RootElement.TryGetProperty("documents", out var docs) || docs.ValueKind != JsonValueKind.Array) return;
            foreach (var d in docs.EnumerateArray())
            {
                if (_items.Count >= Capacity) break;
                if (d.ValueKind != JsonValueKind.Object
                    || !d.TryGetProperty("location", out var loc) || loc.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(loc.GetString()))
                    continue;
                string location = loc.GetString()!;
                if (_items.Exists(x => x.Location == location)) continue;
                string name = d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "Document";
                int page = d.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int pv) ? Math.Max(1, pv) : 1;
                double zoom = d.TryGetProperty("zoom", out var z) && z.ValueKind == JsonValueKind.Number && z.TryGetDouble(out double zv) && zv > 0 && double.IsFinite(zv) ? zv : 1;
                var opened = d.TryGetProperty("opened", out var o) && o.ValueKind == JsonValueKind.String
                             && DateTimeOffset.TryParse(o.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ov) ? ov : DateTimeOffset.MinValue;
                _items.Add(new RecentDocument(location, name, page, zoom, opened));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _items.Clear(); // a damaged list is no list
        }
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = _path + ".tmp";
            using (var stream = File.Create(temp))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteStartArray("documents");
                foreach (var d in _items)
                {
                    w.WriteStartObject();
                    w.WriteString("location", d.Location);
                    w.WriteString("name", d.Name);
                    w.WriteNumber("page", d.Page);
                    w.WriteNumber("zoom", d.Zoom);
                    w.WriteString("opened", d.Opened.ToString("O", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list is a convenience: a full or read-only disk must not stop the viewer.
        }
    }
}
