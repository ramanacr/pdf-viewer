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
