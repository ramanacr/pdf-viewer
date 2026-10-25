using System.Text;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Shaped substitute text against the Direct2D backend: both shape Arabic and Indic words with the
/// same engine code (SubstituteShaper), so a shaped line looks the same on the web and the desktop.
/// </summary>
public class SubstituteShapingParityTests
{
    private readonly Parity _parity;
    public SubstituteShapingParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    private static readonly string WindowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>
    /// A page whose font /F0 is composite (Identity-H, CID = code point, a /ToUnicode CMap) and not
    /// embedded, with /W widths from the installed font; one line per entry, in the order seen.
    /// </summary>
    private static byte[] Page(string baseFont, string fontFile, params string[] lines)
    {
        var metrics = TrueTypeFontFile.TryLoad(File.ReadAllBytes(Path.Combine(WindowsFonts, fontFile))) ?? throw new InvalidOperationException(fontFile);
        var b = new VectorPdfBuilder();
        var cps = lines.SelectMany(l => l.EnumerateRunes().Select(r => r.Value)).Distinct().Order().ToArray();
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test-UCS def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        cmap.Append($"{cps.Length} beginbfchar\n");
        foreach (int cp in cps) cmap.Append($"<{cp:X4}> <{cp:X4}>\n");
        cmap.Append("endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end");
        int toUnicode = b.AddStream("", cmap.ToString());
        string widths = string.Join(" ", cps.Select(cp => $"{cp} [{(int)Math.Round(metrics.Advance(metrics.GlyphFor(cp)))}]"));
        int descriptor = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -300 /CapHeight 700 /StemV 80 >>");
        int cid = b.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths}] /CIDToGIDMap /Identity >>");
        int type0 = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        string Hex(string text) => string.Concat(text.EnumerateRunes().Select(r => r.Value.ToString("X4")));
        string content = string.Concat(lines.Select((l, i) => $"BT /F0 28 Tf 20 {150 - 45 * i} Td <{Hex(l)}> Tj ET\n"));
        b.AddPage(content, $"<< /Font << /F0 {type0} 0 R >> >>", mediaBox: "[0 0 220 200]");
        return b.Build();
    }

    /// <summary>Right-to-left text as a producer lays it out: the characters reversed, left to right.</summary>
    private static string Seen(string reading) => new(reading.Reverse().ToArray());

    [Fact]
    public Task ShapedArabicLine() =>
        // Joining forms, lam-alef and a mark (Arial has the ligature; its marks are placed by GPOS).
        _parity.AssertMatches("shaped-arabic", Page("Arial", "arial.ttf", Seen("بيت سلام"), Seen("بَب لا")), 0.75, 0.005);

    [Fact]
    public Task ShapedDevanagariLine() =>
        // A pre-base i sign, the ksha conjunct and a reph (Nirmala UI).
        _parity.AssertMatches("shaped-devanagari", Page("NirmalaUI", "Nirmala.ttc", "कि क्ष", "कर्म"), 0.75, 0.005);
}
