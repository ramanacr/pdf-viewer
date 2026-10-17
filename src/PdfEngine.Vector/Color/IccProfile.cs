using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PdfEngine.Vector.Color;

/// <summary>Rendering intents (ISO 32000-2 8.6.5.8), in the order of the ICC A2B0/A2B1/A2B2 tags they select.</summary>
public enum PdfRenderingIntent
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3,
}

/// <summary>
/// A parsed ICC profile (ICC.1:2004-10 / ICC.1:2010, versions 2 and 4) reduced to what is needed to convert
/// device colours to the profile connection space: the gray TRC, the RGB matrix/TRC model, and the
/// A2B0..A2B2 lookup pipelines (lut8, lut16 and lutAtoB). Parsing is defensive: any structural problem
/// makes <see cref="TryParse"/> fail so the caller falls back to the alternate space.
/// </summary>
internal sealed class IccProfile
{
    /// <summary>Profiles larger than this are not parsed (real embedded profiles stay well below it).</summary>
    public const int MaxProfileBytes = 32 << 20;

    private const int MaxTags = 1024;

    /// <summary>Ceiling on grid points times output channels of one CLUT, which bounds the allocation.</summary>
    internal const long MaxClutValues = 1 << 22;

    private const int MaxCurveEntries = 1 << 16;

    public const uint SigGray = 0x47524159; // 'GRAY'
    public const uint SigRgb = 0x52474220;  // 'RGB '
    public const uint SigCmyk = 0x434D594B; // 'CMYK'
    public const uint SigLab = 0x4C616220;  // 'Lab '
    public const uint SigXyz = 0x58595A20;  // 'XYZ '

    /// <summary>Major version byte of the header (2 or 4 in practice).</summary>
    public int Version { get; private init; }
    public uint DeviceClass { get; private init; }
    public uint DataSpace { get; private init; }
    public uint Pcs { get; private init; }

    /// <summary>Number of device channels implied by the data colour space.</summary>
    public int Channels { get; private init; }

    internal IccCurve? GrayTrc { get; private init; }
    internal IccCurve[]? RgbTrc { get; private init; }

    /// <summary>Row-major 3x3 matrix from linear RGB to PCS XYZ (columns are rXYZ, gXYZ, bXYZ).</summary>
    internal double[]? RgbMatrix { get; private init; }

    /// <summary>A2B0, A2B1 and A2B2 pipelines; entries are null when the tag is absent.</summary>
    internal IccPipeline?[] AToB { get; private init; } = new IccPipeline?[3];

    public bool IsPcsLab => Pcs == SigLab;

    /// <summary>Parses <paramref name="data"/>; null with a reason when the profile is unusable.</summary>
    public static IccProfile? TryParse(ReadOnlySpan<byte> data, out string? error)
    {
        try
        {
            var profile = Parse(data);
            error = null;
            return profile;
        }
        catch (IccFormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static IccProfile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132 || data.Length > MaxProfileBytes)
            throw new IccFormatException("Profile is too short or too large.");
        // Some writers get the size field wrong: a size beyond the data is ignored, a smaller one trims it.
        uint declared = U32(data, 0);
        if (declared < 132)
            throw new IccFormatException("Profile size field is invalid.");
        if (declared < data.Length)
            data = data[..(int)declared];
        if (U32(data, 36) != 0x61637370) // 'acsp'
            throw new IccFormatException("Profile signature 'acsp' is missing.");

        int version = data[8];
        uint deviceClass = U32(data, 12);
        uint dataSpace = U32(data, 16);
        uint pcs = U32(data, 20);
        if (pcs != SigXyz && pcs != SigLab)
            throw new IccFormatException("Profile connection space is neither XYZ nor Lab.");
        // Device links, abstract and named colour profiles do not describe a device space.
        if (deviceClass is 0x6C696E6B or 0x61627374 or 0x6E6D636C) // 'link', 'abst', 'nmcl'
            throw new IccFormatException("Profile class cannot be used as a colour space.");
        int channels = ChannelsOf(dataSpace);
        if (channels == 0)
            throw new IccFormatException("Profile data colour space is not supported.");

        int tagCount = (int)Math.Min(U32(data, 128), int.MaxValue);
        if (tagCount > MaxTags || 132L + 12L * tagCount > data.Length)
            throw new IccFormatException("Tag table is invalid.");

        var tags = new Dictionary<uint, (int Offset, int Size)>();
        for (int i = 0; i < tagCount; i++)
        {
            int at = 132 + 12 * i;
            uint sig = U32(data, at);
            long offset = U32(data, at + 4), size = U32(data, at + 8);
            if (offset < 128 || size < 8 || offset + size > data.Length)
                continue; // a broken tag is ignored; a needed one then fails below
            tags.TryAdd(sig, ((int)offset, (int)size));
        }

        var pipelines = new Dictionary<int, IccPipeline?>();
        var aToB = new IccPipeline?[3];
        for (int intent = 0; intent < 3; intent++)
        {
            if (!tags.TryGetValue(0x41324230u + (uint)intent, out var tag)) // 'A2B0' + intent
                continue;
            if (!pipelines.TryGetValue(tag.Offset, out var pipeline))
            {
                pipeline = IccPipeline.Parse(data.Slice(tag.Offset, tag.Size), channels, pcs == SigLab);
                pipelines[tag.Offset] = pipeline;
            }
            if (pipeline != null && pipeline.Inputs == channels && pipeline.Outputs == 3)
                aToB[intent] = pipeline;
        }

        IccCurve? grayTrc = null;
        IccCurve[]? rgbTrc = null;
        double[]? matrix = null;
        if (channels == 1 && tags.TryGetValue(0x6B545243, out var kTrc)) // 'kTRC'
            grayTrc = IccCurve.Parse(data.Slice(kTrc.Offset, kTrc.Size), out _);
        if (channels == 3 && dataSpace == SigRgb
            && tags.TryGetValue(0x7258595A, out var rx) && tags.TryGetValue(0x6758595A, out var gx) && tags.TryGetValue(0x6258595A, out var bx)
            && tags.TryGetValue(0x72545243, out var rt) && tags.TryGetValue(0x67545243, out var gt) && tags.TryGetValue(0x62545243, out var bt))
        {
            var r = ReadXyz(data.Slice(rx.Offset, rx.Size));
            var g = ReadXyz(data.Slice(gx.Offset, gx.Size));
            var b = ReadXyz(data.Slice(bx.Offset, bx.Size));
            var trc = new[]
            {
                IccCurve.Parse(data.Slice(rt.Offset, rt.Size), out _),
                IccCurve.Parse(data.Slice(gt.Offset, gt.Size), out _),
                IccCurve.Parse(data.Slice(bt.Offset, bt.Size), out _),
            };
            if (r != null && g != null && b != null && Array.TrueForAll(trc, c => c != null))
            {
                matrix = [r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2]];
                rgbTrc = trc!;
            }
        }

        if (aToB[0] == null && aToB[1] == null && aToB[2] == null && grayTrc == null && matrix == null)
            throw new IccFormatException("Profile has no usable device-to-PCS transform.");

        return new IccProfile
        {
            Version = version,
            DeviceClass = deviceClass,
            DataSpace = dataSpace,
            Pcs = pcs,
            Channels = channels,
            GrayTrc = grayTrc,
            RgbTrc = rgbTrc,
            RgbMatrix = matrix,
            AToB = aToB,
        };
    }

    /// <summary>Device channels of an ICC colour space signature; 0 when it is not a device space handled here.</summary>
    internal static int ChannelsOf(uint space) => space switch
    {
        SigGray => 1,
        SigRgb or 0x434D5920 /* 'CMY ' */ => 3,
        SigCmyk => 4,
        // 'nCLR' spaces: 2CLR .. FCLR.
        >= 0x32434C52 and <= 0x39434C52 when (space & 0x00FFFFFF) == 0x00434C52 => (int)((space >> 24) - '0'),
        >= 0x41434C52 and <= 0x46434C52 when (space & 0x00FFFFFF) == 0x00434C52 => (int)((space >> 24) - 'A' + 10),
        _ => 0,
    };

    private static double[]? ReadXyz(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 20 || U32(tag, 0) != SigXyz) return null;
        var v = new[] { S15F16(tag, 8), S15F16(tag, 12), S15F16(tag, 16) };
        return Array.TrueForAll(v, double.IsFinite) ? v : null;
    }

    internal static uint U32(ReadOnlySpan<byte> d, int at) =>
        at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32BigEndian(d[at..]) : throw new IccFormatException("Read past the end of a tag.");

    internal static ushort U16(ReadOnlySpan<byte> d, int at) =>
        at >= 0 && at + 2 <= d.Length ? BinaryPrimitives.ReadUInt16BigEndian(d[at..]) : throw new IccFormatException("Read past the end of a tag.");

    internal static double S15F16(ReadOnlySpan<byte> d, int at) => (int)U32(d, at) / 65536.0;

    internal static int MaxCurve => MaxCurveEntries;
}

/// <summary>A structural error in an ICC profile.</summary>
internal sealed class IccFormatException(string message) : Exception(message);

/// <summary>A one-dimensional ICC curve: 'curv' (identity, gamma or sampled table) or 'para' (parametric).</summary>
internal sealed class IccCurve
{
    private readonly float[]? _table;
    private readonly double _gamma = 1.0;
    private readonly int _function = -1;
    private readonly double[] _p = [];

    private IccCurve(float[]? table, double gamma, int function, double[] parameters)
    {
        _table = table;
        _gamma = gamma;
        _function = function;
        _p = parameters;
    }

    public static readonly IccCurve Identity = new(null, 1.0, -1, []);

    public bool IsIdentity => _table == null && _function < 0 && _gamma == 1.0;

    /// <summary>A sampled curve from normalised table entries.</summary>
    public static IccCurve FromTable(float[] table) => new(table, 1.0, -1, []);

    /// <summary>Parses a curve element at the start of <paramref name="d"/>; <paramref name="length"/> is its byte size.</summary>
    public static IccCurve? Parse(ReadOnlySpan<byte> d, out int length)
    {
        length = 0;
        if (d.Length < 12) return null;
        uint type = IccProfile.U32(d, 0);
        if (type == 0x63757276) // 'curv'
        {
            long count = IccProfile.U32(d, 8);
            if (count > IccProfile.MaxCurve || 12 + 2 * count > d.Length) return null;
            length = (int)(12 + 2 * count);
            if (count == 0) return Identity;
            if (count == 1)
            {
                double gamma = IccProfile.U16(d, 12) / 256.0;
                return gamma > 0 ? new IccCurve(null, gamma, -1, []) : null;
            }
            var table = new float[count];
            for (int i = 0; i < count; i++)
                table[i] = IccProfile.U16(d, 12 + 2 * i) / 65535f;
            return new IccCurve(table, 1.0, -1, []);
        }
        if (type == 0x70617261) // 'para'
        {
            int function = IccProfile.U16(d, 8);
            int count = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
            if (count < 0 || 12 + 4 * count > d.Length) return null;
            length = 12 + 4 * count;
            var p = new double[7];
            for (int i = 0; i < count; i++)
            {
                p[i] = IccProfile.S15F16(d, 12 + 4 * i);
                if (!double.IsFinite(p[i])) return null;
            }
            return new IccCurve(null, 1.0, function, p);
        }
        return null;
    }

    /// <summary>Evaluates the curve at <paramref name="x"/> (clamped to 0..1); the result is clamped to 0..1.</summary>
    public double Evaluate(double x)
    {
        if (!(x > 0)) x = 0;
        else if (x > 1) x = 1;
        double y;
        if (_table != null)
        {
            double pos = x * (_table.Length - 1);
            int i = (int)pos;
            if (i >= _table.Length - 1) return _table[^1];
            double f = pos - i;
            y = _table[i] + (_table[i + 1] - _table[i]) * f;
        }
        else if (_function >= 0)
        {
            y = Parametric(x);
        }
        else
        {
            y = _gamma == 1.0 ? x : Math.Pow(x, _gamma);
        }
        return double.IsNaN(y) ? 0 : Math.Clamp(y, 0, 1);
    }

    private double Parametric(double x)
    {
        double g = _p[0], a = _p[1], b = _p[2], c = _p[3], d = _p[4], e = _p[5], f = _p[6];
        static double Pow(double v, double g) => v <= 0 ? 0 : Math.Pow(v, g);
        switch (_function)
        {
            case 0:
                return Pow(x, g);
            case 1:
                return a != 0 && x >= -b / a ? Pow(a * x + b, g) : 0;
            case 2:
                return a != 0 && x >= -b / a ? Pow(a * x + b, g) + c : c;
            case 3:
                return x >= d ? Pow(a * x + b, g) : c * x;
            default:
                return x >= d ? Pow(a * x + b, g) + e : c * x + f;
        }
    }
}

/// <summary>How a pipeline's normalised outputs encode PCS values.</summary>
internal enum IccPcsEncoding
{
    /// <summary>XYZ as u1Fixed15: 1.0 is 0x8000.</summary>
    Xyz,
    /// <summary>Lab as in lut16Type (ICC v2 legacy): L 100 is 0xFF00, a/b 0 is 0x8000.</summary>
    LabLegacy16,
    /// <summary>Lab as in lut8Type and the v4 lutAtoBType: L 0..100 and a/b -128..127 span the full range.</summary>
    LabV4,
}

/// <summary>
/// A device-to-PCS lookup pipeline in the lutAtoBType order: A curves, CLUT, M curves, matrix, B curves.
/// lut8Type and lut16Type map onto it as input tables (A), CLUT and output tables (B); their matrix is
/// only defined for XYZ input and is therefore not used for device data.
/// </summary>
internal sealed class IccPipeline
{
    public int Inputs { get; private init; }
    public int Outputs { get; private init; }
    private IccCurve[]? _a;
    private IccClut? _clut;
    private IccCurve[]? _m;
    private double[]? _matrix; // 3x3 row-major then three offsets
    private IccCurve[]? _b;
    public IccPcsEncoding Encoding { get; private init; }

    public static IccPipeline? Parse(ReadOnlySpan<byte> d, int channels, bool labPcs)
    {
        if (d.Length < 12) return null;
        uint type = IccProfile.U32(d, 0);
        return type switch
        {
            0x6D667431 => ParseLut(d, labPcs, eightBit: true),   // 'mft1'
            0x6D667432 => ParseLut(d, labPcs, eightBit: false),  // 'mft2'
            0x6D414220 => ParseAToB(d, labPcs),                  // 'mAB '
            _ => null,
        };
    }

    private static IccPipeline? ParseLut(ReadOnlySpan<byte> d, bool labPcs, bool eightBit)
    {
        if (d.Length < 48) return null;
        int inputs = d[8], outputs = d[9], grid = d[10];
        if (inputs is < 1 or > 8 || outputs != 3 || grid < 2) return null;
        int inEntries = 256, outEntries = 256, at = 48;
        if (!eightBit)
        {
            if (d.Length < 52) return null;
            inEntries = IccProfile.U16(d, 48);
            outEntries = IccProfile.U16(d, 50);
            at = 52;
            if (inEntries is < 2 or > 4096 || outEntries is < 2 or > 4096) return null;
        }
        int bytes = eightBit ? 1 : 2;
        float Sample(ReadOnlySpan<byte> s, int i) => eightBit ? s[i] / 255f : IccProfile.U16(s, i) / 65535f;

        var a = new IccCurve[inputs];
        for (int c = 0; c < inputs; c++)
        {
            if (at + (long)inEntries * bytes > d.Length) return null;
            var table = new float[inEntries];
            for (int i = 0; i < inEntries; i++) table[i] = Sample(d, at + i * bytes);
            a[c] = IccCurveTable(table);
            at += inEntries * bytes;
        }

        var dims = new int[inputs];
        Array.Fill(dims, grid);
        var clut = IccClut.Read(d, at, dims, outputs, bytes, out int clutBytes);
        if (clut == null) return null;
        at += clutBytes;

        var b = new IccCurve[outputs];
        for (int c = 0; c < outputs; c++)
        {
            if (at + (long)outEntries * bytes > d.Length) return null;
            var table = new float[outEntries];
            for (int i = 0; i < outEntries; i++) table[i] = Sample(d, at + i * bytes);
            b[c] = IccCurveTable(table);
            at += outEntries * bytes;
        }

        return new IccPipeline
        {
            Inputs = inputs,
            Outputs = outputs,
            _a = a,
            _clut = clut,
            _b = b,
            Encoding = !labPcs ? IccPcsEncoding.Xyz : eightBit ? IccPcsEncoding.LabV4 : IccPcsEncoding.LabLegacy16,
        };
    }

    private static IccCurve IccCurveTable(float[] table)
    {
        // Linear tables are common (lut16 profiles often have identity input/output tables); skip them.
        bool identity = true;
        for (int i = 0; i < table.Length && identity; i++)
            identity = Math.Abs(table[i] - i / (float)(table.Length - 1)) < 1e-6f;
        return identity ? IccCurve.Identity : IccCurve.FromTable(table);
    }

    private static IccPipeline? ParseAToB(ReadOnlySpan<byte> d, bool labPcs)
    {
        if (d.Length < 32) return null;
        int inputs = d[8], outputs = d[9];
        if (inputs is < 1 or > 8 || outputs != 3) return null;
        long offB = IccProfile.U32(d, 12), offMatrix = IccProfile.U32(d, 16), offM = IccProfile.U32(d, 20),
             offClut = IccProfile.U32(d, 24), offA = IccProfile.U32(d, 28);
        foreach (long o in (ReadOnlySpan<long>)[offB, offMatrix, offM, offClut, offA])
            if (o != 0 && (o < 32 || o >= d.Length)) return null;
        if (offB == 0) return null; // B curves are required

        var b = ReadCurves(d, offB, outputs);
        if (b == null) return null;
        IccCurve[]? a = null, m = null;
        IccClut? clut = null;
        double[]? matrix = null;
        if (offA != 0 && (a = ReadCurves(d, offA, inputs)) == null) return null;
        if (offM != 0 && (m = ReadCurves(d, offM, outputs)) == null) return null;
        if (offClut != 0)
        {
            int at = (int)offClut;
            if (at + 20 > d.Length) return null;
            var dims = new int[inputs];
            for (int i = 0; i < inputs; i++)
            {
                dims[i] = d[at + i];
                if (dims[i] < 2) return null;
            }
            int precision = d[at + 16];
            if (precision is not (1 or 2)) return null;
            clut = IccClut.Read(d, at + 20, dims, outputs, precision, out _);
            if (clut == null) return null;
        }
        else if (inputs != outputs)
        {
            return null; // without a CLUT the channel count cannot change
        }
        if (offMatrix != 0)
        {
            if (offMatrix + 48 > d.Length) return null;
            matrix = new double[12];
            for (int i = 0; i < 12; i++)
            {
                matrix[i] = IccProfile.S15F16(d, (int)offMatrix + 4 * i);
                if (!double.IsFinite(matrix[i])) return null;
            }
        }

        return new IccPipeline
        {
            Inputs = inputs,
            Outputs = outputs,
            _a = a,
            _clut = clut,
            _m = m,
            _matrix = matrix,
            _b = b,
            Encoding = labPcs ? IccPcsEncoding.LabV4 : IccPcsEncoding.Xyz,
        };
    }

    private static IccCurve[]? ReadCurves(ReadOnlySpan<byte> d, long offset, int count)
    {
        var curves = new IccCurve[count];
        int at = (int)offset;
        for (int i = 0; i < count; i++)
        {
            if (at >= d.Length) return null;
            var curve = IccCurve.Parse(d[at..], out int length);
            if (curve == null) return null;
            curves[i] = curve;
            at += (length + 3) & ~3; // elements are 4-byte aligned
        }
        return curves;
    }

    /// <summary>
    /// Evaluates the pipeline for device values in 0..1 and writes the PCS colour: L*, a*, b* for a Lab PCS
    /// or X, Y, Z (D50-relative) for an XYZ PCS.
    /// </summary>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> pcs)
    {
        Span<double> v = stackalloc double[Math.Max(Inputs, 3)];
        for (int i = 0; i < Inputs; i++)
        {
            double x = i < input.Length ? input[i] : 0;
            v[i] = _a != null ? _a[i].Evaluate(x) : Math.Clamp(double.IsNaN(x) ? 0 : x, 0, 1);
        }

        Span<double> w = stackalloc double[3];
        if (_clut != null)
            _clut.Evaluate(v[..Inputs], w);
        else
            v[..3].CopyTo(w);

        if (_m != null)
            for (int i = 0; i < 3; i++) w[i] = _m[i].Evaluate(w[i]);
        if (_matrix != null)
        {
            double x = w[0], y = w[1], z = w[2];
            w[0] = Math.Clamp(_matrix[0] * x + _matrix[1] * y + _matrix[2] * z + _matrix[9], 0, 1);
            w[1] = Math.Clamp(_matrix[3] * x + _matrix[4] * y + _matrix[5] * z + _matrix[10], 0, 1);
            w[2] = Math.Clamp(_matrix[6] * x + _matrix[7] * y + _matrix[8] * z + _matrix[11], 0, 1);
        }
        for (int i = 0; i < 3; i++)
            w[i] = _b![i].Evaluate(w[i]);

        switch (Encoding)
        {
            case IccPcsEncoding.Xyz:
                const double xyzScale = 65535.0 / 32768.0;
                pcs[0] = w[0] * xyzScale;
                pcs[1] = w[1] * xyzScale;
                pcs[2] = w[2] * xyzScale;
                break;
            case IccPcsEncoding.LabLegacy16:
                pcs[0] = w[0] * (65535.0 / 65280.0) * 100.0;
                pcs[1] = w[1] * (65535.0 / 256.0) - 128.0;
                pcs[2] = w[2] * (65535.0 / 256.0) - 128.0;
                break;
            default:
                pcs[0] = w[0] * 100.0;
                pcs[1] = w[1] * 255.0 - 128.0;
                pcs[2] = w[2] * 255.0 - 128.0;
                break;
        }
    }
}

/// <summary>
/// A colour lookup table with per-dimension grid sizes (the first input varies slowest) and normalised
/// outputs, interpolated as lcms2 does: tetrahedrally over the last three inputs, linearly over the others.
/// </summary>
internal sealed class IccClut
{
    private readonly int[] _dims;
    private readonly int[] _strides; // in values, per input dimension
    private readonly int _outputs;
    private readonly float[] _values;

    private IccClut(int[] dims, int outputs, float[] values)
    {
        _dims = dims;
        _outputs = outputs;
        _values = values;
        _strides = new int[dims.Length];
        int stride = outputs;
        for (int i = dims.Length - 1; i >= 0; i--)
        {
            _strides[i] = stride;
            stride *= dims[i];
        }
    }

    public static IccClut? Read(ReadOnlySpan<byte> d, int at, int[] dims, int outputs, int bytesPerValue, out int length)
    {
        length = 0;
        long count = outputs;
        foreach (int g in dims)
        {
            count *= g;
            if (count > IccProfile.MaxClutValues) return null;
        }
        if (at < 0 || at + count * bytesPerValue > d.Length) return null;
        var values = new float[count];
        for (int i = 0; i < count; i++)
            values[i] = bytesPerValue == 1 ? d[at + i] / 255f : IccProfile.U16(d, at + 2 * i) / 65535f;
        length = (int)(count * bytesPerValue);
        return new IccClut(dims, outputs, values);
    }

    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        int n = _dims.Length;
        Span<int> index = stackalloc int[n];
        Span<double> frac = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            double pos = Math.Clamp(double.IsNaN(input[i]) ? 0 : input[i], 0, 1) * (_dims[i] - 1);
            int k = (int)pos;
            if (k >= _dims[i] - 1) k = _dims[i] - 2;
            index[i] = k;
            frac[i] = pos - k;
        }
        Interpolate(0, 0, index, frac, output);
    }

    /// <summary>
    /// Interpolates dimensions <paramref name="dim"/>.. at <paramref name="offset"/>, the way lcms2 does (and
    /// so PDFium): the last three inputs tetrahedrally, each earlier input linearly between the results of
    /// its two neighbouring grid planes.
    /// </summary>
    private void Interpolate(int dim, int offset, ReadOnlySpan<int> index, ReadOnlySpan<double> frac, Span<double> output)
    {
        int left = _dims.Length - dim;
        if (left == 3)
        {
            Tetrahedral(dim, offset + index[dim] * _strides[dim] + index[dim + 1] * _strides[dim + 1] + index[dim + 2] * _strides[dim + 2],
                frac[dim], frac[dim + 1], frac[dim + 2], output);
            return;
        }
        int at = offset + index[dim] * _strides[dim];
        if (left == 1)
        {
            double f = frac[dim];
            for (int o = 0; o < _outputs; o++)
                output[o] = _values[at + o] + (_values[at + _strides[dim] + o] - _values[at + o]) * f;
            return;
        }
        Span<double> high = stackalloc double[_outputs];
        Interpolate(dim + 1, at, index, frac, output);
        if (frac[dim] == 0) return;
        Interpolate(dim + 1, at + _strides[dim], index, frac, high);
        for (int o = 0; o < _outputs; o++)
            output[o] += (high[o] - output[o]) * frac[dim];
    }

    private void Tetrahedral(int dim, int b, double rx, double ry, double rz, Span<double> output)
    {
        int x1 = _strides[dim], y1 = _strides[dim + 1], z1 = _strides[dim + 2];
        var v = _values;
        for (int o = 0; o < _outputs; o++)
        {
            int p = b + o;
            double c0 = v[p], c1, c2, c3;
            if (rx >= ry && ry >= rz)
            {
                c1 = v[p + x1] - c0;
                c2 = v[p + x1 + y1] - v[p + x1];
                c3 = v[p + x1 + y1 + z1] - v[p + x1 + y1];
            }
            else if (rx >= rz && rz >= ry)
            {
                c1 = v[p + x1] - c0;
                c3 = v[p + x1 + z1] - v[p + x1];
                c2 = v[p + x1 + y1 + z1] - v[p + x1 + z1];
            }
            else if (rz >= rx && rx >= ry)
            {
                c3 = v[p + z1] - c0;
                c1 = v[p + x1 + z1] - v[p + z1];
                c2 = v[p + x1 + y1 + z1] - v[p + x1 + z1];
            }
            else if (ry >= rx && rx >= rz)
            {
                c2 = v[p + y1] - c0;
                c1 = v[p + x1 + y1] - v[p + y1];
                c3 = v[p + x1 + y1 + z1] - v[p + x1 + y1];
            }
            else if (ry >= rz && rz >= rx)
            {
                c2 = v[p + y1] - c0;
                c3 = v[p + y1 + z1] - v[p + y1];
                c1 = v[p + x1 + y1 + z1] - v[p + y1 + z1];
            }
            else
            {
                c3 = v[p + z1] - c0;
                c2 = v[p + y1 + z1] - v[p + z1];
                c1 = v[p + x1 + y1 + z1] - v[p + y1 + z1];
            }
            output[o] = c0 + c1 * rx + c2 * ry + c3 * rz;
        }
    }
}
