using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The lazy page tree of a document read by ranges (<see cref="PdfPageTree.IsLazy"/>): a page
/// needs only the tree nodes on its path, and a tree that does not match its /Count values (a
/// lie, a loop, a tree too deep) is read whole instead, as without the lazy tree.
/// </summary>
public class LazyPageTreeTests
{
    // ------------------------------------------------------------------ test files

    /// <summary>
    /// Writes objects 1..n (object 1 is the catalog) in number order, with a classic
    /// cross-reference table. A body that starts with "stream:" is a content stream of the rest.
    /// </summary>
    private static byte[] Write(IReadOnlyList<string> objects, int padding = 0)
    {
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = pdf.Position;
            string body = objects[i];
            if (body.StartsWith("stream:", StringComparison.Ordinal))
            {
                string data = body["stream:".Length..];
                body = $"<< /Length {data.Length} >>\nstream\n{data}\nendstream";
            }
            W($"{i + 1} 0 obj\n{body}\nendobj\n");
            // Spreads the objects through the file, as images between page dictionaries do.
            if (padding > 0) W("%" + new string('x', padding) + "\n");
        }
        long xref = pdf.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long at in offsets) W($"{at:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>A builder of small trees: objects are numbered as they are added; object 1 is the catalog, 2 the font.</summary>
    private sealed class Tree
    {
        public readonly List<string> Objects = ["<< /Type /Catalog /Pages ROOT >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        private int _pages;

        public int Reserve()
        {
            Objects.Add("");
            return Objects.Count;
        }

        /// <summary>A page with its text "Page n" (n counts the pages added); its /Parent is not written (it is not read).</summary>
        public int Page(string extra = "")
        {
            int content = Reserve();
            Objects[content - 1] = $"stream:BT /F1 24 Tf 72 720 Td (Page {++_pages}) Tj ET";
            int page = Reserve();
            Objects[page - 1] = $"<< /Type /Page {extra} /Resources << /Font << /F1 2 0 R >> >> /Contents {content} 0 R >>";
            return page;
        }

        public void Node(int number, IEnumerable<int> kids, int count, string extra = "") =>
            Objects[number - 1] = $"<< /Type /Pages {extra} /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {count} >>";

        public byte[] Build(int root, int padding = 0)
        {
            Objects[0] = Objects[0].Replace("ROOT", $"{root} 0 R");
            return Write(Objects, padding);
        }
    }

    /// <summary>
    /// A balanced tree of <paramref name="pages"/> pages, <paramref name="fanout"/> kids per node,
    /// each node written before its kids (so nodes are spread through the file). Every other
    /// subtree of the root sets its own /MediaBox and /Rotate for its pages to inherit.
    /// </summary>
    private static byte[] Balanced(int pages, int fanout, int padding = 2048)
    {
        var tree = new Tree();
        int Build(int lo, int hi, int depth, int index)
        {
            if (hi - lo == 1) return tree.Page(depth == 0 ? "/MediaBox [0 0 612 792]" : "");
            int node = tree.Reserve();
            int size = (int)Math.Ceiling((hi - lo) / (double)fanout);
            var kids = new List<int>();
            for (int s = lo, i = 0; s < hi; s += size, i++) kids.Add(Build(s, Math.Min(hi, s + size), depth + 1, i));
            string extra = depth == 0 ? "/MediaBox [0 0 612 792]" : depth == 1 && index % 2 == 1 ? "/MediaBox [0 0 400 600] /Rotate 90" : "";
            tree.Node(node, kids, hi - lo, extra);
            return node;
        }
        int root = Build(0, pages, 0, 0);
        return tree.Build(root, padding);
    }

    private static async Task<string> TextOf(PdfRangedDocument ranged, int page) =>
        await ranged.RunAsync(page, (d, ct) => ValueTask.FromResult(PdfTextExtractor.LoadPage(d, page, ct).Text));

    // ------------------------------------------------------------------ fewer reads

    [Fact]
    public async Task SpreadOutFile_FirstPage_NeedsAHandfulOfReads_NotOnePerPage()
    {
        // 200 page dictionaries, each after its own 27 KB image: farther apart than reads are joined.
        var bytes = RangeLoadingTests.LargePdf(200);
        var options = new PdfRangedOptions { TailBytes = 16 * 1024 };

        var lazyReader = new MemoryRangeReader(bytes);
        using (var ranged = await PdfRangedDocument.OpenAsync(lazyReader, options))
        {
            Assert.Equal(200, ranged.Document.PageCount);   // the root's /Count, at once
            Assert.True(ranged.Document.PageTree.IsLazy);
            Assert.NotNull(ranged.Document.PageTree.LoadedPage(0));
            Assert.Null(ranged.Document.PageTree.LoadedPage(1));
            Assert.Contains("Page 1 of 200", await TextOf(ranged, 1));
            Assert.Null(ranged.PageTreeFallbackReason);
            Assert.Null(ranged.FallbackReason);
            Assert.True(lazyReader.Requests <= 6, $"{lazyReader.Requests} reads to show page 1");
            Assert.True(lazyReader.BytesRead < bytes.Length / 20, $"{lazyReader.BytesRead} of {bytes.Length} bytes to show page 1");

            // Another page costs its own reads only: one for the tree (the root has a kid per page), the rest its objects.
            int before = lazyReader.Requests;
            Assert.Contains("Page 137 of 200", await TextOf(ranged, 137));
            Assert.True(lazyReader.Requests - before <= 3, $"{lazyReader.Requests - before} reads for page 137");
            Assert.Null(ranged.Document.PageTree.LoadedPage(99));
        }

        // Without the lazy tree, opening reads every page's dictionary.
        var fullReader = new MemoryRangeReader(bytes);
        using (var ranged = await PdfRangedDocument.OpenAsync(fullReader, new PdfRangedOptions { TailBytes = 16 * 1024, LazyPageTree = false }))
        {
            Assert.False(ranged.Document.PageTree.IsLazy);
            Assert.Contains("Page 1 of 200", await TextOf(ranged, 1));
            Assert.True(fullReader.Requests >= 150, $"{fullReader.Requests} reads without the lazy tree");
        }
    }

    [Fact]
    public async Task BalancedTree_EveryPage_MatchesTheFullWalk()
    {
        var bytes = Balanced(100, fanout: 4);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        Assert.Equal(100, memory.PageCount);
        var reader = new MemoryRangeReader(bytes);
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 4096, ReadAheadBytes = 4096, MergeGapBytes = 0 });
        Assert.Equal(100, ranged.Document.PageCount);
        // Page 1: the catalog, the root and three levels of nodes with their kids, read a level per round.
        Assert.True(reader.Requests <= 20, $"{reader.Requests} reads to open");
        Assert.True(reader.BytesRead < bytes.Length / 3, $"{reader.BytesRead} of {bytes.Length} bytes to open");

        for (int p = 1; p <= 100; p++)
        {
            var lazy = await ranged.FindPageAsync(p);
            var full = memory.PageTree.Pages[p - 1];
            Assert.Equal(full.MediaBox, lazy.MediaBox);
            Assert.Equal(full.CropBox, lazy.CropBox);
            Assert.Equal(full.RotationDegrees, lazy.RotationDegrees);
            Assert.Equal(memory.PageTree.PageObjectNumbers[p - 1], ranged.Document.PageTree.PageObjectNumbers[p - 1]);
            Assert.Equal(memory.PageTree.PageAncestors[p - 1], ranged.Document.PageTree.PageAncestors[p - 1]);
        }
        Assert.Contains(memory.PageTree.Pages, pg => pg.RotationDegrees == 90);   // inheritance was tested
        Assert.Equal("Page 77", (await TextOf(ranged, 77)).Trim());
        Assert.Null(ranged.PageTreeFallbackReason);
        Assert.False(ranged.IsComplete);
    }

    [Fact]
    public async Task CompleteFile_IsNotLazy_AndOpensAsBefore()
    {
        var bytes = Balanced(30, fanout: 3, padding: 0);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        Assert.False(memory.PageTree.IsLazy);
        Assert.Equal(30, memory.PageTree.Pages.Count(p => p != null));
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        await ranged.CompleteAsync();
        Assert.False(ranged.Document.PageTree.IsLazy);
        Assert.Equal(30, ranged.Document.PageCount);
    }

    // ------------------------------------------------------------------ hostile trees

    [Theory]
    [InlineData(2_000_000_000L)]   // far more pages than the file has objects: nothing is sized by it
    [InlineData(1L)]               // fewer pages than the root has kids
    [InlineData(-5L)]
    public async Task RootCountThatCannotBeTrue_ReadsTheWholeTree(long count)
    {
        var tree = new Tree();
        int root = tree.Reserve();
        var kids = Enumerable.Range(0, 6).Select(_ => tree.Page("/MediaBox [0 0 612 792]")).ToList();
        tree.Node(root, kids, 0);
        tree.Objects[root - 1] = tree.Objects[root - 1].Replace("/Count 0", $"/Count {count}");
        var bytes = tree.Build(root, padding: 1024);

        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.Contains("/Count", ranged.PageTreeFallbackReason);
        Assert.False(ranged.Document.PageTree.IsLazy);
        Assert.Equal(6, ranged.Document.PageCount);
        Assert.False(ranged.IsComplete);   // still read by ranges
        Assert.Equal("Page 4", (await TextOf(ranged, 4)).Trim());
    }

    [Fact]
    public async Task RootCountTooHigh_KidsThatAddUp()
    {
        // The root has as many pages as its kids say: it is checked at once.
        var flat = new Tree();
        int flatRoot = flat.Reserve();
        var flatKids = Enumerable.Range(0, 10).Select(_ => flat.Page("/MediaBox [0 0 612 792]")).ToList();
        flat.Node(flatRoot, flatKids, 12);
        using (var opened = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(flat.Build(flatRoot, padding: 1024)), new PdfRangedOptions { TailBytes = 4096 }))
        {
            Assert.NotNull(opened.PageTreeFallbackReason);
            Assert.Equal(10, opened.Document.PageCount);
        }
    }

    [Fact]
    public async Task CountTooHighBelowTheRoot_IsFoundWhenAPageUnderItIsAskedFor()
    {
        // B claims 7 pages and has 5: the root's /Count (5 + 7) matches its kids, so page 1 (under A)
        // is found lazily; page 12 (under B by the counts) shows the lie.
        var tree = new Tree();
        int root = tree.Reserve(), a = tree.Reserve();
        var aKids = Enumerable.Range(0, 5).Select(_ => tree.Page()).ToList();
        int b = tree.Reserve();
        var bKids = Enumerable.Range(0, 5).Select(_ => tree.Page()).ToList();
        tree.Node(a, aKids, 5);
        tree.Node(b, bKids, 7);
        tree.Node(root, [a, b], 12, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root, padding: 1024);

        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.Equal(12, ranged.Document.PageCount);   // what the root says, until a page shows otherwise
        Assert.Equal("Page 1", (await TextOf(ranged, 1)).Trim());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ranged.FindPageAsync(12));
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.Equal(10, ranged.Document.PageCount);
        Assert.Equal("Page 10", (await TextOf(ranged, 10)).Trim());
    }

    [Fact]
    public async Task IntermediateCountThatDoesNotMatchItsKids_ReadsTheWholeTree()
    {
        // Root /Count 6 = 2 + 4 as claimed by its kids, but A has four pages and claims two: page 3
        // would be B's first page by the counts, and is A's third by the tree.
        var tree = new Tree();
        int root = tree.Reserve(), a = tree.Reserve();
        var aKids = Enumerable.Range(0, 4).Select(_ => tree.Page()).ToList();
        int b = tree.Reserve();
        var bKids = Enumerable.Range(0, 4).Select(_ => tree.Page()).ToList();
        tree.Node(a, aKids, 2);
        tree.Node(b, bKids, 4);
        tree.Node(root, [a, b], 6, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root, padding: 1024);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        Assert.Equal(8, memory.PageCount);

        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        // The root's kids add up to its /Count; A's do not, and page 1 is under A.
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.Equal(8, ranged.Document.PageCount);
        Assert.Equal("Page 3", (await TextOf(ranged, 3)).Trim());
    }

    [Fact]
    public async Task KidsThatDoNotAddUpToTheRootCount_ReadTheWholeTree()
    {
        var tree = new Tree();
        int root = tree.Reserve(), a = tree.Reserve();
        var aKids = Enumerable.Range(0, 3).Select(_ => tree.Page()).ToList();
        int b = tree.Reserve();
        var bKids = Enumerable.Range(0, 3).Select(_ => tree.Page()).ToList();
        tree.Node(a, aKids, 3);
        tree.Node(b, bKids, 3);
        tree.Node(root, [a, b], 5, "/MediaBox [0 0 612 792]");   // 3 + 3 is not 5
        var bytes = tree.Build(root, padding: 1024);

        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.Equal(6, ranged.Document.PageCount);
        Assert.Equal("Page 6", (await TextOf(ranged, 6)).Trim());
    }

    [Fact]
    public async Task CyclicKids_AreFound_AndTheTreeIsReadWhole()
    {
        // B's second kid is the root again: by the counts, page 4 is under it.
        var tree = new Tree();
        int root = tree.Reserve(), a = tree.Reserve();
        var aKids = Enumerable.Range(0, 2).Select(_ => tree.Page()).ToList();
        int b = tree.Reserve();
        int p3 = tree.Page();
        tree.Node(a, aKids, 2);
        tree.Node(b, [p3, root], 2);
        tree.Node(root, [a, b], 4, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root, padding: 1024);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        Assert.Equal(3, memory.PageCount);   // the full walk skips a node it has seen

        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.Equal(4, ranged.Document.PageCount);
        Assert.Equal("Page 1", (await TextOf(ranged, 1)).Trim());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ranged.FindPageAsync(4));
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.Equal(3, ranged.Document.PageCount);
        Assert.Equal("Page 3", (await TextOf(ranged, 3)).Trim());
    }

    [Fact]
    public async Task ANodeThatIsItsOwnKid_IsALoop()
    {
        var tree = new Tree();
        int root = tree.Reserve();
        int p1 = tree.Page();
        int self = tree.Reserve();
        tree.Node(self, [self], 1);
        tree.Node(root, [p1, self], 2, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root, padding: 512);
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.Equal("Page 1", (await TextOf(ranged, 1)).Trim());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ranged.FindPageAsync(2));
        Assert.Contains("loop", ranged.PageTreeFallbackReason);
        Assert.Equal(1, ranged.Document.PageCount);
    }

    /// <summary>A chain of <paramref name="depth"/> /Pages nodes, each with one kid, above one page.</summary>
    private static byte[] Chain(int depth, int padding = 512)
    {
        var tree = new Tree();
        var nodes = Enumerable.Range(0, depth).Select(_ => tree.Reserve()).ToList();
        int page = tree.Page("/MediaBox [0 0 300 300]");
        for (int i = 0; i < depth; i++) tree.Node(nodes[i], [i + 1 < depth ? nodes[i + 1] : page], 1);
        return tree.Build(nodes[0], padding);
    }

    [Fact]
    public async Task ShallowChain_IsFoundLazily()
    {
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(Chain(5)), new PdfRangedOptions { TailBytes = 2048, ReadAheadBytes = 1024 });
        Assert.Null(ranged.PageTreeFallbackReason);
        Assert.True(ranged.Document.PageTree.IsLazy);
        Assert.Equal(300, ranged.Document.PageTree.Pages[0].MediaBox.Width);
        Assert.Equal(5, ranged.Document.PageTree.PageAncestors[0].Length);
    }

    [Fact]
    public async Task DeepChain_StopsAfterTheRoundLimit_AndTheWholeTreeIsRead()
    {
        // 40 levels, one round each: more than MaxRounds, within MaxPageTreeDepth.
        var bytes = Chain(40, padding: 4096);
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 2048, ReadAheadBytes = 512, BlockSize = 512 });
        Assert.Contains("rounds", ranged.PageTreeFallbackReason);
        Assert.False(ranged.Document.PageTree.IsLazy);
        Assert.Equal(1, ranged.Document.PageCount);
        Assert.Equal("Page 1", (await TextOf(ranged, 1)).Trim());
    }

    [Fact]
    public async Task TreeDeeperThanTheLimit_FailsAsTheFullWalkDoes()
    {
        var limits = new PdfSecurityLimits { MaxPageTreeDepth = 6 };
        var bytes = Chain(10);
        Assert.Throws<PdfResourceLimitException>(() => PdfVectorDocument.Open(new MemoryByteSource(bytes), limits: limits));
        // Lazily, the depth limit is a mismatch (no exception escapes the descent); the whole
        // tree is then read, and that walk refuses it as it does for a file in memory.
        await Assert.ThrowsAsync<PdfResourceLimitException>(() =>
            PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 64 * 1024 }, limits: limits));
    }

    [Fact]
    public async Task KidWithoutACount_ReadsTheWholeTree()
    {
        var tree = new Tree();
        int root = tree.Reserve(), a = tree.Reserve();
        var aKids = Enumerable.Range(0, 3).Select(_ => tree.Page()).ToList();
        tree.Objects[a - 1] = $"<< /Type /Pages /Kids [{string.Join(" ", aKids.Select(k => $"{k} 0 R"))}] >>";
        int p4 = tree.Page();
        tree.Node(root, [a, p4], 4, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root, padding: 512);
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.Equal(4, ranged.Document.PageCount);
        Assert.Equal("Page 4", (await TextOf(ranged, 4)).Trim());
    }

    [Fact]
    public async Task AKidListedTwice_ReadsTheWholeTree()
    {
        // The full walk reads p1 once (2 pages); the counts would make it pages 1 and 2.
        var tree = new Tree();
        int root = tree.Reserve();
        int p1 = tree.Page(), p2 = tree.Page();
        tree.Node(root, [p1, p1, p2], 3, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root, padding: 512);
        using var ranged = await PdfRangedDocument.OpenAsync(new MemoryRangeReader(bytes), new PdfRangedOptions { TailBytes = 4096 });
        Assert.Contains("twice", ranged.PageTreeFallbackReason);
        Assert.Equal(2, ranged.Document.PageCount);
        Assert.Equal("Page 2", (await TextOf(ranged, 2)).Trim());
    }

    /// <summary>B claims 7 pages and has 5 (see <see cref="CountTooHighBelowTheRoot_IsFoundWhenAPageUnderItIsAskedFor"/>), spread out.</summary>
    private static byte[] LieUnderB(int padding)
    {
        var tree = new Tree();
        int root = tree.Reserve(), a = tree.Reserve();
        var aKids = Enumerable.Range(0, 5).Select(_ => tree.Page()).ToList();
        int b = tree.Reserve();
        var bKids = Enumerable.Range(0, 5).Select(_ => tree.Page()).ToList();
        tree.Node(a, aKids, 5);
        tree.Node(b, bKids, 7);
        tree.Node(root, [a, b], 12, "/MediaBox [0 0 612 792]");
        return tree.Build(root, padding);
    }

    [Fact]
    public async Task AFailedReadWhileTheWholeTreeIsRead_KeepsTheLazyTree_AndTheNextTryWorks()
    {
        var bytes = LieUnderB(padding: 4096);
        bool down = false;
        var reader = new MemoryRangeReader(bytes)
        {
            BeforeRead = (_, _) => Volatile.Read(ref down) ? ValueTask.FromException(new IOException("network down")) : ValueTask.CompletedTask,
        };
        using var ranged = await PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 4096, ReadAheadBytes = 4096, MergeGapBytes = 0 });
        Assert.True(ranged.Document.PageTree.IsLazy);

        // Page 12 shows the lie; reading the whole tree then fails.
        Volatile.Write(ref down, true);
        await Assert.ThrowsAsync<IOException>(() => ranged.FindPageAsync(12));
        Assert.True(ranged.Document.PageTree.IsLazy);   // the document and the flag agree
        Assert.Null(ranged.PageTreeFallbackReason);
        Assert.Equal(12, ranged.Document.PageCount);

        // Back online: an operation that misses (so the document is parsed again) works, and the lie is found again.
        Volatile.Write(ref down, false);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        int otherContent = ((PdfEngine.Vector.Objects.PdfIndirectRef)memory.PageTree.Pages[8].Dictionary["Contents"]!).ObjectNumber;
        string text = await ranged.RunAsync(4, (d, ct) =>
        {
            _ = d.Resolver.Resolve(otherContent);
            return ValueTask.FromResult(PdfTextExtractor.LoadPage(d, 4, ct).Text);
        });
        Assert.Equal("Page 4", text.Trim());
        Assert.True(ranged.Retries > 0);
        Assert.True(ranged.Document.PageTree.IsLazy);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ranged.FindPageAsync(12));
        Assert.NotNull(ranged.PageTreeFallbackReason);
        Assert.False(ranged.Document.PageTree.IsLazy);
        Assert.Equal(10, ranged.Document.PageCount);
        Assert.Equal("Page 9", (await TextOf(ranged, 9)).Trim());
    }

    [Fact]
    public async Task AFailedReadOfPageOnesPath_FailsTheOpen_AtOnce()
    {
        var bytes = RangeLoadingTests.LargePdf(40);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        long page1 = memory.XrefTable.Entries[memory.PageTree.PageObjectNumbers[0]].ByteOffset;
        var reader = new MemoryRangeReader(bytes)
        {
            BeforeRead = (offset, length) => offset <= page1 && page1 < offset + length
                ? ValueTask.FromException(new IOException("network down"))
                : ValueTask.CompletedTask,
        };
        await Assert.ThrowsAsync<IOException>(() => PdfRangedDocument.OpenAsync(reader, new PdfRangedOptions { TailBytes = 16 * 1024 }));
        // The read error ends the open; the file is not read whole instead.
        Assert.True(reader.BytesRead < bytes.Length / 10, $"{reader.BytesRead} of {bytes.Length} bytes read");
    }

    [Fact]
    public void LazyTree_InMemory_ThrowsAMismatch_NotAnEndlessWalk()
    {
        // The tree itself, without the ranged document: a loop is refused within the depth limit.
        var tree = new Tree();
        int root = tree.Reserve();
        int p1 = tree.Page();
        tree.Node(root, [p1, root], 2, "/MediaBox [0 0 612 792]");
        var bytes = tree.Build(root);
        using var memory = PdfVectorDocument.Open(new MemoryByteSource(bytes));
        var lazy = new PdfPageTree(memory.Resolver) { IsLazy = true };
        lazy.Load(memory.Catalog);
        Assert.Equal(2, lazy.Count);
        Assert.NotNull(lazy.Pages[0]);
        Assert.Throws<PdfPageTreeMismatchException>(() => lazy.Pages[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => lazy.Pages[2]);
    }
}
