using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Stamp and measurement appearances as the annotation editor writes them (a designed dynamic
/// stamp with its embedded font subsets, a turned stamp, a picture stamp with transparency, and
/// a labelled area measurement), drawn by the Skia backend against the Direct2D backend.
/// </summary>
public class StampParityTests
{
    private readonly Parity _parity;
    public StampParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    private static async Task<byte[]> Annotated(params PdfAnnotation[] annotations)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.9 0.9 0.9 rg 0 0 200 200 re f", mediaBox: "[0 0 200 200]");
        byte[] original = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(original);
        return PdfAnnotationEditor.Apply(doc, original, e => { foreach (var a in annotations) e.Add(a); });
    }

    private static PdfImageContent Picture()
    {
        int w = 16, h = 8;
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = (byte)(i * 7); rgb[i * 3 + 1] = 90; rgb[i * 3 + 2] = (byte)(255 - i);
            alpha[i] = (byte)(i % w * 16);
        }
        return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, alpha);
    }

    [Fact]
    public async Task DesignedAndTurnedStamps()
    {
        var pdf = await Annotated(
            new PdfAnnotation
            {
                PageNumber = 1, Subtype = "Stamp", Icon = "Approved", Rect = new PdfRect(20, 130, 160, 56),
                Stamp = new PdfStampAppearance { Subtitle = PdfStamps.DynamicLine("Jane Doe", new DateTime(2026, 10, 25, 10, 42, 0)) },
            },
            new PdfAnnotation
            {
                PageNumber = 1, Subtype = "Stamp", Icon = "NotApproved", Rect = PdfStamps.RotatedBounds(new PdfPoint(100, 70), 140, 36, 30),
                Stamp = new PdfStampAppearance { Rotation = 30, Width = 140, Height = 36 },
            });
        // The dynamic line is small text (about 7 pt): the budget is TextParityTests.SmallBodyText's,
        // for the backends' known difference in how much ink thin stems get.
        await _parity.AssertMatches("stamps-designed", pdf, 2.5, 0.015);
    }

    [Fact]
    public async Task PictureStamp_AndAreaMeasurement()
    {
        var pdf = await Annotated(
            new PdfAnnotation
            {
                PageNumber = 1, Subtype = "Stamp", Icon = "Custom", Rect = new PdfRect(20, 120, 160, 60),
                Stamp = new PdfStampAppearance { Image = Picture() },
            },
            PdfMeasurements.Create(PdfMeasurementKind.Area, 1,
                new[] { new PdfPoint(30, 20), new PdfPoint(170, 20), new PdfPoint(150, 100), new PdfPoint(40, 90) },
                PdfMeasure.FromScale(1, "in", 10, "ft"), new[] { 0.8, 0.1, 0.1 }, 1.5));
        await _parity.AssertMatches("stamps-picture-area", pdf, 1.5, 0.02);
    }
}
