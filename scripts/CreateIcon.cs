using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class IconBuilder
{
    public static void BuildIcon(string[] args)
    {
        string inputPath = args.Length > 0 ? args[0] : @"C:\Users\ramanareddy\.gemini\antigravity\brain\9f87780e-135d-4888-9e66-56fa588be862\pdf_viewer_icon_1787752279772.jpg";
        string outputIco = args.Length > 1 ? args[1] : @"d:\Practice\pdf-viewer\assets\app_icon.ico";
        string outputPng = args.Length > 2 ? args[2] : @"d:\Practice\pdf-viewer\assets\app_icon.png";

        Console.WriteLine($"Loading source image: {inputPath}");

        var srcUri = new Uri(inputPath, UriKind.Absolute);
        var originalBmp = new BitmapImage(srcUri);
        var writeableBmp = new FormatConvertedBitmap(originalBmp, PixelFormats.Bgra32, null, 0);

        int width = writeableBmp.PixelWidth;
        int height = writeableBmp.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        writeableBmp.CopyPixels(pixels, stride, 0);

        // Make pure white background transparent (flood fill from borders or color key threshold)
        // Background in the rendered image is near white (>245 in R,G,B)
        // Let's make outside white pixels transparent while preserving smooth edges
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * stride + x * 4;
                byte b = pixels[idx];
                byte g = pixels[idx + 1];
                byte r = pixels[idx + 2];

                // If close to white background
                if (r > 240 && g > 240 && b > 240)
                {
                    // Compute distance from pure white for smooth anti-aliased edge
                    int minVal = Math.Min(r, Math.Min(g, b));
                    if (minVal >= 250)
                    {
                        pixels[idx + 3] = 0; // completely transparent
                    }
                    else
                    {
                        // Semi-transparent fade
                        double factor = (255 - minVal) / 15.0;
                        pixels[idx + 3] = (byte)(Math.Clamp(factor, 0.0, 1.0) * 255);
                    }
                }
            }
        }

        var transparentBmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        transparentBmp.Freeze();

        // Save high-res PNG
        Directory.CreateDirectory(Path.GetDirectoryName(outputPng)!);
        using (var pngStream = new FileStream(outputPng, FileMode.Create))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transparentBmp));
            encoder.Save(pngStream);
        }
        Console.WriteLine($"Saved transparent PNG to {outputPng}");

        // Build multi-resolution ICO: 256, 128, 64, 48, 32, 16
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        var pngDataList = new List<byte[]>();

        foreach (var size in sizes)
        {
            var resized = new TransformedBitmap(transparentBmp, new ScaleTransform((double)size / width, (double)size / height));
            resized.Freeze();

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(resized));
            encoder.Save(ms);
            pngDataList.Add(ms.ToArray());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputIco)!);
        using (var icoStream = new FileStream(outputIco, FileMode.Create))
        using (var writer = new BinaryWriter(icoStream))
        {
            // ICONDIR header
            writer.Write((ushort)0); // Reserved
            writer.Write((ushort)1); // 1 = ICO
            writer.Write((ushort)sizes.Length); // Image count

            int offset = 6 + (16 * sizes.Length);

            // ICONDIRENTRY array
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                byte bSize = (byte)(size >= 256 ? 0 : size);
                byte[] data = pngDataList[i];

                writer.Write(bSize);        // Width
                writer.Write(bSize);        // Height
                writer.Write((byte)0);      // Colors
                writer.Write((byte)0);      // Reserved
                writer.Write((ushort)1);    // Color planes
                writer.Write((ushort)32);   // Bits per pixel
                writer.Write((uint)data.Length); // Data length
                writer.Write((uint)offset);      // File offset

                offset += data.Length;
            }

            // Write PNG payloads
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write(pngDataList[i]);
            }
        }

        Console.WriteLine($"Saved multi-res ICO to {outputIco}");
    }
}
