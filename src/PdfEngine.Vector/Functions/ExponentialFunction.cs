using System;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Functions;

/// <summary>
/// Type 2 exponential interpolation function (ISO 32000-2 7.10.3): y_j = C0_j + x^N * (C1_j - C0_j).
/// </summary>
public sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0;
    private readonly double[] _c1;
    private readonly double _exponent;

    private ExponentialFunction(double[] domain, double[]? range, double[] c0, double[] c1, double exponent)
        : base(domain, range, c0.Length)
    {
        _c0 = c0;
        _c1 = c1;
        _exponent = exponent;
    }

    /// <summary>The interpolation exponent /N.</summary>
    public double Exponent => _exponent;

    internal static ExponentialFunction Create(PdfDictionary dict, double[] domain, double[]? range, ParseContext ctx)
    {
        if (domain.Length != 2)
            throw ctx.Fail("Type 2 function must have exactly one input.");

        double[] c0 = ReadNumbers(dict["C0"], ctx) ?? [0.0];
        double[] c1 = ReadNumbers(dict["C1"], ctx) ?? [1.0];
        if (c0.Length != c1.Length || c0.Length == 0)
            throw ctx.Fail("Type 2 /C0 and /C1 must have the same, non-zero length.");
        if (range != null && range.Length != 2 * c0.Length)
            throw ctx.Fail("Type 2 /Range length does not match /C0.");

        if (ctx.Resolver.Resolve(dict["N"]) is not { } nObj || !nObj.TryGetNumber(out double exponent) || !double.IsFinite(exponent))
            throw ctx.Fail("Type 2 function requires a numeric /N.");

        return new ExponentialFunction(domain, range, c0, c1, exponent);
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> x, Span<double> y)
    {
        // Non-integer N with negative x, or negative N with x = 0, is invalid per 7.10.3; the resulting
        // NaN/infinity is mapped to 0 by the base class.
        double t = _exponent == 1.0 ? x[0] : Math.Pow(x[0], _exponent);
        for (int j = 0; j < y.Length; j++)
            y[j] = _c0[j] + t * (_c1[j] - _c0[j]);
    }
}
