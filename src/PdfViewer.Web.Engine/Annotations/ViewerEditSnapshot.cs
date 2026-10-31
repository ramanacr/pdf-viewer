using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>One annotation of a snapshot: a file's annotation that did not change (its id only), or the annotation as it is now.</summary>
public sealed record ViewerSnapshotItem(string? KeptId, ViewerAnnotation? Annotation);

/// <summary>
/// The reader's edits of one document, kept in the app's private storage while Android ends the
/// app in the background, so that a new process opens the document with the same edits pending
/// (<see cref="ViewerEditSession.Snapshot"/>, <see cref="ViewerEditSession.Restore"/>). It names
/// the document by its location and the SHA-256 of its bytes as opened: the edits are given back
/// only to those bytes. It never contains the document's own text (page content, or a file
/// annotation the reader did not change, which is kept by its id only) or a password: only the
/// annotation data and form values the reader made. It is deleted when the edits are saved or discarded.
/// </summary>
public sealed record ViewerEditSnapshot
{
    /// <summary>The version of the JSON form; another version is not read.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// The largest snapshot written or read: 32 MB of UTF-8 (and as many characters). A larger
    /// one is not kept; the edits stay in memory until saved.
    /// </summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>Where the document is (an Android content URI).</summary>
    public required string Location { get; init; }

    /// <summary>The length of the document's bytes as opened.</summary>
    public required long OriginalLength { get; init; }

    /// <summary>The SHA-256 of the document's bytes as opened, as lower-case hex (<see cref="Hash"/>).</summary>
    public required string OriginalHash { get; init; }

    /// <summary>The annotations in the order the store has them.</summary>
    public IReadOnlyList<ViewerSnapshotItem> Annotations { get; init; } = [];

    /// <summary>The form values the reader set, by field name.</summary>
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Values { get; init; } = [];

    /// <summary>The SHA-256 of <paramref name="bytes"/> as lower-case hex.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The bytes to open so that the snapshot's edits fit them, from the file as it is read now:
    /// the file itself when it is the bytes the snapshot was made on, or its start when the file is
    /// those bytes with incremental updates after them (a save into the file appends one). Null
    /// when the file is something else: the edits cannot be given back.
    /// </summary>
    public byte[]? OriginalOf(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (OriginalLength <= 0 || file.LongLength < OriginalLength) return null;
        var start = file.AsSpan(0, (int)OriginalLength);
        if (!string.Equals(Hash(start), OriginalHash, StringComparison.Ordinal)) return null;
        return file.LongLength == OriginalLength ? file : start.ToArray();
    }

    // ------------------------------------------------------------------ JSON

    /// <summary>The snapshot as JSON text.</summary>
    public string ToJson() => Encoding.UTF8.GetString(Write(Annotations).WrittenSpan);

    /// <summary>
    /// The snapshot as UTF-8 JSON for a file, at most <see cref="MaxBytes"/>. When the custom
    /// stamps make it larger, the stamps that do not fit are left out (with their replies), as
    /// many as needed, so that the other edits are still kept; <paramref name="droppedStamps"/>
    /// counts the stamps left out, for the reader to be told. Null when even the edits without any
    /// custom stamp are larger (nothing is then kept).
    /// </summary>
    public byte[]? ToUtf8(out int droppedStamps) => ToUtf8(MaxBytes, out droppedStamps);

    /// <summary><see cref="ToUtf8(out int)"/> without the count.</summary>
    public byte[]? ToUtf8() => ToUtf8(MaxBytes, out _);

    /// <summary><see cref="ToUtf8(out int)"/> with another limit (for tests).</summary>
    internal byte[]? ToUtf8(int maxBytes, out int droppedStamps)
    {
        droppedStamps = 0;
        var stamps = Annotations.Select(i => i.Annotation?.CustomStamp).OfType<ViewerCustomStamp>().DistinctBy(c => c.Hash, StringComparer.Ordinal).ToList();
        // Base64 is 4 bytes for 3: stamps too large are known before anything is written out,
        // so a reader's many stamps never fill memory here.
        long stampText = stamps.Sum(c => (c.Pdf.LongLength + 2) / 3 * 4);
        if (stampText < maxBytes)
        {
            var whole = Write(Annotations);
            if (whole.WrittenCount <= maxBytes) return whole.WrittenSpan.ToArray();
        }
        if (stamps.Count == 0) return null;
        // Too large: the edits without custom stamps, then the stamps that fit, in the order they were made.
        var none = Without(stamps.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal));
        var bare = Write(none);
        if (bare.WrittenCount > maxBytes) return null;
        long room = maxBytes - bare.WrittenCount;
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in stamps)
        {
            int uses = Annotations.Count(i => i.Annotation?.CustomStamp?.Hash == c.Hash);
            // Its base64, its entry, and each annotation that uses it (a generous allowance for one).
            long cost = (c.Pdf.LongLength + 2) / 3 * 4 + 256 + 4096L * uses;
            if (cost <= room) room -= cost;
            else dropped.Add(c.Hash);
        }
        var kept = Without(dropped);
        var buffer = Write(kept);
        if (buffer.WrittenCount > maxBytes)
        {
            kept = none;
            buffer = bare;
        }
        droppedStamps = Annotations.Count(i => i.Annotation is { CustomStamp: { } c } && !kept.Any(k => ReferenceEquals(k.Annotation, i.Annotation)));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The items without the custom stamps of <paramref name="hashes"/>, and without the replies to what is left out.</summary>
    private List<ViewerSnapshotItem> Without(HashSet<string> hashes)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in Annotations)
            if (i.Annotation is { CustomStamp: { } c } a && hashes.Contains(c.Hash)) gone.Add(a.Id);
        // Replies, a level at a time (a thread is at most the reply depth deep).
        for (int level = 0; level <= ViewerAnnotationStore.MaxReplyDepth; level++)
        {
            int before = gone.Count;
            foreach (var i in Annotations)
                if (i.Annotation is { Kind: ViewerAnnotationKind.Reply } r && gone.Contains(r.InReplyTo)) gone.Add(r.Id);
            if (gone.Count == before) break;
        }
        return Annotations.Where(i => i.Annotation is not { } a || !gone.Contains(a.Id)).ToList();
    }

    private ArrayBufferWriter<byte> Write(IReadOnlyList<ViewerSnapshotItem> annotations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FormatVersion);
            w.WriteString("location", Location);
            w.WriteNumber("length", OriginalLength);
            w.WriteString("sha256", OriginalHash);
            // The pictures and pages of custom stamps, each once (by its hash), as the one-page PDF that draws it.
            var stamps = annotations.Select(i => i.Annotation?.CustomStamp).OfType<ViewerCustomStamp>().DistinctBy(c => c.Hash, StringComparer.Ordinal).ToList();
            if (stamps.Count > 0)
            {
                w.WriteStartArray("customStamps");
                foreach (var c in stamps)
                {
                    w.WriteStartObject();
                    w.WriteString("hash", c.Hash);
                    w.WriteString("kind", c.Kind.ToString());
                    w.WriteBase64String("pdf", c.Pdf);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteStartArray("annotations");
            foreach (var item in annotations)
            {
                if (item.KeptId is { } id)
                {
                    w.WriteStartObject();
                    w.WriteString("kept", id);
                    w.WriteEndObject();
                }
                else if (item.Annotation is { } a)
                {
                    WriteAnnotation(w, a);
                }
            }
            w.WriteEndArray();
            w.WriteStartObject("values");
            foreach (var (field, values) in Values)
            {
                w.WriteStartArray(field);
                foreach (string v in values) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer;
    }

    private static void WriteAnnotation(Utf8JsonWriter w, ViewerAnnotation a)
    {
        w.WriteStartObject();
        w.WriteString("id", a.Id);
        w.WriteNumber("page", a.Page);
        w.WriteString("kind", a.Kind.ToString());
        Numbers(w, "box", [a.Box.X1, a.Box.Y1, a.Box.X2, a.Box.Y2]);
        Numbers(w, "quads", a.QuadPoints);
        w.WriteStartArray("ink");
        foreach (var stroke in a.InkList)
        {
            w.WriteStartArray();
            foreach (double v in stroke) w.WriteNumberValue(v);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        Numbers(w, "vertices", a.Vertices);
        if (a.Callout.Count > 0) Numbers(w, "callout", a.Callout);
        w.WriteString("startEnding", a.StartEnding);
        w.WriteString("endEnding", a.EndEnding);
        w.WriteString("stampName", a.StampName);
        if (a.StampSubtitle != null) w.WriteString("stampSubtitle", a.StampSubtitle);
        w.WriteNumber("stampTurn", a.StampTurn);
        if (a.CustomStamp is { } custom) w.WriteString("customStamp", custom.Hash);
        w.WriteString("inReplyTo", a.InReplyTo);
        w.WriteString("state", a.State);
        w.WriteString("stateModel", a.StateModel);
        w.WriteString("color", a.Color);
        if (a.FillColor != null) w.WriteString("fill", a.FillColor);
        w.WriteNumber("opacity", a.Opacity);
        if (a.Dash != null) Numbers(w, "dash", a.Dash);
        w.WriteBoolean("measurement", a.IsMeasurement);
        w.WriteNumber("strokeWidth", a.StrokeWidth);
        w.WriteNumber("fontSize", a.FontSize);
        w.WriteString("contents", a.Contents);
        w.WriteString("author", a.Author);
        w.WriteString("created", a.Created.ToString("O", CultureInfo.InvariantCulture));
        w.WriteString("modified", a.Modified.ToString("O", CultureInfo.InvariantCulture));
        w.WriteNumber("sourceIndex", a.SourceIndex);
        w.WriteEndObject();
    }

    private static void Numbers(Utf8JsonWriter w, string name, IEnumerable<double> values)
    {
        w.WriteStartArray(name);
        foreach (double v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    /// <summary>
    /// Reads a snapshot written by <see cref="ToJson"/>. Throws <see cref="FormatException"/> when
    /// the text is not one (damaged, another version, a value of the wrong type); the values are
    /// checked again when the session takes them (<see cref="ViewerEditSession.Restore"/>).
    /// </summary>
    public static ViewerEditSnapshot FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxBytes) throw new FormatException("The kept edits are too large.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "version") != FormatVersion) throw new FormatException("The kept edits are of another version.");
            var stamps = new Dictionary<string, ViewerCustomStamp>(StringComparer.Ordinal);
            // Kept edits written before custom stamps have no "customStamps".
            if (root.TryGetProperty("customStamps", out _))
            {
                foreach (var c in Array(root, "customStamps"))
                {
                    if (!Enum.TryParse<ViewerCustomStampKind>(Str(c, "kind"), ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
                        throw new FormatException("A custom stamp is of an unknown kind.");
                    string hash = Str(c, "hash");
                    ViewerCustomStamp stamp;
                    try
                    {
                        // Checked as a new stamp is: the file the edits were kept in is not trusted more.
                        stamp = ViewerCustomStamp.FromKept(kind, c.GetProperty("pdf").GetBytesFromBase64());
                    }
                    catch (ViewerAnnotationException)
                    {
                        throw new FormatException("A custom stamp of the kept edits cannot be read.");
                    }
                    if (stamp.Hash != hash) throw new FormatException("A custom stamp of the kept edits is damaged.");
                    stamps[hash] = stamp;
                }
            }
            var items = new List<ViewerSnapshotItem>();
            foreach (var e in Array(root, "annotations"))
            {
                if (e.ValueKind != JsonValueKind.Object) throw new FormatException("An annotation is not an object.");
                items.Add(e.TryGetProperty("kept", out var kept) ? new ViewerSnapshotItem(Text(kept), null) : new ViewerSnapshotItem(null, ReadAnnotation(e, stamps)));
            }
            var values = new List<KeyValuePair<string, IReadOnlyList<string>>>();
            if (root.TryGetProperty("values", out var v))
            {
                if (v.ValueKind != JsonValueKind.Object) throw new FormatException("The values are not an object.");
                foreach (var p in v.EnumerateObject())
                    values.Add(new(p.Name, Items(p.Value).Select(Text).ToList()));
            }
            long length = root.GetProperty("length").GetInt64();
            return new ViewerEditSnapshot
            {
                Location = Str(root, "location"),
                OriginalLength = length,
                OriginalHash = Str(root, "sha256"),
                Annotations = items,
                Values = values,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            // The exception's type only: the text holds the reader's annotations.
            throw new FormatException($"The kept edits cannot be read ({ex.GetType().Name}).");
        }
    }

    private static ViewerAnnotation ReadAnnotation(JsonElement e, IReadOnlyDictionary<string, ViewerCustomStamp> stamps)
    {
        if (!Enum.TryParse<ViewerAnnotationKind>(Str(e, "kind"), ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
            throw new FormatException("An annotation is of an unknown kind.");
        var box = Doubles(e, "box");
        if (box.Length != 4) throw new FormatException("An annotation's box is not four numbers.");
        return new ViewerAnnotation
        {
            Id = Str(e, "id"),
            Page = Int(e, "page"),
            Kind = kind,
            Box = new ViewerBox(box[0], box[1], box[2], box[3]),
            QuadPoints = Doubles(e, "quads"),
            InkList = Array(e, "ink").Select(s => (IReadOnlyList<double>)Items(s).Select(n => n.GetDouble()).ToArray()).ToArray(),
            Vertices = Doubles(e, "vertices"),
            // Kept edits written before callouts have no "callout".
            Callout = e.TryGetProperty("callout", out _) ? Doubles(e, "callout") : [],
            StartEnding = Str(e, "startEnding"),
            EndEnding = Str(e, "endEnding"),
            StampName = Str(e, "stampName"),
            StampSubtitle = e.TryGetProperty("stampSubtitle", out var sub) ? Text(sub) : null,
            StampTurn = Int(e, "stampTurn"),
            CustomStamp = e.TryGetProperty("customStamp", out var cs)
                ? stamps.GetValueOrDefault(Text(cs)) ?? throw new FormatException("An annotation names a custom stamp the kept edits do not have.")
                : null,
            InReplyTo = Str(e, "inReplyTo"),
            State = Str(e, "state"),
            StateModel = Str(e, "stateModel"),
            Color = Colour(Str(e, "color")),
            FillColor = e.TryGetProperty("fill", out var fill) ? Colour(Text(fill)) : null,
            Opacity = e.GetProperty("opacity").GetDouble(),
            Dash = e.TryGetProperty("dash", out _) ? Doubles(e, "dash") : null,
            IsMeasurement = e.GetProperty("measurement").GetBoolean(),
            StrokeWidth = e.GetProperty("strokeWidth").GetDouble(),
            FontSize = e.GetProperty("fontSize").GetDouble(),
            Contents = Str(e, "contents"),
            Author = Str(e, "author"),
            Created = Date(Str(e, "created")),
            Modified = Date(Str(e, "modified")),
            SourceIndex = Math.Max(-1, Int(e, "sourceIndex")),
        };
    }

    private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new FormatException("A text is not a string.");

    private static string Str(JsonElement e, string name) => Text(e.GetProperty(name));

    private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new FormatException("A list is not an array.");

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) => Items(e.GetProperty(name));

    private static double[] Doubles(JsonElement e, string name) => Array(e, name).Select(n => n.GetDouble()).ToArray();

    /// <summary>"#RRGGBB" only (the store's colours are written back with <see cref="ViewerAnnotationPdf.Rgb"/>).</summary>
    private static string Colour(string s) =>
        s.Length == 7 && s[0] == '#' && s.Skip(1).All(char.IsAsciiHexDigit) ? s : throw new FormatException("A colour is not #RRGGBB.");

    private static DateTimeOffset Date(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : throw new FormatException("A date cannot be read.");
}
