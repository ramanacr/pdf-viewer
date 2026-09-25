using System;
using System.Collections.Generic;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Functions;

/// <summary>
/// A PDF function object (ISO 32000-2 Clause 7.10): a mapping from m inputs to n outputs used by shadings,
/// Separation/DeviceN tint transforms, transfer functions and soft masks.
/// </summary>
/// <remarks>
/// <para>Inputs are clamped to /Domain and outputs to /Range (when present) on every evaluation (7.10.1).</para>
/// <para>
/// Malformed definitions raise <see cref="PdfUnsupportedFeatureException"/> carrying the caller-supplied
/// <c>failureReason</c> — <see cref="PdfFallbackReason.Shading"/> by default (shadings are the main
/// consumer), <see cref="PdfFallbackReason.UnsupportedColorSpace"/> when parsed as a tint transform.
/// Sample tables above <see cref="PdfSecurityLimits.MaxFunctionSamples"/> raise
/// <see cref="PdfResourceLimitException"/>. Malformed input never surfaces as IndexOutOfRangeException.
/// </para>
/// </remarks>
public abstract class PdfFunction
{
    private readonly double[] _domain;
    private readonly double[]? _range;

    private protected PdfFunction(double[] domain, double[]? range, int outputCount)
    {
        _domain = domain;
        _range = range;
        OutputCount = outputCount;
    }

    /// <summary>Number of input values (m), i.e. half the length of /Domain.</summary>
    public int InputCount => _domain.Length / 2;

    /// <summary>Number of output values (n).</summary>
    public int OutputCount { get; }

    /// <summary>The /Domain array (2 x m values).</summary>
    public IReadOnlyList<double> Domain => _domain;

    /// <summary>The /Range array (2 x n values), or null when the function type allows it to be omitted.</summary>
    public IReadOnlyList<double>? Range => _range;

    /// <summary>
    /// Evaluates the function. Missing inputs default to the Domain minimum; NaN inputs are treated as the
    /// minimum; non-finite outputs become 0 before Range clamping.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="output"/> is shorter than <see cref="OutputCount"/>.</exception>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        if (output.Length < OutputCount)
            throw new ArgumentException($"Output span must hold at least {OutputCount} values.", nameof(output));

        int m = InputCount;
        Span<double> x = m <= 32 ? stackalloc double[m] : new double[m];
        for (int i = 0; i < m; i++)
        {
            double lo = _domain[2 * i], hi = _domain[2 * i + 1];
            double v = i < input.Length ? input[i] : lo;
            x[i] = double.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);
        }

        var y = output[..OutputCount];
        EvaluateCore(x, y);

        for (int j = 0; j < y.Length; j++)
        {
            double v = double.IsFinite(y[j]) ? y[j] : 0.0;
            if (_range != null)
                v = Math.Clamp(v, _range[2 * j], _range[2 * j + 1]);
            y[j] = v;
        }
    }

    /// <summary>Evaluates with inputs already clamped to the Domain; the base class clamps the outputs.</summary>
    private protected abstract void EvaluateCore(ReadOnlySpan<double> x, Span<double> y);

    /// <summary>Parses a single function dictionary or stream (types 0, 2, 3, 4).</summary>
    /// <param name="obj">The function object or an indirect reference to it.</param>
    /// <param name="resolver">Resolver for indirect references.</param>
    /// <param name="limits">Security ceilings.</param>
    /// <param name="failureReason">Reason attached to <see cref="PdfUnsupportedFeatureException"/> for malformed input.</param>
    public static PdfFunction Parse(
        PdfObject? obj,
        PdfObjectResolver resolver,
        PdfSecurityLimits limits,
        PdfFallbackReason failureReason = PdfFallbackReason.Shading)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(limits);
        return Parse(obj, new ParseContext(resolver, limits, failureReason), depth: 0);
    }

    /// <summary>
    /// Parses either a single function or an array of 1-output functions sharing the same inputs, which is
    /// combined into one n-output function (as allowed for shading /Function entries, ISO 32000-2 8.7.4.5).
    /// </summary>
    public static PdfFunction ParseMany(
        PdfObject? obj,
        PdfObjectResolver resolver,
        PdfSecurityLimits limits,
        PdfFallbackReason failureReason = PdfFallbackReason.Shading)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(limits);
        return ParseMany(obj, new ParseContext(resolver, limits, failureReason), depth: 0);
    }

    internal static PdfFunction ParseMany(PdfObject? obj, ParseContext ctx, int depth)
    {
        if (ctx.Resolver.Resolve(obj) is not PdfArray array)
            return Parse(obj, ctx, depth);

        if (array.Count == 0)
            throw ctx.Fail("Function array is empty.");
        if (array.Count > 32)
            throw ctx.Fail("Function array has more than 32 entries.");

        var parts = new PdfFunction[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            parts[i] = Parse(array[i], ctx, depth + 1);
            if (parts[i].OutputCount != 1)
                throw ctx.Fail("Each function in a function array must have exactly one output.");
            if (parts[i].InputCount != parts[0].InputCount)
                throw ctx.Fail("Functions in a function array must share the same number of inputs.");
        }

        return parts.Length == 1 ? parts[0] : new CombinedFunction(parts);
    }

    internal static PdfFunction Parse(PdfObject? obj, ParseContext ctx, int depth)
    {
        if (depth > ctx.Limits.MaxFunctionNestingDepth)
            throw ctx.Fail($"Function nesting depth exceeded {ctx.Limits.MaxFunctionNestingDepth}.");

        var resolved = ctx.Resolver.Resolve(obj);
        PdfStream? stream = resolved as PdfStream;
        PdfDictionary? dict = stream?.Dictionary ?? resolved as PdfDictionary;
        if (dict == null)
            throw ctx.Fail("Function is not a dictionary or stream.");

        long type = ctx.Resolver.Resolve(dict["FunctionType"]) is { } ft && ft.TryGetInteger(out long t) ? t : -1;

        double[] domain = ReadNumbers(dict["Domain"], ctx) ?? throw ctx.Fail("Function /Domain is missing.");
        if (domain.Length < 2 || domain.Length % 2 != 0)
            throw ctx.Fail("Function /Domain must contain 2 x m numbers.");
        for (int i = 0; i < domain.Length; i += 2)
        {
            if (!(domain[i] <= domain[i + 1]))
                throw ctx.Fail("Function /Domain has a minimum above its maximum.");
        }

        double[]? range = ReadNumbers(dict["Range"], ctx);
        if (range != null)
        {
            if (range.Length < 2 || range.Length % 2 != 0)
                throw ctx.Fail("Function /Range must contain 2 x n numbers.");
            for (int i = 0; i < range.Length; i += 2)
            {
                if (!(range[i] <= range[i + 1]))
                    throw ctx.Fail("Function /Range has a minimum above its maximum.");
            }
        }

        return type switch
        {
            0 => SampledFunction.Create(stream ?? throw ctx.Fail("Type 0 function must be a stream."), domain, range, ctx),
            2 => ExponentialFunction.Create(dict, domain, range, ctx),
            3 => StitchingFunction.Create(dict, domain, range, ctx, depth),
            4 => PostScriptCalculatorFunction.Create(stream ?? throw ctx.Fail("Type 4 function must be a stream."), domain, range, ctx),
            _ => throw ctx.Fail($"Unsupported FunctionType {type}."),
        };
    }

    /// <summary>Reads an array of numbers, resolving the array and each element. Returns null if absent.</summary>
    internal static double[]? ReadNumbers(PdfObject? obj, ParseContext ctx)
    {
        var resolved = ctx.Resolver.Resolve(obj);
        if (resolved is null or PdfNull)
            return null;
        if (resolved is not PdfArray array)
            throw ctx.Fail("Expected an array of numbers.");

        var values = new double[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            if (ctx.Resolver.Resolve(array[i]) is not { } item || !item.TryGetNumber(out double v) || !double.IsFinite(v))
                throw ctx.Fail("Expected an array of numbers.");
            values[i] = v;
        }
        return values;
    }

    /// <summary>Linear interpolation of ISO 32000-2 7.10.2 (Interpolate(x, xmin, xmax, ymin, ymax)).</summary>
    internal static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax) =>
        xMax == xMin ? yMin : yMin + (x - xMin) * (yMax - yMin) / (xMax - xMin);

    internal sealed record ParseContext(PdfObjectResolver Resolver, PdfSecurityLimits Limits, PdfFallbackReason Reason)
    {
        public PdfUnsupportedFeatureException Fail(string message) => new(Reason, message);
    }
}
