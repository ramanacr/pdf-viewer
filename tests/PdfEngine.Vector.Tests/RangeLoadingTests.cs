using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Range loading: the chunk cache, the byte source over it, the planner (page to byte ranges),
/// and <see cref="PdfRangedDocument"/> opening and drawing pages of a file it reads by ranges,
/// with the fallback to a whole read for a damaged cross-reference table.
/// </summary>
public class RangeLoadingTests
{
    // ------------------------------------------------------------------ the test file

    /// <summary>
    /// A PDF of <paramref name="pages"/> pages, each with a line of text and its own image of
    /// <paramref name="imageSide"/>² RGB pixels (uncompressed), objects written page by page
    /// (content, image, page), the page tree and the catalog last, as many writers do.
    /// </summary>
    internal static byte[] LargePdf(int pages, int imageSide = 96, bool indirectLength = false)
    {
        var objects = new List<byte[]>();
        int Reserve() { objects.Add([]); return objects.Count; }
        void Set(int n, string head, byte[]? data = null)
        {
            var ms = new MemoryStream();
            var ascii = Encoding.ASCII;
            ms.Write(ascii.GetBytes(head));
            if (data != null)
            {
                ms.Write(ascii.GetBytes("\nstream\n"));
                ms.Write(data);
                ms.Write(ascii.GetBytes("\nendstream"));
            }
            objects[n - 1] = ms.ToArray();
        }
        int catalog = Reserve(), tree = Reserve(), font = Reserve();
        Set(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var pageIds = new List<int>();
        for (int p = 1; p <= pages; p++)
        {
            string text = $"BT /F1 24 Tf 72 720 Td (Page {p} of {pages}) Tj ET\nq 200 0 0 200 72 400 cm /Im1 Do Q\n";
            int content = Reserve();
            if (indirectLength)
            {
                int len = Reserve();
                Set(content, $"<< /Length {len} 0 R >>", Encoding.ASCII.GetBytes(text));
                Set(len, text.Length.ToString());
            }
            else
            {
                Set(content, $"<< /Length {text.Length} >>", Encoding.ASCII.GetBytes(text));
            }
            var pixels = new byte[imageSide * imageSide * 3];
            uint seed = (uint)p * 2654435761u;
            for (int i = 0; i < pixels.Length; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                pixels[i] = (byte)(seed >> 24);
            }
            int image = Reserve();
            Set(image, $"<< /Type /XObject /Subtype /Image /Width {imageSide} /Height {imageSide} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>", pixels);
            int page = Reserve();
            Set(page, $"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >> /Contents {content} 0 R >>");
            pageIds.Add(page);
        }
        Set(tree, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pages} >>");
        Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        int info = Reserve();
        Set(info, "<< /Title (Range test) /Producer (RangeLoadingTests) >>");

        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        var offsets = new long[objects.Count];
        // Write order: the shared font, then page by page, the tree and catalog last.
        var order = Enumerable.Range(3, objects.Count - 2).Append(2).Append(1);
        foreach (int n in order)
        {
            offsets[n - 1] = pdf.Position;
            W($"{n} 0 obj\n");
            pdf.Write(objects[n - 1]);
            W("\nendobj\n");
        }
        long xref = pdf.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long at in offsets) W($"{at:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static int ObjectNumberOfImage(PdfVectorDocument doc, int page)
    {
        var resources = doc.PageTree.Pages[page - 1].Resources;
        var xobjects = (PdfDictionary)doc.Resolver.Resolve(resources["XObject"])!;
        return ((PdfIndirectRef)xobjects["Im1"]!).ObjectNumber;
    }

    // ------------------------------------------------------------------ chunk cache

    [Fact]
    public void ChunkCache_StoresWholeBlocks_ReportsMissingRanges()
    {
        var cache = new PdfChunkCache(10_000, blockSize: 1024, maxBytes: 1 << 20);
        Assert.Equal(10, cache.BlockCount);
        Assert.Equal([new PdfByteRange(0, 10_000)], cache.Missing(new PdfByteRange(0, 10_000)));

        cache.Store(2048, new byte[2048]);                      // blocks 2 and 3
        Assert.True(cache.Contains(new PdfByteRange(2100, 4000)));
        Assert.False(cache.Contains(new PdfByteRange(2000, 2100)));
        Assert.Equal([new PdfByteRange(1024, 2048), new PdfByteRange(4096, 5120)], cache.Missing(new PdfByteRange(1500, 4500)));

        // The last block is short: it is kept when it reaches the end of the file.
        cache.Store(9216, new byte[784]);
        Assert.True(cache.Contains(new PdfByteRange(9216, 10_000)));
        Assert.Equal(2048 + 784, cache.CachedBytes);

        // A partial block in the middle is not kept; an unaligned store is refused.
        cache.Store(5120, new byte[1000]);
        Assert.False(cache.Contains(new PdfByteRange(5120, 5121)));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Store(100, new byte[1024]));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Store(9216, new byte[1024]));
    }

    [Fact]
    public void ChunkCache_TryCopy_IsAllOrNothing()
    {
        var cache = new PdfChunkCache(4096, blockSize: 1024);
        var data = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        cache.Store(0, data);
        var buffer = new byte[100];
        Assert.True(cache.TryCopy(1000, buffer));                // across two blocks
        Assert.Equal(data.Skip(1000).Take(100), buffer);
        var other = new byte[100];
        Assert.False(cache.TryCopy(2000, other));                // runs into block 2, not held
        Assert.All(other, b => Assert.Equal(0, b));
        Assert.False(cache.TryCopy(4000, new byte[200]));        // past the end
    }

    [Fact]
    public void ChunkCache_DropsLeastRecentlyUsed_ButKeepsTheCurrentOperation()
    {
        var cache = new PdfChunkCache(100 * 1024, blockSize: 1024, maxBytes: 4 * 1024);
        cache.BeginOperation();
        for (int b = 0; b < 4; b++) cache.Store(b * 1024L, new byte[1024]);
        Assert.Equal(4096, cache.CachedBytes);

        // A new operation: block 0 is used again, then two new blocks push out 1 and 2.
        cache.BeginOperation();
        Assert.True(cache.TryCopy(0, new byte[10]));
        cache.Store(10 * 1024, new byte[1024]);
        cache.Store(11 * 1024, new byte[1024]);
        Assert.Equal(4096, cache.CachedBytes);
        Assert.True(cache.Contains(new PdfByteRange(0, 1024)));
        Assert.False(cache.Contains(new PdfByteRange(1024, 2048)));
        Assert.False(cache.Contains(new PdfByteRange(2048, 3072)));
        Assert.True(cache.Contains(new PdfByteRange(3072, 4096)));
        Assert.Equal(2048, cache.EvictedBytes);

        // One operation larger than the cap keeps all of its blocks until the next one starts.
        cache.BeginOperation();
        for (int b = 20; b < 30; b++) cache.Store(b * 1024L, new byte[1024]);
        Assert.Equal(10 * 1024, cache.CachedBytes);
        cache.BeginOperation();
        Assert.Equal(4 * 1024, cache.CachedBytes);
    }

    [Fact]
    public void ChunkedSource_ReadsHeldBytes_AndThrowsWithTheRangeOtherwise()
    {
        var data = Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray();
        var cache = new PdfChunkCache(data.Length, blockSize: 1024);
        cache.Store(0, data.AsSpan(0, 4096));
        var source = new PdfChunkedByteSource(cache);
        source.Position = 4094;
        Assert.Equal(data[4094], source.ReadByte());
        Assert.Equal(data[4095], source.ReadByte());
        var miss = Assert.Throws<PdfDataNotLoadedException>(() => source.ReadByte());
        Assert.Equal(4096, miss.Offset);
        Assert.Equal(1, cache.MissCount);
        Assert.Equal(new PdfByteRange(4096, 4097), Assert.Single(cache.TakeMisses()));

        Assert.Equal(data.AsSpan(100, 50).ToArray(), source.ReadMemory(100, 50).ToArray());
        Assert.Throws<PdfDataNotLoadedException>(() => source.ReadMemory(4000, 200));

        var slice = source.Slice(1000, 100);
        Assert.Equal(100, slice.Length);
        Assert.Equal(data[1000], slice.ReadByte());
    }

    // ------------------------------------------------------------------ planner

    [Fact]
    public void Planner_ObjectExtentsRunToTheNextObject_AndRangesMergeAndSplit()
    {
        var bytes = LargePdf(3);
        using var doc = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        var planner = new PdfRangePlanner(doc.XrefTable, bytes.Length);
        int image = ObjectNumberOfImage(doc, 2);
        var extent = planner.ExtentOf(image)!.Value;
        string text = Encoding.ASCII.GetString(bytes, (int)extent.Start, (int)extent.Length);
        Assert.StartsWith($"{image} 0 obj", text);
        Assert.EndsWith("endobj\n", text);
        Assert.Equal(extent, planner.ExtentAt(extent.Start + 500));
        Assert.Null(planner.ExtentOf(99_999));

        Assert.Equal([new PdfByteRange(0, 30), new PdfByteRange(100, 120)],
            PdfRangePlanner.Merge([new PdfByteRange(100, 120), new PdfByteRange(0, 10), new PdfByteRange(5, 30)]));
        Assert.Equal([new PdfByteRange(0, 120)], PdfRangePlanner.Merge([new PdfByteRange(100, 120), new PdfByteRange(0, 30)], gap: 70));
        Assert.Equal([new PdfByteRange(10, 1024), new PdfByteRange(1024, 2048), new PdfByteRange(2048, 2500)],
            PdfRangePlanner.Split([new PdfByteRange(10, 2500)], 1024, 1024));
    }

    [Fact]
    public void Planner_PageRanges_HoldThatPagesObjectsOnly()
    {
        var bytes = LargePdf(6);
        using var doc = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        var planner = new PdfRangePlanner(doc.XrefTable, bytes.Length);
        var ranges = planner.PlanPage(doc.Resolver, doc.PageTree.Pages[3].Dictionary, doc.PageTree.PageObjectNumbers[3]);

        bool Covered(PdfByteRange r) => ranges.Any(x => x.Start <= r.Start && r.End <= x.End);
        Assert.True(Covered(planner.ExtentOf(doc.PageTree.PageObjectNumbers[3])!.Value));
        Assert.True(Covered(planner.ExtentOf(ObjectNumberOfImage(doc, 4))!.Value));
        // Not the other pages' images, nor the page tree above it (/Parent is not followed).
        Assert.False(Covered(planner.ExtentOf(ObjectNumberOfImage(doc, 3))!.Value));
        Assert.False(Covered(planner.ExtentOf(ObjectNumberOfImage(doc, 5))!.Value));
        int tree = ((PdfIndirectRef)doc.Catalog["Pages"]!).ObjectNumber;
        Assert.False(Covered(planner.ExtentOf(tree)!.Value));
        Assert.True(ranges.Sum(r => r.Length) < bytes.Length / 4);
    }

    // ------------------------------------------------------------------ ranged document

    [Fact]
    public async Task Ranged_OpensAndDrawsAPage_WithAFractionOfTheFile()
    {
        var bytes = LargePdf(60);
        var reader = new MemoryRangeReader(bytes);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 16 * 1024 });
        Assert.Equal(60, ranged.Document.PageCount);
        Assert.False(ranged.IsComplete);
        Assert.Null(ranged.FallbackReason);
        long afterOpen = reader.BytesRead;
        Assert.True(afterOpen < bytes.Length / 5, $"open read {afterOpen} of {bytes.Length} bytes");

        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        foreach (int page in new[] { 1, 37, 60 })
        {
            var (commands, pixels, text) = await ranged.RunAsync(page, async (d, ct) =>
            {
                var list = await d.GetPageDisplayListAsync(page, ct);
                var image = list.Commands.OfType<DrawImage>().Single();
                return (list.Commands.Count, image.Image.Source!.Decode(ct).Data.ToArray(), PdfTextExtractor.LoadPage(d, page, ct).Text);
            });
            var expected = await memory.GetPageDisplayListAsync(page);
            Assert.Equal(expected.Commands.Count, commands);
            Assert.Equal(expected.Commands.OfType<DrawImage>().Single().Image.Source!.Decode().Data.ToArray(), pixels);
            Assert.Equal(PdfTextExtractor.LoadPage(memory, page).Text, text);
        }
        Assert.Equal(0, ranged.Retries);
        Assert.True(reader.BytesRead < bytes.Length / 4, $"three pages read {reader.BytesRead} of {bytes.Length} bytes");
        Assert.Equal(reader.BytesRead, ranged.Statistics.BytesFetched);
    }

    [Fact]
    public async Task Ranged_FetchesWhatAnOperationMissed_AndRunsItAgain()
    {
        var bytes = LargePdf(20);
        var reader = new MemoryRangeReader(bytes);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 8 * 1024 });
        // Page 2's operation also reads page 15's image, which page 2's closure does not hold.
        int runs = 0;
        var data = await ranged.RunAsync(2, (d, ct) =>
        {
            runs++;
            var stream = (PdfStream)d.Resolver.Resolve(ObjectNumberOfImage(d, 15))!;
            return ValueTask.FromResult(stream.GetRawBytes().ToArray());
        });
        Assert.Equal(2, runs);
        Assert.Equal(1, ranged.Retries);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        Assert.Equal(((PdfStream)memory.Resolver.Resolve(ObjectNumberOfImage(memory, 15))!).GetRawBytes().ToArray(), data);
        Assert.False(ranged.IsComplete);
    }

    [Fact]
    public async Task Ranged_ContentStreamsWithIndirectLengths()
    {
        var bytes = LargePdf(12, indirectLength: true);
        var reader = new MemoryRangeReader(bytes);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 8 * 1024 });
        string text = await ranged.RunAsync(7, (d, ct) => ValueTask.FromResult(PdfTextExtractor.LoadPage(d, 7, ct).Text));
        Assert.Contains("Page 7 of 12", text);
        Assert.Null(ranged.FallbackReason);
    }

    [Fact]
    public async Task Ranged_ObjectStreamsAndCrossReferenceStream()
    {
        var plain = LargePdf(15);
        using var source = PdfVectorDocument.Open(new MemoryByteSource(plain));
        var packed = PdfEngine.Vector.Optimize.PdfOptimizer.Optimize(source, plain, new() { RecompressStreams = false }).Bytes;
        Assert.Contains("/ObjStm", Encoding.Latin1.GetString(packed));
        var reader = new MemoryRangeReader(packed);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 8 * 1024 });
        Assert.Equal(15, ranged.Document.PageCount);
        // The page dictionaries are packed in object streams and content streams wait for their
        // page: opening is a handful of reads, whatever the number of pages.
        Assert.True(reader.Requests <= 8, $"{reader.Requests} reads to open");
        string text = await ranged.RunAsync(9, (d, ct) => ValueTask.FromResult(PdfTextExtractor.LoadPage(d, 9, ct).Text));
        Assert.Contains("Page 9 of 15", text);
        Assert.Null(ranged.FallbackReason);
        Assert.True(reader.BytesRead < packed.Length / 2, $"read {reader.BytesRead} of {packed.Length} bytes");
    }

    [Fact]
    public async Task Ranged_EncryptedDocument_OpensWithItsPassword()
    {
        var plain = LargePdf(8);
        using var source = PdfVectorDocument.Open(new MemoryByteSource(plain));
        var encrypted = PdfEngine.Vector.Security.PdfEncryptor.Encrypt(source, new() { UserPassword = "range" }).Bytes;
        await Assert.ThrowsAsync<PdfEngine.Vector.Diagnostics.PdfEncryptedDocumentException>(
            () => PdfRangedDocument.OpenAsync(new MemoryRangeReader(encrypted), new PdfRangedOptions { TailBytes = 4096 }));
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(encrypted), new PdfRangedOptions { TailBytes = 4096 }, password: "range");
        string text = await ranged.RunAsync(5, (d, ct) => ValueTask.FromResult(PdfTextExtractor.LoadPage(d, 5, ct).Text));
        Assert.Contains("Page 5 of 8", text);
    }

    [Fact]
    public async Task Ranged_BrokenCrossReference_FallsBackToTheWholeFile()
    {
        var bytes = LargePdf(10);
        // startxref points into the middle of an image: the parser can only rebuild the table by scanning.
        string text = Encoding.Latin1.GetString(bytes);
        int at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        int end = text.IndexOf('\n', at);
        var broken = Encoding.Latin1.GetBytes(text[..at] + "1234".PadLeft(end - at, '0') + text[end..]);

        var reader = new MemoryRangeReader(broken);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 4096 });
        Assert.NotNull(ranged.FallbackReason);
        Assert.True(ranged.IsComplete);
        Assert.True(ranged.Document.WasRepaired);
        Assert.Equal(10, ranged.Document.PageCount);
        Assert.Equal(broken, ranged.Bytes);
        string page = await ranged.RunAsync(4, (d, ct) => ValueTask.FromResult(PdfTextExtractor.LoadPage(d, 4, ct).Text));
        Assert.Contains("Page 4 of 10", page);
    }

    [Fact]
    public async Task Ranged_NotAPdf_Throws()
    {
        var junk = Encoding.ASCII.GetBytes(new string('x', 50_000));
        await Assert.ThrowsAnyAsync<Exception>(() => PdfRangedDocument.OpenAsync(new MemoryRangeReader(junk)));
    }

    [Fact]
    public async Task Ranged_ReadAll_CopiesFetchedBlocks_AndCompletes()
    {
        var bytes = LargePdf(30);
        var reader = new MemoryRangeReader(bytes);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 8 * 1024, CompletionRequestBytes = 64 * 1024 });
        await ranged.RunAsync(1, async (d, ct) => (await d.GetPageDisplayListAsync(1, ct)).Commands.Count);
        long before = reader.BytesRead;
        var progress = new List<long>();
        var all = await ranged.CompleteAsync();
        Assert.Equal(bytes, all);
        Assert.True(ranged.IsComplete);
        // What was fetched for opening and page 1 is not fetched again.
        Assert.True(reader.BytesRead < bytes.Length + before / 2, $"read {reader.BytesRead} for {bytes.Length}");
        int requests = reader.Requests;
        Assert.Equal(30, ranged.Document.PageCount);
        await ranged.RunAsync(20, async (d, ct) => (await d.GetPageDisplayListAsync(20, ct)).Commands.Count);
        Assert.Equal(requests, reader.Requests);  // complete: nothing more is fetched
    }

    [Fact]
    public async Task Ranged_SmallCache_StillDrawsEveryPage()
    {
        var bytes = LargePdf(25, imageSide: 128);
        var reader = new MemoryRangeReader(bytes);
        using var ranged = await PdfRangedDocument.OpenAsync(reader,
            new PdfRangedOptions { TailBytes = 8 * 1024, CacheBytes = 200 * 1024, BlockSize = 2048 });
        for (int page = 1; page <= 25; page += 4)
        {
            int images = await ranged.RunAsync(page, async (d, ct) =>
            {
                var list = await d.GetPageDisplayListAsync(page, ct);
                return list.Commands.OfType<DrawImage>().Count(c => c.Image.Source!.Decode(ct).Data.Length == 128 * 128 * 4);
            });
            Assert.Equal(1, images);
        }
        Assert.True(ranged.Cache.CachedBytes <= 400 * 1024, $"cache holds {ranged.Cache.CachedBytes}");
    }
}
