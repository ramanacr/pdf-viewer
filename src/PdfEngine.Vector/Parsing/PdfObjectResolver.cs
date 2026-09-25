using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Xref;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// Resolves indirect object references from classic xref offsets or modern object streams (/ObjStm)
/// with cycle detection and caching.
/// </summary>
public sealed class PdfObjectResolver
{
    private readonly IPdfByteSource _source;
    private readonly PdfXrefTable _xrefTable;
    private readonly PdfSecurityLimits _limits;
    private readonly PdfStreamDecoder _streamDecoder;
    private readonly ConcurrentDictionary<int, PdfObject> _resolvedCache = new();
    private readonly HashSet<int> _resolving = new();

    // The byte source and lexer are positional, so resolution is serialized. Monitor is re-entrant,
    // which object-stream and /Length resolution rely on.
    private readonly object _sync = new();

    public PdfObjectResolver(IPdfByteSource source, PdfXrefTable xrefTable, PdfSecurityLimits? limits = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _xrefTable = xrefTable ?? throw new ArgumentNullException(nameof(xrefTable));
        _limits = limits ?? PdfSecurityLimits.Default;
        _streamDecoder = new PdfStreamDecoder(_limits);
    }

    public PdfObject? Resolve(PdfObject? obj)
    {
        if (obj is PdfIndirectRef indirectRef)
        {
            return Resolve(indirectRef.ObjectNumber);
        }
        return obj;
    }

    public PdfObject? Resolve(int objectNumber)
    {
        if (_resolvedCache.TryGetValue(objectNumber, out var cached))
            return cached;

        if (!_xrefTable.Entries.TryGetValue(objectNumber, out var entry))
            return null;

        if (!entry.IsInUse)
            return null;

        lock (_sync)
        {
            if (_resolvedCache.TryGetValue(objectNumber, out cached))
                return cached;

            if (!_resolving.Add(objectNumber))
            {
                // Circular reference (e.g. a stream whose /Length points back at itself).
                return null;
            }

            try
            {
                PdfObject? resolved = null;

                if (entry.IsCompressed)
                {
                    resolved = ResolveFromObjectStream(entry.CompressedStreamObjectNumber, entry.CompressedStreamIndex, objectNumber);
                }
                else if (entry.ByteOffset > 0 && entry.ByteOffset < _source.Length)
                {
                    _source.Position = entry.ByteOffset;
                    var lexer = new PdfLexer(_source, _limits);

                    var objNumToken = lexer.NextToken();
                    lexer.NextToken(); // generation
                    var objKeyword = lexer.NextToken();

                    bool headerMatches = objNumToken.Type == PdfTokenType.Integer && objNumToken.IntValue == objectNumber &&
                                         objKeyword.Type == PdfTokenType.Keyword && objKeyword.TextValue == "obj";
                    if (!headerMatches)
                    {
                        // The xref points somewhere else than it claims. Rebuild once and retry; never
                        // hand back a different object under this number.
                        if (!_xrefTable.WasReconstructed)
                        {
                            _resolving.Remove(objectNumber);
                            _xrefTable.Rebuild(_source);
                            _resolvedCache.Clear();
                            _objectStreams.Clear();
                            return Resolve(objectNumber);
                        }
                        return null;
                    }

                    var parser = new PdfParser(_limits);
                    resolved = parser.ParseObject(lexer);
                }

                if (resolved is PdfStream stream)
                {
                    resolved = FixIndirectLength(stream);
                }

                if (resolved != null)
                {
                    _resolvedCache[objectNumber] = resolved;
                }

                return resolved;
            }
            finally
            {
                _resolving.Remove(objectNumber);
            }
        }
    }

    /// <summary>
    /// The parser cannot resolve an indirect /Length, so it scans for "endstream". When the real
    /// length is available and plausible, prefer it: binary data may legitimately contain "endstream".
    /// </summary>
    private PdfStream FixIndirectLength(PdfStream stream)
    {
        if (stream.CachedRawBytes.HasValue ||
            !stream.Dictionary.TryGetValue("Length", out var lenObj) ||
            lenObj is not PdfIndirectRef)
        {
            return stream;
        }

        if (Resolve(lenObj) is PdfObject lenValue &&
            lenValue.TryGetInteger(out long length) &&
            length >= 0 &&
            length != stream.StreamLength &&
            stream.StreamOffset + length <= _source.Length)
        {
            return stream with { StreamLength = length };
        }

        return stream;
    }

    private sealed record ObjectStreamIndex(byte[] Data, long First, List<(int ObjNum, long Offset)> Offsets);

    // Decoded object streams, so resolving N objects from one /ObjStm decodes it once, not N times.
    private readonly Dictionary<int, ObjectStreamIndex?> _objectStreams = new();

    private PdfObject? ResolveFromObjectStream(int streamObjNum, int indexInStream, int targetObjNum)
    {
        if (!_objectStreams.TryGetValue(streamObjNum, out var index))
        {
            index = LoadObjectStream(streamObjNum);
            _objectStreams[streamObjNum] = index;
        }

        if (index == null || indexInStream < 0)
            return null;

        // Prefer the recorded index, but tolerate writers whose index disagrees with the header.
        long relOffset = -1;
        if (indexInStream < index.Offsets.Count && index.Offsets[indexInStream].ObjNum == targetObjNum)
        {
            relOffset = index.Offsets[indexInStream].Offset;
        }
        else
        {
            foreach (var (objNum, offset) in index.Offsets)
            {
                if (objNum == targetObjNum)
                {
                    relOffset = offset;
                    break;
                }
            }
        }

        long targetOffset = index.First + relOffset;
        if (relOffset < 0 || targetOffset >= index.Data.Length)
            return null;

        using var memSource = new MemoryByteSource(index.Data);
        memSource.Position = targetOffset;
        var lexer = new PdfLexer(memSource, _limits);
        var parser = new PdfParser(_limits);
        return parser.ParseObject(lexer);
    }

    private ObjectStreamIndex? LoadObjectStream(int streamObjNum)
    {
        if (Resolve(streamObjNum) is not PdfStream objStm)
            return null;

        byte[] decoded = _streamDecoder.DecodeStream(objStm);
        var dict = objStm.Dictionary;

        long n = dict.GetInteger("N") ?? 0;
        long first = dict.GetInteger("First") ?? 0;
        if (n <= 0 || n > _limits.MaxObjectsCount || first < 0 || first > decoded.Length)
            return null;

        using var memSource = new MemoryByteSource(decoded);
        var lexer = new PdfLexer(memSource, _limits);
        var offsets = new List<(int ObjNum, long Offset)>((int)Math.Min(n, 4096));
        for (long i = 0; i < n && memSource.Position < first; i++)
        {
            var numToken = lexer.NextToken();
            var offsetToken = lexer.NextToken();
            if (numToken.Type != PdfTokenType.Integer || offsetToken.Type != PdfTokenType.Integer)
                break;
            offsets.Add(((int)numToken.IntValue, offsetToken.IntValue));
        }

        return new ObjectStreamIndex(decoded, first, offsets);
    }
}
