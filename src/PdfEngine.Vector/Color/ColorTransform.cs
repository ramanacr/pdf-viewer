using System;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEngine.Vector.Color;

/// <summary>
/// A device-to-sRGB colour conversion: an ICC profile transform or the built-in CMYK approximation.
/// Instances are immutable and shared between threads; the image lookup table is built on first use.
/// </summary>
internal abstract class ColorTransform
{
    private ColorLut8? _imageLut;
    private int _imageLutBuilt;

    /// <summary>Number of device components consumed.</summary>
    public abstract int Channels { get; }

    /// <summary>Converts device values (0..1) to companded sRGB (0..1).</summary>
    public abstract void ToSrgb(ReadOnlySpan<double> input, Span<double> rgb);

    /// <summary>
    /// A table that converts 8-bit samples to BGRA quickly (for images), or null when the channel count has
    /// no fast path.
    /// </summary>
    public ColorLut8? ImageLut
    {
        get
        {
            if (Volatile.Read(ref _imageLutBuilt) == 0)
            {
                var lut = ColorLut8.Build(this);
                Interlocked.CompareExchange(ref _imageLut, lut, null);
                Volatile.Write(ref _imageLutBuilt, 1);
            }
            return _imageLut;
        }
    }

    public PdfColor ToColor(ReadOnlySpan<double> input, float alpha)
    {
        Span<double> rgb = stackalloc double[3];
        ToSrgb(input, rgb);
        return PdfColor.FromRgb((float)rgb[0], (float)rgb[1], (float)rgb[2], alpha);
    }
}

/// <summary>Profile connection space to sRGB: Lab to XYZ (D50), Bradford adaptation to D65, sRGB encoding.</summary>
internal static class PcsMath
{
    public static readonly double[] D50 = [0.9642, 1.0, 0.8249];

    // XYZ(D65) to linear sRGB, multiplied by the Bradford D50-to-D65 adaptation.
    private static readonly double[] XyzD50ToLinearSrgb = Build();

    private static double[] Build()
    {
        double[] toSrgb =
        [
            3.2404542, -1.5371385, -0.4985314,
            -0.9692660, 1.8760108, 0.0415560,
            0.0556434, -0.2040259, 1.0572252,
        ];
        var adapt = PdfColorSpace.CieMath.AdaptationToD65(D50);
        var m = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[3 * r + c] = toSrgb[3 * r] * adapt[c] + toSrgb[3 * r + 1] * adapt[3 + c] + toSrgb[3 * r + 2] * adapt[6 + c];
        return m;
    }

    public static void XyzToSrgb(double x, double y, double z, Span<double> rgb)
    {
        var m = XyzD50ToLinearSrgb;
        rgb[0] = PdfColorSpace.CieMath.Compand(m[0] * x + m[1] * y + m[2] * z);
        rgb[1] = PdfColorSpace.CieMath.Compand(m[3] * x + m[4] * y + m[5] * z);
        rgb[2] = PdfColorSpace.CieMath.Compand(m[6] * x + m[7] * y + m[8] * z);
    }

    public static void LabToXyz(double l, double a, double b, out double x, out double y, out double z)
    {
        double fy = (l + 16) / 116;
        double fx = fy + a / 500;
        double fz = fy - b / 200;
        x = D50[0] * InverseF(fx);
        y = D50[1] * InverseF(fy);
        z = D50[2] * InverseF(fz);
    }

    public static void LabToSrgb(double l, double a, double b, Span<double> rgb)
    {
        LabToXyz(l, a, b, out double x, out double y, out double z);
        XyzToSrgb(x, y, z, rgb);
    }

    private static double InverseF(double t) => t > 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);
}

/// <summary>An ICC profile's device-to-PCS model for one rendering intent, followed by PCS to sRGB.</summary>
internal sealed class IccTransform : ColorTransform
{
    private readonly IccProfile _profile;
    private readonly IccPipeline? _pipeline;

    private IccTransform(IccProfile profile, IccPipeline? pipeline)
    {
        _profile = profile;
        _pipeline = pipeline;
        IsSrgb = profile.Channels == 3 && LooksLikeSrgb();
    }

    public override int Channels => _profile.Channels;

    /// <summary>The transform reproduces sRGB within one 8-bit level, so device RGB can pass through unchanged.</summary>
    public bool IsSrgb { get; }

    /// <summary>
    /// The transform for <paramref name="intent"/>: its A2B tag when present (absolute colorimetric uses the
    /// relative one), else another A2B tag, else the matrix/TRC or gray TRC model.
    /// </summary>
    public static IccTransform Create(IccProfile profile, PdfRenderingIntent intent)
    {
        int wanted = intent == PdfRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
        var pipeline = profile.AToB[wanted] ?? profile.AToB[0] ?? profile.AToB[1] ?? profile.AToB[2];
        return new IccTransform(profile, pipeline);
    }

    public override void ToSrgb(ReadOnlySpan<double> input, Span<double> rgb)
    {
        if (_pipeline != null)
        {
            Span<double> pcs = stackalloc double[3];
            _pipeline.Evaluate(input, pcs);
            if (_profile.IsPcsLab) PcsMath.LabToSrgb(pcs[0], pcs[1], pcs[2], rgb);
            else PcsMath.XyzToSrgb(pcs[0], pcs[1], pcs[2], rgb);
            return;
        }
        if (_profile.RgbMatrix is { } m && _profile.RgbTrc is { } trc)
        {
            double r = trc[0].Evaluate(At(input, 0)), g = trc[1].Evaluate(At(input, 1)), b = trc[2].Evaluate(At(input, 2));
            PcsMath.XyzToSrgb(m[0] * r + m[1] * g + m[2] * b, m[3] * r + m[4] * g + m[5] * b, m[6] * r + m[7] * g + m[8] * b, rgb);
            return;
        }
        // Gray TRC: the luminance of a D50 neutral, which maps onto the sRGB neutral axis.
        double y = _profile.GrayTrc?.Evaluate(At(input, 0)) ?? At(input, 0);
        rgb[0] = rgb[1] = rgb[2] = PdfColorSpace.CieMath.Compand(y);
    }

    private static double At(ReadOnlySpan<double> input, int i) => i < input.Length && !double.IsNaN(input[i]) ? input[i] : 0;

    private bool LooksLikeSrgb()
    {
        Span<double> input = stackalloc double[3];
        Span<double> rgb = stackalloc double[3];
        for (int r = 0; r <= 4; r++)
            for (int g = 0; g <= 4; g++)
                for (int b = 0; b <= 4; b++)
                {
                    input[0] = r / 4.0; input[1] = g / 4.0; input[2] = b / 4.0;
                    ToSrgb(input, rgb);
                    for (int c = 0; c < 3; c++)
                        if (Math.Abs(rgb[c] - input[c]) > 1.0 / 255) return false;
                }
        return true;
    }
}

/// <summary>
/// Converts packed 8-bit device samples to BGRA through a sampled grid of a <see cref="ColorTransform"/>:
/// 256 entries for one channel, a 33-point cube with tetrahedral interpolation for three, and 17-point
/// CMY cubes interpolated tetrahedrally and blended linearly along K for four (the layout lcms uses).
/// Grid values are 8.8 fixed point so interpolation stays in integers.
/// </summary>
internal sealed class ColorLut8
{
    private readonly int _channels;
    private readonly int[] _grid;      // 3 values per node, 0..65280 (8.8 fixed point)
    private readonly int[] _index;     // per sample value: grid cell, pre-multiplied by the axis stride is done per axis
    private readonly int[] _fraction;  // per sample value: position inside the cell, 0..256
    private readonly int _points;
    private readonly byte[]? _gray;    // 1 channel: BGR per sample value

    private ColorLut8(int channels, int points, int[] grid, byte[]? gray)
    {
        _channels = channels;
        _points = points;
        _grid = grid;
        _gray = gray;
        _index = new int[256];
        _fraction = new int[256];
        for (int s = 0; s < 256; s++)
        {
            int pos = s * (points - 1);
            int cell = pos / 255;
            if (cell >= points - 1) cell = points - 2;
            _index[s] = cell;
            _fraction[s] = (int)Math.Round((pos - cell * 255) * 256.0 / 255);
        }
    }

    public int Channels => _channels;

    public static ColorLut8? Build(ColorTransform transform)
    {
        int n = transform.Channels;
        Span<double> input = stackalloc double[Math.Max(1, n)];
        Span<double> rgb = stackalloc double[3];
        if (n == 1)
        {
            var gray = new byte[256 * 3];
            for (int s = 0; s < 256; s++)
            {
                input[0] = s / 255.0;
                transform.ToSrgb(input, rgb);
                gray[3 * s] = ToByte(rgb[2]);
                gray[3 * s + 1] = ToByte(rgb[1]);
                gray[3 * s + 2] = ToByte(rgb[0]);
            }
            return new ColorLut8(1, 2, [], gray);
        }
        if (n is not (3 or 4))
            return null;

        int points = n == 3 ? 33 : 17;
        int nodes = 1;
        for (int i = 0; i < n; i++) nodes *= points;
        var grid = new int[nodes * 3];
        Span<int> at = stackalloc int[n];
        for (int node = 0; node < nodes; node++)
        {
            // The first channel varies slowest, as in an ICC CLUT.
            int rest = node;
            for (int i = n - 1; i >= 0; i--)
            {
                at[i] = rest % points;
                rest /= points;
            }
            for (int i = 0; i < n; i++) input[i] = at[i] / (double)(points - 1);
            transform.ToSrgb(input, rgb);
            grid[3 * node] = (int)Math.Round(Math.Clamp(rgb[0], 0, 1) * 65280);
            grid[3 * node + 1] = (int)Math.Round(Math.Clamp(rgb[1], 0, 1) * 65280);
            grid[3 * node + 2] = (int)Math.Round(Math.Clamp(rgb[2], 0, 1) * 65280);
        }
        return new ColorLut8(n, points, grid, null);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)(v * 255 + 0.5), 0, 255);

    /// <summary>
    /// Converts <paramref name="pixels"/> pixels of <see cref="Channels"/> samples each to opaque BGRA.
    /// Large images are split across threads.
    /// </summary>
    public void ToBgra(byte[] samples, int sampleOffset, byte[] bgra, int bgraOffset, int pixels)
    {
        const int chunk = 1 << 16;
        if (pixels < 4 * chunk)
        {
            ToBgra(samples.AsSpan(sampleOffset, pixels * _channels), bgra.AsSpan(bgraOffset, pixels * 4));
            return;
        }
        int chunks = (pixels + chunk - 1) / chunk;
        Parallel.For(0, chunks, i =>
        {
            int first = i * chunk, count = Math.Min(chunk, pixels - first);
            ToBgra(samples.AsSpan(sampleOffset + first * _channels, count * _channels), bgra.AsSpan(bgraOffset + first * 4, count * 4));
        });
    }

    public void ToBgra(ReadOnlySpan<byte> samples, Span<byte> bgra)
    {
        int pixels = Math.Min(samples.Length / _channels, bgra.Length / 4);
        if (_gray != null)
        {
            for (int p = 0; p < pixels; p++)
            {
                int s = samples[p] * 3, o = p * 4;
                bgra[o] = _gray[s];
                bgra[o + 1] = _gray[s + 1];
                bgra[o + 2] = _gray[s + 2];
                bgra[o + 3] = 255;
            }
            return;
        }

        int pts = _points;
        int sz = 3, sy = 3 * pts, sx = 3 * pts * pts, sk = 3 * pts * pts * pts;
        Span<int> a = stackalloc int[3];
        Span<int> b = stackalloc int[3];
        if (_channels == 3)
        {
            for (int p = 0; p < pixels; p++)
            {
                int s = p * 3;
                int x = samples[s], y = samples[s + 1], z = samples[s + 2];
                int offset = _index[x] * sx + _index[y] * sy + _index[z] * sz;
                Tetrahedral(offset, _fraction[x], _fraction[y], _fraction[z], sx, sy, sz, a);
                Store(bgra, p * 4, a);
            }
            return;
        }

        for (int p = 0; p < pixels; p++)
        {
            int s = p * 4;
            int c = samples[s], m = samples[s + 1], y = samples[s + 2], k = samples[s + 3];
            int offset = _index[c] * sk + _index[m] * sx + _index[y] * sy + _index[k] * sz;
            // The K axis is the fastest-varying one, so neighbouring K planes are sz apart; the CMY cube of
            // one K plane uses strides sk, sx and sy.
            int rc = _fraction[c], rm = _fraction[m], ry = _fraction[y], rk = _fraction[k];
            Tetrahedral(offset, rc, rm, ry, sk, sx, sy, a);
            if (rk != 0)
            {
                Tetrahedral(offset + sz, rc, rm, ry, sk, sx, sy, b);
                for (int i = 0; i < 3; i++) a[i] += ((b[i] - a[i]) * rk) >> 8;
            }
            Store(bgra, p * 4, a);
        }
    }

    private static void Store(Span<byte> bgra, int o, ReadOnlySpan<int> rgb)
    {
        bgra[o] = Clamp8(rgb[2]);
        bgra[o + 1] = Clamp8(rgb[1]);
        bgra[o + 2] = Clamp8(rgb[0]);
        bgra[o + 3] = 255;
    }

    private static byte Clamp8(int v)
    {
        v = (v + 128) >> 8;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    private void Tetrahedral(int p, int rx, int ry, int rz, int x1, int y1, int z1, Span<int> result)
    {
        var v = _grid;
        int o1, o2, w1, w2, w3;
        // Pick the tetrahedron containing the point; o1 and o2 are its middle vertices, w the weights of the
        // edges walked from the origin vertex to the opposite one.
        if (rx >= ry)
        {
            if (ry >= rz) { o1 = x1; o2 = x1 + y1; w1 = rx; w2 = ry; w3 = rz; }
            else if (rx >= rz) { o1 = x1; o2 = x1 + z1; w1 = rx; w2 = rz; w3 = ry; }
            else { o1 = z1; o2 = x1 + z1; w1 = rz; w2 = rx; w3 = ry; }
        }
        else
        {
            if (rx >= rz) { o1 = y1; o2 = x1 + y1; w1 = ry; w2 = rx; w3 = rz; }
            else if (ry >= rz) { o1 = y1; o2 = y1 + z1; w1 = ry; w2 = rz; w3 = rx; }
            else { o1 = z1; o2 = y1 + z1; w1 = rz; w2 = ry; w3 = rx; }
        }
        int o3 = x1 + y1 + z1;
        for (int i = 0; i < 3; i++)
        {
            int c0 = v[p + i], c1 = v[p + o1 + i], c2 = v[p + o2 + i], c3 = v[p + o3 + i];
            result[i] = c0 + (((c1 - c0) * w1 + (c2 - c1) * w2 + (c3 - c2) * w3 + 128) >> 8);
        }
    }
}
