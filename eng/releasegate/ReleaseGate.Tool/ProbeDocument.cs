using System.Globalization;
using System.IO;
using System.Text;

namespace ReleaseGate.Tool;

/// <summary>
/// The synthetic document the startup gate opens. Generated rather than committed so that it is
/// byte-for-byte the same on every machine and cannot drift from this description:
/// twelve Letter pages, each with a heading, a column of body text in two standard fonts, a
/// filled and stroked bar chart, a Bezier trend line, and a colour image - the mix a first page
/// of a real report makes the renderer do. Nothing is random, nothing is timestamped.
/// </summary>
internal static class ProbeDocument
{
    public const int PageCount = 12;
    public const string FileName = "startup-probe.pdf";

    private const int ImageWidth = 128;
    private const int ImageHeight = 96;

    private static readonly string[] Words =
    {
        "startup", "measured", "from", "process", "creation", "to", "the", "first", "page", "on", "screen",
        "every", "release", "keeps", "this", "time", "within", "its", "baseline", "document", "renders",
        "text", "paths", "images", "and", "fills", "so", "that", "a", "regression", "anywhere", "shows",
    };

    public static byte[] Create()
    {
        var inv = CultureInfo.InvariantCulture;
        // 1 catalog, 2 pages, 3-4 fonts, 5 image; then a content stream and a page per page.
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Array.Empty<byte>(),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>"),
            Stream($"/Type /XObject /Subtype /Image /Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8", ImagePixels()),
        };

        var kids = new StringBuilder();
        for (int p = 1; p <= PageCount; p++)
        {
            objects.Add(Stream(string.Empty, Ascii(PageContent(p, inv))));
            int content = objects.Count;
            objects.Add(Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R " +
                "/Resources << /Font << /F1 3 0 R /F2 4 0 R >> /XObject << /Im1 5 0 R >> >> >>"));
            kids.Append(objects.Count).Append(" 0 R ");
        }

        objects[1] = Ascii($"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {PageCount} >>");

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Ascii(s));
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            W("\nendobj\n");
        }

        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets) W($"{off:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string PageContent(int page, CultureInfo inv)
    {
        var s = new StringBuilder();

        // Heading.
        s.Append(inv, $"BT /F1 22 Tf 0.1 0.2 0.45 rg 54 730 Td (Startup probe - page {page} of {PageCount}) Tj ET\n");

        // Body text: a left column of justified-looking lines.
        s.Append("0 0 0 rg\n");
        for (int line = 0; line < 34; line++)
        {
            s.Append(inv, $"BT /F2 10.5 Tf 54 {700 - line * 13.5:0.##} Td ({Sentence(page, line)}) Tj ET\n");
        }

        // Bar chart: filled, outlined bars on axes, to the right of the text.
        s.Append("q 0.4 w 0 0 0 RG 350 640 m 350 420 l 570 420 l S Q\n");
        for (int bar = 0; bar < 8; bar++)
        {
            double height = 30 + ((page * 37 + bar * 53) % 170);
            double r = 0.2 + 0.08 * bar, g = 0.45 + 0.05 * ((bar + page) % 5), b = 0.8 - 0.07 * bar;
            s.Append(inv, $"q {r:0.###} {g:0.###} {b:0.###} rg 0.1 0.1 0.1 RG 0.6 w {360 + bar * 26} 420 18 {height:0.##} re B Q\n");
        }

        // Trend line over the bars.
        s.Append(inv, $"q 0.85 0.2 0.15 RG 1.5 w 1 J 360 {460 + page % 7 * 5} m 420 600 480 440 565 {560 + page % 5 * 8} c S Q\n");

        // Image, scaled up so it is resampled rather than copied.
        s.Append("q 216 0 0 162 350 200 cm /Im1 Do Q\n");

        // Footer rule and page number.
        s.Append(inv, $"q 0.5 w 0.6 0.6 0.6 RG 54 60 m 558 60 l S Q BT /F2 9 Tf 290 44 Td ({page}) Tj ET\n");
        return s.ToString();
    }

    private static string Sentence(int page, int line)
    {
        var words = new StringBuilder();
        for (int w = 0; w < 9; w++)
        {
            if (w > 0) words.Append(' ');
            words.Append(Words[(page * 7 + line * 5 + w * 3) % Words.Length]);
        }

        return words.ToString();
    }

    /// <summary>A smooth two-axis gradient with a diagonal band: cheap to describe, not trivially compressible.</summary>
    private static byte[] ImagePixels()
    {
        var pixels = new byte[ImageWidth * ImageHeight * 3];
        int i = 0;
        for (int y = 0; y < ImageHeight; y++)
        {
            for (int x = 0; x < ImageWidth; x++)
            {
                bool band = Math.Abs(x - y * ImageWidth / ImageHeight) < 8;
                pixels[i++] = (byte)(band ? 250 : x * 255 / (ImageWidth - 1));
                pixels[i++] = (byte)(band ? 220 : y * 255 / (ImageHeight - 1));
                pixels[i++] = (byte)(band ? 40 : 255 - (x + y) * 255 / (ImageWidth + ImageHeight - 2));
            }
        }

        return pixels;
    }

    private static byte[] Stream(string dictEntries, byte[] data)
    {
        string head = $"<< {dictEntries}{(dictEntries.Length > 0 ? " " : "")}/Length {data.Length} >>\nstream\n";
        return [.. Ascii(head), .. data, .. Ascii("\nendstream")];
    }

    private static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);
}
