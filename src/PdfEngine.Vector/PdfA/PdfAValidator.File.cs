using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.PdfA;

internal sealed partial class PdfAChecker
{
    private static bool IsEol(byte b) => b is (byte)'\r' or (byte)'\n';
    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private bool At(long pos, string text)
    {
        if (pos < 0 || pos + text.Length > _file.Length) return false;
        for (int i = 0; i < text.Length; i++) if (_file[pos + i] != text[i]) return false;
        return true;
    }

    // ------------------------------------------------------------------ 6.1.2 header, 6.1.3 trailer, 6.1.4 cross-reference

    private void CheckFileStructure()
    {
        // 6.1.2: "%PDF-1.n" at the very start of its own line, then a comment of at least four bytes above 127.
        int lineEnd = 0;
        while (lineEnd < _file.Length && !IsEol(_file[lineEnd])) lineEnd++;
        string headerLine = Encoding.Latin1.GetString(_file, 0, Math.Min(lineEnd, 64));
        bool header = _part == 1
            ? System.Text.RegularExpressions.Regex.IsMatch(headerLine, @"^%PDF-\d\.\d$")
            : System.Text.RegularExpressions.Regex.IsMatch(headerLine, @"^%PDF-1\.[0-7]$");
        if (!header) Fail("6.1.2", 1, $"The file header \"{Short(headerLine)}\" is not %PDF-1.n on a line of its own.");
        int line = lineEnd;
        if (line < _file.Length && _file[line] == '\r') line++;
        if (line < _file.Length && _file[line] == '\n') line++;
        bool comment = line + 4 < _file.Length && _file[line] == '%' && _file[line + 1] > 127 && _file[line + 2] > 127 && _file[line + 3] > 127 && _file[line + 4] > 127;
        if (comment)
        {
            int c = line + 1;
            while (c < _file.Length && !IsEol(_file[c])) c++;
            if (c >= _file.Length) comment = false;
        }
        if (!comment) Fail("6.1.2", 2, "The header is not followed by a comment line of at least four bytes above 127.");

        // 6.1.3: an ID in the (last) trailer, and in the first-page trailer of a linearized file, the same; no encryption; nothing after %%EOF.
        // The latest trailer is the one the final startxref leads to; in a linearized file the
        // first-page trailer must have the ID, and the main trailer's, if it has one, must match.
        var last = LatestTrailer() ?? Trailer;
        if (Linearized() && RawTrailers().FirstOrDefault() is { } firstPage && RawTrailers().Count > 1)
        {
            var main = RawTrailers()[^1];
            if (!IsFileId(firstPage["ID"])) Fail("6.1.3", 2, "The first-page trailer of the linearized file has no file identifier.");
            else if (main["ID"] != null && (!IsFileId(main["ID"]) || !SameIds(firstPage["ID"], main["ID"])))
                Fail("6.1.3", 4, "The first-page and main trailers of the linearized file have different file identifiers.");
        }
        else if (!IsFileId(last["ID"])) Fail("6.1.3", 1, "The trailer has no valid file identifier (/ID with two non-empty strings).");
        if (Trailer.ContainsKey("Encrypt") || last.ContainsKey("Encrypt"))
            Fail("6.1.3", _part == 1 ? 2 : 2, "The document is encrypted (the trailer has /Encrypt).");
        int eof = LastIndexOf("%%EOF");
        if (eof < 0) Fail("6.1.3", 3, "The file does not end with %%EOF.");
        else
        {
            int after = _file.Length - (eof + 5);
            bool ok = after == 0 || (after == 1 && IsEol(_file[^1])) || (after == 2 && _file[^2] == '\r' && _file[^1] == '\n');
            if (!ok) Fail("6.1.3", 3, $"{after} byte(s) follow the last %%EOF.");
        }

        // 6.1.4: classic cross-reference sections: "xref", one EOL, subsection headers "start count" with one space.
        foreach (int xref in AllIndexesOf("xref"))
        {
            if (xref >= 5 && At(xref - 5, "start")) continue;
            int p = xref + 4;
            int eols = 0;
            while (p < _file.Length && IsEol(_file[p])) { eols++; p++; }
            bool single = eols == 1 || (eols == 2 && _file[xref + 4] == '\r' && _file[xref + 5] == '\n');
            if (!single) Fail("6.1.4", 1, "The xref keyword and the subsection header are not separated by a single EOL.", $"offset {xref}");
            CheckXrefSubsections(p, xref);
        }
        if (_part == 1 && AllObjects().Any(o => o.Object is PdfStream { } st && st.Dictionary.GetName("Type") is "XRef" or "ObjStm"))
            Fail("6.1.4", 3, "The file uses cross-reference or object streams, which PDF/A-1 (PDF 1.4) does not have.");

        CheckRawSyntax();
    }

    private void CheckXrefSubsections(int p, int xref)
    {
        // Lines until "trailer": headers are "a b", entries are 20-byte "nnnnnnnnnn ggggg n".
        int guard = 0;
        while (p < _file.Length && guard++ < 1_000_000)
        {
            int e = p;
            while (e < _file.Length && !IsEol(_file[e])) e++;
            string line = Encoding.Latin1.GetString(_file, p, e - p);
            if (line.StartsWith("trailer", StringComparison.Ordinal) || line.Length == 0 && e >= _file.Length) break;
            var trimmed = line.TrimEnd();
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{10} \d{5} [nf]$")) { }
            else if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*\d+\s+\d+\s*$"))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+ \d+$"))
                    Fail("6.1.4", 2, "An xref subsection header's numbers are not separated by a single space.", $"offset {xref}");
            }
            else if (line.Trim().Length > 0) break;
            p = e;
            if (p < _file.Length && _file[p] == 13) p++;
            if (p < _file.Length && _file[p] == 10) p++;
            if (p == e) p++;
        }
    }

    private bool IsFileId(PdfObject? id)
    {
        if (_r.Resolve(id) is not PdfArray { Count: 2 } a) return false;
        return a.Items.All(i => _r.Resolve(i) is PdfString { RawBytes.Length: > 0 });
    }

    private bool SameIds(PdfObject? a, PdfObject? b)
    {
        var x = (PdfArray)_r.Resolve(a)!;
        var y = (PdfArray)_r.Resolve(b)!;
        return ((PdfString)_r.Resolve(x[0])!).RawBytes.Span.SequenceEqual(((PdfString)_r.Resolve(y[0])!).RawBytes.Span);
    }

    private bool Linearized()
    {
        int n = Math.Min(_file.Length, 2048);
        for (int i = 0; i + 11 < n; i++) if (At(i, "/Linearized")) return true;
        return false;
    }

    /// <summary>The trailer of the cross-reference section the last startxref points to (a stream's dictionary for a cross-reference stream).</summary>
    private PdfDictionary? LatestTrailer()
    {
        int sx = LastIndexOf("startxref");
        if (sx < 0) return null;
        int p = sx + 9;
        while (p < _file.Length && IsWhite(_file[p])) p++;
        long offset = 0;
        while (p < _file.Length && char.IsAsciiDigit((char)_file[p])) offset = offset * 10 + (_file[p++] - '0');
        if (offset <= 0 || offset >= _file.Length) return null;
        if (At(offset, "xref"))
        {
            int t = IndexOf("trailer", (int)offset);
            return t < 0 ? null : RawTrailers().Zip(AllIndexesOf("trailer")).FirstOrDefault(x => x.Second == t).First;
        }
        try
        {
            using var src = new MemoryByteSource(_file) { Position = offset };
            var lexer = new PdfLexer(src, Limits.PdfSecurityLimits.Default);
            lexer.NextToken(); lexer.NextToken(); lexer.NextToken(); // n g obj
            return new PdfParser(Limits.PdfSecurityLimits.Default).ParseObject(lexer) as PdfDictionary;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private List<PdfDictionary>? _rawTrailers;

    /// <summary>The trailer dictionaries as written, in file order.</summary>
    private List<PdfDictionary> RawTrailers()
    {
        if (_rawTrailers != null) return _rawTrailers;
        _rawTrailers = new List<PdfDictionary>();
        foreach (int t in AllIndexesOf("trailer"))
        {
            try
            {
                using var src = new Parsing.MemoryByteSource(_file) { Position = t + 7 };
                var lexer = new Parsing.PdfLexer(src, Limits.PdfSecurityLimits.Default);
                if (new Parsing.PdfParser(Limits.PdfSecurityLimits.Default).ParseObject(lexer) is PdfDictionary d) _rawTrailers.Add(d);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        return _rawTrailers;
    }

    /// <summary>
    /// Raw syntax of every object as written: hexadecimal strings (6.1.6) and the endobj keyword on
    /// a line of its own (P1 6.1.8, P2 6.1.9). Stream data is skipped by its length.
    /// </summary>
    private void CheckRawSyntax()
    {
        foreach (var (number, entry) in _doc.XrefTable.Entries)
        {
            if (!entry.IsInUse || number == 0 || entry.IsCompressed || entry.ByteOffset <= 0 || entry.ByteOffset >= _file.Length) continue;
            PdfStream? stream = null;
            try { stream = _r.Resolve(number) as PdfStream; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            ScanObject(number, (int)entry.ByteOffset, stream);
        }
        // Objects in object streams: their strings too.
        foreach (var (number, obj) in AllObjects())
            if (obj is PdfStream { } st && st.Dictionary.GetName("Type") == "ObjStm" && Decode(st) is { } data) ScanHex(data, 0, data.Length, Ref(number));
    }

    private void ScanObject(int number, int offset, PdfStream? stream)
    {
        int i = offset;
        // past "n g obj"
        int objKw = -1;
        for (int k = offset; k < Math.Min(_file.Length - 3, offset + 64); k++) if (At(k, "obj")) { objKw = k; break; }
        if (objKw < 0) return;
        i = objKw + 3;
        int limit = _file.Length;
        while (i < limit)
        {
            byte c = _file[i];
            if (c == '%') { while (i < limit && !IsEol(_file[i])) i++; continue; }
            if (c == '(') { i = SkipLiteral(i); continue; }
            if (c == '<')
            {
                if (i + 1 < limit && _file[i + 1] == '<') { i += 2; continue; }
                i = CheckHex(i, number);
                continue;
            }
            if (c == 's' && At(i, "stream") && (i == 0 || IsWhite(_file[i - 1]) || _file[i - 1] == '>'))
            {
                if (stream != null && _r.Resolve(stream.Dictionary["Length"]) is PdfInteger len && stream.StreamOffset > i && stream.StreamOffset + len.Value <= limit)
                    i = (int)(stream.StreamOffset + len.Value);
                else
                {
                    int e = IndexOf("endstream", i + 6);
                    if (e < 0) return;
                    i = e;
                }
                continue;
            }
            if (c == 'e' && At(i, "endobj"))
            {
                if (!IsEol(_file[i - 1])) Fail("6.1.8", "6.1.9", 2, "The endobj keyword is not preceded by an end of line.", Ref(number));
                if (i + 6 < limit && !IsEol(_file[i + 6])) Fail("6.1.8", "6.1.9", 3, "The endobj keyword is not followed by an end of line.", Ref(number));
                return;
            }
            if (c == 'o' && At(i, " obj") ) return;
            i++;
        }
    }

    private int IndexOf(string text, int from)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        int k = _file.AsSpan(from).IndexOf(needle);
        return k < 0 ? -1 : from + k;
    }

    private int SkipLiteral(int i)
    {
        int depth = 0;
        for (; i < _file.Length; i++)
        {
            byte c = _file[i];
            if (c == '\\') { i++; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i + 1;
        }
        return i;
    }

    /// <summary>A hexadecimal string at <paramref name="i"/>: an even number of hex digits, only hex digits and white space. Returns the position after it.</summary>
    private int CheckHex(int i, int number)
    {
        int digits = 0;
        bool bad = false;
        int j = i + 1;
        for (; j < _file.Length && _file[j] != '>'; j++)
        {
            byte c = _file[j];
            if (IsWhite(c)) continue;
            if (Uri.IsHexDigit((char)c)) digits++;
            else bad = true;
        }
        if (bad) Fail("6.1.6", 2, "A hexadecimal string contains characters other than hex digits and white space.", Ref(number));
        else if (digits % 2 != 0) Fail("6.1.6", 1, "A hexadecimal string has an odd number of digits.", Ref(number));
        return j + 1;
    }

    private void ScanHex(byte[] data, int from, int to, string where)
    {
        for (int i = from; i < to; i++)
        {
            byte c = data[i];
            if (c == '(')
            {
                int depth = 0;
                for (; i < to; i++)
                {
                    if (data[i] == '\\') { i++; continue; }
                    if (data[i] == '(') depth++;
                    else if (data[i] == ')' && --depth == 0) break;
                }
                continue;
            }
            if (c != '<') continue;
            if (i + 1 < to && data[i + 1] == '<') { i++; continue; }
            int digits = 0; bool bad = false; int j = i + 1;
            for (; j < to && data[j] != '>'; j++)
            {
                if (IsWhite(data[j])) continue;
                if (Uri.IsHexDigit((char)data[j])) digits++; else bad = true;
            }
            if (bad) Fail("6.1.6", 2, "A hexadecimal string contains characters other than hex digits and white space.", where);
            else if (digits % 2 != 0) Fail("6.1.6", 1, "A hexadecimal string has an odd number of digits.", where);
            i = j;
        }
    }

    private int LastIndexOf(string text)
    {
        for (int i = _file.Length - text.Length; i >= 0; i--) if (At(i, text)) return i;
        return -1;
    }

    private List<int> AllIndexesOf(string text)
    {
        var result = new List<int>();
        var span = _file.AsSpan();
        var needle = Encoding.ASCII.GetBytes(text);
        int start = 0;
        while (start < span.Length)
        {
            int i = span[start..].IndexOf(needle);
            if (i < 0) break;
            result.Add(start + i);
            start += i + needle.Length;
        }
        return result;
    }

    // ------------------------------------------------------------------ objects: syntax, streams, strings, names, limits

    private void CheckObjects()
    {
        int count = 0;
        foreach (var (number, entry) in _doc.XrefTable.Entries)
        {
            if (!entry.IsInUse || number == 0) continue;
            count++;
            if (!entry.IsCompressed && entry.ByteOffset > 0 && entry.ByteOffset < _file.Length) CheckObjectSyntax(number, entry.ByteOffset);
        }
        if (count > 8388607) Fail("6.1.12", "6.1.13", 7, $"The file has {count} indirect objects, more than 8,388,607.");

        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var (number, obj) in AllObjects())
        {
            VisitValue(obj, number, 0, seen);
            if (obj is PdfStream s) CheckStream(number, s);
        }
        VisitValue(Trailer, 0, 0, seen);
        if (_part == 1 && _r.Resolve(Trailer["Info"]) is PdfDictionary info)
            foreach (var (key, value) in info.Entries)
                if (key is "Title" or "Author" or "Subject" or "Keywords" or "Creator" or "Producer" or "CreationDate" or "ModDate" && _r.Resolve(value) is not (PdfString or PdfNull or null))
                    Fail("6.1.5", 1, $"The document information entry /{key} is not a string.");
    }

    /// <summary>6.1.9 (P1 6.1.8): "n g obj" with single spaces, after an EOL; the obj keyword followed by an EOL.</summary>
    private void CheckObjectSyntax(int number, long offset)
    {
        string c1 = "6.1.8", c2 = "6.1.9";
        // An offset that lands on the end of line before the object is tolerated (as readers do).
        while (offset < _file.Length && IsWhite(_file[offset])) offset++;
        if (offset > 0 && !IsEol(_file[offset - 1])) Fail(c1, c2, 1, "An object does not start on a new line.", Ref(number));
        long p = offset;
        while (p < _file.Length && char.IsAsciiDigit((char)_file[p])) p++;
        if (p >= _file.Length || _file[p] != ' ' || p + 1 >= _file.Length || !char.IsAsciiDigit((char)_file[p + 1]))
        {
            Fail(c1, c2, 1, "The object number and generation are not separated by a single space.", Ref(number));
            return;
        }
        p++;
        while (p < _file.Length && char.IsAsciiDigit((char)_file[p])) p++;
        if (!(p < _file.Length && _file[p] == ' ' && At(p + 1, "obj")))
        {
            Fail(c1, c2, 1, "The generation and the obj keyword are not separated by a single space.", Ref(number));
            return;
        }
        if (p + 4 < _file.Length && !IsEol(_file[p + 4])) Fail(c1, c2, 1, "The obj keyword is not followed by an end of line.", Ref(number));
    }

    /// <summary>6.1.7 (P2 6.1.7.1, 6.1.7.2): stream keyword EOLs, a correct /Length, no external data, allowed filters.</summary>
    private void CheckStream(int number, PdfStream s)
    {
        string c1 = "6.1.7", c2 = "6.1.7.1";
        var d = s.Dictionary;
        if (d.ContainsKey("F") || d.ContainsKey("FFilter") || d.ContainsKey("FDecodeParms"))
            Fail(c1, c2, 3, "A stream refers to external data (/F, /FFilter or /FDecodeParms).", Ref(number));
        foreach (var filter in Filters(d))
        {
            if (filter is "LZWDecode" or "LZW") Fail("6.1.10", "6.1.7.2", 1, "A stream uses the LZW filter.", Ref(number));
            if (filter == "Crypt" && _part >= 2) Fail(null, "6.1.7.2", 2, "A stream uses a Crypt filter.", Ref(number));
            if (_part >= 2 && filter is not ("ASCIIHexDecode" or "ASCII85Decode" or "FlateDecode" or "RunLengthDecode" or "CCITTFaxDecode" or "JBIG2Decode" or "DCTDecode" or "JPXDecode" or "Crypt" or "LZWDecode"))
                Fail(null, "6.1.7.2", 1, $"A stream uses the non-standard filter /{filter}.", Ref(number));
        }
        long start = s.StreamOffset;
        if (s.CachedRawBytes.HasValue || start <= 0 || start > _file.Length) return;
        // "stream" then CRLF or LF (a lone CR is not allowed).
        bool lf = start >= 7 && _file[start - 1] == '\n' && (At(start - 7, "stream") || (_file[start - 2] == '\r' && At(start - 8, "stream")));
        if (!lf) Fail(c1, c2, 1, "The stream keyword is not followed by CRLF or LF.", Ref(number));
        if (_r.Resolve(d["Length"]) is not PdfInteger { Value: >= 0 } len) { Fail(c1, c2, 2, "A stream has no valid /Length.", Ref(number)); return; }
        long end = start + len.Value;
        if (end > _file.Length) { Fail(c1, c2, 2, "A stream's /Length runs past the end of the file.", Ref(number)); return; }
        bool closed = At(end, "\r\nendstream") || At(end, "\nendstream") || At(end, "\rendstream");
        if (!closed)
        {
            // Either the length is wrong, or endstream is not on its own line.
            if (At(end, "endstream")) Fail(c1, c2, 1, "The endstream keyword is not preceded by an end of line.", Ref(number));
            else Fail(c1, c2, 2, "A stream's /Length does not match its data.", Ref(number));
        }
    }

    private IEnumerable<string> Filters(PdfDictionary d)
    {
        switch (_r.Resolve(d["Filter"]))
        {
            case PdfName n: yield return n.Value; break;
            case PdfArray a:
                foreach (var f in a) if (_r.Resolve(f) is PdfName fn) yield return fn.Value;
                break;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Limits (P1 6.1.12, P2 6.1.13) and name encoding (P2 6.1.8) of every direct value.</summary>
    private void VisitValue(PdfObject obj, int owner, int depth, HashSet<PdfObject> seen)
    {
        if (depth > 64) return;
        switch (obj)
        {
            case PdfInteger i when i.Value < int.MinValue || i.Value > int.MaxValue:
                Fail("6.1.12", "6.1.13", 1, $"The integer {i.Value} is outside the allowed range.", Ref(owner));
                break;
            case PdfReal r when Math.Abs(r.Value) > (_part == 1 ? 32767 : 3.403e38):
                Fail("6.1.12", "6.1.13", 2, $"The real number {r.Value.ToString(CultureInfo.InvariantCulture)} is outside the allowed range.", Ref(owner));
                break;
            case PdfString s when s.RawBytes.Length > (_part == 1 ? 65535 : 32767):
                Fail("6.1.12", "6.1.13", 3, $"A string is {s.RawBytes.Length} bytes long.", Ref(owner));
                break;
            case PdfName n:
                CheckName(n.Value, owner);
                break;
            case PdfArray a:
                if (!seen.Add(a)) return;
                if (_part == 1 && a.Count > 8191) Fail("6.1.12", 4, $"An array has {a.Count} elements.", Ref(owner));
                foreach (var item in a) VisitValue(item, owner, depth + 1, seen);
                break;
            case PdfDictionary dict:
                if (!seen.Add(dict)) return;
                if (_part == 1 && dict.Count > 4095) Fail("6.1.12", 5, $"A dictionary has {dict.Count} entries.", Ref(owner));
                foreach (var (key, value) in dict.Entries)
                {
                    CheckName(key, owner);
                    VisitValue(value, owner, depth + 1, seen);
                }
                break;
            case PdfStream st:
                VisitValue(st.Dictionary, owner, depth + 1, seen);
                break;
        }
    }

    private void CheckName(string name, int owner)
    {
        if (name.Length > 127) Fail("6.1.12", "6.1.13", 4, $"A name is {name.Length} bytes long.", Ref(owner));
        if (_part >= 2 && name.Any(c => c > 127))
        {
            var bytes = name.Select(c => (byte)c).ToArray();
            try { StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { Fail(null, "6.1.8", 1, "A name is not valid UTF-8.", Ref(owner)); }
        }
    }
}
