using System;

namespace PdfEngine.Vector.Color;

/// <summary>
/// A compact model of U.S. Web Coated (SWOP) CMYK as Acrobat shows it, used for DeviceCMYK: a Yule-Nielsen
/// modified Neugebauer model. Each ink's nominal coverage gets a dot-gain curve, the Demichel equations
/// weigh the 16 overprints of paper, C, M, Y and K, and the overprints' D50 XYZ values are mixed in
/// XYZ^(1/n) with one n per XYZ channel. Paper is D50 white, so an empty CMYK value stays white.
/// </summary>
/// <remarks>
/// The 15 ink overprints, the dot-gain coefficients and n were fitted by weighted least squares (the K ramp
/// and single inks weigh more) to PDFium's DeviceCMYK conversion, itself an approximation of Adobe's, on a
/// 9-point CMYK grid: 2.4 levels per channel mean error, 19 at worst (in near-black overprints); K 100 %
/// is 33/30/30 against Acrobat's 35/31/32.
/// </remarks>
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
            0.18663, 0.31017, 0.65383, // C
            0.39017, 0.19320, 0.20310, // M
            0.06229, 0.03890, 0.22268, // CM
            0.76962, 0.83885, 0.09178, // Y
            0.12074, 0.27455, 0.09763, // CY
            0.38025, 0.19931, 0.02222, // MY
            0.02765, 0.02806, 0.02772, // CMY
            0.01355, 0.01339, 0.01079, // K
            0.00012, 0.00091, 0.00983, // CK
            0.00310, 0.00012, 0.00012, // MK
            0.00012, 0.00012, 0.00021, // CMK
            0.00530, 0.00679, 0.00012, // YK
            0.00012, 0.00280, 0.00012, // CYK
            0.00204, 0.00012, 0.00012, // MYK
            0.00012, 0.00012, 0.00012, // CMYK
        ],
        [0.55412, -0.19544, 0.43737, -0.08300, 0.37057, 0.12977, 0.49993, -0.16674],
        [1.35204, 1.35316, 1.32606]);

    public override int Channels => 4;

    public override void ToSrgb(ReadOnlySpan<double> input, Span<double> rgb)
    {
        XyzD50(input, out double x, out double y, out double z);
        PcsMath.XyzToSrgb(x, y, z, rgb);
    }

    public override void ToLinearSrgb(ReadOnlySpan<double> input, Span<double> rgb)
    {
        XyzD50(input, out double x, out double y, out double z);
        PcsMath.XyzToSrgb(x, y, z, rgb, linear: true);
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
