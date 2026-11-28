using Android.App;
using Android.Widget;
using Com.Googlecode.Tesseract.Android;
using PdfEngine.Vector.Ocr;
using PdfViewer.Web.Engine;

namespace PdfViewer.Android;

/// <summary>
/// Make scanned pages searchable, as the desktop's Tools → Make Scanned Pages Searchable: the
/// pages that have no text are drawn at 300 dpi and read by Tesseract on the device (English,
/// tessdata_fast; Tesseract4Android, offline, no permission), and the recognized words are added
/// over them as invisible text (<see cref="PdfTextLayerWriter"/>), so search, selection, copy and
/// read aloud work while the pages look as they did. Pages that already have text are left alone:
/// their text is exact, recognition is not. The result is a new PDF; nothing is sent anywhere.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The resolution scanned pages are read at: Tesseract's best range for printed text.</summary>
    private const int OcrDpi = 300;

    private void MakeSearchable()
    {
        if (!NeedDocument()) return;
        var doc = _doc!;
        byte[]? bytes = CurrentBytes();
        if (bytes == null) return;
        SaveNewFileWithProgress("application/pdf", BaseName + " (searchable).pdf", "Looking for scanned pages…", (ct, report) =>
        {
            using (var engine = OpenForTool(bytes))
                if (!PermissionsOf(engine).CanModify)
                    throw new UserMessageException("This document's permissions do not allow adding text to it.");

            // Pages with text are not read again.
            var scanned = new List<int>();
            for (int page = 1; page <= doc.PageCount; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(doc.GetPageTextAsync(page, ct).GetAwaiter().GetResult())) scanned.Add(page);
            }
            if (scanned.Count == 0)
                throw new UserMessageException("Every page of this document already has text, so it is already searchable.");

            string dataPath = EnsureTessdata();
            var recognized = new Dictionary<int, IReadOnlyList<PdfRecognizedWord>>();
            int words = 0;
            var tess = new TessBaseAPI();
            try
            {
                if (!tess.Init(dataPath, "eng"))
                    throw new UserMessageException("The text recognizer could not start on this device.");
                for (int i = 0; i < scanned.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int page = scanned[i];
                    report($"Reading page {page} ({i + 1} of {scanned.Count})…");
                    var found = RecognizePage(tess, doc, page, ct);
                    if (found.Count > 0) recognized[page] = found;
                    words += found.Count;
                }
            }
            finally
            {
                tess.Recycle();
            }
            if (recognized.Count == 0)
                throw new UserMessageException($"No text was recognized on the {scanned.Count} scanned page(s).");

            report("Adding the text…");
            using var target = OpenForTool(bytes);
            _ocrSummary = (words, recognized.Count, scanned.Count);
            return PdfTextLayerWriter.Apply(target, bytes, recognized);
        }, target =>
        {
            var (words, pages, scanned) = _ocrSummary;
            OfferOpen(target, $"{words} word(s) were recognized on {pages} of {scanned} scanned page(s). The new PDF can be searched and its text selected; the open document is not changed.");
        });
    }

    private (int Words, int Pages, int Scanned) _ocrSummary;

    /// <summary>
    /// One page drawn at <see cref="OcrDpi"/> (within the viewer's bitmap limit), in grey, read by
    /// Tesseract word by word; each word's box as fractions of the shown page, top-left origin.
    /// </summary>
    private static List<PdfRecognizedWord> RecognizePage(TessBaseAPI tess, ViewerDocument doc, int page, CancellationToken ct)
    {
        var bitmap = doc.RenderPageAsync(page, OcrDpi / 72.0, false, ct).GetAwaiter().GetResult();
        int w = bitmap.Width, h = bitmap.Height;
        var bgra = bitmap.Pixels.AsSpan();
        var grey = new byte[w * h];
        for (int i = 0, o = 0; i < grey.Length; i++, o += 4)
            grey[i] = (byte)((bgra[o] * 29 + bgra[o + 1] * 150 + bgra[o + 2] * 77) >> 8);
        tess.SetImage(grey, w, h, 1, w);
        var words = new List<PdfRecognizedWord>();
        // Recognition runs when the text is first asked for; until then the result iterator is empty.
        if (string.IsNullOrWhiteSpace(tess.UTF8Text)) return words;
        using var it = tess.ResultIterator;
        if (it == null) return words;
        const int level = TessBaseAPI.PageIteratorLevel.RilWord;
        it.Begin();
        do
        {
            ct.ThrowIfCancellationRequested();
            string? text = it.GetUTF8Text(level);
            if (string.IsNullOrWhiteSpace(text) || it.GetBoundingBox(level) is not { Length: 4 } box) continue;
            // Words Tesseract is unsure of are mostly specks and lines read as text.
            if (it.Confidence(level) < 30) continue;
            int left = box[0], top = box[1], right = box[2], bottom = box[3];
            if (right <= left || bottom <= top) continue;
            words.Add(new PdfRecognizedWord(text.Trim(), (double)left / w, (double)top / h, (double)(right - left) / w, (double)(bottom - top) / h));
        }
        while (it.Next(level));
        it.Delete();
        tess.Clear();
        return words;
    }

    /// <summary>
    /// The English model, copied once from the app's assets to its private files
    /// (<c>files/tessdata/eng.traineddata</c>), where Tesseract reads it; the folder above it is returned.
    /// </summary>
    private string EnsureTessdata()
    {
        string root = FilesDir!.AbsolutePath;
        string dir = System.IO.Path.Combine(root, "tessdata");
        string file = System.IO.Path.Combine(dir, "eng.traineddata");
        using var asset = Assets!.Open("tessdata/eng.traineddata");
        if (File.Exists(file) && new FileInfo(file).Length > 0) return root;
        Directory.CreateDirectory(dir);
        string part = file + ".part";
        using (var output = File.Create(part)) asset.CopyTo(output);
        File.Move(part, file, overwrite: true);
        return root;
    }
}
