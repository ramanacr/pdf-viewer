using System.Buffers.Binary;
using System.IO.Compression;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// TIFF and BMP page pictures (the Android app's Export pages as images, as Windows offers them):
/// read back here, they hold the same pixels as RGB, the right size and the resolution.
/// </summary>
public class ViewerImageEncoderTests
{
    /// <summary>A 3 × 2 BGRA picture with a distinct colour in each pixel.</summary>
    private static byte[] Picture(out (byte R, byte G, byte B)[] rgb)
    {
        rgb = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (10, 20, 30), (200, 100, 50), (255, 255, 255)];
        var bgra = new byte[rgb.Length * 4];
        for (int i = 0; i < rgb.Length; i++)
        {
            bgra[4 * i] = rgb[i].B;
            bgra[4 * i + 1] = rgb[i].G;
            bgra[4 * i + 2] = rgb[i].R;
            bgra[4 * i + 3] = 255;
        }
        return bgra;
    }

    [Fact]
    public void Bmp_IsBottomUp24Bit_WithItsResolution()
    {
        var bgra = Picture(out var rgb);
        var bmp = ViewerImageEncoders.Bmp(bgra, 3, 2, 300);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(bmp.AsSpan(28)));
        Assert.Equal(11811, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(38))); // 300 dpi in pixels per metre
        const int stride = 12; // 9 bytes padded to 12
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
            {
                int o = 54 + (1 - y) * stride + 3 * x;
                var (r, g, b) = rgb[y * 3 + x];
                Assert.Equal((b, g, r), (bmp[o], bmp[o + 1], bmp[o + 2]));
            }
    }

    [Fact]
    public void Tiff_IsDeflatedRgb_WithItsSizeAndResolution()
    {
        var bgra = Picture(out var rgb);
        var tiff = ViewerImageEncoders.Tiff(bgra, 3, 2, 600);
        Assert.Equal("II", System.Text.Encoding.ASCII.GetString(tiff, 0, 2));
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(2)));
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        Assert.Equal(0, ifd % 2);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        var tags = new Dictionary<int, (int Type, uint Value)>();
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            var e = tiff.AsSpan(ifd + 2 + 12 * i, 12);
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(e);
            Assert.True(tag > previous); // ascending, as TIFF 6.0 asks
            previous = tag;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            uint value = type == 3 && BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) == 1
                ? BinaryPrimitives.ReadUInt16LittleEndian(e[8..])
                : BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            tags[tag] = (type, value);
        }
        Assert.Equal(3u, tags[256].Value);
        Assert.Equal(2u, tags[257].Value);
        Assert.Equal(8u, tags[259].Value);
        Assert.Equal(2u, tags[262].Value);
        Assert.Equal(3u, tags[277].Value);
        Assert.Equal(600u, BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)tags[282].Value)));
        Assert.Equal(2u, tags[296].Value);

        using var z = new ZLibStream(new MemoryStream(tiff, (int)tags[273].Value, (int)tags[279].Value), CompressionMode.Decompress);
        var pixels = new byte[18];
        z.ReadExactly(pixels);
        for (int i = 0; i < rgb.Length; i++)
            Assert.Equal(rgb[i], (pixels[3 * i], pixels[3 * i + 1], pixels[3 * i + 2]));
    }
}
