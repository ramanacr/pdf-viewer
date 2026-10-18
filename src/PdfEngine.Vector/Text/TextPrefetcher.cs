using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Text;

/// <summary>
/// Background text builds for <see cref="PdfTextExtractor.Prefetch"/>: one thread below normal
/// priority, a bounded queue served newest first (the page just drawn is the one being looked at),
/// documents held weakly so that a queued request never keeps one alive.
/// </summary>
internal static class TextPrefetcher
{
    /// <summary>Requests kept; older ones are dropped (the pages have scrolled away).</summary>
    private const int MaxQueued = 16;

    private sealed record Request(WeakReference<PdfVectorDocument> Document, int Page, int Generation);

    private static readonly object Sync = new();
    private static readonly LinkedList<Request> Queue = new();
    private static readonly ConditionalWeakTable<PdfVectorDocument, StrongBox<int>> Generations = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static Thread? _worker;

    public static void Enqueue(PdfVectorDocument document, int page)
    {
        lock (Sync)
        {
            int generation = Generations.GetValue(document, _ => new StrongBox<int>()).Value;
            for (var node = Queue.First; node != null; node = node.Next)
            {
                if (node.Value.Page == page && node.Value.Document.TryGetTarget(out var d) && ReferenceEquals(d, document))
                {
                    Queue.Remove(node);
                    break;
                }
            }
            Queue.AddFirst(new Request(new WeakReference<PdfVectorDocument>(document), page, generation));
            while (Queue.Count > MaxQueued) Queue.RemoveLast();
            if (_worker == null)
            {
                _worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Text prefetch" };
                _worker.Start();
            }
        }
        Signal.Set();
    }

    /// <summary>Drops the document's queued requests; a build in progress stops before it starts its work.</summary>
    public static void Cancel(PdfVectorDocument document)
    {
        lock (Sync)
        {
            if (Generations.TryGetValue(document, out var box)) box.Value++;
            for (var node = Queue.First; node != null;)
            {
                var next = node.Next;
                if (!node.Value.Document.TryGetTarget(out var d) || ReferenceEquals(d, document)) Queue.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>Requests not yet served (for tests).</summary>
    internal static int Pending
    {
        get { lock (Sync) return Queue.Count; }
    }

    private static bool Current(PdfVectorDocument document, int generation)
    {
        lock (Sync) return Generations.TryGetValue(document, out var box) && box.Value == generation;
    }

    private static void Run()
    {
        while (true)
        {
            Request? request;
            lock (Sync)
            {
                request = Queue.First?.Value;
                if (request != null) Queue.RemoveFirst();
            }
            if (request == null)
            {
                Signal.WaitOne();
                continue;
            }
            if (!request.Document.TryGetTarget(out var document) || !document.IsOpen) continue;
            try
            {
                PdfTextExtractor.BuildInBackground(document, request.Page, () => Current(document, request.Generation));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A page that cannot be read now is read (and reported) when it is asked for.
            }
        }
    }
}
