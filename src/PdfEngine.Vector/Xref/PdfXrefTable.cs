using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PdfEngine.Vector.Diagnostics;
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

    /// <summary>True when the cross-reference data was rebuilt by scanning the file (damaged/missing xref).</summary>
    public bool WasReconstructed { get; private set; }

    /// <summary>Number of bounded recoveries applied while loading (for local diagnostics).</summary>
    public int RecoveryCount { get; private set; }

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

    /// <summary>
    /// Loads the xref chain from <c>startxref</c>. When that fails or yields no usable catalog, the
    /// table is reconstructed by scanning for <c>N G obj</c> headers (05_PDF_CORE layer 3, step 6).
    /// </summary>
    public void Load(IPdfByteSource source)
    {
        bool chainOk;
        try
        {
            LoadChain(source);
            chainOk = _trailer != null && _trailer.ContainsKey("Root") && _entries.Count > 0;
        }
        catch (PdfResourceLimitException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or PdfSyntaxException or FormatException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            chainOk = false;
        }

        if (!chainOk)
        {
            Reconstruct(source);
        }
    }

    private void AddEntry(PdfXrefEntry entry)
    {
        if (entry.ObjectNumber < 0)
            return;
        if (_entries.Count >= _limits.MaxObjectsCount && !_entries.ContainsKey(entry.ObjectNumber))
        {
            throw new PdfResourceLimitException(
                nameof(PdfSecurityLimits.MaxObjectsCount),
                $"Cross-reference data exceeds the object limit ({_limits.MaxObjectsCount}).");
        }
        _entries[entry.ObjectNumber] = entry;
    }

    private void LoadChain(IPdfByteSource source)
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
                var sectionFree = new HashSet<int>();
                ParseClassicXref(lexer, source, sectionFree);

                // Parse trailer
                var trailerToken = lexer.NextToken();
                if (trailerToken.Type == PdfTokenType.Keyword && trailerToken.TextValue == "trailer")
                {
                    var parser = new PdfParser(_limits);
                    var dictObj = parser.ParseObject(lexer);
                    if (dictObj is PdfDictionary trailerDict)
                    {
                        // Hybrid-reference file (ISO 32000-2 7.5.8.4): objects the table lists as free
                        // may live in the /XRefStm cross-reference stream.
                        if (trailerDict.TryGetValue("XRefStm", out var xrefStmObj) &&
                            xrefStmObj is not null && xrefStmObj.TryGetInteger(out long xrefStmOffset) &&
                            xrefStmOffset > 0 && xrefStmOffset < source.Length && visited.Add(xrefStmOffset))
                        {
                            LoadHybridStream(source, xrefStmOffset, sectionFree);
                        }

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

    private void LoadHybridStream(IPdfByteSource source, long offset, HashSet<int> sectionFree)
    {
        long saved = source.Position;
        try
        {
            source.Position = offset;
            var lexer = new PdfLexer(source, _limits);
            lexer.NextToken();
            lexer.NextToken();
            var objKeyword = lexer.NextToken();
            if (objKeyword.Type != PdfTokenType.Keyword || objKeyword.TextValue != "obj")
                return;

            if (new PdfParser(_limits).ParseObject(lexer) is PdfStream xrefStream)
            {
                foreach (int objNum in sectionFree)
                    _entries.Remove(objNum);
                ParseXrefStream(xrefStream);
            }
        }
        finally
        {
            source.Position = saved;
        }
    }

    /// <summary>
    /// Discards the loaded table and rebuilds it by scanning. Called when an xref offset turns out to
    /// point at a different object than it claims (a common writer bug PDFium also repairs).
    /// </summary>
    public void Rebuild(IPdfByteSource source) => Reconstruct(source);

    /// <summary>
    /// Rebuilds the table by scanning for object headers. Later definitions win (incremental updates
    /// append). The trailer is taken from the last parseable <c>trailer</c> dictionary, or synthesized
    /// from a /Type /Catalog object. Objects inside object streams are registered afterwards.
    /// </summary>
    private void Reconstruct(IPdfByteSource source)
    {
        _entries.Clear();
        _trailer = null;
        WasReconstructed = true;
        RecoveryCount++;

        long length = source.Length;
        if (length > int.MaxValue)
            throw new PdfSyntaxException("File too large to reconstruct cross-reference data.");

        var data = source.ReadMemory(0, (int)length).Span;
        var objectStreams = new List<int>();
        int catalogObject = -1;

        int pos = 0;
        while (pos < data.Length)
        {
            int rel = data.Slice(pos).IndexOf("obj"u8);
            if (rel < 0)
                break;
            int objIdx = pos + rel;
            pos = objIdx + 3;

            if (objIdx + 3 < data.Length && !PdfLexer.IsWhitespace(data[objIdx + 3]) && !PdfLexer.IsDelimiter(data[objIdx + 3]))
                continue; // "objstm", "object", ...
            if (objIdx > 0 && data[objIdx - 1] == 'd')
                continue; // "endobj"

            if (!TryReadNumberBackwards(data, objIdx, out long gen, out int genStart) ||
                !TryReadNumberBackwards(data, genStart, out long num, out int numStart))
            {
                continue;
            }
            if (numStart > 0 && !PdfLexer.IsWhitespace(data[numStart - 1]) && !PdfLexer.IsDelimiter(data[numStart - 1]))
                continue;
            if (num <= 0 || num > int.MaxValue || gen > 65535)
                continue;

            AddEntry(new PdfXrefEntry((int)num, (int)gen, numStart, IsInUse: true));

            // Peek at the object header region for catalog / object-stream markers.
            var peek = data.Slice(pos, Math.Min(512, data.Length - pos));
            int endObj = peek.IndexOf("endobj"u8);
            if (endObj >= 0) peek = peek.Slice(0, endObj);
            if (ContainsTypeName(peek, "Catalog"u8))
                catalogObject = (int)num;
            if (ContainsTypeName(peek, "ObjStm"u8))
                objectStreams.Add((int)num);
        }

        // Trailer: last parseable "trailer" dictionary wins; older keys are merged in.
        int tpos = 0;
        while (true)
        {
            int rel = data.Slice(tpos).IndexOf("trailer"u8);
            if (rel < 0) break;
            int at = tpos + rel;
            tpos = at + 7;
            try
            {
                source.Position = at + 7;
                if (new PdfParser(_limits).ParseObject(new PdfLexer(source, _limits)) is PdfDictionary dict)
                {
                    var older = _trailer;
                    _trailer = dict;
                    if (older != null)
                        MergeTrailer(older);
                }
            }
            catch (Exception ex) when (ex is not PdfResourceLimitException)
            {
                // an unparsable trailer candidate contributes nothing
            }
        }

        // Xref-stream-only files have no "trailer"; synthesize /Root from the catalog object.
        if (_trailer == null || !_trailer.ContainsKey("Root"))
        {
            var entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            if (_trailer != null)
            {
                foreach (var (k, v) in _trailer.Entries) entries[k] = v;
            }
            if (catalogObject > 0)
                entries["Root"] = new PdfIndirectRef(catalogObject, _entries[catalogObject].GenerationNumber);
            _trailer = new PdfDictionary(entries);
        }

        if (objectStreams.Count == 0)
            return;

        var resolver = new PdfObjectResolver(source, this, _limits);
        var decoder = new PdfStreamDecoder(_limits);
        foreach (int stmNum in objectStreams)
        {
            try
            {
                if (resolver.Resolve(stmNum) is not PdfStream stm)
                    continue;
                byte[] decoded = decoder.DecodeStream(stm);
                long n = stm.Dictionary.GetInteger("N") ?? 0;
                using var mem = new MemoryByteSource(decoded);
                var lexer = new PdfLexer(mem, _limits);
                for (int i = 0; i < n && i < _limits.MaxObjectsCount; i++)
                {
                    var numTok = lexer.NextToken();
                    var offTok = lexer.NextToken();
                    if (numTok.Type != PdfTokenType.Integer || offTok.Type != PdfTokenType.Integer)
                        break;
                    int objNum = (int)numTok.IntValue;
                    if (objNum > 0 && !_entries.ContainsKey(objNum))
                    {
                        AddEntry(new PdfXrefEntry(objNum, 0, 0, IsInUse: true,
                            CompressedStreamObjectNumber: stmNum, CompressedStreamIndex: i));
                    }
                }
            }
            catch (Exception ex) when (ex is not PdfResourceLimitException)
            {
                // a damaged object stream contributes nothing
            }
        }

        if (!_trailer.ContainsKey("Root"))
        {
            // The catalog itself may live in an object stream.
            foreach (var (objNum, entry) in _entries)
            {
                if (!entry.IsCompressed) continue;
                if (resolver.Resolve(objNum) is PdfDictionary d && d.GetName("Type") == "Catalog")
                {
                    var entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                    foreach (var (k, v) in _trailer.Entries) entries[k] = v;
                    entries["Root"] = new PdfIndirectRef(objNum);
                    _trailer = new PdfDictionary(entries);
                    break;
                }
            }
        }
    }

    private static bool ContainsTypeName(ReadOnlySpan<byte> region, ReadOnlySpan<byte> typeName)
    {
        int searchFrom = 0;
        while (searchFrom < region.Length)
        {
            int rel = region.Slice(searchFrom).IndexOf("/Type"u8);
            if (rel < 0)
                return false;
            int p = searchFrom + rel + 5;
            searchFrom = p;
            while (p < region.Length && PdfLexer.IsWhitespace(region[p])) p++;
            if (p < region.Length && region[p] == '/' && region.Slice(p + 1).StartsWith(typeName))
            {
                int after = p + 1 + typeName.Length;
                if (after >= region.Length || PdfLexer.IsWhitespace(region[after]) || PdfLexer.IsDelimiter(region[after]))
                    return true;
            }
        }
        return false;
    }

    private static bool TryReadNumberBackwards(ReadOnlySpan<byte> data, int end, out long value, out int start)
    {
        value = 0;
        start = end;
        int p = end - 1;
        while (p >= 0 && PdfLexer.IsWhitespace(data[p])) p--;
        if (p < 0 || data[p] < '0' || data[p] > '9')
            return false;
        int digitsEnd = p + 1;
        int digits = 0;
        while (p >= 0 && data[p] >= '0' && data[p] <= '9' && digits < 10)
        {
            p--;
            digits++;
        }
        start = p + 1;
        return long.TryParse(data.Slice(start, digitsEnd - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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

    private void ParseClassicXref(PdfLexer lexer, IPdfByteSource source, HashSet<int> sectionFree)
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
            long countLong = token2.IntValue;
            if (startObj < 0 || countLong < 0)
                break;
            if (countLong > _limits.MaxObjectsCount)
            {
                throw new PdfResourceLimitException(
                    nameof(PdfSecurityLimits.MaxObjectsCount),
                    $"xref subsection claims {countLong} entries (limit {_limits.MaxObjectsCount}).");
            }
            int count = (int)countLong;

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
                        AddEntry(new PdfXrefEntry(objNum, gen, offset, inUse));
                        if (!inUse)
                            sectionFree.Add(objNum);
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

        long v1 = wArr[0].TryGetInteger(out long a1) ? a1 : 1;
        long v2 = wArr[1].TryGetInteger(out long a2) ? a2 : 2;
        long v3 = wArr[2].TryGetInteger(out long a3) ? a3 : 1;
        // Field widths above 8 bytes cannot be represented and indicate hostile/corrupt data.
        if (v1 < 0 || v2 < 0 || v3 < 0 || v1 > 8 || v2 > 8 || v3 > 8) return;
        int w1 = (int)v1, w2 = (int)v2, w3 = (int)v3;
        int entrySize = w1 + w2 + w3;
        if (entrySize <= 0) return;

        // /Index array specifies subsections: [start1 count1 start2 count2 ...]
        var indexList = new List<(int Start, int Count)>();
        if (dict.TryGetValue("Index", out var idxObj) && idxObj is PdfArray idxArr)
        {
            for (int i = 0; i < idxArr.Count - 1; i += 2)
            {
                int s = (int)(idxArr[i].TryGetInteger(out long si) ? si : 0);
                long c = idxArr[i + 1].TryGetInteger(out long ci) ? ci : 0;
                if (s < 0 || c < 0) continue;
                // A subsection can never describe more entries than the decoded data holds.
                indexList.Add((s, (int)Math.Min(c, data.Length / entrySize)));
            }
        }
        else
        {
            // Default: 0 to /Size
            long size = dict.GetInteger("Size") ?? (data.Length / entrySize);
            indexList.Add((0, (int)Math.Clamp(size, 0, data.Length / entrySize)));
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
                        AddEntry(new PdfXrefEntry(objNum, (int)f3, f2, IsInUse: false));
                    }
                    else if (f1 == 1)
                    {
                        AddEntry(new PdfXrefEntry(objNum, (int)f3, f2, IsInUse: true));
                    }
                    else if (f1 == 2 && f2 > 0 && f2 <= int.MaxValue)
                    {
                        // Compressed object in object stream: f2 = streamObjNum, f3 = indexInStream
                        AddEntry(new PdfXrefEntry(objNum, 0, 0, IsInUse: true,
                            CompressedStreamObjectNumber: (int)f2,
                            CompressedStreamIndex: (int)f3));
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
