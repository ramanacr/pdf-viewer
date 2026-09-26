using System;
using PdfEngine.Vector.Functions;

namespace PdfEngine.Vector.Color;

public abstract partial class PdfColorSpace
{
    /// <summary>
    /// ICCBased (ISO 32000-2 8.6.5.5). No colour management module is used: colours are rendered through the
    /// /Alternate space when present and compatible, otherwise through the device space implied by /N.
    /// </summary>
    public sealed class IccBasedColorSpace : PdfColorSpace
    {
        private readonly double[]? _range;

        internal IccBasedColorSpace(int components, PdfColorSpace alternate, double[]? range)
        {
            NumberOfComponents = components;
            AlternateColorSpace = alternate;
            _range = range;
        }

        public override string Name => "ICCBased";
        public override int NumberOfComponents { get; }

        /// <summary>The space used to render: the profile's /Alternate or the device space for /N.</summary>
        public PdfColorSpace AlternateColorSpace { get; }

        public override (double Min, double Max) GetComponentRange(int i) =>
            _range != null && 2 * i + 1 < _range.Length ? (_range[2 * i], _range[2 * i + 1]) : AlternateColorSpace.GetComponentRange(i);

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f) =>
            AlternateColorSpace.ToRgbColor(components, alpha);

        public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb) =>
            AlternateColorSpace.ToRgb24(components, rgb);
    }

    /// <summary>
    /// DeviceN (ISO 32000-2 8.6.6.5): N colorant tints mapped through the tint transform into the alternate
    /// space. When every colorant is /None nothing is painted (<see cref="IsNone"/>).
    /// </summary>
    public sealed class DeviceNColorSpace : PdfColorSpace
    {
        private readonly string[] _colorants;
        private readonly PdfFunction? _tintTransform;
        private readonly PdfFallbackReason? _unsupportedReason;

        public DeviceNColorSpace(string[] colorants, PdfColorSpace alternateColorSpace, PdfFunction? tintTransform, PdfFallbackReason? unsupportedReason = null)
        {
            _colorants = colorants is { Length: > 0 } ? colorants : throw new ArgumentException("DeviceN requires colorants.", nameof(colorants));
            AlternateColorSpace = alternateColorSpace ?? throw new ArgumentNullException(nameof(alternateColorSpace));
            _tintTransform = tintTransform;
            IsNone = Array.TrueForAll(colorants, c => c == "None");
            _unsupportedReason = IsNone ? null : unsupportedReason ?? (tintTransform == null ? PdfFallbackReason.UnsupportedColorSpace : null);
        }

        public override string Name => "DeviceN";
        public override int NumberOfComponents => _colorants.Length;

        /// <summary>Colorant names in component order.</summary>
        public ReadOnlySpan<string> Colorants => _colorants;

        public PdfColorSpace AlternateColorSpace { get; }
        public PdfFunction? TintTransform => _tintTransform;
        public override bool IsNone { get; }
        public override PdfFallbackReason? UnsupportedReason => _unsupportedReason;

        /// <summary>All tints start at 1.0 (8.6.6.5).</summary>
        public override double[] InitialComponents
        {
            get
            {
                var initial = new double[_colorants.Length];
                Array.Fill(initial, 1.0);
                return initial;
            }
        }

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            if (IsNone)
                return PdfColor.Transparent;

            int n = _colorants.Length;
            Span<double> tints = n <= 32 ? stackalloc double[n] : new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                tints[i] = Math.Clamp(Component(components, i, 1.0), 0, 1);
                sum += tints[i];
            }

            if (_tintTransform != null && TryTint(_tintTransform, AlternateColorSpace, tints, alpha, out var color))
                return color;

            // Flagged fallback: subtractive average of the tints.
            return PdfColor.FromGray((float)(1.0 - sum / n), alpha);
        }
    }

    /// <summary>
    /// Pattern color space (ISO 32000-2 8.6.6.2). <c>[/Pattern base]</c> keeps the base space whose components
    /// colour an uncolored tiling pattern. Rendering patterns is not implemented, so the space is always
    /// flagged with <see cref="PdfFallbackReason.Pattern"/>; <see cref="ToRgbColor"/> gives the base color
    /// when available, else a mid-gray placeholder.
    /// </summary>
    public sealed class PatternColorSpace : PdfColorSpace
    {
        public PatternColorSpace(PdfColorSpace? baseColorSpace)
        {
            // A pattern cannot be the base of a pattern.
            PatternBaseColorSpace = baseColorSpace is { IsPattern: false } ? baseColorSpace : null;
        }

        public override string Name => "Pattern";
        public override int NumberOfComponents => PatternBaseColorSpace?.NumberOfComponents ?? 0;
        public override bool IsPattern => true;
        public override PdfColorSpace? PatternBaseColorSpace { get; }
        public override PdfFallbackReason? UnsupportedReason => PdfFallbackReason.Pattern;

        public override double[] InitialComponents => PatternBaseColorSpace?.InitialComponents ?? [];

        public override (double Min, double Max) GetComponentRange(int i) =>
            PatternBaseColorSpace?.GetComponentRange(i) ?? (0.0, 1.0);

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f) =>
            PatternBaseColorSpace?.ToRgbColor(components, alpha) ?? PdfColor.FromGray(0.5f, alpha);
    }

    /// <summary>
    /// A color space that is unknown, unresolvable or malformed. Carries
    /// <see cref="PdfFallbackReason.UnsupportedColorSpace"/>; colors are approximated from the component
    /// count (1 = gray, 3 = RGB, 4 = CMYK, otherwise black) so rendering can continue while flagged.
    /// </summary>
    public sealed class UnsupportedColorSpace : PdfColorSpace
    {
        public UnsupportedColorSpace(string name, int components, string description)
        {
            Name = string.IsNullOrEmpty(name) ? "Unknown" : name;
            NumberOfComponents = Math.Clamp(components, 1, 32);
            Description = description;
        }

        public override string Name { get; }
        public override int NumberOfComponents { get; }

        /// <summary>Why the space is unsupported (diagnostics only; decide fallback from <see cref="UnsupportedReason"/>).</summary>
        public string Description { get; }

        public override PdfFallbackReason? UnsupportedReason => PdfFallbackReason.UnsupportedColorSpace;

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f) => NumberOfComponents switch
        {
            1 => DeviceGray.ToRgbColor(components, alpha),
            3 => DeviceRgb.ToRgbColor(components, alpha),
            4 => DeviceCmyk.ToRgbColor(components, alpha),
            _ => new PdfColor(0f, 0f, 0f, Math.Clamp(alpha, 0f, 1f)),
        };
    }
}
