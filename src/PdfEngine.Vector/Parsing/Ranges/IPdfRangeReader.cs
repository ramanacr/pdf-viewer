using System;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Diagnostics;

namespace PdfEngine.Vector.Parsing;

/// <summary>A range of bytes of a file: from <see cref="Start"/> (inclusive) to <see cref="End"/> (exclusive).</summary>
public readonly record struct PdfByteRange(long Start, long End)
{
    /// <summary>The number of bytes in the range.</summary>
    public long Length => End - Start;

    /// <summary>True when the range has no bytes.</summary>
    public bool IsEmpty => End <= Start;

    /// <inheritdoc/>
    public override string ToString() => $"[{Start}, {End})";
}

/// <summary>
/// Random access to a PDF file that is not in memory, for example a file on a web server that
/// answers HTTP range requests. The engine asks only for the ranges it needs (the cross-reference
/// data at the end, then the objects of the pages it draws); see <see cref="PdfRangedDocument"/>.
/// </summary>
public interface IPdfRangeReader
{
    /// <summary>The length of the file in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Reads exactly <c>destination.Length</c> bytes from <paramref name="offset"/>. The range is
    /// always inside the file. Throws when the bytes cannot be read.
    /// </summary>
    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IPdfRangeReader"/> over bytes in memory, for tests and for hosts that already have the file.</summary>
public sealed class PdfMemoryRangeReader : IPdfRangeReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private long _bytesRead;
    private int _requests;

    public PdfMemoryRangeReader(ReadOnlyMemory<byte> data) => _data = data;

    /// <inheritdoc/>
    public long Length => _data.Length;

    /// <summary>The bytes read so far.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>The reads so far.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Called before each read (a test can delay or fail it).</summary>
    public Func<long, int, ValueTask>? BeforeRead { get; init; }

    /// <inheritdoc/>
    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > _data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeRead != null) await BeforeRead(offset, destination.Length).ConfigureAwait(false);
        _data.Slice((int)offset, destination.Length).CopyTo(destination);
        Interlocked.Add(ref _bytesRead, destination.Length);
        Interlocked.Increment(ref _requests);
    }
}

/// <summary>
/// The parser needed bytes that are not loaded yet (a <see cref="PdfChunkedByteSource"/> over a
/// partly fetched file). <see cref="PdfRangedDocument"/> catches it, fetches the range and tries
/// again. It is not a syntax error: the recovery paths of the parser (rebuilding a damaged
/// cross-reference table) do not catch it. Its <see cref="PdfVectorException.Kind"/> is
/// <see cref="PdfVectorErrorKind.DataNotLoaded"/>; code that recovers from a
/// <see cref="PdfVectorException"/> lets this one through.
/// </summary>
public sealed class PdfDataNotLoadedException : PdfVectorException
{
    public PdfDataNotLoadedException(long offset, long length)
        : base(PdfVectorErrorKind.DataNotLoaded, $"Bytes {offset} to {offset + length} of the file are not loaded yet.")
    {
        Offset = offset;
        Length = length;
    }

    /// <summary>The first byte that was asked for.</summary>
    public long Offset { get; }

    /// <summary>The number of bytes that were asked for.</summary>
    public long Length { get; }
}
