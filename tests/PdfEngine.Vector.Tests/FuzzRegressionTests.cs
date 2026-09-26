using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Deterministic mutation fuzzing of the parser and content interpreter (09_SECURITY "Fuzzing",
/// backlog K4). Every input must either open and interpret, or fail with a TYPED engine error —
/// never an unclassified crash, hang, or unbounded allocation.
/// </summary>
/// <remarks>
/// Set VECTOR_FUZZ_ITERATIONS to run longer locally or in scheduled CI. A failing seed printed in
/// the assertion reproduces exactly; add it to <see cref="RegressionSeeds"/> once fixed.
/// </remarks>
public class FuzzRegressionTests
{
    // 769: corrupt deflate raised ZLibException (IOException) instead of a typed error.
    private static readonly int[] RegressionSeeds = { 769 };

    private static byte[] Seed()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "0 0 1 rg 0 0 10 10 re f");
        int img = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
            new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 }, flate: true);
        int gs = b.Add("<< /Type /ExtGState /ca 0.5 /BM /Normal >>");
        b.AddPage("q 1 0 0 1 10 10 cm /G1 gs 0 0 0 RG 2 w 0 0 m 50 50 l S BT /F1 12 Tf 10 100 Td [(Fuzz) 50 (ed)] TJ ET " +
                  "/Fm Do q 20 0 0 20 100 100 cm /Im Do Q BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI Q",
            $"<< /Font << /F1 {font} 0 R >> /XObject << /Fm {form} 0 R /Im {img} 0 R >> /ExtGState << /G1 {gs} 0 R >> >>",
            flate: false);
        b.AddPage("0.5 g 10 10 100 100 re f", flate: true);
        return b.Build();
    }

    private static readonly PdfSecurityLimits Limits = new()
    {
        MaxPageBuildTime = TimeSpan.FromSeconds(2),
        MaxDecodedStreamBytes = 16 * 1024 * 1024,
        MaxImagePixels = 4_000_000,
    };

    [Fact]
    public async Task MutatedInputs_OnlyFailWithTypedErrors()
    {
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("VECTOR_FUZZ_ITERATIONS"), out int n) ? n : 400;
        byte[] seed = Seed();
        var seeds = new List<int>(RegressionSeeds);
        for (int i = 0; i < iterations; i++) seeds.Add(i);

        foreach (int s in seeds)
        {
            byte[] input = Mutate(seed, s);
            var clock = Stopwatch.StartNew();
            try
            {
                using var doc = await PdfVectorDocument.OpenAsync(input, limits: Limits);
                for (int page = 1; page <= Math.Min(doc.PageCount, 4); page++)
                {
                    var list = await doc.GetPageDisplayListAsync(page);
                    // The backend must also survive what the interpreter let through (images, glyphs).
                    if (s % 25 == 0)
                    {
                        using var rendered = await InterpreterCoverageTests.RenderAsync(list, 36);
                    }
                }
            }
            catch (PdfVectorException)
            {
                // typed and classified: acceptable
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {s}: untyped {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"seed {s}: took {clock.Elapsed}");
        }
    }

    /// <summary>Byte flips, token-ish splices, truncation and numeric blow-ups.</summary>
    private static byte[] Mutate(byte[] seed, int s)
    {
        var rng = new Random(s);
        var data = new List<byte>(seed);
        int ops = 1 + rng.Next(6);
        for (int k = 0; k < ops; k++)
        {
            switch (rng.Next(6))
            {
                case 0: // flip a byte
                    data[rng.Next(data.Count)] = (byte)rng.Next(256);
                    break;
                case 1: // delete a run
                {
                    int at = rng.Next(data.Count);
                    data.RemoveRange(at, Math.Min(rng.Next(1, 32), data.Count - at));
                    break;
                }
                case 2: // duplicate a run
                {
                    int at = rng.Next(data.Count);
                    int len = Math.Min(rng.Next(1, 64), data.Count - at);
                    data.InsertRange(rng.Next(data.Count), data.GetRange(at, len));
                    break;
                }
                case 3: // insert hostile tokens
                {
                    string[] tokens = { " 99999999999 ", " -1 ", " 1e308 ", " [[[[[[[[ ", " << << << ", " q q q q ", " 0 0 R ", " /Length 9999999 ", " endstream ", " BI ID ", " Do " };
                    var bytes = System.Text.Encoding.ASCII.GetBytes(tokens[rng.Next(tokens.Length)]);
                    data.InsertRange(rng.Next(data.Count), bytes);
                    break;
                }
                case 4: // truncate somewhere in the second half
                {
                    int at = rng.Next(data.Count / 2, data.Count);
                    data.RemoveRange(at, data.Count - at);
                    break;
                }
                default: // change a digit
                {
                    int at = rng.Next(data.Count);
                    for (int p = at; p < data.Count; p++)
                    {
                        if (data[p] >= '0' && data[p] <= '9') { data[p] = (byte)('0' + rng.Next(10)); break; }
                    }
                    break;
                }
            }
        }
        return data.ToArray();
    }
}
