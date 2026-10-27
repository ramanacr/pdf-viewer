using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Services;

namespace PdfEngine.Vector.Annotations;

/// <summary>
/// Comment threads (ISO 32000-2 12.5.6.4): a reply is a markup annotation whose /IRT names the
/// annotation it answers and whose /RT is R (or absent). A thread starts at an annotation that
/// is not a reply. /IRT comes from the file and is untrusted: chains that loop, that are deeper
/// than <see cref="PdfAnnotationLimits.MaxReplyDepth"/> or that end at an annotation not given
/// belong to no thread.
/// </summary>
public static class PdfAnnotationThreads
{
    /// <summary>True for a reply (/IRT with /RT R); a group member (/RT Group) is not one.</summary>
    public static bool IsReply(PdfAnnotation a) => a.InReplyTo > 0 && a.ReplyType is null or "R";

    /// <summary>
    /// For each reply of <paramref name="annotations"/> with an object number: the object number
    /// of the annotation its thread starts at. Replies without a thread are left out. Linear in
    /// the number of annotations times the depth limit.
    /// </summary>
    public static IReadOnlyDictionary<int, int> Roots(IEnumerable<PdfAnnotation> annotations)
    {
        var byNumber = new Dictionary<int, PdfAnnotation>();
        foreach (var a in annotations)
            if (a.ObjectNumber > 0) byNumber.TryAdd(a.ObjectNumber, a);
        // Each reply's root and depth (1: it answers the root itself).
        var found = new Dictionary<int, (int Root, int Depth)>();
        var path = new List<int>();
        var onPath = new HashSet<int>();
        foreach (var start in byNumber.Values)
        {
            if (!IsReply(start) || found.ContainsKey(start.ObjectNumber)) continue;
            path.Clear();
            onPath.Clear();
            var current = start;
            (int Root, int Depth) end = (0, 0);
            while (path.Count <= PdfAnnotationLimits.MaxReplyDepth)
            {
                path.Add(current.ObjectNumber);
                onPath.Add(current.ObjectNumber);
                if (!byNumber.TryGetValue(current.InReplyTo, out var parent)) break; // the parent is missing
                if (found.TryGetValue(parent.ObjectNumber, out var known)) { end = known; break; }
                if (!IsReply(parent)) { end = (parent.ObjectNumber, 0); break; }
                if (onPath.Contains(parent.ObjectNumber)) break; // a loop
                current = parent;
            }
            if (end.Root == 0) continue;
            // path[^1] answers the end: its depth is one more than the end's, and so on back to the start.
            for (int i = path.Count - 1, depth = end.Depth + 1; i >= 0; i--, depth++)
            {
                if (depth > PdfAnnotationLimits.MaxReplyDepth) break;
                found[path[i]] = (end.Root, depth);
            }
        }
        return found.ToDictionary(p => p.Key, p => p.Value.Root);
    }

    /// <summary>The replies of <paramref name="annotations"/> grouped under the object number their thread starts at, in the order given.</summary>
    public static ILookup<int, PdfAnnotation> Threads(IReadOnlyList<PdfAnnotation> annotations)
    {
        var roots = Roots(annotations);
        return annotations.Where(a => roots.ContainsKey(a.ObjectNumber)).ToLookup(a => roots[a.ObjectNumber]);
    }
}
