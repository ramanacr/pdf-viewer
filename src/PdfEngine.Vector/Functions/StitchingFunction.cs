using System;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Functions;

/// <summary>
/// Type 3 stitching function (ISO 32000-2 7.10.4): a 1-input function built from k sub-functions over
/// the sub-domains delimited by /Bounds, each re-mapped through its /Encode pair.
/// </summary>
public sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;
    private readonly double[] _bounds;
    private readonly double[] _encode;

    private StitchingFunction(double[] domain, double[]? range, PdfFunction[] functions, double[] bounds, double[] encode)
        : base(domain, range, functions[0].OutputCount)
    {
        _functions = functions;
        _bounds = bounds;
        _encode = encode;
    }

    internal static StitchingFunction Create(PdfDictionary dict, double[] domain, double[]? range, ParseContext ctx, int depth)
    {
        if (domain.Length != 2)
            throw ctx.Fail("Type 3 function must have exactly one input.");

        if (ctx.Resolver.Resolve(dict["Functions"]) is not PdfArray fnArray || fnArray.Count == 0)
            throw ctx.Fail("Type 3 function requires a non-empty /Functions array.");
        if (fnArray.Count > 256)
            throw ctx.Fail("Type 3 function has too many sub-functions.");

        int k = fnArray.Count;
        var functions = new PdfFunction[k];
        for (int i = 0; i < k; i++)
        {
            functions[i] = Parse(fnArray[i], ctx, depth + 1);
            if (functions[i].InputCount != 1)
                throw ctx.Fail("Type 3 sub-functions must have exactly one input.");
            if (functions[i].OutputCount != functions[0].OutputCount)
                throw ctx.Fail("Type 3 sub-functions must have the same number of outputs.");
        }

        double[] bounds = ReadNumbers(dict["Bounds"], ctx) ?? (k == 1 ? [] : throw ctx.Fail("Type 3 function requires /Bounds."));
        if (bounds.Length != k - 1)
            throw ctx.Fail("Type 3 /Bounds must contain k - 1 numbers.");
        double previous = domain[0];
        foreach (double bound in bounds)
        {
            if (bound < previous || bound > domain[1])
                throw ctx.Fail("Type 3 /Bounds must be increasing and inside /Domain.");
            previous = bound;
        }

        double[] encode = ReadNumbers(dict["Encode"], ctx) ?? throw ctx.Fail("Type 3 function requires /Encode.");
        if (encode.Length != 2 * k)
            throw ctx.Fail("Type 3 /Encode must contain 2 x k numbers.");

        if (range != null && range.Length != 2 * functions[0].OutputCount)
            throw ctx.Fail("Type 3 /Range length does not match its sub-functions.");

        return new StitchingFunction(domain, range, functions, bounds, encode);
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> x, Span<double> y)
    {
        double v = x[0];
        double d0 = Domain[0], d1 = Domain[1];

        // Sub-domain i is [Bounds[i-1], Bounds[i]); the last one is closed at Domain1. When Domain0 equals
        // Bounds0 the first sub-domain degenerates to the single point Domain0 (7.10.4).
        int i = 0;
        while (i < _bounds.Length && v >= _bounds[i])
        {
            if (i == 0 && v == d0 && _bounds[0] == d0)
                break;
            i++;
        }

        double low = i == 0 ? d0 : _bounds[i - 1];
        double high = i == _bounds.Length ? d1 : _bounds[i];
        double e = Interpolate(v, low, high, _encode[2 * i], _encode[2 * i + 1]);

        Span<double> input = [e];
        _functions[i].Evaluate(input, y);
    }
}
