using System.Text;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Pdfium.Native;
using PdfEngine.Text;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Text extraction and sub-pixel search service using Google PDFium native text API.
/// </summary>
public sealed class PdfiumTextService : IPdfTextService
{
    public ValueTask<IReadOnlyList<TextSegment>> ExtractTextSegmentsAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            var segments = new List<TextSegment>();
            using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, pageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid) return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);

            using var textPage = PdfiumNativeBridge.FPDFText_LoadPage(pageHandle);
            if (textPage == null || textPage.IsInvalid) return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);

            float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
            float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

            int charCount = PdfiumNativeBridge.FPDFText_CountChars(textPage);
            if (charCount <= 0) return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);

            var wordBuilder = new StringBuilder();
            int wordStartIndex = -1;
            double wordMinL = double.MaxValue, wordMaxR = double.MinValue;
            double wordMinB = double.MaxValue, wordMaxT = double.MinValue;

            void FlushWord()
            {
                if (wordBuilder.Length > 0 && wordStartIndex >= 0)
                {
                    string wordText = wordBuilder.ToString();
                    double padY = 1.5;
                    double padH = 2.5;
                    double padX = 0.5;

                    double normX = Math.Max(0.0, Math.Min(1.0, (wordMinL - padX) / pageW));
                    double normY = Math.Max(0.0, Math.Min(1.0, 1.0 - ((wordMaxT + padY) / pageH)));
                    double normW = Math.Max(0.001, Math.Min(1.0 - normX, (wordMaxR - wordMinL + (padX * 2)) / pageW));
                    double normH = Math.Max(0.001, Math.Min(1.0 - normY, (wordMaxT - wordMinB + padH) / pageH));

                    segments.Add(new TextSegment
                    {
                        Text = wordText,
                        PageNumber = pageNumber,
                        StartIndex = wordStartIndex,
                        Length = wordText.Length,
                        X = normX,
                        Y = normY,
                        Width = normW,
                        Height = normH,
                        FontSize = Math.Max(1.0, wordMaxT - wordMinB)
                    });

                    wordBuilder.Clear();
                    wordStartIndex = -1;
                    wordMinL = double.MaxValue;
                    wordMaxR = double.MinValue;
                    wordMinB = double.MaxValue;
                    wordMaxT = double.MinValue;
                }
            }

            for (int i = 0; i < charCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                uint charCode = PdfiumNativeBridge.FPDFText_GetUnicode(textPage, i);
                char ch = (char)charCode;

                if (char.IsWhiteSpace(ch) || char.IsControl(ch))
                {
                    FlushWord();
                    continue;
                }

                if (PdfiumNativeBridge.FPDFText_GetCharBox(textPage, i, out double cl, out double cr, out double cb, out double ct) != 0)
                {
                    if (wordBuilder.Length == 0)
                    {
                        wordStartIndex = i;
                    }

                    wordBuilder.Append(ch);
                    wordMinL = Math.Min(wordMinL, Math.Min(cl, cr));
                    wordMaxR = Math.Max(wordMaxR, Math.Max(cl, cr));
                    wordMinB = Math.Min(wordMinB, Math.Min(cb, ct));
                    wordMaxT = Math.Max(wordMaxT, Math.Max(cb, ct));
                }
            }

            FlushWord();
            return ValueTask.FromResult<IReadOnlyList<TextSegment>>(segments);
        }
    }

    public ValueTask<string> ExtractPageTextAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, pageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid) return ValueTask.FromResult(string.Empty);

            using var textPage = PdfiumNativeBridge.FPDFText_LoadPage(pageHandle);
            if (textPage == null || textPage.IsInvalid) return ValueTask.FromResult(string.Empty);

            int charCount = PdfiumNativeBridge.FPDFText_CountChars(textPage);
            if (charCount <= 0) return ValueTask.FromResult(string.Empty);

            byte[] buf = new byte[(charCount + 1) * 2];
            int written = PdfiumNativeBridge.FPDFText_GetText(textPage, 0, charCount, buf);
            if (written > 0)
            {
                return ValueTask.FromResult(PdfiumNativeBridge.Utf16BytesToString(buf, written * 2));
            }

            return ValueTask.FromResult(string.Empty);
        }
    }

    public ValueTask<IReadOnlyList<SearchMatch>> SearchTextAsync(
        IPdfDocument document,
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(query)) return ValueTask.FromResult<IReadOnlyList<SearchMatch>>(results);

        options ??= new SearchOptions();
        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            uint flags = 0;
            if (options.MatchCase) flags |= PdfiumNativeBridge.FPDF_MATCHCASE;
            if (options.MatchWholeWord) flags |= PdfiumNativeBridge.FPDF_MATCHWHOLEWORD;

            byte[] queryUtf16 = PdfiumNativeBridge.StringToUtf16NullTerminated(query);

            for (int p = 0; p < pdfiumDoc.PageCount; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, p);
                if (pageHandle == null || pageHandle.IsInvalid) continue;

                using var textPage = PdfiumNativeBridge.FPDFText_LoadPage(pageHandle);
                if (textPage == null || textPage.IsInvalid) continue;

                float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
                float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

                int totalChars = PdfiumNativeBridge.FPDFText_CountChars(textPage);
                string pageFullText = string.Empty;
                if (totalChars > 0)
                {
                    byte[] pageBuf = new byte[(totalChars + 1) * 2];
                    int written = PdfiumNativeBridge.FPDFText_GetText(textPage, 0, totalChars, pageBuf);
                    if (written > 0)
                    {
                        pageFullText = PdfiumNativeBridge.Utf16BytesToString(pageBuf, written * 2);
                    }
                }

                using var searchHandle = PdfiumNativeBridge.FPDFText_FindStart(textPage, queryUtf16, flags, 0);
                if (searchHandle == null || searchHandle.IsInvalid) continue;

                while (PdfiumNativeBridge.FPDFText_FindNext(searchHandle) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int charIndex = PdfiumNativeBridge.FPDFText_GetSchResultIndex(searchHandle);
                    int count = PdfiumNativeBridge.FPDFText_GetSchCount(searchHandle);

                    if (count > 0 && pageW > 0 && pageH > 0)
                    {
                        double minL = double.MaxValue, maxR = double.MinValue;
                        double minB = double.MaxValue, maxT = double.MinValue;

                        for (int c = 0; c < count; c++)
                        {
                            int ci = charIndex + c;
                            if (PdfiumNativeBridge.FPDFText_GetCharBox(textPage, ci, out double cl, out double cr, out double cb, out double ct) != 0)
                            {
                                minL = Math.Min(minL, Math.Min(cl, cr));
                                maxR = Math.Max(maxR, Math.Max(cl, cr));
                                minB = Math.Min(minB, Math.Min(cb, ct));
                                maxT = Math.Max(maxT, Math.Max(cb, ct));
                            }
                        }

                        if (minL <= maxR && minB <= maxT)
                        {
                            double padY = 1.5;
                            double padH = 2.5;
                            double padX = 1.0;

                            double normX = Math.Max(0.0, Math.Min(1.0, (minL - padX) / pageW));
                            double normY = Math.Max(0.0, Math.Min(1.0, 1.0 - ((maxT + padY) / pageH)));
                            double normW = Math.Max(0.001, Math.Min(1.0 - normX, (maxR - minL + (padX * 2)) / pageW));
                            double normH = Math.Max(0.001, Math.Min(1.0 - normY, (maxT - minB + padH) / pageH));

                            int snipStart = Math.Max(0, charIndex - 30);
                            int snipLen = Math.Min(pageFullText.Length - snipStart, count + 60);
                            string snippet = pageFullText.Length >= (snipStart + snipLen) && snipLen > 0
                                ? pageFullText.Substring(snipStart, snipLen).Replace("\r", " ").Replace("\n", " ")
                                : query;

                            results.Add(new SearchMatch
                            {
                                PageNumber = p + 1,
                                Text = query,
                                X = normX,
                                Y = normY,
                                Width = normW,
                                Height = normH,
                                CharacterIndex = charIndex,
                                MatchLength = count,
                                ContextSnippet = snippet
                            });

                            if (results.Count >= options.MaxResults)
                            {
                                return ValueTask.FromResult<IReadOnlyList<SearchMatch>>(results);
                            }
                        }
                    }
                }
            }

            return ValueTask.FromResult<IReadOnlyList<SearchMatch>>(results);
        }
    }
}
