using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Xref;

/// <summary>
/// Parser and manager for PDF Cross-Reference tables and streams.
/// </summary>
public sealed class PdfXrefTable
{
    private readonly Dictionary<int, PdfXrefEntry> _entries = new();
    private readonly PdfSecurityLimits _limits;
    private PdfDictionary? _trailer;

    public IReadOnlyDictionary<int, PdfXrefEntry> Entries => _entries;
    public PdfDictionary? Trailer => _trailer;

    public PdfXrefTable(PdfSecurityLimits? limits = null)
    {
        _limits = limits ?? PdfSecurityLimits.Default;
    }

    public static long FindStartXref(IPdfByteSource source)
    {
        long len = source.Length;
        int searchBytes = (int)Math.Min(len, 4096);
        long searchStart = len - searchBytes;

        var buf = source.ReadMemory(searchStart, searchBytes);
        var span = buf.Span;

        // Search backward for "startxref"
        ReadOnlySpan<byte> target = "startxref"u8;
        int idx = span.LastIndexOf(target);
        if (idx < 0)
        {
            throw new InvalidDataException("Unable to locate 'startxref' token in PDF header/trailer.");
        }

        // Parse integer after startxref
        int pos = idx + target.Length;
        while (pos < span.Length && PdfLexer.IsWhitespace(span[pos]))
        {
            pos++;
        }

        int numStart = pos;
        while (pos < span.Length && span[pos] >= '0' && span[pos] <= '9')
        {
            pos++;
        }

        if (numStart == pos)
        {
            throw new InvalidDataException("Invalid or missing offset following 'startxref'.");
        }

        string numStr = Encoding.ASCII.GetString(span.Slice(numStart, pos - numStart));
        if (!long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset))
        {
            throw new InvalidDataException($"Malformed startxref offset: '{numStr}'.");
        }

        return offset;
    }

    public void Load(IPdfByteSource source)
    {
        long startXref = FindStartXref(source);
        var visited = new HashSet<long>();
        long currentOffset = startXref;
        int depth = 0;

        while (currentOffset > 0 && depth < _limits.MaxTrailerChainDepth)
        {
            if (!visited.Add(currentOffset))
                break; // Cycle detected

            source.Position = currentOffset;
            var lexer = new PdfLexer(source, _limits);
            var token = lexer.NextToken();

            if (token.Type != PdfTokenType.Keyword || token.TextValue != "xref")
            {
                // Resilient recovery: scan nearby window (+/- 32 bytes) for 'xref' keyword
                long probeStart = Math.Max(0, currentOffset - 32);
                int probeLen = (int)Math.Min(64, source.Length - probeStart);
                var probeBuf = source.ReadMemory(probeStart, probeLen);
                int xrefIdx = probeBuf.Span.IndexOf("xref"u8);
                if (xrefIdx >= 0)
                {
                    source.Position = probeStart + xrefIdx;
                    lexer = new PdfLexer(source, _limits);
                    token = lexer.NextToken();
                }
            }

            if (token.Type == PdfTokenType.Keyword && token.TextValue == "xref")
            {
                // Classic xref table
                ParseClassicXref(lexer, source);

                // Parse trailer
                var trailerToken = lexer.NextToken();
                if (trailerToken.Type == PdfTokenType.Keyword && trailerToken.TextValue == "trailer")
                {
                    var parser = new PdfParser(_limits);
                    var dictObj = parser.ParseObject(lexer);
                    if (dictObj is PdfDictionary trailerDict)
                    {
                        if (_trailer == null)
                        {
                            _trailer = trailerDict;
                        }
                        else
                        {
                            // Merge earlier trailers for keys not already present
                            MergeTrailer(trailerDict);
                        }

                        if (trailerDict.TryGetValue("Prev", out var prevObj) && prevObj is not null && prevObj.TryGetInteger(out long prevOffset))
                        {
                            currentOffset = prevOffset;
                            depth++;
                            continue;
                        }
                    }
                }
                break;
            }
            else if (token.Type == PdfTokenType.Integer)
            {
                // Possible XRef stream: objNum genNum obj << /Type /XRef ... >>
                var genToken = lexer.NextToken();
                var objKeyword = lexer.NextToken();
                if (objKeyword.Type == PdfTokenType.Keyword && objKeyword.TextValue == "obj")
                {
                    var parser = new PdfParser(_limits);
                    var streamObj = parser.ParseObject(lexer);
                    if (streamObj is PdfStream xrefStream)
                    {
                        ParseXrefStream(xrefStream);
                        if (_trailer == null)
                        {
                            _trailer = xrefStream.Dictionary;
                        }
                        else
                        {
                            MergeTrailer(xrefStream.Dictionary);
                        }

                        if (xrefStream.Dictionary.TryGetValue("Prev", out var prevObj) && prevObj is not null && prevObj.TryGetInteger(out long prevOffset))
                        {
                            currentOffset = prevOffset;
                            depth++;
                            continue;
                        }
                    }
                }
                break;
            }
            else
            {
                // Damaged offset; try scanning ahead slightly
                break;
            }
        }
    }

    private void MergeTrailer(PdfDictionary earlierTrailer)
    {
        if (_trailer == null) return;
        var merged = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        foreach (var (k, v) in earlierTrailer.Entries)
        {
            merged[k] = v;
        }
        foreach (var (k, v) in _trailer.Entries)
        {
            merged[k] = v; // Keep newer trailer entries primary
        }
        _trailer = new PdfDictionary(merged);
    }

    private void ParseClassicXref(PdfLexer lexer, IPdfByteSource source)
    {
        while (true)
        {
            long beforeTokenPos = source.Position;
            var token1 = lexer.NextToken();
            if (token1.Type != PdfTokenType.Integer)
            {
                source.Position = beforeTokenPos;
                break; // Likely reached 'trailer'
            }

            var token2 = lexer.NextToken();
            if (token2.Type != PdfTokenType.Integer)
            {
                source.Position = beforeTokenPos;
                break;
            }

            int startObj = (int)token1.IntValue;
            int count = (int)token2.IntValue;

            for (int i = 0; i < count; i++)
            {
                int objNum = startObj + i;
                string? line = ReadLine(source);
                if (string.IsNullOrWhiteSpace(line))
                {
                    // If empty line, retry once
                    line = ReadLine(source);
                    if (string.IsNullOrWhiteSpace(line))
                        break;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gen))
                {
                    bool inUse = parts[2].StartsWith("n", StringComparison.OrdinalIgnoreCase);
                    if (!_entries.ContainsKey(objNum))
                    {
                        _entries[objNum] = new PdfXrefEntry(objNum, gen, offset, inUse);
                    }
                }
            }
        }
    }

    private static string? ReadLine(IPdfByteSource source)
    {
        var sb = new StringBuilder();
        bool foundAny = false;
        while (true)
        {
            int b = source.ReadByte();
            if (b < 0) break;
            foundAny = true;
            if (b == '\r')
            {
                int next = source.ReadByte();
                if (next >= 0 && next != '\n')
                {
                    source.Seek(-1, SeekOrigin.Current);
                }
                break;
            }
            if (b == '\n')
            {
                break;
            }
            sb.Append((char)b);
        }
        return foundAny ? sb.ToString() : null;
    }

    private void ParseXrefStream(PdfStream xrefStream)
    {
        var decoder = new PdfStreamDecoder(_limits);
        byte[] data = decoder.DecodeStream(xrefStream);
        var dict = xrefStream.Dictionary;

        // /W array specifies field widths in bytes [w1, w2, w3]
        if (!dict.TryGetValue("W", out var wObj) || wObj is not PdfArray wArr || wArr.Count < 3)
            return;

        int w1 = (int)(wArr[0].TryGetInteger(out long v1) ? v1 : 1);
        int w2 = (int)(wArr[1].TryGetInteger(out long v2) ? v2 : 2);
        int w3 = (int)(wArr[2].TryGetInteger(out long v3) ? v3 : 1);
        int entrySize = w1 + w2 + w3;
        if (entrySize <= 0) return;

        // /Index array specifies subsections: [start1 count1 start2 count2 ...]
        var indexList = new List<(int Start, int Count)>();
        if (dict.TryGetValue("Index", out var idxObj) && idxObj is PdfArray idxArr)
        {
            for (int i = 0; i < idxArr.Count - 1; i += 2)
            {
                int s = (int)(idxArr[i].TryGetInteger(out long si) ? si : 0);
                int c = (int)(idxArr[i + 1].TryGetInteger(out long ci) ? ci : 0);
                indexList.Add((s, c));
            }
        }
        else
        {
            // Default: 0 to /Size
            int size = (int)(dict.GetInteger("Size") ?? (data.Length / entrySize));
            indexList.Add((0, size));
        }

        int offset = 0;
        foreach (var (startObj, count) in indexList)
        {
            for (int i = 0; i < count; i++)
            {
                if (offset + entrySize > data.Length)
                    return;

                int objNum = startObj + i;
                long f1 = ReadField(data, offset, w1, defaultVal: 1);
                long f2 = ReadField(data, offset + w1, w2, defaultVal: 0);
                long f3 = ReadField(data, offset + w1 + w2, w3, defaultVal: 0);
                offset += entrySize;

                if (!_entries.ContainsKey(objNum))
                {
                    if (f1 == 0)
                    {
                        // Free object
                        _entries[objNum] = new PdfXrefEntry(objNum, (int)f3, f2, IsInUse: false);
                    }
                    else if (f1 == 1)
                    {
                        // Normal uncompressed object
                        _entries[objNum] = new PdfXrefEntry(objNum, (int)f3, f2, IsInUse: true);
                    }
                    else if (f1 == 2)
                    {
                        // Compressed object in object stream: f2 = streamObjNum, f3 = indexInStream
                        _entries[objNum] = new PdfXrefEntry(objNum, 0, 0, IsInUse: true,
                            CompressedStreamObjectNumber: (int)f2,
                            CompressedStreamIndex: (int)f3);
                    }
                }
            }
        }
    }

    private static long ReadField(byte[] data, int start, int length, long defaultVal)
    {
        if (length <= 0) return defaultVal;
        long val = 0;
        for (int i = 0; i < length; i++)
        {
            val = (val << 8) | data[start + i];
        }
        return val;
    }
}
