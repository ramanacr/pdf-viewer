using System;
using System.IO;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// Provides bounded random-access byte reading over PDF data sources with 64-bit offsets.
/// </summary>
public interface IPdfByteSource : IDisposable
{
    long Length { get; }
    long Position { get; set; }

    int ReadByte();
    int Read(Span<byte> destination);
    ReadOnlyMemory<byte> ReadMemory(long offset, int length);
    IPdfByteSource Slice(long offset, long length);
    void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin);
}
