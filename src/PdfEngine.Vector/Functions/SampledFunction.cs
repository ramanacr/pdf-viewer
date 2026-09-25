using System;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Functions;

/// <summary>
/// Type 0 sampled function (ISO 32000-2 7.10.2). Samples are decoded once at parse time; evaluation uses
/// multilinear interpolation between the 2^m surrounding samples (/Order 3 cubic is approximated linearly).
/// </summary>
public sealed class SampledFunction : PdfFunction
{
    /// <summary>Highest input dimension supported (2^m corners are visited per evaluation).</summary>
    internal const int MaxInputs = 8;

    private readonly int[] _size;
    private readonly double[] _encode;
    private readonly double[] _samples; // Already mapped through /Decode; layout [sampleIndex * n + j].
    private readonly int[] _strides;

    private SampledFunction(double[] domain, double[] range, int[] size, double[] encode, double[] samples)
        : base(domain, range, range.Length / 2)
    {
        _size = size;
        _encode = encode;
        _samples = samples;
        _strides = new int[size.Length];
        int stride = 1;
        for (int i = 0; i < size.Length; i++)
        {
            _strides[i] = stride; // The first dimension varies fastest (7.10.2).
            stride *= size[i];
        }
    }

    internal static SampledFunction Create(PdfStream stream, double[] domain, double[]? range, ParseContext ctx)
    {
        var dict = stream.Dictionary;
        if (range == null)
            throw ctx.Fail("Type 0 function requires /Range.");

        int m = domain.Length / 2;
        int n = range.Length / 2;
        if (m > MaxInputs)
            throw ctx.Fail($"Type 0 function has {m} inputs; at most {MaxInputs} are supported.");

        double[] sizeValues = ReadNumbers(dict["Size"], ctx) ?? throw ctx.Fail("Type 0 function requires /Size.");
        if (sizeValues.Length != m)
            throw ctx.Fail("Type 0 /Size must have one entry per input.");

        var size = new int[m];
        long total = n;
        for (int i = 0; i < m; i++)
        {
            double s = sizeValues[i];
            if (s < 1 || s != Math.Floor(s) || s > ctx.Limits.MaxFunctionSamples)
                throw ctx.Fail("Type 0 /Size entries must be positive integers.");
            size[i] = (int)s;
            total = checked(total * size[i]);
            if (total > ctx.Limits.MaxFunctionSamples)
            {
                throw new PdfResourceLimitException(
                    nameof(PdfSecurityLimits.MaxFunctionSamples),
                    $"Type 0 function sample table exceeds {ctx.Limits.MaxFunctionSamples} values.");
            }
        }

        int bps = ctx.Resolver.Resolve(dict["BitsPerSample"]) is { } b && b.TryGetInteger(out long bl) ? (int)Math.Clamp(bl, 0, 64) : 0;
        if (bps is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
            throw ctx.Fail("Type 0 /BitsPerSample must be 1, 2, 4, 8, 12, 16, 24 or 32.");

        double[] encode = ReadNumbers(dict["Encode"], ctx) ?? DefaultEncode(size);
        if (encode.Length != 2 * m)
            throw ctx.Fail("Type 0 /Encode must contain 2 x m numbers.");

        double[] decode = ReadNumbers(dict["Decode"], ctx) ?? range;
        if (decode.Length != 2 * n)
            throw ctx.Fail("Type 0 /Decode must contain 2 x n numbers.");

        byte[] data;
        try
        {
            data = new PdfStreamDecoder(ctx.Limits, ctx.Resolver.Resolve).DecodeStream(stream);
        }
        catch (PdfUnsupportedFeatureException ex)
        {
            throw ctx.Fail($"Type 0 function data cannot be decoded: {ex.Message}");
        }

        // Missing trailing samples (short stream) read as zero rather than failing.
        var samples = new double[total];
        double maxSample = bps == 32 ? uint.MaxValue : (1L << bps) - 1;
        long bitPos = 0;
        for (long i = 0; i < total; i++, bitPos += bps)
        {
            ulong raw = ReadBits(data, bitPos, bps);
            int j = (int)(i % n);
            samples[i] = Interpolate(raw, 0, maxSample, decode[2 * j], decode[2 * j + 1]);
        }

        return new SampledFunction(domain, range, size, encode, samples);
    }

    private static double[] DefaultEncode(int[] size)
    {
        var encode = new double[size.Length * 2];
        for (int i = 0; i < size.Length; i++)
            encode[2 * i + 1] = size[i] - 1;
        return encode;
    }

    private static ulong ReadBits(byte[] data, long bitPos, int bits)
    {
        ulong value = 0;
        for (int i = 0; i < bits; i++)
        {
            long p = bitPos + i;
            long byteIndex = p >> 3;
            int bit = byteIndex < data.Length ? (data[byteIndex] >> (7 - (int)(p & 7))) & 1 : 0;
            value = (value << 1) | (uint)bit;
        }
        return value;
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> x, Span<double> y)
    {
        int m = _size.Length;
        int n = y.Length;
        Span<int> lower = stackalloc int[m];
        Span<double> frac = stackalloc double[m];
        var domain = Domain;

        for (int i = 0; i < m; i++)
        {
            double e = Interpolate(x[i], domain[2 * i], domain[2 * i + 1], _encode[2 * i], _encode[2 * i + 1]);
            e = Math.Clamp(e, 0, _size[i] - 1);
            int lo = (int)Math.Floor(e);
            if (lo >= _size[i] - 1)
            {
                lo = _size[i] - 1;
                frac[i] = 0;
            }
            else
            {
                frac[i] = e - lo;
            }
            lower[i] = lo;
        }

        y.Clear();
        int corners = 1 << m;
        for (int corner = 0; corner < corners; corner++)
        {
            double weight = 1.0;
            int index = 0;
            for (int i = 0; i < m; i++)
            {
                bool upper = (corner & (1 << i)) != 0;
                double f = frac[i];
                if (upper)
                {
                    if (f == 0) { weight = 0; break; }
                    weight *= f;
                    index += (lower[i] + 1) * _strides[i];
                }
                else
                {
                    weight *= 1 - f;
                    index += lower[i] * _strides[i];
                }
            }

            if (weight == 0) continue;
            int baseIndex = index * n;
            for (int j = 0; j < n; j++)
                y[j] += weight * _samples[baseIndex + j];
        }
    }
}
