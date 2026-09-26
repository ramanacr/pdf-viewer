using System;
using PdfEngine.Vector.Fonts;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Predefined CJK CMaps (ISO 32000-2 Table 116) shipped from Adobe cmap-resources: code → CID
/// lookups agree across encodings of the same character collection, -V variants are vertical
/// and override only the vertical forms, and usecmap chains resolve.
/// </summary>
public class PredefinedCMapTests
{
    private static int Cid(string cmapName, params byte[] code)
    {
        var cmap = PredefinedCMaps.Get(cmapName);
        Assert.NotNull(cmap);
        int consumed = cmap!.ReadCode(code, 0, out _, out int cid);
        Assert.Equal(code.Length, consumed);
        return cid;
    }

    [Fact]
    public void AllTable116CMapsLoad()
    {
        foreach (var name in "GB-EUC-H GBK-EUC-H GBK2K-V UniGB-UCS2-H UniGB-UTF16-V B5pc-H HKscs-B5-V ETenms-B5-H CNS-EUC-V UniCNS-UCS2-H UniCNS-UTF16-V 83pv-RKSJ-H 90ms-RKSJ-V 90msp-RKSJ-H Add-RKSJ-V EUC-H Ext-RKSJ-V H V UniJIS-UCS2-HW-V UniJIS-UTF16-H KSC-EUC-V KSCms-UHC-HW-H KSCpc-EUC-H UniKS-UCS2-V UniKS-UTF16-H".Split(' '))
            Assert.NotNull(PredefinedCMaps.Get(name));
        Assert.Null(PredefinedCMaps.Get("Adobe-Korea1-2")); // a collection name, not a CMap
    }

    [Fact]
    public void Hiragana_A_IsCid843_InEveryJapaneseEncoding()
    {
        Assert.Equal(843, Cid("UniJIS-UCS2-H", 0x30, 0x42));  // U+3042
        Assert.Equal(843, Cid("UniJIS-UTF16-H", 0x30, 0x42));
        Assert.Equal(843, Cid("90ms-RKSJ-H", 0x82, 0xA0));    // Shift-JIS
        Assert.Equal(843, Cid("EUC-H", 0xA4, 0xA2));          // EUC-JP
        Assert.Equal(843, Cid("H", 0x24, 0x22));              // ISO-2022-JP (JIS X 0208)
    }

    [Fact]
    public void SingleByteCodes_UseTheirOwnCodespace()
    {
        // 90ms-RKSJ: 0x41 'A' is one byte, mapped to half-width Roman (CID 231 + code - 0x20).
        Assert.Equal(264, Cid("90ms-RKSJ-H", 0x41));
    }

    [Fact]
    public void KoreanEncodingsAgree()
    {
        int uni = Cid("UniKS-UCS2-H", 0xAC, 0x00);  // U+AC00 가
        Assert.True(uni > 0);
        Assert.Equal(uni, Cid("KSC-EUC-H", 0xB0, 0xA1));
        Assert.Equal(uni, Cid("KSCms-UHC-H", 0xB0, 0xA1));
    }

    [Fact]
    public void VerticalVariants_AreVertical_AndOverrideOnlyVerticalForms()
    {
        var v = PredefinedCMaps.Get("UniJIS-UCS2-V")!;
        Assert.True(v.IsVertical);
        Assert.False(PredefinedCMaps.Get("UniJIS-UCS2-H")!.IsVertical);
        Assert.NotEqual(Cid("UniJIS-UCS2-H", 0x30, 0x01), Cid("UniJIS-UCS2-V", 0x30, 0x01)); // 、 has a vertical form
        Assert.Equal(Cid("UniJIS-UCS2-H", 0x30, 0x42), Cid("UniJIS-UCS2-V", 0x30, 0x42));    // あ does not
    }

    [Fact]
    public void CidToUnicode_InvertsTheUtf16CMaps()
    {
        Assert.Equal("あ", PredefinedCMaps.CidToUnicode("Japan1", 843));
        Assert.Equal("A", PredefinedCMaps.CidToUnicode("Japan1", 34));
        Assert.Equal("가", PredefinedCMaps.CidToUnicode("Korea1", Cid("UniKS-UCS2-H", 0xAC, 0x00)));
        Assert.Equal("中", PredefinedCMaps.CidToUnicode("GB1", Cid("UniGB-UCS2-H", 0x4E, 0x2D)));
        Assert.Equal("中", PredefinedCMaps.CidToUnicode("CNS1", Cid("UniCNS-UCS2-H", 0x4E, 0x2D)));
        Assert.Null(PredefinedCMaps.CidToUnicode("Identity", 5));
    }
}
