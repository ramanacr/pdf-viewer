using System;

namespace PdfEngine.Vector.Images.Jbig2;

/// <summary>JBIG2 data could not be decoded (malformed or unsupported); classified by the caller.</summary>
internal sealed class Jbig2Exception : Exception
{
    public Jbig2Exception(string message) : base(message) { }
}

/// <summary>A bilevel bitmap, one byte per pixel (1 = black); reads outside it are 0 (T.88 6.2.5.2).</summary>
internal sealed class Jbig2Bitmap
{
    public readonly int Width, Height;
    public readonly byte[] Px;

    public Jbig2Bitmap(int width, int height, byte fill = 0)
    {
        if (width < 0 || height < 0 || (long)width * height > 1L << 30)
            throw new Jbig2Exception("JBIG2 bitmap is too large.");
        Width = width;
        Height = height;
        Px = new byte[width * height];
        if (fill != 0) Array.Fill(Px, fill);
    }

    public int Get(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Px[y * Width + x] : 0;

    /// <summary>Combines <paramref name="src"/> at (x, y) with a T.88 combination operator (0 OR, 1 AND, 2 XOR, 3 XNOR, 4 REPLACE).</summary>
    public void Combine(Jbig2Bitmap src, int x, int y, int op)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = (int)Math.Min((long)Width, (long)x + src.Width), y1 = (int)Math.Min((long)Height, (long)y + src.Height);
        for (int yy = y0; yy < y1; yy++)
        {
            int d = yy * Width, s = (yy - y) * src.Width - x;
            for (int xx = x0; xx < x1; xx++)
            {
                byte v = src.Px[s + xx];
                ref byte t = ref Px[d + xx];
                t = op switch
                {
                    0 => (byte)(t | v),
                    1 => (byte)(t & v),
                    2 => (byte)(t ^ v),
                    3 => (byte)(1 - (t ^ v)),
                    _ => v,
                };
            }
        }
    }
}

/// <summary>The MQ arithmetic decoder of T.88 Annex E (INITDEC, DECODE, BYTEIN, RENORMD).</summary>
internal sealed class MqDecoder
{
    private static readonly (ushort Qe, byte Nmps, byte Nlps, bool Switch)[] Table =
    {
        (0x5601, 1, 1, true), (0x3401, 2, 6, false), (0x1801, 3, 9, false), (0x0AC1, 4, 12, false),
        (0x0521, 5, 29, false), (0x0221, 38, 33, false), (0x5601, 7, 6, true), (0x5401, 8, 14, false),
        (0x4801, 9, 14, false), (0x3801, 10, 14, false), (0x3001, 11, 17, false), (0x2401, 12, 18, false),
        (0x1C01, 13, 20, false), (0x1601, 29, 21, false), (0x5601, 15, 14, true), (0x5401, 16, 14, false),
        (0x5101, 17, 15, false), (0x4801, 18, 16, false), (0x3801, 19, 17, false), (0x3401, 20, 18, false),
        (0x3001, 21, 19, false), (0x2801, 22, 19, false), (0x2401, 23, 20, false), (0x2201, 24, 21, false),
        (0x1C01, 25, 22, false), (0x1801, 26, 23, false), (0x1601, 27, 24, false), (0x1401, 28, 25, false),
        (0x1201, 29, 26, false), (0x1101, 30, 27, false), (0x0AC1, 31, 28, false), (0x09C1, 32, 29, false),
        (0x08A1, 33, 30, false), (0x0521, 34, 31, false), (0x0441, 35, 32, false), (0x02A1, 36, 33, false),
        (0x0221, 37, 34, false), (0x0141, 38, 35, false), (0x0111, 39, 36, false), (0x0085, 40, 37, false),
        (0x0049, 41, 38, false), (0x0025, 42, 39, false), (0x0015, 43, 40, false), (0x0009, 44, 41, false),
        (0x0005, 45, 42, false), (0x0001, 45, 43, false), (0x5601, 46, 46, false),
    };

    private readonly byte[] _data;
    private readonly int _end;
    private int _bp;
    private uint _chigh, _clow, _a;
    private int _ct;

    public MqDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _end = Math.Min(end, data.Length);
        _bp = start;
        _chigh = start < _end ? data[start] : 0xFFu;
        _clow = 0;
        ByteIn();
        _chigh = ((_chigh << 7) & 0xFFFF) | ((_clow >> 9) & 0x7F);
        _clow = (_clow << 7) & 0xFFFF;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>Bytes consumed so far (for callers that continue after the arithmetic data).</summary>
    public int Position => _bp;

    private byte At(int i) => i < _end ? _data[i] : (byte)0xFF;

    private void ByteIn()
    {
        if (At(_bp) == 0xFF)
        {
            if (At(_bp + 1) > 0x8F)
            {
                _clow += 0xFF00;
                _ct = 8;
            }
            else
            {
                _bp++;
                _clow += (uint)At(_bp) << 9;
                _ct = 7;
            }
        }
        else
        {
            _bp++;
            _clow += _bp < _end ? (uint)_data[_bp] << 8 : 0xFF00;
            _ct = 8;
        }
        if (_clow > 0xFFFF)
        {
            _chigh += _clow >> 16;
            _clow &= 0xFFFF;
        }
    }

    /// <summary>Decodes one bit with context <paramref name="cx"/> of <paramref name="contexts"/> ((index &lt;&lt; 1) | mps).</summary>
    public int Decode(byte[] contexts, int cx)
    {
        int index = contexts[cx] >> 1, mps = contexts[cx] & 1;
        var e = Table[index];
        uint qe = e.Qe;
        uint a = _a - qe;
        int d;
        if (_chigh < qe)
        {
            // LPS exchange
            if (a < qe)
            {
                a = qe;
                d = mps;
                index = e.Nmps;
            }
            else
            {
                a = qe;
                d = 1 ^ mps;
                if (e.Switch) mps = d;
                index = e.Nlps;
            }
        }
        else
        {
            _chigh -= qe;
            if ((a & 0x8000) != 0)
            {
                _a = a;
                return mps;
            }
            // MPS exchange
            if (a < qe)
            {
                d = 1 ^ mps;
                if (e.Switch) mps = d;
                index = e.Nlps;
            }
            else
            {
                d = mps;
                index = e.Nmps;
            }
        }
        do
        {
            if (_ct == 0) ByteIn();
            a <<= 1;
            _chigh = ((_chigh << 1) & 0xFFFF) | ((_clow >> 15) & 1);
            _clow = (_clow << 1) & 0xFFFF;
            _ct--;
        }
        while ((a & 0x8000) == 0);
        _a = a;
        contexts[cx] = (byte)((index << 1) | mps);
        return d;
    }
}

/// <summary>Integer (IAx, Annex A.2) and symbol-ID (IAID, A.3) arithmetic decoding procedures.</summary>
internal sealed class Jbig2IntegerDecoder
{
    private readonly MqDecoder _mq;
    private readonly byte[] _ctx = new byte[512];

    public Jbig2IntegerDecoder(MqDecoder mq) => _mq = mq;

    /// <summary>The value, or null for OOB.</summary>
    public int? Decode()
    {
        int prev = 1;
        int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int bit = _mq.Decode(_ctx, prev);
                prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                v = (v << 1) | bit;
            }
            return v;
        }
        int sign = Bits(1);
        long value = Bits(1) == 0 ? Bits(2)
            : Bits(1) == 0 ? Bits(4) + 4
            : Bits(1) == 0 ? Bits(6) + 20
            : Bits(1) == 0 ? Bits(8) + 84
            : Bits(1) == 0 ? Bits(12) + 340
            : (uint)Bits(32) + 4436L;
        if (sign == 0) return (int)Math.Min(value, int.MaxValue);
        if (value > 0) return (int)-Math.Min(value, int.MaxValue);
        return null;
    }

    public int DecodeRequired(string what) => Decode() ?? throw new Jbig2Exception($"JBIG2 {what} is out of band.");
}

internal sealed class Jbig2IdDecoder
{
    private readonly MqDecoder _mq;
    private readonly int _codeLength;
    private readonly byte[] _ctx;

    public Jbig2IdDecoder(MqDecoder mq, int codeLength)
    {
        _mq = mq;
        _codeLength = Math.Clamp(codeLength, 0, 30);
        _ctx = new byte[1 << (_codeLength + 1)];
    }

    public int Decode()
    {
        int prev = 1;
        for (int i = 0; i < _codeLength; i++)
            prev = (prev << 1) | _mq.Decode(_ctx, prev);
        return prev - (1 << _codeLength);
    }
}
