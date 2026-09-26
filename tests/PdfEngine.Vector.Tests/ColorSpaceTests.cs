using System;
using System.Collections.Generic;
using System.Text;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Xref;
using Xunit;
using static PdfEngine.Vector.Tests.FunctionTests;

namespace PdfEngine.Vector.Tests;

public class ColorSpaceTests
{
    private static readonly PdfObjectResolver Empty = EmptyResolver();

    private static PdfName N(string name) => new(name);
    private static PdfArray A(params PdfObject[] items) => new(items);

    private static void AssertColor(PdfColor actual, double r, double g, double b, double tolerance = 0.01)
    {
        Assert.True(Math.Abs(actual.R - r) <= tolerance, $"R: expected {r}, got {actual.R}");
        Assert.True(Math.Abs(actual.G - g) <= tolerance, $"G: expected {g}, got {actual.G}");
        Assert.True(Math.Abs(actual.B - b) <= tolerance, $"B: expected {b}, got {actual.B}");
    }

    private static PdfStream IccStream(int n, PdfObject? alternate = null)
    {
        var entries = new List<(string, PdfObject)> { ("N", new PdfInteger(n)) };
        if (alternate != null) entries.Add(("Alternate", alternate));
        return StreamDecoderTests.MakeStream([0, 0, 0, 0], entries.ToArray());
    }

    /// <summary>Builds a resolver over a synthetic PDF whose objects are numbered 1..n in order.</summary>
    private static PdfObjectResolver BuildResolver(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xrefOffset = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} >>\nstartxref\n{xrefOffset}\n%%EOF");

        var source = new MemoryByteSource(Encoding.ASCII.GetBytes(sb.ToString()));
        var xref = new PdfXrefTable();
        xref.Load(source);
        return new PdfObjectResolver(source, xref);
    }

    // ---------------- Device spaces ----------------

    [Fact]
    public void DeviceNames_ResolveToDeviceSpaces()
    {
        Assert.Same(PdfColorSpace.DeviceGray, PdfColorSpace.Resolve(N("DeviceGray"), Empty));
        Assert.Same(PdfColorSpace.DeviceRgb, PdfColorSpace.Resolve(N("RGB"), Empty));
        Assert.Same(PdfColorSpace.DeviceCmyk, PdfColorSpace.Resolve(N("DeviceCMYK"), Empty));
        Assert.Null(PdfColorSpace.DeviceRgb.UnsupportedReason);
    }

    [Fact]
    public void InitialComponents_FollowSpec()
    {
        Assert.Equal(new[] { 0.0 }, PdfColorSpace.DeviceGray.InitialComponents);
        Assert.Equal(new[] { 0.0, 0, 0 }, PdfColorSpace.DeviceRgb.InitialComponents);
        Assert.Equal(new[] { 0.0, 0, 0, 1 }, PdfColorSpace.DeviceCmyk.InitialComponents);
    }

    [Fact]
    public void ToRgb24_DeviceSpaces()
    {
        var rgb = new byte[6];
        PdfColorSpace.DeviceCmyk.ToRgb24([1, 0, 0, 0, 0, 0, 0, 1], rgb);
        Assert.Equal(new byte[] { 0, 255, 255, 0, 0, 0 }, rgb);

        PdfColorSpace.DeviceGray.ToRgb24([0.5, 1], rgb);
        Assert.Equal(new byte[] { 128, 128, 128, 255, 255, 255 }, rgb);

        PdfColorSpace.DeviceRgb.ToRgb24([1, 0, 0, 0, 0, 1], rgb);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, rgb);
    }

    // ---------------- ICCBased ----------------

    [Fact]
    public void IccBased_N4_UsesCmyk()
    {
        var cs = PdfColorSpace.Resolve(A(N("ICCBased"), IccStream(4)), Empty);
        Assert.Equal(4, cs.NumberOfComponents);
        Assert.Null(cs.UnsupportedReason);
        AssertColor(cs.ToRgbColor([1, 0, 0, 0]), 0, 1, 1);
        AssertColor(cs.ToRgbColor([0, 0, 0, 1]), 0, 0, 0);
    }

    [Fact]
    public void IccBased_N1_UsesGray_NotRgb()
    {
        var cs = PdfColorSpace.Resolve(A(N("ICCBased"), IccStream(1)), Empty);
        Assert.Equal(1, cs.NumberOfComponents);
        AssertColor(cs.ToRgbColor([0.5]), 0.5, 0.5, 0.5);
    }

    [Fact]
    public void IccBased_PrefersCompatibleAlternate()
    {
        var alternate = A(N("Lab"), Dict(("WhitePoint", Nums(0.9505, 1, 1.089))));
        var cs = PdfColorSpace.Resolve(A(N("ICCBased"), IccStream(3, alternate)), Empty);
        var icc = Assert.IsType<PdfColorSpace.IccBasedColorSpace>(cs);
        Assert.IsType<PdfColorSpace.LabColorSpace>(icc.AlternateColorSpace);
    }

    [Fact]
    public void IccBased_UnusableN_IsFlagged()
    {
        var cs = PdfColorSpace.Resolve(A(N("ICCBased"), IccStream(5)), Empty);
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, cs.UnsupportedReason);
        Assert.Equal(5, cs.NumberOfComponents);
    }

    // ---------------- CIE ----------------

    [Fact]
    public void Lab_White_And_Black()
    {
        var cs = PdfColorSpace.Resolve(A(N("Lab"), Dict(("WhitePoint", Nums(0.9642, 1, 0.8249)))), Empty);
        Assert.Equal(3, cs.NumberOfComponents);
        AssertColor(cs.ToRgbColor([100, 0, 0]), 1, 1, 1);
        AssertColor(cs.ToRgbColor([0, 0, 0]), 0, 0, 0);
    }

    [Fact]
    public void Lab_RangesAndInitialComponents()
    {
        var cs = PdfColorSpace.Resolve(A(N("Lab"), Dict(
            ("WhitePoint", Nums(0.9505, 1, 1.089)),
            ("Range", Nums(10, 20, -50, 50)))), Empty);
        Assert.Equal((0.0, 100.0), cs.GetComponentRange(0));
        Assert.Equal((10.0, 20.0), cs.GetComponentRange(1));
        Assert.Equal(new[] { 0.0, 10, 0 }, cs.InitialComponents);
    }

    [Fact]
    public void CalGray_AppliesGamma()
    {
        var cs = PdfColorSpace.Resolve(A(N("CalGray"), Dict(("WhitePoint", Nums(0.9505, 1, 1.089)), ("Gamma", new PdfReal(2.2)))), Empty);
        Assert.Equal("CalGray", cs.Name);
        var mid = cs.ToRgbColor([0.5]);
        Assert.InRange(mid.R, 0.45f, 0.55f); // gamma 2.2 roughly cancels the sRGB curve
        AssertColor(cs.ToRgbColor([1]), 1, 1, 1);
    }

    [Fact]
    public void CalRgb_SrgbLikeMatrix_MapsPrimaries()
    {
        // sRGB primaries (D65) as a CalRGB matrix.
        var dict = Dict(
            ("WhitePoint", Nums(0.9505, 1, 1.089)),
            ("Gamma", Nums(2.2, 2.2, 2.2)),
            ("Matrix", Nums(0.4124, 0.2126, 0.0193, 0.3576, 0.7152, 0.1192, 0.1805, 0.0722, 0.9505)));
        var cs = PdfColorSpace.Resolve(A(N("CalRGB"), dict), Empty);
        AssertColor(cs.ToRgbColor([1, 0, 0]), 1, 0, 0, 0.02);
        AssertColor(cs.ToRgbColor([1, 1, 1]), 1, 1, 1, 0.02);
    }

    // ---------------- Indexed ----------------

    [Fact]
    public void Indexed_OverIccBased()
    {
        byte[] lookup = [255, 0, 0, 0, 0, 255];
        var cs = PdfColorSpace.Resolve(A(N("Indexed"), A(N("ICCBased"), IccStream(3)), new PdfInteger(1), new PdfString(lookup)), Empty);
        var indexed = Assert.IsType<PdfColorSpace.IndexedColorSpace>(cs);
        Assert.Equal((0.0, 1.0), cs.GetComponentRange(0));
        Assert.Equal(new[] { 0.0 }, cs.InitialComponents);
        AssertColor(cs.ToRgbColor([0]), 1, 0, 0);
        AssertColor(cs.ToRgbColor([1]), 0, 0, 1);
        AssertColor(cs.ToRgbColor([7]), 0, 0, 1); // clamped to hival

        var rgb = new byte[6];
        indexed.ToRgb24([1, 0], rgb);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0 }, rgb);
    }

    [Fact]
    public void Indexed_ShortLookup_RendersBlackNotThrow()
    {
        var cs = PdfColorSpace.Resolve(A(N("I"), N("DeviceRGB"), new PdfInteger(3), new PdfString(new byte[] { 255, 255, 255 })), Empty);
        AssertColor(cs.ToRgbColor([0]), 1, 1, 1);
        AssertColor(cs.ToRgbColor([2]), 0, 0, 0);
    }

    [Fact]
    public void Indexed_OverLab_MapsBytesToLabRange()
    {
        // L byte 255 -> 100, a/b byte 128 -> ~0 with range -128..127.
        var lab = A(N("Lab"), Dict(("WhitePoint", Nums(0.9505, 1, 1.089)), ("Range", Nums(-128, 127, -128, 127))));
        var cs = PdfColorSpace.Resolve(A(N("Indexed"), lab, new PdfInteger(0), new PdfString(new byte[] { 255, 128, 128 })), Empty);
        AssertColor(cs.ToRgbColor([0]), 1, 1, 1, 0.02);
    }

    // ---------------- Separation / DeviceN ----------------

    [Fact]
    public void Separation_Type2TintIntoCmyk()
    {
        var cs = PdfColorSpace.Resolve(A(N("Separation"), N("Spot"), N("DeviceCMYK"), Type2([0, 0, 0, 0], [1, 0, 0, 0], 1)), Empty);
        var sep = Assert.IsType<PdfColorSpace.SeparationColorSpace>(cs);
        Assert.Equal("Spot", sep.ColorantName);
        Assert.Null(cs.UnsupportedReason);
        Assert.Equal(new[] { 1.0 }, cs.InitialComponents);
        AssertColor(cs.ToRgbColor([1]), 0, 1, 1);
        AssertColor(cs.ToRgbColor([0]), 1, 1, 1);
        AssertColor(cs.ToRgbColor([0.5]), 0.5, 1, 1);

        var rgb = new byte[3];
        cs.ToRgb24([1], rgb);
        Assert.Equal(new byte[] { 0, 255, 255 }, rgb);
    }

    [Fact]
    public void Separation_None_IsNoneAndTransparent()
    {
        var cs = PdfColorSpace.Resolve(A(N("Separation"), N("None"), N("DeviceGray"), Type2([1], [0], 1)), Empty);
        Assert.True(cs.IsNone);
        Assert.Equal(0f, cs.ToRgbColor([1]).A);
    }

    [Fact]
    public void Separation_All_IsGrayInverseTint()
    {
        var cs = PdfColorSpace.Resolve(A(N("Separation"), N("All"), N("DeviceCMYK"), Type2([0, 0, 0, 0], [1, 1, 0, 0], 1)), Empty);
        Assert.False(cs.IsNone);
        AssertColor(cs.ToRgbColor([1]), 0, 0, 0);
        AssertColor(cs.ToRgbColor([0.25]), 0.75, 0.75, 0.75);
    }

    [Fact]
    public void Separation_BadTintTransform_IsFlagged()
    {
        var cs = PdfColorSpace.Resolve(A(N("Separation"), N("Spot"), N("DeviceRGB"), Type2([0], [1], 1)), Empty); // 1 output, RGB needs 3
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, cs.UnsupportedReason);
        AssertColor(cs.ToRgbColor([1]), 0, 0, 0);
    }

    [Fact]
    public void DeviceN_Type4TintIntoCmyk()
    {
        var tint = Type4("{ 0 0 }", [0, 1, 0, 1], [0, 1, 0, 1, 0, 1, 0, 1]);
        var cs = PdfColorSpace.Resolve(A(N("DeviceN"), A(N("Cyan"), N("Magenta")), N("DeviceCMYK"), tint), Empty);
        Assert.Equal(2, cs.NumberOfComponents);
        Assert.Null(cs.UnsupportedReason);
        Assert.Equal(new[] { 1.0, 1.0 }, cs.InitialComponents);
        AssertColor(cs.ToRgbColor([1, 1]), 0, 0, 1);
        AssertColor(cs.ToRgbColor([1, 0]), 0, 1, 1);
    }

    [Fact]
    public void DeviceN_AllNone_IsNone()
    {
        var tint = Type4("{ pop pop 0 }", [0, 1, 0, 1], [0, 1]);
        var cs = PdfColorSpace.Resolve(A(N("DeviceN"), A(N("None"), N("None")), N("DeviceGray"), tint), Empty);
        Assert.True(cs.IsNone);
    }

    // ---------------- Pattern ----------------

    [Fact]
    public void Pattern_IsFlaggedAndKeepsBase()
    {
        var plain = PdfColorSpace.Resolve(N("Pattern"), Empty);
        Assert.True(plain.IsPattern);
        Assert.Equal(PdfFallbackReason.Pattern, plain.UnsupportedReason);
        Assert.Null(plain.PatternBaseColorSpace);
        Assert.Equal(0, plain.NumberOfComponents);

        var uncolored = PdfColorSpace.Resolve(A(N("Pattern"), N("DeviceRGB")), Empty);
        Assert.True(uncolored.IsPattern);
        Assert.Same(PdfColorSpace.DeviceRgb, uncolored.PatternBaseColorSpace);
        Assert.Equal(3, uncolored.NumberOfComponents);
        AssertColor(uncolored.ToRgbColor([1, 0, 0]), 1, 0, 0);
    }

    // ---------------- Resource lookup / unknown ----------------

    [Fact]
    public void NamedResource_IsResolved()
    {
        var resources = Dict(("ColorSpace", Dict(("CS0", A(N("ICCBased"), IccStream(4))))));
        var cs = PdfColorSpace.Resolve(N("CS0"), Empty, resources);
        Assert.Equal(4, cs.NumberOfComponents);
    }

    [Fact]
    public void UnknownName_IsFlagged_NotSilentRgb()
    {
        var cs = PdfColorSpace.Resolve(N("CSMissing"), Empty, Dict());
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, cs.UnsupportedReason);
        Assert.Equal(1, cs.NumberOfComponents);

        var family = PdfColorSpace.Resolve(A(N("Bogus"), new PdfInteger(1)), Empty);
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, family.UnsupportedReason);

        var nothing = PdfColorSpace.Resolve(null, Empty);
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, nothing.UnsupportedReason);
    }

    // ---------------- Cycle safety ----------------

    [Fact]
    public void CyclicResourceNames_DoNotOverflow()
    {
        var resources = Dict(("ColorSpace", Dict(("A", N("B")), ("B", N("A")))));
        var cs = PdfColorSpace.Resolve(N("A"), Empty, resources);
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, cs.UnsupportedReason);
    }

    [Fact]
    public void SelfReferencingIndexed_DoesNotOverflow()
    {
        var resolver = BuildResolver("[/Indexed 1 0 R 0 <FF>]");
        var cs = PdfColorSpace.Resolve(new PdfIndirectRef(1), resolver);
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, cs.UnsupportedReason);
    }

    [Fact]
    public void MutuallyReferencingIccAlternates_DoNotOverflow()
    {
        var resolver = BuildResolver(
            "[/ICCBased 2 0 R]",
            "<< /N 3 /Alternate 1 0 R /Length 0 >>\nstream\n\nendstream");
        var cs = PdfColorSpace.Resolve(new PdfIndirectRef(1), resolver);
        // The alternate chain is cut at the depth limit; /N 3 still yields a usable RGB rendering.
        Assert.Equal(3, cs.NumberOfComponents);
    }

    [Fact]
    public void DeepIndexedChain_IsBounded()
    {
        PdfObject cs = N("DeviceGray");
        for (int i = 0; i < 20; i++)
            cs = A(N("Indexed"), cs, new PdfInteger(0), new PdfString(new byte[] { 0 }));
        var resolved = PdfColorSpace.Resolve(cs, Empty);
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, resolved.UnsupportedReason);
    }
}
