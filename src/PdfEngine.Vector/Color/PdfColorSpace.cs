using System;
using System.Collections.Generic;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Functions;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Color;

/// <summary>
/// Color space management conforming to ISO 32000-2 Clause 8.6.
/// Supports the device spaces, CIE-based spaces (CalGray, CalRGB, Lab, ICCBased via /N or /Alternate)
/// and the special spaces (Indexed, Separation, DeviceN, Pattern). Constructs that cannot be rendered
/// faithfully are returned as usable spaces flagged through <see cref="UnsupportedReason"/> rather than
/// being silently substituted.
/// </summary>
public abstract partial class PdfColorSpace
{
    public abstract string Name { get; }
    public abstract int NumberOfComponents { get; }
    public abstract PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f);

    /// <summary>Non-null when this space cannot be rendered faithfully; callers decide fallback from it.</summary>
    public virtual PdfFallbackReason? UnsupportedReason => null;

    /// <summary>True for Pattern color spaces (ISO 32000-2 8.6.6.2).</summary>
    public virtual bool IsPattern => false;

    /// <summary>For <c>[/Pattern base]</c> (uncolored tiling patterns), the underlying space; otherwise null.</summary>
    public virtual PdfColorSpace? PatternBaseColorSpace => null;

    /// <summary>
    /// True for a Separation or DeviceN space whose colorants are all /None: painting operators produce no
    /// visible marks (ISO 32000-2 8.6.6.4), and <see cref="ToRgbColor"/> returns a fully transparent color.
    /// </summary>
    public virtual bool IsNone => false;

    /// <summary>
    /// Initial color components set by the CS/cs operators (ISO 32000-2 8.6.8 / Table 73).
    /// Returns a new array on each call.
    /// </summary>
    public virtual double[] InitialComponents
    {
        get
        {
            var initial = new double[NumberOfComponents];
            for (int i = 0; i < initial.Length; i++)
            {
                var (min, max) = GetComponentRange(i);
                initial[i] = Math.Clamp(0.0, min, max);
            }
            return initial;
        }
    }

    /// <summary>
    /// Valid range of component <paramref name="i"/>; used for image /Decode defaults (ISO 32000-2 8.9.5.2).
    /// Default is 0..1.
    /// </summary>
    public virtual (double Min, double Max) GetComponentRange(int i) => (0.0, 1.0);

    /// <summary>
    /// Converts packed pixels to 8-bit RGB. <paramref name="components"/> holds consecutive pixels of
    /// <see cref="NumberOfComponents"/> values each; three bytes per pixel are written to <paramref name="rgb"/>.
    /// Pixels beyond either span's capacity are ignored.
    /// </summary>
    public virtual void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
    {
        int n = NumberOfComponents;
        if (n <= 0) return;
        int pixels = Math.Min(components.Length / n, rgb.Length / 3);
        for (int p = 0; p < pixels; p++)
        {
            var color = ToRgbColor(components.Slice(p * n, n));
            rgb[3 * p] = ToByte(color.R);
            rgb[3 * p + 1] = ToByte(color.G);
            rgb[3 * p + 2] = ToByte(color.B);
        }
    }

    /// <summary>Maps a 0..1 value to a byte with rounding and clamping.</summary>
    protected static byte ToByte(double value) =>
        double.IsNaN(value) ? (byte)0 : (byte)Math.Clamp((int)(value * 255.0 + 0.5), 0, 255);

    private static double Component(ReadOnlySpan<double> components, int index, double fallback = 0.0) =>
        index < components.Length && !double.IsNaN(components[index]) ? components[index] : fallback;

    public static readonly PdfColorSpace DeviceGray = new DeviceGrayColorSpace();
    public static readonly PdfColorSpace DeviceRgb = new DeviceRgbColorSpace();
    public static readonly PdfColorSpace DeviceCmyk = new DeviceCmykColorSpace();

    /// <summary>
    /// Resolves a color space from a name (device family or /ColorSpace resource key) or an array definition.
    /// Unknown or unresolvable definitions yield a flagged <see cref="UnsupportedColorSpace"/>.
    /// </summary>
    /// <param name="csObj">Name, array, or indirect reference to either.</param>
    /// <param name="resolver">Indirect object resolver.</param>
    /// <param name="pageResources">Resource dictionary used to look up named spaces.</param>
    /// <param name="limits">Security ceilings (nesting depth, stream and function limits).</param>
    public static PdfColorSpace Resolve(
        PdfObject? csObj,
        PdfObjectResolver resolver,
        PdfDictionary? pageResources = null,
        PdfSecurityLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var ctx = new ResolveContext(resolver, pageResources, limits ?? PdfSecurityLimits.Default);
        return Resolve(csObj, ctx, depth: 0);
    }

    private sealed record ResolveContext(PdfObjectResolver Resolver, PdfDictionary? Resources, PdfSecurityLimits Limits);

    private static PdfColorSpace Resolve(PdfObject? csObj, ResolveContext ctx, int depth)
    {
        // Bounds Indexed-of-Indexed chains, self-referencing arrays and cyclic resource names.
        if (depth > ctx.Limits.MaxColorSpaceNestingDepth)
            return new UnsupportedColorSpace("Nested", 1, "Color space nesting depth exceeded.");

        var resolved = ctx.Resolver.Resolve(csObj);
        return resolved switch
        {
            PdfName name => ResolveName(name.Value, ctx, depth),
            PdfArray { Count: > 0 } array => ResolveArray(array, ctx, depth),
            _ => new UnsupportedColorSpace("Unknown", 1, "Color space definition is missing or not a name/array."),
        };
    }

    private static PdfColorSpace? ResolveDeviceName(string name) => name switch
    {
        "DeviceGray" or "G" or "CalGray" => DeviceGray,
        "DeviceRGB" or "RGB" or "CalRGB" => DeviceRgb,
        // CalCMYK is obsolete; ISO 32000-2 8.6.5.1 says to treat it as DeviceCMYK.
        "DeviceCMYK" or "CMYK" or "CalCMYK" => DeviceCmyk,
        "Pattern" => new PatternColorSpace(null),
        _ => null,
    };

    private static PdfColorSpace ResolveName(string name, ResolveContext ctx, int depth)
    {
        var device = ResolveDeviceName(name);
        if (device != null)
            return device;

        if (ctx.Resources != null
            && ctx.Resolver.Resolve(ctx.Resources["ColorSpace"]) is PdfDictionary csDict
            && csDict.TryGetValue(name, out var definition))
        {
            return Resolve(definition, ctx, depth + 1);
        }

        return new UnsupportedColorSpace(name, 1, $"Color space /{name} is not defined in the resources.");
    }

    private static PdfColorSpace ResolveArray(PdfArray arr, ResolveContext ctx, int depth)
    {
        string family = ctx.Resolver.Resolve(arr[0]) is PdfName fam ? fam.Value : string.Empty;
        PdfObject? Arg(int i) => i < arr.Count ? ctx.Resolver.Resolve(arr[i]) : null;

        switch (family)
        {
            case "CalGray":
                return CalGrayColorSpace.Create(Arg(1) as PdfDictionary, ctx.Resolver);

            case "CalRGB":
                return CalRgbColorSpace.Create(Arg(1) as PdfDictionary, ctx.Resolver);

            case "Lab":
                return LabColorSpace.Create(Arg(1) as PdfDictionary, ctx.Resolver);

            case "ICCBased":
                return ResolveIccBased(Arg(1), ctx, depth);

            case "Indexed" or "I":
                return ResolveIndexed(arr, ctx, depth);

            case "Separation":
                return ResolveSeparation(arr, ctx, depth);

            case "DeviceN":
                return ResolveDeviceN(arr, ctx, depth);

            case "Pattern":
                return new PatternColorSpace(arr.Count > 1 ? Resolve(arr[1], ctx, depth + 1) : null);
        }

        // Single-element arrays such as [/DeviceRGB] are tolerated.
        var device = arr.Count == 1 ? ResolveDeviceName(family) : null;
        return device ?? new UnsupportedColorSpace(
            family.Length > 0 ? family : "Unknown", 1, $"Unsupported color space family /{family}.");
    }

    private static PdfColorSpace ResolveIccBased(PdfObject? profile, ResolveContext ctx, int depth)
    {
        if (profile is not PdfStream stream)
            return new UnsupportedColorSpace("ICCBased", 1, "ICCBased color space has no profile stream.");

        var dict = stream.Dictionary;
        int n = ctx.Resolver.Resolve(dict["N"]) is { } nObj && nObj.TryGetInteger(out long nl) ? (int)Math.Clamp(nl, 0, 64) : 0;

        // Prefer a resolvable /Alternate with the same number of components (ISO 32000-2 8.6.5.5).
        PdfColorSpace? alternate = null;
        if (dict.TryGetValue("Alternate", out var altObj))
        {
            var candidate = Resolve(altObj, ctx, depth + 1);
            if (candidate.UnsupportedReason == null && !candidate.IsPattern
                && (n == 0 || candidate.NumberOfComponents == n))
            {
                alternate = candidate;
                if (n == 0) n = candidate.NumberOfComponents;
            }
        }

        alternate ??= n switch
        {
            1 => DeviceGray,
            3 => DeviceRgb,
            4 => DeviceCmyk,
            _ => null,
        };

        double[]? range = null;
        if (ctx.Resolver.Resolve(dict["Range"]) is PdfArray rangeArr && rangeArr.Count >= 2 * n)
        {
            range = new double[2 * n];
            for (int i = 0; i < range.Length; i++)
                range[i] = ctx.Resolver.Resolve(rangeArr[i]) is { } r && r.TryGetNumber(out double v) ? v : (i % 2 == 0 ? 0 : 1);
        }

        if (alternate == null)
        {
            return new UnsupportedColorSpace("ICCBased", n is >= 1 and <= 32 ? n : 1,
                $"ICCBased profile with /N {n} has no usable alternate space.");
        }

        return new IccBasedColorSpace(n, alternate, range);
    }

    private static PdfColorSpace ResolveIndexed(PdfArray arr, ResolveContext ctx, int depth)
    {
        if (arr.Count < 4)
            return new UnsupportedColorSpace("Indexed", 1, "Indexed color space requires base, hival and lookup.");

        var baseCs = Resolve(arr[1], ctx, depth + 1);
        if (baseCs.IsPattern || baseCs.UnsupportedReason != null)
            return new UnsupportedColorSpace("Indexed", 1, "Indexed color space has an unsupported base space.");

        var hiObj = ctx.Resolver.Resolve(arr[2]);
        int hival = hiObj != null && hiObj.TryGetInteger(out long hi) ? (int)Math.Clamp(hi, 0, 255) : 255;

        byte[] lookupTable;
        switch (ctx.Resolver.Resolve(arr[3]))
        {
            case PdfString str:
                lookupTable = str.RawBytes.ToArray();
                break;
            case PdfStream stm:
                try
                {
                    lookupTable = new PdfStreamDecoder(ctx.Limits, ctx.Resolver.Resolve).DecodeStream(stm);
                }
                catch (PdfVectorException)
                {
                    return new UnsupportedColorSpace("Indexed", 1, "Indexed lookup stream cannot be decoded.");
                }
                break;
            default:
                return new UnsupportedColorSpace("Indexed", 1, "Indexed color space has no lookup table.");
        }

        return new IndexedColorSpace(baseCs, hival, lookupTable);
    }

    private static PdfColorSpace ResolveSeparation(PdfArray arr, ResolveContext ctx, int depth)
    {
        if (arr.Count < 4)
            return new UnsupportedColorSpace("Separation", 1, "Separation color space requires name, alternate and tint transform.");

        string colorant = ctx.Resolver.Resolve(arr[1]) is PdfName n ? n.Value : string.Empty;
        var alternate = Resolve(arr[2], ctx, depth + 1);
        var (tint, reason) = ParseTint(arr[3], 1, alternate, ctx);
        return new SeparationColorSpace(colorant, alternate, tint, reason);
    }

    private static PdfColorSpace ResolveDeviceN(PdfArray arr, ResolveContext ctx, int depth)
    {
        if (arr.Count < 4 || ctx.Resolver.Resolve(arr[1]) is not PdfArray namesArr || namesArr.Count == 0)
            return new UnsupportedColorSpace("DeviceN", 1, "DeviceN color space requires names, alternate and tint transform.");

        // ISO 32000-2 8.6.6.5 / Annex C: at most 32 colorants.
        if (namesArr.Count > 32)
            return new UnsupportedColorSpace("DeviceN", 1, "DeviceN color space has more than 32 colorants.");

        var names = new string[namesArr.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = ctx.Resolver.Resolve(namesArr[i]) is PdfName nm ? nm.Value : string.Empty;

        var alternate = Resolve(arr[2], ctx, depth + 1);
        var (tint, reason) = ParseTint(arr[3], names.Length, alternate, ctx);
        return new DeviceNColorSpace(names, alternate, tint, reason);
    }

    /// <summary>
    /// Parses and smoke-tests a tint transform. A transform that is malformed, has the wrong arity, or fails
    /// on probe inputs is dropped and the space flagged with <see cref="PdfFallbackReason.UnsupportedColorSpace"/>.
    /// </summary>
    private static (PdfFunction? Tint, PdfFallbackReason? Reason) ParseTint(
        PdfObject? tintObj, int inputs, PdfColorSpace alternate, ResolveContext ctx)
    {
        if (alternate.UnsupportedReason != null || alternate.IsPattern)
            return (null, PdfFallbackReason.UnsupportedColorSpace);

        try
        {
            var tint = PdfFunction.ParseMany(tintObj, ctx.Resolver, ctx.Limits, PdfFallbackReason.UnsupportedColorSpace);
            if (tint.InputCount != inputs || tint.OutputCount != alternate.NumberOfComponents)
                return (null, PdfFallbackReason.UnsupportedColorSpace);

            Span<double> input = stackalloc double[inputs];
            Span<double> output = stackalloc double[tint.OutputCount];
            foreach (double probe in (ReadOnlySpan<double>)[0.0, 0.5, 1.0])
            {
                input.Fill(probe);
                tint.Evaluate(input, output);
            }
            return (tint, null);
        }
        catch (PdfVectorException)
        {
            return (null, PdfFallbackReason.UnsupportedColorSpace);
        }
    }

    // ------------------------------------------------------------------ device spaces

    private sealed class DeviceGrayColorSpace : PdfColorSpace
    {
        public override string Name => "DeviceGray";
        public override int NumberOfComponents => 1;

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f) =>
            PdfColor.FromGray((float)Component(components, 0), alpha);

        public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
        {
            int pixels = Math.Min(components.Length, rgb.Length / 3);
            for (int p = 0; p < pixels; p++)
            {
                byte g = ToByte(components[p]);
                rgb[3 * p] = g;
                rgb[3 * p + 1] = g;
                rgb[3 * p + 2] = g;
            }
        }
    }

    private sealed class DeviceRgbColorSpace : PdfColorSpace
    {
        public override string Name => "DeviceRGB";
        public override int NumberOfComponents => 3;

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f) =>
            PdfColor.FromRgb(
                (float)Component(components, 0),
                (float)Component(components, 1),
                (float)Component(components, 2),
                alpha);

        public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
        {
            int count = Math.Min(components.Length / 3, rgb.Length / 3) * 3;
            for (int i = 0; i < count; i++)
                rgb[i] = ToByte(components[i]);
        }
    }

    private sealed class DeviceCmykColorSpace : PdfColorSpace
    {
        public override string Name => "DeviceCMYK";
        public override int NumberOfComponents => 4;

        /// <summary>DeviceCMYK initial color is black: [0 0 0 1] (ISO 32000-2 8.6.4.4).</summary>
        public override double[] InitialComponents => [0.0, 0.0, 0.0, 1.0];

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f) =>
            PdfColor.FromCmyk(
                (float)Component(components, 0),
                (float)Component(components, 1),
                (float)Component(components, 2),
                (float)Component(components, 3),
                alpha);

        public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
        {
            int pixels = Math.Min(components.Length / 4, rgb.Length / 3);
            for (int p = 0; p < pixels; p++)
            {
                double k = 1.0 - Math.Clamp(components[4 * p + 3], 0, 1);
                rgb[3 * p] = ToByte((1.0 - Math.Clamp(components[4 * p], 0, 1)) * k);
                rgb[3 * p + 1] = ToByte((1.0 - Math.Clamp(components[4 * p + 1], 0, 1)) * k);
                rgb[3 * p + 2] = ToByte((1.0 - Math.Clamp(components[4 * p + 2], 0, 1)) * k);
            }
        }
    }

    // ------------------------------------------------------------------ Indexed

    /// <summary>Indexed color space (ISO 32000-2 8.6.6.3). The palette is converted to RGB once.</summary>
    public sealed class IndexedColorSpace : PdfColorSpace
    {
        private readonly PdfColorSpace _baseColorSpace;
        private readonly int _hival;
        private readonly byte[] _lookupTable;
        private readonly byte[] _paletteRgb;
        private readonly PdfColor[] _palette;

        public override string Name => "Indexed";
        public override int NumberOfComponents => 1;

        /// <summary>The base color space the lookup table is expressed in.</summary>
        public PdfColorSpace BaseColorSpace => _baseColorSpace;

        /// <summary>Maximum valid index (0..255).</summary>
        public int HiVal => _hival;

        /// <summary>Raw lookup table bytes ((hival + 1) x base components).</summary>
        public ReadOnlyMemory<byte> LookupTable => _lookupTable;

        public IndexedColorSpace(PdfColorSpace baseColorSpace, int hival, byte[] lookupTable)
        {
            _baseColorSpace = baseColorSpace ?? throw new ArgumentNullException(nameof(baseColorSpace));
            _hival = Math.Clamp(hival, 0, 255);
            _lookupTable = lookupTable ?? Array.Empty<byte>();

            int baseCount = Math.Max(0, baseColorSpace.NumberOfComponents);
            _palette = new PdfColor[_hival + 1];
            _paletteRgb = new byte[(_hival + 1) * 3];
            Span<double> baseComps = stackalloc double[Math.Max(1, baseCount)];
            for (int index = 0; index <= _hival; index++)
            {
                int offset = index * baseCount;
                if (baseCount == 0 || offset + baseCount > _lookupTable.Length)
                {
                    _palette[index] = PdfColor.Black; // Short lookup table: missing entries render black.
                    continue;
                }

                // Lookup bytes map linearly onto the base component ranges (8.6.6.3).
                for (int c = 0; c < baseCount; c++)
                {
                    var (min, max) = baseColorSpace.GetComponentRange(c);
                    baseComps[c] = min + _lookupTable[offset + c] / 255.0 * (max - min);
                }
                var color = baseColorSpace.ToRgbColor(baseComps[..baseCount]);
                _palette[index] = color;
                _paletteRgb[3 * index] = ToByte(color.R);
                _paletteRgb[3 * index + 1] = ToByte(color.G);
                _paletteRgb[3 * index + 2] = ToByte(color.B);
            }
        }

        public override (double Min, double Max) GetComponentRange(int i) => (0.0, _hival);

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            var color = _palette[IndexOf(Component(components, 0))];
            return new PdfColor(color.R, color.G, color.B, Math.Clamp(alpha, 0f, 1f));
        }

        public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
        {
            int pixels = Math.Min(components.Length, rgb.Length / 3);
            for (int p = 0; p < pixels; p++)
            {
                int index = IndexOf(components[p]) * 3;
                rgb[3 * p] = _paletteRgb[index];
                rgb[3 * p + 1] = _paletteRgb[index + 1];
                rgb[3 * p + 2] = _paletteRgb[index + 2];
            }
        }

        private int IndexOf(double value) =>
            double.IsNaN(value) ? 0 : (int)Math.Clamp(Math.Round(value), 0, _hival);
    }

    // ------------------------------------------------------------------ Separation

    /// <summary>
    /// Separation color space (ISO 32000-2 8.6.6.4): one tint component mapped through the tint transform
    /// into the alternate space. /All is rendered as gray (1 - tint); /None paints nothing.
    /// </summary>
    public sealed class SeparationColorSpace : PdfColorSpace
    {
        private readonly PdfColorSpace _alternateColorSpace;
        private readonly PdfFunction? _tintTransform;
        private readonly PdfFallbackReason? _unsupportedReason;
        private byte[]? _lut;

        public override string Name => "Separation";
        public override int NumberOfComponents => 1;

        /// <summary>Colorant name (e.g. "PANTONE 185 C", "All", "None"); empty when unknown.</summary>
        public string ColorantName { get; }

        /// <summary>The alternate color space.</summary>
        public PdfColorSpace AlternateColorSpace => _alternateColorSpace;

        /// <summary>The tint transform, or null when it could not be parsed.</summary>
        public PdfFunction? TintTransform => _tintTransform;

        public override bool IsNone => ColorantName == "None";
        public override PdfFallbackReason? UnsupportedReason => _unsupportedReason;

        /// <summary>Tint 1.0 (full colorant) is the initial value (8.6.6.4).</summary>
        public override double[] InitialComponents => [1.0];

        /// <summary>Legacy constructor without a tint transform: renders as gray (1 - tint).</summary>
        public SeparationColorSpace(PdfColorSpace alternateColorSpace)
            : this(string.Empty, alternateColorSpace, null, null)
        {
        }

        public SeparationColorSpace(string colorantName, PdfColorSpace alternateColorSpace, PdfFunction? tintTransform, PdfFallbackReason? unsupportedReason = null)
        {
            ColorantName = colorantName ?? string.Empty;
            _alternateColorSpace = alternateColorSpace ?? throw new ArgumentNullException(nameof(alternateColorSpace));
            _tintTransform = tintTransform;
            // /All and /None do not need the tint transform to render.
            _unsupportedReason = ColorantName is "All" or "None" ? null : unsupportedReason;
        }

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            double tint = Math.Clamp(Component(components, 0, 1.0), 0.0, 1.0);
            if (IsNone)
                return PdfColor.Transparent;

            if (ColorantName != "All" && _tintTransform != null
                && TryTint(_tintTransform, _alternateColorSpace, [tint], alpha, out var color))
            {
                return color;
            }

            return PdfColor.FromGray((float)(1.0 - tint), alpha);
        }

        public override void ToRgb24(ReadOnlySpan<double> components, Span<byte> rgb)
        {
            // Images repeat the same tints: cache a 256-entry table quantised to 8 bits.
            var lut = _lut ??= BuildLut();
            int pixels = Math.Min(components.Length, rgb.Length / 3);
            for (int p = 0; p < pixels; p++)
            {
                int i = ToByte(components[p]) * 3;
                rgb[3 * p] = lut[i];
                rgb[3 * p + 1] = lut[i + 1];
                rgb[3 * p + 2] = lut[i + 2];
            }
        }

        private byte[] BuildLut()
        {
            var lut = new byte[256 * 3];
            for (int i = 0; i < 256; i++)
            {
                var c = ToRgbColor([i / 255.0]);
                lut[3 * i] = ToByte(c.R);
                lut[3 * i + 1] = ToByte(c.G);
                lut[3 * i + 2] = ToByte(c.B);
            }
            return lut;
        }
    }

    /// <summary>
    /// Evaluates a tint transform into the alternate space. Runtime failures of a Type 4 program fall back
    /// (return false) instead of aborting rendering; the transform already passed parse-time probing.
    /// </summary>
    private static bool TryTint(PdfFunction tint, PdfColorSpace alternate, ReadOnlySpan<double> input, float alpha, out PdfColor color)
    {
        Span<double> output = stackalloc double[Math.Max(1, tint.OutputCount)];
        try
        {
            tint.Evaluate(input, output);
        }
        catch (PdfVectorException)
        {
            color = default;
            return false;
        }
        color = alternate.ToRgbColor(output[..tint.OutputCount], alpha);
        return true;
    }
}
