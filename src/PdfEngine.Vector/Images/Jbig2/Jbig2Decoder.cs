using System;
using System.Collections.Generic;
using System.Threading;
using PdfEngine.Vector.Diagnostics;

namespace PdfEngine.Vector.Images.Jbig2;

/// <summary>
/// JBIG2Decode (ISO 32000-2 7.4.7, ITU-T T.88): embedded-stream segments (plus /JBIG2Globals)
/// for one page. Supports symbol dictionaries (arithmetic and Huffman, refinement/aggregation,
/// context reuse), text regions (all corners, transposition, refinement, Huffman symbol-ID
/// tables, custom tables), generic regions (templates 0–3, TPGDON, MMR, unknown length),
/// generic refinement regions (incl. TPGRON), pattern dictionaries, halftone regions and page
/// striping. Output: packed 1-bit rows in PDF sample convention (0 = black).
/// Unsupported: colour extension, 12-pixel extended templates (classified as decode errors).
/// </summary>
internal static class Jbig2Decoder
{
    public static byte[] Decode(byte[] data, byte[]? globals, int width, int height, CancellationToken ct = default)
    {
        try
        {
            var doc = new Jbig2Document(width, height, ct);
            if (globals != null) doc.Process(globals);
            doc.Process(data);
            return doc.ToPdfSamples();
        }
        catch (Jbig2Exception ex)
        {
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, "JBIG2: " + ex.Message);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new PdfUnsupportedFeatureException(PdfFallbackReason.ImageDecode, $"JBIG2 data is malformed ({ex.GetType().Name}).");
        }
    }
}

internal sealed class Jbig2Document
{
    private sealed record Segment(uint Number, int Type, uint[] Referred, uint PageAssociation, int DataStart, int DataEnd, byte[] Data, bool UnknownLength);

    private sealed class SymbolDictionary
    {
        public required List<Jbig2Bitmap> Exported { get; init; }
        public byte[]? GenericContexts, RefinementContexts;
    }

    private readonly Dictionary<uint, object> _results = new();
    private readonly int _imageWidth, _imageHeight;
    private readonly CancellationToken _ct;
    private Jbig2Bitmap? _page;
    private int _pageDefaultOp;
    private const long MaxSymbols = 1 << 20;

    public Jbig2Document(int width, int height, CancellationToken ct)
    {
        _imageWidth = width;
        _imageHeight = height;
        _ct = ct;
    }

    // ------------------------------------------------------------------ segments (7.2)

    private static uint U32(byte[] d, int i)
    {
        if (i + 4 > d.Length) throw new Jbig2Exception("segment truncated");
        return (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
    }
    private static int U16(byte[] d, int i)
    {
        if (i + 2 > d.Length) throw new Jbig2Exception("segment truncated");
        return d[i] << 8 | d[i + 1];
    }
    private static int U8(byte[] d, int i) => i < d.Length ? d[i] : throw new Jbig2Exception("segment truncated");

    public void Process(byte[] data)
    {
        int pos = 0;
        while (pos + 11 <= data.Length)
        {
            _ct.ThrowIfCancellationRequested();
            uint number = U32(data, pos);
            int flags = U8(data, pos + 4);
            int type = flags & 0x3F;
            bool pageAssoc4 = (flags & 0x40) != 0;
            int p = pos + 5;
            int rts = U8(data, p);
            long referredCount = rts >> 5;
            if (referredCount == 7)
            {
                referredCount = U32(data, p) & 0x1FFFFFFF;
                p += 4 + (int)((referredCount + 8) / 8);
            }
            else
            {
                p += 1;
            }
            if (referredCount > 65536) throw new Jbig2Exception("too many referred segments");
            int refSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
            var referred = new uint[referredCount];
            for (int i = 0; i < referredCount; i++, p += refSize)
                referred[i] = refSize == 1 ? (uint)U8(data, p) : refSize == 2 ? (uint)U16(data, p) : U32(data, p);
            uint page = pageAssoc4 ? U32(data, p) : (uint)U8(data, p);
            p += pageAssoc4 ? 4 : 1;
            uint length = U32(data, p);
            p += 4;
            int dataStart = p;
            int dataEnd;
            if (length == 0xFFFFFFFF)
            {
                if (type is not (38 or 39)) throw new Jbig2Exception("unknown segment length outside an immediate generic region");
                dataEnd = FindGenericRegionEnd(data, dataStart);
            }
            else
            {
                if (dataStart + (long)length > data.Length) throw new Jbig2Exception("segment data truncated");
                dataEnd = dataStart + (int)length;
            }
            var seg = new Segment(number, type, referred, page, dataStart, dataEnd, data, length == 0xFFFFFFFF);
            if (!HandleSegment(seg))
                return; // end of page / end of file
            pos = dataEnd;
        }
    }

    /// <summary>Unknown-length generic region (7.2.7): data ends with FF AC (arithmetic) or 00 00 (MMR) plus a 4-byte row count.</summary>
    private static int FindGenericRegionEnd(byte[] data, int start)
    {
        if (start + 18 > data.Length) throw new Jbig2Exception("generic region truncated");
        bool mmr = (data[start + 17] & 1) != 0;
        byte a = mmr ? (byte)0x00 : (byte)0xFF, b = mmr ? (byte)0x00 : (byte)0xAC;
        for (int i = start + 18; i + 6 <= data.Length; i++)
            if (data[i] == a && data[i + 1] == b)
                return i + 6;
        throw new Jbig2Exception("end marker of unknown-length region not found");
    }

    private bool HandleSegment(Segment s)
    {
        switch (s.Type)
        {
            case 0: _results[s.Number] = DecodeSymbolDictionary(s); break;
            case 4: case 6: case 7: Region(s, DecodeTextRegionSegment(s, out var ti), ti, s.Type == 4); break;
            case 16: _results[s.Number] = DecodePatternDictionary(s); break;
            case 20: case 22: case 23: Region(s, DecodeHalftoneRegion(s, out var hi), hi, s.Type == 20); break;
            case 36: case 38: case 39: Region(s, DecodeGenericRegion(s, out var gi), gi, s.Type == 36); break;
            case 40: case 42: case 43: RefinementRegion(s); break;
            case 48: PageInformation(s); break;
            case 49: return false; // end of page
            case 50: break;        // end of stripe: the page is sized from the image
            case 51: return false; // end of file
            case 53: _results[s.Number] = Jbig2HuffmanTable.Parse(s.Data, s.DataStart, s.DataEnd); break;
            default: break;        // profiles (52), extensions (62) and reserved types carry nothing to draw
        }
        return true;
    }

    // ------------------------------------------------------------------ page (7.4.8)

    private void PageInformation(Segment s)
    {
        int w = (int)Math.Min(U32(s.Data, s.DataStart), (uint)int.MaxValue);
        uint hRaw = U32(s.Data, s.DataStart + 4);
        int flags = U8(s.Data, s.DataStart + 16);
        int h = hRaw == 0xFFFFFFFF ? _imageHeight : (int)Math.Min(hRaw, (uint)int.MaxValue);
        if ((long)w * h > 1L << 30) throw new Jbig2Exception("page is too large");
        _pageDefaultOp = (flags >> 3) & 3;
        _page = new Jbig2Bitmap(w, h, (byte)((flags >> 2) & 1));
    }

    private Jbig2Bitmap Page => _page ??= new Jbig2Bitmap(_imageWidth, _imageHeight);

    private readonly record struct RegionInfo(int Width, int Height, int X, int Y, int CombinationOp);

    private static RegionInfo ReadRegionInfo(Segment s)
    {
        int w = (int)Math.Min(U32(s.Data, s.DataStart), (uint)int.MaxValue);
        int h = (int)Math.Min(U32(s.Data, s.DataStart + 4), (uint)int.MaxValue);
        int x = (int)U32(s.Data, s.DataStart + 8), y = (int)U32(s.Data, s.DataStart + 12);
        int flags = U8(s.Data, s.DataStart + 16);
        if ((flags & 8) != 0) throw new Jbig2Exception("colour extension is not supported");
        if ((long)w * h > 1L << 30) throw new Jbig2Exception("region is too large");
        return new RegionInfo(w, h, x, y, flags & 7);
    }

    private void Region(Segment s, Jbig2Bitmap bitmap, RegionInfo info, bool intermediate)
    {
        if (intermediate)
            _results[s.Number] = (bitmap, info);
        else
            Page.Combine(bitmap, info.X, info.Y, info.CombinationOp);
    }

    public byte[] ToPdfSamples()
    {
        var page = Page;
        int rowBytes = (_imageWidth + 7) / 8;
        var output = new byte[checked(rowBytes * _imageHeight)];
        Array.Fill(output, (byte)0xFF); // white where the page has no data
        for (int y = 0; y < _imageHeight; y++)
        {
            for (int x = 0; x < _imageWidth; x++)
            {
                if (page.Get(x, y) == 1) // black → sample 0
                    output[y * rowBytes + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
            }
        }
        return output;
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<T> Referred<T>(Segment s) where T : class
    {
        foreach (uint r in s.Referred)
            if (_results.TryGetValue(r, out var v) && v is T t)
                yield return t;
    }

    private static (sbyte X, sbyte Y)[] ReadAt(byte[] d, int p, int count)
    {
        var at = new (sbyte, sbyte)[count];
        for (int i = 0; i < count; i++)
            at[i] = ((sbyte)U8(d, p + 2 * i), (sbyte)U8(d, p + 2 * i + 1));
        return at;
    }

    private static int CeilLog2(long n)
    {
        int bits = 0;
        while ((1L << bits) < n) bits++;
        return bits;
    }

    // ------------------------------------------------------------------ generic region (7.4.6)

    private Jbig2Bitmap DecodeGenericRegion(Segment s, out RegionInfo info)
    {
        info = ReadRegionInfo(s);
        int p = s.DataStart + 17;
        int flags = U8(s.Data, p++);
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool tpgdon = (flags & 8) != 0;
        if ((flags & 16) != 0) throw new Jbig2Exception("extended generic templates are not supported");
        int height = info.Height;
        int end = s.DataEnd;
        if (s.UnknownLength && IsUnknownLengthMarker(s, mmr))
        {
            height = (int)Math.Min(U32(s.Data, s.DataEnd - 4), (uint)info.Height); // actual row count
            end = s.DataEnd - 6;
        }
        if (mmr)
        {
            var mmrBitmap = Jbig2Regions.DecodeMmr(s.Data, p, end, info.Width, height, out _, _ct);
            info = info with { Height = height };
            return mmrBitmap;
        }
        var at = ReadAt(s.Data, p, template == 0 ? 4 : 1);
        p += template == 0 ? 8 : 2;
        var mq = new MqDecoder(s.Data, p, end);
        var bitmap = Jbig2Regions.DecodeGeneric(mq, Jbig2Regions.NewGenericContexts(template), info.Width, height, template, at, tpgdon, null, _ct);
        info = info with { Height = height };
        return bitmap;
    }

    private static bool IsUnknownLengthMarker(Segment s, bool mmr)
    {
        int m = s.DataEnd - 6;
        return m > s.DataStart && (mmr ? s.Data[m] == 0 && s.Data[m + 1] == 0 : s.Data[m] == 0xFF && s.Data[m + 1] == 0xAC);
    }

    // ------------------------------------------------------------------ refinement region (7.4.7)

    private void RefinementRegion(Segment s)
    {
        var info = ReadRegionInfo(s);
        int p = s.DataStart + 17;
        int flags = U8(s.Data, p++);
        int template = flags & 1;
        bool tpgron = (flags & 2) != 0;
        (sbyte, sbyte)[] at = template == 0 ? ReadAt(s.Data, p, 2) : new (sbyte, sbyte)[] { (-1, -1), (-1, -1) };
        p += template == 0 ? 4 : 0;

        Jbig2Bitmap reference;
        (Jbig2Bitmap Bitmap, RegionInfo Info)? referredRegion = null;
        foreach (uint r in s.Referred)
            if (_results.TryGetValue(r, out var v) && v is ValueTuple<Jbig2Bitmap, RegionInfo> tuple)
                referredRegion = tuple;
        if (referredRegion is { } rr)
        {
            reference = rr.Bitmap;
        }
        else
        {
            // No referred region: refine the page area under this region (7.4.7.5).
            reference = new Jbig2Bitmap(info.Width, info.Height);
            for (int y = 0; y < info.Height; y++)
                for (int x = 0; x < info.Width; x++)
                    reference.Px[y * info.Width + x] = (byte)Page.Get(info.X + x, info.Y + y);
        }
        var mq = new MqDecoder(s.Data, p, s.DataEnd);
        var bitmap = Jbig2Regions.DecodeRefinement(mq, Jbig2Regions.NewRefinementContexts(template), info.Width, info.Height,
            template, reference, 0, 0, at, tpgron, _ct);
        if (s.Type == 40)
            _results[s.Number] = (bitmap, info);
        else
            Page.Combine(bitmap, info.X, info.Y, info.CombinationOp); // the region's external operator (7.4.7.5), as PDFium and jbig2dec
    }

    // ------------------------------------------------------------------ symbol dictionary (7.4.2, 6.5)

    private SymbolDictionary DecodeSymbolDictionary(Segment s)
    {
        byte[] d = s.Data;
        int p = s.DataStart;
        int flags = U16(d, p); p += 2;
        bool huff = (flags & 1) != 0, refAgg = (flags & 2) != 0;
        int selDh = (flags >> 2) & 3, selDw = (flags >> 4) & 3, selBmSize = (flags >> 6) & 1, selAggInst = (flags >> 7) & 1;
        bool contextUsed = (flags & 0x100) != 0, contextRetained = (flags & 0x200) != 0;
        int template = (flags >> 10) & 3, rTemplate = (flags >> 12) & 1;
        (sbyte X, sbyte Y)[] at = Array.Empty<(sbyte, sbyte)>();
        if (!huff) { at = ReadAt(d, p, template == 0 ? 4 : 1); p += template == 0 ? 8 : 2; }
        (sbyte X, sbyte Y)[] rAt = new (sbyte, sbyte)[] { (-1, -1), (-1, -1) };
        if (refAgg && rTemplate == 0) { rAt = ReadAt(d, p, 2); p += 4; }
        long numExported = U32(d, p), numNew = U32(d, p + 4);
        p += 8;
        if (numNew > MaxSymbols || numExported > MaxSymbols) throw new Jbig2Exception("too many symbols");

        var input = new List<Jbig2Bitmap>();
        SymbolDictionary? lastInput = null;
        foreach (var sd in Referred<SymbolDictionary>(s)) { input.AddRange(sd.Exported); lastInput = sd; }
        var tables = new List<Jbig2HuffmanTable>(Referred<Jbig2HuffmanTable>(s));
        int tableIndex = 0;
        Jbig2HuffmanTable Custom() => tableIndex < tables.Count ? tables[tableIndex++] : throw new Jbig2Exception("missing custom Huffman table");

        Jbig2HuffmanTable? tDh = null, tDw = null, tBmSize = null, tAggInst = null;
        if (huff)
        {
            tDh = selDh == 0 ? Jbig2HuffmanTable.Get(4) : selDh == 1 ? Jbig2HuffmanTable.Get(5) : Custom();
            tDw = selDw == 0 ? Jbig2HuffmanTable.Get(2) : selDw == 1 ? Jbig2HuffmanTable.Get(3) : Custom();
            tBmSize = selBmSize == 0 ? Jbig2HuffmanTable.Get(1) : Custom();
            tAggInst = selAggInst == 0 ? Jbig2HuffmanTable.Get(1) : Custom();
        }

        MqDecoder? mq = huff ? null : new MqDecoder(d, p, s.DataEnd);
        Jbig2BitReader? br = huff ? new Jbig2BitReader(d, p, s.DataEnd) : null;
        var gbCtx = contextUsed && lastInput?.GenericContexts is { } g ? (byte[])g.Clone() : Jbig2Regions.NewGenericContexts(template);
        var grCtx = contextUsed && lastInput?.RefinementContexts is { } r ? (byte[])r.Clone() : Jbig2Regions.NewRefinementContexts(rTemplate);
        var iadh = mq != null ? new Jbig2IntegerDecoder(mq) : null;
        var iadw = mq != null ? new Jbig2IntegerDecoder(mq) : null;
        var iaai = mq != null ? new Jbig2IntegerDecoder(mq) : null;
        var iaex = mq != null ? new Jbig2IntegerDecoder(mq) : null;
        int symCodeLen = CeilLog2(input.Count + numNew);
        if (huff) symCodeLen = Math.Max(symCodeLen, 1); // 6.5.8.2.3
        // Aggregation shares IAID, IARDX, IARDY and the refinement contexts with its text-region procedure.
        TextRegionContexts? textCtx = mq != null && refAgg ? new TextRegionContexts(mq, symCodeLen, rTemplate, grCtx) : null;

        var newSymbols = new List<Jbig2Bitmap>();
        int hcHeight = 0;
        while (newSymbols.Count < numNew)
        {
            _ct.ThrowIfCancellationRequested();
            int dh = huff ? tDh!.DecodeRequired(br!, "delta height") : iadh!.DecodeRequired("delta height");
            hcHeight += dh;
            if (hcHeight < 0 || hcHeight > 1 << 16) throw new Jbig2Exception("invalid symbol height");
            int symWidth = 0, totWidth = 0;
            var classWidths = new List<int>();
            while (true)
            {
                int? dw = huff ? tDw!.Decode(br!) : iadw!.Decode();
                if (dw == null) break;
                symWidth += dw.Value;
                if (symWidth < 0 || symWidth > 1 << 16) throw new Jbig2Exception("invalid symbol width");
                totWidth += symWidth;
                if (newSymbols.Count + classWidths.Count >= numNew) throw new Jbig2Exception("more symbols than declared");
                if (!huff || refAgg)
                {
                    Jbig2Bitmap bmp;
                    if (!refAgg)
                    {
                        bmp = Jbig2Regions.DecodeGeneric(mq!, gbCtx, symWidth, hcHeight, template, at, false, null, _ct);
                    }
                    else
                    {
                        int inst = huff ? tAggInst!.DecodeRequired(br!, "instance count") : iaai!.DecodeRequired("instance count");
                        var available = new List<Jbig2Bitmap>(input.Count + newSymbols.Count);
                        available.AddRange(input);
                        available.AddRange(newSymbols);
                        if (inst > 1)
                        {
                            var trp = new TextRegionParams(symWidth, hcHeight, inst, 1, 0, false, 0, 1, 0, 0, true, rTemplate, rAt);
                            bmp = huff
                                ? DecodeTextRegionHuffman(br!, trp, available, FixedLengthIdTable(symCodeLen, available.Count),
                                    Jbig2HuffmanTable.Get(6), Jbig2HuffmanTable.Get(8), Jbig2HuffmanTable.Get(11),
                                    Jbig2HuffmanTable.Get(15), Jbig2HuffmanTable.Get(15), Jbig2HuffmanTable.Get(15), Jbig2HuffmanTable.Get(15),
                                    Jbig2HuffmanTable.Get(1), d, s.DataEnd)
                                : DecodeTextRegionArithmetic(textCtx!, trp, available);
                        }
                        else
                        {
                            int id, rdx, rdy;
                            if (huff)
                            {
                                id = (int)br!.ReadBits(symCodeLen);
                                rdx = Jbig2HuffmanTable.Get(15).DecodeRequired(br, "RDX");
                                rdy = Jbig2HuffmanTable.Get(15).DecodeRequired(br, "RDY");
                                int bmSize = Jbig2HuffmanTable.Get(1).DecodeRequired(br, "BMSIZE");
                                br.Align();
                                int start = br.BytePosition;
                                if (id < 0 || id >= available.Count) throw new Jbig2Exception("symbol id out of range");
                                var rmq = new MqDecoder(d, start, Math.Min(s.DataEnd, start + bmSize));
                                bmp = Jbig2Regions.DecodeRefinement(rmq, grCtx, symWidth, hcHeight, rTemplate, available[id], rdx, rdy, rAt, false, _ct);
                                br.SkipTo(start + bmSize);
                            }
                            else
                            {
                                id = textCtx!.Id.Decode();
                                rdx = textCtx.Rdx.DecodeRequired("RDX");
                                rdy = textCtx.Rdy.DecodeRequired("RDY");
                                if (id < 0 || id >= available.Count) throw new Jbig2Exception("symbol id out of range");
                                bmp = Jbig2Regions.DecodeRefinement(mq!, textCtx.Gr, symWidth, hcHeight, rTemplate, available[id], rdx, rdy, rAt, false, _ct);
                            }
                        }
                    }
                    newSymbols.Add(bmp);
                }
                else
                {
                    classWidths.Add(symWidth);
                }
            }
            if (huff && !refAgg)
            {
                // Height class collective bitmap (6.5.9).
                int bmSize = tBmSize!.DecodeRequired(br!, "BMSIZE");
                br!.Align();
                int start = br.BytePosition;
                Jbig2Bitmap collective;
                if (bmSize == 0)
                {
                    collective = new Jbig2Bitmap(totWidth, hcHeight);
                    int rowBytes = (totWidth + 7) / 8;
                    if (start + (long)rowBytes * hcHeight > s.DataEnd) throw new Jbig2Exception("uncompressed collective bitmap truncated");
                    for (int y = 0; y < hcHeight; y++)
                        for (int x = 0; x < totWidth; x++)
                            collective.Px[y * totWidth + x] = (byte)((d[start + y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1);
                    br.SkipTo(start + rowBytes * hcHeight);
                }
                else
                {
                    collective = Jbig2Regions.DecodeMmr(d, start, Math.Min(s.DataEnd, start + bmSize), totWidth, hcHeight, out _, _ct);
                    br.SkipTo(start + bmSize);
                }
                int x0 = 0;
                foreach (int w in classWidths)
                {
                    var sym = new Jbig2Bitmap(w, hcHeight);
                    for (int y = 0; y < hcHeight; y++)
                        Array.Copy(collective.Px, y * totWidth + x0, sym.Px, y * w, w);
                    newSymbols.Add(sym);
                    x0 += w;
                }
            }
        }

        // Exported symbols (6.5.10): alternating runs of "not exported" / "exported".
        var exported = new List<Jbig2Bitmap>();
        long total = input.Count + newSymbols.Count, index = 0;
        bool exportFlag = false;
        var b1 = Jbig2HuffmanTable.Get(1);
        while (index < total)
        {
            int run = huff ? b1.DecodeRequired(br!, "export run") : iaex!.DecodeRequired("export run");
            if (run < 0 || index + run > total) throw new Jbig2Exception("invalid export run");
            if (exportFlag)
                for (long i = index; i < index + run; i++)
                    exported.Add(i < input.Count ? input[(int)i] : newSymbols[(int)(i - input.Count)]);
            index += run;
            exportFlag = !exportFlag;
        }
        return new SymbolDictionary
        {
            Exported = exported,
            GenericContexts = contextRetained ? gbCtx : null,
            RefinementContexts = contextRetained ? grCtx : null,
        };
    }

    private static Jbig2HuffmanTable FixedLengthIdTable(int codeLength, int count)
    {
        var lines = new List<Jbig2HuffmanTable.Line>(count);
        for (int i = 0; i < count; i++) lines.Add(new Jbig2HuffmanTable.Line(Math.Max(1, codeLength), 0, i));
        return new Jbig2HuffmanTable(lines);
    }

    // ------------------------------------------------------------------ text region (7.4.3, 6.4)

    private sealed record TextRegionParams(int Width, int Height, int Instances, int Strips, int DefaultPixel, bool Transposed,
        int DsOffset, int RefCorner, int CombinationOp, int LogStrips, bool Refine, int RTemplate, (sbyte X, sbyte Y)[] RAt);

    /// <summary>Integer and refinement contexts of one arithmetic text region (shared with its symbol dictionary when aggregating).</summary>
    private sealed class TextRegionContexts
    {
        public readonly MqDecoder Mq;
        public readonly Jbig2IntegerDecoder Dt, Fs, Ds, It, Ri, Rdw, Rdh, Rdx, Rdy;
        public readonly Jbig2IdDecoder Id;
        public readonly byte[] Gr;
        public TextRegionContexts(MqDecoder mq, int symCodeLength, int rTemplate, byte[]? refinementContexts = null)
        {
            Mq = mq;
            Dt = new(mq); Fs = new(mq); Ds = new(mq); It = new(mq); Ri = new(mq);
            Rdw = new(mq); Rdh = new(mq); Rdx = new(mq); Rdy = new(mq);
            Id = new Jbig2IdDecoder(mq, symCodeLength);
            Gr = refinementContexts ?? Jbig2Regions.NewRefinementContexts(rTemplate);
        }
    }

    private Jbig2Bitmap DecodeTextRegionSegment(Segment s, out RegionInfo info)
    {
        info = ReadRegionInfo(s);
        byte[] d = s.Data;
        int p = s.DataStart + 17;
        int flags = U16(d, p); p += 2;
        bool huff = (flags & 1) != 0, refine = (flags & 2) != 0;
        int logStrips = (flags >> 2) & 3, refCorner = (flags >> 4) & 3;
        bool transposed = (flags & 0x40) != 0;
        int combOp = (flags >> 7) & 3, defPixel = (flags >> 9) & 1;
        int dsOffset = (flags >> 10) & 0x1F;
        if (dsOffset > 15) dsOffset -= 32;
        int rTemplate = (flags >> 15) & 1;
        int hflags = 0;
        if (huff) { hflags = U16(d, p); p += 2; }
        (sbyte X, sbyte Y)[] rAt = new (sbyte, sbyte)[] { (-1, -1), (-1, -1) };
        if (refine && rTemplate == 0) { rAt = ReadAt(d, p, 2); p += 4; }
        long instances = U32(d, p); p += 4;
        if (instances > 1 << 24) throw new Jbig2Exception("too many symbol instances");

        var symbols = new List<Jbig2Bitmap>();
        foreach (var sd in Referred<SymbolDictionary>(s)) symbols.AddRange(sd.Exported);
        var trp = new TextRegionParams(info.Width, info.Height, (int)instances, 1 << logStrips, defPixel, transposed, dsOffset,
            refCorner, combOp, logStrips, refine, rTemplate, rAt);

        if (!huff)
        {
            var ctx = new TextRegionContexts(new MqDecoder(d, p, s.DataEnd), CeilLog2(symbols.Count), rTemplate);
            return DecodeTextRegionArithmetic(ctx, trp, symbols);
        }

        var tables = new List<Jbig2HuffmanTable>(Referred<Jbig2HuffmanTable>(s));
        int ti = 0;
        Jbig2HuffmanTable Custom() => ti < tables.Count ? tables[ti++] : throw new Jbig2Exception("missing custom Huffman table");
        int fsSel = hflags & 3, dsSel = (hflags >> 2) & 3, dtSel = (hflags >> 4) & 3;
        int rdwSel = (hflags >> 6) & 3, rdhSel = (hflags >> 8) & 3, rdxSel = (hflags >> 10) & 3, rdySel = (hflags >> 12) & 3, rsizeSel = (hflags >> 14) & 1;
        var fs = fsSel == 0 ? Jbig2HuffmanTable.Get(6) : fsSel == 1 ? Jbig2HuffmanTable.Get(7) : Custom();
        var ds = dsSel == 0 ? Jbig2HuffmanTable.Get(8) : dsSel == 1 ? Jbig2HuffmanTable.Get(9) : dsSel == 2 ? Jbig2HuffmanTable.Get(10) : Custom();
        var dt = dtSel == 0 ? Jbig2HuffmanTable.Get(11) : dtSel == 1 ? Jbig2HuffmanTable.Get(12) : dtSel == 2 ? Jbig2HuffmanTable.Get(13) : Custom();
        Jbig2HuffmanTable R(int sel) => sel == 0 ? Jbig2HuffmanTable.Get(14) : sel == 1 ? Jbig2HuffmanTable.Get(15) : Custom();
        var rdw = R(rdwSel); var rdh = R(rdhSel); var rdx = R(rdxSel); var rdy = R(rdySel);
        var rsize = rsizeSel == 0 ? Jbig2HuffmanTable.Get(1) : Custom();

        var br = new Jbig2BitReader(d, p, s.DataEnd);
        var idTable = ReadSymbolIdTable(br, symbols.Count);
        return DecodeTextRegionHuffman(br, trp, symbols, idTable, fs, ds, dt, rdw, rdh, rdx, rdy, rsize, d, s.DataEnd);
    }

    /// <summary>Symbol ID Huffman table (7.4.3.1.7): run-code lengths, then code lengths per symbol.</summary>
    private static Jbig2HuffmanTable ReadSymbolIdTable(Jbig2BitReader br, int symbolCount)
    {
        var runLines = new List<Jbig2HuffmanTable.Line>(35);
        for (int i = 0; i < 35; i++) runLines.Add(new Jbig2HuffmanTable.Line((int)br.ReadBits(4), 0, i));
        var runTable = new Jbig2HuffmanTable(runLines);
        var lengths = new int[symbolCount];
        int n = 0;
        while (n < symbolCount)
        {
            int code = runTable.DecodeRequired(br, "run code");
            if (code < 32) { lengths[n++] = code; continue; }
            int repeat, value;
            if (code == 32) { if (n == 0) throw new Jbig2Exception("repeat without a previous length"); repeat = 3 + (int)br.ReadBits(2); value = lengths[n - 1]; }
            else if (code == 33) { repeat = 3 + (int)br.ReadBits(3); value = 0; }
            else { repeat = 11 + (int)br.ReadBits(7); value = 0; }
            for (int k = 0; k < repeat && n < symbolCount; k++) lengths[n++] = value;
        }
        br.Align();
        var lines = new List<Jbig2HuffmanTable.Line>(symbolCount);
        for (int i = 0; i < symbolCount; i++) lines.Add(new Jbig2HuffmanTable.Line(lengths[i], 0, i));
        return new Jbig2HuffmanTable(lines);
    }

    private Jbig2Bitmap DecodeTextRegionArithmetic(TextRegionContexts c, TextRegionParams t, List<Jbig2Bitmap> symbols)
    {
        return DecodeTextRegion(t, symbols,
            decodeDt: () => c.Dt.DecodeRequired("DT"),
            decodeFs: () => c.Fs.DecodeRequired("FS"),
            decodeDs: () => c.Ds.Decode(),
            decodeIt: () => c.It.DecodeRequired("IT"),
            decodeId: () => c.Id.Decode(),
            decodeRi: () => c.Ri.DecodeRequired("RI"),
            refine: (sym) =>
            {
                int dw = c.Rdw.DecodeRequired("RDW"), dh = c.Rdh.DecodeRequired("RDH");
                int dx = c.Rdx.DecodeRequired("RDX"), dy = c.Rdy.DecodeRequired("RDY");
                return Jbig2Regions.DecodeRefinement(c.Mq, c.Gr, sym.Width + dw, sym.Height + dh, t.RTemplate, sym,
                    (dw >> 1) + dx, (dh >> 1) + dy, t.RAt, false, _ct);
            });
    }

    private Jbig2Bitmap DecodeTextRegionHuffman(Jbig2BitReader br, TextRegionParams t, List<Jbig2Bitmap> symbols, Jbig2HuffmanTable idTable,
        Jbig2HuffmanTable fs, Jbig2HuffmanTable ds, Jbig2HuffmanTable dt, Jbig2HuffmanTable rdwT, Jbig2HuffmanTable rdhT,
        Jbig2HuffmanTable rdxT, Jbig2HuffmanTable rdyT, Jbig2HuffmanTable rsizeT, byte[] data, int end)
    {
        var gr = Jbig2Regions.NewRefinementContexts(t.RTemplate);
        return DecodeTextRegion(t, symbols,
            decodeDt: () => dt.DecodeRequired(br, "DT"),
            decodeFs: () => fs.DecodeRequired(br, "FS"),
            decodeDs: () => ds.Decode(br),
            decodeIt: () => (int)br.ReadBits(t.LogStrips),
            decodeId: () => idTable.DecodeRequired(br, "symbol id"),
            decodeRi: () => br.ReadBit(),
            refine: (sym) =>
            {
                int dw = rdwT.DecodeRequired(br, "RDW"), dh = rdhT.DecodeRequired(br, "RDH");
                int dx = rdxT.DecodeRequired(br, "RDX"), dy = rdyT.DecodeRequired(br, "RDY");
                int size = rsizeT.DecodeRequired(br, "RSIZE");
                br.Align();
                int start = br.BytePosition;
                var mq = new MqDecoder(data, start, Math.Min(end, start + size));
                var bmp = Jbig2Regions.DecodeRefinement(mq, gr, sym.Width + dw, sym.Height + dh, t.RTemplate, sym,
                    (dw >> 1) + dx, (dh >> 1) + dy, t.RAt, false, _ct);
                br.SkipTo(start + size);
                return bmp;
            });
    }

    /// <summary>The text region decoding procedure (6.4.5), independent of the entropy coder.</summary>
    private Jbig2Bitmap DecodeTextRegion(TextRegionParams t, List<Jbig2Bitmap> symbols,
        Func<int> decodeDt, Func<int> decodeFs, Func<int?> decodeDs, Func<int> decodeIt, Func<int> decodeId, Func<int> decodeRi,
        Func<Jbig2Bitmap, Jbig2Bitmap> refine)
    {
        var region = new Jbig2Bitmap(t.Width, t.Height, (byte)t.DefaultPixel);
        long stripT = -(long)decodeDt() * t.Strips;
        long firstS = 0;
        int placed = 0;
        const int TopLeft = 1, TopRight = 3, BottomLeft = 0, BottomRight = 2;
        while (placed < t.Instances)
        {
            _ct.ThrowIfCancellationRequested();
            stripT += (long)decodeDt() * t.Strips;
            firstS += decodeFs();
            long curS = firstS;
            bool first = true;
            while (true)
            {
                if (!first)
                {
                    int? ids = decodeDs();
                    if (ids == null) break;
                    curS += ids.Value + t.DsOffset;
                }
                first = false;
                if (placed >= t.Instances) break;
                long curT = t.Strips == 1 ? 0 : decodeIt();
                long T = stripT + curT;
                int id = decodeId();
                if ((uint)id >= (uint)symbols.Count) throw new Jbig2Exception("symbol id out of range");
                var sym = symbols[id];
                if (t.Refine && decodeRi() != 0)
                    sym = refine(sym);
                int wi = sym.Width, hi = sym.Height;

                if (!t.Transposed && (t.RefCorner == TopRight || t.RefCorner == BottomRight)) curS += wi - 1;
                else if (t.Transposed && (t.RefCorner == BottomLeft || t.RefCorner == BottomRight)) curS += hi - 1;
                long S = curS;
                long x, y;
                if (!t.Transposed)
                {
                    x = t.RefCorner is TopRight or BottomRight ? S - wi + 1 : S;
                    y = t.RefCorner is BottomLeft or BottomRight ? T - hi + 1 : T;
                }
                else
                {
                    x = t.RefCorner is TopRight or BottomRight ? T - wi + 1 : T;
                    y = t.RefCorner is BottomLeft or BottomRight ? S - hi + 1 : S;
                }
                if (x > int.MinValue / 2 && x < int.MaxValue / 2 && y > int.MinValue / 2 && y < int.MaxValue / 2)
                    region.Combine(sym, (int)x, (int)y, t.CombinationOp);
                if (!t.Transposed && (t.RefCorner == TopLeft || t.RefCorner == BottomLeft)) curS += wi - 1;
                else if (t.Transposed && (t.RefCorner == TopLeft || t.RefCorner == TopRight)) curS += hi - 1;
                placed++;
            }
        }
        return region;
    }

    // ------------------------------------------------------------------ pattern dictionary (7.4.4, 6.7)

    private List<Jbig2Bitmap> DecodePatternDictionary(Segment s)
    {
        byte[] d = s.Data;
        int p = s.DataStart;
        int flags = U8(d, p);
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        int pw = U8(d, p + 1), ph = U8(d, p + 2);
        long grayMax = U32(d, p + 3);
        p += 7;
        if (grayMax > 1 << 16 || pw == 0 || ph == 0) throw new Jbig2Exception("invalid pattern dictionary");
        int width = checked((int)((grayMax + 1) * pw));
        Jbig2Bitmap collective;
        if (mmr)
        {
            collective = Jbig2Regions.DecodeMmr(d, p, s.DataEnd, width, ph, out _, _ct);
        }
        else
        {
            var at = template == 0
                ? new (sbyte, sbyte)[] { ((sbyte)-pw, 0), (-3, -1), (2, -2), (-2, -2) }
                : new (sbyte, sbyte)[] { ((sbyte)-pw, 0) };
            collective = Jbig2Regions.DecodeGeneric(new MqDecoder(d, p, s.DataEnd), Jbig2Regions.NewGenericContexts(template),
                width, ph, template, at, false, null, _ct);
        }
        var patterns = new List<Jbig2Bitmap>((int)grayMax + 1);
        for (int g = 0; g <= grayMax; g++)
        {
            var pat = new Jbig2Bitmap(pw, ph);
            for (int y = 0; y < ph; y++)
                Array.Copy(collective.Px, y * width + g * pw, pat.Px, y * pw, pw);
            patterns.Add(pat);
        }
        return patterns;
    }

    // ------------------------------------------------------------------ halftone region (7.4.5, 6.6)

    private Jbig2Bitmap DecodeHalftoneRegion(Segment s, out RegionInfo info)
    {
        info = ReadRegionInfo(s);
        byte[] d = s.Data;
        int p = s.DataStart + 17;
        int flags = U8(d, p++);
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool enableSkip = (flags & 8) != 0;
        int combOp = (flags >> 4) & 7, defPixel = (flags >> 7) & 1;
        int gw = (int)Math.Min(U32(d, p), 1u << 16), gh = (int)Math.Min(U32(d, p + 4), 1u << 16);
        int gx = (int)U32(d, p + 8), gy = (int)U32(d, p + 12);
        int rx = U16(d, p + 16), ry = U16(d, p + 18);
        p += 20;
        List<Jbig2Bitmap>? patterns = null;
        foreach (var pd in Referred<List<Jbig2Bitmap>>(s)) patterns = pd;
        if (patterns == null || patterns.Count == 0) throw new Jbig2Exception("halftone region without patterns");
        if ((long)gw * gh > 1 << 24) throw new Jbig2Exception("halftone grid too large");
        int pw = patterns[0].Width, ph = patterns[0].Height;

        var region = new Jbig2Bitmap(info.Width, info.Height, (byte)defPixel);
        Jbig2Bitmap? skip = null;
        if (enableSkip)
        {
            skip = new Jbig2Bitmap(gw, gh);
            for (int mg = 0; mg < gh; mg++)
                for (int ng = 0; ng < gw; ng++)
                {
                    long x = ((long)gx + (long)mg * ry + (long)ng * rx) >> 8, y = ((long)gy + (long)mg * rx - (long)ng * ry) >> 8;
                    if (x + pw <= 0 || x >= info.Width || y + ph <= 0 || y >= info.Height)
                        skip.Px[mg * gw + ng] = 1;
                }
        }

        // Gray-scale image (Annex C.5): Gray-coded bitplanes, most significant first.
        int bpp = Math.Max(1, CeilLog2(patterns.Count));
        var values = new int[gw * gh];
        Jbig2Bitmap? previous = null;
        MqDecoder? mq = mmr ? null : new MqDecoder(d, p, s.DataEnd);
        var ctx = Jbig2Regions.NewGenericContexts(template);
        var at = template == 0
            ? new (sbyte, sbyte)[] { (3, -1), (-3, -1), (2, -2), (-2, -2) }
            : new (sbyte, sbyte)[] { (template <= 1 ? (sbyte)3 : (sbyte)2, -1) };
        int mmrPos = p;
        for (int j = bpp - 1; j >= 0; j--)
        {
            Jbig2Bitmap plane = mmr
                ? Jbig2Regions.DecodeMmr(d, mmrPos, s.DataEnd, gw, gh, out mmrPos, _ct)
                : Jbig2Regions.DecodeGeneric(mq!, ctx, gw, gh, template, at, false, skip, _ct);
            if (previous != null)
                for (int i = 0; i < plane.Px.Length; i++) plane.Px[i] ^= previous.Px[i];
            for (int i = 0; i < plane.Px.Length; i++) values[i] |= plane.Px[i] << j;
            previous = plane;
        }

        for (int mg = 0; mg < gh; mg++)
        {
            _ct.ThrowIfCancellationRequested();
            for (int ng = 0; ng < gw; ng++)
            {
                if (skip != null && skip.Px[mg * gw + ng] != 0) continue;
                long x = ((long)gx + (long)mg * ry + (long)ng * rx) >> 8, y = ((long)gy + (long)mg * rx - (long)ng * ry) >> 8;
                var pat = patterns[Math.Min(values[mg * gw + ng], patterns.Count - 1)];
                if (x > -pw && x < info.Width && y > -ph && y < info.Height)
                    region.Combine(pat, (int)x, (int)y, combOp);
            }
        }
        return region;
    }
}
