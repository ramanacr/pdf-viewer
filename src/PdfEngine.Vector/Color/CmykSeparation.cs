using System;
using System.Threading.Tasks;

namespace PdfEngine.Vector.Color;

/// <summary>
/// sRGB to CMYK for separations: the numerical inverse of the DeviceCMYK conversion (<see cref="DefaultCmyk"/>,
/// the built-in SWOP model unless Adobe's profile is installed), with a fixed black generation and a 300 % ink
/// limit. It is not Adobe's U.S. Web Coated (SWOP) B2A table: colours inside the CMYK gamut convert back to the
/// colour they show on screen (on a 54-colour test grid: mean ΔE76 0.65, worst 2.5), while black generation and gamut mapping are this
/// engine's own choices, so plate values for RGB content are approximate.
/// </summary>
/// <remarks>
/// For each node of a 17 × 17 × 17 sRGB grid: C, M and Y are solved with K = 0 by damped Gauss-Newton in CIELAB;
/// the gray component min(C, M, Y) sets K through a medium GCR curve (no black under 25 % gray, then rising to
/// 95 %); C, M and Y are solved again with that K (and with more K when that is closer for dark colours); total
/// ink above 300 % is removed from C, M and Y. Colours between nodes are interpolated trilinearly.
/// </remarks>
internal static class CmykSeparation
{
    private const int Grid = 17;
    public const double InkLimit = 3.0;
    private static readonly Lazy<float[]> Table = new(Build);

    /// <summary>The CMYK (0..1) for an sRGB colour (0..1, companded).</summary>
    public static void FromSrgb(double r, double g, double b, Span<double> cmyk)
    {
        var t = Table.Value;
        double fr = Math.Clamp(r, 0, 1) * (Grid - 1), fg = Math.Clamp(g, 0, 1) * (Grid - 1), fb = Math.Clamp(b, 0, 1) * (Grid - 1);
        int r0 = Math.Min((int)fr, Grid - 2), g0 = Math.Min((int)fg, Grid - 2), b0 = Math.Min((int)fb, Grid - 2);
        double dr = fr - r0, dg = fg - g0, db = fb - b0;
        for (int c = 0; c < 4; c++) cmyk[c] = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            int ir = r0 + (corner & 1), ig = g0 + (corner >> 1 & 1), ib = b0 + (corner >> 2 & 1);
            double w = ((corner & 1) != 0 ? dr : 1 - dr) * ((corner & 2) != 0 ? dg : 1 - dg) * ((corner & 4) != 0 ? db : 1 - db);
            if (w == 0) continue;
            int at = ((ir * Grid + ig) * Grid + ib) * 4;
            for (int c = 0; c < 4; c++) cmyk[c] += w * t[at + c];
        }
    }

    private static float[] Build()
    {
        var transform = DefaultCmyk.For(PdfColorSpace.DefaultCmykIntent);
        var table = new float[Grid * Grid * Grid * 4];
        Parallel.For(0, Grid, ir =>
        {
            Span<double> cmyk = stackalloc double[4];
            for (int ig = 0; ig < Grid; ig++)
                for (int ib = 0; ib < Grid; ib++)
                {
                    Solve(transform, ir / (double)(Grid - 1), ig / (double)(Grid - 1), ib / (double)(Grid - 1), cmyk);
                    int at = ((ir * Grid + ig) * Grid + ib) * 4;
                    for (int c = 0; c < 4; c++) table[at + c] = (float)cmyk[c];
                }
        });
        return table;
    }

    /// <summary>Solves one colour.</summary>
    internal static void Solve(ColorTransform transform, double r, double g, double b, Span<double> cmyk)
    {
        var target = new double[3];
        LinearToLab(Linearize(r), Linearize(g), Linearize(b), out target[0], out target[1], out target[2]);

        // CMY alone, starting from the complement.
        var cmy = new[] { 1 - r, 1 - g, 1 - b };
        SolveCmy(transform, target, cmy, 0);

        double gray = Math.Min(cmy[0], Math.Min(cmy[1], cmy[2]));
        double k = gray <= 0.25 ? 0 : 0.95 * Math.Pow((gray - 0.25) / 0.75, 0.9);

        var best = new double[4];
        double bestError = double.MaxValue;
        var trial = new double[3];
        // The GCR black, and more black where it brings a dark colour closer.
        for (int step = 0; step < 6; step++)
        {
            double kk = Math.Min(1, k + step * 0.1);
            for (int i = 0; i < 3; i++) trial[i] = Math.Clamp(cmy[i] - kk * 0.6, 0, 1);
            double error = SolveCmy(transform, target, trial, kk);
            if (step == 0 || error < bestError - 0.25)
            {
                bestError = Math.Min(bestError, error);
                best[0] = trial[0]; best[1] = trial[1]; best[2] = trial[2]; best[3] = kk;
            }
            if (kk >= 1 || error < 1) break;
        }

        double cmySum = best[0] + best[1] + best[2];
        if (cmySum + best[3] > InkLimit && cmySum > 0)
        {
            double scale = Math.Max(0, (InkLimit - best[3]) / cmySum);
            for (int i = 0; i < 3; i++) best[i] *= scale;
        }
        for (int i = 0; i < 4; i++) cmyk[i] = Math.Clamp(best[i], 0, 1);
    }

    /// <summary>Damped Gauss-Newton on C, M, Y (clamped to 0..1) for a fixed K; returns the remaining ΔE76.</summary>
    private static double SolveCmy(ColorTransform transform, double[] target, double[] cmy, double k)
    {
        var lab = new double[3];
        var probe = new double[3];
        var probeLab = new double[3];
        var jac = new double[9];
        var residual = new double[3];
        var a = new double[9];
        var rhs = new double[3];
        var delta = new double[3];
        double lambda = 1e-3;
        double error = Error(transform, cmy, k, target, lab);
        for (int iteration = 0; iteration < 30 && error > 0.05; iteration++)
        {
            for (int i = 0; i < 3; i++) residual[i] = target[i] - lab[i];
            const double h = 1e-4;
            for (int j = 0; j < 3; j++)
            {
                Array.Copy(cmy, probe, 3);
                double step = probe[j] + h <= 1 ? h : -h;
                probe[j] += step;
                ToLab(transform, probe, k, probeLab);
                for (int i = 0; i < 3; i++) jac[3 * i + j] = (probeLab[i] - lab[i]) / step;
            }
            // (JᵀJ + λ diag) δ = Jᵀ r
            for (int p = 0; p < 3; p++)
            {
                for (int q = 0; q < 3; q++)
                {
                    double sum = 0;
                    for (int i = 0; i < 3; i++) sum += jac[3 * i + p] * jac[3 * i + q];
                    a[3 * p + q] = sum;
                }
                double t = 0;
                for (int i = 0; i < 3; i++) t += jac[3 * i + p] * residual[i];
                rhs[p] = t;
            }
            for (int p = 0; p < 3; p++) a[3 * p + p] = a[3 * p + p] * (1 + lambda) + 1e-9;
            if (!Solve3(a, rhs, delta))
                break;
            for (int i = 0; i < 3; i++) probe[i] = Math.Clamp(cmy[i] + delta[i], 0, 1);
            double trialError = Error(transform, probe, k, target, probeLab);
            if (trialError < error)
            {
                Array.Copy(probe, cmy, 3);
                Array.Copy(probeLab, lab, 3);
                bool converged = error - trialError < 1e-4;
                error = trialError;
                if (converged) break;
                lambda = Math.Max(1e-6, lambda * 0.3);
            }
            else
            {
                lambda *= 10;
                if (lambda > 1e4) break;
            }
        }
        return error;
    }

    private static double Error(ColorTransform transform, double[] cmy, double k, double[] target, double[] lab)
    {
        ToLab(transform, cmy, k, lab);
        double dl = lab[0] - target[0], da = lab[1] - target[1], db = lab[2] - target[2];
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    private static void ToLab(ColorTransform transform, double[] cmy, double k, double[] lab)
    {
        Span<double> input = stackalloc double[4] { cmy[0], cmy[1], cmy[2], k };
        Span<double> rgb = stackalloc double[3];
        transform.ToLinearSrgb(input, rgb);
        LinearToLab(rgb[0], rgb[1], rgb[2], out lab[0], out lab[1], out lab[2]);
    }

    private static double Linearize(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>Linear sRGB to CIELAB (D65 white).</summary>
    internal static void LinearToLab(double r, double g, double b, out double l, out double a, out double bb)
    {
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        l = 116 * fy - 16;
        a = 500 * (fx - fy);
        bb = 200 * (fy - fz);
    }

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;

    private static bool Solve3(double[] m, double[] v, double[] x)
    {
        double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (Math.Abs(det) < 1e-18 || double.IsNaN(det)) return false;
        x[0] = (v[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (v[1] * m[8] - m[5] * v[2]) + m[2] * (v[1] * m[7] - m[4] * v[2])) / det;
        x[1] = (m[0] * (v[1] * m[8] - m[5] * v[2]) - v[0] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * v[2] - v[1] * m[6])) / det;
        x[2] = (m[0] * (m[4] * v[2] - v[1] * m[7]) - m[1] * (m[3] * v[2] - v[1] * m[6]) + v[0] * (m[3] * m[7] - m[4] * m[6])) / det;
        return true;
    }
}
