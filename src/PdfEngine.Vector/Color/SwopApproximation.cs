using System;

namespace PdfEngine.Vector.Color;

/// <summary>
/// A compact model of U.S. Web Coated (SWOP) CMYK as Acrobat shows it, used for DeviceCMYK when no CMYK
/// profile is installed: a Yule-Nielsen modified Neugebauer model. Each ink's nominal coverage gets a
/// dot-gain curve, the Demichel equations weigh the 16 overprints of paper, C, M, Y and K, and the
/// overprints' D50 XYZ values are mixed in XYZ^(1/n) with one n per XYZ channel. Paper is D50 white so
/// an empty CMYK value stays white. The 15 ink overprints, the dot-gain coefficients and n were fitted by
/// least squares to SWOP conversions sampled on a 9-point CMYK grid.
/// </summary>
internal sealed class SwopApproximation : ColorTransform
{
    private readonly double[] _gain;        // per ink: coverage = t + t (1 - t) (p + q t)
    private readonly double[] _n;           // Yule-Nielsen n for X, Y and Z
    private readonly double[] _rootPrimaries; // 16 overprints (bit 0 = C, 1 = M, 2 = Y, 3 = K), XYZ^(1/n)

    /// <param name="overprints">D50 XYZ of the 15 overprints after paper, in Demichel order (C, M, CM, Y, ...).</param>
    internal SwopApproximation(double[] overprints, double[] gain, double[] n)
    {
        if (overprints.Length != 45 || gain.Length != 8 || n.Length != 3 || !Array.TrueForAll(n, v => v > 0))
            throw new ArgumentException("Invalid model parameters.");
        _gain = gain;
        _n = n;
        _rootPrimaries = new double[48];
        for (int i = 0; i < 48; i++)
        {
            double v = i < 3 ? PcsMath.D50[i] : overprints[i - 3];
            _rootPrimaries[i] = Math.Pow(Math.Max(v, 1e-6), 1 / n[i % 3]);
        }
    }

    public static readonly SwopApproximation Instance = new(
        [
            0.18800, 0.31175, 0.64994, // C
            0.38637, 0.19157, 0.20186, // M
            0.06192, 0.03829, 0.22142, // CM
            0.75732, 0.82417, 0.09120, // Y
            0.12069, 0.27405, 0.09597, // CY
            0.37847, 0.19833, 0.02172, // MY
            0.02757, 0.02794, 0.02745, // CMY
            0.01033, 0.01005, 0.00676, // K
            0.00012, 0.00097, 0.01083, // CK
            0.00325, 0.00012, 0.00012, // MK
            0.00012, 0.00012, 0.00012, // CMK
            0.00559, 0.00718, 0.00012, // YK
            0.00012, 0.00280, 0.00012, // CYK
            0.00198, 0.00012, 0.00012, // MYK
            0.00012, 0.00012, 0.00012, // CMYK
        ],
        [0.57618, -0.18036, 0.45224, -0.06675, 0.39196, 0.12481, 0.51725, -0.12285],
        [1.29341, 1.29491, 1.27347]);

    public override int Channels => 4;

    public override void ToSrgb(ReadOnlySpan<double> input, Span<double> rgb)
    {
        XyzD50(input, out double x, out double y, out double z);
        PcsMath.XyzToSrgb(x, y, z, rgb);
    }

    /// <summary>The modelled D50 XYZ of a CMYK colour.</summary>
    internal void XyzD50(ReadOnlySpan<double> input, out double x, out double y, out double z)
    {
        Span<double> a = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            double t = i < input.Length && input[i] > 0 ? Math.Min(input[i], 1) : 0;
            a[i] = Math.Clamp(t + t * (1 - t) * (_gain[2 * i] + _gain[2 * i + 1] * t), 0, 1);
        }

        double sx = 0, sy = 0, sz = 0;
        for (int corner = 0; corner < 16; corner++)
        {
            double w = 1;
            for (int i = 0; i < 4; i++)
                w *= (corner >> i & 1) != 0 ? a[i] : 1 - a[i];
            if (w == 0) continue;
            sx += w * _rootPrimaries[3 * corner];
            sy += w * _rootPrimaries[3 * corner + 1];
            sz += w * _rootPrimaries[3 * corner + 2];
        }
        x = Math.Pow(sx, _n[0]);
        y = Math.Pow(sy, _n[1]);
        z = Math.Pow(sz, _n[2]);
    }
}
