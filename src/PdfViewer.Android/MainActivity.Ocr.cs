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

    /// <summary>
    /// The most pixels a page is drawn with for recognition (about A3 at 300 dpi); a larger page is drawn
    /// at less than <see cref="OcrDpi"/> so it fits in a phone's memory (an A0 page at 300 dpi is ~140 MP).
    /// </summary>
    private const double OcrMaxPixels = 30_000_000;

    /// <summary>SHA-256 of the English model shipped in the assets (PdfViewer.Android.csproj, DownloadTessdata).</summary>
    private const string TessdataSha256 = "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2";
    private static readonly object TessdataLock = new();

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
        var size = doc.Pages[page - 1];
        double scale = OcrDpi / 72.0;
        double pixels = size.Width * scale * size.Height * scale;
        if (pixels > OcrMaxPixels) scale *= Math.Sqrt(OcrMaxPixels / pixels);
        var bitmap = doc.RenderPageAsync(page, scale, false, ct).GetAwaiter().GetResult();
        int w = bitmap.Width, h = bitmap.Height;
        var bgra = bitmap.Pixels.AsSpan();
        var grey = new byte[w * h];
        for (int i = 0, o = 0; i < grey.Length; i++, o += 4)
            grey[i] = (byte)((bgra[o] * 29 + bgra[o + 1] * 150 + bgra[o + 2] * 77) >> 8);
        var words = new List<PdfRecognizedWord>();
        tess.SetImage(grey, w, h, 1, w);
        // The native image and results are let go on every way out: a cancel, an empty page, an error.
        try
        {
            // Recognition runs when the text is first asked for; until then the result iterator is empty.
            if (string.IsNullOrWhiteSpace(tess.UTF8Text)) return words;
            using var it = tess.ResultIterator;
            if (it == null) return words;
            try
            {
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
            }
            finally
            {
                it.Delete();
            }
            return words;
        }
        finally
        {
            tess.Clear();
        }
    }

    /// <summary>
    /// The English model, copied from the app's assets to its private files
    /// (<c>files/tessdata/eng.traineddata</c>), where Tesseract reads it; the folder above it is returned.
    /// The copy is kept only while its SHA-256 is the pinned one, so an app update with a new model
    /// replaces it and a damaged copy is made again. Windows of the app share the files: one copies at a
    /// time, to a temporary file of its own that is then moved into place.
    /// </summary>
    private string EnsureTessdata()
    {
        string root = FilesDir!.AbsolutePath;
        string dir = System.IO.Path.Combine(root, "tessdata");
        string file = System.IO.Path.Combine(dir, "eng.traineddata");
        lock (TessdataLock)
        {
            if (File.Exists(file) && HashOf(file) == TessdataSha256) return root;
            Directory.CreateDirectory(dir);
            string part = System.IO.Path.Combine(dir, $"eng.{Guid.NewGuid():N}.part");
            try
            {
                using (var asset = Assets!.Open("tessdata/eng.traineddata"))
                using (var output = File.Create(part))
                    asset.CopyTo(output);
                if (HashOf(part) != TessdataSha256)
                    throw new UserMessageException("The text recognizer's language data in this app is damaged. Reinstalling the app restores it.");
                File.Move(part, file, overwrite: true);
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
            }
            return root;
        }

        static string HashOf(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }
    }
}
