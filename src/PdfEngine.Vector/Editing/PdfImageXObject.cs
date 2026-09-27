using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Redaction;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// An image XObject (ISO 32000-2 8.9.5) for a picture: a JPEG embedded as it is (DCTDecode), raw
/// gray or RGB samples Flate-compressed, and any transparency as a soft mask (/SMask, 11.6.5.3)
/// so the picture blends with what is under it.
/// </summary>
internal static class PdfImageXObject
{
    /// <summary>Adds the image (and its soft mask) to <paramref name="objects"/>; returns the image's object number.</summary>
    public static int Write(PdfImageContent image, Func<int> allocate, IDictionary<int, PdfObject> objects, bool interpolate = false)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentException("The image has no pixels.");
        var dict = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image"),
            ["Width"] = new PdfInteger(image.Width), ["Height"] = new PdfInteger(image.Height), ["BitsPerComponent"] = new PdfInteger(8),
        };
        byte[] data;
        switch (image.Encoding)
        {
            case PdfImageEncoding.Jpeg:
                dict["ColorSpace"] = new PdfName(image.Components == 1 ? "DeviceGray" : "DeviceRGB");
                dict["Filter"] = new PdfName("DCTDecode");
                data = image.Data;
                break;
            case PdfImageEncoding.Gray:
                if (image.Data.Length < (long)image.Width * image.Height) throw new ArgumentException("The image data is too short.");
                dict["ColorSpace"] = new PdfName("DeviceGray");
                dict["Filter"] = new PdfName("FlateDecode");
                data = ContentRedactor.Deflate(image.Data);
                break;
            default:
                if (image.Data.Length < (long)image.Width * image.Height * 3) throw new ArgumentException("The image data is too short.");
                dict["ColorSpace"] = new PdfName("DeviceRGB");
                dict["Filter"] = new PdfName("FlateDecode");
                data = ContentRedactor.Deflate(image.Data);
                break;
        }
        if (interpolate) dict["Interpolate"] = PdfBoolean.True;
        if (image.Alpha is { } alpha && alpha.Any(a => a != 255))
        {
            if (alpha.Length < (long)image.Width * image.Height) throw new ArgumentException("The image's transparency data is too short.");
            int mask = allocate();
            var maskDict = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image"), ["Width"] = new PdfInteger(image.Width), ["Height"] = new PdfInteger(image.Height),
                ["BitsPerComponent"] = new PdfInteger(8), ["ColorSpace"] = new PdfName("DeviceGray"), ["Filter"] = new PdfName("FlateDecode"),
            };
            if (interpolate) maskDict["Interpolate"] = PdfBoolean.True;
            objects[mask] = PdfObjectWriter.NewStream(maskDict, ContentRedactor.Deflate(alpha));
            dict["SMask"] = new PdfIndirectRef(mask);
        }
        int number = allocate();
        objects[number] = PdfObjectWriter.NewStream(dict, data);
        return number;
    }
}
