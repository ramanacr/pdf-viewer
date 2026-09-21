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

        lock (_resolving)
        {
            if (!_resolving.Add(objectNumber))
            {
                // Circular reference detected; return null or stub to prevent stack overflow
                return null;
            }
        }

        try
        {
            PdfObject? resolved = null;

            if (entry.IsCompressed)
            {
                // Resolve from object stream (/ObjStm)
                resolved = ResolveFromObjectStream(entry.CompressedStreamObjectNumber, entry.CompressedStreamIndex, objectNumber);
            }
            else if (entry.ByteOffset > 0 && entry.ByteOffset < _source.Length)
            {
                // Resolve uncompressed object from file offset
                _source.Position = entry.ByteOffset;
                var lexer = new PdfLexer(_source, _limits);

                var objNumToken = lexer.NextToken();
                var genNumToken = lexer.NextToken();
                var objKeyword = lexer.NextToken();

                if (objKeyword.Type == PdfTokenType.Keyword && objKeyword.TextValue == "obj")
                {
                    var parser = new PdfParser(_limits);
                    resolved = parser.ParseObject(lexer);
                }
            }

            if (resolved != null)
            {
                _resolvedCache[objectNumber] = resolved;
            }

            return resolved;
        }
        finally
        {
            lock (_resolving)
            {
                _resolving.Remove(objectNumber);
            }
        }
    }

    private PdfObject? ResolveFromObjectStream(int streamObjNum, int indexInStream, int targetObjNum)
    {
        var streamObj = Resolve(streamObjNum);
        if (streamObj is not PdfStream objStm)
            return null;

        byte[] decoded = _streamDecoder.DecodeStream(objStm);
        var dict = objStm.Dictionary;

        int n = (int)(dict.GetInteger("N") ?? 0);
        long first = dict.GetInteger("First") ?? 0;
        if (n <= 0 || first < 0 || first > decoded.Length)
            return null;

        using var memSource = new MemoryByteSource(decoded);
        var lexer = new PdfLexer(memSource, _limits);

        // Read the N pairs of [objNum, offset]
        var offsets = new List<(int ObjNum, long Offset)>(n);
        for (int i = 0; i < n; i++)
        {
            var numToken = lexer.NextToken();
            var offsetToken = lexer.NextToken();
            if (numToken.Type == PdfTokenType.Integer && offsetToken.Type == PdfTokenType.Integer)
            {
                offsets.Add(((int)numToken.IntValue, offsetToken.IntValue));
            }
        }

        if (indexInStream < 0 || indexInStream >= offsets.Count)
            return null;

        var targetEntry = offsets[indexInStream];
        long targetOffset = first + targetEntry.Offset;
        if (targetOffset < 0 || targetOffset >= decoded.Length)
            return null;

        memSource.Position = targetOffset;
        var parser = new PdfParser(_limits);
        return parser.ParseObject(lexer);
    }
}
