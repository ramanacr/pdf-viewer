using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Objects;

/// <summary>
/// Abstract base class for all PDF objects according to ISO 32000-2 Clause 7.3.
/// </summary>
public abstract record PdfObject
{
    public virtual bool TryGetNumber(out double value)
    {
        value = 0.0;
        return false;
    }

    public virtual bool TryGetInteger(out long value)
    {
        value = 0;
        return false;
    }

    public virtual bool TryGetString(out string value)
    {
        value = string.Empty;
        return false;
    }

    public virtual bool TryGetName(out string value)
    {
        value = string.Empty;
        return false;
    }

    public virtual bool TryGetBoolean(out bool value)
    {
        value = false;
        return false;
    }
}

/// <summary>Boolean object (ISO 32000-2 7.3.2).</summary>
public sealed record PdfBoolean(bool Value) : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    public override bool TryGetBoolean(out bool value)
    {
        value = Value;
        return true;
    }

    public override string ToString() => Value ? "true" : "false";
}

/// <summary>Numeric integer object (ISO 32000-2 7.3.3).</summary>
public sealed record PdfInteger(long Value) : PdfObject
{
    public override bool TryGetInteger(out long value)
    {
        value = Value;
        return true;
    }

    public override bool TryGetNumber(out double value)
    {
        value = Value;
        return true;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Numeric real object (ISO 32000-2 7.3.3).</summary>
public sealed record PdfReal(double Value) : PdfObject
{
    public override bool TryGetNumber(out double value)
    {
        value = Value;
        return true;
    }

    public override bool TryGetInteger(out long value)
    {
        value = (long)Math.Round(Value);
        return true;
    }

    public override string ToString() => Value.ToString("G", CultureInfo.InvariantCulture);
}

/// <summary>Name object (ISO 32000-2 7.3.5) with leading slash omitted in Value.</summary>
public sealed record PdfName(string Value) : PdfObject
{
    public override bool TryGetName(out string value)
    {
        value = Value;
        return true;
    }

    public override string ToString() => "/" + Value;
}

/// <summary>String object (ISO 32000-2 7.3.4), either literal (...) or hexadecimal &lt;...&gt;.</summary>
public sealed record PdfString(ReadOnlyMemory<byte> RawBytes, bool IsHex = false) : PdfObject
{
    public string AsAscii() => Encoding.ASCII.GetString(RawBytes.Span);

    public string AsDecodedString()
    {
        var span = RawBytes.Span;
        if (span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(span[2..]);
        }
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(span[3..]);
        }

        // Standard PDFDocEncoding fallback: map basic chars directly
        var sb = new StringBuilder(span.Length);
        for (int i = 0; i < span.Length; i++)
        {
            sb.Append((char)span[i]);
        }
        return sb.ToString();
    }

    public override bool TryGetString(out string value)
    {
        value = AsDecodedString();
        return true;
    }

    public override string ToString() => IsHex ? $"<{Convert.ToHexString(RawBytes.Span)}>" : $"({AsDecodedString()})";
}

/// <summary>Array object (ISO 32000-2 7.3.6).</summary>
public sealed record PdfArray(IReadOnlyList<PdfObject> Items) : PdfObject, IReadOnlyList<PdfObject>
{
    public int Count => Items.Count;
    public PdfObject this[int index] => Items[index];
    public IEnumerator<PdfObject> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();

    public override string ToString() => $"[{string.Join(" ", Items)}]";
}

/// <summary>Dictionary object (ISO 32000-2 7.3.7).</summary>
public sealed record PdfDictionary(IReadOnlyDictionary<string, PdfObject> Entries) : PdfObject
{
    public int Count => Entries.Count;
    public bool ContainsKey(string key) => Entries.ContainsKey(key);
    public bool TryGetValue(string key, out PdfObject? value) => Entries.TryGetValue(key, out value);

    public PdfObject? this[string key] => Entries.TryGetValue(key, out var val) ? val : null;

    public T? Get<T>(string key) where T : PdfObject
    {
        if (Entries.TryGetValue(key, out var val) && val is T typed)
            return typed;
        return null;
    }

    public double? GetNumber(string key)
    {
        if (Entries.TryGetValue(key, out var val) && val.TryGetNumber(out double num))
            return num;
        return null;
    }

    public long? GetInteger(string key)
    {
        if (Entries.TryGetValue(key, out var val) && val.TryGetInteger(out long num))
            return num;
        return null;
    }

    public string? GetName(string key)
    {
        if (Entries.TryGetValue(key, out var val) && val.TryGetName(out string name))
            return name;
        return null;
    }

    public string? GetString(string key)
    {
        if (Entries.TryGetValue(key, out var val) && val.TryGetString(out string str))
            return str;
        return null;
    }
}

/// <summary>Null object (ISO 32000-2 7.3.9).</summary>
public sealed record PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    public override string ToString() => "null";
}

/// <summary>Indirect reference object: 12 0 R (ISO 32000-2 7.3.10).</summary>
public sealed record PdfIndirectRef(int ObjectNumber, int GenerationNumber = 0) : PdfObject
{
    public override string ToString() => $"{ObjectNumber} {GenerationNumber} R";
}

/// <summary>Stream object: dictionary followed by raw byte stream (ISO 32000-2 7.3.8).</summary>
public sealed record PdfStream(
    PdfDictionary Dictionary,
    long StreamOffset,
    long StreamLength,
    IPdfByteSource? Source,
    ReadOnlyMemory<byte>? CachedRawBytes = null) : PdfObject
{
    /// <summary>
    /// The data was decrypted by the security handler (or is exempt): a <c>/Crypt</c> filter in
    /// /Filter has already been applied and passes the bytes through.
    /// </summary>
    public bool Decrypted { get; init; }

    public ReadOnlyMemory<byte> GetRawBytes()
    {
        if (CachedRawBytes.HasValue)
            return CachedRawBytes.Value;

        if (Source != null && StreamLength > 0)
        {
            return Source.ReadMemory(StreamOffset, (int)StreamLength);
        }

        return ReadOnlyMemory<byte>.Empty;
    }
}
