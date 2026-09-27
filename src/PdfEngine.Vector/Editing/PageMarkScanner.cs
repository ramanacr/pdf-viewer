using System;
using System.Collections.Generic;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Editing;

/// <summary>A pagination artifact in a content stream: bytes [Start, End) from its tag to its EMC.</summary>
internal sealed record MarkSequence(int Start, int End, PdfMarkKind Kind, List<string> XObjects);

/// <summary>
/// Finds the marked-content sequences Acrobat's Header &amp; Footer, Watermark, Background and Bates
/// tools draw (ISO 32000-2 14.8.2.2): <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Header&gt;&gt; BDC … EMC</c>
/// at the top level of a content stream, with the Subtype Header, Footer, Watermark, Background,
/// PageNum or Bates, or an artifact of Type Background.
/// </summary>
internal static class PageMarkScanner
{
    /// <param name="properties">A property list named in the page's /Properties resources.</param>
    public static List<MarkSequence> Find(byte[] content, Func<string, PdfDictionary?> properties)
    {
        var result = new List<MarkSequence>();
        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        var operands = new List<PdfObject>(4);
        long operandStart = -1;
        int depth = 0;
        (long Start, PdfMarkKind Kind, List<string> XObjects)? open = null;

        while (true)
        {
            lexer.SkipWhitespaceAndComments();
            long tokenPos = src.Position;
            var token = lexer.NextToken();
            if (token.Type == PdfTokenType.EndOfFile) break;
            if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
            {
                if (operands.Count == 0) operandStart = tokenPos;
                src.Position = tokenPos;
                PdfObject? obj;
                try
                {
                    obj = token.Type == PdfTokenType.Keyword ? PdfNull.Instance : parser.ParseObject(lexer);
                    if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    obj = null;
                }
                if (obj != null) operands.Add(obj);
                else if (src.Position <= tokenPos) src.Position = tokenPos + 1;
                continue;
            }

            switch (token.TextValue)
            {
                case "BI":
                    SkipInlineImage(src, lexer, content);
                    break;
                case "BDC" or "BMC":
                    if (depth == 0 && token.TextValue == "BDC" && operands.Count == 2 && operands[0] is PdfName { Value: "Artifact" }
                        && (operands[1] as PdfDictionary ?? (operands[1] is PdfName n ? properties(n.Value) : null)) is { } props
                        && Classify(props) is { } kind)
                        open = (operandStart, kind, new List<string>());
                    depth++;
                    break;
                case "EMC":
                    if (depth > 0) depth--;
                    if (depth == 0 && open is { } o)
                    {
                        result.Add(new MarkSequence((int)o.Start, (int)src.Position, o.Kind, o.XObjects));
                        open = null;
                    }
                    break;
                case "Do":
                    if (open is { } d && operands.Count == 1 && operands[0] is PdfName x) d.XObjects.Add(x.Value);
                    break;
            }
            operands.Clear();
        }
        return result;
    }

    /// <summary>The kind a pagination artifact's property list names, or null for any other marked content.</summary>
    public static PdfMarkKind? Classify(PdfDictionary props)
    {
        string? type = props.GetName("Type"), subtype = props.GetName("Subtype");
        if (type == "Background") return PdfMarkKind.Background;
        if (type != "Pagination") return null;
        return subtype switch
        {
            "Header" or "Footer" or "PageNum" => PdfMarkKind.HeaderFooter,
            "Bates" => PdfMarkKind.Bates,
            "Watermark" => PdfMarkKind.Watermark,
            "Background" => PdfMarkKind.Background,
            _ => null,
        };
    }

    // Past the data of an inline image (BI … ID data EI), which may hold any bytes.
    private static void SkipInlineImage(MemoryByteSource src, PdfLexer lexer, byte[] all)
    {
        while (true)
        {
            var tok = lexer.NextToken();
            if (tok.Type == PdfTokenType.EndOfFile) return;
            if (tok.Type == PdfTokenType.Keyword && tok.TextValue == "ID") break;
        }
        for (long i = src.Position + 1; i + 1 < all.Length; i++)
        {
            if (all[i] == 'E' && all[i + 1] == 'I' && PdfLexer.IsWhitespace(all[i - 1])
                && (i + 2 >= all.Length || PdfLexer.IsWhitespace(all[i + 2]) || PdfLexer.IsDelimiter(all[i + 2])))
            {
                src.Position = i + 2;
                return;
            }
        }
        src.Position = all.Length;
    }
}
