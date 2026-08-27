using System;
using System.IO;
using System.Text;

namespace PdfViewer;

/// <summary>
/// Utility to generate a rich sample PDF document with multiple pages, text, and bookmarks with zero external dependencies.
/// </summary>
public static class SamplePdfGenerator
{
    public static string GenerateSamplePdf(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // If sample document already exists in samples folder, copy it
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, "samples", "SampleDocument.pdf");
        if (File.Exists(candidate))
        {
            File.Copy(candidate, outputPath, true);
            return outputPath;
        }

        // Otherwise generate a multi-page PDF with outlines and text
        return CreateSampleDocument(outputPath, 8);
    }

    private static string CreateSampleDocument(string filePath, int pageCount = 8)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, Encoding.ASCII);

        var offsets = new System.Collections.Generic.List<long>();
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

        var pageObjs = new System.Collections.Generic.List<int>();
        var contentObjs = new System.Collections.Generic.List<int>();
        var outlineObjs = new System.Collections.Generic.List<int>();

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

        string[] sectionTitles =
        {
            "Cover Page",
            "Features & Navigation",
            "Performance Architecture",
            "Search & Extraction",
            "Rendering Engine (PDFium)",
            "Annotation Support",
            "Printing & Export",
            "Technical Specifications"
        };

        // Write Outline Items
        for (int i = 0; i < pageCount; i++)
        {
            int num = outlineObjs[i];
            int prev = i > 0 ? outlineObjs[i - 1] : 0;
            int next = i < pageCount - 1 ? outlineObjs[i + 1] : 0;
            string prevStr = prev > 0 ? $"/Prev {prev} 0 R " : "";
            string nextStr = next > 0 ? $"/Next {next} 0 R " : "";
            string title = i < sectionTitles.Length ? sectionTitles[i] : $"Page {i + 1}";
            WriteObj(num, $"<< /Title ({title}) /Parent {outlinesObj} 0 R {prevStr}{nextStr}/Dest [{pageObjs[i]} 0 R /Fit] >>");
        }

        // Write Pages
        string pageKids = string.Join(" ", pageObjs.Select(p => $"{p} 0 R"));
        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{pageKids}] /Count {pageCount} >>");

        // Write each Page and its Content stream
        for (int i = 0; i < pageCount; i++)
        {
            WriteObj(pageObjs[i], $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] /Contents {contentObjs[i]} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");

            string title = i < sectionTitles.Length ? sectionTitles[i] : $"Page {i + 1}";
            string streamText =
                "BT\n" +
                "/F1 22 Tf\n" +
                "50 720 Td\n" +
                $"({title}) Tj\n" +
                "/F1 12 Tf\n" +
                "0 -30 Td\n" +
                $"(PDF Viewer Native - Google PDFium Engine) Tj\n" +
                "0 -25 Td\n" +
                $"(Demonstrating high-performance native rendering, search, and navigation for page {i + 1}.) Tj\n" +
                "0 -25 Td\n" +
                $"(Keyword: SampleFeature_{i + 1} with instant multi-page search and continuous scroll.) Tj\n" +
                "ET";

            byte[] streamBytes = Encoding.ASCII.GetBytes(streamText);
            WriteObj(contentObjs[i], $"<< /Length {streamBytes.Length} >>\nstream\n{streamText}\nendstream");
        }

        // Write Font
        WriteObj(fontObj, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // Write Info
        WriteObj(infoObj, "<< /Title (PDF Viewer Native Demo Document) /Author (PDF Viewer Native) /Subject (Demonstration Document) /Keywords (PDF, Viewer, PDFium) /Creator (PDF Viewer Native) /Producer (Google PDFium) >>");

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
}
