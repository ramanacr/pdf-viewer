using System;
using System.Collections.Generic;
using System.Text;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fonts;
using Xunit;
using static PdfEngine.Vector.Tests.Fonts.Pdf;

namespace PdfEngine.Vector.Tests;

public class FontTests
{
    private static PdfFont Resolve(PdfDictionary fontDict, string name = "F1") =>
        new PdfFontResolver(Resolver()).ResolveFont(fontDict, name);

    private static PdfDictionary SimpleFont(string baseFont, params (string, PdfObject)[] extra)
    {
        var entries = new List<(string, PdfObject)>
        {
            ("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N(baseFont)),
        };
        entries.AddRange(extra);
        return Dict(entries.ToArray());
    }

    private static PdfFont FontWithToUnicode(string cmap) =>
        Resolve(SimpleFont("Helvetica", ("ToUnicode", Stream(cmap))));

    // ---- Resolver cache ------------------------------------------------------------------------

    [Fact]
    public void Cache_IsKeyedByFontDictionaryIdentity_NotResourceName()
    {
        var resolver = new PdfFontResolver(Resolver());
        var helv = SimpleFont("Helvetica");
        var times = SimpleFont("Times-Roman");
        var pageResources = Dict(("Font", Dict(("F1", helv))));
        var formResources = Dict(("Font", Dict(("F1", times))));

        var a = resolver.ResolveFont("F1", pageResources);
        var b = resolver.ResolveFont("F1", formResources);

        Assert.Equal("Helvetica", a.BaseFont);
        Assert.Equal("Times-Roman", b.BaseFont);
        Assert.Same(a, resolver.ResolveFont("F1", pageResources));
        Assert.Same(b, resolver.ResolveFont(times, "F7"));
    }

    [Fact]
    public void MissingFont_IsSubstitutedAndFlagged()
    {
        var resolver = new PdfFontResolver(Resolver());
        var font = resolver.ResolveFont("F9", Dict(("Font", Dict())));
        Assert.True(font.IsMissingResource);
        Assert.Equal("Helvetica", font.BaseFont);
        Assert.True(resolver.ResolveFont("F1", null).IsMissingResource);

        Assert.False(Resolve(SimpleFont("Helvetica")).IsMissingResource);
    }

    // ---- Encodings -----------------------------------------------------------------------------

    [Fact]
    public void WinAnsiEncoding_MapsHighCodes()
    {
        var font = Resolve(SimpleFont("Helvetica", ("Encoding", N("WinAnsiEncoding"))));
        Assert.Equal("€", font.MapToUnicode(0x80));
        Assert.Equal("“", font.MapToUnicode(0x93));
        Assert.Equal("—", font.MapToUnicode(0x97));
        Assert.Equal("'", font.MapToUnicode(0x27));
        Assert.Equal("é", font.MapToUnicode(0xE9));
        Assert.Equal("Euro", font.GetGlyphName(0x80));
    }

    [Fact]
    public void MacRomanEncoding_MapsHighCodes()
    {
        var font = Resolve(SimpleFont("Helvetica", ("Encoding", N("MacRomanEncoding"))));
        Assert.Equal("é", font.MapToUnicode(0x8E));
        Assert.Equal("•", font.MapToUnicode(0xA5));
        Assert.Equal("ﬁ", font.MapToUnicode(0xDE));
        Assert.Equal("Ä", font.MapToUnicode(0x80));
    }

    [Fact]
    public void Differences_OverrideBaseEncoding_WithAglRules()
    {
        var encoding = Dict(
            ("Type", N("Encoding")),
            ("BaseEncoding", N("WinAnsiEncoding")),
            ("Differences", A(I(65), N("germandbls"), N("uni20AC"), N("f_i"), I(200), N("a.sc"), N("u1F600"), N("zzunknown"))));
        var font = Resolve(SimpleFont("Helvetica", ("Encoding", encoding)));

        Assert.Equal("ß", font.MapToUnicode(65));
        Assert.Equal("€", font.MapToUnicode(66));
        Assert.Equal("fi", font.MapToUnicode(67));
        Assert.Equal("D", font.MapToUnicode(68)); // untouched WinAnsi
        Assert.Equal("a", font.MapToUnicode(200));
        Assert.Equal("\U0001F600", font.MapToUnicode(201));
        Assert.Equal(((char)202).ToString(), font.MapToUnicode(202)); // unknown name -> Latin-1 last resort
        Assert.Equal("uni20AC", font.GetGlyphName(66));
        Assert.Equal("zzunknown", font.GetGlyphName(202));
    }

    [Fact]
    public void NoEncoding_UsesBuiltInStandardEncoding_AndSymbolBuiltIn()
    {
        var helv = Resolve(SimpleFont("Helvetica"));
        Assert.Equal("’", helv.MapToUnicode(0x27)); // quoteright
        Assert.Equal("ﬁ", helv.MapToUnicode(0xAE)); // fi

        var symbol = Resolve(SimpleFont("Symbol"));
        Assert.Equal("α", symbol.MapToUnicode(0x61));
        Assert.Equal("∀", symbol.MapToUnicode(0x22));

        var dingbats = Resolve(SimpleFont("ZapfDingbats"));
        Assert.Equal("✁", dingbats.MapToUnicode(0x21));
        Assert.Equal("★", dingbats.MapToUnicode(0x48));
    }

    [Fact]
    public void ToUnicode_TakesPriorityOverEncoding()
    {
        var font = Resolve(SimpleFont("Helvetica",
            ("Encoding", N("WinAnsiEncoding")),
            ("ToUnicode", Stream("1 beginbfchar <41> <0058> endbfchar"))));
        Assert.Equal("X", font.MapToUnicode(0x41));
        Assert.Equal("B", font.MapToUnicode(0x42));
    }

    [Fact]
    public void GlyphList_LatinExtendedAIsAligned()
    {
        Assert.Equal("Ā", GlyphList.ToUnicode("Amacron"));
        Assert.Equal("ı", GlyphList.ToUnicode("dotlessi"));
        Assert.Equal("Ž", GlyphList.ToUnicode("Zcaron"));
        Assert.Equal("ſ", GlyphList.ToUnicode("longs"));
        Assert.Equal("AB", GlyphList.ToUnicode("uni00410042"));
        Assert.Null(GlyphList.ToUnicode("uniD800"));
        Assert.Null(GlyphList.ToUnicode(".notdef"));
    }

    // ---- ToUnicode CMap ------------------------------------------------------------------------

    [Fact]
    public void ToUnicode_MultipleEntriesPerLine_AndEntriesSpanningLines()
    {
        var font = FontWithToUnicode(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "3 beginbfchar <01> <0041> <02> <0042>\n<03>\n<0043> endbfchar\n" +
            "1 beginbfrange <10>\n<12> <0061> endbfrange endcmap");
        Assert.Equal("A", font.MapToUnicode(1));
        Assert.Equal("B", font.MapToUnicode(2));
        Assert.Equal("C", font.MapToUnicode(3));
        Assert.Equal("a", font.MapToUnicode(0x10));
        Assert.Equal("c", font.MapToUnicode(0x12));
    }

    [Fact]
    public void ToUnicode_BfRangeWithArrayDestination()
    {
        var font = FontWithToUnicode("1 beginbfrange <0001> <0003> [<0041> <0042> <00660069>] endbfrange");
        Assert.Equal("A", font.ToUnicodeMap![1]);
        Assert.Equal("B", font.ToUnicodeMap[2]);
        Assert.Equal("fi", font.ToUnicodeMap[3]);
    }

    [Fact]
    public void ToUnicode_SurrogatePairsAndLigatures()
    {
        var font = FontWithToUnicode(
            "2 beginbfchar <05> <D835DC00> <06> <006600660069> endbfchar\n" +
            "1 beginbfrange <07> <08> <D835DC00> endbfrange\n" +
            "1 beginbfchar <000102> <0058> endbfchar\n" +
            "1 beginbfchar <01020304> <0059> endbfchar");
        Assert.Equal("\U0001D400", font.MapToUnicode(5));
        Assert.Equal("ffi", font.MapToUnicode(6));
        Assert.Equal("\U0001D400", font.MapToUnicode(7));
        Assert.Equal("\U0001D401", font.MapToUnicode(8));
        Assert.Equal("X", font.MapToUnicode(0x000102));
        Assert.Equal("Y", font.MapToUnicode(0x01020304));
    }

    [Fact]
    public void ToUnicode_MalformedInput_DoesNotThrow()
    {
        string[] hostile =
        {
            "1 beginbfchar <0G1> <zz endbfchar",
            "beginbfrange <41> <40> <0041> endbfrange",
            "beginbfchar <> <> (x) /A endbfchar",
            "beginbfrange [ [ [ <41> ",
            "<<<<<>>>>>> ] ] ) ( \\",
            "1 beginbfchar <0102030405> <0041> endbfchar",
            "",
        };
        foreach (string cmap in hostile)
        {
            var font = FontWithToUnicode(cmap);
            Assert.NotNull(font.ToUnicodeMap);
        }

        var odd = FontWithToUnicode("1 beginbfchar <4> <41> endbfchar");
        Assert.Equal("A", odd.MapToUnicode(0x40)); // odd hex digit count pads with 0
    }

    [Fact]
    public void ToUnicode_HugeRange_IsCapped()
    {
        var font = FontWithToUnicode("1 beginbfrange <00000000> <FFFFFFFF> <0041> endbfrange");
        Assert.Equal(CMapParser.MaxCodesPerRange, font.ToUnicodeMap!.Count);
        Assert.Equal("A", font.MapToUnicode(0));
    }

    [Fact]
    public void ToUnicode_TotalEntries_AreCapped()
    {
        var sb = new StringBuilder("100 beginbfrange\n");
        for (int i = 0; i < 20; i++)
            sb.Append($"<{i:X2}000000> <{i:X2}00FFFF> <0041>\n");
        sb.Append("endbfrange");
        var parsed = CMapParser.Parse(Encoding.ASCII.GetBytes(sb.ToString()));
        Assert.Equal(CMapParser.MaxTotalEntries, parsed.Unicode.Count);
    }

    // ---- Standard 14 metrics -------------------------------------------------------------------

    [Theory]
    [InlineData("Helvetica", ' ', 278)]
    [InlineData("Helvetica", 'A', 667)]
    [InlineData("Helvetica", 'a', 556)]
    [InlineData("Helvetica", 'i', 222)]
    [InlineData("Helvetica", 'm', 833)]
    [InlineData("Helvetica", 'W', 944)]
    [InlineData("Helvetica-Oblique", 'i', 222)]
    [InlineData("Helvetica-Bold", 'A', 722)]
    [InlineData("Helvetica-Bold", 'i', 278)]
    [InlineData("Times-Roman", ' ', 250)]
    [InlineData("Times-Roman", 'A', 722)]
    [InlineData("Times-Roman", 'a', 444)]
    [InlineData("Times-Roman", 'i', 278)]
    [InlineData("Times-Roman", 'm', 778)]
    [InlineData("Times-Bold", 'a', 500)]
    [InlineData("Times-Italic", 'a', 500)]
    [InlineData("Courier", 'W', 600)]
    [InlineData("Courier-BoldOblique", 'i', 600)]
    [InlineData("ABCDEF+Helvetica", 'm', 833)]
    [InlineData("Arial,Bold", 'A', 722)]
    public void Standard14_Widths_MatchAfm(string baseFont, char c, double expected)
    {
        var font = Resolve(SimpleFont(baseFont, ("Encoding", N("WinAnsiEncoding"))));
        Assert.Equal(expected, font.GetGlyphWidth(c));
    }

    [Fact]
    public void Standard14_Widths_AreKeyedByGlyphName()
    {
        var font = Resolve(SimpleFont("Helvetica",
            ("Encoding", Dict(("Differences", A(I(1), N("A"), N("fi"), N("quotesingle"), N("Euro")))))));
        Assert.Equal(667, font.GetGlyphWidth(1));
        Assert.Equal(500, font.GetGlyphWidth(2));
        Assert.Equal(191, font.GetGlyphWidth(3));
        Assert.Equal(556, font.GetGlyphWidth(4));

        var winAnsi = Resolve(SimpleFont("Times-Roman", ("Encoding", N("WinAnsiEncoding"))));
        Assert.Equal(500, winAnsi.GetGlyphWidth(0x80));

        var symbol = Resolve(SimpleFont("Symbol"));
        Assert.Equal(631, symbol.GetGlyphWidth(0x61)); // alpha
    }

    [Fact]
    public void Widths_Present_OutOfRangeUsesMissingWidth_DefaultZero()
    {
        var font = Resolve(SimpleFont("Helvetica",
            ("FirstChar", I(65)), ("LastChar", I(66)), ("Widths", Nums(600, 700))));
        Assert.Equal(600, font.GetGlyphWidth(65));
        Assert.Equal(700, font.GetGlyphWidth(66));
        Assert.Equal(0, font.GetGlyphWidth(67));
        Assert.Equal(0, font.MissingWidth);

        var withMissing = Resolve(SimpleFont("Custom",
            ("FirstChar", I(65)), ("LastChar", I(65)), ("Widths", Nums(600)),
            ("FontDescriptor", Dict(("MissingWidth", I(250)), ("Flags", I(32))))));
        Assert.Equal(250, withMissing.GetGlyphWidth(10));
    }

    // ---- Composite fonts -----------------------------------------------------------------------

    private static PdfDictionary Type0(PdfObject encoding, PdfDictionary cidFont, params (string, PdfObject)[] extra)
    {
        var entries = new List<(string, PdfObject)>
        {
            ("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("ABCDEF+MyCid-Identity-H")),
            ("Encoding", encoding), ("DescendantFonts", A(cidFont)),
        };
        entries.AddRange(extra);
        return Dict(entries.ToArray());
    }

    private static PdfDictionary CidFont(string subtype = "CIDFontType2", params (string, PdfObject)[] extra)
    {
        var entries = new List<(string, PdfObject)>
        {
            ("Type", N("Font")), ("Subtype", N(subtype)), ("BaseFont", N("ABCDEF+MyCid")),
        };
        entries.AddRange(extra);
        return Dict(entries.ToArray());
    }

    [Fact]
    public void IdentityH_DecodesTwoByteCodes()
    {
        var font = Resolve(Type0(N("Identity-H"), CidFont()));
        byte[] bytes = { 0x00, 0x41, 0x01, 0x02, 0x07 };

        Assert.Equal(2, font.ReadCode(bytes, 0, out int code, out int cid));
        Assert.Equal(0x41, code);
        Assert.Equal(0x41, cid);
        Assert.Equal(2, font.ReadCode(bytes, 2, out code, out cid));
        Assert.Equal(0x0102, cid);
        Assert.Equal(1, font.ReadCode(bytes, 4, out _, out _)); // truncated tail still consumes
        Assert.Equal(1, font.ReadCode(bytes, 99, out _, out _));
        Assert.False(font.IsVertical);
        Assert.Null(font.UnsupportedReason);
        Assert.Equal("MyCid", font.PostScriptName);

        var vertical = Resolve(Type0(N("Identity-V"), CidFont()));
        Assert.True(vertical.IsVertical);
    }

    [Fact]
    public void SimpleFont_ReadCode_IsOneByte()
    {
        var font = Resolve(SimpleFont("Helvetica"));
        Assert.Equal(1, font.ReadCode(new byte[] { 0x20, 0x41 }, 1, out int code, out int cid));
        Assert.Equal(0x41, code);
        Assert.Equal(0x41, cid);
    }

    [Fact]
    public void EmbeddedCMap_MixedOneAndTwoByteCodespaces()
    {
        var cmap = Stream(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "/CMapName /Test-H def /WMode 0 def\n" +
            "2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange\n" +
            "2 begincidrange <00> <7F> 1 <8140> <817F> 1000 endcidrange\n" +
            "1 begincidchar <9040> 5000 endcidchar\n" +
            "endcmap end end", ("Type", N("CMap")));
        var font = Resolve(Type0(cmap, CidFont()));
        byte[] bytes = { 0x41, 0x81, 0x41, 0x20, 0x90, 0x40, 0xA0 };

        int offset = 0;
        var results = new List<(int Code, int Cid, int Len)>();
        while (offset < bytes.Length)
        {
            int n = font.ReadCode(bytes, offset, out int code, out int cid);
            Assert.True(n >= 1);
            results.Add((code, cid, n));
            offset += n;
        }

        Assert.Equal((0x41, 0x42, 1), results[0]);
        Assert.Equal((0x8141, 1001, 2), results[1]);
        Assert.Equal((0x20, 0x21, 1), results[2]);
        Assert.Equal((0x9040, 5000, 2), results[3]);
        Assert.Equal(0, results[4].Cid); // 0xA0 matches no codespace -> notdef
        Assert.Null(font.UnsupportedReason);
        Assert.True(font.IsWordSpaceCode(results[2].Code, results[2].Len));
    }

    [Fact]
    public void EmbeddedCMap_UseCMapIdentity_AndUnsupportedBase()
    {
        var withIdentity = Resolve(Type0(Stream("/Identity-H usecmap 1 begincidchar <0041> 7 endcidchar"), CidFont()));
        byte[] bytes = { 0x00, 0x41, 0x00, 0x42 };
        withIdentity.ReadCode(bytes, 0, out _, out int cid1);
        withIdentity.ReadCode(bytes, 2, out _, out int cid2);
        Assert.Equal(7, cid1);
        Assert.Equal(0x42, cid2);
        Assert.Null(withIdentity.UnsupportedReason);

        // usecmap of a shipped predefined CMap resolves (Adobe cmap-resources); an unknown name is flagged.
        var withPredefined = Resolve(Type0(Stream("/UniJIS-UCS2-H usecmap"), CidFont()));
        Assert.Null(withPredefined.UnsupportedReason);
        withPredefined.ReadCode(new byte[] { 0x30, 0x42 }, 0, out _, out int hiraganaA);
        Assert.Equal(843, hiraganaA);
        var withUnknown = Resolve(Type0(Stream("/No-Such-CMap-H usecmap"), CidFont()));
        Assert.Equal(PdfFallbackReason.UnsupportedCMap, withUnknown.UnsupportedReason);

        var vertical = Resolve(Type0(Stream("/WMode 1 def 1 begincodespacerange <0000> <FFFF> endcodespacerange"), CidFont()));
        Assert.True(vertical.IsVertical);
    }

    [Fact]
    public void EmbeddedCMap_UseCMapChain_IsDepthLimited()
    {
        // Build a UseCMap chain deeper than the limit; resolution must terminate and flag it.
        PdfStream current = Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange");
        for (int i = 0; i < 10; i++)
            current = Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange", ("UseCMap", current));
        var font = Resolve(Type0(current, CidFont()));
        Assert.Equal(PdfFallbackReason.UnsupportedCMap, font.UnsupportedReason);
        Assert.Equal(2, font.ReadCode(new byte[] { 1, 2 }, 0, out _, out _));
    }

    [Fact]
    public void PredefinedCMap_IsResolved_UnknownNameIsFlagged()
    {
        var font = Resolve(Type0(N("UniJIS-UCS2-H"), CidFont()));
        Assert.Null(font.UnsupportedReason);
        Assert.Equal("あ", font.MapToUnicode(0x3042));

        var rksj = Resolve(Type0(N("90ms-RKSJ-H"), CidFont()));
        Assert.Null(rksj.UnsupportedReason);
        Assert.Equal(2, rksj.ReadCode(new byte[] { 0x82, 0xA0 }, 0, out _, out int cid)); // Shift-JIS lead byte
        Assert.Equal(843, cid);
        Assert.Equal(1, rksj.ReadCode(new byte[] { 0x41 }, 0, out _, out _));

        var unknown = Resolve(Type0(N("Adobe-Korea1-2"), CidFont()));
        Assert.Equal(PdfFallbackReason.UnsupportedCMap, unknown.UnsupportedReason);
    }

    [Fact]
    public void CidToGidMap_Stream_And_Identity()
    {
        byte[] sfnt = SfntBuilder.Build((3, 1, SfntBuilder.Format0(new Dictionary<int, int>())));
        var descriptor = Dict(("Type", N("FontDescriptor")), ("FontName", N("MyCid")), ("Flags", I(4)),
            ("FontFile2", Stream(sfnt)));
        var map = Stream(new byte[] { 0, 0, 0, 5, 0x01, 0x09 });
        var font = Resolve(Type0(N("Identity-H"), CidFont("CIDFontType2", ("CIDToGIDMap", map), ("FontDescriptor", descriptor))));

        Assert.Equal(PdfFontProgramKind.TrueType, font.EmbeddedProgramKind);
        Assert.Equal(sfnt.Length, font.EmbeddedFontData!.Length);
        Assert.Equal(5, font.GetGlyphId(1, 1));
        Assert.Equal(0x109, font.GetGlyphId(2, 2));
        Assert.Equal(0, font.GetGlyphId(7, 7));

        var identity = Resolve(Type0(N("Identity-H"), CidFont("CIDFontType2", ("CIDToGIDMap", N("Identity")), ("FontDescriptor", descriptor))));
        Assert.Equal(42, identity.GetGlyphId(42, 42));

        var cff = Resolve(Type0(N("Identity-H"), CidFont("CIDFontType0", ("FontDescriptor", Dict(
            ("FontFile3", Stream(new byte[] { 1, 0, 4, 2 }, ("Subtype", N("CIDFontType0C")))))))));
        Assert.Equal(PdfFontProgramKind.CidFontType0C, cff.EmbeddedProgramKind);
        Assert.Equal(-1, cff.GetGlyphId(1, 1));
    }

    [Fact]
    public void CidWidths_EdgeCases_AndVerticalMetrics()
    {
        var w = A(I(1), Nums(500, 600), R(10.0), I(12), I(300), N("bad"), I(20), A(I(700), N("x"), I(710)),
            I(30), I(40), A(I(1)), I(50));
        var w2 = A(I(5), Nums(-900, 250, 800), I(8), I(9), I(-1100), I(300), I(880));
        var font = Resolve(Type0(N("Identity-V"), CidFont("CIDFontType2",
            ("W", w), ("DW", I(800)), ("W2", w2))));

        Assert.Equal(500, font.GetGlyphWidth(1));
        Assert.Equal(600, font.GetGlyphWidth(2));
        Assert.Equal(300, font.GetGlyphWidth(10));
        Assert.Equal(300, font.GetGlyphWidth(12));
        Assert.Equal(700, font.GetGlyphWidth(20));
        Assert.Equal(710, font.GetGlyphWidth(22));
        Assert.Equal(800, font.GetGlyphWidth(21)); // malformed entry skipped -> DW
        Assert.Equal(800, font.GetGlyphWidth(99));

        Assert.Equal(new PdfVerticalMetrics(-900, 250, 800), font.GetVerticalMetrics(5));
        Assert.Equal(new PdfVerticalMetrics(-1100, 300, 880), font.GetVerticalMetrics(9));
        Assert.Equal(new PdfVerticalMetrics(-1000, 400, 880), font.GetVerticalMetrics(99)); // DW2 default, VX = w0/2
        Assert.Equal(880, font.DefaultVerticalPositionY);
        Assert.Equal(-1000, font.DefaultVerticalAdvance);
    }

    [Fact]
    public void CidWidths_HugeRange_IsCapped()
    {
        var font = Resolve(Type0(N("Identity-H"), CidFont("CIDFontType2", ("W", A(I(0), I(int.MaxValue), I(500))))));
        Assert.Equal(65536, font.CidWidths!.Count);
    }

    // ---- TrueType cmap -------------------------------------------------------------------------

    [Fact]
    public void TrueTypeCmap_Format0()
    {
        byte[] sfnt = SfntBuilder.Build((1, 0, SfntBuilder.Format0(new Dictionary<int, int> { [0x41] = 3, [0xFF] = 9 })));
        var cmap = TrueTypeCmap.TryParse(sfnt)!;
        Assert.True(cmap.Has(1, 0));
        Assert.Equal(3, cmap.Lookup(1, 0, 0x41));
        Assert.Equal(9, cmap.Lookup(1, 0, 0xFF));
        Assert.Equal(0, cmap.Lookup(1, 0, 0x42));
        Assert.Equal(0, cmap.Lookup(1, 0, 0x1000));
    }

    [Fact]
    public void TrueTypeCmap_Format4_DeltaAndRangeOffset()
    {
        byte[] sub = SfntBuilder.Format4(new[] { (0x41, 0x43, 10) }, new[] { (0x20AC, new[] { 77, 78 }) });
        var cmap = TrueTypeCmap.TryParse(SfntBuilder.Build((3, 1, sub)))!;
        Assert.Equal(10, cmap.Lookup(3, 1, 0x41));
        Assert.Equal(12, cmap.Lookup(3, 1, 0x43));
        Assert.Equal(77, cmap.Lookup(3, 1, 0x20AC));
        Assert.Equal(78, cmap.Lookup(3, 1, 0x20AD));
        Assert.Equal(0, cmap.Lookup(3, 1, 0x44));
        Assert.Equal(0, cmap.Lookup(3, 1, 0x20));
    }

    [Fact]
    public void TrueTypeCmap_Format12()
    {
        byte[] sub = SfntBuilder.Format12((0x41, 0x5A, 100), (0x1F600, 0x1F602, 500));
        var cmap = TrueTypeCmap.TryParse(SfntBuilder.Build((3, 10, sub)))!;
        Assert.Equal(100, cmap.Lookup(3, 10, 0x41));
        Assert.Equal(125, cmap.Lookup(3, 10, 0x5A));
        Assert.Equal(502, cmap.Lookup(3, 10, 0x1F602));
        Assert.Equal(0, cmap.Lookup(3, 10, 0x1F603));
    }

    [Fact]
    public void TrueTypeCmap_TruncatedOrGarbage_DoesNotThrow()
    {
        byte[] good = SfntBuilder.Build((3, 1, SfntBuilder.Format4(new[] { (0x41, 0x43, 10) })));
        for (int len = 0; len < good.Length; len += 3)
        {
            var cmap = TrueTypeCmap.TryParse(good.AsSpan(0, len).ToArray());
            cmap?.Lookup(3, 1, 0x41);
        }
        var rnd = new Random(1234);
        for (int i = 0; i < 200; i++)
        {
            byte[] junk = new byte[rnd.Next(0, 200)];
            rnd.NextBytes(junk);
            TrueTypeCmap.TryParse(junk)?.LookupFirst(0x41);
        }
        Assert.Null(TrueTypeCmap.TryParse(null));
    }

    private static PdfDictionary TrueTypeFont(byte[] sfnt, int flags, params (string, PdfObject)[] extra)
    {
        var entries = new List<(string, PdfObject)>
        {
            ("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", N("QWERTY+MyTT")),
            ("FontDescriptor", Dict(("Flags", I(flags)), ("FontFile2", Stream(sfnt)), ("Ascent", I(900)), ("Descent", I(-250)),
                ("FontBBox", Nums(-100, -250, 1000, 900)))),
        };
        entries.AddRange(extra);
        return Dict(entries.ToArray());
    }

    [Fact]
    public void SimpleTrueType_Symbolic_Uses30WithF000Prefix()
    {
        byte[] sub = SfntBuilder.Format4(new[] { (0xF041, 0xF042, 7) });
        var font = Resolve(TrueTypeFont(SfntBuilder.Build((3, 0, sub)), flags: 4));
        Assert.True(font.IsSymbolic);
        Assert.Equal(7, font.GetGlyphId(0x41, 0x41));
        Assert.Equal(8, font.GetGlyphId(0x42, 0x42));
        Assert.Equal(0, font.GetGlyphId(0x43, 0x43));
        Assert.Equal("MyTT", font.PostScriptName);
        Assert.Equal(0.9, font.Ascent, 6);
        Assert.Equal(-0.25, font.Descent, 6);
        Assert.Equal(new PdfEngine.Geometry.PdfRect(-100, -250, 1100, 1150), font.FontBBox);
    }

    [Fact]
    public void SimpleTrueType_NonSymbolic_Uses31ViaGlyphName_Then10ViaMacRoman()
    {
        byte[] sub31 = SfntBuilder.Format4(new[] { (0x41, 0x43, 10) }, new[] { (0x20AC, new[] { 77 }) });
        var font = Resolve(TrueTypeFont(SfntBuilder.Build((3, 1, sub31)), flags: 32, ("Encoding", N("WinAnsiEncoding"))));
        Assert.False(font.IsSymbolic);
        Assert.Equal(77, font.GetGlyphId(0x80, 0x80)); // Euro via AGL
        Assert.Equal(10, font.GetGlyphId(0x41, 0x41));

        // (1,0) only: WinAnsi 0xE9 (eacute) -> MacRoman 0x8E.
        byte[] sub10 = SfntBuilder.Format0(new Dictionary<int, int> { [0x8E] = 33 });
        var mac = Resolve(TrueTypeFont(SfntBuilder.Build((1, 0, sub10)), flags: 32, ("Encoding", N("WinAnsiEncoding"))));
        Assert.Equal(33, mac.GetGlyphId(0xE9, 0xE9));
        Assert.Equal(0, mac.GetGlyphId(0x41, 0x41));

        var noProgram = Resolve(SimpleFont("Helvetica"));
        Assert.Equal(-1, noProgram.GetGlyphId(0x41, 0x41));
        Assert.Equal(PdfFontProgramKind.None, noProgram.EmbeddedProgramKind);
    }

    // ---- Type3 ---------------------------------------------------------------------------------

    [Fact]
    public void Type3_WidthsConvertedThroughFontMatrix()
    {
        var charProcs = Dict(("square", Stream("0 0 50 50 re f")));
        var resources = Dict(("ProcSet", A(N("PDF"))));
        var font = Resolve(Dict(
            ("Type", N("Font")), ("Subtype", N("Type3")),
            ("FontMatrix", Nums(0.01, 0, 0, 0.01, 0, 0)),
            ("FontBBox", Nums(0, 0, 50, 50)),
            ("FirstChar", I(65)), ("LastChar", I(66)), ("Widths", Nums(50, 25)),
            ("Encoding", Dict(("Differences", A(I(65), N("square"), N("uni0042"))))),
            ("CharProcs", charProcs), ("Resources", resources)));

        Assert.True(font.IsType3);
        Assert.Equal(PdfFallbackReason.Type3Font, font.UnsupportedReason);
        Assert.Equal(0.01, font.FontMatrix.A);
        Assert.Equal(500, font.GetGlyphWidth(65), 6);
        Assert.Equal(250, font.GetGlyphWidth(66), 6);
        Assert.Equal("square", font.GetGlyphName(65));
        Assert.Null(font.GetGlyphName(67));
        Assert.Same(charProcs, font.CharProcs);
        Assert.Same(resources, font.Type3Resources);
        Assert.Equal("B", font.MapToUnicode(66));
    }

    [Fact]
    public void NonType3_HasDefaultFontMatrix()
    {
        var font = Resolve(SimpleFont("Helvetica"));
        Assert.Equal(0.001, font.FontMatrix.A);
        Assert.Equal(0.001, font.FontMatrix.D);
        Assert.False(font.IsType3);
    }

    // ---- Word spacing / descriptor -------------------------------------------------------------

    [Fact]
    public void WordSpace_AppliesOnlyToSingleByteCode32()
    {
        var simple = Resolve(SimpleFont("Helvetica"));
        Assert.True(simple.IsWordSpaceCode(32, 1));
        Assert.False(simple.IsWordSpaceCode(33, 1));

        var composite = Resolve(Type0(N("Identity-H"), CidFont()));
        int n = composite.ReadCode(new byte[] { 0x00, 0x20 }, 0, out int code, out _);
        Assert.Equal(32, code);
        Assert.False(composite.IsWordSpaceCode(code, n));
    }

    [Fact]
    public void Descriptor_FlagsAndNameHeuristics()
    {
        var font = Resolve(SimpleFont("ABCDEF+Garamond-BoldItalic",
            ("FontDescriptor", Dict(("Flags", I(1 | 2)), ("ItalicAngle", I(0)), ("FontWeight", I(400))))));
        Assert.True(font.IsFixedPitch);
        Assert.True(font.IsSerif);
        Assert.True(font.IsBold);   // name heuristic
        Assert.True(font.IsItalic); // name heuristic
        Assert.False(font.IsSymbolic);
        Assert.Equal("Garamond-BoldItalic", font.PostScriptName);
        Assert.Equal(0.8, font.Ascent);
        Assert.Equal(-0.2, font.Descent);

        var weighted = Resolve(SimpleFont("Plain",
            ("FontDescriptor", Dict(("Flags", I(64)), ("FontWeight", I(700)),
                ("FontFile3", Stream(new byte[] { 1, 0, 4, 1 }, ("Subtype", N("Type1C"))))))));
        Assert.True(weighted.IsBold);
        Assert.True(weighted.IsItalic);
        Assert.Equal(PdfFontProgramKind.Type1C, weighted.EmbeddedProgramKind);

        var type1 = Resolve(SimpleFont("Plain", ("FontDescriptor", Dict(("FontFile", Stream(new byte[] { 0x25, 0x21 }))))));
        Assert.Equal(PdfFontProgramKind.Type1, type1.EmbeddedProgramKind);

        var otf = Resolve(SimpleFont("Plain", ("FontDescriptor", Dict(
            ("FontFile3", Stream(new byte[] { 0x4F, 0x54 }, ("Subtype", N("OpenType"))))))));
        Assert.Equal(PdfFontProgramKind.OpenType, otf.EmbeddedProgramKind);
    }

    [Fact]
    public void NonEmbeddedFontWithoutWidths_UsesSubstituteMetricsFromFlags()
    {
        var serif = Resolve(SimpleFont("SomeSerif", ("FontDescriptor", Dict(("Flags", I(2 | 32))))));
        Assert.Equal(722, serif.GetGlyphWidth('A')); // Times-Roman
        var mono = Resolve(SimpleFont("SomeMono", ("FontDescriptor", Dict(("Flags", I(1 | 32))))));
        Assert.Equal(600, mono.GetGlyphWidth('A'));
    }
}
