using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Refits <see cref="SwopApproximation"/>: samples PDFium's DeviceCMYK conversion on a 9-point CMYK grid and
/// fits the model by Levenberg-Marquardt, printing the parameters to paste into the model. Skipped in normal
/// runs (it takes minutes); remove the Skip to run it.
/// </summary>
public class SwopApproximationFit
{
    private readonly ITestOutputHelper _out;
    public SwopApproximationFit(ITestOutputHelper o) => _out = o;

    const int G = 9;

    static double[][] Grid()
    {
        var list = new System.Collections.Generic.List<double[]>();
        for (int c = 0; c < G; c++) for (int m = 0; m < G; m++) for (int y = 0; y < G; y++) for (int k = 0; k < G; k++)
            list.Add([c / (G - 1.0), m / (G - 1.0), y / (G - 1.0), k / (G - 1.0)]);
        return list.ToArray();
    }

    async Task<double[][]> PdfiumTargets(double[][] grid)
    {
        int cols = 81;
        int rows = (grid.Length + cols - 1) / cols;
        var sb = new StringBuilder();
        for (int i = 0; i < grid.Length; i++)
        {
            var p = grid[i];
            int x = i % cols, y = i / cols;
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} k {4} {5} 4 4 re f\n", p[0], p[1], p[2], p[3], x * 4, y * 4));
        }
        var b = new VectorPdfBuilder();
        b.AddPage(sb.ToString(), "<< >>", $"[0 0 {cols * 4} {rows * 4}]");
        byte[] pdf = b.Build();
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var pp = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var result = new double[grid.Length][];
        for (int i = 0; i < grid.Length; i++)
        {
            int x = i % cols, y = i / cols;
            var px = InterpreterCoverageTests.Pixel(pp, x * 4 + 2, pp.HeightPixels - 1 - (y * 4 + 2));
            result[i] = [px.R, px.G, px.B];
        }
        return result;
    }

    static double[] Model(double[] th, double[] cmyk)
    {
        var prim = new double[45];
        for (int i = 0; i < 45; i++) prim[i] = Math.Exp(th[i]);
        var gain = th.Skip(45).Take(8).ToArray();
        var m = new SwopApproximation(prim, gain, [th[53], th[54], th[55]]);
        var rgb = new double[3];
        m.ToSrgb(cmyk, rgb);
        return [rgb[0] * 255, rgb[1] * 255, rgb[2] * 255];
    }

    static double Weight(double[] g)
    {
        int inks = 0; foreach (var v in g) if (v > 0) inks++;
        if (g[0] == 0 && g[1] == 0 && g[2] == 0) return g[3] == 1 ? 12 : 5; // the K ramp: text and gray
        if (inks == 1) return 3;                                            // single-ink tints
        return 1;
    }

    static double Cost(double[] th, double[][] grid, double[][] target)
    {
        double s = 0;
        for (int i = 0; i < grid.Length; i++)
        {
            var o = Model(th, grid[i]);
            double w = Weight(grid[i]);
            for (int c = 0; c < 3; c++) s += w * w * (o[c] - target[i][c]) * (o[c] - target[i][c]);
        }
        return s;
    }

    double[] Fit(double[][] grid, double[][] target)
    {
        // Initialise primaries from the corner targets.
        var th = new double[56];
        for (int corner = 1; corner < 16; corner++)
        {
            double[] cmyk = [corner & 1, corner >> 1 & 1, corner >> 2 & 1, corner >> 3 & 1];
            int idx = Array.FindIndex(grid, g => g.SequenceEqual(cmyk));
            var t = target[idx];
            var lin = t.Select(v => { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }).ToArray();
            // linear sRGB -> XYZ D65 -> rough D50 (ignore adaptation for init)
            double X = 0.4124 * lin[0] + 0.3576 * lin[1] + 0.1805 * lin[2];
            double Y = 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
            double Z = 0.0193 * lin[0] + 0.1192 * lin[1] + 0.9505 * lin[2];
            th[3 * corner - 3] = Math.Log(Math.Max(X * 0.9642 / 0.9505, 0.003));
            th[3 * corner - 2] = Math.Log(Math.Max(Y, 0.003));
            th[3 * corner - 1] = Math.Log(Math.Max(Z * 0.8249 / 1.0888, 0.003));
        }
        for (int i = 0; i < 4; i++) { th[45 + 2 * i] = 0.2; th[46 + 2 * i] = 0; }
        th[53] = th[54] = th[55] = 1.8;

        int P = th.Length, R = grid.Length * 3;
        double lambda = 1e-2;
        double cost = Cost(th, grid, target);
        for (int iter = 0; iter < 60; iter++)
        {
            var r = new double[R];
            var J = new double[R, P];
            for (int i = 0; i < grid.Length; i++)
            {
                var o = Model(th, grid[i]);
                for (int c = 0; c < 3; c++) r[3 * i + c] = Weight(grid[i]) * (o[c] - target[i][c]);
            }
            for (int p = 0; p < P; p++)
            {
                var t2 = (double[])th.Clone();
                double h = 1e-5 * Math.Max(1, Math.Abs(th[p]));
                t2[p] += h;
                for (int i = 0; i < grid.Length; i++)
                {
                    var o = Model(t2, grid[i]);
                    for (int c = 0; c < 3; c++) J[3 * i + c, p] = (Weight(grid[i]) * (o[c] - target[i][c]) - r[3 * i + c]) / h;
                }
            }
            var JtJ = new double[P, P];
            var Jtr = new double[P];
            for (int a = 0; a < P; a++)
            {
                for (int k = 0; k < R; k++) Jtr[a] += J[k, a] * r[k];
                for (int b2 = a; b2 < P; b2++)
                {
                    double s = 0;
                    for (int k = 0; k < R; k++) s += J[k, a] * J[k, b2];
                    JtJ[a, b2] = JtJ[b2, a] = s;
                }
            }
            for (int tries = 0; tries < 10; tries++)
            {
                var A = new double[P, P + 1];
                for (int a = 0; a < P; a++)
                {
                    for (int b2 = 0; b2 < P; b2++) A[a, b2] = JtJ[a, b2];
                    A[a, a] += lambda * (JtJ[a, a] + 1e-6);
                    A[a, P] = -Jtr[a];
                }
                var delta = Solve(A, P);
                var t3 = th.Select((v, i) => v + delta[i]).ToArray();
                for (int q = 53; q < 56; q++) t3[q] = Math.Clamp(t3[q], 0.5, 8);
                for (int q = 0; q < 45; q++) t3[q] = Math.Clamp(t3[q], -9, 0.2);
                double c3 = Cost(t3, grid, target);
                if (c3 < cost) { th = t3; cost = c3; lambda = Math.Max(lambda / 3, 1e-7); break; }
                lambda *= 4;
            }
            if (iter % 10 == 0) _out.WriteLine($"iter {iter} rms {Math.Sqrt(cost / R):F3} lambda {lambda:G3}");
        }
        _out.WriteLine($"final rms {Math.Sqrt(cost / R):F3}");
        return th;
    }

    static double[] Solve(double[,] A, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
            for (int k = 0; k <= n; k++) (A[c, k], A[piv, k]) = (A[piv, k], A[c, k]);
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = A[r, c] / A[c, c];
                for (int k = c; k <= n; k++) A[r, k] -= f * A[c, k];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = A[i, n] / A[i, i];
        return x;
    }

    void Report(string name, double[][] grid, double[][] target, Func<double[], double[]> f)
    {
        double sum = 0, max = 0;
        foreach (var (g, t) in grid.Zip(target))
        {
            var o = f(g);
            for (int c = 0; c < 3; c++) { double d = Math.Abs(o[c] - t[c]); sum += d; max = Math.Max(max, d); }
        }
        _out.WriteLine($"{name}: mean abs {sum / (grid.Length * 3):F2} max {max:F1}");
    }

    [Fact(Skip = "Fitting tool: run by hand to refit the built-in SWOP model.")]
    public async Task FitToPdfium()
    {
        var grid = Grid();
        var pdfium = await PdfiumTargets(grid);
        Report("current model vs pdfium", grid, pdfium, g => { var c = SwopApproximation.Instance.ToColor(g, 1); return [c.R * 255, c.G * 255, c.B * 255]; });
        Report("naive vs pdfium", grid, pdfium, g => [(1 - g[0]) * (1 - g[3]) * 255, (1 - g[1]) * (1 - g[3]) * 255, (1 - g[2]) * (1 - g[3]) * 255]);
        foreach (var (name, target) in new[] { ("pdfium", pdfium) })
        {
            var th = Fit(grid, target);
            Report($"model fit to {name}", grid, pdfium, g => Model(th, g));
            var sb = new StringBuilder();
            for (int i = 0; i < 15; i++)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}, {2:F5},", Math.Exp(th[3 * i]), Math.Exp(th[3 * i + 1]), Math.Exp(th[3 * i + 2])));
            sb.AppendLine(string.Join(", ", th.Skip(45).Take(11).Select(v => v.ToString("F5", CultureInfo.InvariantCulture))));
            var errs = grid.Zip(target).Select(z => (z.First, err: Model(th, z.First).Zip(z.Second).Max(q => Math.Abs(q.First - q.Second)))).OrderByDescending(q => q.err).Take(8);
            foreach (var e in errs) sb.AppendLine($"  worst {string.Join(",", e.First)} err {e.err:F1}");
            _out.WriteLine(sb.ToString());
        }
    }
}
