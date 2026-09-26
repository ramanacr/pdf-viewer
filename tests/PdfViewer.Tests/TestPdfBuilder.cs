using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfViewer.Tests;

/// <summary>
/// Lightweight, zero-dependency PDF document generator for testing standard PDF features.
/// </summary>
public static class TestPdfBuilder
{
    /// <summary>
    /// Creates a document whose pages carry an intrinsic /Rotate entry, for verifying that
    /// rendering and text-coordinate normalization handle page rotation correctly.
    /// </summary>
    public static string CreateRotatedPdf(string filePath, int rotateDegrees, int pageCount = 1)
        => CreateSimplePdf(filePath, pageCount, "RotatedToken", rotateDegrees);

    public static string CreateSimplePdf(string filePath, int pageCount = 3, string keywordPrefix = "SearchableToken", int rotateDegrees = 0)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        int currentObj = 1;
        int catalogObj = currentObj++;
        int outlinesObj = currentObj++;
        int pagesObj = currentObj++;

        var pageObjs = new List<int>();
        var contentObjs = new List<int>();
        var outlineObjs = new List<int>();

        for (int i = 1; i <= pageCount; i++)
        {
            pageObjs.Add(currentObj++);
            contentObjs.Add(currentObj++);
            outlineObjs.Add(currentObj++);
        }

        int fontObj = currentObj++;
        int infoObj = currentObj++;

        // Write Catalog
        WriteObj(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R /Outlines {outlinesObj} 0 R >>");

        // Write Outlines
        WriteObj(outlinesObj, $"<< /Type /Outlines /First {outlineObjs[0]} 0 R /Last {outlineObjs[^1]} 0 R /Count {pageCount} >>");

        // Write Outline Items
        for (int i = 0; i < pageCount; i++)
        {
            int num = outlineObjs[i];
            int prev = i > 0 ? outlineObjs[i - 1] : 0;
            int next = i < pageCount - 1 ? outlineObjs[i + 1] : 0;
            string prevStr = prev > 0 ? $"/Prev {prev} 0 R " : "";
            string nextStr = next > 0 ? $"/Next {next} 0 R " : "";
            WriteObj(num, $"<< /Title (Section {i + 1} Title) /Parent {outlinesObj} 0 R {prevStr}{nextStr}/Dest [{pageObjs[i]} 0 R /Fit] >>");
        }

        // Write Pages
        string pageKids = string.Join(" ", pageObjs.Select(p => $"{p} 0 R"));
        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{pageKids}] /Count {pageCount} >>");

        // Write each Page and its Content stream
        for (int i = 0; i < pageCount; i++)
        {
            string rotateEntry = rotateDegrees != 0 ? $"/Rotate {rotateDegrees} " : string.Empty;
            WriteObj(pageObjs[i], $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] {rotateEntry}/Contents {contentObjs[i]} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");

            string streamText = $"BT\n/F1 16 Tf\n50 700 Td\n(This is page number {i + 1} of the test document. Keyword: {keywordPrefix}_{i + 1}) Tj\nET";
            byte[] streamBytes = Encoding.ASCII.GetBytes(streamText);
            WriteObj(contentObjs[i], $"<< /Length {streamBytes.Length} >>\nstream\n{streamText}\nendstream");
        }

        // Write Font
        WriteObj(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // Write Info
        WriteObj(infoObj, "<< /Title (Test Document Title) /Author (Test Author) /Subject (Test Subject) /Keywords (Test Keywords) /Creator (Test Creator) /Producer (Test Producer) >>");

        // Xref & Trailer
        writer.Flush();
        long startXref = fs.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {offsets.Count + 1} /Root {catalogObj} 0 R /Info {infoObj} 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXref);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return filePath;
    }

    /// <summary>
    /// Creates a single-page PDF carrying a /Link annotation that belongs to the document
    /// rather than to this application, for checking that saving comments does not quietly
    /// take the document's own hyperlinks with it.
    /// </summary>
    public static string CreatePdfWithLinkAnnotation(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        WriteObj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    "/Annots [5 0 R] /Resources << /Font << /F1 6 0 R >> >> >>");

        const string streamText = "BT\n/F1 16 Tf\n50 700 Td\n(A page with a hyperlink on it.) Tj\nET";
        WriteObj(4, $"<< /Length {Encoding.ASCII.GetByteCount(streamText)} >>\nstream\n{streamText}\nendstream");

        WriteObj(5, "<< /Type /Annot /Subtype /Link /Rect [50 690 300 715] /Border [0 0 1] " +
                    "/A << /Type /Action /S /URI /URI (https://example.invalid/) >> >>");

        WriteObj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        writer.Flush();
        long startXref = fs.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {offsets.Count + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXref);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return filePath;
    }

    /// <summary>
    /// Creates a single-page PDF with a real AcroForm containing a text field, a checkbox
    /// and a combo box, for exercising form discovery and field writes.
    /// </summary>
    public static string CreateFormPdf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int pageObj = 3;
        const int fontObj = 4;
        const int textFieldObj = 5;
        const int checkBoxObj = 6;
        const int comboObj = 7;
        const int acroFormObj = 8;
        const int checkOnApObj = 9;
        const int checkOffApObj = 10;

        WriteObj(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R /AcroForm {acroFormObj} 0 R >>");
        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>");
        WriteObj(pageObj,
            $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] " +
            $"/Annots [{textFieldObj} 0 R {checkBoxObj} 0 R {comboObj} 0 R] " +
            $"/Resources << /Font << /Helv {fontObj} 0 R >> >> >>");
        WriteObj(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // Text field with an initial value and a default for reset.
        WriteObj(textFieldObj,
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (FullName) /V (Initial Value) /DV (Default Value) " +
            $"/Rect [72 700 372 724] /F 4 /P {pageObj} 0 R /DA (/Helv 12 Tf 0 g) >>");

        // Checkbox, currently off, with the /AP appearance dictionary real forms carry.
        WriteObj(checkBoxObj,
            $"<< /Type /Annot /Subtype /Widget /FT /Btn /T (Subscribe) /V /Off /AS /Off " +
            $"/AP << /N << /Yes {checkOnApObj} 0 R /Off {checkOffApObj} 0 R >> >> " +
            $"/Rect [72 660 92 680] /F 4 /P {pageObj} 0 R /DA (/Helv 12 Tf 0 g) >>");

        // Combo box (/Ff bit 18 = Combo) with an option list.
        WriteObj(comboObj,
            $"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (Country) /V (India) " +
            $"/Opt [(India) (Germany) (Japan)] " +
            $"/Rect [72 620 272 644] /F 4 /P {pageObj} 0 R /DA (/Helv 12 Tf 0 g) >>");

        WriteObj(acroFormObj,
            $"<< /Fields [{textFieldObj} 0 R {checkBoxObj} 0 R {comboObj} 0 R] /NeedAppearances true " +
            $"/DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv {fontObj} 0 R >> >> >>");

        // Appearance streams for the checkbox's on/off states.
        string onStream = "q 0 0 1 rg 2 2 16 16 re f Q";
        WriteObj(checkOnApObj,
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length {onStream.Length} >>\n" +
            $"stream\n{onStream}\nendstream");

        string offStream = "q Q";
        WriteObj(checkOffApObj,
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length {offStream.Length} >>\n" +
            $"stream\n{offStream}\nendstream");

        writer.Flush();
        long startXref = fs.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {offsets.Count + 1} /Root {catalogObj} 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXref);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return filePath;
    }

    /// <summary>
    /// Creates a document carrying the active content a hostile PDF would use: document-level
    /// JavaScript reachable from the name tree, an /OpenAction that runs script on open, a
    /// /Launch action link, and a URI link. Used to verify the safety inspector detects them.
    /// </summary>
    public static string CreateActiveContentPdf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int pageObj = 3;
        const int contentObj = 4;
        const int jsActionObj = 5;
        const int namesObj = 6;
        const int jsNameTreeObj = 7;
        const int uriLinkObj = 8;
        const int launchLinkObj = 9;

        WriteObj(catalogObj,
            $"<< /Type /Catalog /Pages {pagesObj} 0 R /Names {namesObj} 0 R " +
            $"/OpenAction {jsActionObj} 0 R >>");

        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>");

        WriteObj(pageObj,
            $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] " +
            $"/Contents {contentObj} 0 R /Annots [{uriLinkObj} 0 R {launchLinkObj} 0 R] >>");

        string stream = "BT /F1 12 Tf 50 700 Td (Active content test) Tj ET";
        WriteObj(contentObj, $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");

        // Document-level JavaScript action.
        WriteObj(jsActionObj, "<< /Type /Action /S /JavaScript /JS (app.alert\\('hello'\\);) >>");

        // Name tree that makes the script discoverable as a document JavaScript action.
        WriteObj(namesObj, $"<< /JavaScript {jsNameTreeObj} 0 R >>");
        WriteObj(jsNameTreeObj, $"<< /Names [(EmbeddedScript) {jsActionObj} 0 R] >>");

        // A link that navigates to an external address.
        WriteObj(uriLinkObj,
            $"<< /Type /Annot /Subtype /Link /Rect [50 650 300 670] /Border [0 0 0] " +
            $"/A << /Type /Action /S /URI /URI (https://example.com/tracker) >> >>");

        // A link that asks the reader to start an external program.
        WriteObj(launchLinkObj,
            $"<< /Type /Annot /Subtype /Link /Rect [50 600 300 620] /Border [0 0 0] " +
            $"/A << /Type /Action /S /Launch /F (calc.exe) >> >>");

        writer.Flush();
        long startXref = fs.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {offsets.Count + 1} /Root {catalogObj} 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXref);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return filePath;
    }

    /// <summary>
    /// A document carrying an embedded file attachment, plus a file-attachment annotation on
    /// the page. Attachments are the delivery half of the PDF threat model - the payload
    /// rides inside the document and the script or the user launches it - so sanitization has
    /// to be proven against both the document-level name tree and the page annotation.
    /// </summary>
    public static string CreateAttachmentPdf(string filePath, string payload = "payload contents")
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        const int catalogObj = 1;
        const int pagesObj = 2;
        const int pageObj = 3;
        const int contentObj = 4;
        const int fileSpecObj = 5;
        const int embeddedStreamObj = 6;
        const int attachAnnotObj = 7;

        WriteObj(catalogObj,
            $"<< /Type /Catalog /Pages {pagesObj} 0 R " +
            $"/Names << /EmbeddedFiles << /Names [(payload.txt) {fileSpecObj} 0 R] >> >> >>");

        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>");

        WriteObj(pageObj,
            $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] " +
            $"/Contents {contentObj} 0 R /Annots [{attachAnnotObj} 0 R] >>");

        string stream = "BT /F1 12 Tf 50 700 Td (Attachment carrier) Tj ET";
        WriteObj(contentObj, $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");

        WriteObj(fileSpecObj,
            $"<< /Type /Filespec /F (payload.txt) /UF (payload.txt) " +
            $"/EF << /F {embeddedStreamObj} 0 R >> >>");

        WriteObj(embeddedStreamObj,
            $"<< /Type /EmbeddedFile /Length {payload.Length} >>\nstream\n{payload}\nendstream");

        // A file-attachment annotation: the same payload reachable straight from the page.
        WriteObj(attachAnnotObj,
            $"<< /Type /Annot /Subtype /FileAttachment /Rect [50 600 70 620] " +
            $"/FS {fileSpecObj} 0 R >>");

        writer.Flush();
        long startXref = fs.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {offsets.Count + 1} /Root {catalogObj} 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXref);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return filePath;
    }

    public static string CreateEngineShowcasePdf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        int currentObj = 1;
        int catalogObj = currentObj++;   // 1
        int pagesObj = currentObj++;     // 2
        int page1Obj = currentObj++;     // 3
        int content1Obj = currentObj++;  // 4
        int page2Obj = currentObj++;     // 5
        int content2Obj = currentObj++;  // 6
        int page3Obj = currentObj++;     // 7
        int content3Obj = currentObj++;  // 8
        int fontObj = currentObj++;      // 9
        int outlinesObj = currentObj++;  // 10
        int outline1Obj = currentObj++;  // 11
        int outline2Obj = currentObj++;  // 12
        int outline3Obj = currentObj++;  // 13
        int infoObj = currentObj++;      // 14
        int knockoutObj = currentObj++;  // 15

        // 1. Catalog
        WriteObj(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R /Outlines {outlinesObj} 0 R >>");

        // 2. Pages
        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{page1Obj} 0 R {page2Obj} 0 R {page3Obj} 0 R] /Count 3 >>");

        // Page 1: Pure Native Vector
        WriteObj(page1Obj, $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] /Contents {content1Obj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
        string stream1 =
            "BT\n/F1 22 Tf\n0.12 0.35 0.85 rg\n50 735 Td\n(Page 1: Native Vector Pipeline) Tj\nET\n" +
            "BT\n/F1 12 Tf\n0.25 0.25 0.25 rg\n50 710 Td\n(Rendered from the vector display list: zoom re-rasterizes vectors, never a bitmap) Tj\nET\n" +
            "0.2 0.5 0.9 rg\n0.1 0.25 0.6 RG\n2 w\n100 580 m\n120 520 l\n180 520 l\n130 480 l\n150 420 l\n100 460 l\n50 420 l\n70 480 l\n20 520 l\n80 520 l\nh\nB\n" +
            "0.9 0.4 0.1 RG\n3 w\n240 560 m\n280 620 340 500 380 560 c\n420 620 480 500 520 560 c\nS\n" +
            "0.1 0.7 0.4 RG\n3 w\n240 510 m\n280 450 340 570 380 510 c\n420 450 480 570 520 510 c\nS\n" +
            "0.8 0.2 0.5 RG\n3 w\n240 460 m\n280 520 340 400 380 460 c\n420 520 480 400 520 460 c\nS\n" +
            "0.15 0.15 0.15 RG\n6 w\n1 J\n1 j\n70 340 m 130 400 l S\n70 400 m 130 340 l S\n" +
            "BT\n/F1 15 Tf\n0.1 0.1 0.1 rg\n200 380 Td\n[(V) 50 (e) 20 (c) 10 (t) 10 (o) 10 (r) 20 ( ) 20 (K) 40 (e) 10 (r) 10 (n) 10 (i) 10 (n) 10 (g) 20 ( ) 20 (T) 60 (e) 10 (s) 10 (t)] TJ\nET\n" +
            "BT\n/F1 11 Tf\n0.3 0.3 0.3 rg\n200 355 Td\n(Glyph positions come from PDF font metrics, not UI text layout) Tj\nET\n" +
            "0.94 0.96 1.0 rg\n0.2 0.4 0.85 RG\n1.5 w\n50 180 512 110 re\nB\n" +
            "BT\n/F1 12 Tf\n0.1 0.25 0.7 rg\n70 260 Td\n(ENGINE STATUS: 100% Native Vector) Tj\nET\n" +
            "BT\n/F1 10 Tf\n0.2 0.2 0.2 rg\n70 238 Td\n(o In Auto mode: Status bar displays lightning badge 'Vector'.) Tj\n0 -16 Td\n(o Visual sharpness: Zoom into this page up to 500% - lines and curves stay pin-sharp!) Tj\n0 -16 Td\n(o No bitmap scaling: every zoom level replays the same display list.) Tj\nET\n";
        byte[] b1 = Encoding.ASCII.GetBytes(stream1);
        WriteObj(content1Obj, $"<< /Length {b1.Length} >>\nstream\n{stream1}\nendstream");

        // Page 2: Hybrid Fallback
        WriteObj(page2Obj, $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] /Contents {content2Obj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> /XObject << /KO {knockoutObj} 0 R >> >> >>");
        string stream2 =
            "BT\n/F1 22 Tf\n0.85 0.25 0.15 rg\n50 735 Td\n(Page 2: Hybrid Fallback Pipeline) Tj\nET\n" +
            "BT\n/F1 12 Tf\n0.25 0.25 0.25 rg\n50 710 Td\n(Region fallback: only the unsupported objects are composited from PDFium) Tj\nET\n" +
            "0.99 0.94 0.94 rg\n0.85 0.3 0.3 RG\n1.5 w\n50 580 512 90 re\nB\n" +
            "BT\n/F1 12 Tf\n0.75 0.1 0.1 rg\n70 640 Td\n(NOTICE: The two panels below form a knockout transparency group) Tj\nET\n" +
            "BT\n/F1 10 Tf\n0.3 0.1 0.1 rg\n70 618 Td\n(The vector engine classifies them as a TransparencyGroup fallback region) Tj\n0 -15 Td\n(and composites just those regions from PDFium; the rest stays vector.) Tj\nET\n" +
            "/RelativeColorimetric ri\n" +
            "q\n/KO Do\nQ\n" +
            "0.96 0.96 0.96 rg\n0.5 0.5 0.5 RG\n1.5 w\n50 180 512 180 re\nB\n" +
            "BT\n/F1 12 Tf\n0.2 0.2 0.2 rg\n70 330 Td\n(HOW TO VERIFY FALLBACK DIAGNOSTICS:) Tj\nET\n" +
            "BT\n/F1 10 Tf\n0.25 0.25 0.25 rg\n70 305 Td\n(1. Look at the bottom-right status bar: it displays 'Hybrid (N PDFium regions)'.) Tj\n0 -18 Td\n(2. Hover your mouse over the badge: the tooltip lists the reason:) Tj\n0 -18 Td\n(   'TransparencyGroup'.) Tj\n0 -18 Td\n(3. Switch to 'PDFium Only' in View -> Rendering Engine: renders via standard PDFium.) Tj\n0 -18 Td\n(4. Switch to 'Vector Only' in View -> Rendering Engine: the regions are outlined.) Tj\n0 -18 Td\n(5. Return to 'Auto': transparent fallback is restored instantly.) Tj\nET\n";
        byte[] b2 = Encoding.ASCII.GetBytes(stream2);
        WriteObj(content2Obj, $"<< /Length {b2.Length} >>\nstream\n{stream2}\nendstream");

        // Page 3: Real-Time Engine Comparison
        WriteObj(page3Obj, $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] /Contents {content3Obj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
        string stream3 =
            "BT\n/F1 22 Tf\n0.15 0.65 0.4 rg\n50 735 Td\n(Page 3: Real-Time Engine Switching) Tj\nET\n" +
            "BT\n/F1 12 Tf\n0.25 0.25 0.25 rg\n50 710 Td\n(Compare the vector engine and PDFium text rendering at all scales) Tj\nET\n" +
            "BT\n/F1 7 Tf\n0.2 0.2 0.2 rg\n50 670 Td\n(7pt: The quick brown fox jumps over the lazy dog 1234567890 - Subpixel Font Hinting) Tj\nET\n" +
            "BT\n/F1 9 Tf\n0.2 0.2 0.2 rg\n50 650 Td\n(9pt: The quick brown fox jumps over the lazy dog 1234567890 - Subpixel Font Hinting) Tj\nET\n" +
            "BT\n/F1 11 Tf\n0.2 0.2 0.2 rg\n50 625 Td\n(11pt: The quick brown fox jumps over the lazy dog 1234567890 - Subpixel Font Hinting) Tj\nET\n" +
            "BT\n/F1 14 Tf\n0.15 0.15 0.15 rg\n50 595 Td\n(14pt: WPF glyph runs vs PDFium software rasterizer) Tj\nET\n" +
            "BT\n/F1 20 Tf\n0.1 0.2 0.4 rg\n50 560 Td\n(20pt: Retained vector drawing) Tj\nET\n" +
            "BT\n/F1 28 Tf\n0.15 0.45 0.3 rg\n50 515 Td\n(28pt: High-DPI Scalability) Tj\nET\n" +
            "q\n0.2 0.45 0.85 rg\n50 360 160 100 re\nf\nQ\n" +
            "q\n0.85 0.3 0.2 rg\n140 330 160 100 re\nf\nQ\n" +
            "q\n0.25 0.75 0.45 rg\n230 380 160 80 re\nf\nQ\n" +
            "q\n0.85 0.65 0.1 rg\n320 340 160 90 re\nf\nQ\n" +
            "0.95 0.98 0.96 rg\n0.2 0.6 0.4 RG\n1.5 w\n50 160 512 130 re\nB\n" +
            "BT\n/F1 12 Tf\n0.1 0.45 0.25 rg\n70 260 Td\n(INTERACTIVE TEST EXPERIMENT:) Tj\nET\n" +
            "BT\n/F1 10 Tf\n0.25 0.25 0.25 rg\n70 235 Td\n(1. Open the top menu: View -> Rendering Engine.) Tj\n0 -17 Td\n(2. Switch between 'Auto' (Vector) and 'PDFium Only' (Raster).) Tj\n0 -17 Td\n(3. Observe how the page instantly re-renders in real time.) Tj\n0 -17 Td\n(4. Notice subtle differences in font smoothing and stroke weights.) Tj\n0 -17 Td\n(5. Check the status bar: the badge updates dynamically to match the active engine!) Tj\nET\n";
        byte[] b3 = Encoding.ASCII.GetBytes(stream3);
        WriteObj(content3Obj, $"<< /Length {b3.Length} >>\nstream\n{stream3}\nendstream");

        // 9. Font
        WriteObj(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // 10. Outlines
        WriteObj(outlinesObj, $"<< /Type /Outlines /First {outline1Obj} 0 R /Last {outline3Obj} 0 R /Count 3 >>");

        // 11. Outline 1
        WriteObj(outline1Obj, $"<< /Title (1. Pure Native Vector) /Parent {outlinesObj} 0 R /Next {outline2Obj} 0 R /Dest [{page1Obj} 0 R /Fit] >>");

        // 12. Outline 2
        WriteObj(outline2Obj, $"<< /Title (2. Hybrid Fallback) /Parent {outlinesObj} 0 R /Prev {outline1Obj} 0 R /Next {outline3Obj} 0 R /Dest [{page2Obj} 0 R /Fit] >>");

        // 13. Outline 3
        WriteObj(outline3Obj, $"<< /Title (3. Real-Time Comparison) /Parent {outlinesObj} 0 R /Prev {outline2Obj} 0 R /Dest [{page3Obj} 0 R /Fit] >>");

        // 14. Info
        WriteObj(infoObj, "<< /Title (PDF Engine Showcase) /Author (Antigravity) /Subject (Vector and Hybrid Fallback Demonstration) >>");

        // 15. Knockout transparency group with a translucent element: still outside the native compositor, so PDFium supplies it.
        string knockout =
            "0.2 0.55 0.35 rg\n50 420 220 110 re\nf\n" +
            "BT\n/F1 14 Tf\n1 1 1 rg\n70 485 Td\n(Rendered via PDFium) Tj\n0 -22 Td\n(100% Visual Fidelity) Tj\nET\n" +
            "/T gs\n0.3 0.4 0.7 rg\n300 420 262 110 re\nf\n" +
            "BT\n/F1 14 Tf\n1 1 1 rg\n320 485 Td\n(Zero Truncation) Tj\n0 -22 Td\n(Safe Content Delivery) Tj\nET\n";
        byte[] bk = Encoding.ASCII.GetBytes(knockout);
        WriteObj(knockoutObj, $"<< /Type /XObject /Subtype /Form /BBox [50 420 562 530] /Group << /S /Transparency /K true >> /Resources << /Font << /F1 {fontObj} 0 R >> /ExtGState << /T << /ca 0.85 >> >> >> /Length {bk.Length} >>\nstream\n{knockout}\nendstream");

        // Xref & Trailer
        writer.Flush();
        long startXref = fs.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {offsets.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {offsets.Count + 1} /Root {catalogObj} 0 R /Info {infoObj} 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXref);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return filePath;
    }

    public static string CreateNonLatinPdf(string filePath)
    {
        return CreateSimplePdf(filePath, 2, "UnicodeTest");
    }

    public static string CreateCorruptPdf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, "%PDF-1.7\nCorrupted garbage data that is not a valid PDF file.");
        return filePath;
    }
}
