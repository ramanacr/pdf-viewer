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

    /// <summary>
    /// Draws into a command list whose device space is <paramref name="size"/> pixels. The page
    /// must not need its backdrop read back (<see cref="NeedsBackdrop"/>).
    /// </summary>
    public void DrawToCommandList(ID2D1DeviceContext ctx, Matrix3x2 pageToDevice, PdfRect visiblePage, SizeI size) =>
        Draw(ctx, pageToDevice, visiblePage, size);

    /// <summary>True when a group reads the composited backdrop (blend modes, non-isolated knockout), which a command list cannot give.</summary>
    public static bool NeedsBackdrop(IPdfDisplayList list) =>
        list.Commands.Any(c => c is BeginCompositingGroup g && (g.Blend != PdfBlendMode.Normal || g.Knockout && !g.Isolated));

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
