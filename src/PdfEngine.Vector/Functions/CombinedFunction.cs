using System;
using System.Linq;

namespace PdfEngine.Vector.Functions;

/// <summary>
/// An array of n single-output functions evaluated on the same inputs, yielding n outputs
/// (ISO 32000-2 8.7.4.5.1: shading /Function may be "an array of n 1-out functions").
/// </summary>
public sealed class CombinedFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;

    internal CombinedFunction(PdfFunction[] functions)
        : base(functions[0].Domain.ToArray(), null, functions.Length)
    {
        _functions = functions;
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> x, Span<double> y)
    {
        Span<double> single = stackalloc double[1];
        for (int j = 0; j < _functions.Length; j++)
        {
            _functions[j].Evaluate(x, single);
            y[j] = single[0];
        }
    }
}
