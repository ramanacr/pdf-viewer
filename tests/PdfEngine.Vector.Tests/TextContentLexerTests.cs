using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The text walk's content lexer (<see cref="TextContentLexer"/>) against the reading it replaced:
/// <see cref="PdfLexer"/> and <see cref="PdfParser"/> one object at a time, exactly as the walk
/// read content before. Each content gives the same operators with the same operands.
/// </summary>
public class TextContentLexerTests
{
    private static readonly Dictionary<string, TextOperator> Operators = new(StringComparer.Ordinal)
    {
        ["q"] = TextOperator.q, ["Q"] = TextOperator.Q, ["cm"] = TextOperator.cm, ["BT"] = TextOperator.BT, ["Tf"] = TextOperator.Tf,
        ["gs"] = TextOperator.gs, ["Tc"] = TextOperator.Tc, ["Tw"] = TextOperator.Tw, ["Tz"] = TextOperator.Tz, ["TL"] = TextOperator.TL,
        ["Ts"] = TextOperator.Ts, ["Tr"] = TextOperator.Tr, ["Td"] = TextOperator.Td, ["TD"] = TextOperator.TD, ["Tm"] = TextOperator.Tm,
        ["T*"] = TextOperator.TStar, ["Tj"] = TextOperator.Tj, ["'"] = TextOperator.Quote, ["\""] = TextOperator.DoubleQuote,
        ["TJ"] = TextOperator.TJ, ["BI"] = TextOperator.BI, ["Do"] = TextOperator.Do, ["BDC"] = TextOperator.BDC, ["BMC"] = TextOperator.BMC,
        ["EMC"] = TextOperator.EMC,
    };

    /// <summary>The walk's former reading loop, kept here as the reference.</summary>
    private static List<string> Reference(byte[] content)
    {
        var result = new List<string>();
        var operands = new List<PdfObject>(8);
        using var src = new MemoryByteSource(content);
        var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
        var parser = new PdfParser(PdfSecurityLimits.Default);
        while (true)
        {
            long tokenPos = src.Position;
            PdfToken token;
            try { token = lexer.NextToken(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { break; }
            if (token.Type == PdfTokenType.EndOfFile) break;
            if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
            {
                src.Position = tokenPos;
                PdfObject? obj;
                try
                {
                    obj = token.Type == PdfTokenType.Keyword
                        ? (token.TextValue == "null" ? PdfNull.Instance : token.TextValue == "true" ? PdfBoolean.True : PdfBoolean.False)
                        : parser.ParseObject(lexer);
                    if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { obj = null; src.Position = tokenPos + 1; }
                if (obj != null) operands.Add(obj);
                else if (src.Position == tokenPos) src.Position = tokenPos + 1;
                if (operands.Count > 64) operands.RemoveAt(0);
                continue;
            }
            var op = Operators.TryGetValue(token.TextValue, out var known) ? known : TextOperator.Other;
            result.Add(op + "(" + string.Join(", ", operands.Select(Show)) + ")");
            if (op == TextOperator.BI) PageContentWalker.InlineImage(src, lexer, parser);
            operands.Clear();
        }
        return result;
    }

    private static List<string> Fast(byte[] content)
    {
        var result = new List<string>();
        var lexer = new TextContentLexer(content);
        while (lexer.Next(out var op))
        {
            var shown = new List<string>();
            for (int i = lexer.Count; i >= 1; i--)
            {
                var o = lexer.At(i);
                shown.Add(o.Kind switch
                {
                    TextContentLexer.Kind.Number => Number(o.Number),
                    TextContentLexer.Kind.Name => "/" + (string)o.Value!,
                    TextContentLexer.Kind.String => Show((PdfString)o.Value!),
                    TextContentLexer.Kind.Array => "[" + string.Join(" ", Enumerable.Range(o.Start, o.Count).Select(k => lexer.ArrayItems[k])
                        .Select(it => it.Kind == TextContentLexer.Kind.Number ? Number(it.Number) : Show((PdfString)it.Value!))) + "]",
                    _ => Show((PdfObject)o.Value!),
                });
            }
            result.Add(op + "(" + string.Join(", ", shown) + ")");
            if (op == TextOperator.BI) lexer.SkipInlineImage();
            lexer.Clear();
        }
        return result;
    }

    private static string Number(double v) => "#" + v.ToString("R", CultureInfo.InvariantCulture) + (v == 0 && double.IsNegative(v) ? "-" : "");

    private static string Show(PdfObject o) => o switch
    {
        PdfInteger i => Number(i.Value),
        PdfReal r => Number(r.Value),
        PdfName n => "/" + n.Value,
        PdfString s => (s.IsHex ? "<" : "(") + Convert.ToHexString(s.RawBytes.Span) + (s.IsHex ? ">" : ")"),
        PdfArray a => "[" + string.Join(" ", a.Items.Select(Show)) + "]",
        PdfDictionary d => "<<" + string.Join(" ", d.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => "/" + k + " " + Show(d[k]!))) + ">>",
        PdfIndirectRef r => $"{r.ObjectNumber} {r.GenerationNumber} R",
        PdfBoolean b => b.Value ? "true" : "false",
        PdfNull => "null",
        _ => o.GetType().Name,
    };

    public static IEnumerable<object[]> Contents() => new[]
    {
        "BT /F1 12 Tf 20 250 Td (Hello) Tj ET",
        "q 1 0 0 1 10.5 -3.25 cm BT /F1 9.75 Tf 1 0 0 1 72 720 Tm [(A) -120 (B) 33.5 (C)] TJ T* (x) ' 1 2 (y) \" ET Q",
        // Numbers: signs, dots, exponents, overflow, odd forms.
        "1 -2 +3 .5 -.5 5. -0 -0.0 0.000001 123456789012345 1234567890123456789 99999999999999999999 1e3 1E-2 +.  -  . 1.2.3 --1 0x10 Infinity NaN 12abc Tc",
        // Names with escapes, odd and broken.
        "/A#20B /#41#42 /Bad#4 /Bad#zz /# /Trail# /a/b /été gs",
        // Literal strings: nesting, escapes, octal, line continuations.
        "(a(b)c) Tj (esc \\n\\r\\t\\b\\f\\(\\)\\\\ \\101\\7\\0123 \\q) Tj (line\\\ncont\\\r\nnext\\\rlast) Tj () Tj",
        // Hex strings: odd digits, white space, stray characters.
        "<48656C6C6F> Tj <4 8 6> Tj <> Tj <zz41> Tj [<0041> -50 <00 42>] TJ",
        // References, booleans and null, at the top level and in arrays.
        "1 0 R 2 0 obj true false null 3 R 4 5 R Do [1 0 R (a)] TJ [true (b)] TJ [null] TJ",
        // Dictionaries and nested arrays go to the general parser.
        "/Span << /ActualText (fi) /MCID 3 /Nested << /K [1 2 [3]] >> >> BDC (x) Tj EMC /P <</MCID 0>> BDC EMC [[1 2] (a)] TJ [/N (a)] TJ",
        // Comments, stray delimiters and keywords.
        "% comment\r\n1 2 % trailing\n Td ] >> > { } ) BT (a) Tj ET\n%end",
        // Inline images are skipped.
        "BI /W 2 /H 2 /BPC 8 /CS /G ID \u0001\u0002\u0003\u0004 EI BT (after) Tj ET",
        // Unterminated tokens at the end.
        "BT (open string",
        "BT [(a) 1",
        "BT <4142",
        "BT << /A 1",
        // More than 64 operands: the oldest are dropped.
        string.Join(" ", Enumerable.Range(0, 80)) + " Tm",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Contents))]
    public void ReadsTheSameOperatorsAndOperands_AsTheGeneralParser(string content)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(content);
        Assert.Equal(Reference(bytes), Fast(bytes));
    }

    [Fact]
    public void RandomContent_ReadsAsTheGeneralParser()
    {
        // Fragments of content glued at random: whatever the mix, both readings agree.
        string[] parts =
        {
            "BT", "ET", "q", "Q", "/F1", "12", "-3.5", ".25", "Tf", "Td", "TJ", "Tj", "[", "]", "(a)", "(b\\)c)", "<41>", "<<", ">>", "/K",
            "1 0 R", "true", "null", "%c\n", "{", ")", ">", "BDC", "EMC", "1e2", "#", "/A#41", "0", "R", "\"", "'",
        };
        var random = new Random(1234);
        for (int n = 0; n < 400; n++)
        {
            var sb = new StringBuilder();
            int count = random.Next(1, 40);
            for (int i = 0; i < count; i++) sb.Append(parts[random.Next(parts.Length)]).Append(random.Next(3) == 0 ? "" : " ");
            byte[] bytes = Encoding.Latin1.GetBytes(sb.ToString());
            Assert.Equal(Reference(bytes), Fast(bytes));
        }
    }
}
