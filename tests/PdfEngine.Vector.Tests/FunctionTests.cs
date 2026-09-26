using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Functions;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Xref;
using Xunit;

namespace PdfEngine.Vector.Tests;

public class FunctionTests
{
    internal static PdfObjectResolver EmptyResolver() =>
        new(new MemoryByteSource(Array.Empty<byte>()), new PdfXrefTable());

    internal static PdfArray Nums(params double[] values) =>
        new(values.Select(v => v == Math.Floor(v) ? (PdfObject)new PdfInteger((long)v) : new PdfReal(v)).ToArray());

    internal static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new Dictionary<string, PdfObject>();
        foreach (var (k, v) in entries) d[k] = v;
        return new PdfDictionary(d);
    }

    internal static PdfDictionary Type2(double[] c0, double[] c1, double n) => Dict(
        ("FunctionType", new PdfInteger(2)),
        ("Domain", Nums(0, 1)),
        ("C0", Nums(c0)),
        ("C1", Nums(c1)),
        ("N", new PdfReal(n)));

    internal static PdfStream Type4(string program, double[] domain, double[] range) =>
        StreamDecoderTests.MakeStream(Encoding.ASCII.GetBytes(program),
            ("FunctionType", new PdfInteger(4)),
            ("Domain", Nums(domain)),
            ("Range", Nums(range)));

    private static double[] Eval(PdfFunction f, params double[] input)
    {
        var output = new double[f.OutputCount];
        f.Evaluate(input, output);
        return output;
    }

    private static PdfFunction Parse(PdfObject obj) =>
        PdfFunction.Parse(obj, EmptyResolver(), PdfSecurityLimits.Default);

    // ---------------- Type 2 ----------------

    [Fact]
    public void Type2_LinearAndExponent()
    {
        var f = Parse(Type2([0, 0, 0], [1, 0.5, 0], 1));
        Assert.Equal(1, f.InputCount);
        Assert.Equal(3, f.OutputCount);
        Assert.Equal(new[] { 0.5, 0.25, 0 }, Eval(f, 0.5));

        var squared = Parse(Type2([0], [1], 2));
        Assert.Equal(0.25, Eval(squared, 0.5)[0], 10);
    }

    [Fact]
    public void Type2_ClampsInputToDomainAndOutputToRange()
    {
        var dict = Dict(
            ("FunctionType", new PdfInteger(2)),
            ("Domain", Nums(0, 1)),
            ("Range", Nums(0, 0.8)),
            ("N", new PdfInteger(1)));
        var f = Parse(dict);
        Assert.Equal(0.0, Eval(f, -5)[0]);
        Assert.Equal(0.8, Eval(f, 5)[0]);
    }

    [Fact]
    public void Type2_MissingN_ThrowsTypedUnsupported()
    {
        var dict = Dict(("FunctionType", new PdfInteger(2)), ("Domain", Nums(0, 1)));
        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(dict));
        Assert.Equal(PdfFallbackReason.Shading, ex.Reason);
    }

    [Fact]
    public void Parse_FailureReason_IsConfigurable()
    {
        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() =>
            PdfFunction.Parse(new PdfInteger(3), EmptyResolver(), PdfSecurityLimits.Default, PdfFallbackReason.UnsupportedColorSpace));
        Assert.Equal(PdfFallbackReason.UnsupportedColorSpace, ex.Reason);
    }

    // ---------------- Type 0 ----------------

    [Fact]
    public void Type0_OneInput_LinearInterpolation()
    {
        // 3 samples, 8-bit, one output: 0, 255, 0 -> tent.
        var stream = StreamDecoderTests.MakeStream([0, 255, 0],
            ("FunctionType", new PdfInteger(0)),
            ("Domain", Nums(0, 1)),
            ("Range", Nums(0, 1)),
            ("Size", Nums(3)),
            ("BitsPerSample", new PdfInteger(8)));
        var f = Parse(stream);
        Assert.Equal(0.0, Eval(f, 0)[0], 6);
        Assert.Equal(0.5, Eval(f, 0.25)[0], 6);
        Assert.Equal(1.0, Eval(f, 0.5)[0], 6);
        Assert.Equal(0.0, Eval(f, 1)[0], 6);
    }

    [Fact]
    public void Type0_TwoInputs_Bilinear_WithDecode()
    {
        // 2x2 grid, 16-bit samples, one output, decoded to [0, 10].
        byte[] data = [0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF]; // s(0,0)=0 s(1,0)=max s(0,1)=0 s(1,1)=max
        var stream = StreamDecoderTests.MakeStream(data,
            ("FunctionType", new PdfInteger(0)),
            ("Domain", Nums(0, 1, 0, 1)),
            ("Range", Nums(0, 10)),
            ("Size", Nums(2, 2)),
            ("BitsPerSample", new PdfInteger(16)));
        var f = Parse(stream);
        Assert.Equal(2, f.InputCount);
        Assert.Equal(5.0, Eval(f, 0.5, 0.3)[0], 6);
        Assert.Equal(10.0, Eval(f, 1, 1)[0], 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(32)]
    public void Type0_AllBitDepths_ReadMaxSampleAsRangeMax(int bps)
    {
        // Two samples: 0 then all-ones; the second decodes to Range max.
        int totalBits = 2 * bps;
        var data = new byte[(totalBits + 7) / 8];
        for (int bit = bps; bit < totalBits; bit++) data[bit / 8] |= (byte)(0x80 >> (bit % 8));
        var stream = StreamDecoderTests.MakeStream(data,
            ("FunctionType", new PdfInteger(0)),
            ("Domain", Nums(0, 1)),
            ("Range", Nums(0, 1)),
            ("Size", Nums(2)),
            ("BitsPerSample", new PdfInteger(bps)));
        var f = Parse(stream);
        Assert.Equal(0.0, Eval(f, 0)[0], 6);
        Assert.Equal(1.0, Eval(f, 1)[0], 6);
    }

    [Fact]
    public void Type0_TooManySamples_ThrowsResourceLimit()
    {
        var stream = StreamDecoderTests.MakeStream([0],
            ("FunctionType", new PdfInteger(0)),
            ("Domain", Nums(0, 1, 0, 1)),
            ("Range", Nums(0, 1, 0, 1, 0, 1)),
            ("Size", Nums(100_000, 100_000)),
            ("BitsPerSample", new PdfInteger(8)));
        var ex = Assert.Throws<PdfResourceLimitException>(() => Parse(stream));
        Assert.Equal(nameof(PdfSecurityLimits.MaxFunctionSamples), ex.LimitName);
    }

    [Fact]
    public void Type0_ShortData_ReadsMissingSamplesAsZero()
    {
        var stream = StreamDecoderTests.MakeStream([255],
            ("FunctionType", new PdfInteger(0)),
            ("Domain", Nums(0, 1)),
            ("Range", Nums(0, 1)),
            ("Size", Nums(4)),
            ("BitsPerSample", new PdfInteger(8)));
        var f = Parse(stream);
        Assert.Equal(1.0, Eval(f, 0)[0], 6);
        Assert.Equal(0.0, Eval(f, 1)[0], 6);
    }

    [Fact]
    public void Type0_BadBitsPerSample_ThrowsTyped()
    {
        var stream = StreamDecoderTests.MakeStream([0],
            ("FunctionType", new PdfInteger(0)),
            ("Domain", Nums(0, 1)),
            ("Range", Nums(0, 1)),
            ("Size", Nums(2)),
            ("BitsPerSample", new PdfInteger(7)));
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(stream));
    }

    // ---------------- Type 3 ----------------

    [Fact]
    public void Type3_StitchesSubFunctions()
    {
        var dict = Dict(
            ("FunctionType", new PdfInteger(3)),
            ("Domain", Nums(0, 1)),
            ("Functions", new PdfArray([Type2([0], [1], 1), Type2([1], [0], 1)])),
            ("Bounds", Nums(0.5)),
            ("Encode", Nums(0, 1, 0, 1)));
        var f = Parse(dict);
        Assert.Equal(0.0, Eval(f, 0)[0], 6);
        Assert.Equal(0.5, Eval(f, 0.25)[0], 6);
        Assert.Equal(1.0, Eval(f, 0.5)[0], 6); // second sub-domain starts at the bound
        Assert.Equal(0.5, Eval(f, 0.75)[0], 6);
        Assert.Equal(0.0, Eval(f, 1)[0], 6);
    }

    [Fact]
    public void Type3_ReversedEncode()
    {
        var dict = Dict(
            ("FunctionType", new PdfInteger(3)),
            ("Domain", Nums(0, 1)),
            ("Functions", new PdfArray([Type2([0], [1], 1)])),
            ("Bounds", Nums()),
            ("Encode", Nums(1, 0)));
        Assert.Equal(0.75, Eval(Parse(dict), 0.25)[0], 6);
    }

    [Fact]
    public void Type3_DeepNesting_ThrowsTypedNotStackOverflow()
    {
        PdfObject f = Type2([0], [1], 1);
        for (int i = 0; i < 20; i++)
        {
            f = Dict(
                ("FunctionType", new PdfInteger(3)),
                ("Domain", Nums(0, 1)),
                ("Functions", new PdfArray([f])),
                ("Bounds", Nums()),
                ("Encode", Nums(0, 1)));
        }
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(f));
    }

    [Fact]
    public void Type3_BoundsOutsideDomain_ThrowsTyped()
    {
        var dict = Dict(
            ("FunctionType", new PdfInteger(3)),
            ("Domain", Nums(0, 1)),
            ("Functions", new PdfArray([Type2([0], [1], 1), Type2([1], [0], 1)])),
            ("Bounds", Nums(2)),
            ("Encode", Nums(0, 1, 0, 1)));
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(dict));
    }

    // ---------------- Function arrays ----------------

    [Fact]
    public void ParseMany_CombinesSingleOutputFunctions()
    {
        var array = new PdfArray([Type2([0], [1], 1), Type2([1], [0], 1), Type2([0.5], [0.5], 1)]);
        var f = PdfFunction.ParseMany(array, EmptyResolver(), PdfSecurityLimits.Default);
        Assert.Equal(3, f.OutputCount);
        Assert.Equal(new[] { 0.25, 0.75, 0.5 }, Eval(f, 0.25));
    }

    [Fact]
    public void ParseMany_RejectsMultiOutputMembers()
    {
        var array = new PdfArray([Type2([0, 0], [1, 1], 1), Type2([1], [0], 1)]);
        Assert.Throws<PdfUnsupportedFeatureException>(() =>
            PdfFunction.ParseMany(array, EmptyResolver(), PdfSecurityLimits.Default));
    }

    // ---------------- Type 4 ----------------

    [Theory]
    [InlineData("{ 2 mul }", 0.25, 0.5)]
    [InlineData("{ dup mul }", 0.5, 0.25)]
    [InlineData("{ 1 exch sub }", 0.25, 0.75)]
    [InlineData("{ 0.5 gt { 1 } { 0 } ifelse }", 0.75, 1)]
    [InlineData("{ 0.5 gt { 1 } { 0 } ifelse }", 0.25, 0)]
    [InlineData("{ dup 0.5 lt { pop 0.1 } if }", 0.25, 0.1)]
    [InlineData("{ dup 0.5 lt { pop 0.1 } if }", 0.75, 0.75)]
    [InlineData("{ 90 mul sin }", 1, 1)]
    [InlineData("{ 180 mul cos neg }", 1, 1)]
    [InlineData("{ pop 1 1 atan 45 div }", 0, 1)]
    [InlineData("{ 4 mul sqrt 2 div }", 0.25, 0.5)]
    [InlineData("{ 10 mul cvi 3 idiv 10 div }", 0.9, 0.3)]
    [InlineData("{ 10 mul cvi 4 mod 10 div }", 0.7, 0.3)]
    [InlineData("{ 10 mul round 10 div }", 0.25, 0.3)]
    [InlineData("{ 10 mul floor 10 div }", 0.29, 0.2)]
    [InlineData("{ 10 mul ceiling 10 div }", 0.21, 0.3)]
    [InlineData("{ 10 mul truncate 10 div }", 0.29, 0.2)]
    [InlineData("{ pop 2 3 exp 10 div }", 0, 0.8)]
    [InlineData("{ pop 100 log 10 div }", 0, 0.2)]
    [InlineData("{ pop 1 ln }", 0, 0)]
    [InlineData("{ pop 1 2 bitshift 10 div }", 0, 0.4)]
    [InlineData("{ pop 8 -2 bitshift 10 div }", 0, 0.2)]
    [InlineData("{ pop 6 3 and 10 div }", 0, 0.2)]
    [InlineData("{ pop 6 3 or 10 div }", 0, 0.7)]
    [InlineData("{ pop 6 3 xor 10 div }", 0, 0.5)]
    [InlineData("{ pop true false or { 1 } { 0 } ifelse }", 0, 1)]
    [InlineData("{ pop true not { 1 } { 0 } ifelse }", 0, 0)]
    [InlineData("{ pop 1 1 eq 2 3 ne and { 1 } { 0 } ifelse }", 0, 1)]
    [InlineData("{ pop 2 2 ge 1 2 le and { 1 } { 0 } ifelse }", 0, 1)]
    [InlineData("{ pop 0.1 0.2 0.3 3 1 roll pop pop }", 0, 0.3)]
    [InlineData("{ pop 0.1 0.2 0.3 3 -1 roll 3 1 roll pop pop }", 0, 0.1)]
    [InlineData("{ pop 0.1 0.2 1 index exch pop exch pop }", 0, 0.1)]
    [InlineData("{ pop 0.1 0.2 2 copy add add add }", 0, 0.6)]
    [InlineData("{ abs 1 add 2 div cvr }", -1, 1)]
    [InlineData("{ % comment\n 0.5 add }", 0.25, 0.75)]
    public void Type4_Programs(string program, double input, double expected)
    {
        var f = Parse(Type4(program, [-1, 1], [-10, 10]));
        Assert.Equal(expected, Eval(f, input)[0], 6);
    }

    [Fact]
    public void Type4_MultipleOutputs_TakenFromTopOfStack()
    {
        // Gray -> CMYK style program: 1 input, 4 outputs.
        var f = Parse(Type4("{ dup 0 exch 0 exch }", [0, 1], [0, 1, 0, 1, 0, 1, 0, 1]));
        Assert.Equal(4, f.OutputCount);
        Assert.Equal(new[] { 0.5, 0, 0, 0.5 }, Eval(f, 0.5));
    }

    [Theory]
    [InlineData("2 mul")]                    // no braces
    [InlineData("{ 2 mul")]                  // unterminated
    [InlineData("{ 2 frobnicate }")]         // unknown operator
    [InlineData("{ { 1 } }")]                // dangling procedure
    [InlineData("{ if }")]                   // if without procedure
    [InlineData("{ { 1 } ifelse }")]         // ifelse with one procedure
    public void Type4_MalformedPrograms_ThrowTypedAtParse(string program)
    {
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(Type4(program, [0, 1], [0, 1])));
    }

    [Theory]
    [InlineData("{ pop pop }")]              // underflow
    [InlineData("{ 0 div }")]                // division by zero
    [InlineData("{ 1.5 2 idiv }")]           // type error
    [InlineData("{ pop }")]                  // too few outputs
    [InlineData("{ 0.5 gt }")]               // boolean output
    [InlineData("{ neg sqrt }")]             // sqrt of negative (input 0.5)
    public void Type4_RuntimeErrors_ThrowTyped(string program)
    {
        var f = Parse(Type4(program, [0, 1], [0, 1]));
        Assert.Throws<PdfUnsupportedFeatureException>(() => Eval(f, 0.5));
    }

    [Fact]
    public void Type4_StackOverflow_IsBounded()
    {
        string program = "{ " + string.Concat(Enumerable.Repeat("dup ", 200)) + "}";
        var f = Parse(Type4(program, [0, 1], [0, 1]));
        Assert.Throws<PdfUnsupportedFeatureException>(() => Eval(f, 0.5));
    }

    [Fact]
    public void Type4_OperationBudget_IsBounded()
    {
        var limits = new PdfSecurityLimits { MaxCalculatorOperations = 50 };
        string program = "{ " + string.Concat(Enumerable.Repeat("dup pop ", 100)) + "}";
        var f = PdfFunction.Parse(Type4(program, [0, 1], [0, 1]), EmptyResolver(), limits);
        Assert.Throws<PdfUnsupportedFeatureException>(() => Eval(f, 0.5));
    }

    [Fact]
    public void Type4_DeeplyNestedProcedures_ThrowTypedNotStackOverflow()
    {
        string program = "{ " + string.Concat(Enumerable.Repeat("true { ", 5000)) + string.Concat(Enumerable.Repeat("} if ", 5000)) + "}";
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(Type4(program, [0, 1], [0, 1])));
    }

    [Fact]
    public void Type4_RandomGarbage_NeverThrowsUntyped()
    {
        var rng = new Random(42);
        string[] vocabulary = ["{", "}", "1", "0.5", "-2", "add", "dup", "pop", "exch", "roll", "index", "copy", "if", "ifelse",
            "true", "false", "mul", "div", "idiv", "mod", "atan", "bitshift", "cvi", "not", "eq", "sqrt", "ln"];
        for (int trial = 0; trial < 500; trial++)
        {
            var sb = new StringBuilder("{ ");
            int count = rng.Next(0, 30);
            for (int i = 0; i < count; i++) sb.Append(vocabulary[rng.Next(vocabulary.Length)]).Append(' ');
            sb.Append('}');
            try
            {
                var f = Parse(Type4(sb.ToString(), [0, 1], [0, 1]));
                Eval(f, rng.NextDouble());
            }
            catch (PdfVectorException) { }
        }
    }

    // ---------------- General ----------------

    [Fact]
    public void UnknownFunctionType_ThrowsTyped()
    {
        var dict = Dict(("FunctionType", new PdfInteger(7)), ("Domain", Nums(0, 1)));
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(dict));
    }

    [Fact]
    public void MissingDomain_ThrowsTyped()
    {
        var dict = Dict(("FunctionType", new PdfInteger(2)), ("N", new PdfInteger(1)));
        Assert.Throws<PdfUnsupportedFeatureException>(() => Parse(dict));
    }
}
