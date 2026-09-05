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
