using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Functions;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>
/// The inks of a separation: the four process plates (Cyan, Magenta, Yellow, Black) and the spot plates, and
/// how a colour in any PDF colour space becomes ink on them (ISO 32000-2 8.6.6.4, 8.6.6.5, 10.4.2).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>DeviceCMYK and four-component ICCBased colours put their components on the process plates as they
/// are: the output is taken to be the profile's own condition, so there is no ICC-to-ICC (device link) conversion.</item>
/// <item>DeviceGray (and one-component ICCBased) is black ink only: K = 1 − gray, as PDF/X and Acrobat separate it.</item>
/// <item>RGB, Lab and the other CIE spaces are shown in sRGB first and separated by <see cref="CmykSeparation"/>,
/// the numerical inverse of the engine's SWOP model (approximate; see there).</item>
/// <item>A Separation or DeviceN colorant named Cyan, Magenta, Yellow or Black goes on that process plate; a spot
/// colorant of the model goes on its own plate; a spot the model converts to process (or does not know) is
/// painted through the tint transform and the alternate space. /All paints every plate; /None paints nothing.</item>
/// </list>
/// </remarks>
public sealed class PdfInkModel
{
    /// <summary>At most this many spot plates; further spots convert to process (64-bit plate masks).</summary>
    public const int MaxSpots = 60;

    public static readonly IReadOnlyList<string> ProcessColorants = ["Cyan", "Magenta", "Yellow", "Black"];

    /// <summary>The four process plates.</summary>
    public const ulong ProcessMask = 0b1111;

    private readonly string[] _channels;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <summary>A model with only the process plates: every spot converts to process.</summary>
    public static readonly PdfInkModel ProcessOnly = new(Array.Empty<string>());

    /// <param name="spots">Spot colorant names, each a plate of its own (in this order).</param>
    public PdfInkModel(IEnumerable<string> spots)
    {
        ArgumentNullException.ThrowIfNull(spots);
        var list = new List<string>(ProcessColorants);
        foreach (string spot in spots)
        {
            if (string.IsNullOrEmpty(spot) || spot is "All" or "None" || ProcessColorants.Contains(spot) || list.Contains(spot))
                continue;
            if (list.Count - 4 >= MaxSpots) break;
            list.Add(spot);
        }
        _channels = list.ToArray();
        for (int i = 0; i < _channels.Length; i++) _index[_channels[i]] = i;
    }

    /// <summary>Plate names: Cyan, Magenta, Yellow, Black, then the spots.</summary>
    public IReadOnlyList<string> Channels => _channels;

    public int ChannelCount => _channels.Length;

    /// <summary>Every plate of the model.</summary>
    public ulong AllMask => _channels.Length >= 64 ? ulong.MaxValue : (1UL << _channels.Length) - 1;

    /// <summary>The plate of a colorant, or −1 when the model has none for it (it then converts to process).</summary>
    public int IndexOf(string colorant) => _index.TryGetValue(colorant, out int i) ? i : -1;

    /// <summary>
    /// The ink (0..1) the colour puts on each plate, written to <paramref name="inks"/> (at least
    /// <see cref="ChannelCount"/> long; plates the colour does not paint are 0), and the plates it paints:
    /// the plates an overprinting object changes. Overprint mode 1 is not applied here (see <see cref="NonzeroMask"/>).
    /// </summary>
    internal ulong Evaluate(PdfColorSpace space, ReadOnlySpan<double> components, Span<double> inks)
    {
        ArgumentNullException.ThrowIfNull(space);
        if (inks.Length < _channels.Length)
            throw new ArgumentException("The ink buffer is shorter than the number of plates.", nameof(inks));
        inks[.._channels.Length].Clear();
        return Evaluate(space, components, inks, 0);
    }

    /// <summary>
    /// For overprint mode 1 (ISO 32000-2 8.6.7): the process plates a DeviceCMYK (or four-component ICCBased)
    /// colour paints, those whose component is not zero. Null for any other space, where the mode does not apply.
    /// </summary>
    internal static ulong? NonzeroMask(PdfColorSpace space, ReadOnlySpan<double> components)
    {
        var s = Unwrap(space);
        if (!IsCmyk(s)) return null;
        ulong mask = 0;
        for (int i = 0; i < 4 && i < components.Length; i++)
            if (components[i] > 0) mask |= 1UL << i;
        return mask;
    }

    private static bool IsCmyk(PdfColorSpace s) =>
        s.Name == "DeviceCMYK" || s is PdfColorSpace.IccBasedColorSpace { NumberOfComponents: 4 };

    internal static PdfColorSpace Unwrap(PdfColorSpace s) => s is PlateColorSpace p ? p.Inner : s;

    private ulong Evaluate(PdfColorSpace space, ReadOnlySpan<double> c, Span<double> inks, int depth)
    {
        if (depth > 8) return 0;
        switch (space)
        {
            case PlateColorSpace plate:
                return Evaluate(plate.Inner, c, inks, depth + 1);

            case PdfColorSpace.IndexedColorSpace indexed:
            {
                var baseSpace = indexed.BaseColorSpace;
                int n = Math.Max(1, baseSpace.NumberOfComponents);
                int index = (int)Math.Clamp(Math.Round(Component(c, 0, 0)), 0, indexed.HiVal);
                var table = indexed.LookupTable.Span;
                Span<double> baseComps = stackalloc double[Math.Min(n, 32)];
                if ((index + 1) * n > table.Length)
                {
                    inks[3] = 1; // a missing entry renders black
                    return ProcessMask;
                }
                for (int i = 0; i < baseComps.Length; i++)
                {
                    var (min, max) = baseSpace.GetComponentRange(i);
                    baseComps[i] = min + table[index * n + i] / 255.0 * (max - min);
                }
                return Evaluate(baseSpace, baseComps, inks, depth + 1);
            }

            case PdfColorSpace.SeparationColorSpace separation:
            {
                if (separation.IsNone) return 0;
                double tint = Math.Clamp(Component(c, 0, 1), 0, 1);
                if (separation.ColorantName == "All")
                {
                    for (int i = 0; i < _channels.Length; i++) inks[i] = tint;
                    return AllMask;
                }
                int plate = IndexOf(separation.ColorantName);
                if (plate >= 0)
                {
                    inks[plate] = tint;
                    return 1UL << plate;
                }
                Span<double> one = stackalloc double[1] { tint };
                return ThroughAlternate(separation.TintTransform, separation.AlternateColorSpace, one, inks, depth);
            }

            case PdfColorSpace.DeviceNColorSpace deviceN:
            {
                if (deviceN.IsNone) return 0;
                var names = deviceN.Colorants;
                bool known = true;
                for (int i = 0; i < names.Length && known; i++)
                    known = names[i] == "None" || IndexOf(names[i]) >= 0;
                Span<double> tints = stackalloc double[Math.Min(names.Length, 32)];
                for (int i = 0; i < tints.Length; i++) tints[i] = Math.Clamp(Component(c, i, 1), 0, 1);
                if (!known)
                    return ThroughAlternate(deviceN.TintTransform, deviceN.AlternateColorSpace, tints, inks, depth);
                ulong mask = 0;
                for (int i = 0; i < tints.Length; i++)
                {
                    if (names[i] == "None") continue;
                    int plate = IndexOf(names[i]);
                    // A colorant named twice: the last value wins, as a later operand would.
                    inks[plate] = tints[i];
                    mask |= 1UL << plate;
                }
                return mask;
            }

            case PdfColorSpace.IccBasedColorSpace icc when icc.NumberOfComponents == 4:
            case var _ when space.Name == "DeviceCMYK":
                for (int i = 0; i < 4; i++) inks[i] = Math.Clamp(Component(c, i, 0), 0, 1);
                return ProcessMask;

            case PdfColorSpace.IccBasedColorSpace icc when icc.NumberOfComponents == 1:
            {
                var (min, max) = icc.GetComponentRange(0);
                double gray = max > min ? (Component(c, 0, 0) - min) / (max - min) : 0;
                inks[3] = Math.Clamp(1 - gray, 0, 1);
                return ProcessMask;
            }

            case var _ when space.Name == "DeviceGray":
                inks[3] = Math.Clamp(1 - Component(c, 0, 0), 0, 1);
                return ProcessMask;

            case var _ when space.IsPattern:
                return space.PatternBaseColorSpace is { } patternBase ? Evaluate(patternBase, c, inks, depth + 1) : 0;

            default:
            {
                // RGB, CalRGB, CalGray, Lab, unsupported spaces: as they show on screen, then separated.
                var rgb = space.ToRgbColor(c);
                if (space.Name == "CalGray")
                {
                    inks[3] = Math.Clamp(1 - rgb.G, 0, 1);
                    return ProcessMask;
                }
                Span<double> cmyk = stackalloc double[4];
                CmykSeparation.FromSrgb(rgb.R, rgb.G, rgb.B, cmyk);
                for (int i = 0; i < 4; i++) inks[i] = cmyk[i];
                return ProcessMask;
            }
        }
    }

    private ulong ThroughAlternate(PdfFunction? tint, PdfColorSpace alternate, ReadOnlySpan<double> tints, Span<double> inks, int depth)
    {
        if (tint == null || tint.OutputCount != alternate.NumberOfComponents || tint.OutputCount > 32)
        {
            // No usable tint transform: the mean tint as black, as the engine shows such a space in gray.
            double sum = 0;
            foreach (double t in tints) sum += t;
            inks[3] = tints.Length == 0 ? 0 : sum / tints.Length;
            return ProcessMask;
        }
        Span<double> output = stackalloc double[tint.OutputCount];
        try
        {
            tint.Evaluate(tints, output);
        }
        catch (Diagnostics.PdfVectorException)
        {
            inks[3] = tints.Length > 0 ? tints[0] : 0;
            return ProcessMask;
        }
        return Evaluate(alternate, output, inks, depth + 1);
    }

    private static double Component(ReadOnlySpan<double> c, int i, double fallback) =>
        i < c.Length && !double.IsNaN(c[i]) ? c[i] : fallback;

    /// <summary>sRGB (0..1) to the CMYK the separations use for RGB content (see <see cref="CmykSeparation"/>).</summary>
    public static (double C, double M, double Y, double K) SeparateRgb(double r, double g, double b)
    {
        Span<double> cmyk = stackalloc double[4];
        CmykSeparation.FromSrgb(r, g, b, cmyk);
        return (cmyk[0], cmyk[1], cmyk[2], cmyk[3]);
    }

    /// <summary>The sRGB the engine shows for a DeviceCMYK colour (the SWOP model, or Adobe's profile when installed).</summary>
    public static PdfColor ShowCmyk(double c, double m, double y, double k) =>
        PdfColorSpace.DeviceCmyk.ToRgbColor([c, m, y, k]);
}
