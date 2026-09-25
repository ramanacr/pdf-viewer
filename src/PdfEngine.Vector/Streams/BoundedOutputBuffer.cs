using System;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// Growable output buffer that enforces <see cref="PdfSecurityLimits.MaxDecodedStreamBytes"/> and
/// <see cref="PdfSecurityLimits.MaxDecompressionRatio"/> on every write, so a decompression bomb is
/// rejected as soon as it crosses a ceiling instead of after it has been fully materialised.
/// </summary>
internal sealed class BoundedOutputBuffer
{
    /// <summary>Output size below which the expansion ratio is not enforced (tiny legitimate streams compress extremely well).</summary>
    internal const long RatioEnforcementThreshold = 1024 * 1024;

    private readonly long _maxBytes;
    private readonly double _maxRatio;
    private readonly long _inputLength;
    private byte[] _buffer;
    private int _length;

    public BoundedOutputBuffer(long inputLength, long maxBytes, double maxRatio, int initialCapacity = 0)
    {
        _inputLength = Math.Max(1, inputLength);
        _maxBytes = maxBytes <= 0 ? long.MaxValue : Math.Min(maxBytes, Array.MaxLength);
        _maxRatio = maxRatio <= 0 ? double.MaxValue : maxRatio;
        int capacity = (int)Math.Clamp(initialCapacity, 0, Math.Min(_maxBytes, 1024 * 1024));
        _buffer = capacity == 0 ? Array.Empty<byte>() : new byte[capacity];
    }

    /// <summary>Creates a buffer with no limits (used by the static helper decoders).</summary>
    public static BoundedOutputBuffer Unbounded(int inputLength) =>
        new(inputLength, long.MaxValue, double.MaxValue, inputLength);

    public static BoundedOutputBuffer For(PdfSecurityLimits limits, long inputLength) =>
        new(inputLength, limits.MaxDecodedStreamBytes, limits.MaxDecompressionRatio, (int)Math.Min(inputLength * 2, 1024 * 1024));

    public int Length => _length;

    public void WriteByte(byte value)
    {
        EnsureRoom(1);
        _buffer[_length++] = value;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        EnsureRoom(data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Appends <paramref name="count"/> copies of <paramref name="value"/>.</summary>
    public void Fill(byte value, int count)
    {
        if (count <= 0) return;
        EnsureRoom(count);
        _buffer.AsSpan(_length, count).Fill(value);
        _length += count;
    }

    public byte[] ToArray()
    {
        if (_length == 0) return Array.Empty<byte>();
        if (_length == _buffer.Length) return _buffer;
        return _buffer.AsSpan(0, _length).ToArray();
    }

    private void EnsureRoom(int extra)
    {
        long required = (long)_length + extra;
        if (required > _maxBytes)
        {
            throw new PdfResourceLimitException(
                nameof(PdfSecurityLimits.MaxDecodedStreamBytes),
                $"Decoded stream exceeded the security ceiling of {_maxBytes} bytes.");
        }

        if (required > RatioEnforcementThreshold && (double)required / _inputLength > _maxRatio)
        {
            throw new PdfResourceLimitException(
                nameof(PdfSecurityLimits.MaxDecompressionRatio),
                $"Decompression ratio exceeded the safety threshold of {_maxRatio}x.");
        }

        if (required <= _buffer.Length) return;

        long newCapacity = Math.Max(required, Math.Max(256, (long)_buffer.Length * 2));
        newCapacity = Math.Min(newCapacity, Math.Min(_maxBytes, Array.MaxLength));
        Array.Resize(ref _buffer, (int)newCapacity);
    }
}
