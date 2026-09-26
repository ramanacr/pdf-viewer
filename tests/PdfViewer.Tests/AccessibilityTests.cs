using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using PdfViewer.Text;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Content for assistive technology: tagged PDFs read in structure order with their roles, alt
/// text and /ActualText, artifacts left out; untagged pages get paragraphs, headings and list
/// items inferred; read mode is a text document UI Automation exposes to screen readers.
/// </summary>
public class AccessibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AccessibilityTests_" + Guid.NewGuid().ToString("N"));

    public AccessibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A tagged page: H1, P, Figure with /Alt, L/LI, a P with /ActualText, and a header artifact.</summary>
    private string TaggedPdf()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string content =
            "/Artifact BMC BT /F1 8 Tf 20 190 Td (Running header artifact) Tj ET EMC " +
            "/H1 <</MCID 0>> BDC BT /F1 22 Tf 20 165 Td (Main Title) Tj ET EMC " +
            "/P <</MCID 1>> BDC BT /F1 11 Tf 20 140 Td (First paragraph text.) Tj ET EMC " +
            "/Figure <</MCID 2>> BDC 0 0 1 rg 20 70 60 40 re f EMC " +
            "/LI <</MCID 3>> BDC BT /F1 11 Tf 20 50 Td (Item one) Tj ET EMC " +
            "/P <</MCID 4>> BDC BT /F1 11 Tf 20 30 Td (Original words) Tj ET EMC";
        int page = b.AddPage(content, $"<< /Font << /F1 {font} 0 R >> >>", extra: "/StructParents 0");
        int root = page + 1, doc = page + 2, h1 = page + 3, p = page + 4, fig = page + 5, list = page + 6, li = page + 7, p2 = page + 8;
        Assert.Equal(root, b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R /ParentTree << /Nums [0 [{h1} 0 R {p} 0 R {fig} 0 R {li} 0 R {p2} 0 R]] >> >>"));
        b.Add($"<< /Type /StructElem /S /Document /P {root} 0 R /K [{h1} 0 R {p} 0 R {fig} 0 R {list} 0 R {p2} 0 R] >>");
        b.Add($"<< /Type /StructElem /S /H1 /P {doc} 0 R /Pg {page} 0 R /K 0 >>");
        b.Add($"<< /Type /StructElem /S /P /P {doc} 0 R /Pg {page} 0 R /K 1 >>");
        b.Add($"<< /Type /StructElem /S /Figure /P {doc} 0 R /Pg {page} 0 R /Alt (A blue box) /K 2 >>");
        b.Add($"<< /Type /StructElem /S /L /P {doc} 0 R /K [{li} 0 R] >>");
        b.Add($"<< /Type /StructElem /S /LI /P {list} 0 R /Pg {page} 0 R /K 3 >>");
        b.Add($"<< /Type /StructElem /S /P /P {doc} 0 R /Pg {page} 0 R /ActualText (Replacement words) /K 4 >>");
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R /Lang (en-US)");
        string path = Path.Combine(_dir, "tagged.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public async Task TaggedPdf_IsReadInStructureOrder_WithRolesAltTextAndActualText()
    {
        using var service = new PdfiumDocumentService();
        await service.OpenDocumentAsync(TaggedPdf());
        var content = await service.ExtractAccessibleContentAsync(1);
        Assert.True(content.FromTags);
        Assert.Collection(content.Blocks,
            h => { Assert.Equal(ContentRole.Heading1, h.Role); Assert.Equal("Main Title", h.Text); },
            p => { Assert.Equal(ContentRole.Paragraph, p.Role); Assert.Equal("First paragraph text.", p.Text); },
            f => { Assert.Equal(ContentRole.Figure, f.Role); Assert.Equal("A blue box", f.AltText); },
            l => { Assert.Equal(ContentRole.ListItem, l.Role); Assert.Equal("Item one", l.Text); },
            r => { Assert.Equal(ContentRole.Paragraph, r.Role); Assert.Equal("Replacement words", r.Text); });
        Assert.DoesNotContain("artifact", content.PlainText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntaggedPage_InfersHeadingsParagraphsAndListItems()
    {
        // A 24-unit title, two body paragraphs (the second with a larger gap), two list items, and
        // a vertical margin note (one character per line).
        var glyphs = new System.Collections.Generic.List<TextGlyph>();
        double y = 0.05;
        void Line(string text, double size, double gapAfter)
        {
            for (int k = 0; k < text.Length; k++)
                glyphs.Add(new TextGlyph(text[k].ToString(), new Rect(0.1 + k * size * 0.5, y, size * 0.5, size)));
            glyphs.Add(new TextGlyph("\r\n", Rect.Empty, true));
            y += size + gapAfter;
        }
        Line("Annual Report", 0.04, 0.03);
        Line("The first paragraph runs", 0.015, 0.005);
        Line("over two lines of text.", 0.015, 0.03);
        Line("A second paragraph.", 0.015, 0.03);
        Line("• First point", 0.015, 0.005);
        Line("• Second point", 0.015, 0.03);
        foreach (char c in "MARGIN")
        {
            glyphs.Add(new TextGlyph(c.ToString(), new Rect(0.9, y, 0.01, 0.015)));
            glyphs.Add(new TextGlyph("\r\n", Rect.Empty, true));
            y += 0.02;
        }
        var content = PageAccessibleContent.FromLayout(1, new PageTextLayout(glyphs));
        Assert.False(content.FromTags);
        Assert.Equal(ContentRole.Heading1, content.Blocks[0].Role);
        Assert.Equal("Annual Report", content.Blocks[0].Text);
        Assert.Equal("The first paragraph runs over two lines of text.", content.Blocks[1].Text); // reflowed
        Assert.Contains(content.Blocks, b => b.Role == ContentRole.ListItem && b.Text == "• First point");
        Assert.Equal("MARGIN", content.Blocks[^1].Text); // stacked characters read as one
    }

    [Fact]
    public void ReadMode_IsATextDocumentForScreenReaders()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new ReadModeWindow("test.pdf", dark: false) { Left = -10000, ShowActivated = false, ShowInTaskbar = false };
                window.AddPage(new PageAccessibleContent(1, true, new[]
                {
                    new ContentBlock(ContentRole.Heading1, "Title"),
                    new ContentBlock(ContentRole.Paragraph, "Body text."),
                    new ContentBlock(ContentRole.ListItem, "One"),
                    new ContentBlock(ContentRole.ListItem, "Two"),
                    new ContentBlock(ContentRole.Figure, "", "A chart"),
                }), 1);
                window.Show();
                var viewer = (System.Windows.Controls.FlowDocumentScrollViewer)window.Content;
                var doc = viewer.Document;
                Assert.Contains(doc.Blocks, b => b is List l && l.ListItems.Count == 2);
                string all = new TextRange(doc.ContentStart, doc.ContentEnd).Text;
                Assert.Contains("Title", all);
                Assert.Contains("[Figure: A chart]", all);
                // What Narrator, NVDA and JAWS use to read and navigate text.
                var peer = UIElementAutomationPeer.CreatePeerForElement(viewer);
                Assert.NotNull(peer.GetPattern(PatternInterface.Text));
                window.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "read mode test timed out");
        if (error != null) throw error;
    }
}
