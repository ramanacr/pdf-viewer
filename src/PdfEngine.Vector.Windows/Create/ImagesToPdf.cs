using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PdfEngine.Vector.Create;

namespace PdfEngine.Vector.Windows.Create;

/// <summary>An image to put on pages: a name for messages, and how to open its bytes.</summary>
public sealed record ImageInput(string Name, Func<Stream> Open)
{
    public static ImageInput FromFile(string path) =>
        new(Path.GetFileName(path), () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    public static ImageInput FromBytes(string name, byte[] bytes) => new(name, () => new MemoryStream(bytes, writable: false));
}

public sealed record ImagesToPdfOptions
{
    public PdfImagePageOptions Page { get; init; } = new();
    public ImageConversionOptions Image { get; init; } = new();
    public PdfCreateInfo? Info { get; init; }
}

/// <summary>How far a conversion is: images done of all, and the pages written.</summary>
public readonly record struct ImagesToPdfProgress(int Images, int TotalImages, int Pages);

public sealed record ImagesToPdfResult(int Pages, int JpegPassThrough, int JpegReencoded, int Lossless);

/// <summary>An image could not be read; its name says which.</summary>
public sealed class ImageInputException(string name, string message, Exception inner) : IOException($"'{name}': {message}", inner)
{
    public string Name { get; } = name;
}

/// <summary>
/// Images to a new PDF, a page an image (a page a frame for multi-page TIFF), written as it goes:
/// one image is decoded and written before the next is opened.
/// </summary>
public static class ImagesToPdf
{
    public static ImagesToPdfResult Write(IReadOnlyList<ImageInput> inputs, Stream output, ImagesToPdfOptions? options = null,
        IProgress<ImagesToPdfProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new ImagesToPdfOptions();
        if (inputs.Count == 0) throw new ArgumentException("Choose at least one image.", nameof(inputs));
        using var writer = new PdfDocumentWriter(output);
        int passThrough = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = inputs[i];
            Stream stream;
            try
            {
                stream = input.Open();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ImageInputException(input.Name, "the file could not be opened.", ex);
            }
            using (stream)
            {
                using var frames = WicImageReader.ReadFrames(stream, options.Image).GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PdfCreateImage image;
                    try
                    {
                        if (!frames.MoveNext()) break;
                        image = frames.Current;
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new ImageInputException(input.Name, ex.Message, ex);
                    }
                    writer.AddImagePage(image, options.Page);
                    if (image.Unchanged) passThrough++;
                    progress?.Report(new ImagesToPdfProgress(i, inputs.Count, writer.PageCount));
                }
            }
            progress?.Report(new ImagesToPdfProgress(i + 1, inputs.Count, writer.PageCount));
        }
        writer.Finish(options.Info);
        return new ImagesToPdfResult(writer.PageCount, passThrough, writer.JpegImages - passThrough, writer.LosslessImages);
    }

    /// <summary>The same, into memory.</summary>
    public static byte[] ToBytes(IReadOnlyList<ImageInput> inputs, ImagesToPdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        Write(inputs, ms, options, null, cancellationToken);
        return ms.ToArray();
    }
}
