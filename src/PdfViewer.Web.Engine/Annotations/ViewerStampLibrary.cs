using System.Text.Json;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>A custom stamp kept in a <see cref="ViewerStampLibrary"/>: its id (the stamp's hash), name and kind.</summary>
public sealed record ViewerSavedStamp(string Id, string Name, ViewerCustomStampKind Kind, long Size, DateTimeOffset Created);

/// <summary>
/// The reader's custom stamps kept in a folder of an app's private storage (the Android app's;
/// the web viewer keeps its library in the browser, viewer.js): each stamp's one-page PDF as
/// "&lt;hash&gt;.pdf" and an index with the names. At most <see cref="MaxStamps"/> stamps and
/// <see cref="MaxBytes"/> together. What is read back is checked as a new stamp is
/// (<see cref="ViewerCustomStamp.FromKept"/>); a damaged index or file is left out, not trusted.
/// Nothing is sent anywhere. Not thread-safe.
/// </summary>
public sealed class ViewerStampLibrary
{
    public const int MaxStamps = 30;
    public const long MaxBytes = 64L * 1024 * 1024;
    public const int MaxNameLength = 80;

    private const string IndexName = "stamps.json";
    private readonly string _folder;

    public ViewerStampLibrary(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        _folder = folder;
    }

    private sealed record Entry(string Id, string Name, string Kind, DateTimeOffset Created);

    private static bool IsId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string FileOf(string id) => Path.Combine(_folder, id + ".pdf");

    /// <summary>The saved stamps, oldest first; empty when there are none or the index cannot be read.</summary>
    public IReadOnlyList<ViewerSavedStamp> List()
    {
        var result = new List<ViewerSavedStamp>();
        foreach (var e in ReadIndex())
        {
            try
            {
                var info = new FileInfo(FileOf(e.Id));
                if (!info.Exists) continue;
                result.Add(new ViewerSavedStamp(e.Id, e.Name, e.Kind == nameof(ViewerCustomStampKind.Page) ? ViewerCustomStampKind.Page : ViewerCustomStampKind.Image, info.Length, e.Created));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    /// <summary>
    /// Keeps <paramref name="stamp"/> under <paramref name="name"/>: null when kept (or kept
    /// already), else why not (the library is full, or the storage refused).
    /// </summary>
    public string? Save(ViewerCustomStamp stamp, string name)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var index = ReadIndex();
        if (index.Any(e => e.Id == stamp.Hash) && File.Exists(FileOf(stamp.Hash))) return null;
        index.RemoveAll(e => e.Id == stamp.Hash);
        var kept = List();
        if (kept.Count >= MaxStamps) return $"The stamp library keeps at most {MaxStamps} stamps. Delete one first.";
        if (kept.Sum(s => s.Size) + stamp.Pdf.Length > MaxBytes) return $"The stamp library is full ({MaxBytes / (1024 * 1024)} MB). Delete a stamp first.";
        string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Stamp";
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength];
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(FileOf(stamp.Hash), stamp.Pdf);
            index.Add(new Entry(stamp.Hash, clean, stamp.Kind.ToString(), DateTimeOffset.UtcNow));
            WriteIndex(index);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The stamp could not be kept on this device (its storage is full or refused).";
        }
    }

    /// <summary>A saved stamp, checked again; null when it is not there or is damaged.</summary>
    public ViewerCustomStamp? Load(string id)
    {
        if (!IsId(id) || ReadIndex().FirstOrDefault(e => e.Id == id) is not { } entry) return null;
        try
        {
            var info = new FileInfo(FileOf(id));
            if (!info.Exists || info.Length > ViewerCustomStamp.MaxStampBytes) return null;
            var stamp = ViewerCustomStamp.FromKept(entry.Kind == nameof(ViewerCustomStampKind.Page) ? ViewerCustomStampKind.Page : ViewerCustomStampKind.Image,
                File.ReadAllBytes(info.FullName));
            return stamp.Hash == id ? stamp : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ViewerAnnotationException)
        {
            return null;
        }
    }

    /// <summary>Deletes a saved stamp (stamps already placed stay in their documents); false when it was not there or the storage refused.</summary>
    public bool Delete(string id)
    {
        if (!IsId(id)) return false;
        var index = ReadIndex();
        bool found = index.RemoveAll(e => e.Id == id) > 0;
        try
        {
            WriteIndex(index);
            if (File.Exists(FileOf(id)))
            {
                File.Delete(FileOf(id));
                found = true;
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<Entry> ReadIndex()
    {
        try
        {
            string path = Path.Combine(_folder, IndexName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024) return [];
            // Read by hand (no reflection: the apps are trimmed); an entry that is not as written is left out.
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var list = new List<Entry>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string? id = Text(e, "id"), name = Text(e, "name"), kind = Text(e, "kind");
                if (!IsId(id) || name is not { Length: > 0 and <= MaxNameLength } || name.Any(char.IsControl)) continue;
                var created = e.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out long ms)
                    && ms >= 0 && ms < 253402300800000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : default;
                list.Add(new Entry(id!, name, kind == nameof(ViewerCustomStampKind.Page) ? kind : nameof(ViewerCustomStampKind.Image), created));
            }
            return list.DistinctBy(e => e.Id).Take(MaxStamps).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private void WriteIndex(List<Entry> index)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, IndexName), temporary = path + ".new";
        using (var stream = File.Create(temporary))
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var e in index)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("name", e.Name);
                w.WriteString("kind", e.Kind);
                w.WriteNumber("created", e.Created.ToUnixTimeMilliseconds());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        File.Move(temporary, path, overwrite: true);
    }
}
