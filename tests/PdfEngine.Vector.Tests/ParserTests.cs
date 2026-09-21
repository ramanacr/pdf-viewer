using System;
using System.IO;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Xref;
using Xunit;

namespace PdfEngine.Vector.Tests;

public class ParserTests
{
    [Fact]
    public void Lexer_ParsesTokensCorrectly()
    {
        string pdfText = @"
            % This is a comment
            123 +45 -678 3.14159 /Name#20With#20Spaces (Hello (nested) World\n) <48656C6C6F>
            true false null [ 1 2 3 ] << /Key (Value) /Num 42 >>
        ";

        byte[] bytes = Encoding.ASCII.GetBytes(pdfText);
        using var source = new MemoryByteSource(bytes);
        var lexer = new PdfLexer(source);

        // 123
        var t1 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Integer, t1.Type);
        Assert.Equal(123, t1.IntValue);

        // +45
        var t2 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Integer, t2.Type);
        Assert.Equal(45, t2.IntValue);

        // -678
        var t3 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Integer, t3.Type);
        Assert.Equal(-678, t3.IntValue);

        // 3.14159
        var t4 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Real, t4.Type);
        Assert.Equal(3.14159, t4.RealValue, precision: 4);

        // /Name With Spaces
        var t5 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Name, t5.Type);
        Assert.Equal("Name With Spaces", t5.TextValue);

        // (Hello (nested) World\n)
        var t6 = lexer.NextToken();
        Assert.Equal(PdfTokenType.String, t6.Type);
        Assert.Equal("Hello (nested) World\n", Encoding.ASCII.GetString(t6.BinaryValue.Span));

        // <48656C6C6F> -> "Hello"
        var t7 = lexer.NextToken();
        Assert.Equal(PdfTokenType.HexString, t7.Type);
        Assert.Equal("Hello", Encoding.ASCII.GetString(t7.BinaryValue.Span));

        // true
        var t8 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, t8.Type);
        Assert.Equal("true", t8.TextValue);

        // false
        var t9 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, t9.Type);
        Assert.Equal("false", t9.TextValue);

        // null
        var t10 = lexer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, t10.Type);
        Assert.Equal("null", t10.TextValue);

        // [
        var t11 = lexer.NextToken();
        Assert.Equal(PdfTokenType.BeginArray, t11.Type);
    }

    [Fact]
    public void Parser_ParsesDictionaryAndArray()
    {
        string text = @"<< /Title (Sample Doc) /PageCount 5 /MediaBox [ 0 0 612 792 ] >>";
        using var source = new MemoryByteSource(Encoding.ASCII.GetBytes(text));
        var lexer = new PdfLexer(source);
        var parser = new PdfParser();

        var obj = parser.ParseObject(lexer);
        Assert.NotNull(obj);
        var dict = Assert.IsType<PdfDictionary>(obj);

        Assert.Equal("Sample Doc", dict.GetString("Title"));
        Assert.Equal(5, dict.GetInteger("PageCount"));

        var boxObj = dict["MediaBox"];
        var boxArr = Assert.IsType<PdfArray>(boxObj);
        Assert.Equal(4, boxArr.Count);
        Assert.Equal(612, ((PdfInteger)boxArr[2]).Value);
        Assert.Equal(792, ((PdfInteger)boxArr[3]).Value);
    }

    [Fact]
    public void StreamDecoder_FlateAndHexDecode_WorksCorrectly()
    {
        // Hex test
        byte[] hexInput = "48 65 6c 6C 6f 21>"u8.ToArray();
        byte[] hexDecoded = PdfStreamDecoder.DecodeAsciiHex(hexInput);
        Assert.Equal("Hello!", Encoding.ASCII.GetString(hexDecoded));

        // ASCII85 test
        byte[] a85Input = "87cURD_*$B~>"u8.ToArray(); // "Hello World" in ASCII85
        byte[] a85Decoded = PdfStreamDecoder.DecodeAscii85(a85Input);
        Assert.StartsWith("Hello", Encoding.ASCII.GetString(a85Decoded));
    }

    [Fact]
    public void PredictorDecoder_TiffAndPng_CorrectlyDecodes()
    {
        // PNG Sub filter test: row = [filter=1, delta1, delta2, delta3]
        // raw = 10, 25, 40 -> delta = 10, 15, 15
        byte[] pngSub = [1, 10, 15, 15];
        byte[] decoded = PredictorDecoder.DecodePredictor(pngSub, predictor: 11, columns: 3, colors: 1, bitsPerComponent: 8);
        Assert.Equal(3, decoded.Length);
        Assert.Equal(10, decoded[0]);
        Assert.Equal(25, decoded[1]);
        Assert.Equal(40, decoded[2]);
    }

    [Fact]
    public void MinimalPdf_ParsesCatalogAndPages()
    {
        // Synthetic minimal compliant PDF 1.4 document
        string pdfContent =
@"%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 300 400 ] /Contents 4 0 R >>
endobj
4 0 obj
<< /Length 23 >>
stream
q 1 0 0 1 10 20 cm Q
endstream
endobj
xref
0 5
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000116 00000 n 
0000000205 00000 n 
trailer
<< /Size 5 /Root 1 0 R >>
startxref
276
%%EOF";

        byte[] pdfBytes = Encoding.ASCII.GetBytes(pdfContent.Replace("\r\n", "\n"));
        using var source = new MemoryByteSource(pdfBytes);

        var xref = new PdfXrefTable();
        xref.Load(source);

        Assert.Equal(5, xref.Entries.Count);
        Assert.NotNull(xref.Trailer);

        var resolver = new PdfObjectResolver(source, xref);
        var root = resolver.Resolve(xref.Trailer["Root"]);
        Assert.NotNull(root);
        var catalog = Assert.IsType<PdfDictionary>(root);

        var pageTree = new PdfPageTree(resolver);
        pageTree.Load(catalog);

        Assert.Equal(1, pageTree.Count);
        var page1 = pageTree.Pages[0];
        Assert.Equal(1, page1.PageNumber);
        Assert.Equal(300, page1.MediaBox.Width);
        Assert.Equal(400, page1.MediaBox.Height);
        Assert.Single(page1.Contents);

        var streamData = page1.Contents[0].GetRawBytes();
        string streamStr = Encoding.ASCII.GetString(streamData.Span);
        Assert.Contains("q 1 0 0 1 10 20 cm Q", streamStr);
    }
}
