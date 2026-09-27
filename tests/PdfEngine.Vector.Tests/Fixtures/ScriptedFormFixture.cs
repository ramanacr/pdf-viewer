using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>A scripted order form (shared by the core and viewer tests).</summary>
public static class ScriptedFormFixture
{
    /// <summary>An order form: qty x price = total (in /CO), a SFN subtotal, and formatted fields.</summary>
    public static byte[] Build()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int Js(string code) => b.AddStream(string.Empty, code);
        string Action(int js) => $"<< /S /JavaScript /JS {js} 0 R >>";

        int qtyF = Js("AFNumber_Format(0, 0, 0, 0, \"\", true);");
        int qtyK = Js("AFNumber_Keystroke(0, 0, 0, 0, \"\", true);");
        int priceF = Js("AFNumber_Format(2, 0, 0, 0, \"$\", true);");
        int priceV = Js("AFRange_Validate(true, 0, true, 1000);");
        int totalC = Js("AFSimple_Calculate(\"PRD\", new Array (\"qty\", \"price\"));");
        int totalF = Js("AFNumber_Format(2, 0, 0, 0, \"$\", true);");
        int subC = Js("/** BVCALC qty * price + 10 EVCALC **/ event.value = AFMakeNumber(getField(\"qty\").value) * AFMakeNumber(getField(\"price\").value) + 10;");
        int dateF = Js("AFDate_FormatEx(\"dd-mmm-yyyy\");");
        int phoneF = Js("AFSpecial_Format(2);");
        int pctF = Js("AFPercent_Format(1, 0);");
        int negF = Js("AFNumber_Format(2, 2, 3, 0, \"\", true);");
        int customF = Js("event.value = util.printd(\"yyyy\", new Date());");

        int page = b.AddPage("", $"<< /Font << /Helv {helv} 0 R >> >>", mediaBox: "[0 0 400 400]", extra: "/Annots [PLACEHOLDER]");
        int first = page + 1;
        string W(string name, int y, string aa) =>
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /Rect [20 {y} 380 {y + 20}] /P {page} 0 R /DA (/Helv 10 Tf 0 g) /AA << {aa} >> >>";
        var names = new List<int>();
        names.Add(b.Add(W("qty", 360, $"/F {Action(qtyF)} /K {Action(qtyK)}")));
        names.Add(b.Add(W("price", 330, $"/F {Action(priceF)} /V {Action(priceV)}")));
        int total = b.Add(W("total", 300, $"/C {Action(totalC)} /F {Action(totalF)}"));
        names.Add(total);
        int sub = b.Add(W("subtotal", 270, $"/C {Action(subC)}"));
        names.Add(sub);
        names.Add(b.Add(W("date", 240, $"/F {Action(dateF)}")));
        names.Add(b.Add(W("phone", 210, $"/F {Action(phoneF)}")));
        names.Add(b.Add(W("pct", 180, $"/F {Action(pctF)}")));
        names.Add(b.Add(W("balance", 150, $"/F {Action(negF)}")));
        names.Add(b.Add(W("custom", 120, $"/F {Action(customF)}")));
        string refs = string.Join(" ", names.Select(n => $"{n} 0 R"));
        int acro = b.Add($"<< /Fields [{refs}] /CO [{total} 0 R {sub} 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {helv} 0 R >> >> >>");
        b.WithCatalog($"/AcroForm {acro} 0 R");
        string pdf = Encoding.Latin1.GetString(b.Build());
        return RebuildXref(pdf.Replace("/Annots [PLACEHOLDER]", $"/Annots [{refs}]"));
    }

    private static byte[] RebuildXref(string text)
    {
        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        var body = text[..xref];
        var offsets = System.Text.RegularExpressions.Regex.Matches(body, @"(?m)^(\d+) 0 obj").Select(m => m.Index).ToList();
        var sb = new StringBuilder(body);
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        int trailer = text.IndexOf("trailer", xref, StringComparison.Ordinal);
        sb.Append(text[trailer..text.IndexOf("startxref", trailer, StringComparison.Ordinal)]).Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

}
