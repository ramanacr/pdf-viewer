using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Images.Jbig2;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;
using E = PdfEngine.Vector.Tests.Fixtures.Jbig2TestEncoder;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// JBIG2Decode against PDFium on streams from <see cref="Jbig2TestEncoder"/>: generic regions
/// (every template, TPGDON, custom AT, MMR), symbol dictionaries and text regions (arithmetic and
/// Huffman, all reference corners, transposition, strips, refinement), pattern dictionaries with
/// halftone regions, refinement regions (incl. TPGRON), region combination, and globals.
/// Each case must decode to the intended page exactly and render like PDFium.
/// </summary>
public class Jbig2Tests
{
    private readonly ITestOutputHelper _output;
    public Jbig2Tests(ITestOutputHelper output) => _output = output;

    private const int PW = CcittFaxTests.W, PH = CcittFaxTests.H;

    private static Jbig2Bitmap SourcePage()
    {
        var mask = CcittFaxTests.BlackMask(CcittFaxTests.SourceImage());
        var b = new Jbig2Bitmap(PW, PH);
        Array.Copy(mask, b.Px, mask.Length);
        return b;
    }

    private static readonly (sbyte X, sbyte Y)[][] DefaultAts =
    {
        new (sbyte, sbyte)[] { (3, -1), (-3, -1), (2, -2), (-2, -2) },
        new (sbyte, sbyte)[] { (3, -1) },
        new (sbyte, sbyte)[] { (2, -1) },
        new (sbyte, sbyte)[] { (2, -1) },
    };

    private static byte[] AtBytes((sbyte X, sbyte Y)[] at) => at.SelectMany(a => new[] { (byte)a.X, (byte)a.Y }).ToArray();

    /// <summary>Decodes with our decoder (1 = black per pixel) and checks the PDF rendering against PDFium.</summary>
    private async Task<Jbig2Bitmap> DecodeAndCompare(string name, byte[] stream, int w, int h, byte[]? globals = null)
    {
        var packed = Jbig2Decoder.Decode(stream, globals, w, h);
        var ours = new Jbig2Bitmap(w, h);
        int rowBytes = (w + 7) / 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                ours.Px[y * w + x] = (byte)(((packed[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1) == 0 ? 1 : 0);

        var b = new VectorPdfBuilder();
        string globalsRef = "";
        if (globals != null) globalsRef = $" /DecodeParms << /JBIG2Globals {b.AddStream("", globals)} 0 R >>";
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode{globalsRef}", stream);
        b.AddPage($"q {w} 0 0 {h} 0 0 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>", mediaBox: $"[0 0 {w} {h}]");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var v = result.Page;
        Assert.Empty(result.Fallbacks);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"{name}: {stream.Length} bytes, mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 0.5 && bad <= 0.002, $"{name}: differs from PDFium, mean {mean:F2} bad {bad:P2}");
        return ours;
    }

    private static void AssertSame(Jbig2Bitmap expected, Jbig2Bitmap actual)
    {
        int diff = 0;
        for (int i = 0; i < expected.Px.Length; i++) diff += expected.Px[i] != actual.Px[i] ? 1 : 0;
        Assert.Equal(0, diff);
    }

    private static byte[] GenericSegmentData(Jbig2Bitmap page, int template, bool tpgdon, (sbyte X, sbyte Y)[] at, int x = 0, int y = 0, int op = 0)
    {
        var mq = new E.Mq();
        E.EncodeGeneric(mq, Jbig2Regions.NewGenericContexts(template), page, template, at, tpgdon);
        return E.Concat(E.RegionInfo(page.Width, page.Height, x, y, op), new[] { (byte)((template << 1) | (tpgdon ? 8 : 0)) }, AtBytes(at), mq.Flush());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task GenericRegion_AllTemplates(int template, bool tpgdon)
    {
        var page = SourcePage();
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        e.Segment(39, GenericSegmentData(page, template, tpgdon, DefaultAts[template]));
        e.Segment(49, Array.Empty<byte>());
        AssertSame(page, await DecodeAndCompare($"generic T{template} tpgdon={tpgdon}", e.ToArray(), PW, PH));
    }

    [Fact]
    public async Task GenericRegion_CustomAdaptivePixels()
    {
        var page = SourcePage();
        var at = new (sbyte, sbyte)[] { (-5, 0), (4, -1), (-1, -3), (6, -2) };
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        e.Segment(39, GenericSegmentData(page, 0, true, at));
        e.Segment(49, Array.Empty<byte>());
        AssertSame(page, await DecodeAndCompare("generic custom AT", e.ToArray(), PW, PH));
    }

    [Fact]
    public async Task GenericRegion_Mmr()
    {
        var page = SourcePage();
        var (g4, photometric) = CcittFaxTests.EncodeTiff(CcittFaxTests.SourceImage(), TiffCompressOption.Ccitt4);
        Assert.Equal(0, photometric); // CCITT white runs = white pixels, as JBIG2 MMR requires
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        e.Segment(39, E.Concat(E.RegionInfo(PW, PH, 0, 0, 0), new byte[] { 1 }, g4));
        e.Segment(49, Array.Empty<byte>());
        AssertSame(page, await DecodeAndCompare("generic MMR", e.ToArray(), PW, PH));
    }

    [Fact]
    public async Task Regions_CombineWithXor_AtOffsets()
    {
        var page = SourcePage();
        var block = new Jbig2Bitmap(60, 40, 1);
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        e.Segment(39, GenericSegmentData(page, 1, false, DefaultAts[1]));
        e.Segment(39, GenericSegmentData(block, 2, false, DefaultAts[2], 100, 50, op: 2)); // XOR
        e.Segment(49, Array.Empty<byte>());
        var expected = SourcePage();
        expected.Combine(block, 100, 50, 2);
        AssertSame(expected, await DecodeAndCompare("xor regions", e.ToArray(), PW, PH));
    }

    // ------------------------------------------------------------------ symbols and text

    private static List<Jbig2Bitmap> Glyphs(string text, double size)
    {
        var result = new List<Jbig2Bitmap>();
        foreach (char ch in text)
        {
            var ft = new FormattedText(ch.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Times New Roman"), size, Brushes.Black, 1.0);
            int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 2, h = (int)Math.Ceiling(ft.Height) + 2;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h)); dc.DrawText(ft, new Point(1, 1)); }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4] < 128) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            if (maxX < 0) continue; // whitespace: no symbol
            var b = new Jbig2Bitmap(maxX - minX + 1, maxY - minY + 1);
            for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
                b.Px[(y - minY) * b.Width + (x - minX)] = (byte)(px[(y * w + x) * 4] < 128 ? 1 : 0);
            result.Add(b);
        }
        return result;
    }

    private sealed record Instance(int Symbol, int X, int Y, Jbig2Bitmap? Refined = null);

    /// <summary>Symbols in height-class order (as the dictionary stores them).</summary>
    private static List<Jbig2Bitmap> ByHeight(List<Jbig2Bitmap> symbols) => symbols.OrderBy(s => s.Height).ThenBy(s => s.Width).ToList();

    private static byte[] SymbolDictionaryArith(List<Jbig2Bitmap> symbols, int template)
    {
        var at = DefaultAts[template];
        var mq = new E.Mq();
        var iadh = new E.IntEncoder(mq); var iadw = new E.IntEncoder(mq); var iaex = new E.IntEncoder(mq);
        var ctx = Jbig2Regions.NewGenericContexts(template);
        int height = 0;
        foreach (var cls in symbols.GroupBy(s => s.Height).OrderBy(g => g.Key))
        {
            iadh.Encode(cls.Key - height);
            height = cls.Key;
            int width = 0;
            foreach (var sym in cls)
            {
                iadw.Encode(sym.Width - width);
                width = sym.Width;
                E.EncodeGeneric(mq, ctx, sym, template, at, false);
            }
            iadw.Encode(null);
        }
        iaex.Encode(0);
        iaex.Encode(symbols.Count);
        return E.Concat(E.Be16(template << 10), AtBytes(at), E.Be32(symbols.Count), E.Be32(symbols.Count), mq.Flush());
    }

    /// <summary>Encodes instances with the decoder's own S/T bookkeeping (6.4.5) for any corner / transposition.</summary>
    private static (List<(long StripT, List<(Instance Inst, long CurS, long CurT, int Size)> Items)> Strips, long _) Plan(
        List<Instance> instances, List<Jbig2Bitmap> symbols, int strips, int corner, bool transposed)
    {
        const int TopLeft = 1, TopRight = 3, BottomLeft = 0, BottomRight = 2;
        var items = new List<(long T, long S, Instance I, int Size)>();
        foreach (var i in instances)
        {
            var sym = i.Refined ?? symbols[i.Symbol];
            long T, S; int size;
            if (!transposed)
            {
                S = i.X; size = sym.Width;
                T = corner is BottomLeft or BottomRight ? i.Y + sym.Height - 1 : i.Y;
            }
            else
            {
                S = i.Y; size = sym.Height;
                T = corner is TopRight or BottomRight ? i.X + sym.Width - 1 : i.X;
            }
            _ = TopLeft;
            items.Add((T, S, i, size));
        }
        var result = new List<(long, List<(Instance, long, long, int)>)>();
        foreach (var g in items.GroupBy(t => Math.DivRem(t.T, strips, out long r) - (r < 0 ? 1 : 0)).OrderBy(g => g.Key))
            result.Add((g.Key * strips, g.OrderBy(t => t.S).Select(t => (t.I, t.S, t.T - g.Key * strips, t.Size)).ToList()));
        return (result, 0);
    }

    private static byte[] TextRegionArith(int w, int h, List<Instance> instances, List<Jbig2Bitmap> symbols, int logStrips, int corner,
        bool transposed, int dsOffset, int combOp, bool refine, int rTemplate)
    {
        int strips = 1 << logStrips;
        int codeLen = 0; while ((1 << codeLen) < symbols.Count) codeLen++;
        var mq = new E.Mq();
        var dt = new E.IntEncoder(mq); var fs = new E.IntEncoder(mq); var ds = new E.IntEncoder(mq); var it = new E.IntEncoder(mq);
        var ri = new E.IntEncoder(mq); var rdw = new E.IntEncoder(mq); var rdh = new E.IntEncoder(mq); var rdx = new E.IntEncoder(mq); var rdy = new E.IntEncoder(mq);
        var id = new E.IdEncoder(mq, codeLen);
        var gr = Jbig2Regions.NewRefinementContexts(rTemplate);
        var rAt = new (sbyte, sbyte)[] { (-1, -1), (-1, -1) };
        var plan = Plan(instances, symbols, strips, corner, transposed).Strips;
        dt.Encode(0); // STRIPT = 0
        long stripT = 0, firstS = 0;
        foreach (var (st, list) in plan)
        {
            dt.Encode((int)((st - stripT) / strips));
            stripT = st;
            long curS = 0;
            for (int k = 0; k < list.Count; k++)
            {
                var (inst, s, t, size) = list[k];
                if (k == 0) { fs.Encode((int)(s - firstS)); firstS = s; }
                else ds.Encode((int)(s - curS - dsOffset));
                if (strips > 1) it.Encode((int)t);
                id.Encode(inst.Symbol);
                if (refine)
                {
                    ri.Encode(inst.Refined != null ? 1 : 0);
                    if (inst.Refined != null)
                    {
                        var baseSym = symbols[inst.Symbol];
                        int dW = inst.Refined.Width - baseSym.Width, dH = inst.Refined.Height - baseSym.Height;
                        rdw.Encode(dW); rdh.Encode(dH); rdx.Encode(0); rdy.Encode(0);
                        E.EncodeRefinement(mq, gr, inst.Refined, rTemplate, baseSym, dW >> 1, dH >> 1, rAt);
                    }
                }
                curS = s + size - 1;
            }
            ds.Encode(null); // end of strip
        }
        int flags = (refine ? 2 : 0) | logStrips << 2 | corner << 4 | (transposed ? 0x40 : 0) | combOp << 7 | (dsOffset & 0x1F) << 10 | rTemplate << 15;
        var header = E.Concat(E.RegionInfo(w, h, 0, 0, 0), E.Be16(flags));
        if (refine && rTemplate == 0) header = E.Concat(header, AtBytes(rAt));
        return E.Concat(header, E.Be32(instances.Count), mq.Flush());
    }

    private static Jbig2Bitmap Render(int w, int h, List<Instance> instances, List<Jbig2Bitmap> symbols, int op = 0)
    {
        var page = new Jbig2Bitmap(w, h);
        foreach (var i in instances) page.Combine(i.Refined ?? symbols[i.Symbol], i.X, i.Y, op);
        return page;
    }

    private static List<Instance> Layout(List<Jbig2Bitmap> symbols, int w, int h, int seed)
    {
        var rng = new Random(seed);
        var list = new List<Instance>();
        for (int row = 0; row < 4; row++)
        {
            int x = 4 + rng.Next(6);
            while (true)
            {
                int s = rng.Next(symbols.Count);
                if (x + symbols[s].Width >= w - 2) break;
                list.Add(new Instance(s, x, 8 + row * 36 + rng.Next(5)));
                x += symbols[s].Width + 1 + rng.Next(4);
            }
        }
        return list;
    }

    [Theory]
    [InlineData(1, false, 0, 0)] // TOPLEFT
    [InlineData(0, false, 1, 0)] // BOTTOMLEFT, 2 strips
    [InlineData(2, false, 2, 3)] // BOTTOMRIGHT, 4 strips, DS offset
    [InlineData(3, false, 0, -2)] // TOPRIGHT, negative DS offset
    [InlineData(1, true, 0, 0)]  // transposed TOPLEFT
    [InlineData(2, true, 1, 1)]  // transposed BOTTOMRIGHT
    public async Task SymbolDictionary_AndTextRegion_Arithmetic(int corner, bool transposed, int logStrips, int dsOffset)
    {
        var symbols = ByHeight(Glyphs("JBIG2 textregion", 22).Where(g => g.Width > 1).ToList());
        var instances = Layout(symbols, PW, PH, 11 + corner);
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        uint sd = e.Segment(0, SymbolDictionaryArith(symbols, 0));
        e.Segment(6, TextRegionArith(PW, PH, instances, symbols, logStrips, corner, transposed, dsOffset, 0, false, 0), sd);
        e.Segment(49, Array.Empty<byte>());
        AssertSame(Render(PW, PH, instances, symbols), await DecodeAndCompare($"text corner={corner} T={transposed} strips={1 << logStrips}", e.ToArray(), PW, PH));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TextRegion_WithRefinement(int rTemplate)
    {
        var symbols = ByHeight(Glyphs("refine", 24));
        var instances = Layout(symbols, PW, PH, 5);
        var rng = new Random(3);
        for (int i = 0; i < instances.Count; i += 3)
        {
            // Refined copy: one column wider, a few pixels flipped.
            var baseSym = symbols[instances[i].Symbol];
            var r = new Jbig2Bitmap(baseSym.Width + 1, baseSym.Height);
            r.Combine(baseSym, 0, 0, 4);
            for (int k = 0; k < 6; k++) r.Px[rng.Next(r.Px.Length)] ^= 1;
            instances[i] = instances[i] with { Refined = r };
        }
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        uint sd = e.Segment(0, SymbolDictionaryArith(symbols, 1));
        e.Segment(6, TextRegionArith(PW, PH, instances, symbols, 0, 1, false, 0, 0, true, rTemplate), sd);
        e.Segment(49, Array.Empty<byte>());
        AssertSame(Render(PW, PH, instances, symbols), await DecodeAndCompare($"refinement GR{rTemplate}", e.ToArray(), PW, PH));
    }

    /// <summary>Shared integer/ID encoders of one arithmetic text-region procedure.</summary>
    private sealed class TextEncoders
    {
        public readonly E.IntEncoder Dt, Fs, Ds, It, Ri, Rdw, Rdh, Rdx, Rdy;
        public readonly E.IdEncoder Id;
        public readonly byte[] Gr;
        public TextEncoders(E.Mq mq, int codeLen, int rTemplate)
        {
            Dt = new(mq); Fs = new(mq); Ds = new(mq); It = new(mq); Ri = new(mq); Rdw = new(mq); Rdh = new(mq); Rdx = new(mq); Rdy = new(mq);
            Id = new E.IdEncoder(mq, codeLen);
            Gr = Jbig2Regions.NewRefinementContexts(rTemplate);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SymbolDictionary_RefinementAndAggregation(int rTemplate)
    {
        var baseSyms = ByHeight(Glyphs("abcd", 24));
        var rAt = new (sbyte, sbyte)[] { (-1, -1), (-1, -1) };

        // New symbols: a refined 'a' (one instance) and aggregates "ab" and "cd" (several instances).
        var refined = new Jbig2Bitmap(baseSyms[0].Width, baseSyms[0].Height);
        refined.Combine(baseSyms[0], 0, 0, 4);
        var rng = new Random(rTemplate);
        for (int k = 0; k < 8; k++) refined.Px[rng.Next(refined.Px.Length)] ^= 1;
        Jbig2Bitmap Aggregate(int i, int j, out List<Instance> parts)
        {
            var a = baseSyms[i]; var b2 = baseSyms[j];
            int h = Math.Max(a.Height, b2.Height), w = a.Width + 1 + b2.Width;
            parts = new List<Instance> { new(i, 0, h - a.Height), new(j, a.Width + 1, h - b2.Height) };
            var bmp = new Jbig2Bitmap(w, h);
            foreach (var part in parts) bmp.Combine(baseSyms[part.Symbol], part.X, part.Y, 0);
            return bmp;
        }
        var ab = Aggregate(0, 1, out var abParts);
        var cd = Aggregate(2, 3, out var cdParts);
        var newSyms = new List<(Jbig2Bitmap Bmp, int Kind, List<Instance>? Parts)> { (refined, 1, null), (ab, 2, abParts), (cd, 2, cdParts) };
        newSyms = newSyms.OrderBy(n => n.Bmp.Height).ThenBy(n => n.Bmp.Width).ToList();

        int codeLen = 0; while ((1 << codeLen) < baseSyms.Count + newSyms.Count) codeLen++;
        var mq = new E.Mq();
        var iadh = new E.IntEncoder(mq); var iadw = new E.IntEncoder(mq); var iaai = new E.IntEncoder(mq); var iaex = new E.IntEncoder(mq);
        var te = new TextEncoders(mq, codeLen, rTemplate);
        var available = new List<Jbig2Bitmap>(baseSyms);
        int height = 0;
        foreach (var cls in newSyms.GroupBy(n => n.Bmp.Height).OrderBy(g => g.Key))
        {
            iadh.Encode(cls.Key - height);
            height = cls.Key;
            int width = 0;
            foreach (var n in cls)
            {
                iadw.Encode(n.Bmp.Width - width);
                width = n.Bmp.Width;
                if (n.Kind == 1)
                {
                    iaai.Encode(1);
                    te.Id.Encode(0); te.Rdx.Encode(0); te.Rdy.Encode(0);
                    E.EncodeRefinement(mq, te.Gr, n.Bmp, rTemplate, baseSyms[0], 0, 0, rAt);
                }
                else
                {
                    iaai.Encode(n.Parts!.Count);
                    // The aggregate's text region: 1 strip, TOPLEFT, OR, no DS offset, refinement flag coded (0).
                    var plan = Plan(n.Parts, available, 1, 1, false).Strips;
                    te.Dt.Encode(0);
                    long stripT = 0, firstS = 0;
                    foreach (var (st, list) in plan)
                    {
                        te.Dt.Encode((int)(st - stripT)); stripT = st;
                        long curS = 0;
                        for (int k = 0; k < list.Count; k++)
                        {
                            var (inst, sPos, _, size) = list[k];
                            if (k == 0) { te.Fs.Encode((int)(sPos - firstS)); firstS = sPos; } else te.Ds.Encode((int)(sPos - curS));
                            te.Id.Encode(inst.Symbol);
                            te.Ri.Encode(0);
                            curS = sPos + size - 1;
                        }
                        te.Ds.Encode(null);
                    }
                }
                available.Add(n.Bmp);
            }
            iadw.Encode(null);
        }
        iaex.Encode(baseSyms.Count); // input symbols not exported
        iaex.Encode(newSyms.Count);
        int flags = 2 | rTemplate << 12; // SDREFAGG, template 0
        var sd2 = E.Concat(E.Be16(flags), AtBytes(DefaultAts[0]), rTemplate == 0 ? AtBytes(rAt) : Array.Empty<byte>(),
            E.Be32(newSyms.Count), E.Be32(newSyms.Count), mq.Flush());

        var exported = newSyms.Select(n => n.Bmp).ToList();
        var instances = Layout(exported, PW, PH, 17);
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        uint sd1 = e.Segment(0, SymbolDictionaryArith(baseSyms, 0));
        uint sd = e.Segment(0, sd2, sd1);
        e.Segment(6, TextRegionArith(PW, PH, instances, exported, 0, 1, false, 0, 0, false, 0), sd);
        e.Segment(49, Array.Empty<byte>());
        AssertSame(Render(PW, PH, instances, exported), await DecodeAndCompare($"refagg GR{rTemplate}", e.ToArray(), PW, PH));
    }

    [Fact]
    public async Task SymbolDictionaryInGlobals()
    {
        var symbols = ByHeight(Glyphs("globals", 20));
        var instances = Layout(symbols, PW, PH, 9);
        var g = new E();
        uint sd = g.Segment(0, SymbolDictionaryArith(symbols, 2));
        var e = new E(firstNumber: sd + 1); // page-stream numbers continue after the globals (7.2.2)
        e.Segment(48, E.PageInfo(PW, PH));
        e.Segment(6, TextRegionArith(PW, PH, instances, symbols, 0, 1, false, 0, 0, false, 0), sd);
        e.Segment(49, Array.Empty<byte>());
        AssertSame(Render(PW, PH, instances, symbols), await DecodeAndCompare("globals", e.ToArray(), PW, PH, g.ToArray()));
    }

    [Fact]
    public async Task SymbolDictionary_AndTextRegion_Huffman()
    {
        var symbols = ByHeight(Glyphs("Huffman coded", 22).Where(s => s.Width > 1).ToList());
        var instances = Layout(symbols, PW, PH, 21);

        // Symbol dictionary: SDHUFF, DH = B.4, DW = B.2, BMSIZE = B.1 (uncompressed collective bitmaps), export runs B.1.
        var w = new E.BitWriter();
        int height = 0;
        foreach (var cls in symbols.GroupBy(s => s.Height).OrderBy(g => g.Key))
        {
            E.Huffman(w, Jbig2HuffmanTable.Get(4), cls.Key - height);
            height = cls.Key;
            int width = 0, total = 0;
            foreach (var sym in cls) { E.Huffman(w, Jbig2HuffmanTable.Get(2), sym.Width - width); width = sym.Width; total += sym.Width; }
            E.Huffman(w, Jbig2HuffmanTable.Get(2), null);
            E.Huffman(w, Jbig2HuffmanTable.Get(1), 0); // BMSIZE 0: uncompressed
            w.Align();
            var collective = new Jbig2Bitmap(total, height);
            int x0 = 0;
            foreach (var sym in cls) { collective.Combine(sym, x0, 0, 4); x0 += sym.Width; }
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < total; x++) w.Put(collective.Get(x, y), 1);
                w.Align();
            }
        }
        E.Huffman(w, Jbig2HuffmanTable.Get(1), 0);
        E.Huffman(w, Jbig2HuffmanTable.Get(1), symbols.Count);
        var sdData = E.Concat(E.Be16(1), E.Be32(symbols.Count), E.Be32(symbols.Count), w.ToArray());

        // Text region: SBHUFF, FS = B.6, DS = B.8, DT = B.11; symbol IDs with a fixed-length run-code table.
        int codeLen = 1; while ((1 << codeLen) < symbols.Count) codeLen++;
        var t = new E.BitWriter();
        for (int i = 0; i < 35; i++) t.Put(i == codeLen ? 1 : 0, 4); // run code 'codeLen' is the only one: prefix '0'
        for (int i = 0; i < symbols.Count; i++) t.Put(0, 1);
        t.Align();
        var idLines = Enumerable.Range(0, symbols.Count).Select(i => new Jbig2HuffmanTable.Line(codeLen, 0, i)).ToList();
        var idTable = new Jbig2HuffmanTable(idLines);
        var plan = Plan(instances, symbols, 1, 1, false).Strips;
        E.Huffman(t, Jbig2HuffmanTable.Get(11), 1); // STRIPT = -1
        long stripT = -1, firstS = 0;
        foreach (var (st, list) in plan)
        {
            E.Huffman(t, Jbig2HuffmanTable.Get(11), (int)(st - stripT));
            stripT = st;
            long curS = 0;
            for (int k = 0; k < list.Count; k++)
            {
                var (inst, s, _, size) = list[k];
                if (k == 0) { E.Huffman(t, Jbig2HuffmanTable.Get(6), (int)(s - firstS)); firstS = s; }
                else E.Huffman(t, Jbig2HuffmanTable.Get(8), (int)(s - curS));
                E.Huffman(t, idTable, inst.Symbol);
                curS = s + size - 1;
            }
            E.Huffman(t, Jbig2HuffmanTable.Get(8), null);
        }
        var trData = E.Concat(E.RegionInfo(PW, PH, 0, 0, 0), E.Be16(1 | 1 << 4), E.Be16(0), E.Be32(instances.Count), t.ToArray());

        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        uint sd = e.Segment(0, sdData);
        e.Segment(6, trData, sd);
        e.Segment(49, Array.Empty<byte>());
        AssertSame(Render(PW, PH, instances, symbols), await DecodeAndCompare("huffman text", e.ToArray(), PW, PH));
    }

    // ------------------------------------------------------------------ halftone and refinement regions

    [Fact]
    public async Task PatternDictionary_AndHalftoneRegion()
    {
        const int pw = 4, ph = 4, levels = 8, gw = 70, gh = 36;
        var patterns = new List<Jbig2Bitmap>();
        int[] order = { 0, 10, 2, 8, 5, 15, 7, 13, 1, 11, 3, 9, 4, 14, 6, 12 }; // Bayer
        for (int g = 0; g < levels; g++)
        {
            var p = new Jbig2Bitmap(pw, ph);
            for (int i = 0; i < 16; i++) if (order[i] < g * 2) p.Px[i] = 1;
            patterns.Add(p);
        }
        var collective = new Jbig2Bitmap(pw * levels, ph);
        for (int g = 0; g < levels; g++) collective.Combine(patterns[g], g * pw, 0, 4);
        var mq = new E.Mq();
        var pAt = new (sbyte, sbyte)[] { (-pw, 0), (-3, -1), (2, -2), (-2, -2) };
        E.EncodeGeneric(mq, Jbig2Regions.NewGenericContexts(0), collective, 0, pAt, false);
        var pdData = E.Concat(new byte[] { 0, pw, ph }, E.Be32(levels - 1), mq.Flush());

        var gray = new int[gw * gh];
        for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) gray[y * gw + x] = (x * levels / gw + y / 12) % levels;
        const int bpp = 3;
        var planes = new Jbig2Bitmap[bpp];
        for (int j = 0; j < bpp; j++)
        {
            planes[j] = new Jbig2Bitmap(gw, gh);
            for (int i = 0; i < gray.Length; i++) planes[j].Px[i] = (byte)((gray[i] >> j) & 1);
        }
        var hq = new E.Mq();
        var hctx = Jbig2Regions.NewGenericContexts(0);
        var gAt = DefaultAts[0];
        for (int j = bpp - 1; j >= 0; j--)
        {
            // Gray code: the decoder XORs each plane with the next more significant decoded plane.
            var coded = new Jbig2Bitmap(gw, gh);
            for (int i = 0; i < gray.Length; i++) coded.Px[i] = (byte)(planes[j].Px[i] ^ (j + 1 < bpp ? planes[j + 1].Px[i] : 0));
            E.EncodeGeneric(hq, hctx, coded, 0, gAt, false);
        }
        int rw = gw * pw, rh = gh * ph;
        var htData = E.Concat(E.RegionInfo(rw, rh, 0, 0, 0), new byte[] { 0 }, E.Be32(gw), E.Be32(gh), E.Be32(0), E.Be32(0),
            E.Be16(pw * 256), E.Be16(0), hq.Flush());

        var e = new E();
        e.Segment(48, E.PageInfo(rw, rh));
        uint pd = e.Segment(16, pdData);
        e.Segment(22, htData, pd);
        e.Segment(49, Array.Empty<byte>());
        var expected = new Jbig2Bitmap(rw, rh);
        for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) expected.Combine(patterns[gray[y * gw + x]], x * pw, y * ph, 0);
        AssertSame(expected, await DecodeAndCompare("halftone", e.ToArray(), rw, rh));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task RefinementRegion_OfThePage(int template, bool tpgron)
    {
        var page = SourcePage();
        var refined = SourcePage();
        var rng = new Random(template * 2 + (tpgron ? 1 : 0));
        for (int k = 0; k < 400; k++) refined.Px[rng.Next(refined.Px.Length)] ^= 1;
        var at = new (sbyte, sbyte)[] { (-1, -1), (-1, -1) };
        var mq = new E.Mq();
        var ctx = Jbig2Regions.NewRefinementContexts(template);
        if (!tpgron)
        {
            E.EncodeRefinement(mq, ctx, refined, template, page, 0, 0, at);
        }
        else
        {
            // Typical prediction (6.3.5.6): rows where every uniform-neighbourhood pixel equals the
            // reference are coded with LTP = 1 and skip those pixels.
            int sltp = template == 0 ? 0x100 : 0x80;
            bool ltp = false;
            for (int y = 0; y < refined.Height; y++)
            {
                bool ok = true;
                for (int x = 0; x < refined.Width && ok; x++)
                    if (Uniform(page, x, y, out int v) && refined.Get(x, y) != v) ok = false;
                mq.Encode(ctx, sltp, ok != ltp ? 1 : 0);
                ltp = ok;
                for (int x = 0; x < refined.Width; x++)
                {
                    if (ltp && Uniform(page, x, y, out _)) continue;
                    int rx = x, ry = y;
                    int cx = template == 0
                        ? refined.Get(x - 1, y) | refined.Get(x + 1, y - 1) << 1 | refined.Get(x, y - 1) << 2 | refined.Get(x - 1, y - 1) << 3
                          | page.Get(rx + 1, ry + 1) << 4 | page.Get(rx, ry + 1) << 5 | page.Get(rx - 1, ry + 1) << 6
                          | page.Get(rx + 1, ry) << 7 | page.Get(rx, ry) << 8 | page.Get(rx - 1, ry) << 9
                          | page.Get(rx + 1, ry - 1) << 10 | page.Get(rx, ry - 1) << 11 | page.Get(rx - 1, ry - 1) << 12
                        : refined.Get(x - 1, y) | refined.Get(x + 1, y - 1) << 1 | refined.Get(x, y - 1) << 2 | refined.Get(x - 1, y - 1) << 3
                          | page.Get(rx + 1, ry + 1) << 4 | page.Get(rx, ry + 1) << 5
                          | page.Get(rx + 1, ry) << 6 | page.Get(rx, ry) << 7 | page.Get(rx - 1, ry) << 8
                          | page.Get(rx, ry - 1) << 9;
                    mq.Encode(ctx, cx, refined.Get(x, y));
                }
            }
        }
        var flags = (byte)(template | (tpgron ? 2 : 0));
        var data = E.Concat(E.RegionInfo(PW, PH, 0, 0, 4 /* REPLACE */), new[] { flags }, template == 0 ? AtBytes(at) : Array.Empty<byte>(), mq.Flush());
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        e.Segment(39, GenericSegmentData(page, 0, false, DefaultAts[0]));
        e.Segment(43, data);
        e.Segment(49, Array.Empty<byte>());
        AssertSame(refined, await DecodeAndCompare($"refinement region GR{template} tpgron={tpgron}", e.ToArray(), PW, PH));
    }

    private static bool Uniform(Jbig2Bitmap r, int x, int y, out int v)
    {
        v = r.Get(x, y);
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) if (r.Get(x + i, y + j) != v) return false;
        return true;
    }

    [Fact]
    public void StandardTables_MatchAnnexBPrefixCodes()
    {
        // Explicit prefix codes printed in T.88 Annex B (as reproduced by pdf.js) for a sample of lines.
        void Check(int table, int rangeLow, int code, int length, bool lower = false)
        {
            var line = Jbig2HuffmanTable.Get(table).Codes.Single(c => c.Line.RangeLow == rangeLow && c.Line.IsLower == lower && !c.Line.IsOob);
            Assert.Equal(length, line.Line.PrefixLength);
            Assert.Equal(code, line.Code);
        }
        Check(3, -256, 0xFE, 8); Check(3, -257, 0xFF, 8, lower: true); Check(3, 75, 0x7E, 7);
        Check(6, -2048, 0x1C, 5); Check(6, 0, 0x0, 2); Check(6, -2049, 0x3E, 6, lower: true); Check(6, 2048, 0x3F, 6);
        Check(7, -512, 0x0, 3); Check(7, 1024, 0x3, 3); Check(7, -1025, 0x1E, 5, lower: true);
        Check(8, -7, 0x1FC, 9); Check(8, 646, 0x3D, 6); Check(8, -16, 0x1FE, 9, lower: true); Check(8, 1670, 0x1FF, 9);
        Check(9, -15, 0x1FC, 9); Check(9, 1291, 0x3D, 6);
        Check(10, -21, 0x7A, 7); Check(10, 6, 0x1, 2); Check(10, 4166, 0xFF, 8);
        Check(11, 141, 0x7F, 7); Check(12, 41, 0xFE, 8); Check(13, 7, 0x5, 3); Check(14, 2, 0x7, 3); Check(15, -25, 0x7E, 7, lower: true);
        Assert.Equal(0x1, Jbig2HuffmanTable.Get(8).Codes.Single(c => c.Line.IsOob).Code);
        Assert.Equal(0x0, Jbig2HuffmanTable.Get(9).Codes.Single(c => c.Line.IsOob).Code);
        Assert.Equal(0x2, Jbig2HuffmanTable.Get(10).Codes.Single(c => c.Line.IsOob).Code);
    }

    [Fact]
    public void MutatedStreams_FailOnlyWithClassifiedErrors()
    {
        var symbols = ByHeight(Glyphs("fuzz", 18));
        var instances = Layout(symbols, PW, PH, 1);
        var e = new E();
        e.Segment(48, E.PageInfo(PW, PH));
        uint sd = e.Segment(0, SymbolDictionaryArith(symbols, 0));
        e.Segment(6, TextRegionArith(PW, PH, instances, symbols, 0, 1, false, 0, 0, false, 0), sd);
        e.Segment(49, Array.Empty<byte>());
        var valid = e.ToArray();
        var rng = new Random(42);
        for (int i = 0; i < 300; i++)
        {
            var bytes = (byte[])valid.Clone();
            for (int f = 0; f < 1 + rng.Next(6); f++) bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
            if (rng.Next(4) == 0) Array.Resize(ref bytes, rng.Next(1, bytes.Length));
            try { Jbig2Decoder.Decode(bytes, null, PW, PH); }
            catch (PdfEngine.Vector.Diagnostics.PdfVectorException) { }
        }
    }
}
