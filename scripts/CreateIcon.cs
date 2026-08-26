using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class IconBuilder
{
    public static void BuildIcon(string[]? args = null)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        string rootDir = baseDir;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx")))
            {
                rootDir = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        string assetsDir = Path.Combine(rootDir, "assets");
        Directory.CreateDirectory(assetsDir);

        string appRaw = Path.Combine(assetsDir, "app_icon_raw.png");
        string pdfRaw = Path.Combine(assetsDir, "pdf_file_raw.png");

        if (File.Exists(appRaw))
        {
            ConvertPngToMultiResIco(appRaw, Path.Combine(assetsDir, "app_icon.ico"), Path.Combine(assetsDir, "app_icon.png"));
        }

        if (File.Exists(pdfRaw))
        {
            ConvertPngToMultiResIco(pdfRaw, Path.Combine(assetsDir, "pdf_file.ico"), Path.Combine(assetsDir, "pdf_file.png"));
        }

        // Copy to project asset directories
        string[] targetDirs =
        {
            Path.Combine(rootDir, "src", "PdfViewer", "assets"),
            Path.Combine(rootDir, "src", "Installer", "assets")
        };

        foreach (var tDir in targetDirs)
        {
            Directory.CreateDirectory(tDir);
            foreach (var file in new[] { "app_icon.ico", "app_icon.png", "pdf_file.ico", "pdf_file.png", "app_icon_raw.png", "pdf_file_raw.png" })
            {
                string src = Path.Combine(assetsDir, file);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(tDir, file), overwrite: true);
                }
            }
        }
    }

    public static void ConvertPngToMultiResIco(string inputPng, string outputIco, string outputPng)
    {
        Console.WriteLine($"Converting {inputPng} to multi-resolution ICO -> {outputIco}");

        var srcUri = new Uri(inputPng, UriKind.Absolute);
        var originalBmp = new BitmapImage();
        originalBmp.BeginInit();
        originalBmp.UriSource = srcUri;
        originalBmp.CacheOption = BitmapCacheOption.OnLoad;
        originalBmp.EndInit();
        originalBmp.Freeze();

        var writeableBmp = new FormatConvertedBitmap(originalBmp, PixelFormats.Bgra32, null, 0);
        writeableBmp.Freeze();

        int width = writeableBmp.PixelWidth;
        int height = writeableBmp.PixelHeight;

        // Save master PNG if path is different
        if (!string.Equals(Path.GetFullPath(inputPng), Path.GetFullPath(outputPng), StringComparison.OrdinalIgnoreCase))
        {
            using var pngStream = new FileStream(outputPng, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(writeableBmp));
            encoder.Save(pngStream);
        }

        // Build multi-resolution ICO: 256, 128, 64, 48, 32, 16
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        var pngDataList = new List<byte[]>();

        foreach (var size in sizes)
        {
            var resized = new TransformedBitmap(writeableBmp, new ScaleTransform((double)size / width, (double)size / height));
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

        Console.WriteLine($"Generated ICO successfully: {outputIco}");
    }
}
