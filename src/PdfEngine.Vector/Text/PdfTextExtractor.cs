using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Text;

/// <summary>
/// Builds <see cref="PdfTextPage"/>s for the pages of a <see cref="PdfVectorDocument"/>: the
/// native replacement of PDFium's FPDFText_LoadPage. Fonts are resolved once per document, and
/// recent pages are kept within a character budget, since search and selection read the same
/// pages repeatedly. <see cref="Prefetch"/> builds a page's text in the background (after the
/// page is drawn, say) so that the first selection or search on it does not wait.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>Pages always kept, whatever their size.</summary>
    private const int MinCachedPages = 8;
    private const int MaxCachedPages = 512;
    /// <summary>Characters kept over all cached pages of a document (about 130 bytes each, so some 50 MB).</summary>
    private const long MaxCachedChars = 400_000;

    private sealed class Context
    {
        public required PdfFontResolver Fonts { get; init; }
        public readonly Dictionary<PdfFont, TextFontInfo> FontInfos = new(ReferenceEqualityComparer.Instance);
        /// <summary>The substitute for fonts a page names but does not have (built once, used by every page).</summary>
        public TextFontInfo? Missing;
        public readonly LinkedList<(int Page, PdfTextPage Text)> Recent = new();
        public readonly Dictionary<int, LinkedListNode<(int Page, PdfTextPage Text)>> ByPage = new();
        public long CachedChars;
        /// <summary>Counts invalidations: a page built before one is not kept after it.</summary>
        public int Version;
        public readonly object Sync = new();
    }

    private static readonly ConditionalWeakTable<PdfVectorDocument, Context> Contexts = new();

    private static Context For(PdfVectorDocument doc) =>
        Contexts.GetValue(doc, d =>
        {
            // Fonts that are not embedded are measured on installed faces, found through an index
            // of every installed font: read now in the background, not when a page first needs it.
            TextFontInfo.PrefetchSystemFonts();
            return new Context { Fonts = new PdfFontResolver(d.Resolver, PdfSecurityLimits.Default) };
        });

    /// <summary>The text page of a page (1-based).</summary>
    public static PdfTextPage LoadPage(PdfVectorDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageNumber < 1 || pageNumber > document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var ctx = For(document);
        if (TryCached(ctx, pageNumber, out var cached)) return cached;

        document.BuildGate.Wait(cancellationToken);
        try
        {
            return BuildAndKeep(document, ctx, pageNumber, cancellationToken);
        }
        finally
        {
            document.BuildGate.Release();
        }
    }

    /// <summary><see cref="LoadPage"/> off the calling thread.</summary>
    public static Task<PdfTextPage> LoadPageAsync(PdfVectorDocument document, int pageNumber, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadPage(document, pageNumber, cancellationToken), cancellationToken);

    /// <summary>True when the page's text is built and kept (a load returns at once).</summary>
    public static bool IsLoaded(PdfVectorDocument document, int pageNumber) =>
        Contexts.TryGetValue(document, out var ctx) && TryCached(ctx, pageNumber, out _, touch: false);

    /// <summary>Drops the cached text pages of a document (after it was changed in memory), and its pending prefetches.</summary>
    public static void Invalidate(PdfVectorDocument document)
    {
        TextPrefetcher.Cancel(document);
        if (Contexts.TryGetValue(document, out var ctx))
        {
            lock (ctx.Sync)
            {
                ctx.Recent.Clear();
                ctx.ByPage.Clear();
                ctx.CachedChars = 0;
                ctx.Version++;
            }
        }
    }

    /// <summary>
    /// Builds the page's text in the background, at low priority, unless it is kept already. The
    /// queue is bounded (the most recent requests win), a build never waits for the document while
    /// a page is being drawn, and requests for a document that is closed or invalidated are dropped.
    /// The page is kept like any other, within the cache's character budget.
    /// </summary>
    public static void Prefetch(PdfVectorDocument document, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsOpen || pageNumber < 1 || pageNumber > document.PageCount) return;
        if (IsLoaded(document, pageNumber)) return;
        TextPrefetcher.Enqueue(document, pageNumber);
    }

    /// <summary>The prefetcher's build: the gate is taken only while nothing else holds it.</summary>
    internal static void BuildInBackground(PdfVectorDocument document, int pageNumber, Func<bool> stillWanted)
    {
        var ctx = For(document);
        if (TryCached(ctx, pageNumber, out _, touch: false)) return;
        while (!document.BuildGate.Wait(0))
        {
            if (!document.IsOpen || !stillWanted()) return;
            Thread.Sleep(5);
        }
        try
        {
            if (document.IsOpen && stillWanted()) BuildAndKeep(document, ctx, pageNumber, CancellationToken.None);
        }
        finally
        {
            document.BuildGate.Release();
        }
    }

    private static bool TryCached(Context ctx, int pageNumber, out PdfTextPage page, bool touch = true)
    {
        lock (ctx.Sync)
        {
            if (ctx.ByPage.TryGetValue(pageNumber, out var node))
            {
                if (touch)
                {
                    ctx.Recent.Remove(node);
                    ctx.Recent.AddFirst(node);
                }
                page = node.Value.Text;
                return true;
            }
        }
        page = null!;
        return false;
    }

    /// <summary>Builds the page (the caller holds the build gate) unless another caller just did, and keeps it.</summary>
    private static PdfTextPage BuildAndKeep(PdfVectorDocument document, Context ctx, int pageNumber, CancellationToken ct)
    {
        // Built while this caller waited for the gate (by the prefetcher, say).
        if (TryCached(ctx, pageNumber, out var cached)) return cached;
        int version;
        lock (ctx.Sync) version = ctx.Version;
        var page = Build(document, ctx, pageNumber, ct);
        lock (ctx.Sync)
        {
            if (ctx.Version == version && !ctx.ByPage.ContainsKey(pageNumber))
            {
                ctx.ByPage[pageNumber] = ctx.Recent.AddFirst((pageNumber, page));
                ctx.CachedChars += page.CharCount;
                while (ctx.Recent.Count > MinCachedPages && (ctx.Recent.Count > MaxCachedPages || ctx.CachedChars > MaxCachedChars))
                {
                    var last = ctx.Recent.Last!;
                    ctx.Recent.RemoveLast();
                    ctx.ByPage.Remove(last.Value.Page);
                    ctx.CachedChars -= last.Value.Text.CharCount;
                }
            }
        }
        return page;
    }

    /// <summary>The page's box as PDFium reads it: the crop box within the media box.</summary>
    public static PdfRect PageBox(PdfPageNode node)
    {
        var media = node.MediaBox;
        var crop = node.CropBox;
        double l = Math.Max(media.X, crop.X), b = Math.Max(media.Y, crop.Y);
        double r = Math.Min(media.X + media.Width, crop.X + crop.Width), t = Math.Min(media.Y + media.Height, crop.Y + crop.Height);
        return r > l && t > b ? new PdfRect(l, b, r - l, t - b) : crop;
    }

    private static PdfTextPage Build(PdfVectorDocument document, Context ctx, int pageNumber, CancellationToken ct)
    {
        var node = document.PageTree.Pages[pageNumber - 1];
        var decoder = new PdfStreamDecoder(null, document.Resolver.Resolve);
        // The streams joined with a line break after each, into one array of the exact size.
        var parts = new byte[node.Contents.Count][];
        int length = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                parts[i] = decoder.DecodeStream(node.Contents[i]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // A damaged stream reads as nothing; the other streams of the page still count.
                parts[i] = Array.Empty<byte>();
            }
            length = checked(length + parts[i].Length + 1);
        }
        var content = new byte[length];
        int at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(content, at);
            at += part.Length;
            content[at++] = (byte)'\n';
        }

        TextFontInfo Info(PdfFont font, Objects.PdfDictionary? dict)
        {
            if (!ctx.FontInfos.TryGetValue(font, out var info))
                ctx.FontInfos[font] = info = new TextFontInfo(font, dict, document.Resolver);
            return info;
        }

        // A Helvetica substitute, as PDFium uses for a font name the resources do not have.
        TextFontInfo Missing() => ctx.Missing ??= Info(ctx.Fonts.ResolveFont("\0missing", null), null);

        TextHolder holder;
        try
        {
            holder = new TextContentReader(document.Resolver, ctx.Fonts, Info, Missing).Read(content, node.Resources);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            holder = new TextHolder();
        }
        ct.ThrowIfCancellationRequested();
        return new PdfTextPage(pageNumber, PageBox(node), node.RotationDegrees, holder);
    }
}
