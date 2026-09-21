namespace PdfEngine.Vector.Xref;

/// <summary>
/// Cross-reference entry indicating the physical location of an indirect object in a PDF file.
/// Supports both classic uncompressed offsets and object-stream compressed references.
/// </summary>
public readonly record struct PdfXrefEntry(
    int ObjectNumber,
    int GenerationNumber,
    long ByteOffset,
    bool IsInUse,
    int CompressedStreamObjectNumber = 0,
    int CompressedStreamIndex = 0)
{
    public bool IsCompressed => CompressedStreamObjectNumber > 0;
}
