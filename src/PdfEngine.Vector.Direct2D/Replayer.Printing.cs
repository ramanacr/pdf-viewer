using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using PdfEngine.Geometry;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Replaying into a command list for Direct2D's print control. A command list has no pixels, so
/// what reads the target's size takes it from the recording instead, and glyph runs carry their
/// Unicode text so the printed document (XPS, or a PDF made from it) keeps text as text.
/// </summary>
internal sealed partial class Replayer
{
    // Device size of the command list being recorded; unused when drawing to a bitmap.
    private SizeI _recordingSize;
    private bool _describeText;

    // The surface Draw created: passes filter only what is drawn on it, not inside groups.
    private Surface? _root;
    // Commands [0, _drawEnd) are drawn when it is set (DrawPrefix).
    private int _drawEnd = -1;
    // A pass (DrawPassToCommandList) draws the page-level units starting in (_passFrom, _passTo]
    // except those in _passSkip; null outside a pass.
    private HashSet<int>? _passSkip;
    private int _passFrom, _passTo;
    // Per command, the first command of the page-level unit holding it: a group's commands
    // belong to its Begin.
    private int[]? _unitStart;

    /// <summary>
    /// Draws into a command list whose device space is <paramref name="size"/> pixels. The page
    /// must not have page-level groups that read their backdrop (<see cref="BackdropGroups"/>).
    /// </summary>
    public void DrawToCommandList(ID2D1DeviceContext ctx, Matrix3x2 pageToDevice, PdfRect visiblePage, SizeI size) =>
        Draw(ctx, pageToDevice, visiblePage, size);

    /// <summary>
    /// Draws into a command list only the page-level units whose first command lies in
    /// (<paramref name="from"/>, <paramref name="to"/>], leaving out those starting at an index in
    /// <paramref name="skip"/>. Clips, transforms and opacity layers are replayed in every pass,
    /// so each pass draws its content exactly as the whole page would.
    /// </summary>
    public void DrawPassToCommandList(ID2D1DeviceContext ctx, Matrix3x2 pageToDevice, PdfRect visiblePage, SizeI size,
        int from, int to, HashSet<int> skip)
    {
        Prepare();
        _passFrom = from;
        _passTo = to;
        _passSkip = skip;
        try
        {
            Draw(ctx, pageToDevice, visiblePage, size);
        }
        finally
        {
            _passSkip = null;
        }
    }

    /// <summary>Draws commands [0, <paramref name="end"/>) on a bitmap target: the page composited up to that point.</summary>
    public void DrawPrefix(ID2D1DeviceContext ctx, Matrix3x2 pageToDevice, PdfRect visiblePage, int end)
    {
        _drawEnd = end;
        try
        {
            Draw(ctx, pageToDevice, visiblePage);
        }
        finally
        {
            _drawEnd = -1;
        }
    }

    private bool InPass(int i)
    {
        int unit = _unitStart![i];
        return unit > _passFrom && unit <= _passTo && !_passSkip!.Contains(unit);
    }

    /// <summary>
    /// The page-level groups that read the composited page under them, which a command list
    /// cannot give: blend modes other than Normal, and non-isolated groups whose content blends
    /// or knocks out. Each comes with the index of its End command and its page-space bounds
    /// (null when unknown). Groups nested in other groups are not listed: they composite on
    /// their parent's offscreen bitmap, which has pixels.
    /// </summary>
    public List<(int Begin, int End, PdfRect? Bounds)> BackdropGroups()
    {
        Prepare();
        var cmds = _list.Commands;
        var groups = new List<(int, int, PdfRect?)>();
        for (int i = 0; i < cmds.Count; i++)
        {
            int end = _groupEnd![i] < 0 ? i : _groupEnd[i];
            switch (cmds[i])
            {
                case BeginCompositingGroup g when !_knockoutOverLimit!.Contains(i)
                                                  && (g.Blend != PdfBlendMode.Normal || !g.Isolated && (g.Knockout || _containsBlend![i])):
                    groups.Add((i, end, g.Bounds));
                    break;
                case BeginTransparencyGroup t when _containsBlend![i] && !t.Isolated:
                    groups.Add((i, end, t.Bounds));
                    break;
            }
            if (cmds[i] is BeginCompositingGroup or BeginTransparencyGroup)
                i = Math.Max(i, end); // page level only
        }
        return groups;
    }

    private void MatchUnits()
    {
        var cmds = _list.Commands;
        _unitStart = new int[cmds.Count];
        for (int i = 0; i < cmds.Count; i++)
        {
            _unitStart[i] = i;
            if (cmds[i] is BeginCompositingGroup or BeginTransparencyGroup && _groupEnd![i] > i)
            {
                int end = Math.Min(_groupEnd[i], cmds.Count - 1);
                for (int k = i + 1; k <= end; k++) _unitStart[k] = i;
                i = end;
            }
        }
    }

    private SizeI TargetSize(ID2D1DeviceContext ctx)
    {
        using var targetImage = ctx.Target; // a new reference: released with it
        using var bitmap = targetImage?.QueryInterfaceOrNull<ID2D1Bitmap1>();
        return bitmap?.PixelSize ?? _recordingSize;
    }

    /// <summary>
    /// The text of glyphs [<paramref name="first"/>, +<paramref name="count"/>) of a run, with a
    /// cluster map from each character to its glyph; null when the run has no text or its resolved
    /// glyphs (<paramref name="resolved"/> in all) do not line up with the run's.
    /// </summary>
    private static TextDescription? Describe(PdfGlyphRun run, int resolved, int first, int count)
    {
        if (resolved != run.Glyphs.Count || count <= 0 || first + count > run.Glyphs.Count)
            return null;
        var text = new StringBuilder();
        var clusters = new List<ushort>();
        for (int i = 0; i < count; i++)
        {
            var unicode = run.Glyphs[first + i].Unicode;
            if (string.IsNullOrEmpty(unicode))
                continue;
            text.Append(unicode);
            // A character's cluster starts at its glyph; the first character starts the run.
            for (int k = 0; k < unicode.Length; k++) clusters.Add(clusters.Count == 0 ? (ushort)0 : (ushort)i);
        }
        return text.Length == 0 ? null : new TextDescription(text.ToString(), clusters.ToArray());
    }

    /// <summary>A glyph run description with its cluster map pinned until disposed.</summary>
    private sealed class TextDescription : IDisposable
    {
        private GCHandle _clusters;
        public GlyphRunDescription Value { get; }

        public TextDescription(string text, ushort[] clusters)
        {
            _clusters = GCHandle.Alloc(clusters, GCHandleType.Pinned);
            Value = new GlyphRunDescription { LocaleName = null!, Text = text, ClusterMap = _clusters.AddrOfPinnedObject(), TextPosition = 0 };
        }

        public void Dispose()
        {
            if (_clusters.IsAllocated) _clusters.Free();
        }
    }
}
