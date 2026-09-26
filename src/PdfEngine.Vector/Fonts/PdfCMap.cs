using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Code → CID mapping for composite (Type0) fonts (ISO 32000-2 9.7.5): Identity-H/V or an
/// embedded CMap program with codespace ranges (variable-length codes), cidrange/cidchar
/// mappings and an optional Identity (or embedded) base CMap via <c>usecmap</c>.
/// </summary>
internal sealed class PdfCMap
{
    /// <summary>Maximum nesting of <c>usecmap</c>/<c>/UseCMap</c> chains.</summary>
    public const int MaxUseCMapDepth = 4;

    private static readonly CodespaceRange TwoByteFull = new(new byte[] { 0, 0 }, new byte[] { 0xFF, 0xFF });

    private readonly List<CodespaceRange> _codespaces;
    private readonly Dictionary<long, int> _cidChars = new();
    private readonly List<CidRange> _cidRanges = new();
    private readonly PdfCMap? _base;

    public static readonly PdfCMap IdentityH = new("Identity-H", vertical: false);
    public static readonly PdfCMap IdentityV = new("Identity-V", vertical: true);

    private PdfCMap(string name, bool vertical)
    {
        Name = name;
        IsVertical = vertical;
        IsIdentity = true;
        _codespaces = new List<CodespaceRange> { TwoByteFull };
    }

    private PdfCMap(ParsedCMap parsed, PdfCMap? baseCMap, bool vertical)
    {
        Name = parsed.CMapName ?? "Embedded";
        IsVertical = vertical;
        _base = baseCMap;
        _codespaces = new List<CodespaceRange>(parsed.Codespaces);

        foreach (var r in parsed.CidRanges)
        {
            if (r.Low == r.High)
                _cidChars[Key(r.Length, r.Low)] = r.Cid;
            else
                _cidRanges.Add(r);
        }

        if (_codespaces.Count == 0)
        {
            if (baseCMap != null)
            {
                _codespaces.AddRange(baseCMap._codespaces);
            }
            else
            {
                // Malformed CMap without codespace: infer full ranges from mapping lengths.
                var lengths = new SortedSet<int>();
                foreach (var r in parsed.CidRanges)
                    lengths.Add(r.Length);
                foreach (int len in lengths)
                {
                    var lo = new byte[len];
                    var hi = new byte[len];
                    Array.Fill(hi, (byte)0xFF);
                    _codespaces.Add(new CodespaceRange(lo, hi));
                }
                if (_codespaces.Count == 0)
                    _codespaces.Add(TwoByteFull);
            }
        }
        _codespaces.Sort((a, b) => a.Length.CompareTo(b.Length));
    }

    /// <summary>CMap name (e.g. Identity-H or the embedded /CMapName).</summary>
    public string Name { get; }

    /// <summary>True for WMode 1 (vertical writing).</summary>
    public bool IsVertical { get; }

    /// <summary>True for Identity-H/V (2-byte codes, CID = code).</summary>
    public bool IsIdentity { get; }

    /// <summary>
    /// Builds a CMap from a parsed embedded program. <paramref name="baseCMap"/> is the resolved
    /// <c>/UseCMap</c> stream, if any; a <c>usecmap</c> naming Identity-H/V is resolved here.
    /// </summary>
    public static PdfCMap FromParsed(ParsedCMap parsed, PdfCMap? baseCMap, bool? verticalOverride, out bool unsupportedBase)
    {
        unsupportedBase = false;
        if (baseCMap == null && parsed.UseCMapName != null)
        {
            baseCMap = PredefinedCMaps.Get(parsed.UseCMapName);
            unsupportedBase = baseCMap == null;
        }
        bool vertical = verticalOverride ?? (parsed.WMode == 1 || (baseCMap?.IsVertical ?? false));
        return new PdfCMap(parsed, baseCMap, vertical);
    }

    /// <summary>
    /// Reads one character code starting at <paramref name="offset"/> using the codespace ranges.
    /// Returns the number of bytes consumed, always ≥ 1. Unmatched bytes map to CID 0 (.notdef).
    /// </summary>
    public int ReadCode(ReadOnlySpan<byte> bytes, int offset, out int code, out int cid)
    {
        code = 0;
        cid = 0;
        int remaining = bytes.Length - offset;
        if (offset < 0 || remaining <= 0)
            return 1;

        var slice = bytes.Slice(offset, Math.Min(remaining, 4));
        for (int n = 1; n <= slice.Length; n++)
        {
            var candidate = slice[..n];
            foreach (var range in _codespaces)
            {
                if (range.Length == n && range.Matches(candidate))
                {
                    code = (int)ToCode(candidate);
                    cid = Lookup(code, n);
                    return n;
                }
            }
        }

        // No full match (ISO 32000-2 9.7.6.3): consume the length of the codespace range that
        // matches the longest prefix, else the shortest codespace length; map to .notdef.
        int consume = 0, bestPrefix = -1;
        foreach (var range in _codespaces)
        {
            int prefix = 0;
            while (prefix < range.Length && prefix < slice.Length &&
                   slice[prefix] >= range.Low[prefix] && slice[prefix] <= range.High[prefix])
            {
                prefix++;
            }
            if (prefix > bestPrefix)
            {
                bestPrefix = prefix;
                consume = range.Length;
            }
        }
        if (bestPrefix <= 0)
            consume = _codespaces.Count > 0 ? _codespaces[0].Length : 1;
        consume = Math.Clamp(consume, 1, remaining);
        code = (int)ToCode(bytes.Slice(offset, Math.Min(consume, 4)));
        cid = 0;
        return consume;
    }

    /// <summary>CID for a code of the given byte length; 0 (.notdef) when unmapped.</summary>
    public int Lookup(int code, int length)
    {
        if (IsIdentity)
            return code & 0xFFFF;

        if (_cidChars.TryGetValue(Key(length, (uint)code), out int cid))
            return cid;

        uint c = (uint)code;
        foreach (var r in _cidRanges)
        {
            if (r.Length == length && c >= r.Low && c <= r.High)
                return (int)Math.Min(int.MaxValue, (long)r.Cid + (c - r.Low));
        }

        return _base?.Lookup(code, length) ?? 0;
    }

    /// <summary>
    /// CID for a code whose byte length is unknown: tries each codespace length that can hold it.
    /// </summary>
    public int LookupAnyLength(int code)
    {
        if (IsIdentity)
            return code & 0xFFFF;
        Span<byte> buf = stackalloc byte[4];
        foreach (var range in _codespaces)
        {
            int n = range.Length;
            if (n < 4 && (uint)code >= 1u << (8 * n))
                continue;
            for (int i = 0; i < n; i++)
                buf[i] = (byte)((uint)code >> (8 * (n - 1 - i)));
            if (range.Matches(buf[..n]))
                return Lookup(code, n);
        }
        return 0;
    }

    private static long Key(int length, uint code) => ((long)length << 32) | code;

    private static uint ToCode(ReadOnlySpan<byte> bytes)
    {
        uint v = 0;
        foreach (byte b in bytes)
            v = (v << 8) | b;
        return v;
    }
}
