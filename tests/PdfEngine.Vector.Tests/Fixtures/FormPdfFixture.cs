using System;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>A one-page form covering every fillable field kind (shared by the core and viewer tests).</summary>
public static class FormPdfFixture
{
    /// <summary>
    /// A page with: text field "person.name" (a parent with a widget kid), a multi-line text,
    /// a comb field, a check box with Yes/Off appearances, a two-button radio group, a combo box
    /// and a list box.
    /// </summary>
    public static byte[] Build()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int zadb = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>");
        // A drawn check mark (the state switch is what is tested, not the Dingbats font).
        int yes = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 14 14]", "q 0 g 2 7 m 6 3 l 12 12 l 10.5 13 l 6 5.5 l 3.5 8.5 l f Q");
        int off = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 14 14]", "");
        // Object numbers follow the page (added next): fields and widgets.
        int page = b.AddPage("0.9 g 0 0 300 400 re f", $"<< /Font << /Helv {helv} 0 R >> >>", mediaBox: "[0 0 300 400]",
            extra: "/Annots [PLACEHOLDER]");
        int person = page + 1, nameW = page + 2, notes = page + 3, zip = page + 4, agree = page + 5, colour = page + 6, red = page + 7, blue = page + 8, city = page + 9, fruit = page + 10, acro = page + 11;
        b.Add($"<< /FT /Tx /T (person) /Kids [{nameW} 0 R] >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /Parent {person} 0 R /T (name) /Rect [20 360 280 380] /P {page} 0 R /DA (/Helv 12 Tf 0 g) /MK << /BG [1] /BC [0] >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (notes) /Ff {1 << 12} /Rect [20 300 280 350] /P {page} 0 R /DA (/Helv 0 Tf 0 0 1 rg) >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (zip) /Ff {1 << 24} /MaxLen 5 /Rect [20 270 120 290] /P {page} 0 R /DA (/Helv 12 Tf 0 g) /Q 1 >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [20 240 34 254] /P {page} 0 R /V /Off /AS /Off /AP << /N << /Yes {yes} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /FT /Btn /Ff {1 << 15} /T (colour) /V /Off /Kids [{red} 0 R {blue} 0 R] >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /Parent {colour} 0 R /Rect [20 210 34 224] /P {page} 0 R /AS /Off /AP << /N << /Red {yes} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /Parent {colour} 0 R /Rect [50 210 64 224] /P {page} 0 R /AS /Off /AP << /N << /Blue {yes} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff {1 << 17} /T (city) /Opt [[(PAR) (Paris)] [(ROM) (Rome)]] /V (PAR) /Rect [20 180 180 200] /P {page} 0 R /DA (/Helv 11 Tf 0 g) >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /T (fruit) /Opt [(Apple) (Banana) (Cherry)] /Rect [20 110 180 170] /P {page} 0 R /DA (/Helv 11 Tf 0 g) >>");
        b.Add($"<< /Fields [{person} 0 R {notes} 0 R {zip} 0 R {agree} 0 R {colour} 0 R {city} 0 R {fruit} 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {helv} 0 R /ZaDb {zadb} 0 R >> >> >>");
        b.WithCatalog($"/AcroForm {acro} 0 R");
        var pdf = b.Build();
        // The page's /Annots is patched once the widget numbers are known, and the cross-reference table rebuilt.
        string annots = $"{nameW} 0 R {notes} 0 R {zip} 0 R {agree} 0 R {red} 0 R {blue} 0 R {city} 0 R {fruit} 0 R";
        return Rebuild(pdf, annots);
    }

    /// <summary>Replaces the /Annots placeholder and rebuilds the cross-reference table.</summary>
    private static byte[] Rebuild(byte[] pdf, string annots)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        text = text.Replace("/Annots [PLACEHOLDER]", $"/Annots [{annots}]");
        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        var body = text[..xref];
        var offsets = new System.Collections.Generic.List<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"(?m)^(\d+) 0 obj"))
            offsets.Add(m.Index);
        var sb = new System.Text.StringBuilder(body);
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        var trailerStart = text.IndexOf("trailer", xref, StringComparison.Ordinal);
        var trailer = text[trailerStart..text.IndexOf("startxref", trailerStart, StringComparison.Ordinal)];
        sb.Append(trailer).Append($"startxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

}
