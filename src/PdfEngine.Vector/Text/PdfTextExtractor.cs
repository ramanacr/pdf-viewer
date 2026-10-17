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
/// native replacement of PDFium's FPDFText_LoadPage. Fonts are resolved once per document, and a
/// few recent pages are kept, since search and selection read the same page repeatedly.
/// </summary>
public static class PdfTextExtractor
{
    private const int CachedPages = 8;

    private sealed class Context
    {
        public required PdfFontResolver Fonts { get; init; }
        public readonly Dictionary<PdfFont, TextFontInfo> FontInfos = new(ReferenceEqualityComparer.Instance);
        public readonly LinkedList<(int Page, PdfTextPage Text)> Recent = new();
        public readonly object Sync = new();
    }

    private static readonly ConditionalWeakTable<PdfVectorDocument, Context> Contexts = new();

    private static Context For(PdfVectorDocument doc) =>
        Contexts.GetValue(doc, d => new Context { Fonts = new PdfFontResolver(d.Resolver, PdfSecurityLimits.Default) });

    /// <summary>The text page of a page (1-based).</summary>
    public static PdfTextPage LoadPage(PdfVectorDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageNumber < 1 || pageNumber > document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var ctx = For(document);
        lock (ctx.Sync)
        {
            for (var node = ctx.Recent.First; node != null; node = node.Next)
            {
                if (node.Value.Page != pageNumber) continue;
                ctx.Recent.Remove(node);
                ctx.Recent.AddFirst(node);
                return node.Value.Text;
            }
        }

        document.BuildGate.Wait(cancellationToken);
        PdfTextPage page;
        try
        {
            page = Build(document, ctx, pageNumber, cancellationToken);
        }
        finally
        {
            document.BuildGate.Release();
        }

        lock (ctx.Sync)
        {
            ctx.Recent.AddFirst((pageNumber, page));
            while (ctx.Recent.Count > CachedPages) ctx.Recent.RemoveLast();
        }
        return page;
    }

    /// <summary><see cref="LoadPage"/> off the calling thread.</summary>
    public static Task<PdfTextPage> LoadPageAsync(PdfVectorDocument document, int pageNumber, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadPage(document, pageNumber, cancellationToken), cancellationToken);

    /// <summary>Drops the cached text pages of a document (after it was changed in memory).</summary>
    public static void Invalidate(PdfVectorDocument document)
    {
        if (Contexts.TryGetValue(document, out var ctx))
            lock (ctx.Sync) ctx.Recent.Clear();
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
        var content = new MemoryStream();
        foreach (var stream in node.Contents)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                content.Write(decoder.DecodeStream(stream));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // A damaged stream reads as nothing; the other streams of the page still count.
            }
            content.WriteByte((byte)'\n');
        }

        TextFontInfo Info(PdfFont font, Objects.PdfDictionary? dict)
        {
            if (!ctx.FontInfos.TryGetValue(font, out var info))
                ctx.FontInfos[font] = info = new TextFontInfo(font, dict, document.Resolver);
            return info;
        }

        TextHolder holder;
        try
        {
            holder = new TextContentReader(document.Resolver, ctx.Fonts, Info).Read(content.ToArray(), node.Resources);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            holder = new TextHolder();
        }
        ct.ThrowIfCancellationRequested();
        return new PdfTextPage(pageNumber, PageBox(node), node.RotationDegrees, holder);
    }
}
