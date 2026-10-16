using System;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.PdfA;

namespace PdfEngine.Vector.Accessibility;

/// <summary>
/// Fonts (7.21): embedded, with consistent CMaps, widths and encodings, a Unicode value for every
/// code shown and no .notdef glyph. PDF/A-2u (6.2.11) states the same requirements, so the PDF/A
/// checker's font rules run and their issues are renamed to PDF/UA's clauses and checkpoints.
/// </summary>
internal sealed partial class PdfUaChecker
{
    private static readonly Regex PageOf = new(@"page (\d+)", RegexOptions.CultureInvariant);

    private void CheckFonts()
    {
        foreach (var issue in PdfAValidator.FontIssues(_doc, _file))
        {
            (string clause, string checkpoint)? mapped = (issue.Clause, issue.Test) switch
            {
                ("6.2.11.3.1", _) => ("7.21.3.1", "31-001"),
                ("6.2.11.3.2", _) => ("7.21.3.2", "31-004"),
                ("6.2.11.3.3", 1) => ("7.21.3.3", "31-005"),
                ("6.2.11.3.3", 2) => ("7.21.3.3", "31-006"),
                ("6.2.11.3.3", _) => ("7.21.3.3", "31-007"),
                ("6.2.11.4.1", 2) => ("7.21.4.1", "31-009"),
                ("6.2.11.4.1", _) => ("7.21.4.1", "31-008"),
                ("6.2.11.4.2", 1) => ("7.21.4.2", "31-010"),
                ("6.2.11.4.2", _) => ("7.21.4.2", "31-011"),
                ("6.2.11.5", _) => ("7.21.5", "31-012"),
                ("6.2.11.6", 1) => ("7.21.6", "31-014"),
                ("6.2.11.6", 2) => ("7.21.6", "31-013"),
                ("6.2.11.6", 3) => ("7.21.6", "31-016"),
                ("6.2.11.6", _) => ("7.21.6", "31-017"),
                ("6.2.11.7", _) => ("7.21.7", "10-001"),
                ("6.2.11.8", _) => ("7.21.8", "31-030"),
                _ => null,
            };
            if (mapped is not { } m) continue;
            int? page = issue.Location != null && PageOf.Match(issue.Location) is { Success: true } pm ? int.Parse(pm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            Report(m.checkpoint, m.clause, PdfUaCategory.Fonts, issue.Message, page: page);
        }
        CheckCharSets();
    }

    /// <summary>
    /// A Type 1 subset's /CharSet names exactly the glyphs its program has (7.21.4.2); the PDF/A rule
    /// above checks only the glyphs the text uses.
    /// </summary>
    private void CheckCharSets()
    {
        foreach (var (number, obj) in AllObjects())
        {
            if (obj is not PdfDictionary font || font.GetName("Type") != "Font" || font.GetName("Subtype") is not ("Type1" or "MMType1")) continue;
            if (_r.Resolve(font["FontDescriptor"]) is not PdfDictionary descriptor || _r.Resolve(descriptor["CharSet"]) is not PdfString charSet) continue;
            if (!descriptor.ContainsKey("FontFile") && !descriptor.ContainsKey("FontFile3")) continue;
            CffFont? cff;
            try
            {
                _fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default);
                cff = FontProgramPreparer.Prepare(_fontResolver.ResolveFont(font, "F")) is { Format: PdfFontProgramFormat.OpenTypeCff } prepared ? PdfAChecker.CffOf(prepared) : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { cff = null; }
            if (cff == null) continue;
            var listed = charSet.AsDecodedString().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var present = Enumerable.Range(1, Math.Max(0, cff.GlyphCount - 1)).Select(cff.GlyphName).Where(n => n != ".notdef").ToHashSet(StringComparer.Ordinal);
            listed.Remove(".notdef");
            string label = font.GetName("BaseFont") is { } bf ? $"The font {bf}" : "A font";
            if (listed.Except(present).Any())
                Report("31-010", "7.21.4.2", PdfUaCategory.Fonts, $"{label}'s /CharSet lists glyphs its embedded program does not have.", obj: number);
            else if (present.Except(listed).Any())
                Report("31-010", "7.21.4.2", PdfUaCategory.Fonts, $"{label}'s /CharSet leaves out glyphs its embedded program has.", obj: number);
        }
    }
}
