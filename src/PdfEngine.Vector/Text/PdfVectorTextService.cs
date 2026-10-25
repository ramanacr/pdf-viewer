using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Text;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Text;

/// <summary>
/// The native <see cref="IPdfTextService"/>: word segments, page text and search from
/// <see cref="PdfTextPage"/>, with the same results (and the same normalized-coordinate and
/// padding conventions) as the PDFium text service it replaces. It works on a
/// <see cref="PdfVectorDocument"/>; a document opened by another engine is read through a vector
/// document attached to it (<see cref="Attach"/>) or, failing that, opened from its file.
/// </summary>
public sealed class PdfVectorTextService : IPdfTextService
{
    private readonly ConditionalWeakTable<IPdfDocument, PdfVectorDocument> _attached = new();

    /// <summary>Reads <paramref name="document"/>'s text from <paramref name="vectorDocument"/> (the same file, opened by the vector engine).</summary>
    public void Attach(IPdfDocument document, PdfVectorDocument vectorDocument)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vectorDocument);
        _attached.AddOrUpdate(document, vectorDocument);
    }

    private PdfVectorDocument Resolve(IPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is PdfVectorDocument vector) return vector;
        if (_attached.TryGetValue(document, out var attached)) return attached;
        if (!string.IsNullOrEmpty(document.FilePath) && File.Exists(document.FilePath))
        {
            var opened = PdfVectorDocument.OpenAsync(document.FilePath).AsTask().GetAwaiter().GetResult();
            _attached.AddOrUpdate(document, opened);
            return opened;
        }
        throw new ArgumentException("The document has no vector document to read its text from; attach one first.", nameof(document));
    }

    private static PdfTextPage Load(PdfVectorDocument doc, int pageNumber, CancellationToken ct)
    {
        if (!doc.IsOpen) throw new ObjectDisposedException(nameof(doc));
        return PdfTextExtractor.LoadPage(doc, pageNumber, ct);
    }

    /// <summary>The page size PDFium reports (FPDF_GetPageWidthF/HeightF): the page box, turned by /Rotate.</summary>
    private static (double Width, double Height) DisplaySize(PdfTextPage page) =>
        page.Rotation is 90 or 270 ? (page.PageBox.Height, page.PageBox.Width) : (page.PageBox.Width, page.PageBox.Height);

    /// <summary>A UTF-16 unit as the PDFium service turned it into text (a lone surrogate reads as U+FFFD).</summary>
    private static string Unit(char c) => char.IsSurrogate(c) ? "�" : c.ToString();

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TextSegment>> ExtractTextSegmentsAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = Resolve(document);
        var segments = new List<TextSegment>();
        if (pageNumber < 1 || pageNumber > doc.PageCount) return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);
        var page = Load(doc, pageNumber, cancellationToken);
        var (pageW, pageH) = DisplaySize(page);
        double originX = page.PageBox.X, originY = page.PageBox.Y;
        if (page.CharCount == 0 || pageW <= 0 || pageH <= 0) return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);

        var word = new StringBuilder();
        int wordStart = -1;
        double minL = double.MaxValue, maxR = double.MinValue, minB = double.MaxValue, maxT = double.MinValue;

        void Flush()
        {
            if (word.Length == 0 || wordStart < 0) return;
            const double padY = 1.5, padH = 2.5, padX = 0.5;
            double normX = Math.Max(0.0, Math.Min(1.0, (minL - originX - padX) / pageW));
            double normY = Math.Max(0.0, Math.Min(1.0, 1.0 - ((maxT - originY + padY) / pageH)));
            double normW = Math.Max(0.001, Math.Min(1.0 - normX, (maxR - minL + (padX * 2)) / pageW));
            double normH = Math.Max(0.001, Math.Min(1.0 - normY, (maxT - minB + padH) / pageH));
            string text = word.ToString();
            segments.Add(new TextSegment
            {
                Text = text, PageNumber = pageNumber, StartIndex = wordStart, Length = text.Length,
                X = normX, Y = normY, Width = normW, Height = normH, FontSize = Math.Max(1.0, maxT - minB),
            });
            word.Clear();
            wordStart = -1;
            minL = double.MaxValue; maxR = double.MinValue; minB = double.MaxValue; maxT = double.MinValue;
        }

        for (int i = 0; i < page.CharCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string chars = Unit(page.GetUnicode(i));
            if (char.IsWhiteSpace(chars[0]) || char.IsControl(chars[0]))
            {
                Flush();
                continue;
            }
            var box = page.GetCharBox(i);
            if (word.Length == 0) wordStart = i;
            word.Append(chars);
            minL = Math.Min(minL, Math.Min(box.Left, box.Right));
            maxR = Math.Max(maxR, Math.Max(box.Left, box.Right));
            minB = Math.Min(minB, Math.Min(box.Bottom, box.Top));
            maxT = Math.Max(maxT, Math.Max(box.Bottom, box.Top));
        }
        Flush();
        return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);
    }

    /// <inheritdoc/>
    public ValueTask<string> ExtractPageTextAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var doc = Resolve(document);
        if (pageNumber < 1 || pageNumber > doc.PageCount) return ValueTask.FromResult(string.Empty);
        var page = Load(doc, pageNumber, cancellationToken);
        return ValueTask.FromResult(page.GetAllText().TrimEnd('\0'));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SearchMatch>> SearchTextAsync(IPdfDocument document, string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query)) return ValueTask.FromResult<IReadOnlyList<SearchMatch>>(results);
        options ??= new SearchOptions();
        var doc = Resolve(document);
        for (int p = 1; p <= doc.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = Load(doc, p, cancellationToken);
            var (pageW, pageH) = DisplaySize(page);
            double originX = page.PageBox.X, originY = page.PageBox.Y;
            string pageText = page.GetAllText();
            foreach (var (charIndex, count) in page.Find(query, options.MatchCase, options.MatchWholeWord))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count <= 0 || pageW <= 0 || pageH <= 0) continue;
                double minL = double.MaxValue, maxR = double.MinValue, minB = double.MaxValue, maxT = double.MinValue;
                for (int c = 0; c < count; c++)
                {
                    int ci = charIndex + c;
                    if (ci < 0 || ci >= page.CharCount) continue;
                    var box = page.GetCharBox(ci);
                    minL = Math.Min(minL, Math.Min(box.Left, box.Right));
                    maxR = Math.Max(maxR, Math.Max(box.Left, box.Right));
                    minB = Math.Min(minB, Math.Min(box.Bottom, box.Top));
                    maxT = Math.Max(maxT, Math.Max(box.Bottom, box.Top));
                }
                if (minL > maxR || minB > maxT) continue;
                const double padY = 1.5, padH = 2.5, padX = 1.0;
                double normX = Math.Max(0.0, Math.Min(1.0, (minL - originX - padX) / pageW));
                double normY = Math.Max(0.0, Math.Min(1.0, 1.0 - ((maxT - originY + padY) / pageH)));
                double normW = Math.Max(0.001, Math.Min(1.0 - normX, (maxR - minL + (padX * 2)) / pageW));
                double normH = Math.Max(0.001, Math.Min(1.0 - normY, (maxT - minB + padH) / pageH));
                int snipStart = Math.Max(0, charIndex - 30);
                int snipLen = Math.Min(pageText.Length - snipStart, count + 60);
                string snippet = pageText.Length >= snipStart + snipLen && snipLen > 0
                    ? pageText.Substring(snipStart, snipLen).Replace("\r", " ").Replace("\n", " ")
                    : query;
                results.Add(new SearchMatch
                {
                    PageNumber = p, Text = query, X = normX, Y = normY, Width = normW, Height = normH,
                    CharacterIndex = charIndex, MatchLength = count, ContextSnippet = snippet,
                });
                if (results.Count >= options.MaxResults) return ValueTask.FromResult<IReadOnlyList<SearchMatch>>(results);
            }
        }
        return ValueTask.FromResult<IReadOnlyList<SearchMatch>>(results);
    }
}
