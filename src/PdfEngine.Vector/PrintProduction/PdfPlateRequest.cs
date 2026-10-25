using System;
using System.Collections.Generic;
using System.Threading;
using PdfEngine.Vector.Color;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>
/// Which plates a separation display list draws: one to three plates of <paramref name="Model"/>, drawn as the
/// red, green and blue channels of the page (one plate: gray). A channel's value is 1 − ink, so white is no ink.
/// </summary>
/// <param name="SimulateOverprint">
/// Honour /OP, /op and /OPM (ISO 32000-2 8.6.7): an overprinting object leaves the plates it does not paint as
/// they are instead of knocking them out. Needs exactly one plate, as each plate keeps or loses the backdrop on
/// its own.
/// </param>
public sealed record PdfPlateRequest(PdfInkModel Model, IReadOnlyList<int> Channels, bool SimulateOverprint)
{
    public PdfInkModel Model { get; } = Model ?? throw new ArgumentNullException(nameof(Model));

    public IReadOnlyList<int> Channels { get; } = Validate(Model, Channels, SimulateOverprint);

    /// <summary>The plates drawn, as a mask of the model's channels.</summary>
    public ulong ChannelMask
    {
        get
        {
            ulong mask = 0;
            foreach (int c in Channels) mask |= 1UL << c;
            return mask;
        }
    }

    private static IReadOnlyList<int> Validate(PdfInkModel model, IReadOnlyList<int> channels, bool overprint)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count is < 1 or > 3)
            throw new ArgumentException("A plate display list draws one to three plates.", nameof(channels));
        if (overprint && channels.Count != 1)
            throw new ArgumentException("Overprint simulation draws one plate at a time.", nameof(channels));
        foreach (int c in channels)
            if (c < 0 || c >= model.ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(channels), "A plate is not one of the model's.");
        return channels;
    }

    /// <summary>A stable text for cache keys.</summary>
    internal string Key => $"{string.Join(",", Channels)}|{(SimulateOverprint ? 1 : 0)}|{string.Join("/", Model.Channels)}";
}

/// <summary>
/// A colour space whose colours are plate values: every colour of <see cref="Inner"/> becomes the ink it puts on
/// the requested plates, as 1 − ink in the red, green and blue channels. The interpreter paints with it in place of
/// the document's spaces when it builds a plate display list, so images, shadings, patterns and text all separate
/// through the one model.
/// </summary>
internal sealed class PlateColorSpace : PdfColorSpace
{
    private readonly PdfPlateRequest _request;
    private readonly int _a, _b, _c;
    private byte[]? _lut;

    public PlateColorSpace(PdfColorSpace inner, PdfPlateRequest request)
    {
        Inner = inner;
        _request = request;
        var ch = request.Channels;
        _a = ch[0];
        _b = ch.Count > 1 ? ch[1] : ch[0];
        _c = ch.Count > 2 ? ch[2] : _b;
    }

    public PdfColorSpace Inner { get; }

    // Indexed keeps its name: image decoding reads its samples as palette indices.
    public override string Name => Inner.Name == "Indexed" ? "Indexed" : "Plate";
    public override int NumberOfComponents => Inner.NumberOfComponents;
    public override double[] InitialComponents => Inner.InitialComponents;
    public override (double Min, double Max) GetComponentRange(int i) => Inner.GetComponentRange(i);
    public override PdfFallbackReason? UnsupportedReason => Inner.UnsupportedReason;
    public override bool IsNone => Inner.IsNone;

    public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
    {
        int n = _request.Model.ChannelCount;
        Span<double> inks = n <= 64 ? stackalloc double[n] : new double[n];
        _request.Model.Evaluate(Inner, components, inks);
        return new PdfColor((float)(1 - inks[_a]), (float)(1 - inks[_b]), (float)(1 - inks[_c]), Math.Clamp(alpha, 0f, 1f));
    }

    public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
    {
        int n = Math.Max(1, NumberOfComponents);
        int pixels = Math.Min(components.Length / n, rgb.Length / 3);
        // Single-component images (gray, separation, indexed) repeat few values: a 256-entry table.
        if (n == 1 && Inner.Name != "Indexed")
        {
            var lut = _lut ??= BuildLut();
            var (min, max) = GetComponentRange(0);
            for (int p = 0; p < pixels; p++)
            {
                int i = (int)Math.Clamp(Math.Round((components[p] - min) / (max - min == 0 ? 1 : max - min) * 255), 0, 255) * 3;
                rgb[3 * p] = lut[i];
                rgb[3 * p + 1] = lut[i + 1];
                rgb[3 * p + 2] = lut[i + 2];
            }
            return;
        }
        base.ToRgb24(components, rgb);
    }

    private byte[] BuildLut()
    {
        var lut = new byte[256 * 3];
        var (min, max) = GetComponentRange(0);
        for (int i = 0; i < 256; i++)
        {
            var c = ToRgbColor([min + i / 255.0 * (max - min)]);
            lut[3 * i] = ToByte(c.R);
            lut[3 * i + 1] = ToByte(c.G);
            lut[3 * i + 2] = ToByte(c.B);
        }
        return lut;
    }
}
