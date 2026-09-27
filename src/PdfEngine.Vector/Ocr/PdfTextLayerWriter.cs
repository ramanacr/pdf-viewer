using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Ocr;

/// <summary>A recognized word: its text and box, normalized to the page as displayed (after /Rotate), top-left origin.</summary>
public sealed record PdfRecognizedWord(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// Makes scanned pages searchable: the recognized words are added over each page as invisible
/// text (render mode 3), each placed on and stretched to its word's box, so selecting, searching
/// and copying work in any reader while the page looks exactly as it did. The font is a glyphless
/// Type0 font whose ToUnicode map gives back the recognized characters. Written as an
/// incremental update.
/// </summary>
public static class PdfTextLayerWriter
{
    public static byte[] Apply(PdfVectorDocument document, byte[] original, IReadOnlyDictionary<int, IReadOnlyList<PdfRecognizedWord>> pages)
    {
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (r.Resolve(trailer["Root"]) is not PdfDictionary root) throw new InvalidOperationException("The document has no catalog.");
        var pageObjects = Redaction.PdfRedactor.PageObjectNumbers(root, r);

        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(document);

        // One CID per distinct character, over the whole update.
        var cids = new Dictionary<string, int>(StringComparer.Ordinal);
        int CidOf(string ch)
        {
            if (!cids.TryGetValue(ch, out int cid)) cids[ch] = cid = cids.Count + 1;
            return cid;
        }

        int fontNumber = next++, descendantNumber = next++, descriptorNumber = next++, toUnicodeNumber = next++;
        foreach (var (pageNumber, words) in pages.OrderBy(p => p.Key))
        {
            if (pageNumber < 1 || pageNumber > pageObjects.Count || words.Count == 0) continue;
            var node = document.PageTree.Pages[pageNumber - 1];
            var crop = node.CropBox;
            int rotation = node.RotationDegrees;

            var content = new StringBuilder("q BT 3 Tr\n");
            foreach (var word in words)
            {
                string text = word.Text.Trim();
                if (text.Length == 0 || word.Width <= 0 || word.Height <= 0) continue;
                var elements = StringInfoElements(text);
                // The word's box in user space: its display-bottom-left corner, and the run and rise directions.
                var origin = ToUser(word.X, word.Y + word.Height, crop, rotation);
                var runEnd = ToUser(word.X + word.Width, word.Y + word.Height, crop, rotation);
                var top = ToUser(word.X, word.Y, crop, rotation);
                double runLength = Distance(origin, runEnd), height = Distance(origin, top);
                if (runLength <= 0 || height <= 0) continue;
                double size = height * 0.9;
                double natural = elements.Count * 0.5 * size; // every glyph is 500 units wide
                double scaling = natural > 0 ? 100 * runLength / natural : 100;
                double cos = (runEnd.X - origin.X) / runLength, sin = (runEnd.Y - origin.Y) / runLength;
                var codes = new StringBuilder();
                foreach (var ch in elements) codes.Append(CidOf(ch).ToString("X4", CultureInfo.InvariantCulture));
                content.Append("/FOcr ").Append(F(size)).Append(" Tf ").Append(F(scaling)).Append(" Tz ")
                       .Append(F(cos)).Append(' ').Append(F(sin)).Append(' ').Append(F(-sin)).Append(' ').Append(F(cos)).Append(' ')
                       // The baseline a little above the box's bottom (descenders), along the word's up direction (-sin, cos).
                       .Append(F(origin.X - sin * size * 0.12)).Append(' ').Append(F(origin.Y + cos * size * 0.12)).Append(" Tm <")
                       .Append(codes).Append("> Tj\n");
            }
            content.Append("ET Q\n");

            int streamNumber = next++;
            objects[streamNumber] = PdfObjectWriter.NewStream(
                new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, Deflate(Encoding.ASCII.GetBytes(content.ToString())));

            int pageObj = pageObjects[pageNumber - 1];
            var pageDict = (PdfDictionary)r.Resolve(pageObj)!;
            var contents = new List<PdfObject>();
            switch (pageDict["Contents"])
            {
                case PdfIndirectRef single when r.Resolve(single) is PdfStream: contents.Add(single); break;
                case PdfIndirectRef arrRef when r.Resolve(arrRef) is PdfArray arr: contents.AddRange(arr); break;
                case PdfArray direct: contents.AddRange(direct); break;
            }
            contents.Add(new PdfIndirectRef(streamNumber));

            // The page's resources, with the text layer's font beside its own.
            var resources = new Dictionary<string, PdfObject>(node.Resources.Entries);
            var fonts = new Dictionary<string, PdfObject>((r.Resolve(node.Resources["Font"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>())
            {
                ["FOcr"] = new PdfIndirectRef(fontNumber),
            };
            resources["Font"] = new PdfDictionary(fonts);
            objects[pageObj] = new PdfDictionary(new Dictionary<string, PdfObject>(pageDict.Entries)
            {
                ["Contents"] = new PdfArray(contents),
                ["Resources"] = new PdfDictionary(resources),
            });
        }
        if (cids.Count == 0) return original;

        objects[fontNumber] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("Type0"), ["BaseFont"] = new PdfName("GlyphLessFont"),
            ["Encoding"] = new PdfName("Identity-H"),
            ["DescendantFonts"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(descendantNumber) }),
            ["ToUnicode"] = new PdfIndirectRef(toUnicodeNumber),
        });
        objects[descendantNumber] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"), ["Subtype"] = new PdfName("CIDFontType2"), ["BaseFont"] = new PdfName("GlyphLessFont"),
            ["CIDSystemInfo"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Registry"] = PdfObjectWriter.TextString("Adobe"), ["Ordering"] = PdfObjectWriter.TextString("Identity"), ["Supplement"] = new PdfInteger(0),
            }),
            ["FontDescriptor"] = new PdfIndirectRef(descriptorNumber),
            ["DW"] = new PdfInteger(500),
            ["CIDToGIDMap"] = new PdfName("Identity"),
        });
        objects[descriptorNumber] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("FontDescriptor"), ["FontName"] = new PdfName("GlyphLessFont"), ["Flags"] = new PdfInteger(5),
            ["FontBBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(500), new PdfInteger(1000) }),
            ["ItalicAngle"] = new PdfInteger(0), ["Ascent"] = new PdfInteger(1000), ["Descent"] = new PdfInteger(0),
            ["CapHeight"] = new PdfInteger(1000), ["StemV"] = new PdfInteger(80),
        });
        objects[toUnicodeNumber] = PdfObjectWriter.NewStream(
            new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, Deflate(Encoding.ASCII.GetBytes(ToUnicode(cids))));

        return PdfIncrementalWriter.Append(original, document, objects);
    }

    /// <summary>A display-normalized point (top-left origin, after /Rotate) in the page's user space.</summary>
    internal static (double X, double Y) ToUser(double nx, double ny, PdfRect crop, int rotation) => rotation switch
    {
        90 => (crop.X + ny * crop.Width, crop.Y + nx * crop.Height),
        180 => (crop.X + crop.Width - nx * crop.Width, crop.Y + ny * crop.Height),
        270 => (crop.X + crop.Width - ny * crop.Width, crop.Y + crop.Height - nx * crop.Height),
        _ => (crop.X + nx * crop.Width, crop.Y + (1 - ny) * crop.Height),
    };

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>User-perceived characters (a surrogate pair or a letter with its accents is one).</summary>
    private static List<string> StringInfoElements(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    private static string ToUnicode(Dictionary<string, int> cids)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = cids.OrderBy(c => c.Value).ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (ch, cid) in chunk)
                sb.Append('<').Append(cid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                  .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(ch))).Append(">\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));
}
