using System;
using System.IO;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Text;
using PdfViewer.Services;

namespace PdfViewer;

/// <summary>
/// Utility to generate a rich sample PDF document with multiple pages, formatted text, bookmarks, and headings for testing.
/// </summary>
public static class SamplePdfGenerator
{
    public static string GenerateSamplePdf(string outputPath)
    {
        LicenseService.Initialize();

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var doc = new Document();

        // Cover Page
        var coverPage = doc.Pages.Add();
        var title = new TextFragment("Aspose.Pdf Native Windows Viewer\nDemo & Test Document\n");
        title.TextState.FontSize = 26;
        title.TextState.FontStyle = FontStyles.Bold;
        title.TextState.ForegroundColor = Aspose.Pdf.Color.DarkBlue;
        title.HorizontalAlignment = Aspose.Pdf.HorizontalAlignment.Center;
        coverPage.Paragraphs.Add(title);

        var subtitle = new TextFragment("A native high-performance PDF desktop viewer built with WPF and .NET 9\n\n");
        subtitle.TextState.FontSize = 14;
        subtitle.TextState.ForegroundColor = Aspose.Pdf.Color.Gray;
        subtitle.HorizontalAlignment = Aspose.Pdf.HorizontalAlignment.Center;
        coverPage.Paragraphs.Add(subtitle);

        var coverDesc = new TextFragment(
            "This sample document demonstrates the viewing capabilities of the application:\n\n" +
            "• Continuous Vertical Scrolling & Single Page Paginated Modes\n" +
            "• Dynamic Zooming (Fit Width, Fit Page, 25% - 500%) & Mouse Wheel Zoom\n" +
            "• In-Document Text Search with Real-Time Match Counter\n" +
            "• Hierarchical Bookmark / Outline Navigation\n" +
            "• Page Thumbnails with Active Page Indicator\n" +
            "• Document Properties & Metadata Inspector\n" +
            "• Export Pages to High-Resolution PNG/JPEG Images\n" +
            "• High-Quality Windows Printing\n" +
            "• Modern Light & Dark Themes");
        coverDesc.TextState.FontSize = 13;
        coverDesc.TextState.LineSpacing = 6;
        coverPage.Paragraphs.Add(coverDesc);

        var rootBookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "Cover Page",
            Bold = true,
            Destination = new FitExplicitDestination(coverPage)
        };
        doc.Outlines.Add(rootBookmark);

        // Section 1: Features Overview
        var page2 = doc.Pages.Add();
        var sec1Header = new TextFragment("1. Architecture & Capabilities\n");
        sec1Header.TextState.FontSize = 18;
        sec1Header.TextState.FontStyle = FontStyles.Bold;
        sec1Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page2.Paragraphs.Add(sec1Header);

        var sec1Body = new TextFragment(
            "The desktop viewer renders PDF documents directly via Aspose.Pdf rasterization devices. " +
            "Rendering is offloaded onto background thread pools with an LRU (Least-Recently-Used) cache, " +
            "guaranteeing responsive 60 FPS scrolling and low memory consumption even when navigating hundreds of pages.\n\n" +
            "Search functionality utilizes TextFragmentAbsorber to pinpoint exact occurrences of text across " +
            "the entire document structure, highlighting matches with page jumps.");
        sec1Body.TextState.FontSize = 12;
        page2.Paragraphs.Add(sec1Body);

        var sec1Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "1. Architecture & Capabilities",
            Destination = new FitExplicitDestination(page2)
        };
        doc.Outlines.Add(sec1Bookmark);

        // Section 2: Technical Specifications & Data Table
        var page3 = doc.Pages.Add();
        var sec2Header = new TextFragment("2. Technical Specifications\n");
        sec2Header.TextState.FontSize = 18;
        sec2Header.TextState.FontStyle = FontStyles.Bold;
        sec2Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page3.Paragraphs.Add(sec2Header);

        var table = new Table
        {
            ColumnWidths = "140 340",
            Border = new BorderInfo(BorderSide.All, 1f, Aspose.Pdf.Color.LightGray),
            DefaultCellBorder = new BorderInfo(BorderSide.All, 0.5f, Aspose.Pdf.Color.LightGray),
            Margin = new MarginInfo { Top = 10, Bottom = 10 }
        };

        void AddRow(string col1, string col2, bool isHeader = false)
        {
            var row = table.Rows.Add();
            var c1 = row.Cells.Add(col1);
            var c2 = row.Cells.Add(col2);
            if (isHeader)
            {
                row.BackgroundColor = Aspose.Pdf.Color.FromRgb(240.0 / 255.0, 244.0 / 255.0, 248.0 / 255.0);
                c1.DefaultCellTextState.FontStyle = FontStyles.Bold;
                c2.DefaultCellTextState.FontStyle = FontStyles.Bold;
            }
        }

        AddRow("Component", "Specification", true);
        AddRow("Target Framework", ".NET 9.0 Windows (net9.0-windows)");
        AddRow("UI Framework", "Windows Presentation Foundation (WPF)");
        AddRow("Solution Format", "Modern .slnx (Visual Studio 2022+ / .NET 9+)");
        AddRow("PDF Engine", "Aspose.Pdf for .NET");
        AddRow("License System", "Aspose.Total.lic Automatic Discovery & Embedded Resource");
        AddRow("Cache Mechanism", "Thread-safe LRU Memory Bitmap Cache");
        AddRow("Theme Support", "Light & Dark Mode dynamically switched");

        page3.Paragraphs.Add(table);

        var sec2Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "2. Technical Specifications",
            Destination = new FitExplicitDestination(page3)
        };
        doc.Outlines.Add(sec2Bookmark);

        // Section 3: Caching & Multi-Threading
        var page4 = doc.Pages.Add();
        var sec3Header = new TextFragment("3. Caching & Background Threading\n");
        sec3Header.TextState.FontSize = 18;
        sec3Header.TextState.FontStyle = FontStyles.Bold;
        sec3Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page4.Paragraphs.Add(sec3Header);

        var sec3Body = new TextFragment(
            "The rendering engine combines WPF dispatcher prioritization with background task workers. " +
            "Rendered bitmaps are frozen (Freezable.Freeze()) to permit safe cross-thread sharing without UI deadlocks.\n\n" +
            "An LRU memory cache ensures that navigating large documents with dozens or hundreds of pages operates with a constant memory footprint, automatically recycling bitmaps when memory capacity is reached.");
        sec3Body.TextState.FontSize = 12;
        page4.Paragraphs.Add(sec3Body);

        var sec3Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "3. Caching & Background Threading",
            Destination = new FitExplicitDestination(page4)
        };
        doc.Outlines.Add(sec3Bookmark);

        // Section 4: Bookmark & Outline Navigation
        var page5 = doc.Pages.Add();
        var sec4Header = new TextFragment("4. Outline Hierarchy & Bookmarks\n");
        sec4Header.TextState.FontSize = 18;
        sec4Header.TextState.FontStyle = FontStyles.Bold;
        sec4Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page5.Paragraphs.Add(sec4Header);

        var sec4Body = new TextFragment(
            "Document outlines provide direct hierarchical navigation through chapters, sections, and appendices. " +
            "Selecting any item in the left sidebar instantly centers the viewport on the destination page and updates the active indicator.");
        sec4Body.TextState.FontSize = 12;
        page5.Paragraphs.Add(sec4Body);

        var sec4Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "4. Outline Hierarchy & Bookmarks",
            Destination = new FitExplicitDestination(page5)
        };
        doc.Outlines.Add(sec4Bookmark);

        // Section 5: Text Search & Absorber
        var page6 = doc.Pages.Add();
        var sec5Header = new TextFragment("5. High-Speed Text Search\n");
        sec5Header.TextState.FontSize = 18;
        sec5Header.TextState.FontStyle = FontStyles.Bold;
        sec5Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page6.Paragraphs.Add(sec5Header);

        var sec5Body = new TextFragment(
            "Keyword search across all pages executes asynchronously, returning snippets, page numbers, and hit counters. " +
            "Users can double-click any search result to jump directly to that page.\n\n" +
            "Keyword for search verification: QuantumComputingX99");
        sec5Body.TextState.FontSize = 12;
        page6.Paragraphs.Add(sec5Body);

        var sec5Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "5. High-Speed Text Search",
            Destination = new FitExplicitDestination(page6)
        };
        doc.Outlines.Add(sec5Bookmark);

        // Section 6: Image Export & Printing
        var page7 = doc.Pages.Add();
        var sec6Header = new TextFragment("6. Image Export & Printing\n");
        sec6Header.TextState.FontSize = 18;
        sec6Header.TextState.FontStyle = FontStyles.Bold;
        sec6Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page7.Paragraphs.Add(sec6Header);

        var sec6Body = new TextFragment(
            "Export pages to standalone PNG or JPEG files at customizable DPI resolutions (72 to 600 DPI). " +
            "Standard Windows Print Dialog integration allows printing full documents or custom page ranges with native quality.");
        sec6Body.TextState.FontSize = 12;
        page7.Paragraphs.Add(sec6Body);

        var sec6Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "6. Image Export & Printing",
            Destination = new FitExplicitDestination(page7)
        };
        doc.Outlines.Add(sec6Bookmark);

        // Section 7: Summary & Verification
        var page8 = doc.Pages.Add();
        var sec7Header = new TextFragment("7. Summary & Verification Notes\n");
        sec7Header.TextState.FontSize = 18;
        sec7Header.TextState.FontStyle = FontStyles.Bold;
        sec7Header.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(0.0, 102.0 / 255.0, 204.0 / 255.0);
        page8.Paragraphs.Add(sec7Header);

        var sec7Body = new TextFragment(
            "All core features have been automated and tested:\n" +
            "• Unit tests passing with 100% success rate.\n" +
            "• Aspose.Total license active without watermarks or page limits.\n" +
            "• Smooth continuous scrolling across all pages.\n" +
            "• Navigation sidebar with real-time center page synchronization and thumbnail scrolling.");
        sec7Body.TextState.FontSize = 12;
        page8.Paragraphs.Add(sec7Body);

        var sec7Bookmark = new OutlineItemCollection(doc.Outlines)
        {
            Title = "7. Summary & Verification",
            Destination = new FitExplicitDestination(page8)
        };
        doc.Outlines.Add(sec7Bookmark);

        doc.Save(outputPath);
        return Path.GetFullPath(outputPath);
    }
}
