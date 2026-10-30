using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;

namespace PdfEngine.Vector.Create;

/// <summary>The document information a new document is written with (ISO 32000-2 14.3.3).</summary>
public sealed record PdfCreateInfo
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Creator { get; init; }
    public string Producer { get; init; } = "PdfEngine.Vector";
    /// <summary>The creation and modification date; the current time when null. With a fixed date, the same pages are written as the same bytes.</summary>
    public DateTime? Date { get; init; }
}

/// <summary>
/// Writes a new document straight to a stream, an object at a time: each page is finished and
/// written before the next one is given, so a document of a thousand scans never holds more than
/// one of them in memory. Compressed data goes out as it is compressed, its /Length following in
/// an object of its own. The page tree, catalog, information dictionary and cross-reference table
/// are written by <see cref="Finish"/>.
/// </summary>
public sealed class PdfDocumentWriter : IDisposable
{
    private readonly CountingStream _out;
    private readonly List<long> _offsets = new() { 0 };
    private readonly List<int> _pages = new();
    private readonly Dictionary<string, int> _profiles = new(StringComparer.Ordinal);
    private bool _finished;

    /// <summary>The page tree's object number, for a page's /Parent.</summary>
    public int PagesNumber { get; }

    public int PageCount => _pages.Count;

    /// <summary>Images written as the JPEG files they came from.</summary>
    public int JpegImages { get; private set; }

    /// <summary>Images compressed losslessly.</summary>
    public int LosslessImages { get; private set; }

    public PdfDocumentWriter(Stream output)
    {
        _out = new CountingStream(output ?? throw new ArgumentNullException(nameof(output)));
        // 1.7: 16-bit samples and object-level features of 1.5 and later are all allowed.
        Raw("%PDF-1.7\n%âãÏÓ\n");
        PagesNumber = Reserve();
    }

    /// <summary>A number for an object written later.</summary>
    public int Reserve()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    public int Add(PdfObject obj)
    {
        int n = Reserve();
        Write(n, obj);
        return n;
    }

    public void Write(int number, PdfObject obj)
    {
        Begin(number);
        PdfObjectWriter.Write(_out, obj);
        Raw("\nendobj\n");
    }

    /// <summary>A stream whose data is deflated as it is written; returns its number.</summary>
    public int AddDeflated(IReadOnlyDictionary<string, PdfObject> entries, Action<Stream> writeData)
    {
        int n = Reserve(), length = Reserve();
        Begin(n);
        var dict = new Dictionary<string, PdfObject>(entries) { ["Filter"] = new PdfName("FlateDecode"), ["Length"] = new PdfIndirectRef(length) };
        PdfObjectWriter.Write(_out, new PdfDictionary(dict));
        Raw("\nstream\n");
        long start = _out.Count;
        using (var z = new ZLibStream(_out, CompressionLevel.Optimal, leaveOpen: true)) writeData(z);
        long written = _out.Count - start;
        Raw("\nendstream\nendobj\n");
        Write(length, new PdfInteger(written));
        return n;
    }

    /// <summary>Adds a page of the given size, drawing <paramref name="content"/> with <paramref name="resources"/>.</summary>
    public int AddPage(double width, double height, byte[] content, PdfDictionary resources) =>
        AddPage(width, height, content, resources, null);

    /// <summary>
    /// Adds a page as <see cref="AddPage(double, double, byte[], PdfDictionary)"/> does, with more entries in
    /// its dictionary, such as /Annots (whose objects can be written before or after the page, with numbers
    /// from <see cref="Reserve"/>). The writer's own entries (/Type, /Parent, /MediaBox, /Resources, /Contents) win.
    /// </summary>
    public int AddPage(double width, double height, byte[] content, PdfDictionary resources, IReadOnlyDictionary<string, PdfObject>? entries)
    {
        if (_finished) throw new InvalidOperationException("The document is already finished.");
        int contents = content.Length > 512
            ? AddDeflated(new Dictionary<string, PdfObject>(), s => s.Write(content))
            : Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), content));
        var pageEntries = entries != null ? new Dictionary<string, PdfObject>(entries) : new Dictionary<string, PdfObject>();
        pageEntries["Type"] = new PdfName("Page");
        pageEntries["Parent"] = new PdfIndirectRef(PagesNumber);
        pageEntries["MediaBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), Number(width), Number(height) });
        pageEntries["Resources"] = resources;
        pageEntries["Contents"] = new PdfIndirectRef(contents);
        int page = Add(new PdfDictionary(pageEntries));
        _pages.Add(page);
        return page;
    }

    /// <summary>A page holding one image, placed as <paramref name="options"/> say.</summary>
    public int AddImagePage(PdfCreateImage image, PdfImagePageOptions options)
    {
        var place = PdfImagePlacement.For(image, options);
        int xobject = AddImage(image);
        var (a, b, c, d, e, f) = place.Matrix(image.Orientation);
        var content = new StringBuilder("q\n");
        if (place.Clip)
        {
            double m = Math.Clamp(options.Margin, 0, Math.Min(place.PageWidth, place.PageHeight) / 2 - 1);
            content.Append(F(m)).Append(' ').Append(F(m)).Append(' ').Append(F(place.PageWidth - 2 * m)).Append(' ').Append(F(place.PageHeight - 2 * m)).Append(" re W n\n");
        }
        content.Append(F(a)).Append(' ').Append(F(b)).Append(' ').Append(F(c)).Append(' ').Append(F(d)).Append(' ')
               .Append(F(e)).Append(' ').Append(F(f)).Append(" cm\n/Im0 Do\nQ\n");
        var resources = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Im0"] = new PdfIndirectRef(xobject) }),
        });
        return AddPage(place.PageWidth, place.PageHeight, Encoding.ASCII.GetBytes(content.ToString()), resources);
    }

    /// <summary>Writes an image XObject (with its soft mask and colour profile); returns its number.</summary>
    public int AddImage(PdfCreateImage image)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentException("An image needs pixels.", nameof(image));
        int? smask = null;
        if (image.Alpha is { } alpha)
        {
            smask = AddSamples(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image"),
                ["Width"] = new PdfInteger(image.Width), ["Height"] = new PdfInteger(image.Height),
                ["ColorSpace"] = new PdfName("DeviceGray"), ["BitsPerComponent"] = new PdfInteger(8),
            }, alpha, image.Width, 1, 8);
        }

        var entries = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image"),
            ["Width"] = new PdfInteger(image.Width), ["Height"] = new PdfInteger(image.Height),
            ["ColorSpace"] = ColorSpace(image),
            ["BitsPerComponent"] = new PdfInteger(image.Kind == PdfImageData.Jpeg ? 8 : image.BitsPerComponent),
        };
        if (smask is int sm) entries["SMask"] = new PdfIndirectRef(sm);

        if (image.Kind == PdfImageData.Jpeg)
        {
            entries["Filter"] = new PdfName("DCTDecode");
            if (image.InvertedCmyk && image.Color == PdfImageColor.Cmyk)
                entries["Decode"] = new PdfArray(Enumerable.Range(0, 8).Select(i => (PdfObject)new PdfInteger(i % 2 == 0 ? 1 : 0)).ToList());
            JpegImages++;
            return Add(new PdfStream(new PdfDictionary(entries), 0, image.Data.Length, null, image.Data));
        }

        if (image.BitsPerComponent is not (1 or 2 or 4 or 8 or 16)) throw new ArgumentException("Samples are 1, 2, 4, 8 or 16 bits.", nameof(image));
        long expected = ((long)image.Width * image.Components * image.BitsPerComponent + 7) / 8 * image.Height;
        if (image.Data.Length < expected) throw new ArgumentException("The image has fewer samples than its size needs.", nameof(image));
        LosslessImages++;
        return AddSamples(entries, image.Data, image.Width, image.Components, image.BitsPerComponent);
    }

    private int AddSamples(Dictionary<string, PdfObject> entries, ReadOnlyMemory<byte> data, int width, int colors, int bpc)
    {
        entries["DecodeParms"] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Predictor"] = new PdfInteger(15), ["Colors"] = new PdfInteger(colors),
            ["BitsPerComponent"] = new PdfInteger(bpc), ["Columns"] = new PdfInteger(width),
        });
        return AddDeflated(entries, s => PngPredictor.Encode(data.Span, width, colors, bpc, s));
    }

    private PdfObject ColorSpace(PdfCreateImage image)
    {
        var baseColor = image.Palette != null && image.Color == PdfImageColor.Cmyk ? PdfImageColor.Rgb : image.Color;
        int n = baseColor switch { PdfImageColor.Gray => 1, PdfImageColor.Rgb => 3, _ => 4 };
        PdfObject space = new PdfName(baseColor switch { PdfImageColor.Gray => "DeviceGray", PdfImageColor.Rgb => "DeviceRGB", _ => "DeviceCMYK" });
        if (image.IccProfile is { } icc && IccMatches(icc, n))
        {
            string key = Convert.ToHexString(SHA256.HashData(icc));
            if (!_profiles.TryGetValue(key, out int profile))
            {
                profile = AddDeflated(new Dictionary<string, PdfObject> { ["N"] = new PdfInteger(n), ["Alternate"] = space }, s => s.Write(icc));
                _profiles[key] = profile;
            }
            space = new PdfArray(new PdfObject[] { new PdfName("ICCBased"), new PdfIndirectRef(profile) });
        }
        if (image.Palette is { } palette)
        {
            int entriesCount = palette.Length / n;
            if (entriesCount < 1 || entriesCount > 256) throw new ArgumentException("A palette has 1 to 256 entries.", nameof(image));
            return new PdfArray(new PdfObject[] { new PdfName("Indexed"), space, new PdfInteger(entriesCount - 1), new PdfString(palette.AsMemory(0, entriesCount * n).ToArray(), true) });
        }
        return space;
    }

    /// <summary>An ICC profile whose data colour space (header bytes 16-19) has <paramref name="n"/> components.</summary>
    internal static bool IccMatches(byte[] icc, int n)
    {
        if (icc.Length < 132 || icc[36] != 'a' || icc[37] != 'c' || icc[38] != 's' || icc[39] != 'p') return false;
        string space = Encoding.ASCII.GetString(icc, 16, 4);
        return n switch { 1 => space == "GRAY", 3 => space == "RGB ", 4 => space == "CMYK", _ => false };
    }

    /// <summary>Writes the page tree, the catalog, the information dictionary and the cross-reference table.</summary>
    public void Finish(PdfCreateInfo? info = null) => Finish(info, null);

    /// <summary>
    /// Finishes the document as <see cref="Finish(PdfCreateInfo?)"/> does, with more entries in its catalog,
    /// such as /Outlines, /AcroForm or /PageMode. /Type and /Pages are the writer's own.
    /// </summary>
    public void Finish(PdfCreateInfo? info, IReadOnlyDictionary<string, PdfObject>? catalogEntries)
    {
        if (_finished) throw new InvalidOperationException("The document is already finished.");
        if (_pages.Count == 0) throw new InvalidOperationException("A document needs at least one page.");
        _finished = true;
        info ??= new PdfCreateInfo();
        Write(PagesNumber, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = new PdfArray(_pages.Select(p => (PdfObject)new PdfIndirectRef(p)).ToList()),
            ["Count"] = new PdfInteger(_pages.Count),
        }));
        var catalogDict = catalogEntries != null ? new Dictionary<string, PdfObject>(catalogEntries) : new Dictionary<string, PdfObject>();
        catalogDict["Type"] = new PdfName("Catalog");
        catalogDict["Pages"] = new PdfIndirectRef(PagesNumber);
        if (!string.IsNullOrWhiteSpace(info.Title) && !catalogDict.ContainsKey("ViewerPreferences"))
            catalogDict["ViewerPreferences"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["DisplayDocTitle"] = PdfBoolean.True });
        int catalog = Add(new PdfDictionary(catalogDict));

        string now = PdfTextStrings.FormatDate(info.Date ?? DateTime.Now);
        var infoEntries = new Dictionary<string, PdfObject>();
        void Text(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) infoEntries[key] = PdfObjectWriter.TextString(value); }
        Text("Title", info.Title);
        Text("Author", info.Author);
        Text("Subject", info.Subject);
        Text("Creator", info.Creator);
        Text("Producer", info.Producer);
        infoEntries["CreationDate"] = PdfObjectWriter.TextString(now);
        infoEntries["ModDate"] = PdfObjectWriter.TextString(now);
        int infoNumber = Add(new PdfDictionary(infoEntries));

        for (int n = 1; n < _offsets.Count; n++)
            if (_offsets[n] < 0) Write(n, PdfNull.Instance); // reserved and never used: still a valid object

        byte[] id = _out.Hash();
        long xref = _out.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(_offsets.Count).Append("\n0000000000 65535 f \n");
        for (int n = 1; n < _offsets.Count; n++) sb.Append(_offsets[n].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n");
        Raw(sb.ToString());
        PdfObjectWriter.Write(_out, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Size"] = new PdfInteger(_offsets.Count),
            ["Root"] = new PdfIndirectRef(catalog),
            ["Info"] = new PdfIndirectRef(infoNumber),
            ["ID"] = new PdfArray(new PdfObject[] { new PdfString(id, true), new PdfString(id, true) }),
        }));
        Raw($"\nstartxref\n{xref}\n%%EOF\n");
        _out.Flush();
    }

    private void Begin(int number)
    {
        if (number <= 0 || number >= _offsets.Count) throw new ArgumentOutOfRangeException(nameof(number));
        if (_offsets[number] >= 0) throw new InvalidOperationException($"Object {number} is already written.");
        _offsets[number] = _out.Count;
        Raw($"{number} 0 obj\n");
    }

    private void Raw(string s) => _out.Write(Encoding.Latin1.GetBytes(s));

    private static PdfObject Number(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(Math.Round(v, 4));

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));

    public void Dispose() => _out.Dispose();

    /// <summary>Counts and hashes what is written (the offsets and the file identifier), passing it on.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        public long Count { get; private set; }
        public byte[] Hash() => _md5.GetCurrentHash();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _md5.AppendData(buffer);
            Count += buffer.Length;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _md5.Dispose();
            base.Dispose(disposing);
        }
    }
}
