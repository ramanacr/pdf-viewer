using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Xref;

namespace PdfEngine.Vector.Parsing;

/// <summary>What a <see cref="PdfObjectClosure"/> walks.</summary>
public enum PdfClosureKind
{
    /// <summary>
    /// What opening a document reads: the trailer's /Root, /Info and /Encrypt, the page tree with
    /// each page's boxes, its resource dictionary (not what it names) and the start and end of its
    /// content streams, and the optional-content configuration.
    /// </summary>
    Structure,

    /// <summary>
    /// What drawing a page reads: everything the page dictionary reaches (contents, resources,
    /// fonts, images, annotations and their appearances), except the links back up or across the
    /// document (/Parent, an annotation's /P, actions and destinations, threads, thumbnails).
    /// </summary>
    Page,
}

/// <summary>
/// The byte ranges that objects occupy in a file, from its cross-reference table: an object runs
/// from its offset to the next object's offset (or the end of the file). An object in an object
/// stream is read with its stream, so its range is the stream's. Used by <see cref="PdfRangedDocument"/>
/// to fetch the objects a page needs before the page is parsed.
/// </summary>
public sealed class PdfRangePlanner
{
    private readonly PdfXrefTable _xref;
    private readonly long[] _offsets; // sorted, distinct; the file length last

    /// <summary>Ranges closer than this are fetched as one request.</summary>
    public const long DefaultMergeGap = 16 * 1024;

    public PdfRangePlanner(PdfXrefTable xref, long fileLength)
    {
        _xref = xref ?? throw new ArgumentNullException(nameof(xref));
        FileLength = fileLength;
        var offsets = new List<long>(xref.Entries.Count + 1);
        foreach (var entry in xref.Entries.Values)
        {
            if (entry.IsInUse && !entry.IsCompressed && entry.ByteOffset > 0 && entry.ByteOffset < fileLength)
                offsets.Add(entry.ByteOffset);
        }
        offsets.Add(fileLength);
        offsets.Sort();
        _offsets = offsets.Distinct().ToArray();
    }

    public long FileLength { get; }

    /// <summary>The bytes of an object (of its object stream, for a compressed one), or null when the table has no such object.</summary>
    public PdfByteRange? ExtentOf(int objectNumber)
    {
        if (!_xref.Entries.TryGetValue(objectNumber, out var entry) || !entry.IsInUse) return null;
        if (entry.IsCompressed)
        {
            if (!_xref.Entries.TryGetValue(entry.CompressedStreamObjectNumber, out var stream) || !stream.IsInUse || stream.IsCompressed) return null;
            entry = stream;
        }
        if (entry.ByteOffset <= 0 || entry.ByteOffset >= FileLength) return null;
        return new PdfByteRange(entry.ByteOffset, NextOffsetAfter(entry.ByteOffset));
    }

    /// <summary>The range of the object that holds <paramref name="offset"/>: from the nearest object start at or before it to the next one.</summary>
    public PdfByteRange ExtentAt(long offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, FileLength - 1));
        int i = Array.BinarySearch(_offsets, offset);
        if (i < 0) i = ~i - 1;
        long start = i >= 0 ? _offsets[i] : 0;
        return new PdfByteRange(start, NextOffsetAfter(start));
    }

    private long NextOffsetAfter(long offset)
    {
        int i = Array.BinarySearch(_offsets, offset);
        i = i >= 0 ? i + 1 : ~i;
        return i < _offsets.Length ? _offsets[i] : FileLength;
    }

    /// <summary>Sorts ranges and joins those that overlap or are closer than <paramref name="gap"/> bytes.</summary>
    public static List<PdfByteRange> Merge(IEnumerable<PdfByteRange> ranges, long gap = 0)
    {
        var sorted = ranges.Where(r => !r.IsEmpty).OrderBy(r => r.Start).ToList();
        var merged = new List<PdfByteRange>(sorted.Count);
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End + gap)
                merged[^1] = new PdfByteRange(merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>Splits ranges into pieces of at most <paramref name="maxBytes"/>, each starting at a multiple of <paramref name="alignment"/> after the first.</summary>
    public static List<PdfByteRange> Split(IEnumerable<PdfByteRange> ranges, long maxBytes, long alignment = 1)
    {
        maxBytes = Math.Max(alignment, maxBytes / alignment * alignment);
        var result = new List<PdfByteRange>();
        foreach (var r in ranges)
        {
            for (long s = r.Start; s < r.End;)
            {
                long e = Math.Min(r.End, (s + maxBytes) / alignment * alignment);
                if (e <= s) e = Math.Min(r.End, s + maxBytes);
                result.Add(new PdfByteRange(s, e));
                s = e;
            }
        }
        return result;
    }

    /// <summary>
    /// Every byte range that a page needs, found by resolving its objects with <paramref name="resolver"/>
    /// (for a file that is wholly readable, as in tests). <see cref="PdfRangedDocument"/> walks the
    /// same closure level by level, fetching each level's ranges before it resolves them.
    /// </summary>
    public List<PdfByteRange> PlanPage(PdfObjectResolver resolver, PdfDictionary page, int pageObjectNumber)
    {
        var closure = new PdfObjectClosure(this, PdfClosureKind.Page);
        if (pageObjectNumber > 0) closure.AddRoot(pageObjectNumber);
        else closure.AddDirectRoot(page);
        var ranges = new List<PdfByteRange>();
        while (!closure.IsDone)
        {
            ranges.AddRange(closure.PendingRanges());
            closure.Advance(n => resolver.Resolve(n));
        }
        return Merge(ranges);
    }
}

/// <summary>
/// A walk over the objects that a document's structure (<see cref="PdfClosureKind.Structure"/>)
/// or a page (<see cref="PdfClosureKind.Page"/>) needs, one level of references at a time:
/// <see cref="PendingRanges"/> are the bytes of the objects found but not read yet; after they are
/// fetched, <see cref="Advance"/> reads those objects and finds the next level. An object that
/// cannot be read yet (its bytes were only partly fetched) is asked for whole in the next level.
/// </summary>
public sealed class PdfObjectClosure
{
    /// <summary>A content stream larger than this is fetched as its first and last block when the document opens.</summary>
    public const long PartialThreshold = 64 * 1024;

    private readonly PdfRangePlanner _planner;
    private readonly HashSet<int> _seen = new();
    private List<(int Number, Rule Rule)> _pending = new();
    private readonly HashSet<int> _partial = new();
    private readonly HashSet<int> _retried = new();
    private readonly List<PdfDictionary> _directRoots = new();
    private long _bytes;

    private enum Rule { Walk, Leaf, Contents }

    // Links that leave the page: up the tree, to other pages, to the document's navigation.
    private static readonly HashSet<string> PageExcluded = new(StringComparer.Ordinal)
    {
        "Parent", "P", "Prev", "Next", "First", "Last", "Dest", "A", "AA", "PA", "Metadata", "PieceInfo",
        "Thumb", "B", "Thread", "Threads", "StructParent", "StructParents", "IRT", "Popup", "Pg", "K",
    };

    private static readonly HashSet<string> StructureWalked = new(StringComparer.Ordinal)
    {
        "Root", "Encrypt", "Pages", "Kids", "MediaBox", "CropBox", "Rotate", "OCProperties", "OCGs", "D", "ON", "OFF",
        "Length", "CF", "StdCF", "DefaultCryptFilter", "Recipients",
    };

    public PdfObjectClosure(PdfRangePlanner planner, PdfClosureKind kind)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        Kind = kind;
    }

    public PdfClosureKind Kind { get; }

    /// <summary>The most objects visited (default 20 000).</summary>
    public int MaxObjects { get; init; } = 20_000;

    /// <summary>The most bytes asked for (default 256 MB); past it, the walk stops.</summary>
    public long MaxBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The objects visited so far.</summary>
    public int ObjectCount => _seen.Count;

    /// <summary>True when there is nothing more to read (or a limit was reached).</summary>
    public bool IsDone => (_pending.Count == 0 && _directRoots.Count == 0) || _bytes > MaxBytes;

    /// <summary>Starts the walk at an indirect object (a page, or the trailer's /Root).</summary>
    public void AddRoot(int objectNumber) => Add(objectNumber, Rule.Walk);

    /// <summary>Starts the walk at a direct object: the trailer, or a page written inside its parent's /Kids.</summary>
    public void AddDirectRoot(PdfDictionary dictionary) => _directRoots.Add(dictionary);

    private void Add(int number, Rule rule)
    {
        if (number <= 0 || _seen.Count >= MaxObjects || !_seen.Add(number)) return;
        _pending.Add((number, rule));
        if (rule == Rule.Contents) _partial.Add(number);
    }

    /// <summary>The bytes of the objects to read next, merged.</summary>
    public List<PdfByteRange> PendingRanges()
    {
        WalkDirectRoots();
        var ranges = new List<PdfByteRange>();
        foreach (var (number, _) in _pending)
        {
            if (_planner.ExtentOf(number) is not { } extent) continue;
            if (_partial.Contains(number) && extent.Length > PartialThreshold)
            {
                // The dictionary at the start, and the end of the data where "endstream" is checked.
                ranges.Add(new PdfByteRange(extent.Start, extent.Start + 4096));
                ranges.Add(new PdfByteRange(extent.End - 4096, extent.End));
            }
            else
            {
                ranges.Add(extent);
            }
        }
        var merged = PdfRangePlanner.Merge(ranges);
        foreach (var r in merged) _bytes += r.Length;
        return merged;
    }

    /// <summary>
    /// Reads the pending objects with <paramref name="resolve"/> and queues what they refer to. An
    /// object whose bytes are not all there yet (<see cref="PdfDataNotLoadedException"/>) is queued
    /// again, whole, once.
    /// </summary>
    public void Advance(Func<int, PdfObject?> resolve)
    {
        WalkDirectRoots();
        var current = _pending;
        _pending = new List<(int, Rule)>();
        foreach (var (number, rule) in current)
        {
            PdfObject? obj;
            try
            {
                obj = resolve(number);
            }
            catch (PdfDataNotLoadedException)
            {
                if (_retried.Add(number))
                {
                    _partial.Remove(number);
                    _pending.Add((number, rule));
                }
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                continue; // a damaged object: the parse of the page reports it
            }
            if (obj != null && rule != Rule.Leaf) Walk(obj, null, rule, 0);
        }
    }

    private void WalkDirectRoots()
    {
        if (_directRoots.Count == 0) return;
        var roots = _directRoots.ToArray();
        _directRoots.Clear();
        foreach (var root in roots) Walk(root, null, Rule.Walk, 0);
    }

    private void Walk(PdfObject obj, string? key, Rule rule, int depth)
    {
        if (depth > 32) return;
        switch (obj)
        {
            case PdfIndirectRef r:
                if (key != null || rule == Rule.Walk) QueueReference(r.ObjectNumber, key);
                break;
            case PdfArray array:
                foreach (var item in array) Walk(item, key, rule, depth + 1);
                break;
            case PdfStream stream:
                WalkDictionary(stream.Dictionary, rule, depth);
                break;
            case PdfDictionary dict:
                WalkDictionary(dict, rule, depth);
                break;
        }
    }

    private void WalkDictionary(PdfDictionary dict, Rule rule, int depth)
    {
        foreach (var (k, v) in dict.Entries)
        {
            if (rule == Rule.Contents && k != "Length") continue;
            if (Kind == PdfClosureKind.Page && PageExcluded.Contains(k)) continue;
            if (Kind == PdfClosureKind.Structure && !StructureWalked.Contains(k) && k is not ("Resources" or "Contents" or "Info")) continue;
            Walk(v, k, rule, depth + 1);
        }
    }

    private void QueueReference(int number, string? key)
    {
        if (Kind == PdfClosureKind.Page)
        {
            Add(number, Rule.Walk);
            return;
        }
        Add(number, key switch
        {
            "Contents" => Rule.Contents,
            "Resources" or "Info" => Rule.Leaf,
            _ => Rule.Walk,
        });
    }
}
