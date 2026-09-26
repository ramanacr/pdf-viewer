using System;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Color;

public abstract partial class PdfColorSpace
{
    /// <summary>
    /// CIE XYZ to sRGB conversion helpers (IEC 61966-2-1) with Bradford chromatic adaptation from the
    /// color space /WhitePoint to D65.
    /// </summary>
    internal static class CieMath
    {
        public static readonly double[] D65 = [0.95047, 1.0, 1.08883];

        private static readonly double[] Bradford =
        [
            0.8951, 0.2664, -0.1614,
            -0.7502, 1.7135, 0.0367,
            0.0389, -0.0685, 1.0296,
        ];

        private static readonly double[] BradfordInverse =
        [
            0.9869929, -0.1470543, 0.1599627,
            0.4323053, 0.5183603, 0.0492912,
            -0.0085287, 0.0400428, 0.9684867,
        ];

        /// <summary>Reads /WhitePoint (required, Y must be 1.0); falls back to D65 when missing or invalid.</summary>
        public static double[] ReadWhitePoint(PdfDictionary? dict, PdfObjectResolver resolver)
        {
            var wp = ReadNumbers(dict, "WhitePoint", resolver, 3);
            return wp != null && wp[0] > 0 && wp[1] > 0 && wp[2] > 0 ? wp : D65;
        }

        public static double[]? ReadNumbers(PdfDictionary? dict, string key, PdfObjectResolver resolver, int count)
        {
            if (dict == null || resolver.Resolve(dict[key]) is not PdfArray arr || arr.Count < count)
                return null;
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (resolver.Resolve(arr[i]) is not { } item || !item.TryGetNumber(out values[i]) || !double.IsFinite(values[i]))
                    return null;
            }
            return values;
        }

        /// <summary>Computes the 3x3 Bradford adaptation matrix from <paramref name="whitePoint"/> to D65.</summary>
        public static double[] AdaptationToD65(double[] whitePoint)
        {
            Span<double> src = stackalloc double[3];
            Span<double> dst = stackalloc double[3];
            Multiply(Bradford, whitePoint, src);
            Multiply(Bradford, D65, dst);

            // M^-1 * diag(dst/src) * M
            var scaled = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    scaled[3 * r + c] = Bradford[3 * r + c] * (dst[r] / src[r]);

            var result = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++) sum += BradfordInverse[3 * r + k] * scaled[3 * k + c];
                    result[3 * r + c] = sum;
                }
            return result;
        }

        public static void Multiply(ReadOnlySpan<double> m, ReadOnlySpan<double> v, Span<double> result)
        {
            for (int r = 0; r < 3; r++)
                result[r] = m[3 * r] * v[0] + m[3 * r + 1] * v[1] + m[3 * r + 2] * v[2];
        }

        /// <summary>Converts adapted (D65) XYZ to companded sRGB in 0..1.</summary>
        public static PdfColor XyzD65ToSrgb(double x, double y, double z, float alpha)
        {
            double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
            double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
            double b = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
            return PdfColor.FromRgb((float)Compand(r), (float)Compand(g), (float)Compand(b), alpha);
        }

        /// <summary>sRGB transfer function applied to a linear value.</summary>
        public static double Compand(double linear)
        {
            if (double.IsNaN(linear) || linear <= 0) return 0;
            if (linear >= 1) return 1;
            return linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        }

        public static PdfColor AdaptedXyzToSrgb(double[] adaptation, double x, double y, double z, float alpha)
        {
            Span<double> xyz = [x, y, z];
            Span<double> d65 = stackalloc double[3];
            Multiply(adaptation, xyz, d65);
            return XyzD65ToSrgb(d65[0], d65[1], d65[2], alpha);
        }
    }

    /// <summary>
    /// CalGray (ISO 32000-2 8.6.5.2): A is raised to /Gamma to give luminance, then displayed through the sRGB
    /// transfer curve. The white point only scales a neutral, so the result is a neutral gray.
    /// </summary>
    public sealed class CalGrayColorSpace : PdfColorSpace
    {
        private readonly double _gamma;

        private CalGrayColorSpace(double gamma) => _gamma = gamma;

        public override string Name => "CalGray";
        public override int NumberOfComponents => 1;

        /// <summary>The /Gamma exponent (default 1).</summary>
        public double Gamma => _gamma;

        internal static CalGrayColorSpace Create(PdfDictionary? dict, PdfObjectResolver resolver)
        {
            double gamma = dict != null && resolver.Resolve(dict["Gamma"]) is { } g && g.TryGetNumber(out double gv) && gv > 0 && double.IsFinite(gv) ? gv : 1.0;
            return new CalGrayColorSpace(gamma);
        }

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            double a = Math.Clamp(Component(components, 0), 0, 1);
            return PdfColor.FromGray((float)CieMath.Compand(Math.Pow(a, _gamma)), alpha);
        }
    }

    /// <summary>
    /// CalRGB (ISO 32000-2 8.6.5.3): components are raised to /Gamma, mapped to XYZ through /Matrix,
    /// adapted from /WhitePoint to D65 (Bradford) and converted to sRGB.
    /// </summary>
    public sealed class CalRgbColorSpace : PdfColorSpace
    {
        private readonly double[] _gamma;
        private readonly double[] _matrix; // Row-major XYZ = M * [A^GR B^GG C^GB]
        private readonly double[] _adaptation;

        private CalRgbColorSpace(double[] gamma, double[] matrix, double[] adaptation)
        {
            _gamma = gamma;
            _matrix = matrix;
            _adaptation = adaptation;
        }

        public override string Name => "CalRGB";
        public override int NumberOfComponents => 3;

        internal static CalRgbColorSpace Create(PdfDictionary? dict, PdfObjectResolver resolver)
        {
            var whitePoint = CieMath.ReadWhitePoint(dict, resolver);
            var gamma = CieMath.ReadNumbers(dict, "Gamma", resolver, 3) ?? [1.0, 1.0, 1.0];
            for (int i = 0; i < 3; i++)
                if (!(gamma[i] > 0)) gamma[i] = 1.0;

            // /Matrix is [XA YA ZA XB YB ZB XC YC ZC]: column j holds the XYZ of component j.
            var m = CieMath.ReadNumbers(dict, "Matrix", resolver, 9) ?? [1, 0, 0, 0, 1, 0, 0, 0, 1];
            double[] matrix =
            [
                m[0], m[3], m[6],
                m[1], m[4], m[7],
                m[2], m[5], m[8],
            ];
            return new CalRgbColorSpace(gamma, matrix, CieMath.AdaptationToD65(whitePoint));
        }

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            Span<double> linear = stackalloc double[3];
            for (int i = 0; i < 3; i++)
                linear[i] = Math.Pow(Math.Clamp(Component(components, i), 0, 1), _gamma[i]);

            Span<double> xyz = stackalloc double[3];
            CieMath.Multiply(_matrix, linear, xyz);
            return CieMath.AdaptedXyzToSrgb(_adaptation, xyz[0], xyz[1], xyz[2], alpha);
        }
    }

    /// <summary>
    /// CIE L*a*b* (ISO 32000-2 8.6.5.4): L in 0..100, a/b limited by /Range (default -100..100),
    /// converted to XYZ relative to /WhitePoint, adapted to D65 and converted to sRGB.
    /// </summary>
    public sealed class LabColorSpace : PdfColorSpace
    {
        private readonly double[] _whitePoint;
        private readonly double[] _range; // amin amax bmin bmax
        private readonly double[] _adaptation;

        private LabColorSpace(double[] whitePoint, double[] range)
        {
            _whitePoint = whitePoint;
            _range = range;
            _adaptation = CieMath.AdaptationToD65(whitePoint);
        }

        public override string Name => "Lab";
        public override int NumberOfComponents => 3;

        internal static LabColorSpace Create(PdfDictionary? dict, PdfObjectResolver resolver)
        {
            var range = CieMath.ReadNumbers(dict, "Range", resolver, 4) ?? [-100, 100, -100, 100];
            if (!(range[0] <= range[1])) (range[0], range[1]) = (-100, 100);
            if (!(range[2] <= range[3])) (range[2], range[3]) = (-100, 100);
            return new LabColorSpace(CieMath.ReadWhitePoint(dict, resolver), range);
        }

        public override (double Min, double Max) GetComponentRange(int i) => i switch
        {
            0 => (0.0, 100.0),
            1 => (_range[0], _range[1]),
            2 => (_range[2], _range[3]),
            _ => (0.0, 1.0),
        };

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            double l = Math.Clamp(Component(components, 0), 0, 100);
            double a = Math.Clamp(Component(components, 1), _range[0], _range[1]);
            double b = Math.Clamp(Component(components, 2), _range[2], _range[3]);

            double fy = (l + 16) / 116;
            double fx = fy + a / 500;
            double fz = fy - b / 200;

            double x = _whitePoint[0] * InverseF(fx);
            double y = _whitePoint[1] * InverseF(fy);
            double z = _whitePoint[2] * InverseF(fz);
            return CieMath.AdaptedXyzToSrgb(_adaptation, x, y, z, alpha);
        }

        /// <summary>The g(x) function of ISO 32000-2 8.6.5.4.</summary>
        private static double InverseF(double t) =>
            t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);
    }
}
