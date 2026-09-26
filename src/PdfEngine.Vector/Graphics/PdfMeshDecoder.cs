using System;
using System.Collections.Generic;
using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Diagnostics;

namespace PdfEngine.Vector.Graphics;

/// <summary>
/// Decodes the vertex/patch data of mesh shadings (ISO 32000-2 8.7.4.5.5–8.7.4.5.8): free-form
/// (4) and lattice-form (5) Gouraud triangle meshes, Coons (6) and tensor-product (7) patch meshes,
/// with /BitsPerCoordinate, /BitsPerComponent, /BitsPerFlag and /Decode, including shared-edge
/// flags. Data are byte-aligned per vertex (types 4, 5) and per patch (6, 7), as producers write
/// them. Truncated data ends the mesh at the last complete element (lenient, like other readers).
/// </summary>
internal sealed class PdfMeshDecoder
{
    /// <summary>Upper bounds that keep hostile meshes from exhausting memory.</summary>
    public const int MaxVertices = 3_000_000;
    public const int MaxPatches = 250_000;

    private readonly int _type;
    private readonly int _bitsPerCoordinate, _bitsPerComponent, _bitsPerFlag;
    private readonly double[] _decode;
    private readonly int _valueCount; // colour components, or 1 with a function
    private readonly PdfColorSpace _cs;
    private readonly bool _hasFunction;
    private readonly int _verticesPerRow;

    public PdfMeshDecoder(int type, int bitsPerCoordinate, int bitsPerComponent, int bitsPerFlag, double[] decode,
        PdfColorSpace cs, bool hasFunction, int verticesPerRow)
    {
        _type = type;
        _bitsPerCoordinate = bitsPerCoordinate;
        _bitsPerComponent = bitsPerComponent;
        _bitsPerFlag = bitsPerFlag;
        _decode = decode;
        _cs = cs;
        _hasFunction = hasFunction;
        _valueCount = hasFunction ? 1 : Math.Max(1, cs.NumberOfComponents);
        _verticesPerRow = verticesPerRow;
        if (bitsPerCoordinate is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32) ||
            bitsPerComponent is not (1 or 2 or 4 or 8 or 12 or 16) ||
            (type is 4 or 6 or 7 && bitsPerFlag is not (2 or 4 or 8)))
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.Shading, "Mesh shading has invalid bit widths.");
        if (decode.Length < 4 + 2 * _valueCount)
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.Shading, "Mesh shading /Decode array is too short.");
        if (type == 5 && verticesPerRow < 2)
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.Shading, "Lattice mesh needs /VerticesPerRow ≥ 2.");
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private long _bit;
        public BitReader(byte[] data) => _data = data;
        public bool Has(int bits) => _bit + bits <= (long)_data.Length * 8;
        public uint Read(int bits)
        {
            uint v = 0;
            for (int i = 0; i < bits; i++, _bit++)
                v = (v << 1) | (uint)((_data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1);
            return v;
        }
        public void Align() => _bit = (_bit + 7) & ~7L;
    }

    private static double Map(uint raw, int bits, double min, double max) =>
        min + raw * (max - min) / (bits == 32 ? uint.MaxValue : (double)((1L << bits) - 1));

    private int VertexBits => 2 * _bitsPerCoordinate + _valueCount * _bitsPerComponent;

    private PdfPoint ReadPoint(BitReader r) => new(
        Map(r.Read(_bitsPerCoordinate), _bitsPerCoordinate, _decode[0], _decode[1]),
        Map(r.Read(_bitsPerCoordinate), _bitsPerCoordinate, _decode[2], _decode[3]));

    private (PdfColor Color, double T) ReadValue(BitReader r)
    {
        Span<double> comps = stackalloc double[_valueCount];
        for (int c = 0; c < _valueCount; c++)
            comps[c] = Map(r.Read(_bitsPerComponent), _bitsPerComponent, _decode[4 + 2 * c], _decode[5 + 2 * c]);
        return _hasFunction ? (default, comps[0]) : (_cs.ToRgbColor(comps, 1f), 0);
    }

    private PdfMeshVertex ReadVertex(BitReader r)
    {
        var p = ReadPoint(r);
        var (color, t) = ReadValue(r);
        return new PdfMeshVertex(p.X, p.Y, color, t);
    }

    public (List<PdfMeshVertex> Triangles, List<PdfMeshPatch> Patches) Decode(byte[] data)
    {
        var triangles = new List<PdfMeshVertex>();
        var patches = new List<PdfMeshPatch>();
        var r = new BitReader(data);
        switch (_type)
        {
            case 4: DecodeFreeForm(r, triangles); break;
            case 5: DecodeLattice(r, triangles); break;
            default: DecodePatches(r, patches); break;
        }
        return (triangles, patches);
    }

    private void DecodeFreeForm(BitReader r, List<PdfMeshVertex> tris)
    {
        PdfMeshVertex va = default, vb = default, vc = default;
        bool haveTriangle = false;
        while (r.Has(_bitsPerFlag + VertexBits))
        {
            uint flag = r.Read(_bitsPerFlag);
            if (flag == 0)
            {
                // Three new vertices (the flags of the second and third are ignored).
                var a = ReadVertex(r); r.Align();
                if (!r.Has(_bitsPerFlag + VertexBits)) return;
                r.Read(_bitsPerFlag); var b = ReadVertex(r); r.Align();
                if (!r.Has(_bitsPerFlag + VertexBits)) return;
                r.Read(_bitsPerFlag); var c = ReadVertex(r); r.Align();
                (va, vb, vc) = (a, b, c);
                haveTriangle = true;
            }
            else
            {
                var v = ReadVertex(r); r.Align();
                if (!haveTriangle) continue; // a continuation with nothing to continue: skip
                (va, vb, vc) = flag == 1 ? (vb, vc, v) : (va, vc, v);
            }
            Add(tris, va, vb, vc);
        }
    }

    private void DecodeLattice(BitReader r, List<PdfMeshVertex> tris)
    {
        var previous = new List<PdfMeshVertex>(_verticesPerRow);
        var row = new List<PdfMeshVertex>(_verticesPerRow);
        while (true)
        {
            row.Clear();
            for (int i = 0; i < _verticesPerRow; i++)
            {
                if (!r.Has(VertexBits)) return;
                row.Add(ReadVertex(r));
                r.Align();
            }
            if (previous.Count == _verticesPerRow)
            {
                for (int i = 0; i + 1 < _verticesPerRow; i++)
                {
                    Add(tris, previous[i], previous[i + 1], row[i]);
                    Add(tris, previous[i + 1], row[i + 1], row[i]);
                }
            }
            (previous, row) = (row, previous);
        }
    }

    private static void Add(List<PdfMeshVertex> tris, PdfMeshVertex a, PdfMeshVertex b, PdfMeshVertex c)
    {
        if (tris.Count + 3 > MaxVertices)
            throw new PdfResourceLimitException("MaxMeshVertices", "Mesh shading exceeds the vertex budget.");
        tris.Add(a); tris.Add(b); tris.Add(c);
    }

    // Stream order of the 16 tensor points as indices i * 4 + j (8.7.4.5.8, Table 86).
    private static readonly int[] TensorOrder = { 0, 1, 2, 3, 7, 11, 15, 14, 13, 12, 8, 4, 5, 6, 10, 9 };

    private void DecodePatches(BitReader r, List<PdfMeshPatch> patches)
    {
        bool tensor = _type == 7;
        int total = tensor ? 16 : 12;
        PdfMeshPatch? prev = null;
        while (r.Has(_bitsPerFlag))
        {
            uint flag = r.Read(_bitsPerFlag);
            var pts = new PdfPoint[16];
            var colors = new PdfColor[4];
            var ts = new double[4];
            int firstPoint = 0, firstColor = 0;
            if (flag != 0)
            {
                if (prev == null) return; // malformed: nothing to share an edge with
                // Shared edge of the previous patch becomes this patch's u = 0 edge (p00..p03).
                int[] edge = flag switch
                {
                    1 => new[] { 3, 7, 11, 15 },   // p03 p13 p23 p33
                    2 => new[] { 15, 14, 13, 12 }, // p33 p32 p31 p30
                    _ => new[] { 12, 8, 4, 0 },    // p30 p20 p10 p00
                };
                int[] cornerColors = flag switch { 1 => new[] { 1, 2 }, 2 => new[] { 2, 3 }, _ => new[] { 3, 0 } };
                for (int k = 0; k < 4; k++) pts[k] = prev.Points[edge[k]];
                colors[0] = prev.Colors[cornerColors[0]]; colors[1] = prev.Colors[cornerColors[1]];
                ts[0] = prev.T[cornerColors[0]]; ts[1] = prev.T[cornerColors[1]];
                firstPoint = 4;
                firstColor = 2;
            }
            int needBits = (total - firstPoint) * 2 * _bitsPerCoordinate + (4 - firstColor) * _valueCount * _bitsPerComponent;
            if (!r.Has(needBits)) return;
            for (int k = firstPoint; k < total; k++)
                pts[TensorOrder[k]] = ReadPoint(r);
            // Corner colours in stream order: c00, c03, c33, c30 (shared ones already filled).
            for (int k = firstColor; k < 4; k++)
                (colors[k], ts[k]) = ReadValue(r);
            r.Align();
            if (!tensor)
                ComputeCoonsInterior(pts);
            var patch = new PdfMeshPatch(pts, colors, ts);
            if (patches.Count >= MaxPatches)
                throw new PdfResourceLimitException("MaxMeshPatches", "Mesh shading exceeds the patch budget.");
            patches.Add(patch);
            prev = patch;
        }
    }

    /// <summary>Interior control points that make a tensor patch equal to the Coons patch (8.7.4.5.8).</summary>
    private static void ComputeCoonsInterior(PdfPoint[] p)
    {
        PdfPoint P(int i, int j) => p[i * 4 + j];
        PdfPoint Comb(double[] w, PdfPoint[] q)
        {
            double x = 0, y = 0;
            for (int k = 0; k < w.Length; k++) { x += w[k] * q[k].X; y += w[k] * q[k].Y; }
            return new PdfPoint(x / 9, y / 9);
        }
        var weights = new[] { -4.0, 6, 6, -2, -2, 3, 3, -1 };
        p[1 * 4 + 1] = Comb(weights, new[] { P(0, 0), P(0, 1), P(1, 0), P(0, 3), P(3, 0), P(3, 1), P(1, 3), P(3, 3) });
        p[1 * 4 + 2] = Comb(weights, new[] { P(0, 3), P(0, 2), P(1, 3), P(0, 0), P(3, 3), P(3, 2), P(1, 0), P(3, 0) });
        p[2 * 4 + 1] = Comb(weights, new[] { P(3, 0), P(3, 1), P(2, 0), P(3, 3), P(0, 0), P(0, 1), P(2, 3), P(0, 3) });
        p[2 * 4 + 2] = Comb(weights, new[] { P(3, 3), P(3, 2), P(2, 3), P(3, 0), P(0, 3), P(0, 2), P(2, 0), P(0, 0) });
    }
}
