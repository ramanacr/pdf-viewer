using System;
using System.Collections.Generic;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Color;

/// <summary>
/// Color space management conforming to ISO 32000-2 Clause 8.6.
/// Supports DeviceGray, DeviceRGB, DeviceCMYK, Indexed, and Separation color spaces.
/// </summary>
public abstract class PdfColorSpace
{
    public abstract string Name { get; }
    public abstract int NumberOfComponents { get; }
    public abstract PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f);

    public static readonly PdfColorSpace DeviceGray = new DeviceGrayColorSpace();
    public static readonly PdfColorSpace DeviceRgb = new DeviceRgbColorSpace();
    public static readonly PdfColorSpace DeviceCmyk = new DeviceCmykColorSpace();

    public static PdfColorSpace Resolve(
        PdfObject? csObj,
        PdfObjectResolver resolver,
        PdfDictionary? pageResources = null)
    {
        var resolved = resolver.Resolve(csObj);

        if (resolved is PdfName name)
        {
            return name.Value switch
            {
                "DeviceGray" or "G" => DeviceGray,
                "DeviceRGB" or "RGB" => DeviceRgb,
                "DeviceCMYK" or "CMYK" => DeviceCmyk,
                _ => ResolveFromResources(name.Value, pageResources, resolver)
            };
        }

        if (resolved is PdfArray arr && arr.Count > 0)
        {
            string family = arr[0].TryGetName(out string fam) ? fam : string.Empty;

            if (family == "Indexed" && arr.Count >= 4)
            {
                var baseCs = Resolve(arr[1], resolver, pageResources);
                int hival = (int)(arr[2].TryGetInteger(out long hi) ? hi : 255);
                var lookupObj = resolver.Resolve(arr[3]);
                byte[] lookupTable;

                if (lookupObj is PdfString str)
                {
                    lookupTable = str.RawBytes.ToArray();
                }
                else if (lookupObj is PdfStream stm)
                {
                    var decoder = new PdfStreamDecoder();
                    lookupTable = decoder.DecodeStream(stm);
                }
                else
                {
                    lookupTable = Array.Empty<byte>();
                }

                return new IndexedColorSpace(baseCs, hival, lookupTable);
            }

            if (family == "Separation" && arr.Count >= 4)
            {
                var altCs = Resolve(arr[2], resolver, pageResources);
                return new SeparationColorSpace(altCs);
            }
        }

        return DeviceRgb;
    }

    private static PdfColorSpace ResolveFromResources(
        string name,
        PdfDictionary? resources,
        PdfObjectResolver resolver)
    {
        if (resources != null && resources.TryGetValue("ColorSpace", out var csDictObj))
        {
            var csDict = resolver.Resolve(csDictObj) as PdfDictionary;
            if (csDict != null && csDict.TryGetValue(name, out var refObj))
            {
                return Resolve(refObj, resolver, resources);
            }
        }
        return DeviceRgb;
    }

    private sealed class DeviceGrayColorSpace : PdfColorSpace
    {
        public override string Name => "DeviceGray";
        public override int NumberOfComponents => 1;

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            float g = components.Length > 0 ? (float)components[0] : 0f;
            return PdfColor.FromGray(g, alpha);
        }
    }

    private sealed class DeviceRgbColorSpace : PdfColorSpace
    {
        public override string Name => "DeviceRGB";
        public override int NumberOfComponents => 3;

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            float r = components.Length > 0 ? (float)components[0] : 0f;
            float g = components.Length > 1 ? (float)components[1] : 0f;
            float b = components.Length > 2 ? (float)components[2] : 0f;
            return PdfColor.FromRgb(r, g, b, alpha);
        }
    }

    private sealed class DeviceCmykColorSpace : PdfColorSpace
    {
        public override string Name => "DeviceCMYK";
        public override int NumberOfComponents => 4;

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            float c = components.Length > 0 ? (float)components[0] : 0f;
            float m = components.Length > 1 ? (float)components[1] : 0f;
            float y = components.Length > 2 ? (float)components[2] : 0f;
            float k = components.Length > 3 ? (float)components[3] : 0f;
            return PdfColor.FromCmyk(c, m, y, k, alpha);
        }
    }

    public sealed class IndexedColorSpace : PdfColorSpace
    {
        private readonly PdfColorSpace _baseColorSpace;
        private readonly int _hival;
        private readonly byte[] _lookupTable;

        public override string Name => "Indexed";
        public override int NumberOfComponents => 1;

        public IndexedColorSpace(PdfColorSpace baseColorSpace, int hival, byte[] lookupTable)
        {
            _baseColorSpace = baseColorSpace;
            _hival = hival;
            _lookupTable = lookupTable;
        }

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            int index = components.Length > 0 ? (int)Math.Clamp(Math.Round(components[0]), 0, _hival) : 0;
            int baseCompCount = _baseColorSpace.NumberOfComponents;
            int offset = index * baseCompCount;

            if (offset + baseCompCount <= _lookupTable.Length)
            {
                Span<double> baseComps = stackalloc double[baseCompCount];
                for (int i = 0; i < baseCompCount; i++)
                {
                    baseComps[i] = _lookupTable[offset + i] / 255.0;
                }
                return _baseColorSpace.ToRgbColor(baseComps, alpha);
            }

            return PdfColor.Black;
        }
    }

    public sealed class SeparationColorSpace : PdfColorSpace
    {
        private readonly PdfColorSpace _alternateColorSpace;
        public override string Name => "Separation";
        public override int NumberOfComponents => 1;

        public SeparationColorSpace(PdfColorSpace alternateColorSpace)
        {
            _alternateColorSpace = alternateColorSpace;
        }

        public override PdfColor ToRgbColor(ReadOnlySpan<double> components, float alpha = 1.0f)
        {
            double tint = components.Length > 0 ? components[0] : 1.0;
            // Simple subtractive tint mapping
            float v = (float)(1.0 - tint);
            return PdfColor.FromGray(v, alpha);
        }
    }
}
