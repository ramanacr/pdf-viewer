using System;
using System.IO;
using System.Xml;

namespace PdfEngine.Vector.Export;

/// <summary>
/// The .docx package: WordprocessingML parts in an <see cref="OpcPackage"/>, with the
/// document's relationships. Images are written as they are added, so they are not all held in memory.
/// </summary>
internal sealed class DocxPackage : IExportMedia, IDisposable
{
    internal const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string R = OpcPackage.R;
    internal const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    internal const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    internal const string Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private const string ContentBase = "application/vnd.openxmlformats-officedocument.wordprocessingml.";
    private const string Document = "word/document.xml";

    private readonly OpcPackage _opc;
    private int _images;

    public DocxPackage(Stream output)
    {
        _opc = new OpcPackage(output);
    }

    public string NewRelationship(string type, string target, bool external = false) => _opc.AddRelationship(Document, type, target, external);

    public bool WantsImages => true;

    /// <summary>Adds an image part (png or jpeg); returns its relationship id from the document.</summary>
    public string AddImage(byte[] data, string extension)
    {
        string name = $"media/image{++_images}.{extension}";
        _opc.WriteMedia("word/" + name, data);
        return NewRelationship("image", name);
    }

    public int ImageCount => _images;

    /// <summary>Writes an XML part of the document (under word/).</summary>
    public void WritePart(string name, string contentType, Action<XmlWriter> write) => _opc.WritePart(name, contentType, write);

    public static string WordType(string part) => ContentBase + part + "+xml";

    /// <summary>Writes the package's own parts: content types, relationships and properties.</summary>
    public void Finish(string? title) => _opc.Finish(title, Document);

    public void Dispose() => _opc.Dispose();
}
